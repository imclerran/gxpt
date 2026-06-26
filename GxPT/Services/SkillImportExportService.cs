using System;
using System.IO;
using System.Text;
using Ionic.Zip;

namespace GxPT
{
    // Core skill import/export (no UI), parallel to ImportExportService: throw on failure so callers
    // handle UX. A .gxsk archive is a zip holding one skill folder ("<slug>/SKILL.md" plus supporting
    // files); flat archives with SKILL.md at the root are also accepted on import, for hand-made zips.
    // XP / .NET 3.5 friendly.
    internal static class SkillImportExportService
    {
        public static void ExportSkill(Skill skill, string archivePath)
        {
            if (skill == null || string.IsNullOrEmpty(skill.Directory) || !Directory.Exists(skill.Directory))
                throw new InvalidOperationException("Skill folder not found.");
            if (string.IsNullOrEmpty(archivePath))
                throw new ArgumentException("Archive path is required.", "archivePath");

            using (var zip = new ZipFile())
            {
                zip.AlternateEncoding = Encoding.UTF8;
                zip.AlternateEncodingUsage = ZipOption.AsNecessary;
                zip.CompressionLevel = Ionic.Zlib.CompressionLevel.BestCompression;
                zip.AddDirectory(skill.Directory, skill.Slug);
                zip.Save(archivePath);
            }
        }

        // Imports the archive's skill into <targetRoot>/<slug>. The zip is staged through a temp folder
        // (ZipSafe-validated) so the skill is located and validated before anything touches the skills
        // root. confirmOverwrite is consulted with the slug when the target folder already exists;
        // returning false cancels. Returns the imported slug, or null when cancelled.
        public static string ImportSkill(string archivePath, string targetRoot, Predicate<string> confirmOverwrite)
        {
            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException("Archive not found.", archivePath);
            if (string.IsNullOrEmpty(targetRoot))
                throw new ArgumentException("Target skills folder is required.", "targetRoot");

            string staging = Path.Combine(Path.GetTempPath(),
                "GxPT-skill-import-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                ZipSafe.SafeExtract(archivePath, staging, true);

                string skillDir = LocateSkillDir(staging);

                // Same validity bar as catalog discovery (SkillCatalog.TryLoad): a SKILL.md whose
                // frontmatter declares a non-empty description.
                string text = File.ReadAllText(Path.Combine(skillDir, "SKILL.md"), Encoding.UTF8);
                SkillFrontmatter fm = SkillFrontmatter.Parse(text);
                if (fm == null || string.IsNullOrEmpty(fm.Description))
                    throw new InvalidDataException(
                        "The archive's SKILL.md declares no description in its frontmatter.");

                // Folder layout: the folder name is the slug (as in catalog discovery). Flat layout has
                // no folder to name the skill, so fall back to the frontmatter name, then the file name.
                string slug;
                if (string.Equals(skillDir, staging, StringComparison.OrdinalIgnoreCase))
                {
                    slug = SkillSlug.Make(fm.Name);
                    if (string.IsNullOrEmpty(slug))
                        slug = SkillSlug.Make(Path.GetFileNameWithoutExtension(archivePath));
                }
                else
                {
                    slug = SkillSlug.Make(Path.GetFileName(skillDir));
                }
                if (string.IsNullOrEmpty(slug))
                    throw new InvalidDataException("Could not derive a skill slug from the archive.");

                string target = Path.Combine(targetRoot, slug);
                if (Directory.Exists(target))
                {
                    if (confirmOverwrite == null || !confirmOverwrite(slug)) return null;
                    Directory.Delete(target, true);
                }
                Directory.CreateDirectory(targetRoot);
                FileSafe.CopyDirectory(skillDir, target);
                return slug;
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch { }
            }
        }

        // True when the zip looks like a skill archive (a SKILL.md at the root or one folder deep).
        // Used to route a generic .zip between the skill and conversation importers; .gxsk/.gxcv route
        // by extension and never get here.
        public static bool ArchiveContainsSkill(string archivePath)
        {
            return ZipSafe.ContainsEntry(archivePath, delegate(string name)
            {
                if (string.Equals(name, "SKILL.md", StringComparison.OrdinalIgnoreCase))
                    return true;
                int slash = name.IndexOf('/');
                return slash >= 0 && name.IndexOf('/', slash + 1) < 0 &&
                    string.Equals(name.Substring(slash + 1), "SKILL.md", StringComparison.OrdinalIgnoreCase);
            });
        }

        // The extracted skill folder: the staging root itself (flat layout), else the single top-level
        // folder holding a SKILL.md. Zero or several candidate folders is an error - one archive, one
        // skill - so a stray multi-skill zip fails loudly instead of half-importing.
        private static string LocateSkillDir(string staging)
        {
            if (File.Exists(Path.Combine(staging, "SKILL.md"))) return staging;

            string[] dirs = Directory.GetDirectories(staging);
            string found = null;
            for (int i = 0; i < dirs.Length; i++)
            {
                if (!File.Exists(Path.Combine(dirs[i], "SKILL.md"))) continue;
                if (found != null)
                    throw new InvalidDataException("The archive contains more than one skill.");
                found = dirs[i];
            }
            if (found == null)
                throw new InvalidDataException("The archive does not contain a skill (no SKILL.md found).");
            return found;
        }
    }
}

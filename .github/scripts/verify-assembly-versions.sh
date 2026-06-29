#!/usr/bin/env bash
#
# Verifies assembly / installer version bumps for the GxPT solution.
#
# Two independent checks are performed against a base git ref:
#
#   1. For every assembly EXCEPT the main GxPT application and the test
#      projects: if any file under that assembly's project directory was
#      modified, its [assembly: AssemblyVersion(...)] must have been bumped.
#
#   2. The GxPT application version lives in three places that are only ever
#      changed together when prepping a release:
#         - GxPT/Properties/AssemblyInfo.cs  (AssemblyVersion + AssemblyFileVersion)
#         - GxPT/GxPT.csproj                 (ApplicationVersion)
#         - GxPT.Setup/GxPT.Setup.vdproj     (ProductVersion)
#      If any one of them changes, all of them must change, AND the installer
#      ProductCode (a GUID) in the .vdproj must have been regenerated.
#
# Usage: verify-assembly-versions.sh <base-ref>
#   <base-ref> is the git ref/sha to compare the current tree against
#   (e.g. origin/main, or the "before" sha of a push).
#
set -euo pipefail

BASE="${1:-${BASE_SHA:-}}"
if [[ -z "$BASE" || "$BASE" =~ ^0+$ ]]; then
  echo "No base ref to compare against; skipping assembly version verification."
  exit 0
fi

# Compare against the merge-base so we only look at changes introduced on top
# of the base branch (this mirrors what GitHub shows as the PR diff).
if ! MB="$(git merge-base "$BASE" HEAD 2>/dev/null)"; then
  echo "Could not determine merge-base with '$BASE'; comparing against it directly."
  MB="$BASE"
fi

CHANGED="$(git diff --name-only "$MB" HEAD)"

errors=()

# Extract the first match of a Perl regex (with a \K look-behind) from a file in
# the current working tree. Prints empty string if the file or pattern is absent.
match_head() {
  local path="$1" re="$2" out
  [[ -f "$path" ]] || { printf ''; return; }
  out="$(grep -oP "$re" "$path" 2>/dev/null | head -1)" || true
  printf '%s' "$out"
}

# Same, but reads the file as it existed at the given git ref.
match_ref() {
  local ref="$1" path="$2" re="$3" out
  out="$(git show "$ref:$path" 2>/dev/null | grep -oP "$re" | head -1)" || true
  printf '%s' "$out"
}

# True if any changed path is inside the given directory.
dir_changed() {
  local dir="$1"
  printf '%s\n' "$CHANGED" | grep -q "^${dir}/"
}

# Regexes. AssemblyVersion lines are anchored to the start of the line so the
# commented-out "// [assembly: AssemblyVersion(\"1.0.*\")]" example is ignored.
RE_ASMVER='^\[assembly: AssemblyVersion\("\K[^"]+'
RE_ASMFILE='^\[assembly: AssemblyFileVersion\("\K[^"]+'
RE_APPVER='<ApplicationVersion>\K[^<]+'
RE_PRODVER='"ProductVersion" = "8:\K[^"]+'
RE_PRODCODE='"ProductCode" = "8:\K\{[^"]+'   # only the GUID ProductCode, not prerequisites

# ---------------------------------------------------------------------------
# Check 1: non-main, non-test assemblies must bump AssemblyVersion when touched
# ---------------------------------------------------------------------------
echo "== Check 1: AssemblyVersion bumps for modified assemblies =="

mapfile -t ASM_INFOS < <(
  git ls-files \
    | grep -E 'Properties/AssemblyInfo\.cs$' \
    | grep -v '^GxPT/Properties/AssemblyInfo.cs$' \
    | grep -viE '\.tests/|/tests/'
)

for info in "${ASM_INFOS[@]}"; do
  # project dir = parent of the Properties/ folder
  proj_dir="$(dirname "$(dirname "$info")")"

  if ! dir_changed "$proj_dir"; then
    echo "  - $proj_dir: unchanged, skipping"
    continue
  fi

  old_ver="$(match_ref "$MB" "$info" "$RE_ASMVER")"
  if [[ -z "$old_ver" ]]; then
    echo "  - $proj_dir: new assembly (no base version), skipping"
    continue
  fi

  new_ver="$(match_head "$info" "$RE_ASMVER")"
  if [[ "$old_ver" == "$new_ver" ]]; then
    errors+=("Assembly '$proj_dir' was modified but its AssemblyVersion was not updated (still $new_ver). Bump AssemblyVersion in $info.")
    echo "  - $proj_dir: MODIFIED but version unchanged ($new_ver)  <-- FAIL"
  else
    echo "  - $proj_dir: modified, version $old_ver -> $new_ver  OK"
  fi
done

# ---------------------------------------------------------------------------
# Check 2: GxPT release version triple + installer ProductCode
# ---------------------------------------------------------------------------
echo "== Check 2: GxPT release version consistency =="

GXPT_ASM="GxPT/Properties/AssemblyInfo.cs"
GXPT_CSPROJ="GxPT/GxPT.csproj"
GXPT_VDPROJ="GxPT.Setup/GxPT.Setup.vdproj"

old_asmver="$(match_ref "$MB" "$GXPT_ASM" "$RE_ASMVER")"
new_asmver="$(match_head "$GXPT_ASM" "$RE_ASMVER")"
old_asmfile="$(match_ref "$MB" "$GXPT_ASM" "$RE_ASMFILE")"
new_asmfile="$(match_head "$GXPT_ASM" "$RE_ASMFILE")"
old_appver="$(match_ref "$MB" "$GXPT_CSPROJ" "$RE_APPVER")"
new_appver="$(match_head "$GXPT_CSPROJ" "$RE_APPVER")"
old_prodver="$(match_ref "$MB" "$GXPT_VDPROJ" "$RE_PRODVER")"
new_prodver="$(match_head "$GXPT_VDPROJ" "$RE_PRODVER")"
old_prodcode="$(match_ref "$MB" "$GXPT_VDPROJ" "$RE_PRODCODE")"
new_prodcode="$(match_head "$GXPT_VDPROJ" "$RE_PRODCODE")"

changed_asmver=$([[ "$old_asmver"  != "$new_asmver"  ]] && echo 1 || echo 0)
changed_asmfile=$([[ "$old_asmfile" != "$new_asmfile" ]] && echo 1 || echo 0)
changed_appver=$([[ "$old_appver"  != "$new_appver"  ]] && echo 1 || echo 0)
changed_prodver=$([[ "$old_prodver" != "$new_prodver" ]] && echo 1 || echo 0)
changed_prodcode=$([[ "$old_prodcode" != "$new_prodcode" ]] && echo 1 || echo 0)

if (( changed_asmver || changed_asmfile || changed_appver || changed_prodver )); then
  echo "  GxPT version change detected:"
  echo "    AssemblyVersion     $old_asmver -> $new_asmver"
  echo "    AssemblyFileVersion $old_asmfile -> $new_asmfile"
  echo "    ApplicationVersion  $old_appver -> $new_appver"
  echo "    ProductVersion      $old_prodver -> $new_prodver"
  echo "    ProductCode         $old_prodcode -> $new_prodcode"

  (( changed_asmver ))  || { errors+=("GxPT version is being changed, but AssemblyVersion in $GXPT_ASM was not updated."); }
  (( changed_asmfile )) || { errors+=("GxPT version is being changed, but AssemblyFileVersion in $GXPT_ASM was not updated."); }
  (( changed_appver ))  || { errors+=("GxPT version is being changed, but ApplicationVersion in $GXPT_CSPROJ was not updated."); }
  (( changed_prodver )) || { errors+=("GxPT version is being changed, but ProductVersion in $GXPT_VDPROJ was not updated."); }
  (( changed_prodcode )) || { errors+=("GxPT version is being changed, but the installer ProductCode in $GXPT_VDPROJ was not regenerated."); }
else
  echo "  No GxPT release version change detected; nothing to enforce."
fi

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------
if (( ${#errors[@]} > 0 )); then
  echo ""
  echo "Assembly version verification FAILED:"
  for e in "${errors[@]}"; do
    echo "  ✗ $e"
  done
  exit 1
fi

echo ""
echo "Assembly version verification passed."

using System;
using System.IO;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    // Persistence of the conversation's current directory (host `cd`) - the 2026-07-31 amendment to
    // the host-cd/worktree design: stored anchor-relative so the file cannot express an escape,
    // restored with validation, so a reopened conversation resumes where its transcript's cd echoes
    // say it is. Real temp directories because RevalidateCurrentDir checks existence on disk.
    public sealed class CurrentDirPersistenceTests : IDisposable
    {
        private readonly string _anchor;
        private readonly string _sub;      // <anchor>/src
        private readonly string _deep;     // <anchor>/src/app

        public CurrentDirPersistenceTests()
        {
            _anchor = Path.Combine(Path.GetTempPath(), "curdir_" + Guid.NewGuid().ToString("N"));
            _sub = Path.Combine(_anchor, "src");
            _deep = Path.Combine(_sub, "app");
            Directory.CreateDirectory(_deep);
        }

        public void Dispose()
        {
            try { Directory.Delete(_anchor, true); } catch { }
        }

        private Conversation WorkspaceConvo()
        {
            var convo = new Conversation(null);
            convo.Name = "W";
            convo.WorkingDir = _anchor;
            convo.History.Add(new ChatMessage("user", "hi"));
            return convo;
        }

        private static string QuotedJsonPath(string path)
        {
            return "\"" + path.Replace("\\", "\\\\") + "\"";
        }

        [Fact]
        public void CurrentDir_round_trips_via_anchor_relative_form()
        {
            var convo = WorkspaceConvo();
            convo.CurrentDir = _deep;

            string json = ConversationStore.ToJson(convo);
            // Stored relative to the anchor with forward slashes, never absolute.
            Assert.Contains("\"CurrentDir\":\"src/app\"", json);

            var reload = ConversationStore.LoadFromJson(null, json);
            Assert.Equal(Path.GetFullPath(_deep), reload.CurrentDir);
        }

        [Fact]
        public void CurrentDir_at_anchor_is_omitted()
        {
            var convo = WorkspaceConvo(); // CurrentDir null = at the anchor
            Assert.DoesNotContain("CurrentDir", ConversationStore.ToJson(convo));
        }

        [Fact]
        public void CurrentDir_without_workspace_is_omitted()
        {
            var convo = new Conversation(null);
            convo.Name = "N";
            convo.CurrentDir = _sub; // no WorkingDir: nothing to be relative to
            convo.History.Add(new ChatMessage("user", "hi"));
            Assert.DoesNotContain("CurrentDir", ConversationStore.ToJson(convo));
        }

        [Fact]
        public void CurrentDir_outside_anchor_is_never_persisted()
        {
            // An out-of-anchor runtime value is an upstream bug: serialize drops it rather than
            // writing an escape into the file format.
            var convo = WorkspaceConvo();
            convo.CurrentDir = Path.GetTempPath();
            Assert.DoesNotContain("CurrentDir", ConversationStore.ToJson(convo));
        }

        [Fact]
        public void Load_legacy_file_without_current_dir_is_null()
        {
            var convo = ConversationStore.LoadFromJson(null, "{\"Name\":\"C\",\"Messages\":[]}");
            Assert.Null(convo.CurrentDir);
        }

        [Fact]
        public void Load_escaping_stored_value_falls_back_to_null()
        {
            // Hand-edited/corrupt file: a relative escape must never survive the load.
            string json = "{\"Name\":\"C\",\"WorkingDir\":" + QuotedJsonPath(_anchor)
                + ",\"CurrentDir\":\"../evil\",\"Messages\":[]}";
            Assert.Null(ConversationStore.LoadFromJson(null, json).CurrentDir);
        }

        [Fact]
        public void Load_absolute_stored_value_falls_back_to_null()
        {
            // The persisted form is anchor-relative by contract; an absolute path (even one inside
            // the anchor) is rejected outright, mirroring PathSandbox.
            string json = "{\"Name\":\"C\",\"WorkingDir\":" + QuotedJsonPath(_anchor)
                + ",\"CurrentDir\":" + QuotedJsonPath(_sub) + ",\"Messages\":[]}";
            Assert.Null(ConversationStore.LoadFromJson(null, json).CurrentDir);
        }

        [Fact]
        public void Load_dot_stored_value_normalizes_to_null()
        {
            // "." resolves to the anchor itself, whose canonical representation is null.
            string json = "{\"Name\":\"C\",\"WorkingDir\":" + QuotedJsonPath(_anchor)
                + ",\"CurrentDir\":\".\",\"Messages\":[]}";
            Assert.Null(ConversationStore.LoadFromJson(null, json).CurrentDir);
        }

        [Fact]
        public void Revalidate_accepts_live_subdir()
        {
            Assert.Equal(Path.GetFullPath(_sub), ConversationStore.RevalidateCurrentDir(_anchor, _sub));
        }

        [Fact]
        public void Revalidate_normalizes_anchor_itself_to_null()
        {
            // "At the anchor" is represented as null, never as a path.
            Assert.Null(ConversationStore.RevalidateCurrentDir(_anchor, _anchor));
        }

        [Fact]
        public void Revalidate_rejects_missing_directory()
        {
            // Deleted while the app was closed (e.g. a pruned worktree): fall back to the anchor.
            Assert.Null(ConversationStore.RevalidateCurrentDir(_anchor, Path.Combine(_anchor, "gone")));
        }

        [Fact]
        public void Revalidate_rejects_out_of_anchor_directory()
        {
            // Exists, but outside the anchor: rejected regardless.
            Assert.Null(ConversationStore.RevalidateCurrentDir(_anchor, Path.GetTempPath()));
        }

        [Fact]
        public void Revalidate_without_workspace_is_null()
        {
            Assert.Null(ConversationStore.RevalidateCurrentDir(null, _sub));
        }
    }
}

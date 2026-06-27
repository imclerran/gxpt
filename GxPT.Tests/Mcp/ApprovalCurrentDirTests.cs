using System.IO;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    // Design A1: remembered PATH approvals key on the canonicalized ABSOLUTE location (resolved against
    // the call's current directory), not the relative argument — so a "this folder and below" rule means
    // a fixed place on disk and does not silently change scope as the model `cd`s.
    public class ApprovalCurrentDirTests
    {
        private sealed class ScriptedPrompt : IToolApprovalPrompt
        {
            public ApprovalChoice Next = ApprovalChoice.Deny;
            public int Calls;
            public ApprovalChoice Ask(ApprovalRequest req) { Calls++; return Next; }
        }

        private static JObject Args(string json) { return JObject.Parse(json); }

        [Fact]
        public void Path_rule_is_keyed_on_absolute_location_not_relative_arg()
        {
            string anchor = Path.Combine(Path.GetTempPath(), "appr");
            string sub = Path.Combine(anchor, "sub");

            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberPrefixArg };
            var store = new InMemoryApprovalStore();
            var pol = new ToolApprovalPolicy(new ToolClassifier(), prompt, store);
            pol.WorkingDir = anchor;

            // First write while scoped into sub: prompts once and remembers "sub and below" (absolute).
            pol.CurrentDir = sub;
            Assert.Equal(ApprovalDecision.Allow, pol.Check("files__write", Args("{\"path\":\"a.txt\"}")));
            Assert.Equal(1, prompt.Calls);

            // A different file in the same current dir is covered by the remembered rule — no new prompt.
            pol.CurrentDir = sub;
            Assert.Equal(ApprovalDecision.Allow, pol.Check("files__write", Args("{\"path\":\"b.txt\"}")));
            Assert.Equal(1, prompt.Calls);

            // The SAME relative path but from a DIFFERENT current dir (the anchor) resolves to a different
            // absolute location, so the rule does NOT cover it and the gate prompts again.
            pol.CurrentDir = anchor;
            pol.Check("files__write", Args("{\"path\":\"a.txt\"}"));
            Assert.Equal(2, prompt.Calls);
        }
    }
}

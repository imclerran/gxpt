using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GxPT;
using Mcp35.Client;
using Xunit;

namespace GxPT.Tests.Mcp
{
    // McpHost.SetBuiltInServerEnabled: the narrow runtime start/stop of one built-in server (the
    // skills-enablement refresh) that replaced the full host rebuild — the rebuild swapped the
    // registry object out from under in-flight turns (the "cd-only sub-agent" doom loop).
    public class McpHostServerToggleTests
    {
        private static List<string> Manifest(McpToolRegistry reg)
        {
            var names = new List<string>();
            foreach (var line in reg.NamesManifestSystemMessage().Split('\n'))
                if (line.StartsWith("- ")) names.Add(line.Substring(2));
            return names;
        }

        private static McpHost NewHost(out FakeServerConnector connector, out McpToolRegistry reg)
        {
            connector = new FakeServerConnector();
            reg = new McpToolRegistry(null);
            return new McpHost(connector, reg, null, 2000);
        }

        private static McpServerSpec SkillsSpec(bool enabled)
        {
            var s = Specs.Scoped("skills", enabled);
            s.RunsWithoutWorkdir = true; // like the real extensions server
            return s;
        }

        [Fact]
        public void Disable_tears_down_only_that_servers_instances()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true), SkillsSpec(true) });
            host.EnsureWorkingDir("C:\\a");
            // Live: skills eager (null workdir), files@a, skills@a.
            Assert.Equal(3, c.Created.Count);

            Assert.True(host.SetBuiltInServerEnabled("skills", false));

            foreach (var conn in c.Created)
            {
                if (conn.Name == "skills") Assert.Equal(ConnectionState.Closed, conn.State);
                else Assert.Equal(ConnectionState.Ready, conn.State); // files untouched
            }
            var m = Manifest(reg);
            Assert.DoesNotContain("skills__skills_tool", m);
            Assert.Contains("files__files_tool", m);
            Assert.Contains("C:\\a", host.ActiveWorkingDirs); // the workdir set itself survives
        }

        [Fact]
        public void Enable_connects_eager_and_one_instance_per_live_workdir()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true), SkillsSpec(false) });
            host.EnsureWorkingDir("C:\\a");
            host.EnsureWorkingDir("C:\\b");
            Assert.DoesNotContain("skills", c.CreatedNames); // disabled at Start

            Assert.True(host.SetBuiltInServerEnabled("skills", true));

            var skillsWorkdirs = new List<string>();
            for (int i = 0; i < c.CreatedNames.Count; i++)
                if (c.CreatedNames[i] == "skills") skillsWorkdirs.Add(c.Workdirs[i]);
            Assert.Contains(null, skillsWorkdirs);     // the eager, workdir-less instance
            Assert.Contains("C:\\a", skillsWorkdirs);
            Assert.Contains("C:\\b", skillsWorkdirs);
            Assert.Equal(3, skillsWorkdirs.Count);

            McpServerConnection r; string tool;
            Assert.True(reg.TryResolve("skills__skills_tool", "C:\\a", out r, out tool));
            Assert.True(reg.TryResolve("skills__skills_tool", "C:\\b", out r, out tool));
        }

        [Fact]
        public void Enable_skips_scratch_dirs_the_spec_is_not_eligible_for()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            var command = Specs.Scoped("command", true);
            command.RunsInScratch = true;
            host.Start(new[] { command, SkillsSpec(false) });
            host.EnsureScratchDir("C:\\scratch\\abc");

            Assert.True(host.SetBuiltInServerEnabled("skills", true));

            var skillsWorkdirs = new List<string>();
            for (int i = 0; i < c.CreatedNames.Count; i++)
                if (c.CreatedNames[i] == "skills") skillsWorkdirs.Add(c.Workdirs[i]);
            Assert.Equal(new string[] { null }, skillsWorkdirs.ToArray()); // eager only, never the scratch dir
        }

        [Fact]
        public void Toggle_is_idempotent_and_spawns_nothing_new()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { SkillsSpec(true) });
            int before = c.Created.Count;

            Assert.True(host.SetBuiltInServerEnabled("skills", true)); // already enabled

            Assert.Equal(before, c.Created.Count);
        }

        [Fact]
        public void Toggle_reports_failure_when_it_cannot_commit()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);

            // Before Start: nothing to flip — the caller must roll back its optimistic state.
            Assert.False(host.SetBuiltInServerEnabled("skills", true));

            host.Start(new[] { SkillsSpec(true) });
            Assert.False(host.SetBuiltInServerEnabled("ghost", true)); // unknown spec

            host.Dispose();
            Assert.False(host.SetBuiltInServerEnabled("skills", false)); // disposed
        }

        [Fact]
        public void Enable_waits_for_a_mid_connect_workdir_and_tops_it_up()
        {
            // The _connecting race: a workdir's scoped set is mid-handshake (its ConnectScoped spec
            // snapshot saw skills DISABLED) when the enable lands. The toggle must wait for that
            // publish and then top the set up, not silently miss the folder until the next rebuild.
            var connector = new GatedServerConnector();
            var reg = new McpToolRegistry(null);
            var host = new McpHost(connector, reg, null, 5000);
            var skills = Specs.Scoped("skills", false); // plain scoped: no eager instance to gate
            host.Start(new[] { Specs.Scoped("files", true), skills });

            var ensureThread = new Thread(delegate() { host.EnsureWorkingDir("C:\\a"); });
            ensureThread.IsBackground = true;
            ensureThread.Start();
            Assert.True(connector.Opening.WaitOne(5000), "connect never reached Open()");

            bool ok = false;
            var toggleThread = new Thread(delegate() { ok = host.SetBuiltInServerEnabled("skills", true); });
            toggleThread.IsBackground = true;
            toggleThread.Start();

            connector.OpenGate.Set(); // release the parked handshake (stays signaled for later opens)
            Assert.True(ensureThread.Join(5000), "ensure thread did not finish");
            Assert.True(toggleThread.Join(5000), "toggle thread did not finish");

            Assert.True(ok);
            McpServerConnection r; string tool;
            Assert.True(reg.TryResolve("skills__skills_tool", "C:\\a", out r, out tool),
                "skills server missing for the workdir that was mid-connect during the enable");
        }

        [Fact]
        public void Disable_mid_connect_discards_the_instance_at_publish()
        {
            // Mirror race: skills is ENABLED and mid-handshake for a new workdir when the disable
            // lands. The disable pass can't see the unpublished connection, so ConnectScoped's own
            // publish step must discard it — otherwise a live, invisible skills server would expose
            // its tools to a conversation the flip meant to hide them from.
            var connector = new GatedServerConnector();
            var reg = new McpToolRegistry(null);
            var host = new McpHost(connector, reg, null, 5000);
            host.Start(new[] { Specs.Scoped("skills", true) });

            var ensureThread = new Thread(delegate() { host.EnsureWorkingDir("C:\\a"); });
            ensureThread.IsBackground = true;
            ensureThread.Start();
            Assert.True(connector.Opening.WaitOne(5000), "connect never reached Open()");

            // Disable while the handshake is parked; nothing is published yet, so this returns fast.
            Assert.True(host.SetBuiltInServerEnabled("skills", false));

            connector.OpenGate.Set();
            Assert.True(ensureThread.Join(5000), "ensure thread did not finish");

            Assert.Empty(Manifest(reg)); // never published into the registry
            Assert.True(connector.Created.All(conn => conn.State == ConnectionState.Closed),
                "the mid-connect instance of the disabled spec was not torn down");
        }
    }
}

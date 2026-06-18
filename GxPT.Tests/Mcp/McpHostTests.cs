using System.Collections.Generic;
using System.Linq;
using GxPT;
using Mcp35.Client;
using Xunit;

namespace GxPT.Tests.Mcp
{
    public class McpHostTests
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

        [Fact]
        public void Start_opens_workdir_independent_servers_and_defers_scoped()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);

            host.Start(new[] { Specs.Eager("web", true), Specs.Eager("github", true), Specs.Scoped("files", true) });

            Assert.Equal(new[] { "web", "github" }, c.CreatedNames.ToArray()); // files deferred
            var m = Manifest(reg);
            Assert.Contains("web__web_tool", m);
            Assert.Contains("github__github_tool", m);
            Assert.DoesNotContain("files__files_tool", m);
        }

        [Fact]
        public void Start_skips_disabled_servers()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);

            host.Start(new[] { Specs.Eager("web", false), Specs.Eager("github", true) });

            Assert.Equal(new[] { "github" }, c.CreatedNames.ToArray());
            Assert.DoesNotContain("web__web_tool", Manifest(reg));
        }

        [Fact]
        public void EnsureWorkingDir_opens_scoped_servers_with_the_workdir()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true), Specs.Scoped("git", true) });
            Assert.Empty(c.CreatedNames); // nothing opened until a workdir is ensured

            host.EnsureWorkingDir("C:\\proj");

            Assert.Equal(new[] { "files", "git" }, c.CreatedNames.ToArray());
            Assert.True(c.Workdirs.All(w => w == "C:\\proj"));
            var m = Manifest(reg);
            Assert.Contains("files__files_tool", m);
            Assert.Contains("git__git_tool", m);
            Assert.Contains("C:\\proj", host.ActiveWorkingDirs);
        }

        [Fact]
        public void Start_opens_workdirless_instance_for_RunsWithoutWorkdir_scoped_server()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);

            var skills = Specs.Scoped("skills", true);
            skills.RunsWithoutWorkdir = true;
            host.Start(new[] { skills, Specs.Scoped("files", true) });

            // skills got an eager, workdir-less instance at Start; plain scoped files did not.
            Assert.Equal(new[] { "skills" }, c.CreatedNames.ToArray());
            Assert.Null(c.Workdirs[c.CreatedNames.IndexOf("skills")]); // created with no workdir
            Assert.Contains("skills__skills_tool", Manifest(reg)); // usable without ensuring a workdir
        }

        [Fact]
        public void RunsWithoutWorkdir_server_also_opens_a_per_workdir_instance()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            var skills = Specs.Scoped("skills", true);
            skills.RunsWithoutWorkdir = true;

            host.Start(new[] { skills });          // eager (null workdir) instance
            host.EnsureWorkingDir("C:\\proj");     // per-workdir instance

            Assert.Equal(2, c.CreatedNames.FindAll(delegate(string n) { return n == "skills"; }).Count);
            Assert.Contains(null, c.Workdirs);
            Assert.Contains("C:\\proj", c.Workdirs);
        }

        [Fact]
        public void EnsureScratchDir_opens_only_scratch_eligible_scoped_specs()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            var command = Specs.Scoped("command", true);
            command.RunsInScratch = true;
            host.Start(new[] { Specs.Scoped("files", true), command, Specs.Scoped("git", true) });

            host.EnsureScratchDir("C:\\scratch\\abc");

            // Only the command server (RunsInScratch) launched in the scratch dir; files/git stayed off.
            Assert.Equal(new[] { "command" }, c.CreatedNames.ToArray());
            Assert.True(c.Workdirs.All(w => w == "C:\\scratch\\abc"));
            var m = Manifest(reg);
            Assert.Contains("command__command_tool", m);
            Assert.DoesNotContain("files__files_tool", m);
            Assert.DoesNotContain("git__git_tool", m);
            Assert.Contains("C:\\scratch\\abc", host.ActiveWorkingDirs);

            // The command tool routes to the scratch-bound connection.
            McpServerConnection r; string tool;
            Assert.True(reg.TryResolve("command__command_tool", "C:\\scratch\\abc", out r, out tool));
            Assert.Same(c.Created.Last(), r);
        }

        [Fact]
        public void EnsureScratchDir_set_is_torn_down_by_RetainOnly_when_unreferenced()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            var command = Specs.Scoped("command", true);
            command.RunsInScratch = true;
            host.Start(new[] { command });
            host.EnsureScratchDir("C:\\scratch\\abc");
            var conn = c.Created.Last();

            host.RetainOnly(new string[0]); // no open tab references it anymore

            Assert.Equal(ConnectionState.Closed, conn.State);
            Assert.DoesNotContain("C:\\scratch\\abc", host.ActiveWorkingDirs);
        }

        [Fact]
        public void EnsureWorkingDir_is_idempotent_for_the_same_folder()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true) });

            host.EnsureWorkingDir("C:\\a");
            host.EnsureWorkingDir("C:\\a"); // second call must NOT spawn another set

            Assert.Equal(new[] { "files" }, c.CreatedNames.ToArray());
        }

        [Fact]
        public void EnsureWorkingDir_before_Start_still_launches_scoped_servers()
        {
            // Reproduces the startup race: the working folder is applied before Start() has captured
            // the scoped specs. Start() must honor the already-requested workdir and launch them.
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);

            host.EnsureWorkingDir("C:\\proj");           // arrives first; no specs yet
            Assert.Empty(c.CreatedNames);

            host.Start(new[] { Specs.Scoped("command", true), Specs.Eager("web", true) });

            Assert.Contains("command", c.CreatedNames);  // scoped server launched by Start
            Assert.Contains("command__command_tool", Manifest(reg));
            Assert.Contains("C:\\proj", host.ActiveWorkingDirs);
        }

        [Fact]
        public void Different_workdirs_get_independent_scoped_sets_and_route_per_folder()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true) });

            host.EnsureWorkingDir("C:\\a");
            var connA = c.Created.Last();
            host.EnsureWorkingDir("C:\\b");
            var connB = c.Created.Last();

            // Both folders served by their own process; neither torn down by the other.
            Assert.Equal(new[] { "files", "files" }, c.CreatedNames.ToArray());
            Assert.Equal(new[] { "C:\\a", "C:\\b" }, c.Workdirs.ToArray());
            Assert.NotSame(connA, connB);
            Assert.Equal(ConnectionState.Ready, connA.State); // NOT closed by ensuring "C:\b"
            Assert.Equal(ConnectionState.Ready, connB.State);

            // The same tool name routes to the connection bound to the calling folder.
            McpServerConnection r; string tool;
            Assert.True(reg.TryResolve("files__files_tool", "C:\\a", out r, out tool));
            Assert.Same(connA, r);
            Assert.True(reg.TryResolve("files__files_tool", "C:\\b", out r, out tool));
            Assert.Same(connB, r);
        }

        [Fact]
        public void ReleaseWorkingDir_tears_down_only_that_folder()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true) });
            host.EnsureWorkingDir("C:\\a");
            var connA = c.Created.Last();
            host.EnsureWorkingDir("C:\\b");
            var connB = c.Created.Last();

            host.ReleaseWorkingDir("C:\\a");

            Assert.Equal(ConnectionState.Closed, connA.State);
            Assert.Equal(ConnectionState.Ready, connB.State);
            Assert.DoesNotContain("C:\\a", host.ActiveWorkingDirs);
            Assert.Contains("C:\\b", host.ActiveWorkingDirs);
            Assert.Contains("files__files_tool", Manifest(reg)); // still provided by "C:\b"
        }

        [Fact]
        public void RetainOnly_tears_down_folders_no_longer_in_use()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true) });
            host.EnsureWorkingDir("C:\\a");
            var connA = c.Created.Last();
            host.EnsureWorkingDir("C:\\b");
            var connB = c.Created.Last();

            host.RetainOnly(new[] { "C:\\b" }); // only the folder with an open tab survives

            Assert.Equal(ConnectionState.Closed, connA.State);
            Assert.Equal(ConnectionState.Ready, connB.State);
            Assert.Equal(new[] { "C:\\b" }, host.ActiveWorkingDirs);
        }

        [Fact]
        public void Disabled_scoped_spec_is_not_opened()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", false) });

            host.EnsureWorkingDir("C:\\a");

            Assert.Empty(c.CreatedNames);
            Assert.Empty(Manifest(reg));
        }

        [Fact]
        public void A_connection_closing_removes_its_tools_from_the_registry()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Eager("web", true) });
            Assert.Contains("web__web_tool", Manifest(reg));

            c.Created[0].Dispose(); // simulates a fault/close → StateChanged(Closed)

            Assert.DoesNotContain("web__web_tool", Manifest(reg));
        }

        [Fact]
        public void Dispose_closes_all_connections()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Eager("web", true), Specs.Scoped("files", true) });
            host.EnsureWorkingDir("C:\\a");
            Assert.Equal(2, Manifest(reg).Count);

            host.Dispose();

            Assert.Empty(Manifest(reg));
            Assert.True(c.Created.All(conn => conn.State == ConnectionState.Closed));
        }

        [Fact]
        public void Dispose_does_not_block_while_a_server_is_still_connecting()
        {
            // Regression: closing the app while servers were still connecting used to hang for the
            // whole handshake because Start held the host lock across the blocking conn.Open().
            var connector = new GatedServerConnector();
            var reg = new McpToolRegistry(null);
            var host = new McpHost(connector, reg, null, 5000);

            // Connect on a background thread; its eager Open() parks in the gated handshake.
            var startThread = new System.Threading.Thread(delegate () { host.Start(new[] { Specs.Eager("web", true) }); });
            startThread.IsBackground = true;
            startThread.Start();
            Assert.True(connector.Opening.WaitOne(5000), "connect never reached Open()");

            // Dispose must return promptly even though the connect is still blocked.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            host.Dispose();
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 1000, "Dispose blocked on in-flight connect: " + sw.ElapsedMilliseconds + "ms");

            // Release the handshake; the connect finishes, sees the host disposed, and discards its
            // server rather than publishing it — nothing leaks into the registry.
            connector.OpenGate.Set();
            Assert.True(startThread.Join(5000), "connect thread did not finish");
            Assert.Empty(Manifest(reg));
            Assert.True(connector.Created.All(conn => conn.State == ConnectionState.Closed));
        }
    }

    // Compile-coverage + smoke for the live connector against the real Mcp35.Client transports.
    public class DefaultServerConnectorTests
    {
        [Fact]
        public void Creates_http_connection_in_created_state_without_opening()
        {
            var ci = new Mcp35.Core.Protocol.Implementation { Name = "GxPT", Version = "test" };
            var connector = new DefaultServerConnector(ci, "curl", null, null);

            var spec = new McpServerSpec
            {
                Name = "github",
                Kind = McpTransportKind.Http,
                Url = "https://api.githubcopilot.com/mcp/",
                Enabled = true
            };
            spec.Headers["Authorization"] = "Bearer ghp_x";

            var conn = connector.Create(spec, null);
            Assert.NotNull(conn);
            Assert.Equal("github", conn.Name);
            Assert.Equal(ConnectionState.Created, conn.State); // not opened → no network
            conn.Dispose();
        }
    }
}

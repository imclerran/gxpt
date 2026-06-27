// These tests configure the git server through the PROCESS-GLOBAL GXPT_WORKDIR (and GXPT_GIT_PATH)
// environment variables (see Harness.NewGitServer). xUnit runs separate test classes as parallel
// collections by default, so two classes setting GXPT_WORKDIR concurrently would race — a server
// could read another class's workdir and reject this class's host-injected current dir as "escaping
// the workspace root". Serialize the whole assembly so the shared env var is stable per test.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

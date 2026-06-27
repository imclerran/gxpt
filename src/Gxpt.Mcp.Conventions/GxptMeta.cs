namespace Gxpt.Mcp.Conventions
{
    /// <summary>
    /// GxPT's own conventions layered on top of the transport-agnostic MCP <c>_meta</c> slot. The
    /// generic protocol libraries (<c>Mcp35.Core</c>/<c>Mcp35.Server</c>/<c>Mcp35.Client</c>) only know
    /// that a <c>tools/call</c> can carry an opaque <c>_meta</c> bag; this assembly defines the specific
    /// keys GxPT puts in it, so a product-specific convention never pollutes the protocol.
    /// <para>
    /// Both the host (which writes the field) and the workdir-scoped servers (which read it) reference
    /// this assembly, so the literal key is defined exactly once — the same reason
    /// <see cref="Mcp35.Core.Security.PathSandbox"/> is centralized rather than copied per consumer.
    /// </para>
    /// </summary>
    public static class GxptMeta
    {
        /// <summary>
        /// The conversation's current working directory for this call, as an <b>absolute</b> path the
        /// host has already canonicalized and validated to be within the server's launch-time workspace
        /// (<c>GXPT_WORKDIR</c>). Absent/empty means "the workspace root" — the safe floor. Workdir-scoped
        /// servers re-validate it against their own anchor before use (see <see cref="CwdScope"/>).
        /// </summary>
        public const string CwdKey = "gxpt.cwd";
    }
}

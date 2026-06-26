namespace Mcp35.Core.Protocol
{
    /// <summary>
    /// Well-known keys for the out-of-band <c>params._meta</c> map on a <c>tools/call</c> request.
    /// <para>
    /// <c>_meta</c> is the MCP-reserved metadata slot carried as a sibling of <c>arguments</c>: it is
    /// not part of any tool's input schema, so the host can pass per-call data the model neither sees
    /// nor controls. The host uses it to inject the conversation's <b>current working directory</b>
    /// (the "host-cd" mechanism) as a host-authoritative field, so a workdir-scoped server can run a
    /// call rooted at a subdirectory of its launch-time workspace without the model being able to
    /// widen the root by spoofing the field. Keys are namespaced (<c>gxpt.*</c>) so they never collide
    /// with a future protocol <c>_meta</c> convention.
    /// </para>
    /// </summary>
    public static class McpMeta
    {
        /// <summary>
        /// The conversation's current working directory for this call, as an <b>absolute</b> path the
        /// host has already canonicalized and validated to be within the server's launch-time
        /// workspace (<c>GXPT_WORKDIR</c>). Absent/empty means "the workspace root" — the safe floor.
        /// Workdir-scoped servers re-validate it against their own anchor before use (defense in depth).
        /// </summary>
        public const string CwdKey = "gxpt.cwd";
    }
}

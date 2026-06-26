using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mcp35.Core.Protocol
{
    /// <summary>
    /// An MCP tool. <see cref="InputSchema"/> and <see cref="Annotations"/> are open-ended
    /// JSON Schema / metadata and pass through verbatim as JObject (lossless round-trip).
    /// </summary>
    public sealed class Tool
    {
        [JsonProperty("name")]
        public string Name;

        // May be long or absent; deliberately NOT used in the names manifest.
        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
        public string Description;

        [JsonProperty("inputSchema", NullValueHandling = NullValueHandling.Ignore)]
        public JObject InputSchema;

        [JsonProperty("annotations", NullValueHandling = NullValueHandling.Ignore)]
        public JObject Annotations;
    }

    public sealed class ListToolsParams
    {
        [JsonProperty("cursor", NullValueHandling = NullValueHandling.Ignore)]
        public string Cursor;
    }

    public sealed class ListToolsResult
    {
        [JsonProperty("tools")]
        public List<Tool> Tools;

        // Present => the registry pages tools/list until exhausted.
        [JsonProperty("nextCursor", NullValueHandling = NullValueHandling.Ignore)]
        public string NextCursor;
    }

    public sealed class CallToolParams
    {
        [JsonProperty("name")]
        public string Name;

        [JsonProperty("arguments", NullValueHandling = NullValueHandling.Ignore)]
        public JObject Arguments;

        // Out-of-band request metadata (MCP-reserved `_meta`): a sibling of `arguments`, NOT part of
        // any tool's input schema, so the host can carry per-call data the model can neither see nor
        // spoof. The host injects the conversation's current working directory here (key
        // McpMeta.CwdKey) so workdir-scoped servers can re-root a `cd`-scoped call without the model
        // being able to widen the root. See the host-cd/worktree design.
        [JsonProperty("_meta", NullValueHandling = NullValueHandling.Ignore)]
        public JObject Meta;
    }

    public sealed class CallToolResult
    {
        [JsonProperty("content")]
        public List<ContentBlock> Content;

        // Tool-level failure (NOT a JsonRpcError) — the model sees this.
        [JsonProperty("isError", NullValueHandling = NullValueHandling.Ignore)]
        public bool IsError;

        [JsonProperty("structuredContent", NullValueHandling = NullValueHandling.Ignore)]
        public JObject StructuredContent;
    }
}

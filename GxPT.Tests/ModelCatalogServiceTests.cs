using System.Collections.Generic;
using GxPT;
using Xunit;
using Newtonsoft.Json.Linq;

namespace GxPT.Tests
{
    // Pure-logic coverage for the model context-size catalog: the /models JSON parse, the
    // "id<TAB>tokens" file format round-trip, and the alias/variant-tolerant lookup that the
    // status bar's context meter depends on. (The curl fetch itself needs a network and is
    // exercised manually.)
    public class ModelCatalogServiceTests
    {
        // ---- ParseModelsJson ----

        [Fact]
        public void Parses_ids_and_context_lengths_from_models_payload()
        {
            string json = @"{ ""data"": [
                { ""id"": ""anthropic/claude-sonnet-4.5"", ""context_length"": 200000, ""name"": ""x"" },
                { ""id"": ""openai/gpt-4o"", ""context_length"": 128000 }
            ] }";
            var map = ModelCatalogService.ParseModelsJson(json);
            Assert.NotNull(map);
            Assert.Equal(2, map.Count);
            Assert.Equal(200000, map["anthropic/claude-sonnet-4.5"]);
            Assert.Equal(128000, map["openai/gpt-4o"]);
        }

        [Fact]
        public void Skips_entries_without_a_usable_context_length()
        {
            string json = @"{ ""data"": [
                { ""id"": ""a/no-length"" },
                { ""id"": ""b/null-length"", ""context_length"": null },
                { ""id"": ""c/zero"", ""context_length"": 0 },
                { ""id"": ""d/ok"", ""context_length"": 32768 },
                { ""context_length"": 9999 },
                ""not-an-object""
            ] }";
            var map = ModelCatalogService.ParseModelsJson(json);
            Assert.NotNull(map);
            Assert.Single(map);
            Assert.Equal(32768, map["d/ok"]);
        }

        [Fact]
        public void Returns_null_for_malformed_or_unexpected_payloads()
        {
            Assert.Null(ModelCatalogService.ParseModelsJson("not json"));
            Assert.Null(ModelCatalogService.ParseModelsJson(@"{ ""error"": ""nope"" }"));
            Assert.Null(ModelCatalogService.ParseModelsJson(@"{ ""data"": ""not-an-array"" }"));
        }

        // ---- catalog file format ----

        [Fact]
        public void Catalog_file_round_trips_and_is_sorted()
        {
            var map = new Dictionary<string, int>
            {
                { "openai/gpt-4o", 128000 },
                { "anthropic/claude-sonnet-4.5", 200000 }
            };
            string text = ModelCatalogService.FormatCatalogFile(map);
            // Sorted by id, one tab-separated line each.
            Assert.Equal("anthropic/claude-sonnet-4.5\t200000\nopenai/gpt-4o\t128000\n", text);

            var back = ModelCatalogService.ParseCatalogFile(text);
            Assert.Equal(2, back.Count);
            Assert.Equal(200000, back["anthropic/claude-sonnet-4.5"]);
            Assert.Equal(128000, back["openai/gpt-4o"]);
        }

        [Fact]
        public void Catalog_file_parse_tolerates_comments_blanks_and_garbage()
        {
            string text = "# hand-edited\r\n\r\nno-tab-here\nbad/number\tNaN\nok/model\t100000\n\tmissing-id\n";
            var map = ModelCatalogService.ParseCatalogFile(text);
            Assert.Single(map);
            Assert.Equal(100000, map["ok/model"]);
        }

        // ---- ParseModelsJsonFull ----

        [Fact]
        public void ParseModelsJsonFull_parses_image_and_file_modalities()
        {
            string json = @"{ ""data"": [
                { ""id"": ""vendor/vision"", ""context_length"": 100000,
                  ""architecture"": { ""input_modalities"": [""text"", ""image""] } },
                { ""id"": ""vendor/docs"", ""context_length"": 50000,
                  ""architecture"": { ""input_modalities"": [""text"", ""image"", ""file""] } },
                { ""id"": ""vendor/text-only"", ""context_length"": 32768,
                  ""architecture"": { ""input_modalities"": [""text""] } },
                { ""id"": ""vendor/no-arch"", ""context_length"": 8192 }
            ] }";
            var map = ModelCatalogService.ParseModelsJsonFull(json);
            Assert.NotNull(map);
            Assert.Equal(4, map.Count);
            Assert.True(map["vendor/vision"].SupportsImageInput);
            Assert.False(map["vendor/vision"].SupportsFileInput);
            Assert.Equal(100000, map["vendor/vision"].ContextLength);
            Assert.True(map["vendor/docs"].SupportsImageInput);
            Assert.True(map["vendor/docs"].SupportsFileInput);
            Assert.False(map["vendor/text-only"].SupportsImageInput);
            Assert.False(map["vendor/no-arch"].SupportsImageInput);
        }

        [Fact]
        public void ParseModelsJsonFull_skips_entries_without_positive_context_length()
        {
            string json = @"{ ""data"": [
                { ""id"": ""a/no-ctx"",  ""architecture"": { ""input_modalities"": [""image""] } },
                { ""id"": ""b/zero"",    ""context_length"": 0 },
                { ""id"": ""c/ok"",      ""context_length"": 128000 }
            ] }";
            var map = ModelCatalogService.ParseModelsJsonFull(json);
            Assert.NotNull(map);
            Assert.Single(map);
            Assert.True(map.ContainsKey("c/ok"));
        }

        [Fact]
        public void ParseModelsJsonFull_raw_is_set_on_live_parse()
        {
            string json = @"{ ""data"": [
                { ""id"": ""vendor/model"", ""context_length"": 32768,
                  ""architecture"": { ""input_modalities"": [""text"", ""image""] },
                  ""name"": ""Test Model"" }
            ] }";
            var map = ModelCatalogService.ParseModelsJsonFull(json);
            Assert.NotNull(map["vendor/model"].Raw);
            Assert.Equal("Test Model", (string)map["vendor/model"].Raw["name"]);
        }

        [Fact]
        public void ParseModelsJsonFull_returns_null_for_malformed_payloads()
        {
            Assert.Null(ModelCatalogService.ParseModelsJsonFull("not json"));
            Assert.Null(ModelCatalogService.ParseModelsJsonFull(@"{ ""error"": ""nope"" }"));
        }

        // ---- info catalog file format ----

        [Fact]
        public void Info_catalog_file_round_trips()
        {
            var map = new Dictionary<string, ModelInfo>
            {
                { "vendor/vision", new ModelInfo("vendor/vision", 100000, true, false) },
                { "vendor/docs",   new ModelInfo("vendor/docs",   50000,  true, true) },
                { "vendor/text",   new ModelInfo("vendor/text",   32768,  false, false) }
            };
            string json = ModelCatalogService.FormatInfoCatalogFile(map);
            var back = ModelCatalogService.ParseInfoCatalogFile(json);
            Assert.Equal(3, back.Count);
            Assert.True(back["vendor/vision"].SupportsImageInput);
            Assert.False(back["vendor/vision"].SupportsFileInput);
            Assert.Equal(100000, back["vendor/vision"].ContextLength);
            Assert.True(back["vendor/docs"].SupportsImageInput);
            Assert.True(back["vendor/docs"].SupportsFileInput);
            Assert.False(back["vendor/text"].SupportsImageInput);
            // Raw is null when loaded from the persisted cache.
            Assert.Null(back["vendor/vision"].Raw);
        }

        [Fact]
        public void Info_catalog_file_is_sorted_by_id()
        {
            var map = new Dictionary<string, ModelInfo>
            {
                { "z/last",  new ModelInfo("z/last",  1000, false, false) },
                { "a/first", new ModelInfo("a/first", 2000, true,  false) }
            };
            string json = ModelCatalogService.FormatInfoCatalogFile(map);
            // "a/first" must appear before "z/last" in the output
            Assert.True(json.IndexOf("a/first") < json.IndexOf("z/last"));
        }

        [Fact]
        public void Info_catalog_file_tolerates_malformed_input()
        {
            Assert.NotNull(ModelCatalogService.ParseInfoCatalogFile(""));
            Assert.NotNull(ModelCatalogService.ParseInfoCatalogFile(null));
            Assert.NotNull(ModelCatalogService.ParseInfoCatalogFile("not json"));
            Assert.Empty(ModelCatalogService.ParseInfoCatalogFile("[]"));
        }

        // ---- TryGetModelInfo lookup ladder ----

        [Fact]
        public void TryGetModelInfo_lookup_ladder_and_modalities()
        {
            ModelCatalogService.SetModelInfoForTests(new Dictionary<string, ModelInfo>
            {
                { "vendor/vision-model", new ModelInfo("vendor/vision-model", 100000, true, false) },
                { "vendor/doc-model",    new ModelInfo("vendor/doc-model",    50000,  true, true) },
                { "vendor/text-model",   new ModelInfo("vendor/text-model",   32768,  false, false) }
            });
            try
            {
                ModelInfo info;
                // Verbatim match.
                Assert.True(ModelCatalogService.TryGetModelInfo("vendor/vision-model", out info));
                Assert.True(info.SupportsImageInput);
                Assert.False(info.SupportsFileInput);
                // "~" alias stripped.
                Assert.True(ModelCatalogService.TryGetModelInfo("~vendor/vision-model", out info));
                Assert.True(info.SupportsImageInput);
                // ":variant" suffix stripped.
                Assert.True(ModelCatalogService.TryGetModelInfo("vendor/vision-model:nitro", out info));
                Assert.True(info.SupportsImageInput);
                // Both image + file.
                Assert.True(ModelCatalogService.TryGetModelInfo("vendor/doc-model", out info));
                Assert.True(info.SupportsImageInput);
                Assert.True(info.SupportsFileInput);
                // Text-only model.
                Assert.True(ModelCatalogService.TryGetModelInfo("vendor/text-model", out info));
                Assert.False(info.SupportsImageInput);
                Assert.False(info.SupportsFileInput);
                // Unknown → false + null info.
                Assert.False(ModelCatalogService.TryGetModelInfo("nobody/unknown", out info));
                Assert.Null(info);
                Assert.False(ModelCatalogService.TryGetModelInfo(null, out info));
                Assert.False(ModelCatalogService.TryGetModelInfo("  ", out info));
            }
            finally
            {
                ModelCatalogService.SetModelInfoForTests(null);
            }
        }

        // ---- TryGetContextLength lookup ladder ----

        [Fact]
        public void Lookup_matches_verbatim_alias_and_variant_forms()
        {
            ModelCatalogService.SetMapForTests(new Dictionary<string, int>
            {
                { "anthropic/claude-sonnet-latest", 200000 },
                { "deepseek/deepseek-v4-pro", 164000 },
                { "meta-llama/llama-3-8b:free", 8192 }
            });
            try
            {
                int ctx;
                // Verbatim.
                Assert.True(ModelCatalogService.TryGetContextLength("deepseek/deepseek-v4-pro", out ctx));
                Assert.Equal(164000, ctx);
                // ":free" entries exist in the catalog and match verbatim.
                Assert.True(ModelCatalogService.TryGetContextLength("meta-llama/llama-3-8b:free", out ctx));
                Assert.Equal(8192, ctx);
                // "~" alias marker stripped.
                Assert.True(ModelCatalogService.TryGetContextLength("~anthropic/claude-sonnet-latest", out ctx));
                Assert.Equal(200000, ctx);
                // ":variant" routing suffix stripped when the suffixed id isn't listed.
                Assert.True(ModelCatalogService.TryGetContextLength("deepseek/deepseek-v4-pro:nitro", out ctx));
                Assert.Equal(164000, ctx);
                // Unknown model: false and zero (status bar falls back to a bare token count).
                Assert.False(ModelCatalogService.TryGetContextLength("nobody/ever-heard-of-it", out ctx));
                Assert.Equal(0, ctx);
                Assert.False(ModelCatalogService.TryGetContextLength(null, out ctx));
                Assert.False(ModelCatalogService.TryGetContextLength("  ", out ctx));
            }
            finally
            {
                ModelCatalogService.SetMapForTests(new Dictionary<string, int>());
            }
        }
    }
}

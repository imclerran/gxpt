using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace GxPT
{
    // Single source of truth for the models shipped to NEW installs and the default selection.
    // The settings schema (SettingsSchema.BuildDefaults, which seeds settings.json and backs the
    // form's typed fallback) and the model dropdown's fresh-install fallback
    // (MainForm.PopulateModelsFromSettings) both pull from here, so the list is defined once and they
    // can't drift apart. Existing users' configured models live in their own settings.json and are
    // never overwritten by this.
    internal static class ModelDefaults
    {
        // Selected by default on a fresh install. Must be one of Models below.
        public const string DefaultModel = "~anthropic/claude-sonnet-latest";

        // Default model for each agent effort tier (Settings > Tools > Sub-agents). Each must be one of
        // Models below so a fresh install's tier combos start on a real, in-list model. low = cheap/fast,
        // high = most capable.
        public const string DefaultEffortLow = "deepseek/deepseek-v4-flash";
        public const string DefaultEffortMedium = "z-ai/glm-5.2";
        public const string DefaultEffortHigh = "~anthropic/claude-opus-latest";

        // Default catalog, in display order (the model combo is sorted, so order is cosmetic there;
        // it is preserved verbatim in the seeded settings.json).
        public static readonly string[] Models = new string[]
        {
            "~anthropic/claude-opus-latest",
            "~anthropic/claude-sonnet-latest",
            "~anthropic/claude-haiku-latest",
            "~google/gemini-pro-latest",
            "~google/gemini-flash-latest",
            "google/gemma-4-31b-it",
            "openai/gpt-5.4",
            "openai/gpt-5.1-codex-mini",
            "openai/gpt-chat-latest",
            "openai/gpt-5.4-mini",
            "openai/gpt-4o",
            "deepseek/deepseek-v4-pro",
            "deepseek/deepseek-v4-flash",
            "moonshotai/kimi-k2.6",
            "moonshotai/kimi-k2.7-code",
            "qwen/qwen3.7-max",
            "qwen/qwen3.7-plus",
            "minimax/minimax-m3",
            "stepfun/step-3.7-flash",
            "z-ai/glm-5.2"
        };

        // Convenience copy as a List (callers that mutate or store a List).
        public static List<string> ModelList()
        {
            return new List<string>(Models);
        }

        // Content-derived fingerprint of the recommended catalog. The list is sorted before hashing
        // so that cosmetic reordering doesn't change the result - only a genuine add/remove does. This
        // is what drives the "updated recommended models" banner: callers compare it to the last value
        // the user acknowledged (recommended_hash_seen). Editing Models above changes this automatically,
        // so there is no version number to bump by hand.
        public static string RecommendedHash()
        {
            var sorted = (string[])Models.Clone();
            Array.Sort(sorted, StringComparer.Ordinal);
            string joined = string.Join("\n", sorted);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(joined));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8 && i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}

using GxPT;
using Xunit;

namespace GxPT.Tests.Mcp
{
    public class DoomLoopDetectorTests
    {
        // Record a sequence of same-arg calls; return the 0-based index of the first call that reports
        // a loop, or -1 if none did.
        private static int FirstHit(params string[] names)
        {
            var d = new DoomLoopDetector();
            for (int i = 0; i < names.Length; i++)
                if (d.Record(names[i], "{}")) return i;
            return -1;
        }

        [Fact]
        public void Period1_needs_three_identical_calls()
        {
            Assert.Equal(-1, FirstHit("a", "a"));       // two in a row is not yet a loop
            Assert.Equal(2, FirstHit("a", "a", "a"));   // the third closes the period-1 cycle
        }

        [Fact]
        public void Two_identical_then_a_break_does_not_fire()
        {
            Assert.Equal(-1, FirstHit("a", "a", "b"));
        }

        [Fact]
        public void Period2_oscillation_fires_on_the_second_repeat()
        {
            // The case a consecutive-identical check misses: edit -> test -> edit -> test ...
            Assert.Equal(-1, FirstHit("a", "b", "a"));      // A B A: one-and-a-half cycles, not yet
            Assert.Equal(3, FirstHit("a", "b", "a", "b"));  // A B A B: two full period-2 cycles
        }

        [Fact]
        public void Period3_and_period4_cycles_are_caught()
        {
            Assert.Equal(5, FirstHit("a", "b", "c", "a", "b", "c"));            // period 3, twice
            Assert.Equal(7, FirstHit("a", "b", "c", "d", "a", "b", "c", "d"));  // period 4, twice
            // A period-4 pattern that only completes once is not yet a loop.
            Assert.Equal(-1, FirstHit("a", "b", "c", "d", "a", "b", "c"));
        }

        [Fact]
        public void A_non_repeating_sequence_never_fires()
        {
            Assert.Equal(-1, FirstHit("a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l"));
        }

        [Fact]
        public void Distinct_leading_calls_do_not_seed_a_false_cycle()
        {
            // A unique prefix followed by a genuine period-1 tail fires only when the third identical
            // call lands - the earlier distinct calls never combine into a phantom cycle.
            Assert.Equal(5, FirstHit("x", "y", "z", "a", "a", "a"));
        }

        [Fact]
        public void Scope_salt_distinguishes_identical_calls_in_different_directories()
        {
            // A directory walk up a tree: "list ." then "cd .." repeated VERBATIM while moving through
            // different directories. Without the scope salt this read as a period-2 cycle and wrapped
            // a legitimately exploring agent up early (observed false positive).
            var d = new DoomLoopDetector();
            Assert.False(d.Record("files__list", "{\"path\":\".\"}", "C:\\r\\a\\b\\c"));
            Assert.False(d.Record("cd", "{\"path\":\"..\"}", "C:\\r\\a\\b"));
            Assert.False(d.Record("files__list", "{\"path\":\".\"}", "C:\\r\\a\\b"));
            Assert.False(d.Record("cd", "{\"path\":\"..\"}", "C:\\r\\a"));
            Assert.False(d.Record("files__list", "{\"path\":\".\"}", "C:\\r\\a"));
            Assert.False(d.Record("cd", "{\"path\":\"..\"}", "C:\\r"));
            Assert.False(d.Record("files__list", "{\"path\":\".\"}", "C:\\r"));
        }

        [Fact]
        public void Same_place_repetition_still_fires()
        {
            var d = new DoomLoopDetector();
            Assert.False(d.Record("files__write", "{\"path\":\"x\"}", "C:\\r\\sub"));
            Assert.False(d.Record("files__write", "{\"path\":\"x\"}", "C:\\r\\sub"));
            Assert.True(d.Record("files__write", "{\"path\":\"x\"}", "C:\\r\\sub"));
        }

        [Fact]
        public void Cycle_that_returns_to_the_same_places_still_fires()
        {
            // Oscillating between two directories doing the same things: the scopes repeat along with
            // the calls, so the salted period-4 cycle is still caught on its second full repetition.
            var d = new DoomLoopDetector();
            Assert.False(d.Record("cd", "{\"path\":\"sub\"}", "C:\\r\\sub"));
            Assert.False(d.Record("files__edit", "{\"path\":\"f\"}", "C:\\r\\sub"));
            Assert.False(d.Record("cd", "{\"path\":\"..\"}", "C:\\r"));
            Assert.False(d.Record("command__run", "{\"cmd\":\"test\"}", "C:\\r"));
            Assert.False(d.Record("cd", "{\"path\":\"sub\"}", "C:\\r\\sub"));
            Assert.False(d.Record("files__edit", "{\"path\":\"f\"}", "C:\\r\\sub"));
            Assert.False(d.Record("cd", "{\"path\":\"..\"}", "C:\\r"));
            Assert.True(d.Record("command__run", "{\"cmd\":\"test\"}", "C:\\r"));
        }

        [Fact]
        public void Null_scope_and_the_two_arg_overload_are_equivalent()
        {
            var d = new DoomLoopDetector();
            Assert.False(d.Record("a", "{}"));
            Assert.False(d.Record("a", "{}", null));
            Assert.True(d.Record("a", "{}"));
        }

        [Fact]
        public void Same_tool_with_different_args_is_not_a_cycle()
        {
            var d = new DoomLoopDetector();
            Assert.False(d.Record("files__read", "{\"path\":\"a\"}"));
            Assert.False(d.Record("files__read", "{\"path\":\"b\"}"));
            Assert.False(d.Record("files__read", "{\"path\":\"c\"}"));
        }

        [Fact]
        public void Argument_key_order_and_whitespace_share_a_signature()
        {
            Assert.Equal(
                DoomLoopDetector.Signature("t", "{\"a\":1,\"b\":2}"),
                DoomLoopDetector.Signature("t", "{ \"b\": 2, \"a\": 1 }"));
        }

        [Fact]
        public void Reordered_identical_calls_normalize_into_one_cycle()
        {
            var d = new DoomLoopDetector();
            Assert.False(d.Record("t", "{\"a\":1,\"b\":2}"));
            Assert.False(d.Record("t", "{\"b\":2,\"a\":1}"));
            Assert.True(d.Record("t", "{ \"a\":1, \"b\":2 }"));   // three semantically identical calls
        }

        [Fact]
        public void Nested_object_keys_are_sorted_recursively()
        {
            Assert.Equal(
                DoomLoopDetector.Signature("t", "{\"o\":{\"x\":1,\"y\":2}}"),
                DoomLoopDetector.Signature("t", "{\"o\":{\"y\":2,\"x\":1}}"));
        }

        [Fact]
        public void Array_order_stays_significant()
        {
            Assert.NotEqual(
                DoomLoopDetector.Signature("t", "{\"a\":[1,2]}"),
                DoomLoopDetector.Signature("t", "{\"a\":[2,1]}"));
        }

        [Fact]
        public void Different_argument_values_are_different_signatures()
        {
            Assert.NotEqual(
                DoomLoopDetector.Signature("t", "{\"path\":\"a\"}"),
                DoomLoopDetector.Signature("t", "{\"path\":\"b\"}"));
        }

        [Fact]
        public void Unparseable_arguments_fall_back_to_raw_text_without_throwing()
        {
            var d = new DoomLoopDetector();
            Assert.False(d.Record("t", "{not json"));
            Assert.False(d.Record("t", "{not json"));
            Assert.True(d.Record("t", "{not json"));   // identical raw junk still forms a cycle
        }

        [Fact]
        public void Null_name_and_args_are_handled()
        {
            var d = new DoomLoopDetector();
            Assert.False(d.Record(null, null));
            Assert.False(d.Record(null, null));
            Assert.True(d.Record(null, null));
        }

        [Fact]
        public void Clear_resets_the_window()
        {
            var d = new DoomLoopDetector();
            Assert.False(d.Record("a", "{}"));
            Assert.False(d.Record("a", "{}"));
            d.Clear();
            Assert.False(d.Record("a", "{}"));   // the pre-Clear pair no longer counts
            Assert.False(d.Record("a", "{}"));
            Assert.True(d.Record("a", "{}"));    // three fresh calls close the cycle
        }

        [Fact]
        public void Detection_survives_window_eviction()
        {
            // Fill and rotate the bounded window (MaxHistory=12) with distinct calls, then a fresh
            // period-1 tail: trimming the ring must not corrupt detection - the third identical call
            // still fires (index 14 of the 15-call sequence).
            Assert.Equal(14, FirstHit("d0", "d1", "d2", "d3", "d4", "d5", "d6", "d7", "d8", "d9", "d10",
                                      "d11", "z", "z", "z"));
        }

        [Fact]
        public void Widest_cycle_still_fires_after_eviction()
        {
            // The 12-entry window must leave room for the widest cycle (period 4 needs 8 signatures)
            // even after older entries are evicted: an A B C D A B C D tail behind a full window still
            // fires, on its final call (index 15).
            Assert.Equal(15, FirstHit("p0", "p1", "p2", "p3", "p4", "p5", "p6", "p7",
                                      "a", "b", "c", "d", "a", "b", "c", "d"));
        }
    }
}

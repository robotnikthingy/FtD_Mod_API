using System;
using System.Collections.Generic;
using System.Linq;
using BrilliantSkies.Ftd.AdvancedCannons;
using FtdModApi;
using UnityEngine;

namespace FtdModApiTest
{
    /// <summary>
    /// Checks that need nothing from the player. They run once the main menu is ready, use only this mod's own
    /// test values, and undo everything they change.
    /// </summary>
    internal static class SelfTests
    {
        private const string G = "Self-tests (no game needed)";

        private sealed class OrderContext
        {
            public readonly List<string> Ran = new List<string>();
        }

        internal static void RunAll()
        {
            Report.Run(G, "Modifier pipeline runs in priority order", PipelineOrder);
            Report.Run(G, "A failing modifier is switched off and the rest keep running", PipelineFailure);
            Report.Run(G, "API events reach every handler", ApiEvent);
            Report.Run(G, "Tunables: Add, Multiply and Set combine, and undo cleanly", TunableCombine);
            Report.Run(G, "Tunables: limits (min/max) hold", TunableLimits);
            Report.Run(G, "Tunables: game values are readable", GameTunable);
            Report.Run(G, "Content ids are stable and reversible", ContentIdsCheck);
            Report.Run(G, "ModNet messages round-trip every value type", ModNetRoundTrip);
            Report.Run(G, "ModNet rejects short or damaged messages safely", ModNetDamaged);
            Report.Run(G, "Key combos parse and print", KeyCombos);
            Report.Run(G, "Content: block list and lookups", ContentLookups);
            Report.Run(G, "APS payload numbers match the game's formulas", PayloadNumbers);
        }

        // ---- pipelines ---------------------------------------------------------------------------------------------

        private static ModifierPipeline<OrderContext> _orderPipeline;
        private static ModifierPipeline<OrderContext> _failPipeline;
        private static ApiEvent<OrderContext> _event;

        private static string PipelineOrder()
        {
            _orderPipeline = _orderPipeline ?? Plugin.TestSystem.CreatePipeline<OrderContext>("OrderTest");
            var regs = new[]
            {
                _orderPipeline.Add(Plugin.ModId, c => c.Ran.Add("clamp"), Priority.Clamp, "clamp"),
                _orderPipeline.Add(Plugin.ModId, c => c.Ran.Add("add"), Priority.Add, "add"),
                _orderPipeline.Add(Plugin.ModId, c => c.Ran.Add("observe"), Priority.Observe, "observe"),
                _orderPipeline.Add(Plugin.ModId, c => c.Ran.Add("multiply"), Priority.Multiply, "multiply"),
                _orderPipeline.Add(Plugin.ModId, c => c.Ran.Add("override"), Priority.Override, "override")
            };
            var ctx = new OrderContext();
            _orderPipeline.Run(ctx);
            var order = string.Join(" > ", ctx.Ran);
            CheckFailed.If(order != "add > multiply > override > clamp > observe", "ran as " + order);

            foreach (var r in regs) r.Dispose();
            CheckFailed.If(_orderPipeline.Count != 0, $"{_orderPipeline.Count} modifier(s) left after removing them all");
            var after = new OrderContext();
            _orderPipeline.Run(after);
            CheckFailed.If(after.Ran.Count != 0, "a removed modifier still ran");
            return order + "; removing them works";
        }

        private static string PipelineFailure()
        {
            _failPipeline = _failPipeline ?? Plugin.TestSystem.CreatePipeline<OrderContext>("FailureTest");
            int thrown = 0, ranAfter = 0;
            // This modifier throws on purpose: the two "ApiTest.FailureTest" errors in the game log are expected.
            var bad = _failPipeline.Add(Plugin.ModId, c => { thrown++; throw new InvalidOperationException("expected test failure (ignore)"); }, Priority.Add, "expected-failure test");
            var good = _failPipeline.Add(Plugin.ModId, c => ranAfter++, Priority.Multiply, "keeps running");
            for (int i = 0; i < ModifierPipeline<OrderContext>.MaxFailures + 3; i++) _failPipeline.Run(new OrderContext());
            bad.Dispose();
            good.Dispose();
            int runs = ModifierPipeline<OrderContext>.MaxFailures + 3;
            CheckFailed.If(ranAfter != runs, $"the healthy modifier ran {ranAfter} of {runs} times");
            CheckFailed.If(thrown != ModifierPipeline<OrderContext>.MaxFailures, $"the failing modifier ran {thrown} times (expected {ModifierPipeline<OrderContext>.MaxFailures}, then switched off)");
            return $"switched off after {thrown} failures; the other modifier ran all {runs} times (the 2 errors about 'expected-failure test' in the log are part of this test)";
        }

        private static string ApiEvent()
        {
            _event = _event ?? Plugin.TestSystem.CreateEvent<OrderContext>("EventTest");
            var a = _event.Subscribe(Plugin.ModId, c => c.Ran.Add("a"), 0, "a");
            var b = _event.Subscribe(Plugin.ModId, c => c.Ran.Add("b"), 10, "b");
            var ctx = new OrderContext();
            _event.Raise(ctx);
            a.Dispose();
            b.Dispose();
            CheckFailed.If(string.Join(",", ctx.Ran) != "a,b", "handlers ran as " + string.Join(",", ctx.Ran));
            return "2 handlers, in priority order";
        }

        // ---- tunables ----------------------------------------------------------------------------------------------

        private static string TunableCombine()
        {
            var key = Plugin.TestValue.Key;
            void Expect(double expected, string step)
            {
                double now = Tunables.Get(key);
                CheckFailed.If(Math.Abs(now - expected) > 1e-6, $"{step}: {key} is {now}, expected {expected}");
                CheckFailed.If(Math.Abs(Plugin.TestValue.Value - expected) > 1e-6, $"{step}: the mod's value object reads {Plugin.TestValue.Value}, expected {expected}");
            }

            int changedEvents = 0;
            Action<TunableValue> onChanged = v => changedEvents++;
            Plugin.TestValue.Changed += onChanged;
            try
            {
                Expect(10, "default");
                var mul = Tunables.Multiply(Plugin.ModId, key, 2);
                Expect(20, "x2");
                var add = Tunables.Add(Plugin.ModId, key, 5);
                Expect(30, "+5 then x2 (adds run before multiplies)");
                var set = Tunables.Set(Plugin.ModId, key, 7.0);
                Expect(7, "set 7 (overrides run last)");
                set.Dispose();
                Expect(30, "set removed");
                add.Dispose();
                mul.Dispose();
                Expect(10, "all removed");
            }
            finally
            {
                Plugin.TestValue.Changed -= onChanged;
            }
            CheckFailed.If(changedEvents == 0, "the value's Changed event never fired");
            return $"10 > 20 > 30 > 7 > 30 > 10; Changed fired {changedEvents} times";
        }

        private static string TunableLimits()
        {
            var key = Plugin.TestLimited.Key;
            var reg = Tunables.Multiply(Plugin.ModId, key, 100);
            double high = Tunables.Get(key);
            reg.Dispose();
            var low = Tunables.Add(Plugin.ModId, key, -100);
            double lowValue = Tunables.Get(key);
            low.Dispose();
            double back = Tunables.Get(key);
            CheckFailed.If(Math.Abs(high - 10) > 1e-6, $"x100 gave {high}, the maximum is 10");
            CheckFailed.If(Math.Abs(lowValue - 0) > 1e-6, $"-100 gave {lowValue}, the minimum is 0");
            CheckFailed.If(Math.Abs(back - 5) > 1e-6, $"after undoing it reads {back}, the default is 5");
            return "held at 10 and 0, back to 5";
        }

        private static string GameTunable()
        {
            const string key = "AdvCannon.BaseExplosiveDamage";
            CheckFailed.If(!Tunables.Exists(key), key + " is not listed");
            double viaApi = Tunables.Get(key);
            double field = AdvCannonConstants.BaseExplosiveDamage;
            CheckFailed.If(Math.Abs(viaApi - field) > 1e-3, $"{key}: the API reads {viaApi} but the game uses {field}");
            return $"{key} = {viaApi} (same as the game); {Tunables.Systems.Count} mod system(s) registered";
        }

        // ---- content ids, ModNet data, key combos ------------------------------------------------------------------

        private static string ContentIdsCheck()
        {
            const string category = "FtdModApiTest", id = "FtdModApiTest.Sample";
            int a = ContentIds.Register(category, id);
            int b = ContentIds.Register(category, id);
            int c = ContentIds.Compute(category, id);
            CheckFailed.If(a != b || a != c, $"registering twice gave {a} and {b}, computing gave {c}");
            CheckFailed.If(ContentIds.Lookup(category, a) != id, "Lookup did not return the id");
            CheckFailed.If(!ContentIds.IsModded(a), $"{a} is not in the modded range");
            // The number is fixed forever (it is saved in blueprints): this is the value every machine must compute.
            CheckFailed.If(ContentIds.Compute("ApsShellPart", "ExampleBalanceMod.DenseSolidBody") != ContentIds.Compute("ApsShellPart", "ExampleBalanceMod.DenseSolidBody"),
                "Compute is not stable within a session");
            return $"'{id}' is {a} every time";
        }

        private static string ModNetRoundTrip()
        {
            var guid = Guid.NewGuid();
            var text = "Grüße, ✓ 漢字";
            var bytes = new byte[] { 0, 1, 2, 250, 255 };
            var w = new ModNetWriter()
                .Write(true).Write((byte)200).Write(-123456).Write(4000000000u).Write(-9876543210L).Write(ulong.MaxValue)
                .Write(3.25f).Write(Math.PI).Write(text).Write(string.Empty).Write(bytes).Write(guid)
                .Write(new Vector3(1, -2, 3.5f)).Write(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)).Write(new Color(0.1f, 0.5f, 1f, 0.25f));
            var r = new ModNetReader(w.ToArray());
            CheckFailed.If(r.ReadBool() != true, "bool");
            CheckFailed.If(r.ReadByte() != 200, "byte");
            CheckFailed.If(r.ReadInt() != -123456, "int");
            CheckFailed.If(r.ReadUInt() != 4000000000u, "uint");
            CheckFailed.If(r.ReadLong() != -9876543210L, "long");
            CheckFailed.If(r.ReadULong() != ulong.MaxValue, "ulong");
            CheckFailed.If(r.ReadFloat() != 3.25f, "float");
            CheckFailed.If(r.ReadDouble() != Math.PI, "double");
            CheckFailed.If(r.ReadString() != text, "string (unicode)");
            CheckFailed.If(r.ReadString() != string.Empty, "empty string");
            CheckFailed.If(!r.ReadBytes().SequenceEqual(bytes), "bytes");
            CheckFailed.If(r.ReadGuid() != guid, "guid");
            CheckFailed.If(r.ReadVector3() != new Vector3(1, -2, 3.5f), "Vector3");
            var q = r.ReadQuaternion();
            CheckFailed.If(q.x != 0.1f || q.y != 0.2f || q.z != 0.3f || q.w != 0.9f, "Quaternion");
            var col = r.ReadColor();
            CheckFailed.If(col.r != 0.1f || col.g != 0.5f || col.b != 1f || col.a != 0.25f, "Color");
            CheckFailed.If(r.Remaining != 0, $"{r.Remaining} bytes left over");
            return $"15 values, {w.Length} bytes";
        }

        private static string ModNetDamaged()
        {
            var full = new ModNetWriter().Write("hello").Write(42).ToArray();

            bool Rejects(Action read)
            {
                try
                {
                    read();
                    return false;
                }
                catch (ModNetFormatException)
                {
                    return true;
                }
            }

            CheckFailed.If(!Rejects(() => new ModNetReader(full.Take(3).ToArray()).ReadString()), "a cut-off string was read without an error");
            CheckFailed.If(!Rejects(() => { var r = new ModNetReader(full); r.ReadString(); r.ReadInt(); r.ReadInt(); }), "reading past the end gave no error");
            var lying = new ModNetWriter().Write(int.MaxValue).ToArray(); // a length far longer than the message
            CheckFailed.If(!Rejects(() => new ModNetReader(lying).ReadString()), "a string claiming 2 GB was not rejected");
            CheckFailed.If(!Rejects(() => new ModNetReader(lying).ReadBytes()), "a byte array claiming 2 GB was not rejected");
            return "cut-off data, reading past the end and bad lengths all give ModNetFormatException";
        }

        private static string KeyCombos()
        {
            var combo = new KeyCombo(KeyCode.T, KeyModifiers.Ctrl | KeyModifiers.Shift);
            CheckFailed.If(!KeyCombo.TryParse(combo.ToString(), out var back) || back != combo, $"'{combo}' did not parse back to itself");
            CheckFailed.If(!KeyCombo.TryParse("ctrl+shift+t", out var lower) || lower != combo, "lower-case 'ctrl+shift+t' did not parse");
            CheckFailed.If(KeyCombo.TryParse("Ctrl+NotAKey", out _), "'Ctrl+NotAKey' parsed");
            return combo + " round-trips";
        }

        private static string ContentLookups()
        {
            var items = Content.Items;
            CheckFailed.If(items.Count < 100, $"only {items.Count} blocks listed");
            var metal = Content.FindItem("Metal Block");
            CheckFailed.If(metal == null, "'Metal Block' not found by name");
            var byGuid = Content.FindItem(metal.Guid.ToString());
            CheckFailed.If(byGuid == null || byGuid.Guid != metal.Guid, "'Metal Block' not found by its guid");
            CheckFailed.If(Content.FindItem("FtdModApiTest no such block") != null, "a block that does not exist was found");
            return $"{items.Count} blocks; Metal Block = {metal.Guid}";
        }

        private static string PayloadNumbers()
        {
            float strength = 1f;
            float he = ApsPayloads.HeDamage(strength);
            float expected = PayloadDerivedValues.GetScaledExplosionDamage(AdvCannonConstants.BaseExplosiveDamage);
            CheckFailed.If(Math.Abs(he - expected) > 1e-2f, $"HeDamage(1) = {he}, the game gives {expected}");
            CheckFailed.If(Math.Abs(ApsPayloads.EmpDamage(2f) - 2f * AdvCannonConstants.BaseEmpDamage) > 1e-2f, "EmpDamage does not match the game");
            CheckFailed.If(ApsPayloads.ExplosionRadius(he) <= 0f, "explosion radius is not positive");
            return $"strength 1 = {he:0} HE damage, radius {ApsPayloads.ExplosionRadius(he):0.0} m";
        }
    }
}

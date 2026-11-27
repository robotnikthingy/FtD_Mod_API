using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FtdModApi;

namespace FtdModApiTest
{
    /// <summary>
    /// Content the test mod registers so those features can be tried: a craft module, a breadboard component, a Lua
    /// function and an APS shell part. None changes gameplay.
    /// </summary>
    internal static class TestParts
    {
        internal const string CraftGroup = "Craft data and modules";
        internal const string BoardGroup = "Breadboard components";
        internal const string LuaGroup = "Lua functions";
        internal const string ShellGroup = "APS shell parts and payloads";

        internal const string ShellPartId = "FtdModApiTest.CountingHeBody";

        internal static void RegisterAll()
        {
            // Craft module: on every craft, saved with it.
            Report.Run("Setup", "Craft module registered", () =>
            {
                var info = CraftModules.Register<TestCraftModule>(Plugin.ModId, "test-module");
                CheckFailed.If(info == null, "Register returned null");
                return "on every craft";
            });
            Report.Expect(CraftGroup, "Module created when a craft spawns", "spawn any craft");
            Report.Expect(CraftGroup, "CraftData set, read, list and clear", "spawn any craft");
            Report.Expect(CraftGroup, "CraftData JSON values round-trip", "spawn any craft");
            Report.Expect(CraftGroup, "Module updates while the craft is in play", "spawn any craft and wait a second");
            Report.Expect(CraftGroup, "Module saves with the craft", "save a craft (blueprint, campaign save or autosave)");
            Report.Expect(CraftGroup, "Saved module data comes back when the craft spawns again", "save a craft as a blueprint, then spawn that blueprint");
            Report.Expect(CraftGroup, "Module removed with the craft", "delete a craft or leave the game");

            // Breadboard component.
            Report.Run("Setup", "Breadboard component registered", () =>
            {
                var info = BreadboardComponents.Register<TestEcho>(Plugin.ModId, "echo", "[API test] Echo",
                    "Outputs its input unchanged and counts its runs (FtdModApiTest).", BreadboardKinds.All);
                CheckFailed.If(info == null, "Register returned null");
                return "'[API test] Echo' on every breadboard type";
            });
            Report.Expect(BoardGroup, "A mod component runs on a breadboard", "put '[API test] Echo' on a breadboard (it is in the component menu under mod components)");
            Report.Expect(BoardGroup, "Its input reaches its output", "connect a constant to '[API test] Echo' and its output to something");

            // Lua function.
            Report.Run("Setup", "Lua function registered", () =>
            {
                var info = LuaFunctions.Register(Plugin.ModId, "Ping", call =>
                {
                    var text = call.String(0, "");
                    Report.Seen(LuaGroup, "Lua can call a mod function", $"from '{call.Construct?.GetName()}' with \"{text}\"");
                    return "pong " + text;
                }, "Returns \"pong \" plus its argument (FtdModApiTest).");
                CheckFailed.If(info == null, "Register returned null");
                return info.LuaName;
            });
            Report.Expect(LuaGroup, "Lua can call a mod function", "in a Lua box: function Update(I) I:Log(Mods.FtdModApiTest.Ping(\"hi\")) end");

            // APS shell part: an exact copy of the HE body that also counts its detonations.
            Report.Run("Setup", "APS shell part registered", () =>
            {
                var part = ShellParts.Register(Plugin.ModId, new ShellPartDefinition
                {
                    Id = ShellPartId,
                    Name = "[API test] HE body",
                    Description = "Exactly the game's HE body; FtdModApiTest counts its detonations. Shells keep working if the test mod is removed (the part is dropped).",
                    BehavesAs = "HeWarhead",
                    Payload = new CountingPayload(),
                    KeepGamePayload = true
                });
                CheckFailed.If(part == null, "Register returned null");
                CheckFailed.If(part.Number != ContentIds.Compute("ApsShellPart", ShellPartId), "its saved number is not the one computed from its id");
                CheckFailed.If(ShellParts.Find(ShellPartId) != part, "Find did not return it");
                return $"'[API test] HE body' #{part.Number}";
            });
            Report.Expect(ShellGroup, "The test part is in the ammo customiser", "open an APS ammo customiser and look for '[API test] HE body'");
            Report.Expect(ShellGroup, "A shell with the test part detonates through the payload hook", "fire a shell that has '[API test] HE body' at something");
        }
    }

    /// <summary>Craft module that checks its own lifecycle and CraftData.</summary>
    public sealed class TestCraftModule : CraftModule
    {
        private const string SavedPrefix = "FtdModApiTest:";
        private int _saves;
        private static int _dataChecked;
        private static int _jsonChecked;

        private sealed class Sample
        {
            public int Number { get; set; }
            public string Text { get; set; }
            public List<double> Values { get; set; }
        }

        protected override void OnSpawned(string savedData)
        {
            var name = Craft?.GetName() ?? "?";
            Report.Seen(TestParts.CraftGroup, "Module created when a craft spawns", $"'{name}'");
            if (savedData != null)
            {
                if (savedData.StartsWith(SavedPrefix, StringComparison.Ordinal))
                    Report.Seen(TestParts.CraftGroup, "Saved module data comes back when the craft spawns again", $"'{name}': \"{savedData}\"");
                else
                    Report.Fail(TestParts.CraftGroup, "Saved module data comes back when the craft spawns again", $"'{name}': unexpected data \"{savedData}\"");
            }

            // CraftData: a probe value is set and cleared at once, so nothing extra is saved with player designs.
            if (Interlocked.Exchange(ref _dataChecked, 1) == 0)
            {
                Report.Run(TestParts.CraftGroup, "CraftData set, read, list and clear", () =>
                {
                    var probe = Guid.NewGuid().ToString("N");
                    CraftData.Set(Craft, Plugin.ModId, "probe", probe);
                    CheckFailed.If(CraftData.Get(Craft, Plugin.ModId, "probe") != probe, "Get did not return what Set stored");
                    CheckFailed.If(!CraftData.Has(Craft, Plugin.ModId, "probe"), "Has is false after Set");
                    CheckFailed.If(!CraftData.Keys(Craft, Plugin.ModId).Contains("probe"), "Keys does not list it");
                    CraftData.Set(Craft, Plugin.ModId, "probe", null);
                    CheckFailed.If(CraftData.Has(Craft, Plugin.ModId, "probe"), "setting null did not remove it");
                    CraftData.Clear(Craft, Plugin.ModId);
                    CheckFailed.If(CraftData.Keys(Craft, Plugin.ModId).Count != 0, "Clear left keys behind");
                    return $"on '{name}'";
                });
            }
            if (Interlocked.Exchange(ref _jsonChecked, 1) == 0)
            {
                Report.Run(TestParts.CraftGroup, "CraftData JSON values round-trip", () =>
                {
                    var sample = new Sample { Number = 42, Text = "Grüße", Values = new List<double> { 1.5, -2 } };
                    CraftData.SetJson(Craft, Plugin.ModId, "json", sample);
                    var back = CraftData.GetJson<Sample>(Craft, Plugin.ModId, "json");
                    CraftData.Clear(Craft, Plugin.ModId);
                    CheckFailed.If(back == null, "GetJson returned null");
                    CheckFailed.If(back.Number != 42 || back.Text != "Grüße" || back.Values == null || !back.Values.SequenceEqual(sample.Values), "the value came back different");
                    CheckFailed.If(CraftData.GetJson(Craft, Plugin.ModId, "json", -1) != -1, "the fallback was not used after Clear");
                    return "object with a list and unicode text";
                });
            }
        }

        protected override string OnSave()
        {
            int n = Interlocked.Increment(ref _saves);
            Report.Seen(TestParts.CraftGroup, "Module saves with the craft", $"'{Craft?.GetName()}'");
            return SavedPrefix + "saved " + n + " time(s) this session";
        }

        protected override void OnUpdate()
        {
            Report.Seen(TestParts.CraftGroup, "Module updates while the craft is in play", $"'{Craft?.GetName()}'");
        }

        protected override void OnRemoved()
        {
            Report.Seen(TestParts.CraftGroup, "Module removed with the craft", $"'{Craft?.GetName()}'");
        }
    }

    /// <summary>Breadboard component: output = input.</summary>
    public sealed class TestEcho : ModBreadboardComponent
    {
        public TestEcho()
        {
            AddInput("In", "Any number");
            AddOutput("Out", "The same number");
        }

        protected override void Compute()
        {
            double v = InNumber(0);
            Out(0, v);
            Report.Seen(TestParts.BoardGroup, "A mod component runs on a breadboard", $"on a {Kind} breadboard");
            if (v != 0) Report.Seen(TestParts.BoardGroup, "Its input reaches its output", $"{v:0.###} in, {v:0.###} out");
        }
    }

    /// <summary>Payload of the test shell part: counts detonations, does nothing else.</summary>
    public sealed class CountingPayload : ApsPayload
    {
        public override void Detonate(ApsPayloadHit hit)
        {
            Report.Seen(TestParts.ShellGroup, "A shell with the test part detonates through the payload hook",
                $"{hit.Count} part(s), strength {hit.Strength:0.###}, {hit.Reason}{(hit.Craft != null ? $", on '{hit.Craft.GetName()}'" : ", in the open")}");
        }

        public override IEnumerable<ApsPayloadStat> Describe(ApsPayloadShell shell)
        {
            Report.Seen(TestParts.ShellGroup, "The test part is in the ammo customiser", $"{shell.Count} part(s) in a {shell.Diameter * 1000f:0} mm shell");
            yield return new ApsPayloadStat("Test parts", shell.Count.ToString(), "FtdModApiTest: the stats tab shows mod payload rows.");
        }
    }
}

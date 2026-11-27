using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BrilliantSkies.Core.Id;
using FtdModApi.Patching;
using HarmonyLib;

namespace FtdModApi
{
    /// <summary>
    /// The armour class of one block, as the game is about to use it (damage, explosion resistance,
    /// the block tooltip). Change <see cref="ArmourClass"/>. Possibly on a worker thread; the context is
    /// reused, so do not keep it.
    /// </summary>
    public sealed class ArmourClassContext
    {
        [ThreadStatic] private static ArmourClassContext _cached;

        private bool _inUse;

        private ArmourClassContext()
        {
        }

        internal static ArmourClassContext Rent(Block block, float armourClass)
        {
            var c = _cached != null && !_cached._inUse ? _cached : (_cached == null ? (_cached = new ArmourClassContext()) : new ArmourClassContext());
            c._inUse = true;
            c.Block = block;
            c.ArmourClass = armourClass;
            c.OriginalArmourClass = armourClass;
            return c;
        }

        internal void Release()
        {
            _inUse = false;
            Block = null;
        }

        /// <summary>Armour class to use. Values below 1 are raised to 1, as the game does.</summary>
        public float ArmourClass { get; set; }

        /// <summary>
        /// The game's value: the item's armour class, reduced by fire damage and raised by ring shields.
        /// </summary>
        public float OriginalArmourClass { get; private set; }

        /// <summary>The item's base armour class, before fire damage and shields.</summary>
        public float ItemArmourClass => Block.item?.ArmourClass ?? OriginalArmourClass;

        public string BlockName => Block.item?.ComponentId?.Name;

        public Guid BlockGuid => Block.item?.ComponentId?.Guid ?? Guid.Empty;

        /// <summary>The construct the block belongs to, or null.</summary>
        public MainConstruct Construct => Block.MainConstruct as MainConstruct;

        /// <summary>Raw game block. Unstable.</summary>
        public Block Block { get; private set; }
    }

    /// <summary>
    /// The maximum health of one block, worked out when it is placed or loaded (and again when campaign difficulty
    /// or the damage setting changes). Change <see cref="MaxHealth"/>. The context is reused, so do not keep it.
    /// </summary>
    public sealed class MaxHealthContext
    {
        [ThreadStatic] private static MaxHealthContext _cached;

        private bool _inUse;

        private MaxHealthContext()
        {
        }

        internal static MaxHealthContext Rent(Block block, ObjectId team, float itemHealth, float maxHealth)
        {
            var c = _cached != null && !_cached._inUse ? _cached : (_cached == null ? (_cached = new MaxHealthContext()) : new MaxHealthContext());
            c._inUse = true;
            c.Block = block;
            c.Team = team;
            c.ItemHealth = itemHealth;
            c.OriginalMaxHealth = maxHealth;
            c.MaxHealth = maxHealth;
            return c;
        }

        internal void Release()
        {
            _inUse = false;
            Block = null;
            Team = null;
        }

        /// <summary>Maximum health to use (at least 1). Current health is scaled with it.</summary>
        public float MaxHealth { get; set; }

        /// <summary>The game's value: the item's health with campaign difficulty and the damage setting applied.</summary>
        public float OriginalMaxHealth { get; private set; }

        /// <summary>The health in the block's item definition.</summary>
        public float ItemHealth { get; private set; }

        public string BlockName => Block?.item?.ComponentId?.Name;

        public Guid BlockGuid => Block?.item?.ComponentId?.Guid ?? Guid.Empty;

        /// <summary>The team the block belongs to (may be null while a construct is being built).</summary>
        public ObjectId Team { get; private set; }

        /// <summary>Raw game block. Unstable.</summary>
        public Block Block { get; private set; }
    }

    /// <summary>Hooks into block properties.</summary>
    public static class BlockApi
    {
        /// <summary>
        /// Modify any block's armour class. Feature id: <see cref="Features.ArmourClass"/>.
        /// Because the game reads armour class wherever it needs it, this also changes what the block
        /// tooltip shows ("Armour class boosted from X to Y").
        /// </summary>
        /// <example>
        /// <code>
        /// BlockApi.ArmourClass.Add("MyMod", ctx => { if (ctx.BlockName == "Metal Block") ctx.ArmourClass *= 1.1f; });
        /// </code>
        /// </example>
        public static ModifierPipeline<ArmourClassContext> ArmourClass { get; } =
            new ModifierPipeline<ArmourClassContext>(Features.ArmourClass);

        /// <summary>
        /// Modify any block's maximum health. Feature id: <see cref="Features.BlockHealth"/>.
        /// Applies to blocks placed or loaded after the modifier is added.
        /// </summary>
        /// <example>
        /// <code>
        /// BlockApi.MaxHealth.Add("MyMod", ctx => { if (ctx.BlockName == "Metal Block") ctx.MaxHealth *= 1.25f; });
        /// </code>
        /// </example>
        public static ModifierPipeline<MaxHealthContext> MaxHealth { get; } =
            new ModifierPipeline<MaxHealthContext>(Features.BlockHealth);

        /// <summary>Convenience: multiply one block type's maximum health (guid or name). Works for duplicated blocks too.</summary>
        public static Registration SetHealthMultiplier(string modId, string blockGuidOrName, float factor)
        {
            var match = BlockMatcher(blockGuidOrName, factor);
            return MaxHealth.Add(modId, ctx => { if (match(ctx.BlockGuid, ctx.BlockName)) ctx.MaxHealth *= factor; }, Priority.Multiply, $"{blockGuidOrName} health x{factor}");
        }

        /// <summary>Convenience: multiply one block type's armour class (guid or name).</summary>
        public static Registration SetArmourMultiplier(string modId, string blockGuidOrName, float factor)
        {
            var match = BlockMatcher(blockGuidOrName, factor);
            return ArmourClass.Add(modId, ctx => { if (match(ctx.BlockGuid, ctx.BlockName)) ctx.ArmourClass *= factor; }, Priority.Multiply, $"{blockGuidOrName} armour x{factor}");
        }

        private static Func<Guid, string, bool> BlockMatcher(string blockGuidOrName, float factor)
        {
            if (string.IsNullOrWhiteSpace(blockGuidOrName)) throw new ArgumentException("A block guid or name is required.", nameof(blockGuidOrName));
            if (float.IsNaN(factor) || factor <= 0f) throw new ArgumentOutOfRangeException(nameof(factor), "Must be more than 0.");
            var key = blockGuidOrName.Trim();
            if (Guid.TryParse(key, out var guid)) return (g, n) => g == guid;
            return (g, n) => string.Equals(n, key, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Called from patched game code in place of Block.CalculateMaximumHealth(team, itemHealth).</summary>
        public static float CalculateMaximumHealthFor(ObjectId team, float itemHealth, Block block)
        {
            float health = Block.CalculateMaximumHealth(team, itemHealth);
            var pipeline = MaxHealth;
            if (pipeline.IsEmpty || block == null) return health;

            var ctx = MaxHealthContext.Rent(block, team, itemHealth, health);
            try
            {
                pipeline.Run(ctx);
                var v = ctx.MaxHealth;
                var result = float.IsNaN(v) || float.IsInfinity(v) ? health : Math.Max(1f, v);
                if (result != health && ModLog.VerboseEnabled)
                {
                    var name = ctx.BlockName ?? ctx.BlockGuid.ToString();
                    FtdApi.Log.VerboseOnce("block-health:" + name, () => $"Block '{name}' max health {health:0.#} -> {result:0.#} (item health {itemHealth:0.#}; first change for this block type).");
                }
                return result;
            }
            finally
            {
                ctx.Release();
            }
        }

        private static int _healthRewrites;

        // Block.InitialiseCommon / ProcessPossibleMaximumHealthChange: "CalculateMaximumHealth(team, item.Health)"
        // becomes "CalculateMaximumHealthFor(team, item.Health, this)". The call is turned into "ldarg.0" (so it keeps
        // any labels) and the new call follows it.
        private static IEnumerable<CodeInstruction> MaxHealth_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(Block), nameof(Block.CalculateMaximumHealth));
            var replacement = AccessTools.Method(typeof(BlockApi), nameof(CalculateMaximumHealthFor));
            foreach (var ins in instructions)
            {
                if (ins.opcode == OpCodes.Call && Equals(ins.operand, original))
                {
                    ins.opcode = OpCodes.Ldarg_0;
                    ins.operand = null;
                    _healthRewrites++;
                    yield return ins;
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                    continue;
                }
                yield return ins;
            }
        }

        internal static IEnumerable<PatchSpec> Specs()
        {
            yield return new PatchSpec
            {
                Id = "Block.InitialiseCommon / ProcessPossibleMaximumHealthChange: max health modifiers",
                FeatureId = Features.BlockHealth,
                Targets = () =>
                {
                    _healthRewrites = 0;
                    return new MethodBase[]
                    {
                        PatchSpec.Method(typeof(Block), nameof(Block.InitialiseCommon)),
                        PatchSpec.Method(typeof(Block), nameof(Block.ProcessPossibleMaximumHealthChange), typeof(ObjectId))
                    };
                },
                Transpiler = PatchSpec.Hook(typeof(BlockApi), nameof(MaxHealth_Transpiler)),
                Verify = () => _healthRewrites == 2 ? null : $"expected 2 CalculateMaximumHealth calls, found {_healthRewrites}"
            };

            // Block.GetArmourClass() is virtual, but nothing overrides it in 4.3.4, so one postfix covers every block.
            yield return new PatchSpec
            {
                Id = "Block.GetArmourClass: armour class modifiers",
                FeatureId = Features.ArmourClass,
                Target = () => PatchSpec.Method(typeof(Block), nameof(Block.GetArmourClass)),
                Postfix = PatchSpec.Hook(typeof(BlockApi), nameof(GetArmourClass_Postfix))
            };
        }

        private static void GetArmourClass_Postfix(Block __instance, ref float __result)
        {
            var pipeline = ArmourClass;
            if (pipeline.IsEmpty) return;

            var ctx = ArmourClassContext.Rent(__instance, __result);
            try
            {
                pipeline.Run(ctx);
                var v = ctx.ArmourClass;
                __result = float.IsNaN(v) || float.IsInfinity(v) ? __result : Math.Max(1f, v);
            }
            finally
            {
                ctx.Release();
            }
        }
    }
}

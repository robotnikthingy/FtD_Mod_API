using System;
using BrilliantSkies.Core;
using BrilliantSkies.Core.Id;
using BrilliantSkies.Core.Types;
using BrilliantSkies.Ftd.Avatar.Build;
using BrilliantSkies.Ftd.DamageLogging;
using BrilliantSkies.Ftd.DamageModels;
using BrilliantSkies.Modding.Types;
using FtdModApi.Patching;
using UnityEngine;

namespace FtdModApi
{
    public static partial class Events
    {
        /// <summary>Blocks placed, removed, destroyed and repaired.</summary>
        public static class Blocks
        {
            // ---- hooks -------------------------------------------------------------------------------------

            private static readonly EventHook PlacingHook = new EventHook(Features.EventBlockBuild, () => new[]
            {
                new PatchSpec
                {
                    Id = "mBuild.Server_PlaceBlockOnConstructable: Blocks.Placing",
                    FeatureId = Features.EventBlockBuild,
                    Target = () => EventHelpers.Declared(typeof(mBuild), "Server_PlaceBlockOnConstructable"),
                    Prefix = PatchSpec.Hook(typeof(Blocks), nameof(ServerPlace_Prefix))
                },
                new PatchSpec
                {
                    // Replace = remove the old block, then place the new one: raise Placing before the removal.
                    Id = "mBuild.Server_ReplaceBlock: Blocks.Placing (replace)",
                    FeatureId = Features.EventBlockBuild,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(mBuild), "Server_ReplaceBlock"),
                    Prefix = PatchSpec.Hook(typeof(Blocks), nameof(ServerReplace_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Blocks), nameof(ServerReplace_Finalizer))
                },
                new PatchSpec
                {
                    Id = "mBuild.Server_PlaceSubConstructableOnConstructable: Blocks.Placing (turrets)",
                    FeatureId = Features.EventBlockBuild,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(mBuild), "Server_PlaceSubConstructableOnConstructable"),
                    Prefix = PatchSpec.Hook(typeof(Blocks), nameof(ServerPlaceSub_Prefix))
                }
            });

            private static readonly EventHook PlacedHook = new EventHook(Features.EventBlockBuild, () => new[]
            {
                new PatchSpec
                {
                    Id = "ConstructBasics.AddBlock: Blocks.Placed",
                    FeatureId = Features.EventBlockBuild,
                    Target = () => EventHelpers.Declared(typeof(ConstructBasics), "AddBlock"),
                    Postfix = PatchSpec.Hook(typeof(Blocks), nameof(AddBlock_Postfix))
                }
            });

            private static readonly EventHook RemovingHook = new EventHook(Features.EventBlockBuild, () => new[]
            {
                new PatchSpec
                {
                    Id = "mBuild.Server_RemoveBlock: Blocks.Removing",
                    FeatureId = Features.EventBlockBuild,
                    Target = () => EventHelpers.Declared(typeof(mBuild), "Server_RemoveBlock"),
                    Prefix = PatchSpec.Hook(typeof(Blocks), nameof(ServerRemove_Prefix))
                }
            });

            private static readonly EventHook RemovedHook = new EventHook(Features.EventBlockBuild, () => new[]
            {
                new PatchSpec
                {
                    Id = "ConstructBasics.PermanentlyRemoveBlock: Blocks.Removed",
                    FeatureId = Features.EventBlockBuild,
                    Target = () => EventHelpers.Declared(typeof(ConstructBasics), "PermanentlyRemoveBlock"),
                    Prefix = PatchSpec.Hook(typeof(Blocks), nameof(PermanentlyRemove_Prefix))
                }
            });

            private static readonly EventHook DestroyedHook = new EventHook(Features.EventBlockDestroyed, () => new[]
            {
                new PatchSpec
                {
                    Id = "Block.MainThreadHealthIsZeroSoKill: Blocks.Destroyed",
                    FeatureId = Features.EventBlockDestroyed,
                    Target = () => EventHelpers.Declared(typeof(Block), "MainThreadHealthIsZeroSoKill"),
                    Prefix = PatchSpec.Hook(typeof(Blocks), nameof(Kill_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Blocks), nameof(Kill_Postfix))
                }
            });

            private static readonly EventHook RepairedHook = new EventHook(Features.EventBlockRepaired, () => new[]
            {
                new PatchSpec
                {
                    // Virtual; MainConstruct's override calls this base method, so one patch covers every construct.
                    Id = "AllConstruct.BlockRepairedSoPerformAllActions: Blocks.Repaired",
                    FeatureId = Features.EventBlockRepaired,
                    Target = () => EventHelpers.Declared(typeof(AllConstruct), "BlockRepairedSoPerformAllActions"),
                    Postfix = PatchSpec.Hook(typeof(Blocks), nameof(Repaired_Postfix))
                }
            });

            // ---- channels ----------------------------------------------------------------------------------

            /// <summary>
            /// A player (or a game action on their behalf, like mirror, fill or replace) is about to place a block, turret or
            /// spin block. Raised before the game's own checks, so the game can still refuse it (doesn't fit, can't afford).
            /// Cancellable. Host.
            /// </summary>
            public static EventChannel<BlockPlacingEvent> Placing { get; } = new EventChannel<BlockPlacingEvent>(
                "Blocks.Placing", EventSide.Host, "A player is about to place a block.", PlacingHook);

            /// <summary>
            /// A new block was placed on a craft (player building, prefab, tutorial). Not raised for blocks created while a craft
            /// spawns from its blueprint: use <see cref="Constructs.Spawned"/> for those. All machines.
            /// </summary>
            public static EventChannel<BlockPlacedEvent> Placed { get; } = new EventChannel<BlockPlacedEvent>(
                "Blocks.Placed", EventSide.All, "A new block was placed.", PlacedHook);

            /// <summary>A player is about to remove a block (or a whole turret). Cancellable. Host.</summary>
            public static EventChannel<BlockRemovingEvent> Removing { get; } = new EventChannel<BlockRemovingEvent>(
                "Blocks.Removing", EventSide.Host, "A player is about to remove a block.", RemovingHook);

            /// <summary>
            /// A block is being removed from a craft's design: by a player, by separation into debris, or by other game code.
            /// Raised just before, while the block can still be read. All machines.
            /// </summary>
            public static EventChannel<BlockRemovedEvent> Removed { get; } = new EventChannel<BlockRemovedEvent>(
                "Blocks.Removed", EventSide.All, "A block is about to be removed from the design.", RemovedHook);

            /// <summary>A block's health reached zero from damage and it was destroyed (it stays in the design for repair). Host.</summary>
            public static EventChannel<BlockDestroyedEvent> Destroyed { get; } = new EventChannel<BlockDestroyedEvent>(
                "Blocks.Destroyed", EventSide.Host, "A block was destroyed by damage.", DestroyedHook);

            /// <summary>A destroyed block was repaired back to life (repair bots, tentacles, the repair tool, instant repair). All machines.</summary>
            public static EventChannel<BlockRepairedEvent> Repaired { get; } = new EventChannel<BlockRepairedEvent>(
                "Blocks.Repaired", EventSide.All, "A destroyed block was repaired.", RepairedHook);

            // ---- patch bodies ------------------------------------------------------------------------------

            [ThreadStatic] private static int _replaceDepth;

            private static bool ServerReplace_Prefix(AllConstruct C, Vector3i LP, Quaternion LR, Guid itemGuid, ObjectId team, ref bool __result)
            {
                _replaceDepth++;
                if (C == null || !Placing.HasSubscribers) return true;
                var e = new BlockPlacingEvent(C, itemGuid, LP, LR, team, PlaceOptions.None, PlacingKind.Replace);
                Placing.Raise(e);
                if (!e.IsCancelled) return true;
                __result = false;
                return false;
            }

            // Runs whether or not the original ran or threw.
            private static void ServerReplace_Finalizer()
            {
                if (_replaceDepth > 0) _replaceDepth--;
            }

            private static bool ServerPlaceSub_Prefix(AllConstruct C, Guid itemGuid, Vector3i localPos, Quaternion localRotation, ObjectId team, ref SubConstruct __result)
            {
                if (C == null || !Placing.HasSubscribers) return true;
                var e = new BlockPlacingEvent(C, itemGuid, localPos, localRotation, team, PlaceOptions.None, PlacingKind.Subconstruct);
                Placing.Raise(e);
                if (!e.IsCancelled) return true;
                __result = null;
                return false;
            }

            private static bool ServerPlace_Prefix(AllConstruct C, Guid itemGuid, Vector3i localPos, Quaternion localRotation, ObjectId team, PlaceOptions placeOptions, ref bool __result)
            {
                // Inside a replace, Placing was already raised by the replace prefix.
                if (C == null || _replaceDepth > 0 || !Placing.HasSubscribers) return true;
                var e = new BlockPlacingEvent(C, itemGuid, localPos, localRotation, team, placeOptions, PlacingKind.Block);
                Placing.Raise(e);
                if (!e.IsCancelled) return true;
                __result = false;
                return false;
            }

            private static void AddBlock_Postfix(Block B, bool isPlacedAsAPrefab, bool __result)
            {
                if (!__result || B == null || !Placed.HasSubscribers) return;
                Placed.Raise(new BlockPlacedEvent(B, EventHelpers.ConstructOf(B), isPlacedAsAPrefab));
            }

            private static bool ServerRemove_Prefix(AllConstruct C, Vector3 localPosition, ObjectId team, IDamageLogger gunner, ref bool __result)
            {
                if (C == null || !Removing.HasSubscribers) return true;
                var position = (Vector3i)localPosition;
                Block block = null;
                SubConstruct sub = null;
                try
                {
                    block = C.AllBasicsRestricted.GetBlockViaLocalPosition(position);
                    sub = C.AllBasicsRestricted.GetSubConstructableViaLocalPosition(position);
                    if (block != null && block.item != null && block.item.ItemType == enumItemType.subconstructableActiveBlock && block.SubConstruct != null)
                        sub = block.SubConstruct as SubConstruct;
                }
                catch (Exception ex)
                {
                    FtdApi.Log.VerboseOnce("blocks-removing-lookup", () => "Blocks.Removing could not look up the block: " + ex.Message);
                }
                if (block == null && sub == null) return true; // nothing there; the game does nothing either

                var e = new BlockRemovingEvent(C, block, sub, position, team);
                Removing.Raise(e);
                if (!e.IsCancelled) return true;
                __result = false;
                return false;
            }

            private static void PermanentlyRemove_Prefix(Block B)
            {
                if (B == null || !Removed.HasSubscribers) return;
                Removed.Raise(new BlockRemovedEvent(B, EventHelpers.ConstructOf(B), B.IsAlive));
            }

            // __state: the block's construct before it dies (dying can split the block off into debris).
            private static void Kill_Prefix(Block __instance, out AllConstruct __state)
            {
                __state = Destroyed.HasSubscribers ? EventHelpers.ConstructOf(__instance) : null;
            }

            private static void Kill_Postfix(Block __instance, IDamageDescription DD, AllConstruct __state)
            {
                if (__instance == null || !Destroyed.HasSubscribers) return;
                Destroyed.Raise(new BlockDestroyedEvent(__instance, __state ?? EventHelpers.ConstructOf(__instance), DD));
            }

            private static void Repaired_Postfix(AllConstruct __instance, Block B)
            {
                if (B == null || !Repaired.HasSubscribers) return;
                Repaired.Raise(new BlockRepairedEvent(B, __instance));
            }
        }
    }

    /// <summary>Common properties of events about one block.</summary>
    public abstract class BlockEventBase : GameEvent
    {
        protected BlockEventBase(Block block, AllConstruct construct)
        {
            Block = block;
            Construct = construct;
        }

        /// <summary>The block. Game object: its members can change between game versions.</summary>
        public Block Block { get; }

        /// <summary>The construct the block is on: the craft, or a turret/spin block subconstruct.</summary>
        public AllConstruct Construct { get; }

        /// <summary>The craft (the main construct), even when the block is on a turret.</summary>
        public MainConstruct MainConstruct => EventHelpers.MainOf(Construct);

        public ItemDefinition Item => Block?.item;

        /// <summary>The block type's name, e.g. "Metal Block".</summary>
        public string ItemName => EventHelpers.ItemName(Item);

        public Guid ItemGuid => Item?.ComponentId.Guid ?? Guid.Empty;

        /// <summary>Position on <see cref="Construct"/>, in blocks.</summary>
        public Vector3i LocalPosition => Block != null ? Block.LocalPosition : default;
    }

    public sealed class BlockPlacingEvent : CancellableEvent
    {
        private ItemDefinition _item;
        private bool _itemLooked;

        internal BlockPlacingEvent(AllConstruct construct, Guid itemGuid, Vector3i localPosition, Quaternion localRotation, ObjectId team, PlaceOptions options, PlacingKind kind)
        {
            Kind = kind;
            Construct = construct;
            ItemGuid = itemGuid;
            LocalPosition = localPosition;
            LocalRotation = localRotation;
            Team = team;
            Options = options;
        }

        /// <summary>The construct being built on: the craft, or a turret/spin block subconstruct.</summary>
        public AllConstruct Construct { get; }

        public MainConstruct MainConstruct => EventHelpers.MainOf(Construct);

        public Guid ItemGuid { get; }

        /// <summary>The block type being placed (looked up on first use).</summary>
        public ItemDefinition Item
        {
            get
            {
                if (!_itemLooked)
                {
                    _item = EventHelpers.FindItem(ItemGuid);
                    _itemLooked = true;
                }
                return _item;
            }
        }

        public string ItemName => EventHelpers.ItemName(Item);

        public Vector3i LocalPosition { get; }

        public Quaternion LocalRotation { get; }

        /// <summary>The team of the player placing it.</summary>
        public ObjectId Team { get; }

        /// <summary>The game's placement flags.</summary>
        public PlaceOptions Options { get; }

        /// <summary>True when the block is part of a prefab being placed.</summary>
        public bool IsPrefab => (Options & PlaceOptions.UsePrefabForBlockInitialise) != 0;

        /// <summary>A normal block, a replacement of an existing block, or a turret/spin block.</summary>
        public PlacingKind Kind { get; }
    }

    /// <summary>What a <see cref="BlockPlacingEvent"/> is placing.</summary>
    public enum PlacingKind
    {
        /// <summary>A normal block.</summary>
        Block,

        /// <summary>Replacing the block already at the position (the old one is removed first if not cancelled).</summary>
        Replace,

        /// <summary>A turret, spin block or other block that creates a subconstruct.</summary>
        Subconstruct
    }

    public sealed class BlockPlacedEvent : BlockEventBase
    {
        internal BlockPlacedEvent(Block block, AllConstruct construct, bool isPrefab) : base(block, construct)
        {
            IsPrefab = isPrefab;
        }

        /// <summary>True when the block came from a prefab.</summary>
        public bool IsPrefab { get; }
    }

    public sealed class BlockRemovingEvent : CancellableEvent
    {
        internal BlockRemovingEvent(AllConstruct construct, Block block, SubConstruct subconstruct, Vector3i localPosition, ObjectId team)
        {
            Construct = construct;
            Block = block;
            Subconstruct = subconstruct;
            LocalPosition = localPosition;
            Team = team;
        }

        public AllConstruct Construct { get; }

        public MainConstruct MainConstruct => EventHelpers.MainOf(Construct);

        /// <summary>The block at the position (null when only a subconstruct is there).</summary>
        public Block Block { get; }

        /// <summary>Set when the removal takes away a whole turret or spin block with everything on it.</summary>
        public SubConstruct Subconstruct { get; }

        public bool RemovesSubconstruct => Subconstruct != null;

        public ItemDefinition Item => Block?.item;

        public string ItemName => EventHelpers.ItemName(Item);

        public Vector3i LocalPosition { get; }

        public ObjectId Team { get; }
    }

    public sealed class BlockRemovedEvent : BlockEventBase
    {
        internal BlockRemovedEvent(Block block, AllConstruct construct, bool wasAlive) : base(block, construct)
        {
            WasAlive = wasAlive;
        }

        /// <summary>False when the block was already destroyed (a dead block being cleared from the design).</summary>
        public bool WasAlive { get; }
    }

    public sealed class BlockDestroyedEvent : BlockEventBase
    {
        internal BlockDestroyedEvent(Block block, AllConstruct construct, IDamageDescription damage) : base(block, construct)
        {
            Damage = damage;
        }

        /// <summary>The damage that destroyed it.</summary>
        public IDamageDescription Damage { get; }

        /// <summary>Who did it (for kill credit). May be null.</summary>
        public IDamageLogger Gunner => Damage?.DamageLogger;
    }

    public sealed class BlockRepairedEvent : BlockEventBase
    {
        internal BlockRepairedEvent(Block block, AllConstruct construct) : base(block, construct)
        {
        }
    }
}

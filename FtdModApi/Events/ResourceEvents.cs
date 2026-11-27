using System;
using System.Collections.Generic;
using BrilliantSkies.Core;
using BrilliantSkies.Core.Id;
using BrilliantSkies.Core.Timing;
using BrilliantSkies.Core.Types;
using BrilliantSkies.FromTheDepths.Planets;
using BrilliantSkies.Ftd.Planets.Instances;
using BrilliantSkies.Ftd.Planets.Instances.Resources;
using BrilliantSkies.Ftd.Rz.Transfer;
using BrilliantSkies.Ftd.Rz.Transfer.SupplyGroups;
using BrilliantSkies.ResourceGatherer;
using FtdModApi.Patching;

namespace FtdModApi
{
    public static partial class Events
    {
        /// <summary>
        /// Materials and energy moving around: salvage and dump pickups, resource dumps, harvesting in resource zones,
        /// and supply-group transfers between craft.
        /// </summary>
        public static class Resources
        {
            // ---- hooks -------------------------------------------------------------------------------------

            private static readonly EventHook GainHook = new EventHook(Features.EventResources, () => new[]
            {
                new PatchSpec
                {
                    // Every salvage reward (destroying enemy blocks) and every dump pickup ends here.
                    Id = "ResourceStoreAbstract.GiveResourceAndExp: Resources.Gaining/Gained",
                    FeatureId = Features.EventResources,
                    Target = () => PatchSpec.Method(typeof(ResourceStoreAbstract), "GiveResourceAndExp",
                        typeof(float), typeof(float), typeof(ObjectId), typeof(Vector3d)),
                    Prefix = PatchSpec.Hook(typeof(Resources), nameof(Give_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Resources), nameof(Give_Postfix))
                },
                new PatchSpec
                {
                    // Tags gains made by the once-a-second dump pickup, so they can be told apart from salvage.
                    Id = "DumpPickupManager.PickupDumpedResources: Resources.Gaining source",
                    FeatureId = Features.EventResources,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(DumpPickupManager), "PickupDumpedResources"),
                    Prefix = PatchSpec.Hook(typeof(Resources), nameof(Pickup_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Resources), nameof(Pickup_Finalizer))
                }
            });

            private static readonly EventHook DumpHook = new EventHook(Features.EventResources, () => new[]
            {
                new PatchSpec
                {
                    Id = "ResourceDumpSync.CreateNewDump: Resources.Dumping",
                    FeatureId = Features.EventResources,
                    Target = () => EventHelpers.Declared(typeof(ResourceDumpSync), "CreateNewDump"),
                    Prefix = PatchSpec.Hook(typeof(Resources), nameof(CreateNewDump_Prefix))
                },
                new PatchSpec
                {
                    Id = "ResourceDumpSync._CreateNewDump: Resources.Dumped",
                    FeatureId = Features.EventResources,
                    Target = () => EventHelpers.Declared(typeof(ResourceDumpSync), "_CreateNewDump"),
                    Prefix = PatchSpec.Hook(typeof(Resources), nameof(CreateDumpLocal_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Resources), nameof(CreateDumpLocal_Postfix))
                }
            });

            private static readonly EventHook HarvestHook = new EventHook(Features.EventResources, () => new[]
            {
                new PatchSpec
                {
                    Id = "ResourceGathererAbstract.Run: Resources.Harvested (which gatherer)",
                    FeatureId = Features.EventResources,
                    Target = () => EventHelpers.Declared(typeof(ResourceGathererAbstract), "Run"),
                    Prefix = PatchSpec.Hook(typeof(Resources), nameof(GathererRun_Prefix)),
                    Finalizer = PatchSpec.Hook(typeof(Resources), nameof(GathererRun_Finalizer))
                },
                new PatchSpec
                {
                    Id = "MapResourceZone.HowMuchResourceCanITake: Resources.Harvested (amount)",
                    FeatureId = Features.EventResources,
                    Target = () => EventHelpers.Declared(typeof(MapResourceZone), "HowMuchResourceCanITake"),
                    Postfix = PatchSpec.Hook(typeof(Resources), nameof(ZoneTake_Postfix))
                }
            }, () =>
            {
                if (GameEvents.Once_Second == null) return "GameEvents.Once_Second is missing";
                GameEvents.Once_Second.RegWithEvent(FlushHarvest);
                return null;
            });

            private static readonly EventHook TransferHook = new EventHook(Features.EventResources, () => new[]
            {
                new PatchSpec
                {
                    Id = "SupplyGroupProcessingFtdSingleton.MakeEvents: Resources.Transferred",
                    FeatureId = Features.EventResources,
                    Target = () => EventHelpers.Declared(typeof(SupplyGroupProcessingFtdSingleton), "MakeEvents"),
                    Postfix = PatchSpec.Hook(typeof(Resources), nameof(MakeEvents_Postfix))
                }
            });

            // ---- channels ----------------------------------------------------------------------------------

            /// <summary>
            /// A craft or faction store is about to receive materials (and energy): salvage from destroying blocks, or a
            /// resource dump being picked up. <c>Material</c> and <c>Energy</c> can be changed; cancelling gives nothing.
            /// For a dump pickup the dump has already been emptied by then, so cancelling or lowering the amount destroys
            /// those resources. Amounts are before the campaign's salvage multiplier. Host; can run on a worker thread.
            /// </summary>
            public static EventChannel<ResourcesGainingEvent> Gaining { get; } = new EventChannel<ResourcesGainingEvent>(
                "Resources.Gaining", EventSide.Host, EventThread.Any, "Salvage or a dump pickup is about to be given.", GainHook);

            /// <summary>
            /// A craft or faction store received materials (and energy) from salvage or a dump pickup: the amounts actually
            /// added after the multiplier and storage limits (worked out from the store's stock, so approximate if another
            /// thread changes the same store at that moment). Host.
            /// </summary>
            public static EventChannel<ResourcesGainedEvent> Gained { get; } = new EventChannel<ResourcesGainedEvent>(
                "Resources.Gained", EventSide.Host, "Salvage or a dump pickup was given.", GainHook);

            /// <summary>
            /// Materials are about to be left in the world as a resource dump (a craft scrapped or killed, overflow from
            /// salvage, an AI dump order, refunds). <c>Material</c> and <c>Energy</c> can be changed; cancelling makes no
            /// dump, so the resources are lost. Host; can run on a worker thread.
            /// </summary>
            public static EventChannel<DumpingEvent> Dumping { get; } = new EventChannel<DumpingEvent>(
                "Resources.Dumping", EventSide.Host, EventThread.Any, "A resource dump is about to be created.", DumpHook);

            /// <summary>
            /// Materials were added to a resource dump: a new one, or an existing dump of the same team within 100 m. All
            /// machines.
            /// </summary>
            public static EventChannel<DumpedEvent> Dumped { get; } = new EventChannel<DumpedEvent>(
                "Resources.Dumped", EventSide.All, "Materials were left in a resource dump.", DumpHook);

            /// <summary>
            /// Materials a craft's gatherers took from a resource zone, added up over about a second (one event per craft per
            /// second while it harvests). Only craft in play: out-of-play fleets harvest through the campaign simulation and are
            /// not reported. Host.
            /// </summary>
            public static EventChannel<HarvestedEvent> Harvested { get; } = new EventChannel<HarvestedEvent>(
                "Resources.Harvested", EventSide.Host, "A craft harvested materials (per second).", HarvestHook);

            /// <summary>
            /// Supply groups moved materials and energy between craft and stores (about once a second). One event per run with
            /// every member whose stock changed. Host.
            /// </summary>
            public static EventChannel<TransferredEvent> Transferred { get; } = new EventChannel<TransferredEvent>(
                "Resources.Transferred", EventSide.Host, "Supply groups moved resources between craft.", TransferHook);

            // ---- gaining -----------------------------------------------------------------------------------

            [ThreadStatic] private static int _pickupDepth;

            private static void Pickup_Prefix() => _pickupDepth++;

            private static void Pickup_Finalizer()
            {
                if (_pickupDepth > 0) _pickupDepth--;
            }

            private struct GainState
            {
                public bool Active;
                public double Material;
                public double Energy;
                public ResourceGainSource Source;
            }

            private static bool Give_Prefix(ResourceStoreAbstract __instance, ref float material, ref float energy,
                ObjectId teamOfDestroyedEntity, Vector3d universePositionOfDamage, out GainState __state)
            {
                __state = default;
                if (__instance == null || Net.IsClient) return true;
                if (!Gaining.HasSubscribers && !Gained.HasSubscribers) return true;
                // A faction store gets its own team's salvage as a resource dump instead (raised as Dumping/Dumped).
                if (__instance.IsCommodityStore && teamOfDestroyedEntity == __instance.TeamId) return true;
                var source = _pickupDepth > 0 ? ResourceGainSource.DumpPickup : ResourceGainSource.Salvage;
                if (Gaining.HasSubscribers)
                {
                    var e = new ResourcesGainingEvent(__instance, material, energy, teamOfDestroyedEntity, universePositionOfDamage, source);
                    Gaining.Raise(e);
                    if (e.IsCancelled) return false;
                    material = Math.Max(0f, e.Material);
                    energy = Math.Max(0f, e.Energy);
                }
                if (Gained.HasSubscribers)
                {
                    __state.Active = true;
                    __state.Source = source;
                    __state.Material = __instance.iMaterial?.Quantity ?? 0;
                    __state.Energy = __instance.iEnergy?.Quantity ?? 0;
                }
                return true;
            }

            private static void Give_Postfix(ResourceStoreAbstract __instance, ObjectId teamOfDestroyedEntity, Vector3d universePositionOfDamage, GainState __state)
            {
                if (!__state.Active) return;
                double m = (__instance.iMaterial?.Quantity ?? 0) - __state.Material;
                double en = (__instance.iEnergy?.Quantity ?? 0) - __state.Energy;
                if (m < 0.0001 && en < 0.0001) return; // nothing added (full, or turned into a dump)
                Gained.RaiseOnMainThread(new ResourcesGainedEvent(__instance, (float)Math.Max(0, m), (float)Math.Max(0, en),
                    teamOfDestroyedEntity, universePositionOfDamage, __state.Source));
            }

            // ---- dumps -------------------------------------------------------------------------------------

            private static bool CreateNewDump_Prefix(Vector3d universePosition, ObjectId owner, ref float material, ref float energy)
            {
                if (Net.IsClient || !Dumping.HasSubscribers) return true;
                if (material <= 0f && energy <= 0f) return true; // the game rejects these itself
                var e = new DumpingEvent(universePosition, owner, material, energy);
                Dumping.Raise(e);
                if (e.IsCancelled) return false;
                material = Math.Max(0f, e.Material);
                energy = Math.Max(0f, e.Energy);
                // The game logs an error for an empty dump; skip it quietly if a handler set both to zero.
                return material > 0f || energy > 0f;
            }

            private static void CreateDumpLocal_Prefix(out int __state)
            {
                __state = -1;
                if (!Dumped.HasSubscribers) return;
                try
                {
                    __state = InstanceSpecification.i?.ResourceZones?.DumpCount ?? -1;
                }
                catch
                {
                    __state = -1;
                }
            }

            private static void CreateDumpLocal_Postfix(Vector3d universePosition, ObjectId owner, float material, float energy, WorldResourceDump __result, int __state)
            {
                if (__result == null || !Dumped.HasSubscribers) return;
                bool isNew = false;
                try
                {
                    isNew = __state >= 0 && (InstanceSpecification.i?.ResourceZones?.DumpCount ?? 0) > __state;
                }
                catch
                {
                }
                Dumped.RaiseOnMainThread(new DumpedEvent(__result, universePosition, owner, material, energy, isNew));
            }

            // ---- harvesting --------------------------------------------------------------------------------

            [ThreadStatic] private static ResourceGathererAbstract _gatherer;

            private static readonly object _harvestGate = new object();
            private static Dictionary<MainConstruct, HarvestTally> _harvest = new Dictionary<MainConstruct, HarvestTally>();
            private static DateTime _lastFlush = DateTime.UtcNow;

            private sealed class HarvestTally
            {
                public float Material;
                public MapResourceZone Zone;
            }

            private static void GathererRun_Prefix(ResourceGathererAbstract __instance)
            {
                _gatherer = Harvested.HasSubscribers && !Net.IsClient ? __instance : null;
            }

            private static void GathererRun_Finalizer()
            {
                _gatherer = null;
            }

            private static void ZoneTake_Postfix(MapResourceZone __instance, float __result)
            {
                var g = _gatherer;
                if (g is null || __result <= 0f) return;
                var c = g.MainConstruct as MainConstruct;
                if (c is null) return;
                lock (_harvestGate)
                {
                    if (!_harvest.TryGetValue(c, out var t))
                    {
                        t = new HarvestTally();
                        _harvest[c] = t;
                    }
                    t.Material += __result;
                    t.Zone = __instance;
                }
            }

            private static void FlushHarvest(ITimeStep step)
            {
                Dictionary<MainConstruct, HarvestTally> batch;
                lock (_harvestGate)
                {
                    if (_harvest.Count == 0)
                    {
                        _lastFlush = DateTime.UtcNow;
                        return;
                    }
                    batch = _harvest;
                    _harvest = new Dictionary<MainConstruct, HarvestTally>();
                }
                float seconds = step?.DeltaTime ?? 1f;
                if (seconds <= 0f) seconds = (float)(DateTime.UtcNow - _lastFlush).TotalSeconds;
                _lastFlush = DateTime.UtcNow;
                if (!Harvested.HasSubscribers) return;
                foreach (var kv in batch)
                {
                    if (kv.Value.Material <= 0f || kv.Key == null || kv.Key.Destroyed) continue; // pulled out of play or destroyed since
                    Harvested.Raise(new HarvestedEvent(kv.Key, kv.Value.Material, seconds, kv.Value.Zone));
                }
            }

            // ---- transfers ---------------------------------------------------------------------------------

            private static void MakeEvents_Postfix(SupplyGroupProcessingFtdSingleton __instance)
            {
                if (!Transferred.HasSubscribers) return;
                List<SupplyTransfer> changes = null;
                try
                {
                    foreach (var group in __instance.SupplyGroups.Groups)
                    {
                        foreach (var member in group.SupplyForces)
                        {
                            if (Math.Abs(member.MaterialChange) < 0.001 && Math.Abs(member.EnergyChange) < 0.001) continue;
                            (changes ?? (changes = new List<SupplyTransfer>())).Add(
                                new SupplyTransfer(member.Force as Force, group.Team, member.MaterialChange, member.EnergyChange));
                        }
                    }
                }
                catch (Exception e)
                {
                    FtdApi.Log.Verbose(() => "Resources.Transferred skipped a run: " + e.Message);
                    return;
                }
                if (changes == null) return;
                Transferred.RaiseOnMainThread(new TransferredEvent(changes));
            }
        }
    }

    /// <summary>Where gained resources came from.</summary>
    public enum ResourceGainSource
    {
        /// <summary>Destroying blocks of a craft (salvage reward to the craft or faction that did it).</summary>
        Salvage,
        /// <summary>A craft picking up a resource dump.</summary>
        DumpPickup
    }

    /// <summary>Resources about to be given to a store. Change Material/Energy, or cancel.</summary>
    public sealed class ResourcesGainingEvent : CancellableEvent
    {
        internal ResourcesGainingEvent(IResourceStore store, float material, float energy, ObjectId sourceTeam, Vector3d position, ResourceGainSource source)
        {
            Store = store;
            Material = material;
            Energy = energy;
            SourceTeam = sourceTeam;
            Position = position;
            Source = source;
        }

        /// <summary>The store receiving: a craft's store or a faction's commodity store. Game object.</summary>
        public IResourceStore Store { get; }

        /// <summary>The team of the store receiving.</summary>
        public ObjectId Team => Store != null ? Store.TeamId : ObjectId.NoLinkage;

        /// <summary>The craft whose store this is, or null for a faction store.</summary>
        public Force Force => EventHelpers.ForceOfStore(Store);

        /// <summary>Materials, before the campaign's salvage multiplier.</summary>
        public float Material { get; set; }

        /// <summary>Energy (usually 0).</summary>
        public float Energy { get; set; }

        /// <summary>The team the resources came from (the destroyed craft's team, or the dump's).</summary>
        public ObjectId SourceTeam { get; }

        /// <summary>True when the resources came from the receiver's own team (no salvage multiplier).</summary>
        public bool IsOwnTeam => SourceTeam == Team;

        /// <summary>Where the block was destroyed or the dump lies (universe coordinates).</summary>
        public Vector3d Position { get; }

        public ResourceGainSource Source { get; }
    }

    /// <summary>Resources that were given to a store: the amounts actually added.</summary>
    public sealed class ResourcesGainedEvent : GameEvent
    {
        internal ResourcesGainedEvent(IResourceStore store, float material, float energy, ObjectId sourceTeam, Vector3d position, ResourceGainSource source)
        {
            Store = store;
            Material = material;
            Energy = energy;
            SourceTeam = sourceTeam;
            Position = position;
            Source = source;
        }

        /// <summary>The store that received: a craft's store or a faction's commodity store. Game object.</summary>
        public IResourceStore Store { get; }

        public ObjectId Team => Store != null ? Store.TeamId : ObjectId.NoLinkage;

        /// <summary>The craft whose store this is, or null for a faction store.</summary>
        public Force Force => EventHelpers.ForceOfStore(Store);

        /// <summary>Materials added (after the multiplier and the storage limit).</summary>
        public float Material { get; }

        /// <summary>Energy added.</summary>
        public float Energy { get; }

        public ObjectId SourceTeam { get; }

        public bool IsOwnTeam => SourceTeam == Team;

        public Vector3d Position { get; }

        public ResourceGainSource Source { get; }
    }

    /// <summary>A resource dump is about to be created. Change Material/Energy, or cancel.</summary>
    public sealed class DumpingEvent : CancellableEvent
    {
        internal DumpingEvent(Vector3d position, ObjectId owner, float material, float energy)
        {
            Position = position;
            Owner = owner;
            Material = material;
            Energy = energy;
        }

        /// <summary>Where (universe coordinates).</summary>
        public Vector3d Position { get; }

        /// <summary>The team the dump belongs to (its own team picks it up without the salvage multiplier).</summary>
        public ObjectId Owner { get; }

        public float Material { get; set; }

        public float Energy { get; set; }
    }

    /// <summary>Materials were added to a resource dump.</summary>
    public sealed class DumpedEvent : GameEvent
    {
        internal DumpedEvent(WorldResourceDump dump, Vector3d position, ObjectId owner, float material, float energy, bool isNew)
        {
            Dump = dump;
            Position = position;
            Owner = owner;
            Material = material;
            Energy = energy;
            IsNew = isNew;
        }

        /// <summary>The dump. Game object.</summary>
        public WorldResourceDump Dump { get; }

        public Vector3d Position { get; }

        public ObjectId Owner { get; }

        /// <summary>Materials added.</summary>
        public float Material { get; }

        public float Energy { get; }

        /// <summary>True when a new dump was made, false when the materials joined a nearby dump of the same team.</summary>
        public bool IsNew { get; }
    }

    /// <summary>Materials a craft harvested over the last second or so.</summary>
    public sealed class HarvestedEvent : GameEvent
    {
        internal HarvestedEvent(MainConstruct construct, float material, float seconds, MapResourceZone zone)
        {
            Construct = construct;
            Material = material;
            Seconds = seconds;
            Zone = zone;
        }

        /// <summary>The craft. Game object.</summary>
        public MainConstruct Construct { get; }

        public string Name
        {
            get
            {
                try
                {
                    return Construct?.GetName();
                }
                catch
                {
                    return null;
                }
            }
        }

        public ObjectId Team
        {
            get
            {
                try
                {
                    return Construct != null ? Construct.GetTeam() : ObjectId.NoLinkage;
                }
                catch
                {
                    return ObjectId.NoLinkage;
                }
            }
        }

        /// <summary>Materials taken from the zone in this period.</summary>
        public float Material { get; }

        /// <summary>Length of the period in game seconds (about 1).</summary>
        public float Seconds { get; }

        /// <summary>Materials per second.</summary>
        public float PerSecond => Seconds > 0f ? Material / Seconds : Material;

        /// <summary>The resource zone harvested. Game object.</summary>
        public MapResourceZone Zone { get; }

        public string ZoneName
        {
            get
            {
                try
                {
                    return Zone?.Name?.ToString();
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    /// <summary>One supply-group member's change in a transfer run.</summary>
    public sealed class SupplyTransfer
    {
        internal SupplyTransfer(Force force, ObjectId team, double material, double energy)
        {
            Force = force;
            Team = team;
            Material = material;
            Energy = energy;
        }

        /// <summary>The craft (or other supply member; null if it is not a craft). Game object.</summary>
        public Force Force { get; }

        public string Name => Force?.Name;

        public ObjectId Team { get; }

        /// <summary>Materials received (positive) or sent (negative).</summary>
        public double Material { get; }

        /// <summary>Energy received (positive) or sent (negative).</summary>
        public double Energy { get; }
    }

    /// <summary>A supply-group transfer run: every member whose stock changed.</summary>
    public sealed class TransferredEvent : GameEvent
    {
        internal TransferredEvent(IReadOnlyList<SupplyTransfer> changes)
        {
            Changes = changes;
        }

        public IReadOnlyList<SupplyTransfer> Changes { get; }
    }
}

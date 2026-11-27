using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BrilliantSkies.Core;
using BrilliantSkies.Ftd.Constructs.Modules.Main.Detection;
using BrilliantSkies.Ftd.Constructs.Modules.Main.Detection.Tracking;
using BrilliantSkies.Ftd.Game.Pools;
using FtdModApi.Patching;
using HarmonyLib;
using UnityEngine;

namespace FtdModApi
{
    public static partial class Events
    {
        /// <summary>
        /// A craft's sensors: first contact with another craft (needs an AI mainframe, which runs the sensors), and
        /// incoming missiles and shells (missile warner blocks work without a mainframe; other detection blocks need one).
        /// </summary>
        public static class Detection
        {
            // ---- hooks -------------------------------------------------------------------------------------

            private static AccessTools.FieldRef<MainConstructDetection, ConcurrentDictionary<ITrackable, TargetTrackCollectionsAndDetections>> _tracks;

            private static readonly EventHook ContactHook = new EventHook(Features.EventDetection, () => new[]
            {
                new PatchSpec
                {
                    Id = "DetectionParametersAndMaths.ProvideADetectionOnThisConstruct: Detection.ContactMade",
                    FeatureId = Features.EventDetection,
                    Target = () =>
                    {
                        _tracks = FieldRefs.Get<MainConstructDetection, ConcurrentDictionary<ITrackable, TargetTrackCollectionsAndDetections>>("_constructDictionary");
                        return EventHelpers.Declared(typeof(DetectionParametersAndMaths), "ProvideADetectionOnThisConstruct");
                    },
                    Prefix = PatchSpec.Hook(typeof(Detection), nameof(ProvideDetection_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Detection), nameof(ProvideDetection_Postfix))
                }
            });

            private static readonly EventHook WarningHook = new EventHook(Features.EventDetection, () => new[]
            {
                new PatchSpec
                {
                    Id = "MissileWarningManager.MissileDetected: Detection.IncomingProjectile",
                    FeatureId = Features.EventDetection,
                    Target = () => EventHelpers.Declared(typeof(MissileWarningManager), "MissileDetected"),
                    Postfix = PatchSpec.Hook(typeof(Detection), nameof(MissileDetected_Postfix))
                }
            });

            // ---- channels ----------------------------------------------------------------------------------

            /// <summary>
            /// A craft's sensors detected another craft for the first time (this craft had no track on it yet). Raised once
            /// per pair of craft until the target is destroyed. Includes friendly craft its sensors pick up. If a game screen
            /// (fleet or sensor UI) created the track first, no event is raised for that pair. Host.
            /// </summary>
            public static EventChannel<ContactEvent> ContactMade { get; } = new EventChannel<ContactEvent>(
                "Detection.ContactMade", EventSide.Host, "A craft first detected another craft.", ContactHook);

            /// <summary>
            /// A craft's missile warners or detection blocks spotted an incoming missile, CRAM shell or APS shell (the first
            /// sighting; the same projectile is reported again only if it is spotted afresh at least 5 seconds after the last
            /// report). Host.
            /// </summary>
            public static EventChannel<IncomingProjectileEvent> IncomingProjectile { get; } = new EventChannel<IncomingProjectileEvent>(
                "Detection.IncomingProjectile", EventSide.Host, "A craft spotted an incoming projectile.", WarningHook);

            // ---- contacts (detection thread) ---------------------------------------------------------------

            private static void ProvideDetection_Prefix(DetectionParametersAndMaths __instance, IMainConstructBlock Us, IMainConstructBlock T, out bool __state)
            {
                __state = true;
                if (_tracks == null || Net.IsClient || !ContactMade.HasSubscribers) return;
                try
                {
                    var detections = Us?.DetectionsRestricted as MainConstructDetection;
                    var target = T as ITrackable;
                    if (detections == null || target == null) return;
                    var dict = _tracks(detections);
                    __state = dict == null || dict.ContainsKey(target);
                }
                catch
                {
                    __state = true;
                }
            }

            private static void ProvideDetection_Postfix(DetectionParametersAndMaths __instance, IMainConstructBlock Us, IMainConstructBlock T, bool __state)
            {
                if (__state) return;
                try
                {
                    var detections = Us?.DetectionsRestricted as MainConstructDetection;
                    var target = T as ITrackable;
                    if (detections == null || target == null) return;
                    var dict = _tracks(detections);
                    if (dict == null || !dict.ContainsKey(target)) return;
                    ContactMade.RaiseOnMainThread(new ContactEvent(Us as MainConstruct, T as MainConstruct, __instance.DetectionType));
                }
                catch
                {
                }
            }

            // ---- incoming projectiles (worker threads) ------------------------------------------------------

            private const float RepeatSeconds = 5f;

            private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

            private sealed class WarningOwner
            {
                public MainConstruct Construct;
                public readonly Dictionary<int, float> LastReported = new Dictionary<int, float>();
            }

            private static readonly ConditionalWeakTable<MissileWarningManager, WarningOwner> _owners = new ConditionalWeakTable<MissileWarningManager, WarningOwner>();

            private static WarningOwner OwnerOf(MissileWarningManager mwm)
            {
                if (_owners.TryGetValue(mwm, out var owner)) return owner;
                MainConstruct found = null;
                try
                {
                    var all = StaticConstructablesManager.Constructables;
                    for (int i = 0; i < all.Count; i++)
                    {
                        var c = all[i];
                        if (c != null && ReferenceEquals(c.MWM, mwm))
                        {
                            found = c;
                            break;
                        }
                    }
                }
                catch
                {
                    // the list can change under us on a worker thread; try again next time
                }
                if (found == null) return null;
                owner = new WarningOwner { Construct = found };
                try
                {
                    _owners.Add(mwm, owner);
                }
                catch (ArgumentException)
                {
                    _owners.TryGetValue(mwm, out owner); // another thread got there first
                }
                return owner;
            }

            private static void MissileDetected_Postfix(MissileWarningManager __instance, IProjectile P, bool __result)
            {
                if (!__result || P == null || Net.IsClient || !IncomingProjectile.HasSubscribers) return;
                try
                {
                    var owner = OwnerOf(__instance);
                    if (owner == null) return;
                    float now = (float)_clock.Elapsed.TotalSeconds;
                    int id = P.UniqueId;
                    lock (owner)
                    {
                        if (owner.LastReported.TryGetValue(id, out float last) && now - last < RepeatSeconds) return;
                        owner.LastReported[id] = now;
                        if (owner.LastReported.Count > 512)
                        {
                            var stale = new List<int>();
                            foreach (var kv in owner.LastReported)
                                if (now - kv.Value > RepeatSeconds) stale.Add(kv.Key);
                            foreach (var k in stale) owner.LastReported.Remove(k);
                        }
                    }
                    IncomingProjectile.RaiseOnMainThread(new IncomingProjectileEvent(owner.Construct, P));
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>A craft first detected another craft.</summary>
    public sealed class ContactEvent : GameEvent
    {
        internal ContactEvent(MainConstruct detector, MainConstruct target, DetectionType sensor)
        {
            Detector = detector;
            Target = target;
            Sensor = sensor;
            DetectorName = EventHelpers.SafeName(detector);
            TargetName = EventHelpers.SafeName(target);
        }

        /// <summary>The craft whose sensors made the detection. Game object.</summary>
        public MainConstruct Detector { get; }

        /// <summary>The craft detected. Game object.</summary>
        public MainConstruct Target { get; }

        public string DetectorName { get; }

        public string TargetName { get; }

        /// <summary>The kind of sensor that made the first detection.</summary>
        public DetectionType Sensor { get; }

        /// <summary>True when the two craft are on different teams.</summary>
        public bool IsOtherTeam
        {
            get
            {
                try
                {
                    return Detector != null && Target != null && Detector.GetTeam() != Target.GetTeam();
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    /// <summary>A craft spotted an incoming projectile.</summary>
    public sealed class IncomingProjectileEvent : GameEvent
    {
        internal IncomingProjectileEvent(MainConstruct construct, IProjectile projectile)
        {
            Construct = construct;
            Projectile = projectile;
            Kind = Events.Projectiles.KindOf(projectile as Projectile);
            Name = EventHelpers.SafeName(construct);
            try
            {
                Position = ((I_Targetable)projectile).CentreOfMass;
            }
            catch
            {
                Position = Vector3.zero;
            }
        }

        /// <summary>The craft that saw it. Game object.</summary>
        public MainConstruct Construct { get; }

        public string Name { get; }

        /// <summary>The projectile (pooled: it is reused once it is gone, so don't keep it). Game object.</summary>
        public IProjectile Projectile { get; }

        public ProjectileKind Kind { get; }

        public bool IsMissile => Kind == ProjectileKind.Missile;

        /// <summary>Where it was when spotted (game world).</summary>
        public Vector3 Position { get; }
    }
}

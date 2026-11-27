using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BrilliantSkies.Core;
using BrilliantSkies.Ftd.DamageLogging;
using BrilliantSkies.Ftd.Game.Pools;
using FtdModApi.Patching;
using HarmonyLib;
using MissileExplosionHandlerAlias = BrilliantSkies.Ftd.Missiles.Blueprints.MissileExplosionHandler;

namespace FtdModApi
{
    public static partial class Events
    {
        /// <summary>Every weapon type: about to fire (cancellable) and fired.</summary>
        public static class Weapons
        {
            private static int _fireRewrites;

            private static readonly EventHook FireHook = new EventHook(Features.EventWeapons, () => new[]
            {
                new PatchSpec
                {
                    // ConstructableWeapon.Fire does the game's readiness checks (slot, aim, reload, fail-safe, ammo limit),
                    // then calls the weapon type's WeaponFire. That one call is routed through WeaponFireGate.
                    Id = "ConstructableWeapon.Fire: Weapons.Firing/Fired",
                    FeatureId = Features.EventWeapons,
                    Target = () =>
                    {
                        _fireRewrites = 0;
                        return EventHelpers.Declared(typeof(ConstructableWeapon), "Fire");
                    },
                    Transpiler = PatchSpec.Hook(typeof(Weapons), nameof(Fire_Transpiler)),
                    Verify = () => _fireRewrites == 1 ? null : $"expected 1 WeaponFire call in ConstructableWeapon.Fire, found {_fireRewrites}"
                }
            });

            /// <summary>
            /// A weapon passed the game's readiness checks (aimed, reloaded, fail-safe) and is about to fire. Covers APS, CRAM,
            /// missile launchers, lasers, particle cannons, plasma, flamers and simple weapons, whether fired by AI, the player,
            /// Lua or a breadboard. Cancel to hold fire this time. Host. Runs on any thread (usually an AI worker thread):
            /// keep handlers fast and thread-safe.
            /// </summary>
            public static EventChannel<WeaponFiringEvent> Firing { get; } = new EventChannel<WeaponFiringEvent>(
                "Weapons.Firing", EventSide.Host, EventThread.Any, "A weapon is about to fire.", FireHook);

            /// <summary>
            /// A weapon fired (at least one shot, missile or beam left it). Delivered on the main thread, at most a frame later.
            /// Host. For the projectile itself use the weapon family's own event (<see cref="Aps.ShellFired"/>, ...).
            /// </summary>
            public static EventChannel<WeaponFiredEvent> Fired { get; } = new EventChannel<WeaponFiredEvent>(
                "Weapons.Fired", EventSide.Host, "A weapon fired.", FireHook);

            private static IEnumerable<CodeInstruction> Fire_Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var weaponFire = AccessTools.Method(typeof(ConstructableWeapon), "WeaponFire", new[] { typeof(FiredMunitionReturn) });
                var gate = AccessTools.Method(typeof(Weapons), nameof(WeaponFireGate));
                foreach (var ins in instructions)
                {
                    // callvirt this.WeaponFire(FMR)  ->  call WeaponFireGate(this, FMR): same stack, labels untouched.
                    if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call) && Equals(ins.operand, weaponFire))
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = gate;
                        _fireRewrites++;
                    }
                    yield return ins;
                }
            }

            /// <summary>Called from the patched ConstructableWeapon.Fire in place of weapon.WeaponFire(fmr).</summary>
            public static void WeaponFireGate(ConstructableWeapon weapon, FiredMunitionReturn fmr)
            {
                if (weapon == null) return;
                if (Firing.HasSubscribers && fmr != null)
                {
                    var e = new WeaponFiringEvent(weapon, fmr);
                    Firing.Raise(e);
                    if (e.IsCancelled) return; // FMR stays "not fired", so the game doesn't start the reload timer
                }
                weapon.WeaponFire(fmr);
                if (Fired.HasSubscribers && fmr != null && fmr.GetFired())
                    Fired.RaiseOnMainThread(new WeaponFiredEvent(weapon, fmr));
            }
        }

        /// <summary>Advanced cannon (APS) events.</summary>
        public static class Aps
        {
            private static readonly EventHook ShellFiredHook = new EventHook(Features.EventAps, () => new[]
            {
                new PatchSpec
                {
                    Id = "AdvCannonFiringPiece.MainThreadFireCallback: Aps.ShellFired",
                    FeatureId = Features.EventAps,
                    Target = () => EventHelpers.Declared(typeof(AdvCannonFiringPiece), "MainThreadFireCallback"),
                    Prefix = PatchSpec.Hook(typeof(Aps), nameof(MainThreadFireCallback_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Aps), nameof(MainThreadFireCallback_Postfix))
                }
            });

            /// <summary>An APS gun fired a shell; the projectile now exists. Main thread. Host.</summary>
            public static EventChannel<ApsShellFiredEvent> ShellFired { get; } = new EventChannel<ApsShellFiredEvent>(
                "Aps.ShellFired", EventSide.Host, "An APS gun fired a shell.", ShellFiredHook);

            // The game can give up before creating the projectile; the FMR is shared between weapons, so compare before/after.
            private static void MainThreadFireCallback_Prefix(FiredMunitionReturn FMR, out IProjectileFollow __state)
            {
                __state = FMR?.ProjectileFired;
            }

            private static void MainThreadFireCallback_Postfix(AdvCannonFiringPiece __instance, FiredMunitionReturn FMR, IDamageLogger logger, ShellModel shellModel, float railDraw, IProjectileFollow __state)
            {
                if (__instance == null || !ShellFired.HasSubscribers) return;
                if (FMR == null || FMR.ProjectileFired == null || ReferenceEquals(FMR.ProjectileFired, __state)) return;
                ShellFired.Raise(new ApsShellFiredEvent(__instance, shellModel, FMR?.ProjectileFired, logger, railDraw));
            }
        }

        /// <summary>CRAM cannon events.</summary>
        public static class Cram
        {
            private static readonly EventHook ShellFiredHook = new EventHook(Features.EventCram, () => new[]
            {
                new PatchSpec
                {
                    Id = "CannonFiringPiece.MainThreadFire: Cram.ShellFired",
                    FeatureId = Features.EventCram,
                    Target = () => EventHelpers.Declared(typeof(CannonFiringPiece), "MainThreadFire"),
                    Postfix = PatchSpec.Hook(typeof(Cram), nameof(MainThreadFire_Postfix))
                }
            });

            private static readonly EventHook PackedHook = new EventHook(Features.EventCram, () => new[]
            {
                new PatchSpec
                {
                    Id = "CannonFiringPiece.RunPacking: Cram.Packed",
                    FeatureId = Features.EventCram,
                    Target = () => EventHelpers.Declared(typeof(CannonFiringPiece), "RunPacking"),
                    Prefix = PatchSpec.Hook(typeof(Cram), nameof(RunPacking_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Cram), nameof(RunPacking_Postfix))
                }
            });

            /// <summary>A CRAM cannon fired a shell; the projectile now exists. Main thread. Host.</summary>
            public static EventChannel<CramShellFiredEvent> ShellFired { get; } = new EventChannel<CramShellFiredEvent>(
                "Cram.ShellFired", EventSide.Host, "A CRAM cannon fired a shell.", ShellFiredHook);

            /// <summary>A CRAM cannon finished packing a full shell (it can fire at full strength now). Main thread. Host.</summary>
            public static EventChannel<CramPackedEvent> Packed { get; } = new EventChannel<CramPackedEvent>(
                "Cram.Packed", EventSide.Host, "A CRAM cannon finished packing a shell.", PackedHook);

            private static void MainThreadFire_Postfix(CannonFiringPiece __instance, FiredMunitionReturn FMR, RailwayGunShellStats shell, IDamageLogger logger)
            {
                if (__instance == null || !ShellFired.HasSubscribers) return;
                ShellFired.Raise(new CramShellFiredEvent(__instance, shell, FMR?.ProjectileFired, logger));
            }

            private static void RunPacking_Prefix(CannonFiringPiece __instance, out float __state)
            {
                __state = __instance != null ? __instance.PackTime : 0f;
            }

            private static void RunPacking_Postfix(CannonFiringPiece __instance, float __state)
            {
                if (__instance == null || Net.IsClient || !Packed.HasSubscribers) return;
                float max;
                try
                {
                    max = __instance.Node.Stats.MaxPackTime;
                }
                catch
                {
                    return;
                }
                const float tolerance = 0.0001f;
                if (max > 0f && __state < max - tolerance && __instance.PackTime >= max - tolerance)
                    Packed.Raise(new CramPackedEvent(__instance, __instance.PackTime));
            }
        }

        /// <summary>Missile events: launched, reloaded, detonated.</summary>
        public static class Missiles
        {
            // Private fields of MissileExplosionHandler, resolved when the Detonated hook is installed (not at class load,
            // so a renamed field only breaks this one event).
            private static AccessTools.FieldRef<MissileExplosionHandlerAlias, bool> _exploding;
            private static AccessTools.FieldRef<MissileExplosionHandlerAlias, BrilliantSkies.Ftd.Missiles.Missile> _handlerMissile;

            private static MethodBase DetonateTarget()
            {
                _exploding = FieldRefs.Get<MissileExplosionHandlerAlias, bool>("_exploding");
                _handlerMissile = FieldRefs.Get<MissileExplosionHandlerAlias, BrilliantSkies.Ftd.Missiles.Missile>("_missile");
                return typeof(MissileExplosionHandlerAlias).GetMethod("ExplodeNow", BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(IAllConstructBlock), typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector3), typeof(bool), typeof(bool) }, null);
            }

            private static readonly EventHook LaunchedHook = new EventHook(Features.EventMissiles, () => new[]
            {
                new PatchSpec
                {
                    Id = "LaunchpadAbstract.PrepareAndFireMissile: Missiles.Launched",
                    FeatureId = Features.EventMissiles,
                    Target = () => EventHelpers.Declared(typeof(LaunchpadAbstract), "PrepareAndFireMissile"),
                    Postfix = PatchSpec.Hook(typeof(Missiles), nameof(PrepareAndFireMissile_Postfix))
                }
            });

            private static readonly EventHook ReloadedHook = new EventHook(Features.EventMissiles, () => new[]
            {
                new PatchSpec
                {
                    Id = "MissileTube.Reload: Missiles.Reloaded",
                    FeatureId = Features.EventMissiles,
                    Target = () => EventHelpers.Declared(typeof(MissileTube), "Reload"),
                    Postfix = PatchSpec.Hook(typeof(Missiles), nameof(Reload_Postfix))
                }
            });

            private static readonly EventHook DetonatedHook = new EventHook(Features.EventMissiles, () => new[]
            {
                new PatchSpec
                {
                    Id = "MissileExplosionHandler.ExplodeNow: Missiles.Detonated",
                    FeatureId = Features.EventMissiles,
                    Target = DetonateTarget,
                    Prefix = PatchSpec.Hook(typeof(Missiles), nameof(ExplodeNow_Prefix))
                },
                // Optional: tell the cause apart for detonations that pass no target (fuse, interceptor, script).
                CauseSpec("MissileImpactAndTriggering.CheckFuse: detonation cause", typeof(BrilliantSkies.Ftd.Missiles.Blueprints.MissileImpactAndTriggering), "CheckFuse", nameof(Cause_Fuse)),
                CauseSpec("MissileImpactAndTriggering.OnCollision: detonation cause", typeof(BrilliantSkies.Ftd.Missiles.Blueprints.MissileImpactAndTriggering), "OnCollision", nameof(Cause_Collision)),
                CauseSpec("MissileInterceptor.Explode: detonation cause", typeof(BrilliantSkies.Ftd.Missiles.Components.MissileInterceptor), "Explode", nameof(Cause_Interceptor)),
                CauseSpec("MissileBreadboardController.ApplyOutputs: detonation cause", typeof(BrilliantSkies.Ftd.Missiles.Breadboard.MissileBreadboardController), "ApplyOutputs", nameof(Cause_Scripted))
            });

            /// <summary>A missile, torpedo or bomb left its launcher (including MIRV warheads). Main thread. All machines.</summary>
            public static EventChannel<MissileLaunchedEvent> Launched { get; } = new EventChannel<MissileLaunchedEvent>(
                "Missiles.Launched", EventSide.All, "A missile left its launcher.", LaunchedHook);

            /// <summary>A missile launcher tube loaded a new missile (ammo was paid). Main thread. Host (and clients when synced).</summary>
            public static EventChannel<MissileReloadedEvent> Reloaded { get; } = new EventChannel<MissileReloadedEvent>(
                "Missiles.Reloaded", EventSide.All, "A launcher tube loaded a missile.", ReloadedHook);

            /// <summary>
            /// A missile is detonating (impact, proximity or altitude fuse, shot down, out of flight time, interceptor,
            /// script). Raised just before the explosion, while the missile can still be read. <c>IsArmed</c> is false for a
            /// dud that won't explode. Main thread. Host.
            /// </summary>
            public static EventChannel<MissileDetonatedEvent> Detonated { get; } = new EventChannel<MissileDetonatedEvent>(
                "Missiles.Detonated", EventSide.Host, "A missile is detonating.", DetonatedHook);

            private static PatchSpec CauseSpec(string id, Type type, string method, string hook) => new PatchSpec
            {
                Id = id,
                FeatureId = Features.EventMissiles,
                Required = false,
                Target = () => EventHelpers.Declared(type, method),
                Prefix = PatchSpec.Hook(typeof(Missiles), hook),
                Finalizer = PatchSpec.Hook(typeof(Missiles), nameof(Cause_Clear))
            };

            [ThreadStatic] private static DetonationCause _callerCause;

            private static void Cause_Fuse() => _callerCause = DetonationCause.Fuse;
            private static void Cause_Collision() => _callerCause = DetonationCause.Impact;
            private static void Cause_Interceptor() => _callerCause = DetonationCause.Interceptor;
            private static void Cause_Scripted() => _callerCause = DetonationCause.Scripted;
            private static void Cause_Clear() => _callerCause = DetonationCause.Other;

            private static void PrepareAndFireMissile_Postfix(LaunchpadAbstract __instance, IDamageLogger logger, BrilliantSkies.Ftd.Missiles.Missile missile, BrilliantSkies.Ftd.Missiles.Missile firingMirv)
            {
                if (missile == null || !Launched.HasSubscribers) return;
                Launched.Raise(new MissileLaunchedEvent(__instance, missile, firingMirv, logger));
            }

            private static void Reload_Postfix(MissileTube __instance, bool __result)
            {
                // Reload also returns true without loading anything when the gantry is missing.
                if (!__result || __instance == null || !__instance.Loaded || !Reloaded.HasSubscribers) return;
                Reloaded.Raise(new MissileReloadedEvent(__instance._launchPad));
            }

            private static void ExplodeNow_Prefix(MissileExplosionHandlerAlias __instance, IAllConstructBlock construct, UnityEngine.Vector3 globalPoint, bool safetyOn, bool DetonatedByProxy)
            {
                if (__instance == null || Net.IsClient || !Detonated.HasSubscribers || _exploding == null || _handlerMissile == null) return;
                if (_exploding(__instance)) return; // the game ignores repeat calls
                var missile = _handlerMissile(__instance);
                if (missile == null) return;

                DetonationCause cause;
                if (missile.Health < 1f) cause = DetonationCause.ShotDown;
                else if (construct != null) cause = DetonatedByProxy ? DetonationCause.Fuse : DetonationCause.Impact;
                else if (_callerCause != DetonationCause.Other) cause = _callerCause;
                else if (missile.Blueprint != null && missile.FlightTime > missile.Blueprint.LifeTime) cause = DetonationCause.Expired;
                else cause = DetonationCause.Other;

                bool armed = !safetyOn && missile.Blueprint != null && missile.TimeSinceLaunch >= missile.Blueprint.WarheadArmTime;
                Detonated.Raise(new MissileDetonatedEvent(missile, cause, construct as AllConstruct, globalPoint, armed));
            }
        }
    }

    /// <summary>The game's weapon families, as the API sees them.</summary>
    public enum WeaponKind
    {
        Other,
        /// <summary>Advanced cannon firing piece.</summary>
        Aps,
        /// <summary>CRAM cannon firing piece.</summary>
        Cram,
        /// <summary>Missile launcher controller (all its tubes).</summary>
        Missile,
        Laser,
        ParticleCannon,
        Plasma,
        Flamer,
        /// <summary>Simple and custom simple cannons, guns and similar.</summary>
        Simple
    }

    /// <summary>Why a missile detonated.</summary>
    public enum DetonationCause
    {
        Other,
        /// <summary>Hit a craft or the ground/water.</summary>
        Impact,
        /// <summary>A proximity or altitude fuse triggered.</summary>
        Fuse,
        /// <summary>Destroyed by damage (CIWS, flak, LAMS, another missile).</summary>
        ShotDown,
        /// <summary>Ran out of flight time.</summary>
        Expired,
        /// <summary>An interceptor missile reached its target.</summary>
        Interceptor,
        /// <summary>Detonated by a missile breadboard. (Lua and remote-guidance self-destructs currently report Other.)</summary>
        Scripted
    }

    internal static class WeaponKinds
    {
        internal static WeaponKind Of(ConstructableWeapon w)
        {
            switch (w)
            {
                case AdvCannonFiringPiece _: return WeaponKind.Aps;
                case CannonFiringPiece _: return WeaponKind.Cram;
                case MissileControl _: return WeaponKind.Missile;
                case ParticleCannon _: return WeaponKind.ParticleCannon; // includes the melee lens
                case PlasmaMantlet _: return WeaponKind.Plasma;
                case FlamerMain _: return WeaponKind.Flamer;
                case ConventionalLaser _: return WeaponKind.Laser;
                case Laser _: return WeaponKind.Laser;
                case WorldWarCannonBase _: return WeaponKind.Simple;
                default: return WeaponKind.Other;
            }
        }
    }

    /// <summary>Common properties of weapon events.</summary>
    public abstract class WeaponEventBase : GameEvent
    {
        protected WeaponEventBase(ConstructableWeapon weapon)
        {
            Weapon = weapon;
            Kind = WeaponKinds.Of(weapon);
        }

        /// <summary>The weapon block (firing piece, launcher controller, laser combiner, ...). Game object.</summary>
        public ConstructableWeapon Weapon { get; }

        public WeaponKind Kind { get; }

        /// <summary>The craft the weapon is on.</summary>
        public MainConstruct MainConstruct => Weapon?.MainConstruct as MainConstruct;
    }

    public sealed class WeaponFiringEvent : CancellableEvent
    {
        internal WeaponFiringEvent(ConstructableWeapon weapon, FiredMunitionReturn fmr)
        {
            Weapon = weapon;
            Kind = WeaponKinds.Of(weapon);
            Gunner = fmr.Gunner;
            WeaponSlot = fmr.WeaponSlot;
            ForceFire = fmr.ForceFire;
        }

        public ConstructableWeapon Weapon { get; }

        public WeaponKind Kind { get; }

        /// <summary>The craft the weapon is on.</summary>
        public MainConstruct MainConstruct => Weapon?.MainConstruct as MainConstruct;

        /// <summary>Who gets the credit for hits. May be null.</summary>
        public IDamageLogger Gunner { get; }

        /// <summary>The weapon slot being fired (0 = all).</summary>
        public int WeaponSlot { get; }

        /// <summary>True when the player or a script forced the shot regardless of aim.</summary>
        public bool ForceFire { get; }
    }

    public sealed class WeaponFiredEvent : WeaponEventBase
    {
        internal WeaponFiredEvent(ConstructableWeapon weapon, FiredMunitionReturn fmr) : base(weapon)
        {
            Gunner = fmr.Gunner;
            WeaponSlot = fmr.WeaponSlot;
        }

        public IDamageLogger Gunner { get; }

        public int WeaponSlot { get; }
    }

    public sealed class ApsShellFiredEvent : WeaponEventBase
    {
        internal ApsShellFiredEvent(AdvCannonFiringPiece gun, ShellModel shell, IProjectileFollow projectile, IDamageLogger gunner, float railDraw) : base(gun)
        {
            Gun = gun;
            Shell = shell;
            Projectile = projectile;
            Gunner = gunner;
            RailDraw = railDraw;
        }

        /// <summary>The APS firing piece.</summary>
        public AdvCannonFiringPiece Gun { get; }

        /// <summary>The shell design that was fired (its stats include every mod's changes).</summary>
        public ShellModel Shell { get; }

        /// <summary>The projectile in flight (may be null if the game didn't create one).</summary>
        public IProjectileFollow Projectile { get; }

        public IDamageLogger Gunner { get; }

        /// <summary>Railgun energy used for this shot.</summary>
        public float RailDraw { get; }

        /// <summary>Shell diameter in metres.</summary>
        public float Diameter => Shell?.Diameter ?? 0f;
    }

    public sealed class CramShellFiredEvent : WeaponEventBase
    {
        internal CramShellFiredEvent(CannonFiringPiece gun, RailwayGunShellStats shell, IProjectileFollow projectile, IDamageLogger gunner) : base(gun)
        {
            Gun = gun;
            Shell = shell;
            Projectile = projectile;
            Gunner = gunner;
        }

        /// <summary>The CRAM firing piece.</summary>
        public CannonFiringPiece Gun { get; }

        /// <summary>The fired shell's stats (after every mod's changes).</summary>
        public RailwayGunShellStats Shell { get; }

        public IProjectileFollow Projectile { get; }

        public IDamageLogger Gunner { get; }
    }

    public sealed class CramPackedEvent : WeaponEventBase
    {
        internal CramPackedEvent(CannonFiringPiece gun, float packTime) : base(gun)
        {
            Gun = gun;
            PackTime = packTime;
        }

        public CannonFiringPiece Gun { get; }

        /// <summary>Seconds of packing in the finished shell.</summary>
        public float PackTime { get; }
    }

    public sealed class MissileLaunchedEvent : GameEvent
    {
        internal MissileLaunchedEvent(LaunchpadAbstract launcher, BrilliantSkies.Ftd.Missiles.Missile missile, BrilliantSkies.Ftd.Missiles.Missile mirvParent, IDamageLogger gunner)
        {
            Launcher = launcher;
            Missile = missile;
            MirvParent = mirvParent;
            Gunner = gunner;
        }

        /// <summary>The launch pad (gantry, launcher block). Game object.</summary>
        public LaunchpadAbstract Launcher { get; }

        public BrilliantSkies.Ftd.Missiles.Missile Missile { get; }

        /// <summary>For a MIRV warhead, the missile it split from; otherwise null.</summary>
        public BrilliantSkies.Ftd.Missiles.Missile MirvParent { get; }

        public bool IsMirvWarhead => MirvParent != null;

        public IDamageLogger Gunner { get; }

        public MainConstruct MainConstruct => Launcher?.MainConstruct as MainConstruct;
    }

    public sealed class MissileReloadedEvent : GameEvent
    {
        internal MissileReloadedEvent(LaunchpadAbstract launcher)
        {
            Launcher = launcher;
        }

        public LaunchpadAbstract Launcher { get; }

        public MainConstruct MainConstruct => Launcher?.MainConstruct as MainConstruct;
    }

    public sealed class MissileDetonatedEvent : GameEvent
    {
        internal MissileDetonatedEvent(BrilliantSkies.Ftd.Missiles.Missile missile, DetonationCause cause, AllConstruct target, UnityEngine.Vector3 position, bool armed)
        {
            Missile = missile;
            Cause = cause;
            Target = target;
            Position = position;
            IsArmed = armed;
        }

        public BrilliantSkies.Ftd.Missiles.Missile Missile { get; }

        public DetonationCause Cause { get; }

        /// <summary>The construct it hit or triggered on, if any.</summary>
        public AllConstruct Target { get; }

        /// <summary>Where it detonated (world position).</summary>
        public UnityEngine.Vector3 Position { get; }

        /// <summary>False when the warhead wasn't armed yet (or safety was on): the missile is removed without exploding.</summary>
        public bool IsArmed { get; }

        /// <summary>The launcher it came from (may be null if that block is gone).</summary>
        public LaunchpadAbstract Launcher => Missile != null ? Missile.LaunchPad : null;
    }
}

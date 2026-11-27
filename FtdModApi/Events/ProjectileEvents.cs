using System;
using BrilliantSkies.Core;
using BrilliantSkies.Core.Units;
using BrilliantSkies.Ftd.DamageLogging;
using BrilliantSkies.Ftd.DamageModels;
using BrilliantSkies.Ftd.Game.Pools;
using BrilliantSkies.GridCasts;
using FtdModApi.Patching;
using HarmonyLib;
using UnityEngine;

namespace FtdModApi
{
    public static partial class Events
    {
        /// <summary>
        /// Shells, bullets and missiles in flight: hits, detonations, damage to the munition itself, shields.
        /// The game runs projectile maths on worker threads; these events are queued to the main thread (they arrive
        /// within a frame), except <see cref="ShieldHit"/>, which runs on the worker thread so it can change the outcome.
        /// They can be very frequent in big battles: keep handlers cheap.
        /// </summary>
        public static class Projectiles
        {
            // ---- hooks -------------------------------------------------------------------------------------

            private static readonly EventHook HitHook = new EventHook(Features.EventProjectiles, () => new[]
            {
                new PatchSpec
                {
                    Id = "ProjectileDamage.ProcessOne: Projectiles.Hit",
                    FeatureId = Features.EventProjectiles,
                    Target = () => EventHelpers.Declared(typeof(ProjectileDamage), "ProcessOne"),
                    Prefix = PatchSpec.Hook(typeof(Projectiles), nameof(ProcessOne_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Projectiles), nameof(ProcessOne_Postfix))
                }
            });

            private static readonly EventHook DetonatedHook = new EventHook(Features.EventProjectiles, () => new[]
            {
                ExplodeSpec(typeof(AdvPooledProjectile), true, nameof(AdvExplode_Postfix)),
                ExplodeSpec(typeof(PooledCramProjectile), false, nameof(CramExplode_Postfix)),
                ExplodeSpec(typeof(PooledProjectile), false, nameof(SimpleExplode_Postfix))
            });

            private static readonly EventHook DamagedHook = new EventHook(Features.EventProjectiles, () => new[]
            {
                new PatchSpec
                {
                    // Shared by missiles, APS and CRAM shells: the one place a munition loses health.
                    Id = "Projectile.ReduceHealth: Projectiles.Damaged",
                    FeatureId = Features.EventProjectiles,
                    Target = () => EventHelpers.Declared(typeof(Projectile), "ReduceHealth"),
                    Prefix = PatchSpec.Hook(typeof(Projectiles), nameof(ReduceHealth_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Projectiles), nameof(ReduceHealth_Postfix))
                }
            });

            private static readonly EventHook ShieldHook = new EventHook(Features.EventProjectiles, () => new[]
            {
                new PatchSpec
                {
                    Id = "ShieldProjector.Reflect: Projectiles.ShieldHit",
                    FeatureId = Features.EventProjectiles,
                    Target = () => EventHelpers.Declared(typeof(ShieldProjector), "Reflect"),
                    Postfix = PatchSpec.Hook(typeof(Projectiles), nameof(Reflect_Postfix))
                }
            });

            private static PatchSpec ExplodeSpec(Type type, bool required, string postfix) => new PatchSpec
            {
                Id = type.Name + ".Explode: Projectiles.Detonated",
                FeatureId = Features.EventProjectiles,
                Required = required,
                Target = () => EventHelpers.Declared(type, "Explode"),
                Postfix = PatchSpec.Hook(typeof(Projectiles), postfix)
            };

            // ---- channels ----------------------------------------------------------------------------------

            /// <summary>
            /// An APS, CRAM or simple-weapon projectile, or a fragment from one, hit something (a block, a shell, a missile, a
            /// player, a balloon). One event per thing hit along its path, with health before and after (so <c>IsKill</c> tells
            /// you about the killing blow). Fragments use their parent shell's <c>ProjectileId</c>. Hits that do no damage
            /// (e.g. setting off ERA, or damage turned off) still appear with <c>DamageDone</c> 0. Very frequent: in big battles
            /// fragments make thousands per second. Queued to the main thread. Host.
            /// </summary>
            public static EventChannel<ProjectileHitEvent> Hit { get; } = new EventChannel<ProjectileHitEvent>(
                "Projectiles.Hit", EventSide.Host, "A projectile hit something.", HitHook);

            /// <summary>
            /// An APS, CRAM or simple-weapon shell detonated or released its payload (fuse, impact, ground hit, shot down, ...).
            /// Missiles have their own <see cref="Missiles.Detonated"/>. Queued to the main thread. Host.
            /// </summary>
            public static EventChannel<ProjectileDetonatedEvent> Detonated { get; } = new EventChannel<ProjectileDetonatedEvent>(
                "Projectiles.Detonated", EventSide.Host, "A shell detonated.", DetonatedHook);

            /// <summary>
            /// A munition in flight (missile, APS or CRAM shell) took damage from CIWS, flak, LAMS or another munition.
            /// <c>Destroyed</c> is true when this damage destroys it. Queued to the main thread. Host.
            /// </summary>
            public static EventChannel<ProjectileDamagedEvent> Damaged { get; } = new EventChannel<ProjectileDamagedEvent>(
                "Projectiles.Damaged", EventSide.Host, "A munition in flight took damage.", DamagedHook);

            /// <summary>
            /// A projectile hit a shield. The game has rolled whether it is reflected; set <c>Reflects</c> to change that.
            /// Runs on any thread (the projectile worker thread): fast, thread-safe handlers only. Host.
            /// </summary>
            public static EventChannel<ShieldHitEvent> ShieldHit { get; } = new EventChannel<ShieldHitEvent>(
                "Projectiles.ShieldHit", EventSide.Host, EventThread.Any, "A projectile hit a shield.", ShieldHook);

            // ---- patch bodies ------------------------------------------------------------------------------

            /// <summary>What ProcessOne's prefix saw before the hit was processed.</summary>
            private struct HitState
            {
                public bool Eligible;
                public float Health;
                public float Kinetic;
                public float ArmourPiercing;
            }

            private static void ProcessOne_Prefix(ProjectileImpactState pState, GridCastHit hit, out HitState __state)
            {
                __state = default;
                if (!Hit.HasSubscribers || Net.IsClient || pState is null || hit is null) return;
                var target = hit.DamageableObject;
                // The game skips these (nothing damageable, the thing it just ricocheted off, shields).
                if (target is null || ReferenceEquals(target, pState.DontHitAgain) || hit.HitSource == HitSource.Shield) return;
                try
                {
                    __state.Health = target.GetCurrentHealth();
                }
                catch
                {
                    return;
                }
                __state.Kinetic = pState.KineticDamage;
                __state.ArmourPiercing = pState.ArmourPiercing;
                __state.Eligible = true;
            }

            private static void ProcessOne_Postfix(ProjectileImpactState pState, GridCastHit hit, int projectileId, HitState __state)
            {
                if (!__state.Eligible || !Hit.HasSubscribers) return;
                var target = hit.DamageableObject;
                float after;
                AllConstruct construct;
                Vector3 position;
                try
                {
                    after = target.GetCurrentHealth();
                    construct = target.GetC();
                    position = hit.InPointGlobal;
                }
                catch
                {
                    return;
                }
                Hit.RaiseOnMainThread(new ProjectileHitEvent(target, hit.BlockHit, construct, __state.Health, after,
                    __state.Kinetic, pState.KineticDamage, __state.ArmourPiercing, pState.Gunner, position, projectileId));
            }

            private static void AdvExplode_Postfix(AdvPooledProjectile __instance, IAllConstructBlock C, Block B, Func<Vector3> dynamicPoint, ExplosionReason reason, bool __result)
                => RaiseDetonated(__instance, ProjectileKind.Aps, C, B, dynamicPoint, reason, __result);

            private static void CramExplode_Postfix(PooledCramProjectile __instance, IAllConstructBlock C, Block B, Func<Vector3> dynamicPoint, ExplosionReason reason, bool __result)
                => RaiseDetonated(__instance, ProjectileKind.Cram, C, B, dynamicPoint, reason, __result);

            private static void SimpleExplode_Postfix(PooledProjectile __instance, IAllConstructBlock C, Block B, Func<Vector3> dynamicPoint, ExplosionReason reason, bool __result)
                => RaiseDetonated(__instance, ProjectileKind.Simple, C, B, dynamicPoint, reason, __result);

            private static void RaiseDetonated(Projectile projectile, ProjectileKind kind, IAllConstructBlock c, Block b, Func<Vector3> dynamicPoint, ExplosionReason reason, bool result)
            {
                // Explode returns false when nothing was released (client, inert shell, or for APS a repeat call).
                if (!result || projectile is null || Net.IsClient || !Detonated.HasSubscribers) return;
                Vector3 position;
                IDamageLogger gunner;
                try
                {
                    position = dynamicPoint != null ? dynamicPoint() : projectile.Position;
                    gunner = projectile.Gunner;
                }
                catch
                {
                    return;
                }
                Detonated.RaiseOnMainThread(new ProjectileDetonatedEvent(projectile, kind, MapReason(reason), c as AllConstruct, b, position, gunner));
            }

            private static void ReduceHealth_Prefix(Projectile __instance, out float __state)
            {
                __state = __instance is null ? 0f : __instance.Health;
            }

            private static void ReduceHealth_Postfix(Projectile __instance, IDamageDescription damage, float __state)
            {
                if (__instance is null || !Damaged.HasSubscribers) return;
                float after = __instance.Health;
                if (after >= __state) return; // no damage taken
                Damaged.RaiseOnMainThread(new ProjectileDamagedEvent(__instance, KindOf(__instance), __state, after, damage?.DamageLogger));
            }

            private static void Reflect_Postfix(ShieldProjector __instance, Angle angleOfIncidence, ref bool __result)
            {
                if (__instance is null || !ShieldHit.HasSubscribers) return;
                var e = new ShieldHitEvent(__instance, angleOfIncidence.FDeg, __result);
                ShieldHit.Raise(e);
                if (e.Reflects && !__result)
                {
                    // The game only marks the shield as hit (flash, last-hit time) when its own roll reflects.
                    try
                    {
                        __instance.ShieldClass.HitByProjectile(ShieldProjector.ProbabilityOfReflection(__instance.CurrentStrength, e.AngleOfIncidence));
                    }
                    catch
                    {
                        // visual only
                    }
                }
                __result = e.Reflects;
            }

            internal static ProjectileKind KindOf(Projectile p)
            {
                switch (p)
                {
                    case AdvPooledProjectile _: return ProjectileKind.Aps;
                    case PooledCramProjectile _: return ProjectileKind.Cram;
                    case PooledProjectile _: return ProjectileKind.Simple;
                    case BrilliantSkies.Ftd.Missiles.Missile _: return ProjectileKind.Missile;
                    default: return ProjectileKind.Other;
                }
            }

            private static DetonationReason MapReason(ExplosionReason r)
            {
                switch (r)
                {
                    case ExplosionReason.run_out_of_kinetic_energy: return DetonationReason.Stopped;
                    case ExplosionReason.penetration_depth_fuse: return DetonationReason.PenetrationFuse;
                    case ExplosionReason.soft_head: return DetonationReason.SoftHead;
                    case ExplosionReason.timed_fuse_from_first_surface: return DetonationReason.DelayFuse;
                    case ExplosionReason.inertial_fuse: return DetonationReason.InertialFuse;
                    case ExplosionReason.high_altitude_fuse:
                    case ExplosionReason.low_altitude_fuse: return DetonationReason.AltitudeFuse;
                    case ExplosionReason.timed_fuse: return DetonationReason.TimedFuse;
                    case ExplosionReason.ground_hit: return DetonationReason.GroundHit;
                    case ExplosionReason.shell_destroyed: return DetonationReason.ShotDown;
                    case ExplosionReason.squash_head: return DetonationReason.SquashHead;
                    case ExplosionReason.ERA_has_exploded_us: return DetonationReason.Era;
                    default: return DetonationReason.Other;
                }
            }
        }
    }

    /// <summary>What kind of munition a projectile is.</summary>
    public enum ProjectileKind
    {
        Other,
        Aps,
        Cram,
        /// <summary>Simple weapon shell or bullet.</summary>
        Simple,
        Missile
    }

    /// <summary>Why a shell detonated.</summary>
    public enum DetonationReason
    {
        Other,
        /// <summary>Ran out of kinetic energy inside its target.</summary>
        Stopped,
        PenetrationFuse,
        /// <summary>Soft (HE, frag, ...) head burst on the first armour it met.</summary>
        SoftHead,
        /// <summary>Delay fuse: a set time after hitting the first surface.</summary>
        DelayFuse,
        /// <summary>Inertial fuse: triggered by a sudden change of direction (e.g. a hit).</summary>
        InertialFuse,
        AltitudeFuse,
        TimedFuse,
        /// <summary>Hit the ground or water floor.</summary>
        GroundHit,
        /// <summary>Destroyed by CIWS, flak or other interception.</summary>
        ShotDown,
        SquashHead,
        /// <summary>Set off by explosive reactive armour.</summary>
        Era
    }

    public sealed class ProjectileHitEvent : GameEvent
    {
        internal ProjectileHitEvent(IDamageable target, Block block, AllConstruct construct, float healthBefore, float healthAfter,
            float kineticBefore, float kineticAfter, float armourPiercing, IDamageLogger gunner, Vector3 position, int projectileId)
        {
            Target = target;
            Block = block;
            Construct = construct;
            HealthBefore = healthBefore;
            HealthAfter = healthAfter;
            KineticBefore = kineticBefore;
            KineticAfter = kineticAfter;
            ArmourPiercing = armourPiercing;
            Gunner = gunner;
            Position = position;
            ProjectileId = projectileId;
        }

        /// <summary>What was hit: a block, or a munition (missile or shell) when CIWS or flak hits it.</summary>
        public IDamageable Target { get; }

        /// <summary>The block hit (null when the target isn't a block).</summary>
        public Block Block { get; }

        /// <summary>The construct the target belongs to (null for munitions).</summary>
        public AllConstruct Construct { get; }

        public MainConstruct MainConstruct => EventHelpers.MainOf(Construct);

        /// <summary>True when the target is a missile or shell in flight.</summary>
        public bool IsMunitionTarget => Target is Projectile;

        public float HealthBefore { get; }

        public float HealthAfter { get; }

        /// <summary>Health the hit removed.</summary>
        public float DamageDone => Math.Max(0f, HealthBefore - HealthAfter);

        /// <summary>True when this hit destroyed the target (the killing blow), using the game's own death thresholds.</summary>
        public bool IsKill
        {
            get
            {
                if (Target is BrilliantSkies.Ftd.Missiles.Missile) return HealthBefore >= 1f && HealthAfter < 1f;
                if (Target is Projectile) return HealthBefore > 1f && HealthAfter <= 1f; // shells die at 1 health or less
                return HealthBefore > BlockDeathHealth && HealthAfter <= BlockDeathHealth;
            }
        }

        // Block.ApplyDamage kills a block at 0.005 health or less.
        private const float BlockDeathHealth = 0.005f;

        /// <summary>The projectile's kinetic damage before and after the hit (what it has left to penetrate further).</summary>
        public float KineticBefore { get; }

        public float KineticAfter { get; }

        public float ArmourPiercing { get; }

        public IDamageLogger Gunner { get; }

        /// <summary>Where it hit (world position).</summary>
        public Vector3 Position { get; }

        /// <summary>The projectile's id (the same for every hit by one shell).</summary>
        public int ProjectileId { get; }
    }

    public sealed class ProjectileDetonatedEvent : GameEvent
    {
        internal ProjectileDetonatedEvent(Projectile projectile, ProjectileKind kind, DetonationReason reason, AllConstruct target, Block block, Vector3 position, IDamageLogger gunner)
        {
            Projectile = projectile;
            Kind = kind;
            Reason = reason;
            Target = target;
            Block = block;
            Position = position;
            Gunner = gunner;
        }

        /// <summary>The shell. Pooled: by the time handlers run it may already be reused for another shot, so read what you need here.</summary>
        public Projectile Projectile { get; }

        public ProjectileKind Kind { get; }

        public DetonationReason Reason { get; }

        /// <summary>The construct it detonated on or in, if any.</summary>
        public AllConstruct Target { get; }

        /// <summary>The block it detonated on, if the game passed one.</summary>
        public Block Block { get; }

        public Vector3 Position { get; }

        public IDamageLogger Gunner { get; }
    }

    public sealed class ProjectileDamagedEvent : GameEvent
    {
        internal ProjectileDamagedEvent(Projectile projectile, ProjectileKind kind, float healthBefore, float healthAfter, IDamageLogger attacker)
        {
            Projectile = projectile;
            Kind = kind;
            HealthBefore = healthBefore;
            HealthAfter = healthAfter;
            Attacker = attacker;
        }

        /// <summary>The missile or shell (pooled shells may be reused by the time handlers run).</summary>
        public Projectile Projectile { get; }

        public ProjectileKind Kind { get; }

        public bool IsMissile => Kind == ProjectileKind.Missile;

        public float HealthBefore { get; }

        public float HealthAfter { get; }

        /// <summary>True when this damage destroys it (using the game's thresholds for missiles and shells).</summary>
        public bool Destroyed => IsMissile
            ? HealthBefore >= 1f && HealthAfter < 1f   // missiles die below 1 health
            : HealthBefore > 1f && HealthAfter <= 1f;  // shells die at 1 health or less

        /// <summary>Who shot it (for interception credit). May be null.</summary>
        public IDamageLogger Attacker { get; }
    }

    /// <summary>A projectile hit a shield. Not cancellable, but <see cref="Reflects"/> can be changed.</summary>
    public sealed class ShieldHitEvent : GameEvent
    {
        internal ShieldHitEvent(ShieldProjector shield, float angleDegrees, bool reflects)
        {
            Shield = shield;
            AngleOfIncidence = angleDegrees;
            GameReflects = reflects;
            Reflects = reflects;
        }

        /// <summary>The shield projector. Read-only use only: this runs on a worker thread.</summary>
        public ShieldProjector Shield { get; }

        /// <summary>Angle between the projectile and the shield, in degrees.</summary>
        public float AngleOfIncidence { get; }

        /// <summary>What the game rolled.</summary>
        public bool GameReflects { get; }

        /// <summary>Whether the projectile is reflected. Set it to override the game's roll.</summary>
        public bool Reflects { get; set; }
    }
}

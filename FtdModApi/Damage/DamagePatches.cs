using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BrilliantSkies.Ftd.Constructs.Modules.All.StandardExplosion;
using BrilliantSkies.Ftd.DamageLogging;
using BrilliantSkies.Ftd.DamageModels;
using BrilliantSkies.Ftd.Game.Pools;
using FtdModApi.Patching;
using HarmonyLib;
using UnityEngine;

namespace FtdModApi.Damage
{
    /// <summary>
    /// The Harmony side of <see cref="DamageApi"/>. Everything here is internal: mods only see the pipelines.
    ///
    /// Game code this relies on (FtD 4.3.4), found by tracing every damage path in the decompiled source:
    /// <list type="bullet">
    /// <item>Block.ApplyDamage(IDamageDescription, float): the one place every damage type removes block
    /// health. It calls IDamageDescription.CalculateDamage(armourClass, health, position) unless a
    /// precalculated amount is passed (fire damage does this).</item>
    /// <item>ProjectileImpactState.Reset(kinetic, ap, velocity, gunner): called by the four projectile
    /// pools (APS, CRAM, simple weapons, fragments) every time a projectile is launched. The pool
    /// constructors also call it with 0 damage, which we skip.</item>
    /// <item>ExplosionCreator.Explosion(...): both overloads; every explosion in the game is created through
    /// them (shells, missiles, PACs, block deaths, ammo racks, boilers, refinery, avatar items). The
    /// two-argument overload can call the one-argument one, so a depth guard stops double modification.</item>
    /// </list>
    /// </summary>
    internal static class DamagePatches
    {
        internal static IEnumerable<PatchSpec> Specs()
        {
            var self = typeof(DamagePatches);

            // ---- Damage.Block ------------------------------------------------------------------
            yield return new PatchSpec
            {
                Id = "Block.ApplyDamage: modify calculated damage (transpiler)",
                FeatureId = Features.BlockDamage,
                Target = () => PatchSpec.Method(typeof(Block), nameof(Block.ApplyDamage), typeof(IDamageDescription), typeof(float)),
                Transpiler = PatchSpec.Hook(self, nameof(ApplyDamage_Transpiler)),
                Verify = () => _calculateDamageInjections == 1
                    ? null
                    : $"expected 1 call to IDamageDescription.CalculateDamage, found {_calculateDamageInjections}"
            };
            yield return new PatchSpec
            {
                Id = "Block.ApplyDamage: modify precalculated damage (fire)",
                FeatureId = Features.BlockDamage,
                Required = false,
                Target = () => PatchSpec.Method(typeof(Block), nameof(Block.ApplyDamage), typeof(IDamageDescription), typeof(float)),
                Prefix = PatchSpec.Hook(self, nameof(ApplyDamage_PrecalculatedPrefix))
            };

            // ---- Damage.BlockDamagedEvent -----------------------------------------------------------
            yield return new PatchSpec
            {
                Id = "Block.ApplyDamage: damaged event",
                FeatureId = Features.BlockDamagedEvent,
                Target = () => PatchSpec.Method(typeof(Block), nameof(Block.ApplyDamage), typeof(IDamageDescription), typeof(float)),
                Prefix = PatchSpec.Hook(self, nameof(ApplyDamage_EventPrefix)),
                Postfix = PatchSpec.Hook(self, nameof(ApplyDamage_EventPostfix))
            };

            // ---- Damage.ProjectileLaunch ------------------------------------------------------------
            yield return new PatchSpec
            {
                Id = "ProjectileImpactState.Reset: launch modifiers",
                FeatureId = Features.ProjectileLaunch,
                Target = () => PatchSpec.Method(typeof(ProjectileImpactState), nameof(ProjectileImpactState.Reset),
                    typeof(float), typeof(float), typeof(Vector3), typeof(IDamageLogger)),
                Prefix = PatchSpec.Hook(self, nameof(Reset_Prefix))
            };

            // ---- Damage.ProjectileSource (tags which weapon family called Reset) -----------------
            foreach (var spec in SourceTag<AdvPooledProjectile>(ProjectileSource.AdvancedCannon, nameof(TagAdvancedCannon)))
                yield return spec;
            foreach (var spec in SourceTag<PooledCramProjectile>(ProjectileSource.Cram, nameof(TagCram)))
                yield return spec;
            foreach (var spec in SourceTag<PooledProjectile>(ProjectileSource.SimpleWeapon, nameof(TagSimpleWeapon)))
                yield return spec;
            foreach (var spec in SourceTag<PooledFragment>(ProjectileSource.Fragment, nameof(TagFragment)))
                yield return spec;

            // ---- Damage.Explosion --------------------------------------------------------------------
            yield return new PatchSpec
            {
                Id = "ExplosionCreator.Explosion(DD)",
                FeatureId = Features.Explosion,
                Target = () => PatchSpec.Method(typeof(ExplosionCreator), nameof(ExplosionCreator.Explosion), typeof(ExplosionDamageDescription)),
                Prefix = PatchSpec.Hook(self, nameof(Explosion_Prefix)),
                Finalizer = PatchSpec.Hook(self, nameof(Explosion_Finalizer))
            };
            yield return new PatchSpec
            {
                Id = "ExplosionCreator.Explosion(C, DD)",
                FeatureId = Features.Explosion,
                Target = () => PatchSpec.Method(typeof(ExplosionCreator), nameof(ExplosionCreator.Explosion), typeof(IAllConstructBlock), typeof(ExplosionDamageDescription)),
                Prefix = PatchSpec.Hook(self, nameof(ExplosionOnConstruct_Prefix)),
                Finalizer = PatchSpec.Hook(self, nameof(Explosion_Finalizer))
            };
        }

        private static IEnumerable<PatchSpec> SourceTag<TPooled>(ProjectileSource source, string prefixName)
        {
            yield return new PatchSpec
            {
                Id = typeof(TPooled).Name + ".ActivateHere: tag as " + source,
                FeatureId = Features.ProjectileSource,
                Required = false,
                // Exactly one ActivateHere exists on each pooled type in 4.3.4. If an overload is added this
                // throws AmbiguousMatchException, the tag is switched off and the feature reports Degraded.
                Target = () => typeof(TPooled).GetMethod("ActivateHere", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
                Prefix = PatchSpec.Hook(typeof(DamagePatches), prefixName),
                Finalizer = PatchSpec.Hook(typeof(DamagePatches), nameof(RestoreTag))
            };
        }

        // =========================================================================================
        // Damage.Block
        // =========================================================================================

        private static int _calculateDamageInjections;

        private static IEnumerable<CodeInstruction> ApplyDamage_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var calculate = AccessTools.Method(typeof(IDamageDescription), nameof(IDamageDescription.CalculateDamage),
                new[] { typeof(float), typeof(float), typeof(Vector3) });
            var hook = AccessTools.Method(typeof(DamagePatches), nameof(OnBlockDamageCalculated));
            int injected = 0;

            foreach (var instruction in instructions)
            {
                yield return instruction;

                // Stack after the call: [damage]. Push the block and the description, call the hook,
                // and leave the (possibly changed) damage where the game expects it.
                if (calculate != null && instruction.Calls(calculate))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Call, hook);
                    injected++;
                }
            }

            _calculateDamageInjections = injected;
        }

        private static void ApplyDamage_PrecalculatedPrefix(Block __instance, IDamageDescription DD, ref float precalculatedDamage)
        {
            // Mirrors the game's guard: precalculated damage is only used when >= 0, and dead blocks are skipped.
            if (precalculatedDamage >= 0f && __instance.CurrentHealth > 0f && DamageHelp.DealDamage)
            {
                precalculatedDamage = OnBlockDamageCalculated(precalculatedDamage, __instance, DD);
            }
        }

        /// <summary>Called from inside Block.ApplyDamage, possibly on a worker thread.</summary>
        internal static float OnBlockDamageCalculated(float damage, Block block, IDamageDescription dd)
        {
            var pipeline = DamageApi.Block;
            if (pipeline.IsEmpty || block == null || dd == null) return damage;

            var ctx = BlockDamageContext.Rent(block, dd, damage);
            try
            {
                pipeline.Run(ctx);
                var result = ctx.Damage;
                if (float.IsNaN(result) || float.IsInfinity(result) || result < 0f)
                {
                    FtdApi.Log.WarningOnce("block-damage-invalid",
                        $"A Damage.Block modifier produced {result}; clamping. (Negative damage would heal blocks.)");
                    result = float.IsPositiveInfinity(result) ? float.MaxValue : 0f;
                }
                return result;
            }
            finally
            {
                ctx.Release();
            }
        }

        // =========================================================================================
        // Damage.BlockDamagedEvent
        // =========================================================================================

        private static void ApplyDamage_EventPrefix(Block __instance, out float __state)
        {
            __state = __instance.CurrentHealth;
        }

        private static void ApplyDamage_EventPostfix(Block __instance, IDamageDescription DD, float __state)
        {
            var evt = DamageApi.BlockDamaged;
            if (evt.IsEmpty || DD == null) return;

            var after = __instance.CurrentHealth;
            if (!(after < __state)) return;

            var args = BlockDamagedArgs.Rent(__instance, DD, __state, after);
            try
            {
                evt.Raise(args);
            }
            finally
            {
                args.Release();
            }
        }

        // =========================================================================================
        // Damage.ProjectileLaunch / Damage.ProjectileSource
        // =========================================================================================

        [ThreadStatic] private static ProjectileSource _currentSource;

        private static void TagAdvancedCannon(out ProjectileSource __state) { __state = _currentSource; _currentSource = ProjectileSource.AdvancedCannon; }
        private static void TagCram(out ProjectileSource __state) { __state = _currentSource; _currentSource = ProjectileSource.Cram; }
        private static void TagSimpleWeapon(out ProjectileSource __state) { __state = _currentSource; _currentSource = ProjectileSource.SimpleWeapon; }
        private static void TagFragment(out ProjectileSource __state) { __state = _currentSource; _currentSource = ProjectileSource.Fragment; }

        private static void RestoreTag(ProjectileSource __state)
        {
            _currentSource = __state;
        }

        private static void Reset_Prefix(ref float kineticDamage, ref float armourPiercing, Vector3 velocityVector, IDamageLogger gunner)
        {
            // Pools construct their state objects with 0 damage; those are not launches.
            if (kineticDamage <= 0f) return;

            var pipeline = DamageApi.ProjectileLaunch;
            if (pipeline.IsEmpty) return;

            var ctx = ProjectileLaunchContext.Rent(kineticDamage, armourPiercing, velocityVector, gunner, _currentSource);
            try
            {
                pipeline.Run(ctx);
                kineticDamage = Sanitise(ctx.KineticDamage, kineticDamage, "KineticDamage");
                armourPiercing = Sanitise(ctx.ArmourPiercing, armourPiercing, "ArmourPiercing");
            }
            finally
            {
                ctx.Release();
            }
        }

        // =========================================================================================
        // Damage.Explosion
        // =========================================================================================

        [ThreadStatic] private static int _explosionDepth;

        private static void Explosion_Prefix(ExplosionDamageDescription DD)
        {
            if (_explosionDepth++ == 0) ModifyExplosion(DD, hasSourceConstruct: false);
        }

        private static void ExplosionOnConstruct_Prefix(IAllConstructBlock C, ExplosionDamageDescription DD)
        {
            if (_explosionDepth++ == 0) ModifyExplosion(DD, hasSourceConstruct: C != null);
        }

        private static void Explosion_Finalizer()
        {
            _explosionDepth--;
        }

        private static void ModifyExplosion(ExplosionDamageDescription dd, bool hasSourceConstruct)
        {
            var pipeline = DamageApi.Explosion;
            if (dd == null || pipeline.IsEmpty) return;

            var ctx = ExplosionContext.Rent(dd, hasSourceConstruct);
            try
            {
                pipeline.Run(ctx);
                dd.DamagePotential = Sanitise(ctx.Damage, dd.DamagePotential, "Damage");
                dd.Radius = Sanitise(ctx.Radius, dd.Radius, "Radius");
            }
            finally
            {
                ctx.Release();
            }
        }

        private static float Sanitise(float value, float fallback, string what)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                FtdApi.Log.WarningOnce("invalid-" + what, $"A modifier set {what} to {value}; keeping the game's value instead.");
                return fallback;
            }
            return value < 0f ? 0f : value;
        }
    }
}

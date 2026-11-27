using System;
using BrilliantSkies.Core.Id;
using BrilliantSkies.Ftd.DamageLogging;
using BrilliantSkies.Ftd.DamageModels;
using UnityEngine;

namespace FtdModApi
{
    /// <summary>The kind of damage, independent of the game's internal enum so mods keep compiling across updates.</summary>
    public enum ApiDamageType
    {
        Other,
        /// <summary>Shells and bullets (APS, CRAM, simple weapons, fragments) penetrating blocks.</summary>
        Kinetic,
        /// <summary>Rams, collisions, CRAM impact/thumper and hollow-point heads: kinetic damage applied in one go.</summary>
        Crash,
        Explosive,
        Emp,
        Laser,
        Particle,
        Plasma,
        Fire
    }

    /// <summary>Which weapon family launched a kinetic projectile.</summary>
    public enum ProjectileSource
    {
        /// <summary>Source tagging failed or a new projectile type was added by a game update.</summary>
        Unknown,
        AdvancedCannon,
        Cram,
        /// <summary>Simple (non-customisable) cannons and guns.</summary>
        SimpleWeapon,
        /// <summary>Fragments from frag warheads.</summary>
        Fragment
    }

    internal static class DamageTypeMap
    {
        internal static ApiDamageType From(DamageType t)
        {
            switch (t)
            {
                case DamageType.Kinetic: return ApiDamageType.Kinetic;
                case DamageType.Crash: return ApiDamageType.Crash;
                case DamageType.Explosive: return ApiDamageType.Explosive;
                case DamageType.Emp: return ApiDamageType.Emp;
                case DamageType.Laser: return ApiDamageType.Laser;
                case DamageType.Particle: return ApiDamageType.Particle;
                case DamageType.Plasma: return ApiDamageType.Plasma;
                case DamageType.Fire: return ApiDamageType.Fire;
                default: return ApiDamageType.Other;
            }
        }
    }

    /// <summary>
    /// Damage about to be dealt to one block. Change <see cref="Damage"/> to scale or replace it.
    /// <para>
    /// Runs for every damage type at the point where the game subtracts health from a block, on the
    /// server (or in single player) only, and possibly on a worker thread: do not touch Unity objects,
    /// and do not keep a reference to the context after your modifier returns (it is reused).
    /// </para>
    /// <para>
    /// This changes the health removed from this block only. It does not change how far a shell
    /// penetrates or the damage numbers shown on the HUD; use <see cref="DamageApi.ProjectileLaunch"/>
    /// or <see cref="DamageApi.Explosion"/> to change a weapon's actual output.
    /// </para>
    /// </summary>
    public sealed class BlockDamageContext
    {
        [ThreadStatic] private static BlockDamageContext _cached;

        private bool _inUse;

        private float? _armourClass;
        private MainConstruct _construct;
        private bool _constructResolved;

        private BlockDamageContext()
        {
        }

        internal static BlockDamageContext Rent(Block block, IDamageDescription dd, float damage)
        {
            var c = _cached != null && !_cached._inUse ? _cached : (_cached == null ? (_cached = new BlockDamageContext()) : new BlockDamageContext());
            c._inUse = true;
            c.Block = block;
            c.Description = dd;
            c.OriginalDamage = damage;
            c.Damage = damage;
            c.DamageType = DamageTypeMap.From(dd.DamageType);
            c._armourClass = null;
            c._construct = null;
            c._constructResolved = false;
            return c;
        }

        internal void Release()
        {
            _inUse = false;
            Block = null;
            Description = null;
            _construct = null;
        }

        /// <summary>Health that will be removed from the block. Change this. Negative values are treated as 0.</summary>
        public float Damage { get; set; }

        /// <summary>The game's own figure before any modifier ran.</summary>
        public float OriginalDamage { get; private set; }

        public ApiDamageType DamageType { get; private set; }

        /// <summary>True for explosions and explosion-like effects (including blast force callbacks).</summary>
        public bool IsExplosion => DamageType == ApiDamageType.Explosive || (Description != null && Description.IsExplosion);

        public float CurrentHealth => Block.CurrentHealth;

        public float MaximumHealth => Block.MaximumHealth;

        /// <summary>Armour class the game used for this hit (fire resistance for lasers, as the game does).</summary>
        public float ArmourClass
        {
            get
            {
                if (!_armourClass.HasValue)
                {
                    _armourClass = DamageType == ApiDamageType.Laser
                        ? Block.item.ExtraSettings.FireResistance
                        : Block.GetArmourClass();
                }
                return _armourClass.Value;
            }
        }

        /// <summary>The attack's armour piercing value.</summary>
        public float ArmourPierce => Description.ArmourPierce;

        /// <summary>
        /// Damage potential the attack has left after this block (already reduced by the game).
        /// For shells this is the remaining kinetic energy.
        /// </summary>
        public float RemainingPotential => Description.DamagePotential;

        /// <summary>Name of the block's item, e.g. "Metal Block". Null if unknown.</summary>
        public string BlockName => Block.item?.ComponentId?.Name;

        /// <summary>Guid of the block's item definition; stable across saves and game versions.</summary>
        public Guid BlockGuid => Block.item?.ComponentId?.Guid ?? Guid.Empty;

        /// <summary>World position of the block.</summary>
        public Vector3 Position => Block.GameWorldPosition;

        /// <summary>Who caused the damage, or null (some environmental damage has no attacker).</summary>
        public IDamageLogger Attacker => Description.DamageLogger;

        /// <summary>Team of the attacker, or null.</summary>
        public ObjectId AttackerTeam => Description.DamageLogger?.FactionId;

        /// <summary>The vehicle or structure being hit. Null for blocks not on a main construct.</summary>
        public MainConstruct TargetConstruct
        {
            get
            {
                if (!_constructResolved)
                {
                    _construct = Block.MainConstruct as MainConstruct;
                    _constructResolved = true;
                }
                return _construct;
            }
        }

        /// <summary>Team of the construct being hit, or null.</summary>
        public ObjectId TargetTeam => TargetConstruct?.GetTeam();

        /// <summary>True when attacker and target are on the same team.</summary>
        public bool IsFriendlyFire
        {
            get
            {
                var a = AttackerTeam;
                var t = TargetTeam;
                return !ReferenceEquals(a, null) && !ReferenceEquals(t, null) && a == t;
            }
        }

        /// <summary>Raw game block. Unstable: its members can change in any game update.</summary>
        public Block Block { get; private set; }

        /// <summary>Raw game damage description. Unstable: its members can change in any game update.</summary>
        public IDamageDescription Description { get; private set; }
    }

    /// <summary>
    /// A block just lost health. Raised after the game applied the damage, on the server (or in single
    /// player), possibly on a worker thread. Do not keep a reference to it: it is reused.
    /// </summary>
    public sealed class BlockDamagedArgs
    {
        [ThreadStatic] private static BlockDamagedArgs _cached;

        private bool _inUse;

        private BlockDamagedArgs()
        {
        }

        internal static BlockDamagedArgs Rent(Block block, IDamageDescription dd, float before, float after)
        {
            var a = _cached != null && !_cached._inUse ? _cached : (_cached == null ? (_cached = new BlockDamagedArgs()) : new BlockDamagedArgs());
            a._inUse = true;
            a.Block = block;
            a.Description = dd;
            a.HealthBefore = before;
            a.HealthAfter = after;
            a.DamageType = DamageTypeMap.From(dd.DamageType);
            return a;
        }

        internal void Release()
        {
            _inUse = false;
            Block = null;
            Description = null;
        }

        public ApiDamageType DamageType { get; private set; }

        public float HealthBefore { get; private set; }

        public float HealthAfter { get; private set; }

        public float DamageDealt => HealthBefore - HealthAfter;

        /// <summary>
        /// True when this hit took the block to zero. The block itself is removed slightly later, on the main thread.
        /// </summary>
        public bool Destroyed => HealthAfter <= 0.005f;

        public string BlockName => Block.item?.ComponentId?.Name;

        public Guid BlockGuid => Block.item?.ComponentId?.Guid ?? Guid.Empty;

        public IDamageLogger Attacker => Description.DamageLogger;

        public ObjectId AttackerTeam => Description.DamageLogger?.FactionId;

        public MainConstruct TargetConstruct => Block.MainConstruct as MainConstruct;

        /// <summary>Raw game block. Unstable.</summary>
        public Block Block { get; private set; }

        /// <summary>Raw game damage description. Unstable.</summary>
        public IDamageDescription Description { get; private set; }
    }

    /// <summary>
    /// A kinetic projectile (shell, bullet or fragment) is being launched. Changing <see cref="KineticDamage"/>
    /// and <see cref="ArmourPiercing"/> here changes the projectile itself, so it affects penetration
    /// through every block it hits. Runs on server and clients alike.
    /// </summary>
    public sealed class ProjectileLaunchContext
    {
        [ThreadStatic] private static ProjectileLaunchContext _cached;

        private bool _inUse;

        private ProjectileLaunchContext()
        {
        }

        internal static ProjectileLaunchContext Rent(float kinetic, float ap, Vector3 velocity, IDamageLogger gunner, ProjectileSource source)
        {
            var c = _cached != null && !_cached._inUse ? _cached : (_cached == null ? (_cached = new ProjectileLaunchContext()) : new ProjectileLaunchContext());
            c._inUse = true;
            c.KineticDamage = kinetic;
            c.OriginalKineticDamage = kinetic;
            c.ArmourPiercing = ap;
            c.OriginalArmourPiercing = ap;
            c.Velocity = velocity;
            c.Attacker = gunner;
            c.Source = source;
            return c;
        }

        internal void Release()
        {
            _inUse = false;
            Attacker = null;
        }

        /// <summary>Kinetic damage the projectile starts with. Change this.</summary>
        public float KineticDamage { get; set; }

        /// <summary>Armour piercing. Change this.</summary>
        public float ArmourPiercing { get; set; }

        public float OriginalKineticDamage { get; private set; }

        public float OriginalArmourPiercing { get; private set; }

        /// <summary>Launch velocity (world space, m/s).</summary>
        public Vector3 Velocity { get; private set; }

        public float Speed => Velocity.magnitude;

        public ProjectileSource Source { get; private set; }

        /// <summary>
        /// Who fired it. Only fragments know this at launch; for shells the game attaches the firer a frame later,
        /// so this is usually null.
        /// </summary>
        public IDamageLogger Attacker { get; private set; }
    }

    /// <summary>
    /// An explosion is about to be applied to the world: HE shells, missiles, warheads, ammo and fuel
    /// cook-offs, exploding blocks. Change <see cref="Damage"/> and <see cref="Radius"/>.
    /// Runs on the server (or in single player).
    /// </summary>
    public sealed class ExplosionContext
    {
        [ThreadStatic] private static ExplosionContext _cached;

        private bool _inUse;

        private ExplosionContext()
        {
        }

        internal static ExplosionContext Rent(ExplosionDamageDescription dd, bool hasSourceConstruct)
        {
            var c = _cached != null && !_cached._inUse ? _cached : (_cached == null ? (_cached = new ExplosionContext()) : new ExplosionContext());
            c._inUse = true;
            c.Description = dd;
            c.Damage = dd.DamagePotential;
            c.OriginalDamage = dd.DamagePotential;
            c.Radius = dd.Radius;
            c.OriginalRadius = dd.Radius;
            c.HasSourceConstruct = hasSourceConstruct;
            return c;
        }

        internal void Release()
        {
            _inUse = false;
            Description = null;
        }

        /// <summary>Explosive damage potential. Change this.</summary>
        public float Damage { get; set; }

        /// <summary>Blast radius in metres. Change this. The game draws its own visual effect before this runs, so the visual keeps its original size.</summary>
        public float Radius { get; set; }

        public float OriginalDamage { get; private set; }

        public float OriginalRadius { get; private set; }

        public Vector3 Position => Description.Position;

        /// <summary>True for flak (proximity-fused) explosions.</summary>
        public bool IsFlak => Description.IsFlak;

        /// <summary>True when the game passed the construct the explosion started on (e.g. a shell impact or an exploding block).</summary>
        public bool HasSourceConstruct { get; private set; }

        public IDamageLogger Attacker => Description.DamageLogger;

        public ObjectId AttackerTeam => Description.DamageLogger?.FactionId;

        /// <summary>Raw game description. Unstable.</summary>
        public ExplosionDamageDescription Description { get; private set; }
    }
}

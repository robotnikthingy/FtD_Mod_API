using System;
using System.Collections.Generic;
using UnityEngine;

namespace FtdModApi
{
    /// <summary>The game's five CRAM pellet kinds.</summary>
    public enum CramPelletKind
    {
        /// <summary>Hardener pellets: kinetic damage and armour piercing.</summary>
        Hardener = 0,
        /// <summary>High explosive pellets.</summary>
        Explosive = 1,
        /// <summary>Fragmentation pellets.</summary>
        Fragmentation = 2,
        /// <summary>EMP pellets.</summary>
        Emp = 3,
        /// <summary>Incendiary pellets.</summary>
        Incendiary = 4
    }

    /// <summary>
    /// Hooks into the CRAM cannon. Feature ids: <see cref="Features.CramShellStats"/> and
    /// <see cref="Features.CramPellets"/>.
    /// </summary>
    /// <remarks>
    /// To add a new pellet block, see <see cref="CramPellets"/>.
    /// </remarks>
    public static class CramApi
    {
        /// <summary>
        /// A CRAM shell's stats, worked out from the gun's pellets and how long it has packed.
        /// Change the context's values to change the shell. Feature id: <see cref="Features.CramShellStats"/>.
        /// </summary>
        /// <remarks>
        /// The game asks for these stats when the gun fires, and also for the gun's tooltip, its firepower
        /// rating and the AI's fire control. So a modifier must give the same result every time for the same input:
        /// change values, do not count shots or keep state here (use <see cref="DamageApi.ProjectileLaunch"/>
        /// with <see cref="ProjectileSource.Cram"/> to react to actual shots). It may run on a worker thread.
        /// </remarks>
        public static ModifierPipeline<CramShellContext> ShellStats { get; } =
            new ModifierPipeline<CramShellContext>(Features.CramShellStats);
    }

    /// <summary>
    /// One CRAM shell's stats while it is being worked out. Only the settable values change the shell.
    /// Do not keep a reference to the context after your modifier returns (it is reused).
    /// </summary>
    public sealed class CramShellContext
    {
        [ThreadStatic] private static CramShellContext _cached;

        private bool _inUse;
        private CramPelletCount[] _modded;

        private CramShellContext()
        {
        }

        internal static CramShellContext Rent(RailwayGunStats stats, RailwayGunShellStats shell, float packTime, CramPelletCount[] modded)
        {
            var c = _cached != null && !_cached._inUse ? _cached : (_cached == null ? (_cached = new CramShellContext()) : new CramShellContext());
            c._inUse = true;
            c.GameStats = stats;
            c._modded = modded ?? CramPelletCount.None;
            c.PackTime = packTime;
            c.ShellDiameter = stats.ShellDiameter;
            c.HardenerPellets = stats.HardPerSec;
            c.ExplosivePellets = stats.ExpPerSec;
            c.FragmentationPellets = stats.FragPerSec;
            c.EmpPellets = stats.EmpPerSec;
            c.IncendiaryPellets = stats.FirePerSec;
            c.TotalPayload = shell.TotalPayload;
            c.AmmoCost = shell.AmmoCost;
            c.IsImpactFused = shell.IsImpactDamage;
            c.KineticDamage = shell.KineticDamage;
            c.ArmourPiercing = shell.ArmourPiercing;
            c.Health = shell.Health;
            c.ExplosivePower = shell.HePower;
            c.FragmentationPower = shell.FragPower;
            c.FragmentationAngle = shell.FragAngle;
            c.EmpDamage = shell.EmpDamage;
            c.FireFuel = shell.FireFuel;
            c.FireIntensity = shell.FireIntensity;
            c.FireOxidizer = shell.FireOxidizer;
            return c;
        }

        internal void WriteBack(RailwayGunShellStats shell)
        {
            shell.KineticDamage = Mathf.Max(0f, KineticDamage);
            shell.ArmourPiercing = Mathf.Max(0f, ArmourPiercing);
            shell.Health = Health;
            shell.HePower = Mathf.Max(0f, ExplosivePower);
            shell.FragPower = Mathf.Max(0f, FragmentationPower);
            shell.FragAngle = FragmentationAngle;
            shell.EmpDamage = Mathf.Max(0f, EmpDamage);
            shell.FireFuel = Mathf.Max(0f, FireFuel);
            shell.FireIntensity = FireIntensity;
            shell.FireOxidizer = Mathf.Max(0f, FireOxidizer);
        }

        internal void Release()
        {
            _inUse = false;
            GameStats = null;
            _modded = null;
        }

        // ---- Read-only inputs -------------------------------------------------------------

        /// <summary>Seconds of packing this shell represents (capped at the gun's maximum).</summary>
        public float PackTime { get; private set; }

        /// <summary>Shell diameter in metres.</summary>
        public float ShellDiameter { get; private set; }

        /// <summary>Hardener pellets connected (including modded pellets based on hardener).</summary>
        public int HardenerPellets { get; private set; }

        /// <summary>HE pellets connected (including modded pellets based on HE).</summary>
        public int ExplosivePellets { get; private set; }

        /// <summary>Frag pellets connected (including modded pellets based on frag).</summary>
        public int FragmentationPellets { get; private set; }

        /// <summary>EMP pellets connected (including modded pellets based on EMP).</summary>
        public int EmpPellets { get; private set; }

        /// <summary>Incendiary pellets connected (including modded pellets based on incendiary).</summary>
        public int IncendiaryPellets { get; private set; }

        /// <summary>Total payload packed into the shell.</summary>
        public float TotalPayload { get; private set; }

        /// <summary>Ammo used to pack this shell (read-only: the game takes ammo while packing).</summary>
        public float AmmoCost { get; private set; }

        /// <summary>True when the fusing box is set to impact (the game then reduces kinetic damage and AP).</summary>
        public bool IsImpactFused { get; private set; }

        /// <summary>How many of a modded pellet (<see cref="CramPellets"/>) are connected to this gun.</summary>
        public int ModdedPelletCount(string pelletId)
        {
            foreach (var p in _modded)
            {
                if (string.Equals(p.Pellet.Id, pelletId, StringComparison.Ordinal)) return p.Count;
            }
            return 0;
        }

        /// <summary>Every modded pellet connected to this gun, with counts.</summary>
        public IReadOnlyList<CramPelletCount> ModdedPellets => _modded;

        /// <summary>
        /// The game's own stats object for this gun. Raw game type: may change between game versions,
        /// so prefer the properties above.
        /// </summary>
        public RailwayGunStats GameStats { get; private set; }

        // ---- Values you can change --------------------------------------------------------

        /// <summary>Kinetic damage of the shell.</summary>
        public float KineticDamage { get; set; }

        /// <summary>Armour piercing of the shell.</summary>
        public float ArmourPiercing { get; set; }

        /// <summary>Shell health (how much anti-munition damage it takes to destroy it). 0 or less: the gun will not fire.</summary>
        public float Health { get; set; }

        /// <summary>HE payload power (the game turns this into explosion damage and radius).</summary>
        public float ExplosivePower { get; set; }

        /// <summary>Frag payload power.</summary>
        public float FragmentationPower { get; set; }

        /// <summary>Frag cone angle in degrees (normally the player's setting).</summary>
        public float FragmentationAngle { get; set; }

        /// <summary>EMP damage.</summary>
        public float EmpDamage { get; set; }

        /// <summary>Incendiary fuel.</summary>
        public float FireFuel { get; set; }

        /// <summary>Incendiary intensity (normally from the player's setting).</summary>
        public float FireIntensity { get; set; }

        /// <summary>Incendiary oxidizer.</summary>
        public float FireOxidizer { get; set; }
    }

    /// <summary>How many of one modded pellet are connected to a CRAM gun.</summary>
    public readonly struct CramPelletCount
    {
        internal static readonly CramPelletCount[] None = new CramPelletCount[0];

        internal CramPelletCount(CramPelletInfo pellet, int count, int[] byKind)
        {
            Pellet = pellet;
            Count = count;
            ByKind = byKind;
        }

        public CramPelletInfo Pellet { get; }

        /// <summary>Connections counted (a pellet touching two packers counts twice, like the game's own pellets).</summary>
        public int Count { get; }

        /// <summary>How many of those counted towards each game pellet kind, indexed by <see cref="CramPelletKind"/>.</summary>
        internal int[] ByKind { get; }
    }
}

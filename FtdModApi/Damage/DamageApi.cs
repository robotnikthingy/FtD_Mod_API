namespace FtdModApi
{
    /// <summary>
    /// Hooks into how damage is created and applied. All weapon types share these, so one modifier
    /// covers APS, CRAM, missiles, lasers, PACs, plasma, fire and simple weapons alike.
    /// </summary>
    /// <remarks>
    /// <para>Where each hook sits in the game:</para>
    /// <list type="bullet">
    /// <item><see cref="ProjectileLaunch"/>: when a shell, bullet or fragment is launched (its starting kinetic damage and AP).</item>
    /// <item><see cref="Explosion"/>: when any explosion is created (its damage and radius).</item>
    /// <item><see cref="Block"/>: when any damage is about to be subtracted from one block's health.</item>
    /// <item><see cref="BlockDamaged"/>: after a block lost health.</item>
    /// </list>
    /// <para>
    /// Check <see cref="Features.IsAvailable"/> with the matching <see cref="Features"/> id if your mod
    /// cannot work without a hook. After a game update a hook may be switched off rather than misbehave.
    /// </para>
    /// <para>
    /// Damage is decided by the server. In multiplayer every player should run the same mods with the
    /// same settings, or the clients will see shells behave differently from what the server applies.
    /// </para>
    /// </remarks>
    public static class DamageApi
    {
        /// <summary>
        /// Damage about to be removed from a single block, for every damage type.
        /// Feature id: <see cref="Features.BlockDamage"/>.
        /// </summary>
        public static ModifierPipeline<BlockDamageContext> Block { get; } =
            new ModifierPipeline<BlockDamageContext>(Features.BlockDamage);

        /// <summary>
        /// Raised after a block lost health. Feature id: <see cref="Features.BlockDamagedEvent"/>.
        /// </summary>
        public static ApiEvent<BlockDamagedArgs> BlockDamaged { get; } =
            new ApiEvent<BlockDamagedArgs>(Features.BlockDamagedEvent);

        /// <summary>
        /// A kinetic projectile's starting damage and armour piercing. This is the place for
        /// "+20% APS kinetic damage" style changes. Feature id: <see cref="Features.ProjectileLaunch"/>.
        /// </summary>
        public static ModifierPipeline<ProjectileLaunchContext> ProjectileLaunch { get; } =
            new ModifierPipeline<ProjectileLaunchContext>(Features.ProjectileLaunch);

        /// <summary>
        /// An explosion's damage and radius, before it is applied to anything.
        /// Feature id: <see cref="Features.Explosion"/>.
        /// </summary>
        public static ModifierPipeline<ExplosionContext> Explosion { get; } =
            new ModifierPipeline<ExplosionContext>(Features.Explosion);
    }
}

using BrilliantSkies.Ftd.Missiles;
using BrilliantSkies.Ftd.Missiles.Components;
using BrilliantSkies.Ftd.Missiles.Ui;

namespace ExampleBalanceMod
{
    /// <summary>
    /// Example code-based missile part: a lighter reinforced body. It derives from the game's body, so the game
    /// treats it as a body everywhere; it only changes what it overrides. Registered in ExampleBalancePlugin.
    /// </summary>
    public class ExampleLightBody : MissileBody
    {
        /// <summary>Tells the game's own part scan to skip this class: FtdModApi gives it its number.</summary>
        public new const enumMissileComponentType ComponentType = enumMissileComponentType.None;

        public ExampleLightBody(MissileSize size, UIParameterBag bag)
            : base(size, bag)
        {
        }

        public override string Name => "Light reinforced body";

        public override string Description => "A lighter reinforced body: less health, 40% less mass. (Example part from FtdModApi.)";

        public override float HealthModifier => 1.0f;

        protected override float MassModifier => 0.6f;
    }
}

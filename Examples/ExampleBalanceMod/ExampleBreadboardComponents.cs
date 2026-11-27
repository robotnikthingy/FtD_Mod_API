using FtdModApi;

namespace ExampleBalanceMod
{
    /// <summary>
    /// Example breadboard component: multiplies its input by a factor, and can keep the result within limits.
    /// Shows inputs, outputs, number and toggle settings, and a custom label.
    /// </summary>
    public class ScaleAndLimit : ModBreadboardComponent
    {
        private readonly NumberSetting _factor;
        private readonly ToggleSetting _limit;
        private readonly NumberSetting _min;
        private readonly NumberSetting _max;

        public ScaleAndLimit()
        {
            AddInput("Value", "The number to scale");
            AddOutput("Result", "Value x factor, kept between min and max when limiting is on");
            _factor = AddNumberSetting("factor", "Factor", 2, -10, 10, 0.1, "What the input is multiplied by");
            _limit = AddToggleSetting("limit", "Limit the result", false, "Keep the result between min and max");
            _min = AddNumberSetting("min", "Min", -1, -1000, 1000, 1);
            _max = AddNumberSetting("max", "Max", 1, -1000, 1000, 1);
        }

        protected override string Label => _limit.Value
            ? $"x{_factor.Value:0.##} [{_min.Value:0.##}..{_max.Value:0.##}]"
            : $"x{_factor.Value:0.##}";

        protected override void Compute()
        {
            double v = InNumber(0) * _factor.Value;
            if (_limit.Value)
            {
                double lo = System.Math.Min(_min.Value, _max.Value), hi = System.Math.Max(_min.Value, _max.Value);
                v = System.Math.Max(lo, System.Math.Min(hi, v));
            }
            Out(0, v);
        }
    }

    /// <summary>
    /// Example breadboard component with no inputs: the craft's speed and altitude, and the highest speed seen.
    /// Shows reading the craft (Construct) and memory between runs (Remember/Recall).
    /// </summary>
    public class CraftStatus : ModBreadboardComponent
    {
        public CraftStatus()
        {
            AddOutput("Speed", "Speed in m/s");
            AddOutput("Altitude", "Height of the centre of mass above sea level, in metres");
            AddOutput("Top speed", "Highest speed seen since the breadboard was placed or loaded");
        }

        protected override void Compute()
        {
            var c = Construct;
            if (c == null)
            {
                Out(0, 0.0);
                Out(1, 0.0);
                Out(2, Recall(0));
                return;
            }
            double speed = c.PartPhysics.Velocities.VelocityVector.magnitude;
            double top = System.Math.Max(Recall(0), speed);
            Remember(0, top);
            Out(0, speed);
            Out(1, c.AltitudeOfComAboveMeanSeaLevel);
            Out(2, top);
        }
    }
}

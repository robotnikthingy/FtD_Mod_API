using System.Threading;
using FtdModApi;

namespace ExampleBalanceMod
{
    /// <summary>
    /// Example craft module: counts how many times a craft has spawned and how many blocks it has lost. The counts are
    /// saved with the craft (blueprint, campaign save, out of play and back), so a design saved to a file carries its
    /// counts to every craft spawned from it.
    /// </summary>
    public class ExampleCraftLog : CraftModule
    {
        private int _spawns;
        private int _blocksLost;

        protected override void OnSpawned(string savedData)
        {
            // Saved as "spawns;blocksLost".
            if (savedData != null)
            {
                var parts = savedData.Split(';');
                if (parts.Length == 2)
                {
                    int.TryParse(parts[0], out _spawns);
                    int.TryParse(parts[1], out _blocksLost);
                }
            }
            _spawns++;

            // CraftData: a plain value next to the module's own data, set once and kept for good.
            if (!CraftData.Has(Craft, "ExampleBalanceMod", "firstSeen"))
                CraftData.Set(Craft, "ExampleBalanceMod", "firstSeen", System.DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

            FtdApi.GetLog("ExampleBalanceMod").Info(
                $"Craft log: '{Craft.GetName()}' spawned (time {_spawns}, {_blocksLost} block(s) lost so far, first seen {CraftData.Get(Craft, "ExampleBalanceMod", "firstSeen")}).");
        }

        // Block events can run on worker threads, hence Interlocked.
        public void BlockLost() => Interlocked.Increment(ref _blocksLost);

        protected override string OnSave() => _spawns + ";" + Volatile.Read(ref _blocksLost);

        protected override void OnRemoved()
        {
            FtdApi.GetLog("ExampleBalanceMod").Info($"Craft log: '{Craft.GetName()}' removed ({_spawns} spawn(s), {_blocksLost} block(s) lost; saved with the craft).");
        }
    }
}

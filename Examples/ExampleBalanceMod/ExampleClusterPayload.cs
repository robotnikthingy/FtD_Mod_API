using System.Collections.Generic;
using System.Threading;
using FtdModApi;
using UnityEngine;

namespace ExampleBalanceMod
{
    /// <summary>
    /// Example custom APS payload: instead of one HE blast, the "Cluster charge" body sets off four smaller blasts
    /// in a line ahead of where the shell detonates. Registered in ExampleBalancePlugin.
    /// </summary>
    public sealed class ExampleClusterPayload : ApsPayload
    {
        private const int Blasts = 4;
        private const float Spacing = 1.5f; // metres between blasts

        private static int _logged;

        public override void Detonate(ApsPayloadHit hit)
        {
            // Same total strength as the same number of HE bodies, split into four blasts.
            float damage = ApsPayloads.HeDamage(hit.Strength / Blasts);
            float radius = ApsPayloads.ExplosionRadius(damage);
            for (int i = 0; i < Blasts; i++)
            {
                hit.ExplosionAt(hit.Position + hit.Direction * (i * Spacing), damage, radius);
            }

            // Detonate runs on a worker thread; ModLog is safe to use from there. Log the first few only.
            if (Interlocked.Increment(ref _logged) <= 3)
            {
                FtdApi.GetLog("ExampleBalanceMod").Info(
                    $"Cluster charge ({hit.Count} part(s), strength {hit.Strength:0.##}) went off ({hit.Reason}): {Blasts} blasts of {damage * hit.DamageFactor:0} damage, radius {radius:0.0} m"
                    + (hit.Craft != null ? $", on '{hit.Craft.GetName()}'." : ", in the open."));
            }
        }

        public override IEnumerable<ApsPayloadStat> Describe(ApsPayloadShell shell)
        {
            float damage = ApsPayloads.HeDamage(shell.Strength / Blasts);
            yield return new ApsPayloadStat("Cluster blasts", $"{Blasts} x {damage:0} damage",
                "Four HE blasts in a line, 1.5 m apart, starting where the shell detonates.");
            yield return new ApsPayloadStat("Blast radius", $"{ApsPayloads.ExplosionRadius(damage):0.0} m",
                "Radius of each blast.");
        }
    }
}

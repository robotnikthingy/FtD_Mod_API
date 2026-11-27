using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace FtdModApi.Patching
{
    /// <summary>
    /// Applies the API's Harmony patches one at a time. A patch that cannot find its target, throws while
    /// patching, or fails its own check is rolled back and reported, and only the feature that depends
    /// on it is switched off. The rest of the API keeps working.
    /// </summary>
    internal static class PatchManager
    {
        internal const string HarmonyId = "ftdmodapi.core";

        private static Harmony _harmony;
        private static readonly List<PatchResult> _results = new List<PatchResult>();

        internal static IReadOnlyList<PatchResult> Results
        {
            get
            {
                lock (_results) return _results.ToList();
            }
        }

        internal static Harmony Harmony => _harmony ?? (_harmony = new Harmony(HarmonyId));

        /// <summary>Applies the specs and returns their results (also kept in <see cref="Results"/>).</summary>
        internal static List<PatchResult> Apply(IEnumerable<PatchSpec> specs)
        {
            var batch = specs.ToList();
            var log = FtdApi.Log;
            var batchResults = new List<PatchResult>();

            foreach (var spec in batch)
            {
                var result = ApplyOne(spec);
                batchResults.Add(result);
                lock (_results) _results.Add(result);
                if (!result.Success)
                {
                    if (spec.Required) log.Error("Patch failed: " + result);
                    else log.Warning("Optional patch failed: " + result);
                }
            }

            UpdateFeatureStates(batch.Select(s => s.FeatureId).Distinct());
            return batchResults;
        }

        private static PatchResult ApplyOne(PatchSpec spec)
        {
            List<MethodBase> targets;
            try
            {
                targets = spec.Targets != null
                    ? spec.Targets().Where(t => t != null).Distinct().ToList()
                    : new List<MethodBase> { spec.Target() };
            }
            catch (Exception e)
            {
                return Fail(spec, null, "target not found (" + e.GetType().Name + ": " + e.Message + ")");
            }
            if (targets.Count == 0 || targets[0] == null) return Fail(spec, null, "target not found");

            var targetName = targets.Count == 1 ? Describe(targets[0]) : $"{targets.Count} methods";
            var patched = new List<MethodBase>();
            foreach (var target in targets)
            {
                try
                {
                    Harmony.Patch(
                        target,
                        prefix: spec.Prefix != null ? new HarmonyMethod(spec.Prefix) : null,
                        postfix: spec.Postfix != null ? new HarmonyMethod(spec.Postfix) : null,
                        transpiler: spec.Transpiler != null ? new HarmonyMethod(spec.Transpiler) : null,
                        finalizer: spec.Finalizer != null ? new HarmonyMethod(spec.Finalizer) : null);
                    patched.Add(target);
                }
                catch (Exception e)
                {
                    Rollback(spec, target);
                    foreach (var done in patched) Rollback(spec, done);
                    return Fail(spec, targetName, "Harmony could not patch " + Describe(target) + " (" + e.GetType().Name + ": " + e.Message + ")");
                }
            }

            string verifyError = null;
            if (spec.Verify != null)
            {
                try
                {
                    verifyError = spec.Verify();
                }
                catch (Exception e)
                {
                    verifyError = "check threw " + e.GetType().Name + ": " + e.Message;
                }
            }
            if (verifyError != null)
            {
                foreach (var done in patched) Rollback(spec, done);
                return Fail(spec, targetName, verifyError);
            }

            FtdApi.Log.Verbose(() => $"Patched '{spec.Id}' ({spec.FeatureId}): " + string.Join(", ", patched.Select(Describe)));
            return new PatchResult(spec.Id, spec.FeatureId, spec.Required, true, targetName, null);
        }

        private static void Rollback(PatchSpec spec, MethodBase target)
        {
            foreach (var m in new[] { spec.Prefix, spec.Postfix, spec.Transpiler, spec.Finalizer })
            {
                if (m == null) continue;
                try
                {
                    Harmony.Unpatch(target, m);
                }
                catch
                {
                    // Nothing was applied for this method.
                }
            }
        }

        private static PatchResult Fail(PatchSpec spec, string target, string error)
            => new PatchResult(spec.Id, spec.FeatureId, spec.Required, false, target, error);

        private static void UpdateFeatureStates(IEnumerable<string> featureIds)
        {
            var all = Results;
            foreach (var id in featureIds)
            {
                var feature = Features.Get(id);
                if (feature == null) continue;
                var mine = all.Where(r => r.FeatureId == id).ToList();
                var requiredFailures = mine.Where(r => !r.Success && r.Required).ToList();
                var optionalFailures = mine.Where(r => !r.Success && !r.Required).ToList();

                if (requiredFailures.Count > 0)
                {
                    feature.State = FeatureState.Unavailable;
                    feature.Detail = string.Join("; ", requiredFailures.Select(r => r.Id + ": " + r.Error));
                }
                else if (optionalFailures.Count > 0)
                {
                    feature.State = FeatureState.Degraded;
                    feature.Detail = string.Join("; ", optionalFailures.Select(r => r.Id + ": " + r.Error));
                }
                else
                {
                    feature.State = FeatureState.Available;
                    feature.Detail = string.Empty;
                }
            }
        }

        internal static string Describe(MethodBase m)
        {
            var parameters = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
            return (m.DeclaringType != null ? m.DeclaringType.Name + "." : string.Empty) + m.Name + "(" + parameters + ")";
        }
    }
}

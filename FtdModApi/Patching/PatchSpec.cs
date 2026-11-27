using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace FtdModApi.Patching
{
    /// <summary>
    /// One Harmony patch the API wants to apply, plus what it means if it fails.
    /// Patches are declared as data so the manager can apply each one in isolation, check it,
    /// and switch off only the feature that depends on it when a game update breaks it.
    /// </summary>
    internal sealed class PatchSpec
    {
        /// <summary>Unique, human-readable id, e.g. "Block.ApplyDamage (transpiler)".</summary>
        public string Id;

        /// <summary>The <see cref="Features"/> id this patch serves.</summary>
        public string FeatureId;

        /// <summary>
        /// Required patches take the whole feature down when they fail. Optional ones only mark it
        /// <see cref="FeatureState.Degraded"/> (e.g. the projectile "source" tags).
        /// </summary>
        public bool Required = true;

        /// <summary>
        /// Finds the game method to patch. Runs inside a try/catch: a removed type or member
        /// (TypeLoadException, MissingMethodException, null) just fails this patch.
        /// </summary>
        public Func<MethodBase> Target;

        /// <summary>
        /// Alternative to <see cref="Target"/> for one patch applied to several methods (e.g. a transpiler
        /// that rewrites the same instruction in a family of classes). All succeed or all are rolled back.
        /// </summary>
        public Func<IEnumerable<MethodBase>> Targets;

        public MethodInfo Prefix;
        public MethodInfo Postfix;
        public MethodInfo Transpiler;
        public MethodInfo Finalizer;

        /// <summary>
        /// Optional check run right after patching. Return null when OK, or a reason it failed
        /// (e.g. a transpiler could not find the instruction it injects after). A failed check unpatches.
        /// </summary>
        public Func<string> Verify;

        /// <summary>Resolves a method by name and exact parameter types, or throws with a clear message.</summary>
        public static MethodInfo Method(Type type, string name, params Type[] parameters)
        {
            var m = AccessTools.Method(type, name, parameters);
            if (m == null) throw new MissingMethodException(type.FullName, name);
            return m;
        }

        /// <summary>Resolves one of our own hook methods (always exists unless we made a typo).</summary>
        public static MethodInfo Hook(Type type, string name)
        {
            var m = AccessTools.Method(type, name);
            if (m == null) throw new MissingMethodException(type.FullName, name);
            return m;
        }
    }

    /// <summary>What happened to one <see cref="PatchSpec"/>.</summary>
    public sealed class PatchResult
    {
        internal PatchResult(string id, string featureId, bool required, bool success, string target, string error)
        {
            Id = id;
            FeatureId = featureId;
            Required = required;
            Success = success;
            Target = target;
            Error = error;
        }

        public string Id { get; }
        public string FeatureId { get; }
        public bool Required { get; }
        public bool Success { get; }
        /// <summary>The resolved game method, e.g. "Block.ApplyDamage(IDamageDescription, Single)".</summary>
        public string Target { get; }
        public string Error { get; }

        public override string ToString()
            => (Success ? "OK    " : (Required ? "FAIL  " : "WARN  ")) + Id
               + (Target != null ? "  ->  " + Target : string.Empty)
               + (Error != null ? "  :: " + Error : string.Empty);
    }
}

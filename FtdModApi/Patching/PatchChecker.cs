using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using FtdModApi.Patching;
using HarmonyLib;

namespace FtdModApi
{
    /// <summary>How one patch fared in <see cref="PatchChecker"/>.</summary>
    public enum PatchCheckStatus
    {
        /// <summary>Target found and everything the patch relies on matches.</summary>
        Ok,

        /// <summary>An optional patch is broken: its feature still works, with less detail.</summary>
        Warning,

        /// <summary>A required patch is broken: its feature (or event) won't work on this game build.</summary>
        Broken,

        /// <summary>Couldn't be checked here (for example a game assembly that only loads inside the game).</summary>
        Unknown
    }

    /// <summary>One patch in a <see cref="PatchCheckReport"/>.</summary>
    public sealed class PatchCheck
    {
        internal PatchCheck(string source, string id, string featureId, bool required)
        {
            Source = source;
            Id = id;
            FeatureId = featureId;
            Required = required;
        }

        /// <summary>Where the patch comes from: "startup", "main menu", "event", "Lua", "breadboards".</summary>
        public string Source { get; }

        public string Id { get; }

        public string FeatureId { get; }

        public bool Required { get; }

        public PatchCheckStatus Status { get; internal set; } = PatchCheckStatus.Ok;

        /// <summary>The game method(s) it patches, when found.</summary>
        public List<string> Targets { get; } = new List<string>();

        /// <summary>What is wrong (or couldn't be checked).</summary>
        public List<string> Problems { get; } = new List<string>();

        /// <summary>Notes that are not problems (e.g. a transpiler that can't be run outside the game).</summary>
        public List<string> Notes { get; } = new List<string>();

        internal void Fail(string problem)
        {
            Problems.Add(problem);
            var bad = Required ? PatchCheckStatus.Broken : PatchCheckStatus.Warning;
            if (Status == PatchCheckStatus.Ok || Status == PatchCheckStatus.Unknown || (Status == PatchCheckStatus.Warning && bad == PatchCheckStatus.Broken))
                Status = bad;
        }

        internal void CantCheck(string why)
        {
            Problems.Add(why);
            if (Status == PatchCheckStatus.Ok) Status = PatchCheckStatus.Unknown;
        }
    }

    /// <summary>The result of <see cref="PatchChecker.Run"/>.</summary>
    public sealed class PatchCheckReport
    {
        internal PatchCheckReport(string gameVersion, string apiVersion, bool offline)
        {
            GameVersion = gameVersion;
            ApiVersion = apiVersion;
            Offline = offline;
        }

        public string GameVersion { get; }

        public string ApiVersion { get; }

        /// <summary>True when run outside the game (the patch checker tool).</summary>
        public bool Offline { get; }

        public List<PatchCheck> Checks { get; } = new List<PatchCheck>();

        /// <summary>Problems gathering the patches themselves (a whole group that couldn't be built).</summary>
        public List<string> GatherErrors { get; } = new List<string>();

        public int Count(PatchCheckStatus status) => Checks.Count(c => c.Status == status);

        /// <summary>True when no required patch is broken and every group could be gathered.</summary>
        public bool AllRequiredOk => Count(PatchCheckStatus.Broken) == 0 && GatherErrors.Count == 0;

        /// <summary>A readable report: a summary, then each feature with its patches.</summary>
        public string ToText(bool includeOk = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"FtdModApi {ApiVersion} patch check against From The Depths {GameVersion}{(Offline ? " (offline)" : " (in game)")}");
            sb.AppendLine($"{Checks.Count} patches: {Count(PatchCheckStatus.Ok)} OK, {Count(PatchCheckStatus.Broken)} broken, {Count(PatchCheckStatus.Warning)} warnings, {Count(PatchCheckStatus.Unknown)} not checkable here.");
            sb.AppendLine(AllRequiredOk ? "Result: every required patch fits this game build." : "Result: SOME FEATURES WILL NOT WORK on this game build (see BROKEN below).");
            foreach (var e in GatherErrors) sb.AppendLine("GATHER ERROR: " + e);
            sb.AppendLine();
            foreach (var group in Checks.GroupBy(c => c.FeatureId ?? "(no feature)").OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var worst = group.Any(c => c.Status == PatchCheckStatus.Broken) ? "BROKEN"
                    : group.Any(c => c.Status == PatchCheckStatus.Warning) ? "WARN"
                    : group.Any(c => c.Status == PatchCheckStatus.Unknown) ? "UNKNOWN" : "OK";
                if (!includeOk && worst == "OK") continue;
                sb.AppendLine($"[{worst}] {group.Key}");
                foreach (var c in group)
                {
                    if (!includeOk && c.Status == PatchCheckStatus.Ok) continue;
                    var tag = c.Status == PatchCheckStatus.Ok ? "OK     " : c.Status == PatchCheckStatus.Broken ? "BROKEN " : c.Status == PatchCheckStatus.Warning ? "WARN   " : "UNKNOWN";
                    sb.AppendLine($"  {tag} {c.Id}  ({c.Source}{(c.Required ? "" : ", optional")})");
                    foreach (var t in c.Targets) sb.AppendLine("           -> " + t);
                    foreach (var p in c.Problems) sb.AppendLine("           !! " + p);
                    foreach (var n in c.Notes) sb.AppendLine("           .. " + n);
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Checks every patch the API can apply against the loaded game assemblies, without patching anything: that each
    /// target method still exists, that the parameter names and types the API's hooks rely on still match, and (outside
    /// the game) that each transpiler still finds the code it rewrites. Used by the offline PatchChecker tool after a
    /// game update; also usable in game.
    /// </summary>
    public static class PatchChecker
    {
        private static readonly Regex IndexParam = new Regex(@"^__(\d+)$");

        /// <summary>What the API logged during an offline run (it can't reach the game's log there).</summary>
        public static IReadOnlyList<string> OfflineLog
        {
            get { lock (ModLog.OfflineLines) return ModLog.OfflineLines.ToList(); }
        }

        /// <summary>
        /// Runs the check. offline: true when called from the PatchChecker tool, outside the game. Then the API also
        /// runs transpilers on the game methods' code (in game they have already run, and running them again would
        /// disturb their counters).
        /// </summary>
        public static PatchCheckReport Run(bool offline) => Run(offline, false);

        /// <summary>As <see cref="Run(bool)"/>; verbose also records the API's verbose lines in <see cref="OfflineLog"/>.</summary>
        public static PatchCheckReport Run(bool offline, bool verbose)
        {
            if (offline)
            {
                ModLog.Offline = true;
                if (verbose) ModLog.VerboseEnabled = true;
            }
            var report = new PatchCheckReport(SafeGameVersion(), FtdApi.Version.ToString(), offline);
            foreach (var (source, specs) in Gather(report))
            {
                foreach (var spec in specs)
                {
                    if (spec == null) continue;
                    report.Checks.Add(CheckOne(source, spec, offline));
                }
            }
            return report;
        }

        // Outside the game the game's version settings are never configured, so read the constants it starts from.
        private static string SafeGameVersion()
        {
            try
            {
                var t = Type.GetType("FtdVersion, Ftd");
                if (t != null)
                {
                    int Get(string name) => (int)(t.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? 0);
                    return $"{Get("Major")}.{Get("Minor")}.{Get("Subordinate")}";
                }
            }
            catch
            {
            }
            try
            {
                return FtdApi.GameVersion;
            }
            catch
            {
                return "unknown version";
            }
        }

        private static IEnumerable<(string, List<PatchSpec>)> Gather(PatchCheckReport report)
        {
            var groups = new List<(string, Func<IEnumerable<PatchSpec>>)>();
            try
            {
                foreach (var (name, build) in FtdModApi.Core.Bootstrap.StartupSources()) groups.Add(("startup: " + name, build));
            }
            catch (Exception e)
            {
                report.GatherErrors.Add("the startup patch list could not be read: " + Describe(e));
            }
            groups.Add(("main menu", () => ApsGauge.MainMenuSpecs()));
            groups.Add(("breadboards", () => BreadboardComponents.Specs()));
            groups.Add(("Lua", () => LuaFunctions.Specs()));
            foreach (var (source, build) in groups)
            {
                List<PatchSpec> specs = null;
                try
                {
                    specs = build().ToList();
                }
                catch (Exception e)
                {
                    report.GatherErrors.Add($"{source} patches could not be built: {Describe(e)}");
                }
                if (specs != null) yield return (source, specs);
            }

            List<EventHook> hooks = null;
            try
            {
                Events.TouchAll();
                hooks = EventRegistry.Hooks;
            }
            catch (Exception e)
            {
                report.GatherErrors.Add("event channels could not be created: " + Describe(e));
            }
            if (hooks == null) yield break;
            foreach (var hook in hooks)
            {
                List<PatchSpec> specs = null;
                try
                {
                    specs = hook.BuildSpecs()?.ToList();
                }
                catch (Exception e)
                {
                    report.GatherErrors.Add($"event hook for {hook.FeatureId} could not be built: {Describe(e)}");
                }
                if (specs != null) yield return ("event", specs);
            }
        }

        private static PatchCheck CheckOne(string source, PatchSpec spec, bool offline)
        {
            var check = new PatchCheck(source, spec.Id, spec.FeatureId, spec.Required);

            // 1. Targets.
            List<MethodBase> targets;
            try
            {
                targets = spec.Targets != null ? spec.Targets().ToList() : new List<MethodBase> { spec.Target?.Invoke() };
                // As the patch manager does: a family target may list a method twice; patch (and transpile) it once.
                if (spec.Targets != null) targets = targets.Where(t => t != null).Distinct().ToList();
            }
            catch (Exception e)
            {
                ClassifyException(check, "target not found", e);
                return check;
            }
            if (targets.Count == 0 || targets.Any(t => t == null))
            {
                check.Fail("target not found");
                return check;
            }
            foreach (var t in targets) check.Targets.Add(Describe(t));

            // 2. Our hooks against each target.
            foreach (var target in targets)
            {
                try
                {
                    CheckHook(check, "prefix", spec.Prefix, target, isPrefix: true);
                    CheckHook(check, "postfix", spec.Postfix, target, isPrefix: false);
                    CheckHook(check, "finalizer", spec.Finalizer, target, isPrefix: false);
                    CheckState(check, spec, target);
                }
                catch (Exception e)
                {
                    ClassifyException(check, "could not compare the hooks with " + Describe(target), e);
                }
            }

            // 3. Transpilers: run them on the original code (outside the game only), then the patch's own check.
            bool canVerify = true;
            if (spec.Transpiler != null)
            {
                if (!offline)
                {
                    check.Notes.Add("transpiler: already applied at startup in game; see the feature's state");
                    canVerify = false;
                }
                else
                {
                    foreach (var target in targets)
                    {
                        var why = RunTranspiler(spec.Transpiler, target);
                        if (why != null)
                        {
                            // Not checked is not OK: say so.
                            check.CantCheck("transpiler not run here (" + why + "); its check was skipped");
                            canVerify = false;
                        }
                    }
                }
            }
            if (spec.Verify != null && canVerify)
            {
                try
                {
                    var problem = spec.Verify();
                    if (problem != null) check.Fail("check failed: " + problem);
                }
                catch (Exception e)
                {
                    ClassifyException(check, "its check could not run", e);
                }
            }
            return check;
        }

        // Harmony's rules for injected parameters, so a renamed or retyped game parameter is caught before patching.
        private static void CheckHook(PatchCheck check, string kind, MethodInfo hook, MethodBase target, bool isPrefix)
        {
            if (hook == null) return;
            var targetParams = target.GetParameters();
            var returnType = (target as MethodInfo)?.ReturnType ?? typeof(void);
            var declaring = target.DeclaringType;
            var where = $"{kind} {hook.Name}";

            if (isPrefix && hook.ReturnType != typeof(void) && hook.ReturnType != typeof(bool))
                check.Fail($"{where} returns {hook.ReturnType.Name}; a prefix must return void or bool");

            foreach (var p in hook.GetParameters())
            {
                var name = p.Name ?? string.Empty;
                var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                switch (name)
                {
                    case "__instance":
                        if (target.IsStatic) check.Notes.Add($"{where}: __instance on a static method is always null");
                        else if (!type.IsAssignableFrom(declaring) && !declaring.IsAssignableFrom(type))
                            check.Fail($"{where}: __instance is {type.Name}, the method now belongs to {declaring?.Name}");
                        continue;
                    case "__result":
                        if (returnType == typeof(void)) check.Fail($"{where}: __result, but {Describe(target)} now returns nothing");
                        else if (type != returnType && !(type.IsAssignableFrom(returnType) && !p.ParameterType.IsByRef))
                            check.Fail($"{where}: __result is {type.Name}, the method now returns {returnType.Name}");
                        continue;
                    case "__state":
                    case "__exception":
                    case "__originalMethod":
                    case "__args":
                    case "__runOriginal":
                    case "__resultRef":
                        continue;
                }
                if (name.StartsWith("___", StringComparison.Ordinal))
                {
                    var field = AccessTools.Field(declaring, name.Substring(3));
                    if (field == null) check.Fail($"{where}: field '{name.Substring(3)}' no longer exists on {declaring?.Name}");
                    continue;
                }
                var m = IndexParam.Match(name);
                if (m.Success)
                {
                    int i = int.Parse(m.Groups[1].Value);
                    if (i >= targetParams.Length) check.Fail($"{where}: {name}, but the method now has {targetParams.Length} parameter(s)");
                    continue;
                }
                var match = targetParams.FirstOrDefault(tp => tp.Name == name);
                if (match == null)
                {
                    check.Fail($"{where}: parameter '{name}' not found; the method's parameters are now ({string.Join(", ", targetParams.Select(tp => tp.ParameterType.Name + " " + tp.Name))})");
                    continue;
                }
                var targetType = match.ParameterType.IsByRef ? match.ParameterType.GetElementType() : match.ParameterType;
                if (type != targetType && !(type.IsAssignableFrom(targetType) && !p.ParameterType.IsByRef))
                    check.Fail($"{where}: parameter '{name}' is {type.Name} in the hook but {targetType.Name} in the game");
            }
        }

        private static void CheckState(PatchCheck check, PatchSpec spec, MethodBase target)
        {
            Type StateOf(MethodInfo m)
            {
                var p = m?.GetParameters().FirstOrDefault(x => x.Name == "__state");
                if (p == null) return null;
                return p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
            }
            var pre = StateOf(spec.Prefix);
            foreach (var other in new[] { spec.Postfix, spec.Finalizer })
            {
                var t = StateOf(other);
                if (t != null && pre != null && t != pre)
                    check.Fail($"__state is {pre.Name} in the prefix but {t.Name} in {other.Name}");
            }
        }

        /// <summary>Set by the tool's --no-transpilers option: skip running transpilers.</summary>
        public static bool SkipTranspilers { get; set; }

        // Runs the transpiler on the original instructions. Returns null when it ran, else why it couldn't.
        //
        // Harmony 2.2.2's public PatchProcessor.GetOriginalInstructions first looks up existing patches, which starts
        // its runtime-detour engine. On modern .NET that engine crashes the whole process (an uncatchable CLR error),
        // so outside the game the method's code is read with Harmony's internal MethodBodyReader directly, with an IL
        // generator from a throwaway DynamicMethod. Found by reflection: if this Harmony build differs, the transpiler
        // is reported as not checkable instead.
        private static string RunTranspiler(MethodInfo transpiler, MethodBase target)
        {
            if (SkipTranspilers) return "transpiler checks were switched off (--no-transpilers)";
            List<CodeInstruction> original;
            object generator;
            try
            {
                generator = new System.Reflection.Emit.DynamicMethod("FtdModApi_PatchCheck", typeof(void), Type.EmptyTypes, typeof(PatchChecker).Module, true).GetILGenerator();
                original = ReadInstructions(target, generator);
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException tie0 && tie0.InnerException != null ? tie0.InnerException : e;
                return "the game method's code could not be read: " + Describe(inner);
            }
            if (original == null) return "this Harmony build has no code reader the checker can use";

            try
            {
                var parameters = transpiler.GetParameters();
                var args = parameters.Select(p =>
                {
                    if (typeof(IEnumerable<CodeInstruction>).IsAssignableFrom(p.ParameterType)) return (object)original.Select(i => i.Clone()).ToList();
                    if (p.ParameterType.Name == "ILGenerator") return generator;
                    if (typeof(MethodBase).IsAssignableFrom(p.ParameterType)) return target;
                    return null;
                }).ToArray();
                var result = transpiler.Invoke(null, args) as IEnumerable<CodeInstruction>;
                _ = result?.ToList(); // enumerate: transpilers are often lazy
                return null;
            }
            catch (Exception e)
            {
                return "it threw: " + Describe(e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e);
            }
        }

        private static MethodInfo _readerGetInstructions;
        private static MethodInfo _toCodeInstruction;

        // HarmonyLib.MethodBodyReader.GetInstructions(ILGenerator, MethodBase) -> List<ILInstruction>;
        // ILInstruction.GetCodeInstruction() -> CodeInstruction. Null when this Harmony doesn't have them.
        private static List<CodeInstruction> ReadInstructions(MethodBase target, object generator)
        {
            if (_readerGetInstructions == null)
            {
                var harmony = typeof(CodeInstruction).Assembly;
                var reader = harmony.GetType("HarmonyLib.MethodBodyReader");
                _readerGetInstructions = reader?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "GetInstructions" && m.GetParameters().Length == 2
                                         && m.GetParameters()[0].ParameterType.Name == "ILGenerator"
                                         && typeof(MethodBase).IsAssignableFrom(m.GetParameters()[1].ParameterType));
                var ilInstruction = harmony.GetType("HarmonyLib.ILInstruction");
                _toCodeInstruction = ilInstruction?.GetMethod("GetCodeInstruction", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (_readerGetInstructions == null || _toCodeInstruction == null) return null;
            }
            var raw = _readerGetInstructions.Invoke(null, new[] { generator, target }) as System.Collections.IEnumerable;
            if (raw == null) return null;
            var list = new List<CodeInstruction>();
            foreach (var il in raw) list.Add((CodeInstruction)_toCodeInstruction.Invoke(il, null));
            return list;
        }

        // A removed member is a real break; an assembly that can't load outside the game is not.
        private static void ClassifyException(PatchCheck check, string what, Exception e)
        {
            var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
            // Environment limits (native Unity code, assemblies that only load in game) can't be judged here.
            // DllNotFound/EntryPointNotFound derive from TypeLoadException, so they are tested first.
            bool environment = inner is DllNotFoundException || inner is EntryPointNotFoundException
                || inner is FileNotFoundException || inner is FileLoadException || inner is BadImageFormatException
                || inner is ReflectionTypeLoadException || inner is TypeInitializationException || inner is NotSupportedException
                || inner is NotImplementedException || inner is System.Security.SecurityException;
            if (environment)
                check.CantCheck($"{what}: couldn't be checked outside the game ({Describe(inner)})");
            else
                // Includes a game class that is gone (TypeLoadException), the most common break after an update.
                check.Fail($"{what}: {Describe(inner)}");
        }

        private static string Describe(Exception e) => e.GetType().Name + ": " + e.Message;

        private static string Describe(MethodBase m)
        {
            if (m == null) return "(none)";
            try
            {
                return $"{m.DeclaringType?.Name}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})";
            }
            catch
            {
                return $"{m.DeclaringType?.Name}.{m.Name}";
            }
        }
    }
}

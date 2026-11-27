using System;
using BrilliantSkies.Core;
using FtdModApi.Patching;
using HarmonyLib;

namespace FtdModApi
{
    public static partial class Events
    {
        /// <summary>Lua boxes: script errors and log lines (reported on the host, where Update runs). Mod functions: see <see cref="LuaFunctions"/>.</summary>
        public static class Lua
        {
            private static AccessTools.FieldRef<LuaBinding, MainConstruct> _bindingConstruct;

            private static readonly EventHook ErrorHook = new EventHook(Features.EventLua, () => new[]
            {
                new PatchSpec
                {
                    Id = "LuaBox.RunLuaUpdate: Lua.ScriptError (while running)",
                    FeatureId = Features.EventLua,
                    Target = () => EventHelpers.Declared(typeof(LuaBox), "RunLuaUpdate"),
                    Prefix = PatchSpec.Hook(typeof(Lua), nameof(Run_Prefix)),
                    Postfix = PatchSpec.Hook(typeof(Lua), nameof(Run_Postfix))
                },
                new PatchSpec
                {
                    Id = "LuaBox.SetLuaCode: Lua.ScriptError (loading the code)",
                    FeatureId = Features.EventLua,
                    Target = () => EventHelpers.Declared(typeof(LuaBox), "SetLuaCode"),
                    Postfix = PatchSpec.Hook(typeof(Lua), nameof(SetCode_Postfix))
                }
            });

            private static readonly EventHook LogHook = new EventHook(Features.EventLua, () => new[]
            {
                new PatchSpec
                {
                    Id = "LuaBinding.Log: Lua.Logged",
                    FeatureId = Features.EventLua,
                    Target = () =>
                    {
                        _bindingConstruct = FieldRefs.Get<LuaBinding, MainConstruct>("_C");
                        return EventHelpers.Declared(typeof(LuaBinding), "Log");
                    },
                    Postfix = PatchSpec.Hook(typeof(Lua), nameof(Log_Postfix))
                },
                new PatchSpec
                {
                    Id = "LuaBinding.LogToHud: Lua.Logged (to the HUD)",
                    FeatureId = Features.EventLua,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(LuaBinding), "LogToHud"),
                    Postfix = PatchSpec.Hook(typeof(Lua), nameof(LogToHud_Postfix))
                }
            });

            /// <summary>
            /// A Lua box's script stopped with an error: while loading the code (a syntax error) or while running Update
            /// (a runtime error, including errors from mod Lua functions). Host.
            /// </summary>
            public static EventChannel<LuaErrorEvent> ScriptError { get; } = new EventChannel<LuaErrorEvent>(
                "Lua.ScriptError", EventSide.Host, "A Lua script stopped with an error.", ErrorHook);

            /// <summary>A Lua script wrote a log line (<c>I:Log</c>) or a HUD message (<c>I:LogToHud</c>). Host.</summary>
            public static EventChannel<LuaLogEvent> Logged { get; } = new EventChannel<LuaLogEvent>(
                "Lua.Logged", EventSide.Host, "A Lua script logged a message.", LogHook);

            private static void Run_Prefix(LuaBox __instance, out bool __state)
            {
                __state = __instance != null && __instance.Running;
            }

            private static void Run_Postfix(LuaBox __instance, bool __state)
            {
                if (!__state || __instance == null || __instance.Running || string.IsNullOrEmpty(__instance.ErrorStack)) return;
                if (ScriptError.HasSubscribers) ScriptError.RaiseOnMainThread(new LuaErrorEvent(__instance, __instance.ErrorStack, false));
            }

            private static void SetCode_Postfix(LuaBox __instance)
            {
                // Clients also load the code (when it syncs); only the host reports.
                if (Net.IsClient || __instance == null || __instance.Running || string.IsNullOrEmpty(__instance.ErrorStack)) return;
                if (ScriptError.HasSubscribers) ScriptError.RaiseOnMainThread(new LuaErrorEvent(__instance, __instance.ErrorStack, true));
            }

            private static void Log_Postfix(LuaBinding __instance, string message)
            {
                // Clients get an automatic "Lua does not run on a client" line every tick; only report the host's.
                if (Net.IsClient || !Logged.HasSubscribers) return;
                Logged.RaiseOnMainThread(new LuaLogEvent(ConstructOf(__instance), message, false));
            }

            private static void LogToHud_Postfix(LuaBinding __instance, string message)
            {
                if (Net.IsClient || !Logged.HasSubscribers) return;
                Logged.RaiseOnMainThread(new LuaLogEvent(ConstructOf(__instance), message, true));
            }

            private static MainConstruct ConstructOf(LuaBinding binding)
            {
                if (binding == null || _bindingConstruct == null) return null;
                try
                {
                    return _bindingConstruct(binding);
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    /// <summary>A Lua box's script stopped with an error.</summary>
    public sealed class LuaErrorEvent : GameEvent
    {
        internal LuaErrorEvent(LuaBox box, string message, bool whileLoading)
        {
            Box = box;
            Message = message;
            WhileLoading = whileLoading;
            Construct = box?.MainConstruct as MainConstruct;
            Name = EventHelpers.SafeName(Construct);
        }

        /// <summary>The Lua box. Game object.</summary>
        public LuaBox Box { get; }

        /// <summary>The craft it is on.</summary>
        public MainConstruct Construct { get; }

        public string Name { get; }

        /// <summary>The error, as shown in the Lua box.</summary>
        public string Message { get; }

        /// <summary>True for an error loading the code (syntax), false for one while it ran.</summary>
        public bool WhileLoading { get; }
    }

    /// <summary>A Lua script logged a message.</summary>
    public sealed class LuaLogEvent : GameEvent
    {
        internal LuaLogEvent(MainConstruct construct, string message, bool toHud)
        {
            Construct = construct;
            Message = message;
            ToHud = toHud;
            Name = EventHelpers.SafeName(construct);
        }

        /// <summary>The craft whose Lua box logged it.</summary>
        public MainConstruct Construct { get; }

        public string Name { get; }

        public string Message { get; }

        /// <summary>True for I:LogToHud, false for I:Log.</summary>
        public bool ToHud { get; }
    }
}

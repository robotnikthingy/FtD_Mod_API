using System;
using System.Collections.Generic;
using System.Linq;
using FtdModApi.Patching;
using HarmonyLib;
using MoonSharp.Interpreter;

namespace FtdModApi
{
    /// <summary>
    /// Functions mods add to players' Lua scripts. Each mod gets a table under <c>Mods</c>: a function registered as
    /// ("MyMod", "Boost") is called from a Lua box as <c>Mods.MyMod.Boost(5)</c> (or <c>Mods["My Mod"].Boost(5)</c> when
    /// the mod id has spaces); use a dot, not a colon. Register on the main thread, ideally in your plugin's OnLoad
    /// (functions registered later still reach running scripts). Scripts can't change or remove the functions. A Lua
    /// box's Update runs on the host only, but the code outside Update also runs once on multiplayer clients when it loads.
    /// </summary>
    /// <example>
    /// <code>
    /// LuaFunctions.Register("MyMod", "CraftName", call => call.Construct?.GetName(), "The name of the craft running the script.");
    /// // Lua:  I:Log(Mods.MyMod.CraftName())
    /// </code>
    /// </example>
    public static class LuaFunctions
    {
        /// <summary>The name of the global table that holds every mod's functions.</summary>
        public const string TableName = "Mods";

        private static readonly object _gate = new object();
        private static readonly List<LuaFunctionInfo> _functions = new List<LuaFunctionInfo>();
        private static Script _script;
        private static readonly Dictionary<string, Table> _modTables = new Dictionary<string, Table>(StringComparer.Ordinal);
        private static bool _patched;

        /// <summary>Every registered function.</summary>
        public static IReadOnlyList<LuaFunctionInfo> All
        {
            get { lock (_gate) return _functions.ToList(); }
        }

        /// <summary>
        /// Adds a function to players' Lua scripts. The handler runs on the game's thread while the script runs; return a
        /// number, string, bool, null, or a game value Lua already knows (Vector3, ...). Throwing stops that Lua box with
        /// your message shown in its error box, like a Lua error.
        /// </summary>
        public static LuaFunctionInfo Register(string modId, string name, Func<LuaCall, object> handler, string description = null)
        {
            if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A function name is required.", nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var info = new LuaFunctionInfo(modId, name, handler, description);
            lock (_gate)
            {
                _functions.RemoveAll(f => string.Equals(f.ModId, modId, StringComparison.Ordinal) && string.Equals(f.Name, name, StringComparison.Ordinal));
                _functions.Add(info);
            }
            EnsurePatched();
            lock (_gate)
            {
                if (_script != null) AddToTable(info);
            }
            FtdApi.Log.Verbose(() => $"Lua function {TableName}.{modId}.{name} registered.");
            return info;
        }

        private static void EnsurePatched()
        {
            if (_patched) return;
            lock (_gate)
            {
                if (_patched) return;
                _patched = true;
            }
            var results = PatchManager.Apply(Specs());
            if (results.Any(r => !r.Success))
                FtdApi.Log.Error("Mod Lua functions are unavailable: " + string.Join("; ", results.Where(r => !r.Success).Select(r => r.Error)));
        }

        internal static PatchSpec[] Specs()
        {
            return new[]
            {
                new PatchSpec
                {
                    Id = "LuaMaster.CreateSandbox: Mods table for mod Lua functions",
                    FeatureId = Features.LuaFunctions,
                    Target = () => EventHelpers.Declared(typeof(LuaMaster), "CreateSandbox"),
                    Postfix = PatchSpec.Hook(typeof(LuaFunctions), nameof(CreateSandbox_Postfix))
                },
                new PatchSpec
                {
                    // The game marks the running box before Update, but not before the code outside Update runs.
                    Id = "LuaBox.SetLuaCode: mark the box running its code (for mod Lua functions)",
                    FeatureId = Features.LuaFunctions,
                    Required = false,
                    Target = () => EventHelpers.Declared(typeof(LuaBox), "SetLuaCode"),
                    Prefix = PatchSpec.Hook(typeof(LuaFunctions), nameof(SetLuaCode_Prefix))
                }
            };
        }

        private static void SetLuaCode_Prefix(LuaBox __instance)
        {
            if (__instance != null) LuaBox.ActiveBox = __instance;
        }

        private static void CreateSandbox_Postfix(Table __result)
        {
            if (__result == null) return;
            try
            {
                // The sandbox's own script (LuaMaster.Lua): read from the table, because FtdLua was built against a
                // MoonSharp with a different version number and the compiler won't accept LuaMaster.Lua's type.
                var script = __result.OwnerScript;
                if (script == null) return;
                lock (_gate)
                {
                    if (!ReferenceEquals(_script, script))
                    {
                        _script = script;
                        _modTables.Clear();
                        foreach (var f in _functions) AddToTable(f);
                    }
                    __result[TableName] = DynValue.NewTable(MakeModsProxy(_script));
                }
            }
            catch (Exception e)
            {
                FtdApi.Log.ErrorOnce("lua-sandbox", "Could not add the Mods table to a Lua box.", e);
            }
        }

        // Each sandbox gets its own empty, locked proxy tables, so one script can't change what another sees.
        private static Table MakeModsProxy(Script script)
        {
            var proxy = new Table(script);
            var meta = new Table(script);
            meta["__index"] = DynValue.NewCallback((ctx, args) =>
            {
                var key = args.Count > 1 ? args[1] : DynValue.Nil;
                if (key.Type != DataType.String) return DynValue.Nil;
                Table real;
                lock (_gate)
                {
                    if (!_modTables.TryGetValue(key.String, out real)) return DynValue.Nil;
                }
                return DynValue.NewTable(MakeReadOnly(script, real));
            });
            meta["__newindex"] = ReadOnlyError();
            meta["__metatable"] = DynValue.NewString("locked");
            proxy.MetaTable = meta;
            return proxy;
        }

        private static Table MakeReadOnly(Script script, Table real)
        {
            var proxy = new Table(script);
            var meta = new Table(script);
            meta["__index"] = DynValue.NewTable(real);
            meta["__newindex"] = ReadOnlyError();
            meta["__metatable"] = DynValue.NewString("locked");
            proxy.MetaTable = meta;
            return proxy;
        }

        private static DynValue ReadOnlyError()
            => DynValue.NewCallback((ctx, args) => throw new ScriptRuntimeException(TableName + " is read-only"));

        // Called with _gate held and _script set. The real tables are never given to scripts.
        private static void AddToTable(LuaFunctionInfo f)
        {
            if (!_modTables.TryGetValue(f.ModId, out var t))
            {
                t = new Table(_script);
                _modTables[f.ModId] = t;
            }
            var script = _script;
            t[f.Name] = DynValue.NewCallback((ctx, args) => Invoke(f, script, args), f.Name);
        }

        private static DynValue Invoke(LuaFunctionInfo f, Script script, CallbackArguments args)
        {
            object result;
            try
            {
                result = f.Handler(new LuaCall(args));
            }
            catch (ScriptRuntimeException)
            {
                throw;
            }
            catch (Exception e)
            {
                FtdApi.Log.Verbose(() => $"Lua function {f.ModId}.{f.Name} threw: {e}");
                throw new ScriptRuntimeException($"{f.ModId}.{f.Name}: {e.Message}");
            }
            try
            {
                switch (result)
                {
                    case null: return DynValue.Nil;
                    case DynValue d: return d;
                    case bool b: return DynValue.NewBoolean(b);
                    case string s: return DynValue.NewString(s);
                    case int i: return DynValue.NewNumber(i);
                    case long l: return DynValue.NewNumber(l);
                    case float fl: return DynValue.NewNumber(fl);
                    case double db: return DynValue.NewNumber(db);
                    default: return DynValue.FromObject(script, result);
                }
            }
            catch (Exception e)
            {
                throw new ScriptRuntimeException($"{f.ModId}.{f.Name}: its result can't be passed to Lua ({e.Message})");
            }
        }
    }

    /// <summary>A registered mod Lua function.</summary>
    public sealed class LuaFunctionInfo
    {
        internal LuaFunctionInfo(string modId, string name, Func<LuaCall, object> handler, string description)
        {
            ModId = modId;
            Name = name;
            Handler = handler;
            Description = description ?? string.Empty;
        }

        public string ModId { get; }

        public string Name { get; }

        public string Description { get; }

        internal Func<LuaCall, object> Handler { get; }

        /// <summary>How players call it, e.g. Mods.MyMod.Boost().</summary>
        public string LuaName => LuaFunctions.TableName + "." + ModId + "." + Name;
    }

    /// <summary>One call of a mod Lua function: its arguments and the Lua box that made it.</summary>
    public sealed class LuaCall
    {
        private readonly CallbackArguments _args;

        internal LuaCall(CallbackArguments args)
        {
            _args = args;
            Box = LuaBox.ActiveBox;
        }

        /// <summary>The Lua box running the script. Game object.</summary>
        public LuaBox Box { get; }

        /// <summary>The craft the Lua box is on.</summary>
        public MainConstruct Construct => Box?.MainConstruct as MainConstruct;

        /// <summary>How many arguments the script passed.</summary>
        public int Count => _args?.Count ?? 0;

        /// <summary>The raw Lua value of argument i (0-based). Nil when there is none.</summary>
        public DynValue Arg(int i) => _args != null && i >= 0 && i < _args.Count ? _args[i] : DynValue.Nil;

        /// <summary>Argument i as a number, or the fallback when it is missing or not a number.</summary>
        public double Number(int i, double fallback = 0)
        {
            var v = Arg(i);
            if (v.Type == DataType.Number) return v.Number;
            var n = v.CastToNumber();
            return n ?? fallback;
        }

        /// <summary>Argument i as text, or the fallback when it is missing.</summary>
        public string String(int i, string fallback = null)
        {
            var v = Arg(i);
            if (v.Type == DataType.Nil || v.Type == DataType.Void) return fallback;
            return v.Type == DataType.String ? v.String : v.CastToString() ?? fallback;
        }

        /// <summary>Argument i as true/false (Lua rules: only nil and false are false), or the fallback when missing.</summary>
        public bool Bool(int i, bool fallback = false)
        {
            var v = Arg(i);
            if (v.Type == DataType.Nil || v.Type == DataType.Void) return fallback;
            return v.CastToBool();
        }

        /// <summary>Argument i converted to a .NET object (number, string, table, game object), or null.</summary>
        public object Object(int i) => Arg(i).ToObject();
    }
}

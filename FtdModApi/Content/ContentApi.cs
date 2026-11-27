using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BrilliantSkies.Core.Id;
using BrilliantSkies.Modding;
using BrilliantSkies.Modding.Containers;
using BrilliantSkies.Modding.Types;
using FtdModApi.Patching;
using Newtonsoft.Json.Linq;

namespace FtdModApi
{
    /// <summary>Read-only description of a block (item) definition.</summary>
    public sealed class ItemInfo
    {
        internal ItemInfo(ItemDefinition def)
        {
            Definition = def;
        }

        public Guid Guid => Definition.ComponentId?.Guid ?? Guid.Empty;

        /// <summary>Internal name, e.g. "Metal Block". Used for lookups.</summary>
        public string Name => Definition.ComponentId?.Name;

        /// <summary>Name shown to players (English).</summary>
        public string DisplayName => SafeEnglish(Definition.DisplayName);

        /// <summary>The C# block class that implements it (e.g. "AdvCannonFiringPiece"), or empty for plain blocks.</summary>
        public string ClassName => Definition.Code?.ClassName ?? string.Empty;

        /// <summary>The inventory tab or variant group the item sits under.</summary>
        public string Tab => Content.TabName(Definition);

        /// <summary>Folder of the mod that defines it (core game content lives in the game's core mod folders).</summary>
        public string ModFolder => FolderName(Definition.ModDirectoryWithSlash);

        /// <summary>False when hidden from the build menu and block search.</summary>
        public bool ShownInBuildMenu => Definition.DisplayOnInventory;

        /// <summary>Raw game definition. Unstable.</summary>
        public ItemDefinition Definition { get; }

        internal static string SafeEnglish(BrilliantSkies.Localisation.Widgets.LocalStringScraped s)
        {
            try
            {
                return s?.ScrapableEnglish ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static string FolderName(string dirWithSlash)
        {
            if (string.IsNullOrEmpty(dirWithSlash)) return string.Empty;
            try
            {
                return Path.GetFileName(dirWithSlash.TrimEnd('/', '\\'));
            }
            catch
            {
                return dirWithSlash;
            }
        }
    }

    /// <summary>
    /// Switch off or swap existing game content: blocks, prefabs and APS shell parts. This is what a
    /// total-conversion mod (for example a WWII mod without lasers or PACs) is built on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Register rules in your plugin's OnLoad. FtD builds its content after plugins load, and the API
    /// applies the rules at that moment, so they cover every save, blueprint and campaign design.
    /// </para>
    /// <list type="bullet">
    /// <item><b>Hide</b>: removes a block or prefab from the build menu and block search. Existing
    /// blueprints that contain it still load and work, and players can still paste it in from a prefab (add
    /// <b>Prevent paste</b> to stop that).</item>
    /// <item><b>Prevent paste</b>: the block is left out when a player pastes a prefab with the build tool,
    /// including prefabs captured from other craft and blueprints. The player is told what was left out. Their
    /// prefab is not changed, and spawning blueprints, loading saves and retrofitting are not affected, so
    /// existing designs never lose blocks.</item>
    /// <item><b>Replace</b>: every existing copy of a block, in every blueprint, save and campaign design,
    /// becomes another block (the game's own item-replacement system). Requires a restart to change.</item>
    /// <item><b>Hide shell part</b>: removes an APS shell part from the ammo customiser. Existing shell
    /// designs that use it keep working.</item>
    /// </list>
    /// <para>
    /// Blocks and prefabs can be named by guid or by internal name (case-insensitive). After each
    /// launch, Mods/FtdModApi/FtdModApi_content.txt lists every block, prefab and shell part with its
    /// guid, and shows what each rule matched.
    /// </para>
    /// <para>
    /// Data-only mods: put <c>FtdModApi.content.json</c> in your mod folder:
    /// <code>
    /// {
    ///   "hideItems":      ["Laser Combiner", "0a1b2c3d-...."],
    ///   "hidePrefabs":    ["Some Laser Prefab"],
    ///   "hideModFolders": ["Core_Laser", "Core_Plasma"],
    ///   "preventPaste":   ["Laser Combiner"],
    ///   "preventPasteModFolders": ["Core_Laser"],
    ///   "replaceItems":   { "Old Block Name": "Replacement Block Name" },
    ///   "hideShellParts": ["EmpWarhead", "GravitonRam"],
    ///   "cramPellets":    [ { "id": "MyMod.DenseHe", "block": "guid or name", "payload": 1.3, "health": 0.8 } ],
    ///   "missileParts":   [ { "id": "MyMod.HeavyHe", "name": "Heavy HE warhead", "behavesAs": "ExplosiveWarhead", "payloadVolume": 1.25 } ],
    ///   "apsLoaders":     { "Autoloader (8m shells)": 0.9 },
    ///   "apsMaxGaugeMm":  1000,
    ///   "blockHealth":    { "Heavy Armour": 1.25 },
    ///   "blockArmour":    { "Metal Block": 1.1 }
    /// }
    /// </code>
    /// </para>
    /// </remarks>
    public static class Content
    {
        /// <summary>File name the API looks for in every enabled mod folder.</summary>
        public const string JsonFileName = "FtdModApi.content.json";

        private enum RuleKind
        {
            HideItem,
            HidePrefab,
            HideFolder,
            ReplaceItem,
            PreventPasteItem,
            PreventPasteFolder
        }

        private static bool IsPasteRule(RuleKind kind) => kind == RuleKind.PreventPasteItem || kind == RuleKind.PreventPasteFolder;

        // Block guid -> mod id, for blocks that are left out of prefab pastes. Replaced as a whole when rules change.
        private static volatile Dictionary<Guid, string> _pasteBlocked = new Dictionary<Guid, string>();

        internal static IReadOnlyDictionary<Guid, string> PasteBlocked => _pasteBlocked;

        private sealed class Rule
        {
            public string ModId;
            public RuleKind Kind;
            public string Target;
            public string Replacement;
            public string Outcome = "not applied yet";
        }

        private static readonly object _gate = new object();
        private static readonly List<Rule> _rules = new List<Rule>();
        private static readonly Dictionary<ShellModuleType, List<string>> _hiddenShellParts = new Dictionary<ShellModuleType, List<string>>();
        private static volatile ShellModuleType[] _hiddenShellPartsSnapshot = new ShellModuleType[0];
        private static bool _configurationBuilt;

        // ------------------------------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------------------------------

        /// <summary>Hide a block from the build menu and block search. Feature: <see cref="Features.ContentItems"/>.</summary>
        public static Registration HideItem(string modId, string guidOrName) => AddRule(modId, RuleKind.HideItem, guidOrName, null);

        /// <summary>
        /// Hide every block and prefab defined in one mod folder, e.g. "Core_Laser" or "Core_Plasma"
        /// (the folder names are listed in FtdModApi_content.txt). Feature: <see cref="Features.ContentItems"/>.
        /// </summary>
        public static Registration HideModFolder(string modId, string folderName) => AddRule(modId, RuleKind.HideFolder, folderName, null);

        /// <summary>
        /// Leave a block out when a player pastes a prefab with the build tool (prefabs captured from other craft
        /// and blueprints included); its mirror variants are left out too. Usually combined with <see cref="HideItem"/>.
        /// The player sees what was left out. Spawning blueprints, loading saves, retrofitting and undo are not
        /// affected, and the player's prefab itself is not changed. Feature: <see cref="Features.ContentPaste"/>.
        /// </summary>
        public static Registration PreventPaste(string modId, string guidOrName) => AddRule(modId, RuleKind.PreventPasteItem, guidOrName, null);

        /// <summary>
        /// <see cref="PreventPaste"/> for every block defined in one mod folder, e.g. "Core_Laser".
        /// Feature: <see cref="Features.ContentPaste"/>.
        /// </summary>
        public static Registration PreventPasteFromModFolder(string modId, string folderName) => AddRule(modId, RuleKind.PreventPasteFolder, folderName, null);

        /// <summary>Hide a prefab from the build menu. Feature: <see cref="Features.ContentItems"/>.</summary>
        public static Registration HidePrefab(string modId, string guidOrName) => AddRule(modId, RuleKind.HidePrefab, guidOrName, null);

        /// <summary>
        /// Turn every copy of one block into another, everywhere (blueprints, saves, campaign designs).
        /// The replaced block's id is kept, so removing the rule later restores the original block.
        /// Feature: <see cref="Features.ContentItems"/>.
        /// </summary>
        public static Registration ReplaceItem(string modId, string itemGuidOrName, string replacementGuidOrName)
        {
            if (string.IsNullOrEmpty(replacementGuidOrName)) throw new ArgumentException("A replacement is required.", nameof(replacementGuidOrName));
            return AddRule(modId, RuleKind.ReplaceItem, itemGuidOrName, replacementGuidOrName);
        }

        /// <summary>
        /// Hide an APS shell part from the ammo customiser, by its game name (e.g. "EmpWarhead", "GravitonRam";
        /// see FtdModApi_content.txt for the list). Feature: <see cref="Features.ContentShellParts"/>.
        /// </summary>
        public static Registration HideShellPart(string modId, string shellPartName)
        {
            RequireModId(modId);
            ShellModuleType type;
            var modded = ShellParts.Find(shellPartName);
            if (modded != null)
            {
                type = modded.Type;
            }
            else if (!ShellParts.TryParseGamePart(shellPartName, out type))
            {
                FtdApi.GetLog(modId).Warning($"Unknown APS shell part '{shellPartName}'. Valid names are listed in FtdModApi_content.txt.");
                return new Registration(null);
            }

            lock (_gate)
            {
                if (!_hiddenShellParts.TryGetValue(type, out var owners))
                {
                    owners = new List<string>();
                    _hiddenShellParts.Add(type, owners);
                }
                owners.Add(modId);
                _hiddenShellPartsSnapshot = _hiddenShellParts.Keys.ToArray();
            }

            return new Registration(() =>
            {
                lock (_gate)
                {
                    if (_hiddenShellParts.TryGetValue(type, out var owners))
                    {
                        owners.Remove(modId);
                        if (owners.Count == 0) _hiddenShellParts.Remove(type);
                    }
                    _hiddenShellPartsSnapshot = _hiddenShellParts.Keys.ToArray();
                }
            });
        }

        /// <summary>
        /// All block definitions, once the game has built its content (empty before that, e.g. during OnLoad;
        /// use <see cref="Lifecycle.WhenMainMenuReady"/> if you need them).
        /// </summary>
        public static IReadOnlyList<ItemInfo> Items
        {
            get
            {
                var items = ItemContainer();
                return items == null ? new List<ItemInfo>() : items.Components.Where(d => d != null).Select(d => new ItemInfo(d)).ToList();
            }
        }

        /// <summary>Finds a block by guid or internal name, or null (also null before the game has built its content).</summary>
        public static ItemInfo FindItem(string guidOrName)
        {
            var items = ItemContainer();
            if (items == null) return null;
            var matches = Resolve(items.Components, guidOrName);
            return matches.Count > 0 ? new ItemInfo(matches[0]) : null;
        }

        /// <summary>Applies a content JSON file (format in the class remarks). Returns how many rules were added.</summary>
        public static int LoadJson(string modId, string path)
        {
            var log = FtdApi.GetLog(modId);
            JObject root;
            try
            {
                root = JObject.Parse(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                log.Error("Could not read " + path + ": " + e.Message);
                return 0;
            }

            int added = 0;
            foreach (var s in Strings(root["hideItems"])) { HideItem(modId, s); added++; }
            foreach (var s in Strings(root["hidePrefabs"])) { HidePrefab(modId, s); added++; }
            foreach (var s in Strings(root["hideModFolders"])) { HideModFolder(modId, s); added++; }
            foreach (var s in Strings(root["preventPaste"])) { PreventPaste(modId, s); added++; }
            foreach (var s in Strings(root["preventPasteModFolders"])) { PreventPasteFromModFolder(modId, s); added++; }
            foreach (var s in Strings(root["hideShellParts"])) { if (HideShellPart(modId, s).IsActive) added++; }
            if (root["cramPellets"] is JArray pellets)
            {
                foreach (var t in pellets.OfType<JObject>())
                {
                    var def = new CramPelletDefinition
                    {
                        Id = (string)t["id"],
                        Block = (string)t["block"],
                        Description = (string)t["description"],
                        Payload = (float?)t["payload"] ?? 1f,
                        KineticDamage = (float?)t["kineticDamage"] ?? 1f,
                        ArmourPiercing = (float?)t["armourPiercing"] ?? 1f,
                        Health = (float?)t["health"] ?? 1f
                    };
                    if (CramPellets.Register(modId, def) != null) added++;
                }
            }
            foreach (var (jsonKey, apply) in new (string, Action<string, float>)[]
                     {
                         ("blockHealth", (b, f) => BlockApi.SetHealthMultiplier(modId, b, f)),
                         ("blockArmour", (b, f) => BlockApi.SetArmourMultiplier(modId, b, f))
                     })
            {
                if (!(root[jsonKey] is JObject perBlock)) continue;
                foreach (var p in perBlock.Properties())
                {
                    if ((p.Value.Type != JTokenType.Float && p.Value.Type != JTokenType.Integer) || p.Value.Value<float>() <= 0f)
                    {
                        log.Warning($"{JsonFileName}: {jsonKey} '{p.Name}' needs a multiplier above 0.");
                        continue;
                    }
                    apply(p.Name, p.Value.Value<float>());
                    added++;
                }
            }
            if (root["apsLoaders"] is JObject loaders)
            {
                foreach (var p in loaders.Properties())
                {
                    if (p.Value.Type != JTokenType.Float && p.Value.Type != JTokenType.Integer || p.Value.Value<float>() <= 0f)
                    {
                        log.Warning($"{JsonFileName}: apsLoaders '{p.Name}' needs a number above 0 (loading time factor).");
                        continue;
                    }
                    ApsApi.SetLoaderTimeFactor(modId, p.Name, p.Value.Value<float>());
                    added++;
                }
            }
            if (root["apsMaxGaugeMm"] != null)
            {
                var mm = (float?)root["apsMaxGaugeMm"];
                if (mm.HasValue) { ApsGauge.RequestMaxGauge(modId, mm.Value); added++; }
                else log.Warning($"{JsonFileName}: apsMaxGaugeMm must be a number (mm), e.g. 1000.");
            }
            if (root["missileParts"] is JArray missileParts)
            {
                foreach (var t in missileParts.OfType<JObject>())
                {
                    var def = new MissilePartDefinition
                    {
                        Id = (string)t["id"],
                        Name = (string)t["name"],
                        Description = (string)t["description"],
                        BehavesAs = (string)t["behavesAs"],
                        HealthModifier = (float?)t["healthModifier"],
                        LaunchSpeed = (float?)t["launchSpeed"],
                        AmmoCostModifier = (float?)t["ammoCostModifier"],
                        MassModifier = (float?)t["massModifier"],
                        DragModifier = (float?)t["dragModifier"],
                        AngularDragModifier = (float?)t["angularDragModifier"],
                        PayloadVolume = (float?)t["payloadVolume"]
                    };
                    if (MissileParts.Register(modId, def) != null) added++;
                }
            }
            if (root["replaceItems"] is JObject replacements)
            {
                foreach (var p in replacements.Properties())
                {
                    var with = p.Value.Type == JTokenType.String ? p.Value.Value<string>() : null;
                    if (string.IsNullOrEmpty(with))
                    {
                        log.Warning($"{JsonFileName}: replacement for '{p.Name}' must be a block name or guid.");
                        continue;
                    }
                    ReplaceItem(modId, p.Name, with);
                    added++;
                }
            }
            log.Info($"Added {added} content rule(s) from {Path.GetFileName(path)}.");
            return added;
        }

        // ------------------------------------------------------------------------------------------
        // Rules
        // ------------------------------------------------------------------------------------------

        private static Registration AddRule(string modId, RuleKind kind, string target, string replacement)
        {
            RequireModId(modId);
            if (string.IsNullOrEmpty(target)) throw new ArgumentException("A block or prefab name or guid is required.", nameof(target));

            var rule = new Rule { ModId = modId, Kind = kind, Target = target.Trim(), Replacement = replacement?.Trim() };
            bool built;
            lock (_gate)
            {
                _rules.Add(rule);
                built = _configurationBuilt;
            }

            if (built)
            {
                if (kind == RuleKind.ReplaceItem)
                {
                    rule.Outcome = "registered after the game built its content; takes effect after a restart";
                    FtdApi.GetLog(modId).Warning($"ReplaceItem('{rule.Target}') was registered too late and needs a restart. Register it in OnLoad.");
                }
                else if (IsPasteRule(kind))
                {
                    RebuildPasteRules(null, logProblems: true);
                }
                else
                {
                    ApplyHide(rule); // hiding can be applied live
                }
            }

            return new Registration(() =>
            {
                bool rebuild;
                lock (_gate)
                {
                    _rules.Remove(rule);
                    rebuild = _configurationBuilt && IsPasteRule(rule.Kind);
                }
                if (rebuild) RebuildPasteRules(null, logProblems: false);
            });
        }

        private static void RequireModId(string modId)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
        }

        private static IEnumerable<string> Strings(JToken token)
        {
            if (token is JArray arr)
            {
                foreach (var t in arr)
                {
                    if (t.Type == JTokenType.String && !string.IsNullOrWhiteSpace(t.Value<string>())) yield return t.Value<string>();
                }
            }
        }

        // ------------------------------------------------------------------------------------------
        // Applying rules (when FtD builds its configuration)
        // ------------------------------------------------------------------------------------------

        internal static IEnumerable<PatchSpec> Specs()
        {
            yield return new PatchSpec
            {
                Id = "Configured.RunReplacements: apply hide/replace rules",
                FeatureId = Features.ContentItems,
                Target = () => PatchSpec.Method(typeof(Configured), nameof(Configured.RunReplacements), typeof(ConfigurationManagerComponentLinker)),
                Postfix = PatchSpec.Hook(typeof(Content), nameof(RunReplacements_Postfix))
            };
            foreach (var spec in PrefabPaste.Specs()) yield return spec;
            yield return new PatchSpec
            {
                Id = "ShellModuleDictionary.GetAllModulesTypes: hide shell parts from the customiser",
                FeatureId = Features.ContentShellParts,
                Target = () => PatchSpec.Method(typeof(ShellModuleDictionary), nameof(ShellModuleDictionary.GetAllModulesTypes)),
                Postfix = PatchSpec.Hook(typeof(Content), nameof(GetAllModulesTypes_Postfix))
            };
        }

        private static void RunReplacements_Postfix(Configured __instance, ConfigurationManagerComponentLinker linker)
        {
            try
            {
                List<Rule> rules;
                lock (_gate) rules = _rules.ToList();

                var items = __instance.Get<ModificationComponentContainerItem>();
                foreach (var rule in rules.Where(r => r.Kind == RuleKind.ReplaceItem)) ApplyReplace(rule, items, linker);
                foreach (var rule in rules.Where(r => r.Kind != RuleKind.ReplaceItem && !IsPasteRule(r.Kind))) ApplyHide(rule, __instance);
                RebuildPasteRules(__instance, logProblems: true);
                foreach (var rule in rules)
                {
                    var r = rule;
                    FtdApi.GetLog(r.ModId).Verbose(() => $"Content rule {r.Kind} '{r.Target}'{(r.Replacement != null ? " -> '" + r.Replacement + "'" : string.Empty)}: {r.Outcome}.");
                }

                lock (_gate) _configurationBuilt = true;
            }
            catch (Exception e)
            {
                FtdApi.Log.Error("Applying content rules failed.", e);
            }
        }

        private static void ApplyReplace(Rule rule, ModificationComponentContainerItem items, ConfigurationManagerComponentLinker linker)
        {
            var log = FtdApi.GetLog(rule.ModId);
            var from = Resolve(items.Components, rule.Target);
            var to = Resolve(items.Components, rule.Replacement);
            if (from.Count != 1 || to.Count != 1)
            {
                rule.Outcome = DescribeMatchProblem("block", rule.Target, from.Count) ?? DescribeMatchProblem("replacement", rule.Replacement, to.Count);
                log.Warning($"ReplaceItem('{rule.Target}' -> '{rule.Replacement}'): {rule.Outcome}");
                return;
            }

            var fromName = from[0].ComponentId.Name;
            var replaced = items.Replace(linker, from[0].ComponentId.Guid, to[0].ComponentId.Guid);
            if (replaced == null)
            {
                rule.Outcome = "the game refused the replacement";
                log.Warning($"ReplaceItem('{rule.Target}' -> '{rule.Replacement}'): {rule.Outcome}");
                return;
            }
            replaced.DisplayOnInventory = false; // same as the game's own .itemreplace files
            rule.Outcome = $"'{fromName}' now builds as '{to[0].ComponentId.Name}'";
        }

        private static void ApplyHide(Rule rule, Configured configured = null)
        {
            configured = configured ?? Configured.i;
            if (configured == null) return;

            List<InventoryItemBaseClass> matches;
            if (rule.Kind == RuleKind.HideFolder)
            {
                matches = configured.Get<ModificationComponentContainerItem>().Components.Cast<InventoryItemBaseClass>()
                    .Concat(configured.Get<ModificationComponentContainerPrefab>().Components)
                    .Where(c => c != null && string.Equals(ItemInfo.FolderName(c.ModDirectoryWithSlash), rule.Target, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count == 0)
                {
                    rule.Outcome = $"no blocks or prefabs come from a mod folder named '{rule.Target}'";
                    FtdApi.GetLog(rule.ModId).Warning($"HideModFolder('{rule.Target}'): {rule.Outcome}");
                    return;
                }
                foreach (var m in matches) m.DisplayOnInventory = false;
                rule.Outcome = $"hid {matches.Count} blocks and prefabs";
                return;
            }
            if (rule.Kind == RuleKind.HideItem)
                matches = Resolve(configured.Get<ModificationComponentContainerItem>().Components, rule.Target).Cast<InventoryItemBaseClass>().ToList();
            else
                matches = Resolve(configured.Get<ModificationComponentContainerPrefab>().Components, rule.Target).Cast<InventoryItemBaseClass>().ToList();

            if (matches.Count == 0)
            {
                rule.Outcome = DescribeMatchProblem(rule.Kind == RuleKind.HideItem ? "block" : "prefab", rule.Target, 0);
                FtdApi.GetLog(rule.ModId).Warning($"{rule.Kind}('{rule.Target}'): {rule.Outcome}");
                return;
            }

            foreach (var m in matches) m.DisplayOnInventory = false;
            rule.Outcome = matches.Count == 1 ? "hidden" : $"hidden ({matches.Count} definitions share this name)";
        }

        private static void RebuildPasteRules(Configured configured, bool logProblems)
        {
            try
            {
                configured = configured ?? Configured.i;
                if (configured == null) return;
                var items = configured.Get<ModificationComponentContainerItem>();
                List<Rule> rules;
                lock (_gate) rules = _rules.Where(r => IsPasteRule(r.Kind)).ToList();

                var blocked = new Dictionary<Guid, string>();
                foreach (var rule in rules)
                {
                    List<ItemDefinition> matches = rule.Kind == RuleKind.PreventPasteFolder
                        ? items.Components.Where(c => c != null && c.ComponentId != null
                                                      && string.Equals(ItemInfo.FolderName(c.ModDirectoryWithSlash), rule.Target, StringComparison.OrdinalIgnoreCase)).ToList()
                        : Resolve(items.Components, rule.Target);
                    if (matches.Count == 0)
                    {
                        rule.Outcome = rule.Kind == RuleKind.PreventPasteFolder
                            ? $"no blocks come from a mod folder named '{rule.Target}'"
                            : DescribeMatchProblem("block", rule.Target, 0);
                        if (logProblems) FtdApi.GetLog(rule.ModId).Warning($"{(rule.Kind == RuleKind.PreventPasteFolder ? "PreventPasteFromModFolder" : "PreventPaste")}('{rule.Target}'): {rule.Outcome}");
                        continue;
                    }

                    int variants = 0;
                    foreach (var m in matches)
                    {
                        foreach (var guid in WithMirrorVariants(m, items))
                        {
                            if (blocked.ContainsKey(guid)) continue;
                            blocked[guid] = rule.ModId;
                            variants++;
                        }
                    }
                    rule.Outcome = $"{matches.Count} block{(matches.Count == 1 ? "" : "s")} left out of prefab pastes"
                                   + (variants > matches.Count ? $" (with mirror variants: {variants})" : string.Empty);
                }
                _pasteBlocked = blocked;
            }
            catch (Exception e)
            {
                FtdApi.Log.Error("Applying paste rules failed.", e);
            }
        }

        private static IEnumerable<Guid> WithMirrorVariants(ItemDefinition item, ModificationComponentContainerItem items)
        {
            yield return item.ComponentId.Guid;
            foreach (var reference in new[] { item.MirrorLaterialFlipReplacementReference, item.MirrorVerticalFlipReplacementReference })
            {
                Guid guid;
                try
                {
                    if (reference == null || !reference.IsValidReference || reference.Reference == null) continue;
                    guid = reference.Reference.Guid;
                }
                catch
                {
                    continue;
                }
                if (guid == Guid.Empty || guid == item.ComponentId.Guid) continue;
                items.Find(guid, out bool exists);
                if (exists) yield return guid;
            }
        }

        private static string DescribeMatchProblem(string what, string target, int count)
        {
            if (count == 1) return null;
            return count == 0
                ? $"no {what} named or with guid '{target}'"
                : $"{count} {what}s are named '{target}'; use the guid instead";
        }

        private static List<T> Resolve<T>(IEnumerable<T> components, string guidOrName) where T : class, IModComponent
        {
            var list = components.Where(c => c != null && c.ComponentId != null).ToList();
            if (Guid.TryParse(guidOrName, out var guid))
                return list.Where(c => c.ComponentId.Guid == guid).ToList();

            var byName = list.Where(c => string.Equals(c.ComponentId.Name, guidOrName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count > 0) return byName;

            // Fall back to the English display name players see.
            return list.Where(c => c is InventoryItemBaseClass inv
                                   && string.Equals(ItemInfo.SafeEnglish(inv.DisplayName), guidOrName, StringComparison.OrdinalIgnoreCase))
                       .ToList();
        }

        private static void GetAllModulesTypes_Postfix(List<ShellModule> __result)
        {
            var hidden = _hiddenShellPartsSnapshot;
            if (hidden.Length == 0 || __result == null) return;
            __result.RemoveAll(m => m != null && Array.IndexOf(hidden, m.Type) >= 0);
        }

        private static readonly Dictionary<Guid, string> _tabNames = new Dictionary<Guid, string>();

        internal static string TabName(ItemDefinition def)
        {
            var reference = def.InventoryTabOrVariantId?.Reference;
            if (reference == null || reference.Guid == Guid.Empty) return string.Empty;
            if (!string.IsNullOrEmpty(reference.Name)) return reference.Name;
            lock (_tabNames)
            {
                if (_tabNames.TryGetValue(reference.Guid, out var cached)) return cached;
                string name = string.Empty;
                try
                {
                    var items = ItemContainer();
                    if (items != null && items.Find(reference.Guid, out ItemDefinition tab)) name = tab.ComponentId?.Name ?? string.Empty;
                }
                catch
                {
                    // Leave blank.
                }
                _tabNames[reference.Guid] = name;
                return name;
            }
        }

        private static ModificationComponentContainerItem ItemContainer()
        {
            try
            {
                return Configured.i?.Get<ModificationComponentContainerItem>();
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------------------------------
        // Startup helpers and report
        // ------------------------------------------------------------------------------------------

        internal static void LoadDataOnlyMods(string modsRoot)
        {
            if (string.IsNullOrEmpty(modsRoot) || !Directory.Exists(modsRoot)) return;
            foreach (var dir in Directory.GetDirectories(modsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(dir, JsonFileName);
                if (!File.Exists(file)) continue;
                try
                {
                    if (!ModDisabler.CheckModEnabled(dir, out _)) continue;
                }
                catch
                {
                    continue;
                }
                LoadJson(Path.GetFileName(dir), file);
            }
        }

        /// <summary>Every block, grouped by mod folder. Large: mod packs can add thousands of generated variants.</summary>
        internal static string BuildBlocksReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("FtdModApi block list (regenerated every launch)");
            sb.AppendLine("guid  name  |  display name  |  class  |  tab      [hidden] = not in the build menu");
            if (Configured.i == null)
            {
                sb.AppendLine("The game had not built its content yet.");
                return sb.ToString();
            }
            foreach (var g in Items.GroupBy(i => i.ModFolder).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine();
                sb.AppendLine($"[{g.Key}]  {g.Count()} blocks");
                foreach (var i in g.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"  {i.Guid}  {i.Name}  |  {i.DisplayName}  |  {i.ClassName}  |  {i.Tab}{(i.ShownInBuildMenu ? string.Empty : "   [hidden]")}");
                }
            }
            return sb.ToString();
        }

        internal static string BuildReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("FtdModApi content report (regenerated every launch)");
            sb.AppendLine("Use these names or guids with Content.HideItem / HidePrefab / ReplaceItem / PreventPaste / HideShellPart,");
            sb.AppendLine("or in a " + JsonFileName + " file.");
            sb.AppendLine();

            sb.AppendLine("RULES");
            List<Rule> rules;
            lock (_gate) rules = _rules.ToList();
            if (rules.Count == 0) sb.AppendLine("  None.");
            foreach (var r in rules)
            {
                var what = r.Kind == RuleKind.ReplaceItem ? $"ReplaceItem '{r.Target}' -> '{r.Replacement}'" : $"{r.Kind} '{r.Target}'";
                sb.AppendLine($"  [{r.ModId}] {what}: {r.Outcome}");
            }
            lock (_gate)
            {
                foreach (var kv in _hiddenShellParts.OrderBy(k => k.Key.ToString()))
                    sb.AppendLine($"  [{string.Join(", ", kv.Value.Distinct())}] HideShellPart '{ShellParts.Describe(kv.Key)}': hidden");
            }
            sb.AppendLine();

            sb.AppendLine("APS SHELL PARTS  (game name or modded id, display name; modded parts show what they behave as)");
            try
            {
                var dict = ShellModuleDictionary.Instance.ModuleDictionary;
                foreach (var kv in dict.ToList().OrderBy(k => (int)k.Key))
                {
                    bool hidden = Array.IndexOf(_hiddenShellPartsSnapshot, kv.Key) >= 0;
                    var modded = ShellParts.Find(kv.Key);
                    var extra = modded == null ? string.Empty : $"   [modded by {modded.ModId}, #{modded.Number}, behaves as {modded.BehavesAs}]";
                    sb.AppendLine($"  {ShellParts.Describe(kv.Key),-24} {kv.Value?.Name}{extra}{(hidden ? "   [hidden]" : string.Empty)}");
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("  Could not read shell parts: " + e.Message);
            }
            sb.AppendLine();

            CramPellets.AppendReport(sb);
            MissileParts.AppendReport(sb);
            ApsGauge.AppendReport(sb);

            var configured = Configured.i;
            if (configured == null)
            {
                sb.AppendLine("Blocks and prefabs: the game had not built its content yet.");
                return sb.ToString();
            }

            var allItems = Items;
            sb.AppendLine("BLOCKS BY MOD FOLDER  (shown in build menu / total). Full list: FtdModApi_blocks.txt");
            sb.AppendLine("  Hide a whole folder with Content.HideModFolder or \"hideModFolders\" in JSON.");
            foreach (var g in allItems.GroupBy(i => i.ModFolder).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"  {g.Key,-40} {g.Count(i => i.ShownInBuildMenu),6} / {g.Count(),-6}");
            }
            sb.AppendLine();

            sb.AppendLine("PREFABS  (guid  name  |  mod folder)");
            try
            {
                foreach (var p in configured.Get<ModificationComponentContainerPrefab>().Components
                             .Where(p => p?.ComponentId != null)
                             .OrderBy(p => p.ComponentId.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"  {p.ComponentId.Guid}  {p.ComponentId.Name}  |  {ItemInfo.FolderName(p.ModDirectoryWithSlash)}{(p.DisplayOnInventory ? string.Empty : "   [hidden]")}");
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("  Could not read prefabs: " + e.Message);
            }

            return sb.ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace FtdModApi
{
    /// <summary>Whether an API feature is working in the current game build.</summary>
    public enum FeatureState
    {
        /// <summary>Not attempted yet (the API is still loading, or the feature waits for the main menu).</summary>
        Pending,
        /// <summary>All of the feature's patches applied and passed their checks.</summary>
        Available,
        /// <summary>
        /// Some patches failed. The feature works but with gaps, described in <see cref="FeatureInfo.Detail"/>.
        /// </summary>
        Degraded,
        /// <summary>The feature's required patches failed, usually because a game update changed the code it hooks.</summary>
        Unavailable
    }

    /// <summary>Status of one API feature.</summary>
    public sealed class FeatureInfo
    {
        internal FeatureInfo(string id, string description)
        {
            Id = id;
            Description = description;
        }

        /// <summary>Stable id, e.g. "Damage.Block". Use these with <see cref="Features.IsAvailable"/>.</summary>
        public string Id { get; }

        public string Description { get; }

        public FeatureState State { get; internal set; } = FeatureState.Pending;

        /// <summary>Why the feature is degraded or unavailable; empty when it is fine.</summary>
        public string Detail { get; internal set; } = string.Empty;

        public override string ToString()
            => Id + ": " + State + (string.IsNullOrEmpty(Detail) ? string.Empty : " (" + Detail + ")");
    }

    /// <summary>
    /// Every API feature and whether it survived the current game build. Mods should check
    /// <see cref="IsAvailable"/> before relying on a feature, so a game update degrades
    /// gracefully instead of silently doing nothing.
    /// </summary>
    public static class Features
    {
        public const string BlockDamage = "Damage.Block";
        public const string BlockDamagedEvent = "Damage.BlockDamagedEvent";
        public const string ProjectileLaunch = "Damage.ProjectileLaunch";
        public const string ProjectileSource = "Damage.ProjectileSource";
        public const string Explosion = "Damage.Explosion";
        public const string MainMenuEvent = "Lifecycle.MainMenu";
        public const string ArmourClass = "Blocks.ArmourClass";
        public const string BlockHealth = "Blocks.Health";
        public const string Tunables = "Tunables";
        public const string ReadOnlyTunables = "Tunables.ReadOnly";
        public const string SettingsMenu = "Settings.Menu";
        public const string ContentItems = "Content.Items";
        public const string ContentShellParts = "Content.ShellParts";
        public const string ContentPaste = "Content.PreventPaste";
        public const string ShellPartRegistry = "Content.ShellPartRegistry";
        public const string ShellPartBehaviour = "Content.ShellPartBehaviour";
        public const string CramPellets = "Content.CramPellets";
        public const string CramShellStats = "Cram.ShellStats";
        public const string MissilePartRegistry = "Content.MissilePartRegistry";
        public const string MissilePartBehaviour = "Content.MissilePartBehaviour";
        public const string ApsShellStats = "Aps.ShellStats";
        public const string ApsLoaderTiming = "Aps.LoaderTiming";
        public const string ApsGaugeCap = "Aps.GaugeCap";
        public const string ApsPayloads = "Aps.Payloads";
        public const string EventTicks = "Events.Ticks";
        public const string EventBuildMode = "Events.BuildMode";
        public const string EventConstructLifecycle = "Events.Constructs.Lifecycle";
        public const string EventConstructPlay = "Events.Constructs.Play";
        public const string EventConstructScrap = "Events.Constructs.Scrap";
        public const string EventConstructTeam = "Events.Constructs.Team";
        public const string EventSubconstructs = "Events.Constructs.Subconstructs";
        public const string EventBlockBuild = "Events.Blocks.Build";
        public const string EventBlockDestroyed = "Events.Blocks.Destroyed";
        public const string EventBlockRepaired = "Events.Blocks.Repaired";
        public const string EventWeapons = "Events.Weapons";
        public const string EventAps = "Events.Aps";
        public const string EventCram = "Events.Cram";
        public const string EventMissiles = "Events.Missiles";
        public const string EventProjectiles = "Events.Projectiles";
        public const string EventSession = "Events.Session";
        public const string EventPlayers = "Events.Players";
        public const string EventResources = "Events.Resources";
        public const string EventCampaign = "Events.Campaign";
        public const string EventAi = "Events.Ai";
        public const string EventDetection = "Events.Detection";
        public const string EventLua = "Events.Lua";
        public const string LuaFunctions = "Lua.Functions";
        public const string Keybinds = "Input.Keybinds";
        public const string EventUi = "Events.Ui";
        public const string BreadboardComponents = "Breadboard.Components";
        public const string ModNet = "Net.ModNet";
        public const string CraftData = "Craft.Data";

        private static readonly Dictionary<string, FeatureInfo> _features =
            new Dictionary<string, FeatureInfo>(StringComparer.Ordinal);

        /// <summary>True when the feature is <see cref="FeatureState.Available"/> or <see cref="FeatureState.Degraded"/>.</summary>
        public static bool IsAvailable(string id)
        {
            lock (_features)
            {
                return _features.TryGetValue(id, out var f)
                    && (f.State == FeatureState.Available || f.State == FeatureState.Degraded);
            }
        }

        /// <summary>Status of one feature, or null if the id is unknown.</summary>
        public static FeatureInfo Get(string id)
        {
            lock (_features)
            {
                return _features.TryGetValue(id, out var f) ? f : null;
            }
        }

        /// <summary>All registered features, sorted by id.</summary>
        public static IReadOnlyList<FeatureInfo> All
        {
            get
            {
                lock (_features)
                {
                    return _features.Values.OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
                }
            }
        }

        internal static void SetState(string id, FeatureState state, string detail = "")
        {
            var f = Get(id);
            if (f == null) return;
            lock (_features)
            {
                f.State = state;
                f.Detail = detail ?? string.Empty;
            }
        }

        internal static FeatureInfo Register(string id, string description)
        {
            lock (_features)
            {
                if (!_features.TryGetValue(id, out var f))
                {
                    f = new FeatureInfo(id, description);
                    _features.Add(id, f);
                }
                return f;
            }
        }
    }
}

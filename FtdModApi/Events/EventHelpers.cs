using System;
using System.Linq;
using System.Reflection;
using BrilliantSkies.Ftd.Avatar.Build;
using BrilliantSkies.Modding;
using BrilliantSkies.Modding.Containers;
using BrilliantSkies.Modding.Types;

namespace FtdModApi
{
    /// <summary>Small shared helpers for the event hooks.</summary>
    internal static class EventHelpers
    {
        /// <summary>
        /// The one method with this name declared on the type (not inherited). Throws if there are none or several,
        /// so a game update that adds an overload fails the hook's check instead of patching the wrong method.
        /// </summary>
        internal static MethodInfo Declared(Type type, string name)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            var matches = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == name).ToList();
            if (matches.Count == 0) throw new MissingMethodException(type.FullName, name);
            if (matches.Count > 1) throw new AmbiguousMatchException($"{type.FullName}.{name} has {matches.Count} overloads");
            return matches[0];
        }

        internal static ItemDefinition FindItem(Guid guid)
        {
            try
            {
                var items = Configured.i?.Get<ModificationComponentContainerItem>();
                if (items == null) return null;
                var item = items.Find(guid, out bool found);
                return found ? item : null;
            }
            catch
            {
                return null;
            }
        }

        internal static string ItemName(ItemDefinition item)
        {
            try
            {
                return item?.ComponentId.Name;
            }
            catch
            {
                return null;
            }
        }

        internal static AllConstruct BuildModeConstruct()
        {
            try
            {
                return cBuild.GetSingleton()?.GetC();
            }
            catch
            {
                return null;
            }
        }

        internal static MainConstruct MainOf(AllConstruct construct)
        {
            if (construct == null) return null;
            return construct as MainConstruct ?? construct.Main;
        }

        internal static AllConstruct ConstructOf(Block block)
        {
            if (block == null) return null;
            try
            {
                return block.GetConstructableOrSubConstructable() as AllConstruct;
            }
            catch
            {
                return null;
            }
        }
    
        /// <summary>A prefab blueprint's name, or null when it has none.</summary>
        internal static string PrefabName(Blueprint prefab)
        {
            if (prefab == null) return null;
#pragma warning disable CS0618 // older prefabs keep their name here; newer ones use blueprintName
            if (!string.IsNullOrWhiteSpace(prefab.Name)) return prefab.Name;
#pragma warning restore CS0618
            return string.IsNullOrWhiteSpace(prefab.blueprintName) ? null : prefab.blueprintName;
        }
    
        /// <summary>The craft whose resource store this is, or null (a faction store, or not found).</summary>
        internal static Force ForceOfStore(IResourceStore store)
        {
            if (store == null || store is BrilliantSkies.FromTheDepths.Planets.TeamResourceStore) return null;
            try
            {
                var forces = ForceManager.Instance.forces;
                for (int i = 0; i < forces.Count; i++)
                {
                    var f = forces[i];
                    if (f != null && ReferenceEquals(f.GetResourceStore(), store)) return f;
                }
            }
            catch
            {
                // the list can change under us on a worker thread
            }
            return null;
        }

        /// <summary>A faction's display name, or null.</summary>
        internal static string FactionName(BrilliantSkies.Core.Id.ObjectId faction)
        {
            if (faction == null || !faction.AppearsValid) return null;
            try
            {
                return faction.FactionSpec()?.Name;
            }
            catch
            {
                return null;
            }
        }
    
        /// <summary>A fleet's name, or null.</summary>
        internal static string FleetName(Fleet fleet)
        {
            if (fleet == null) return null;
            try
            {
                return fleet.Name;
            }
            catch
            {
                return null;
            }
        }
    
        /// <summary>A craft's name, or null (also when it has been destroyed and can no longer be read).</summary>
        internal static string SafeName(MainConstruct construct)
        {
            if (construct == null) return null;
            try
            {
                return construct.GetName();
            }
            catch
            {
                return null;
            }
        }
    }
}

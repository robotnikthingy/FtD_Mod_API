using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BrilliantSkies.Core.JsonPlus.Converters;
using BrilliantSkies.Core.Serialisation.Bytes;
using BrilliantSkies.Core.Types;
using BrilliantSkies.DataManagement.Serialisation;
using BrilliantSkies.Ftd.Avatar.Build;
using BrilliantSkies.Ftd.Avatar.Build.UndoRedo;
using BrilliantSkies.Modding;
using BrilliantSkies.Modding.Containers;
using FtdModApi.Patching;
using UnityEngine;

namespace FtdModApi
{
    /// <summary>
    /// Leaves blocks named by <see cref="Content.PreventPaste"/> out of prefab pastes (the build tool's prefab mode,
    /// including prefabs captured from other craft and blueprints).
    /// </summary>
    /// <remarks>
    /// The paste gets its own filtered copy of the prefab when the game creates the paste command, so:
    /// the player's prefab (and any prefab file they save) is never changed; undo and redo use the filtered copy;
    /// in multiplayer the filtered copy is what is sent and placed, so host and clients place the same blocks.
    /// Spawning a blueprint, loading a save, retrofitting and undoing an erase are not affected, so existing
    /// designs never lose blocks.
    /// </remarks>
    internal static class PrefabPaste
    {
        // True while the local player's build tool is pasting (so only they get the "left out" message).
        [ThreadStatic] private static bool _localPaste;

        private static readonly MethodInfo CloneMethod =
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static IEnumerable<PatchSpec> Specs()
        {
            yield return new PatchSpec
            {
                Id = "PrefabCommand..ctor: leave out blocks that may not be pasted",
                FeatureId = Features.ContentPaste,
                Target = () => typeof(PrefabCommand).GetConstructor(new[]
                {
                    typeof(Blueprint), typeof(AllConstruct), typeof(Vector3i), typeof(Quaternion), typeof(MirrorInfo)
                }),
                Prefix = PatchSpec.Hook(typeof(PrefabPaste), nameof(PrefabCommandCtor_Prefix))
            };
            yield return new PatchSpec
            {
                Id = "cBuild.PerformPrefabOperation: tell the player which blocks were left out",
                FeatureId = Features.ContentPaste,
                Required = false,
                Target = () => EventHelpers.Declared(typeof(cBuild), "PerformPrefabOperation"),
                Prefix = PatchSpec.Hook(typeof(PrefabPaste), nameof(Perform_Prefix)),
                Finalizer = PatchSpec.Hook(typeof(PrefabPaste), nameof(Perform_Finalizer))
            };
        }

        private static void Perform_Prefix() => _localPaste = true;

        private static void Perform_Finalizer() => _localPaste = false;

        private static void PrefabCommandCtor_Prefix(ref Blueprint prefab)
        {
            try
            {
                var filtered = Filter(prefab, out var removed);
                if (ReferenceEquals(filtered, prefab)) return;
                prefab = filtered;
                Report(removed, _localPaste);
            }
            catch (Exception e)
            {
                // Never stop the paste itself.
                FtdApi.Log.ErrorOnce("PrefabPaste.Filter", "Checking a prefab paste for blocks that may not be pasted failed; it was pasted unchanged.", e);
            }
        }

        private static void Report(Dictionary<string, Dictionary<string, int>> removed, bool tellPlayer)
        {
            if (removed == null) return;
            foreach (var byMod in removed)
            {
                int total = byMod.Value.Values.Sum();
                var names = string.Join(", ", byMod.Value.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => kv.Value > 1 ? $"{kv.Key} x{kv.Value}" : kv.Key));
                var text = $"{total} block{(total == 1 ? "" : "s")} left out of the pasted prefab (not allowed by {byMod.Key}): {names}";
                var log = FtdApi.GetLog(byMod.Key);
                if (tellPlayer) log.Hud(text);
                else log.Info(text);
            }
        }

        /// <summary>
        /// The prefab without blocked blocks, or <paramref name="prefab"/> itself when nothing is blocked.
        /// Never changes <paramref name="prefab"/>.
        /// </summary>
        internal static Blueprint Filter(Blueprint prefab, out Dictionary<string, Dictionary<string, int>> removedByMod)
        {
            removedByMod = null;
            var blocked = Content.PasteBlocked;
            if (blocked.Count == 0 || prefab?.BlockIds == null || prefab.BlockIds.Length == 0) return prefab;
            var items = Configured.i?.Get<ModificationComponentContainerItem>();
            if (items == null) return prefab;

            int n = prefab.BlockIds.Length;
            bool[] drop = null;
            int dropped = 0;
            for (int i = 0; i < n; i++)
            {
                var def = items.FindUsingTheRuntimeId(prefab.BlockIds[i], out bool found);
                if (!found || def?.ComponentId == null) continue;
                if (!blocked.TryGetValue(def.ComponentId.Guid, out var modId)) continue;

                if (drop == null) drop = new bool[n];
                drop[i] = true;
                dropped++;
                if (removedByMod == null) removedByMod = new Dictionary<string, Dictionary<string, int>>();
                if (!removedByMod.TryGetValue(modId, out var names)) removedByMod[modId] = names = new Dictionary<string, int>();
                var name = ItemInfo.SafeEnglish(def.DisplayName);
                if (string.IsNullOrEmpty(name)) name = def.ComponentId.Name;
                names.TryGetValue(name, out int count);
                names[name] = count + 1;
            }
            if (dropped == 0) return prefab;

            try
            {
                return WithoutBlocks(prefab, drop, n - dropped);
            }
            catch (Exception e)
            {
                // Unexpected prefab layout: keep every array as it is and only make the blocked entries unplaceable
                // (the game skips block ids it cannot find). Correct locally; multiplayer logs a lookup error per block.
                FtdApi.Log.WarningOnce("PrefabPaste.Layout", "A prefab had an unexpected data layout (" + e.Message + "); blocked blocks are skipped without rebuilding it.");
                var copy = Clone(prefab);
                copy.BlockIds = (int[])prefab.BlockIds.Clone();
                for (int i = 0; i < n; i++) if (drop[i]) copy.BlockIds[i] = -1;
                return copy;
            }
        }

        private static Blueprint WithoutBlocks(Blueprint prefab, bool[] drop, int kept)
        {
            int n = drop.Length;
            var newIndex = new int[n];
            for (int i = 0, k = 0; i < n; i++) newIndex[i] = drop[i] ? -1 : k++;

            var copy = Clone(prefab);
            copy.BlockIds = Keep(prefab.BlockIds, drop, kept, nameof(Blueprint.BlockIds));
            copy.BLP = Keep(prefab.BLP, drop, kept, nameof(Blueprint.BLP));
            copy.BLR = Keep(prefab.BLR, drop, kept, nameof(Blueprint.BLR));
            copy.BCI = Keep(prefab.BCI, drop, kept, nameof(Blueprint.BCI));
            if (prefab.BP1 != null) copy.BP1 = Keep(prefab.BP1, drop, kept, nameof(Blueprint.BP1));
            if (prefab.BP2 != null) copy.BP2 = Keep(prefab.BP2, drop, kept, nameof(Blueprint.BP2));
            if (prefab.BlockState != null) copy.BlockState = Keep(prefab.BlockState, drop, kept, nameof(Blueprint.BlockState));
            copy.BlockData = KeepBlockData(prefab.BlockData, newIndex);
            copy.BEI = KeepExtraInfo(prefab.BEI, newIndex);

            copy.AliveCount = copy.BlockState == null ? kept : copy.BlockState.Count(s => s == DeadState.Alive);
            int removed = n - kept;
            if (prefab.TotalBlockCount >= removed) copy.TotalBlockCount = prefab.TotalBlockCount - removed;
            copy.BytesTransferBlockIdsGuids = null;
            ResetCostCache(copy);
            return copy;
        }

        private static Blueprint Clone(Blueprint prefab)
        {
            var copy = (Blueprint)CloneMethod.Invoke(prefab, null);
            ResetCostCache(copy);
            return copy;
        }

        private static FieldInfo _costCache;
        private static bool _costCacheLooked;

        // The cost cache object would otherwise be shared with the original prefab.
        private static void ResetCostCache(Blueprint copy)
        {
            if (!_costCacheLooked)
            {
                _costCache = typeof(Blueprint).GetField("_resourceCost", BindingFlags.Instance | BindingFlags.NonPublic);
                _costCacheLooked = true;
            }
            try
            {
                _costCache?.SetValue(copy, null); // the game recreates it when needed
            }
            catch
            {
                // Only a cache.
            }
        }

        private static T[] Keep<T>(T[] source, bool[] drop, int kept, string what)
        {
            if (source == null) throw new InvalidDataException(what + " is missing");
            if (source.Length != drop.Length) throw new InvalidDataException($"{what} has {source.Length} entries for {drop.Length} blocks");
            var result = new T[kept];
            for (int i = 0, k = 0; i < source.Length; i++)
            {
                if (!drop[i]) result[k++] = source[i];
            }
            return result;
        }

        // Block settings: entries of [3-byte block index][saved data], in block order (written by SuperSaver).
        private static byte[] KeepBlockData(byte[] data, int[] newIndex)
        {
            if (data == null || data.Length == 0) return data;
            var output = new MemoryStream(data.Length);
            var loader = new SuperLoader();
            uint position = 0;
            while (position < data.Length)
            {
                uint start = position;
                if (start + 3 > data.Length) throw new InvalidDataException("block data ends inside an entry");
                ulong index = ByteConversion.ConvertOutAnUnsignedInt(data, start, 3);
                loader.Deserialise(data, ref position, 3);
                if (position <= start || position > data.Length) throw new InvalidDataException("a block data entry could not be read");
                if (index < (ulong)newIndex.Length && newIndex[index] >= 0)
                {
                    var entry = new byte[position - start];
                    Buffer.BlockCopy(data, (int)start, entry, 0, entry.Length);
                    ByteConversion.ConvertInAnUnsignedInt(entry, 0, 3, (ulong)newIndex[index]);
                    output.Write(entry, 0, entry.Length);
                }
            }
            return output.ToArray();
        }

        // Extra block info: entries of [block index, length, values...].
        private static double[] KeepExtraInfo(double[] bei, int[] newIndex)
        {
            if (bei == null || bei.Length == 0) return bei;
            var result = new List<double>(bei.Length);
            int j = 0;
            while (j < bei.Length)
            {
                if (j + 1 >= bei.Length) throw new InvalidDataException("extra block info ends inside an entry");
                int index = (int)bei[j];
                int length = (int)bei[j + 1];
                if (length < 0 || j + 2 + length > bei.Length) throw new InvalidDataException("extra block info has a bad length");
                if (index >= 0 && index < newIndex.Length && newIndex[index] >= 0)
                {
                    result.Add(newIndex[index]);
                    result.Add(length);
                    for (int t = 0; t < length; t++) result.Add(bei[j + 2 + t]);
                }
                j += 2 + length;
            }
            return result.ToArray();
        }
    }
}

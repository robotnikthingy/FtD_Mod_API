using System;
using System.Collections.Generic;
using System.Text;

namespace FtdModApi
{
    /// <summary>
    /// Turns a mod's string id ("MyWW2Mod.ShellPart.Cordite") into a numeric id that is the same on every
    /// machine, every launch and every install order. Use it wherever the game saves content as a number
    /// (APS shell parts are saved as ints, for example), so blueprints keep working across mod updates.
    /// </summary>
    /// <remarks>
    /// Numbers are derived from the string with a fixed hash (FNV-1a) into a reserved range far above
    /// anything the game uses, so nothing has to be stored. If two different strings land on the same
    /// number in the same category, the second registration fails with a clear error and the mod
    /// author renames one of them.
    /// </remarks>
    public static class ContentIds
    {
        /// <summary>Smallest id handed out. Game enums use small values (APS shell parts are 0–35).</summary>
        public const int FirstModdedId = 1_000_000;

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, Dictionary<int, string>> _byCategory =
            new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);

        /// <summary>
        /// The numeric id for <paramref name="id"/> within <paramref name="category"/> (e.g. "ApsShellPart").
        /// Calling it again with the same strings returns the same number. Ids are case-sensitive.
        /// </summary>
        /// <exception cref="InvalidOperationException">A different id in this category already has this number.</exception>
        public static int Register(string category, string id)
        {
            if (string.IsNullOrEmpty(category)) throw new ArgumentException("Category is required.", nameof(category));
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Id is required.", nameof(id));

            int number = Compute(category, id);
            lock (_gate)
            {
                if (!_byCategory.TryGetValue(category, out var map))
                {
                    map = new Dictionary<int, string>();
                    _byCategory.Add(category, map);
                }
                if (map.TryGetValue(number, out var existing))
                {
                    if (existing == id) return number;
                    throw new InvalidOperationException(
                        $"Content id '{id}' collides with '{existing}' in category '{category}' (both map to {number}). Rename one of them.");
                }
                map.Add(number, id);
                return number;
            }
        }

        /// <summary>The string id registered for a number, or null.</summary>
        public static string Lookup(string category, int number)
        {
            lock (_gate)
            {
                return _byCategory.TryGetValue(category, out var map) && map.TryGetValue(number, out var id) ? id : null;
            }
        }

        /// <summary>True for numbers in the modded range (i.e. not the game's own).</summary>
        public static bool IsModded(int number) => number >= FirstModdedId;

        /// <summary>The number <see cref="Register"/> would return, without registering it.</summary>
        public static int Compute(string category, string id)
        {
            // FNV-1a, 32-bit, over UTF-8 of "category\0id". Fixed forever: changing it would break saves.
            uint hash = 2166136261;
            foreach (var b in Encoding.UTF8.GetBytes(category + "\0" + id))
            {
                hash ^= b;
                hash *= 16777619;
            }
            const uint range = int.MaxValue - (uint)FirstModdedId;
            return FirstModdedId + (int)(hash % range);
        }
    }
}

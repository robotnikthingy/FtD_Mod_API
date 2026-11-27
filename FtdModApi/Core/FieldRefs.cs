using System;
using System.Reflection;
using HarmonyLib;

namespace FtdModApi
{
    /// <summary>
    /// Wraps <see cref="AccessTools.FieldRefAccess{T, F}(string)"/>. In the game it is the same call. In the offline
    /// patch checker (.NET 8) Harmony 2.2.2 cannot build the field accessor (it needs MonoMod's runtime support), so
    /// there the field is only checked: it must exist on <typeparamref name="T"/>, be an instance field, and have a
    /// type <typeparamref name="F"/> can hold, the same rules Harmony applies in game.
    /// </summary>
    internal static class FieldRefs
    {
        internal static AccessTools.FieldRef<T, F> Get<T, F>(string name)
        {
            if (!ModLog.Offline) return AccessTools.FieldRefAccess<T, F>(name);

            var field = AccessTools.Field(typeof(T), name);
            if (field == null)
                throw new MissingFieldException(typeof(T).FullName, name);
            if (field.IsStatic)
                throw new ArgumentException($"{typeof(T).FullName}.{name} is static; an instance field was expected");
            bool fits = typeof(F).IsValueType || field.FieldType.IsValueType
                ? typeof(F) == field.FieldType
                : typeof(F).IsAssignableFrom(field.FieldType);
            if (!fits)
                throw new ArgumentException($"{typeof(T).FullName}.{name} is {field.FieldType.FullName}, not {typeof(F).FullName}");
            return _ => throw new InvalidOperationException("Field accessors are not available in the offline patch checker.");
        }
    }
}

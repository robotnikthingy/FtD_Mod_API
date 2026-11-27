using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace FtdModApi.Core
{
    /// <summary>
    /// FtD loads each mod DLL with Assembly.LoadFile, so a mod's reference to "FtdModApi" or "0Harmony"
    /// is not bound to an already-loaded copy automatically. This handler points those references at the
    /// copies shipped with the API, so dependent mods never need to bundle Harmony themselves.
    ///
    /// Must not touch any Harmony type: it runs before Harmony is resolved.
    /// </summary>
    internal static class AssemblyResolver
    {
        private static readonly string[] SharedAssemblies = { "FtdModApi", "0Harmony" };
        private static bool _installed;

        internal static string ApiDirectory { get; private set; }

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                ApiDirectory = Path.GetDirectoryName(typeof(AssemblyResolver).Assembly.Location);
            }
            catch
            {
                ApiDirectory = null;
            }
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            AssemblyName requested;
            try
            {
                requested = new AssemblyName(args.Name);
            }
            catch
            {
                return null;
            }

            if (!SharedAssemblies.Contains(requested.Name, StringComparer.OrdinalIgnoreCase)) return null;

            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => string.Equals(SafeName(a), requested.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // 1. The copy that ships next to the API.
            var ours = loaded.FirstOrDefault(a => IsInApiDirectory(a));
            if (ours != null) return ours;

            // 2. Any loaded copy at least as new as the one requested.
            var compatible = loaded
                .Where(a => requested.Version == null || a.GetName().Version >= requested.Version)
                .OrderByDescending(a => a.GetName().Version)
                .FirstOrDefault();
            if (compatible != null) return compatible;

            // 3. Load it from the API folder.
            if (ApiDirectory != null)
            {
                var path = Path.Combine(ApiDirectory, requested.Name + ".dll");
                if (File.Exists(path))
                {
                    try
                    {
                        return Assembly.LoadFile(path);
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
            return null;
        }

        private static string SafeName(Assembly a)
        {
            try
            {
                return a.GetName().Name;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsInApiDirectory(Assembly a)
        {
            if (ApiDirectory == null) return false;
            try
            {
                if (a.IsDynamic || string.IsNullOrEmpty(a.Location)) return false;
                return string.Equals(
                    Path.GetFullPath(Path.GetDirectoryName(a.Location)),
                    Path.GetFullPath(ApiDirectory),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}

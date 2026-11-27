using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace FtdModApiPatchCheck
{
    /// <summary>
    /// Checks every FtdModApi patch against a From The Depths install without running the game.
    /// Usage: FtdModApi.PatchChecker [--managed "path\to\From_The_Depths_Data\Managed"] [--out report.txt] [--all] [--verbose]
    /// Exit code: 0 = every required patch fits, 1 = some feature is broken, 2 = the check couldn't run.
    /// </summary>
    internal static class Program
    {
        private const string DefaultManaged = @"C:\Program Files (x86)\Steam\steamapps\common\From The Depths\From_The_Depths_Data\Managed";

        private static string _managed;

        private static int Main(string[] args)
        {
            string managed = null;
            string outFile = "FtdModApi_patchcheck.txt";
            bool all = false, verbose = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--managed" when i + 1 < args.Length: managed = args[++i]; break;
                    case "--out" when i + 1 < args.Length: outFile = args[++i]; break;
                    case "--all": all = true; break;
                    case "--verbose": verbose = true; break;
                    case "--no-transpilers": global::FtdModApi.PatchChecker.SkipTranspilers = true; break;
                    case "--help":
                    case "-h":
                    case "/?":
                        Console.WriteLine("FtdModApi patch checker: checks every FtdModApi patch against a From The Depths install, without running the game.");
                        Console.WriteLine("  --managed <folder>  the game's From_The_Depths_Data\\Managed folder (default: the Steam install, or FTD_MANAGED)");
                        Console.WriteLine("  --out <file>        where to write the full report (default: FtdModApi_patchcheck.txt)");
                        Console.WriteLine("  --all               also list OK patches on screen");
                        Console.WriteLine("  --verbose           also show what the API logged while checking");
                        Console.WriteLine("  --no-transpilers    don't run transpilers on the game's code (if that part ever crashes)");
                        return 0;
                    default:
                        Console.Error.WriteLine("Unknown option: " + args[i] + " (try --help)");
                        return 2;
                }
            }

            managed ??= Environment.GetEnvironmentVariable("FTD_MANAGED") ?? DefaultManaged;
            if (!File.Exists(Path.Combine(managed, "Ftd.dll")))
            {
                Console.Error.WriteLine($"Ftd.dll was not found in \"{managed}\". Pass the game's Managed folder with --managed.");
                return 2;
            }
            _managed = managed;
            AssemblyLoadContext.Default.Resolving += Resolve;

            Console.WriteLine($"Checking FtdModApi patches against {managed}");
            object report;
            try
            {
                report = Run(outFile, all, verbose);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("The check could not run: " + e);
                return 2;
            }
            return (bool)report ? 0 : 1;
        }

        // Kept separate so FtdModApi (and through it the game's assemblies) only load after the resolver is in place.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object Run(string outFile, bool all, bool verbose)
        {
            var report = global::FtdModApi.PatchChecker.Run(offline: true, verbose: verbose);
            var full = report.ToText(includeOk: true);
            try
            {
                File.WriteAllText(outFile, full);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Could not write {outFile}: {e.Message}");
            }

            Console.WriteLine();
            Console.WriteLine(report.ToText(includeOk: all));
            if (verbose)
            {
                Console.WriteLine("What the API logged while checking:");
                foreach (var line in global::FtdModApi.PatchChecker.OfflineLog) Console.WriteLine("  " + line);
            }
            Console.WriteLine($"Full report: {Path.GetFullPath(outFile)}");
            return report.AllRequiredOk;
        }

        // Game assemblies come from the game's folder; the runtime's own (mscorlib, System...) are never replaced.
        private static Assembly Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            if (name.Name == "mscorlib" || name.Name == "netstandard") return null;
            var path = Path.Combine(_managed, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }
    }
}

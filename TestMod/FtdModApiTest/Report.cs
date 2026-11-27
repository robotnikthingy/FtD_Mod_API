using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FtdModApi;

namespace FtdModApiTest
{
    /// <summary>Outcome of one check.</summary>
    internal enum CheckStatus
    {
        /// <summary>Worked.</summary>
        Pass,

        /// <summary>Did not work.</summary>
        Fail,

        /// <summary>Not seen yet: needs something to happen in game first.</summary>
        Waiting,

        /// <summary>Information only.</summary>
        Info
    }

    internal sealed class Check
    {
        public string Group;
        public string Name;
        public CheckStatus Status;
        public string Detail;
        public string Hint;
        public long Count;
        public int Order;
    }

    /// <summary>
    /// Every check and its result, and the report file. Thread-safe: game worker threads report here too.
    /// </summary>
    internal static class Report
    {
        internal const string FileName = "FtdModApiTest_report.txt";

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, Check> _checks = new Dictionary<string, Check>(StringComparer.Ordinal);
        private static readonly List<string> _groupOrder = new List<string>();
        private static int _order;
        private static long _changes;
        private static long _changesWritten = -1;

        private static Check Get(string group, string name)
        {
            var key = group + "\u0001" + name;
            if (!_checks.TryGetValue(key, out var c))
            {
                c = new Check { Group = group, Name = name, Status = CheckStatus.Waiting, Order = _order++ };
                _checks.Add(key, c);
                if (!_groupOrder.Contains(group)) _groupOrder.Add(group);
                _changes++;
            }
            return c;
        }

        /// <summary>Declares a check that needs something to happen in game, with what to do.</summary>
        public static void Expect(string group, string name, string hint)
        {
            lock (_gate)
            {
                var c = Get(group, name);
                c.Hint = hint;
            }
        }

        public static void Pass(string group, string name, string detail = null) => Set(group, name, CheckStatus.Pass, detail);

        public static void Info(string group, string name, string detail) => Set(group, name, CheckStatus.Info, detail);

        /// <summary>Marks a check failed. A failed check stays failed (later passes do not hide it).</summary>
        public static void Fail(string group, string name, string detail)
        {
            Set(group, name, CheckStatus.Fail, detail);
            Plugin.Log.Warning($"Test FAILED: {group} / {name}: {detail}");
        }

        /// <summary>Passes or fails from a condition.</summary>
        public static bool Assert(string group, string name, bool ok, string detailIfOk, string detailIfFailed)
        {
            if (ok) Pass(group, name, detailIfOk);
            else Fail(group, name, detailIfFailed);
            return ok;
        }

        /// <summary>Runs a check; an exception fails it.</summary>
        public static void Run(string group, string name, Func<string> test)
        {
            try
            {
                var detail = test();
                Pass(group, name, detail);
            }
            catch (CheckFailed e)
            {
                Fail(group, name, e.Message);
            }
            catch (Exception e)
            {
                Fail(group, name, e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Counts one occurrence; the first one passes the check and keeps its detail.</summary>
        public static void Seen(string group, string name, string firstDetail = null)
        {
            lock (_gate)
            {
                var c = Get(group, name);
                c.Count++;
                if (c.Status == CheckStatus.Waiting)
                {
                    c.Status = CheckStatus.Pass;
                    c.Detail = firstDetail;
                    _changes++;
                }
                else if (c.Count <= 1000 && (c.Count & (c.Count - 1)) == 0)
                {
                    _changes++; // counts shown in the report: rewrite now and then (1, 2, 4, 8, ...)
                }
            }
        }

        private static void Set(string group, string name, CheckStatus status, string detail)
        {
            lock (_gate)
            {
                var c = Get(group, name);
                if (c.Status == CheckStatus.Fail && status != CheckStatus.Fail) return;
                if (c.Status == status && c.Detail == detail) return;
                c.Status = status;
                c.Detail = detail;
                _changes++;
            }
        }

        public static (int pass, int fail, int waiting) Counts()
        {
            lock (_gate)
            {
                return (_checks.Values.Count(c => c.Status == CheckStatus.Pass),
                        _checks.Values.Count(c => c.Status == CheckStatus.Fail),
                        _checks.Values.Count(c => c.Status == CheckStatus.Waiting));
            }
        }

        public static string Summary()
        {
            var (pass, fail, waiting) = Counts();
            return $"{pass} passed, {fail} failed, {waiting} not seen yet";
        }

        public static string Directory()
        {
            try
            {
                return Path.Combine(BrilliantSkies.Core.Constants.Get.PermanentPaths.RootModDir().ToString(), "FtdModApiTest");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Writes the report if anything changed since the last write (or always when forced).</summary>
        public static bool Write(bool force)
        {
            string text;
            lock (_gate)
            {
                if (!force && _changes == _changesWritten) return false;
                _changesWritten = _changes;
                text = Build();
            }
            var dir = Directory();
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, FileName), text);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.WarningOnce("report-write", "Could not write the test report: " + e.Message);
                return false;
            }
        }

        private static string Label(CheckStatus s)
        {
            switch (s)
            {
                case CheckStatus.Pass: return "PASS";
                case CheckStatus.Fail: return "FAIL";
                case CheckStatus.Info: return "info";
                default: return "....";
            }
        }

        private static string Build()
        {
            var sb = new StringBuilder();
            var (pass, fail, waiting) = (_checks.Values.Count(c => c.Status == CheckStatus.Pass),
                                         _checks.Values.Count(c => c.Status == CheckStatus.Fail),
                                         _checks.Values.Count(c => c.Status == CheckStatus.Waiting));
            sb.AppendLine($"FtdModApi test report: FtdModApiTest {Plugin.TestModVersion}, FtdModApi {FtdApi.Version}, From The Depths {SafeGameVersion()}");
            sb.AppendLine($"Written {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}. Rewritten while you play (every 30 s when something changed),");
            sb.AppendLine("when you press the report key (Ctrl+Shift+T by default) and when the game quits.");
            sb.AppendLine();
            sb.AppendLine($"SUMMARY: {pass} passed, {fail} failed, {waiting} not seen yet.");
            if (fail == 0) sb.AppendLine("Nothing has failed.");
            else
            {
                sb.AppendLine("FAILED:");
                foreach (var c in _checks.Values.Where(c => c.Status == CheckStatus.Fail).OrderBy(c => c.Order))
                    sb.AppendLine($"  {c.Group} / {c.Name}: {c.Detail}");
            }
            sb.AppendLine();
            sb.AppendLine("PASS = worked.  FAIL = did not work (please report it with this file and the game log).");
            sb.AppendLine(".... = not seen yet: do what the hint says, then press the report key again.");
            sb.AppendLine("Counts in [brackets] are how often something happened this session.");
            sb.AppendLine("This mod only watches: it changes no gameplay. Its test parts behave exactly like the game parts they copy.");

            foreach (var group in _groupOrder)
            {
                var checks = _checks.Values.Where(c => c.Group == group).OrderBy(c => c.Order).ToList();
                int gp = checks.Count(c => c.Status == CheckStatus.Pass), gf = checks.Count(c => c.Status == CheckStatus.Fail);
                sb.AppendLine();
                sb.AppendLine($"[{group}]  {gp}/{checks.Count(c => c.Status != CheckStatus.Info)} passed{(gf > 0 ? $", {gf} FAILED" : "")}");
                foreach (var c in checks)
                {
                    var count = c.Count > 0 ? $" [{c.Count}]" : string.Empty;
                    string text;
                    if (c.Status == CheckStatus.Waiting)
                        text = string.IsNullOrEmpty(c.Hint) ? "not seen yet" : "not seen yet: " + c.Hint;
                    else
                        text = c.Detail ?? string.Empty;
                    sb.AppendLine($"  {Label(c.Status)}  {c.Name}{count}{(text.Length > 0 ? "  - " + text : string.Empty)}");
                }
            }
            return sb.ToString();
        }

        private static string SafeGameVersion()
        {
            try
            {
                return FtdApi.GameVersion;
            }
            catch
            {
                return "?";
            }
        }
    }

    /// <summary>Thrown by a check to fail with a readable reason.</summary>
    internal sealed class CheckFailed : Exception
    {
        public CheckFailed(string message) : base(message)
        {
        }

        public static void If(bool condition, string message)
        {
            if (condition) throw new CheckFailed(message);
        }
    }
}

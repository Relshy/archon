// fps cap + cpu limits.
//
// fps goes through DFIntTaskSchedulerTargetFps in ClientAppSettings.json,
// roblox's own override file, same thing bloxstrap uses. nothing is patched.
// priority and cores are just Process.PriorityClass / ProcessorAffinity.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Archon
{
    class PerformanceSettings
    {
        public const int Unlimited = 9999;

        // 0 means leave roblox's own default alone
        public int TargetFps;

        public ProcessPriorityClass Priority = ProcessPriorityClass.Normal;

        // 0 means all cores
        public int CoreLimit;

        private static string FilePath
        {
            get { return Path.Combine(AccountStore.Directory, "performance.txt"); }
        }

        public static PerformanceSettings Load()
        {
            PerformanceSettings s = new PerformanceSettings();
            string path = FilePath;
            if (!File.Exists(path)) return s;

            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq);
                    string value = line.Substring(eq + 1);

                    if (key == "fps")
                    {
                        int fps;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out fps))
                            s.TargetFps = fps;
                    }
                    else if (key == "priority")
                    {
                        try { s.Priority = (ProcessPriorityClass)Enum.Parse(typeof(ProcessPriorityClass), value); }
                        catch { }
                    }
                    else if (key == "cores")
                    {
                        int cores;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out cores))
                            s.CoreLimit = cores;
                    }
                }
            }
            catch { }

            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AccountStore.Directory);
                StringBuilder sb = new StringBuilder();
                sb.Append("fps=").Append(TargetFps.ToString(CultureInfo.InvariantCulture)).AppendLine();
                sb.Append("priority=").Append(Priority.ToString()).AppendLine();
                sb.Append("cores=").Append(CoreLimit.ToString(CultureInfo.InvariantCulture)).AppendLine();
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    static class RobloxTuning
    {
        // each of these keeps its own roblox install, so write to whichever exist
        private static readonly string[] BootstrapperNames = { "Roblox", "Bloxstrap", "Fishstrap" };

        private const string FpsFlag = "DFIntTaskSchedulerTargetFps";

        // this gets called on every poll, not just when the setting changes.
        // roblox's bootstrapper resets ClientAppSettings.json to {} while it's
        // starting a client, so the cap has to get put back before the client
        // reads it. only read at client startup, so running clients don't care.
        private static readonly Dictionary<string, DateTime> _seenStamp = new Dictionary<string, DateTime>();
        private static int _lastTarget = -1;

        public static void ApplyFpsCap(int targetFps)
        {
            foreach (string versionDir in FindVersionFolders())
            {
                string settingsDir = Path.Combine(versionDir, "ClientSettings");
                string settingsPath = Path.Combine(settingsDir, "ClientAppSettings.json");

                try
                {
                    // don't even read it if nothing changed since last time
                    DateTime stamp;
                    try
                    {
                        stamp = File.Exists(settingsPath)
                            ? File.GetLastWriteTimeUtc(settingsPath)
                            : DateTime.MinValue;
                    }
                    catch { stamp = DateTime.MinValue; }

                    DateTime lastSeen;
                    if (targetFps == _lastTarget &&
                        _seenStamp.TryGetValue(settingsPath, out lastSeen) &&
                        lastSeen == stamp)
                        continue;

                    _seenStamp[settingsPath] = stamp;

                    string original = stamp == DateTime.MinValue ? null : File.ReadAllText(settingsPath);

                    string updated = SetFlag(original, FpsFlag, targetFps);
                    if (updated == null) continue;          // already right, or not ours to touch
                    if (updated == original) continue;

                    Directory.CreateDirectory(settingsDir);
                    WriteAtomic(settingsPath, updated);

                    try { _seenStamp[settingsPath] = File.GetLastWriteTimeUtc(settingsPath); }
                    catch { _seenStamp.Remove(settingsPath); }
                }
                catch { /* one unwritable install should not block the rest */ }
            }

            _lastTarget = targetFps;
        }

        // installs only change on update, no point walking three trees every tick
        private static string[] _cachedVersionDirs;
        private static DateTime _versionDirsAsOf;

        private static IEnumerable<string> FindVersionFolders()
        {
            if (_cachedVersionDirs != null &&
                (DateTime.UtcNow - _versionDirsAsOf).TotalSeconds < 30)
                return _cachedVersionDirs;

            List<string> found = new List<string>(ScanVersionFolders());
            _cachedVersionDirs = found.ToArray();
            _versionDirsAsOf = DateTime.UtcNow;
            return _cachedVersionDirs;
        }

        private static IEnumerable<string> ScanVersionFolders()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            foreach (string bootstrapper in BootstrapperNames)
            {
                string versionsRoot = Path.Combine(localAppData, bootstrapper, "Versions");
                if (!Directory.Exists(versionsRoot)) continue;

                string[] versionDirs;
                try { versionDirs = Directory.GetDirectories(versionsRoot); }
                catch { continue; }

                foreach (string dir in versionDirs)
                    if (File.Exists(Path.Combine(dir, "RobloxPlayerBeta.exe")))
                        yield return dir;
            }
        }

        // edits one key in the file's text and leaves every other byte alone.
        // don't swap this for parse -> dictionary -> reserialise, that mangles
        // decimals, big ints and nested objects, and bloxstrap users have
        // whole fflag files sitting in here. returns null if there's nothing
        // to do or the file isn't an object literal.
        private static string SetFlag(string json, string key, int value)
        {
            bool wanted = value > 0;
            string number = value.ToString(CultureInfo.InvariantCulture);
            string pair = "\"" + key + "\":" + number;

            if (json == null || json.Trim().Length == 0)
                return wanted ? "{" + pair + "}" : null;

            string trimmed = json.Trim();
            if (trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}')
                return null;   // not an object literal, don't touch it

            Regex entry = new Regex(
                Regex.Escape("\"" + key + "\"") +
                "\\s*:\\s*(-?\\d+(?:\\.\\d+)?|true|false|\"(?:[^\"\\\\]|\\\\.)*\")");

            Match found = entry.Match(json);

            if (wanted)
            {
                if (found.Success)
                {
                    if (found.Groups[1].Value == number) return null;   // already correct
                    return json.Substring(0, found.Index) + pair +
                           json.Substring(found.Index + found.Length);
                }

                int brace = json.IndexOf('{');
                if (brace < 0) return null;

                bool empty = Regex.IsMatch(json, "^\\s*\\{\\s*\\}\\s*$");
                return json.Substring(0, brace + 1) + pair + (empty ? "" : ",") +
                       json.Substring(brace + 1);
            }

            if (!found.Success) return null;   // nothing to clear

            string without = json.Substring(0, found.Index) +
                             json.Substring(found.Index + found.Length);

            // clean up the comma the removal left behind
            without = Regex.Replace(without, "\\{\\s*,", "{");
            without = Regex.Replace(without, ",\\s*\\}", "}");
            without = Regex.Replace(without, ",\\s*,", ",");
            return without;
        }

        // temp file then swap, so a client reading this exact moment can't catch
        // it half written. WriteAllText truncates first and we're racing roblox's
        // bootstrapper on the same path.
        private static void WriteAtomic(string path, string content)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));

            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        // what we last set per pid. only write when it actually changes,
        // otherwise we stomp on anything the user does in task manager.
        private static readonly Dictionary<int, string> _appliedTo = new Dictionary<int, string>();

        public static void ApplyResourceLimits(Process p, ProcessPriorityClass priority, int coreLimit)
        {
            int pid;
            try { pid = p.Id; } catch { return; }

            string wanted = priority + "/" + coreLimit.ToString(CultureInfo.InvariantCulture);

            string already;
            if (_appliedTo.TryGetValue(pid, out already) && already == wanted) return;
            _appliedTo[pid] = wanted;

            try { p.PriorityClass = priority; } catch { }

            if (coreLimit <= 0) return;

            try
            {
                int totalCores = Environment.ProcessorCount;
                if (coreLimit >= totalCores) return;

                // lowest N logical cpus. on a hybrid chip that's hyperthread
                // pairs not real cores, and past 64 cpus processor groups get
                // ignored. one mask can't express either, so, whatever.
                long mask = coreLimit >= 64 ? -1L : (1L << coreLimit) - 1L;
                p.ProcessorAffinity = (IntPtr)mask;
            }
            catch { }
        }

        public static void ForgetExited(ICollection<int> livePids)
        {
            if (_appliedTo.Count == 0) return;

            List<int> gone = new List<int>();
            foreach (int pid in _appliedTo.Keys)
                if (!livePids.Contains(pid)) gone.Add(pid);

            foreach (int pid in gone) _appliedTo.Remove(pid);
        }
    }
}

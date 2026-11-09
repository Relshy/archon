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

        // the last cap this app wrote. going back to default only takes out a
        // flag that still holds it - any other value was set in bloxstrap or
        // by hand, and default means leave it alone, not delete it.
        private static int _ourFps;

        public static void ApplyFpsCap(int targetFps)
        {
            if (targetFps > 0) _ourFps = targetFps;
            else if (_ourFps <= 0)
            {
                // never capped anything, so there's nothing of ours to undo
                _lastTarget = targetFps;
                return;
            }

            foreach (string versionDir in FindVersionFolders())
            {
                string settingsDir = Path.Combine(versionDir, "ClientSettings");
                string settingsPath = Path.Combine(settingsDir, "ClientAppSettings.json");

                try
                {
                    // don't even read it if nothing changed since last time.
                    // a failed stat has to skip the file, not count as it
                    // being missing - "missing" gets a fresh file holding only
                    // our flag, written over whatever else is in there.
                    DateTime stamp = File.Exists(settingsPath)
                        ? File.GetLastWriteTimeUtc(settingsPath)
                        : DateTime.MinValue;

                    DateTime lastSeen;
                    if (targetFps == _lastTarget &&
                        _seenStamp.TryGetValue(settingsPath, out lastSeen) &&
                        lastSeen == stamp)
                        continue;

                    _seenStamp[settingsPath] = stamp;

                    string original = stamp == DateTime.MinValue ? null : File.ReadAllText(settingsPath);

                    string updated = SetFlag(original, FpsFlag, targetFps, _ourFps);
                    if (updated == null) continue;          // already right, or not ours to touch
                    if (updated == original) continue;

                    Directory.CreateDirectory(settingsDir);
                    WriteAtomic(settingsPath, updated);

                    try { _seenStamp[settingsPath] = File.GetLastWriteTimeUtc(settingsPath); }
                    catch { _seenStamp.Remove(settingsPath); }
                }
                catch (IOException)
                {
                    // usually the bootstrapper holding the file mid-write, the
                    // exact moment the cap matters. the stamp is already
                    // recorded, so without forgetting it every later poll
                    // would skip this file until something else changed it.
                    _seenStamp.Remove(settingsPath);
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

        // an update adds a version folder, and the settings file written into it
        // is what the next client reads - no waiting out the cache for that one
        public static void ForgetVersionFolders()
        {
            _cachedVersionDirs = null;
        }

        // the Versions folder of every bootstrapper that's installed
        public static IEnumerable<string> VersionRoots()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            foreach (string bootstrapper in BootstrapperNames)
            {
                string versionsRoot = Path.Combine(localAppData, bootstrapper, "Versions");
                if (Directory.Exists(versionsRoot)) yield return versionsRoot;
            }
        }

        private static IEnumerable<string> ScanVersionFolders()
        {
            foreach (string versionsRoot in VersionRoots())
            {
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
        // a value of 0 clears the key, but only while it still holds `ours`.
        private static string SetFlag(string json, string key, int value, int ours)
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
            if (found.Groups[1].Value != ours.ToString(CultureInfo.InvariantCulture))
                return null;                   // not ours to clear

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

        // what we last set per pid. only write what actually changed,
        // otherwise we stomp on anything the user does in task manager.
        private struct Applied
        {
            public ProcessPriorityClass Priority;
            public int Cores;   // 0 means every core
        }

        private static readonly Dictionary<int, Applied> _appliedTo = new Dictionary<int, Applied>();

        public static void ApplyResourceLimits(Process p, ProcessPriorityClass priority, int coreLimit)
        {
            int pid;
            try { pid = p.Id; } catch { return; }

            // 0 and anything at or past the core count both mean every core
            int totalCores = Environment.ProcessorCount;
            int cores = coreLimit > 0 && coreLimit < totalCores ? coreLimit : 0;

            Applied before;
            bool seen = _appliedTo.TryGetValue(pid, out before);
            if (seen && before.Priority == priority && before.Cores == cores) return;

            Applied now;
            now.Priority = priority;
            now.Cores = cores;
            _appliedTo[pid] = now;

            if (!seen || before.Priority != priority)
            {
                try { p.PriorityClass = priority; } catch { }
            }

            // a client we never limited is already on every core. one we did
            // keeps its mask until it's explicitly handed every core back.
            int had = seen ? before.Cores : 0;
            if (cores == had) return;

            try
            {
                // lowest N logical cpus. on a hybrid chip that's hyperthread
                // pairs not real cores, and past 64 cpus processor groups get
                // ignored. one mask can't express either, so, whatever.
                int n = cores > 0 ? cores : totalCores;
                long mask = n >= 64 ? -1L : (1L << n) - 1L;
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

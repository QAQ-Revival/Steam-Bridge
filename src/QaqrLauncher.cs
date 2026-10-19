using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace EndfieldSteamBridge
{
    internal static class LaunchMethod
    {
        public const string Official = "official", Qaqr = "qaqr";
        public static string Normalize(string value)
        {
            if (String.IsNullOrEmpty(value) || value == Official) return Official;
            if (value == Qaqr) return Qaqr;
            throw new InvalidDataException("不支持的启动方式：" + value);
        }
        public static string Name(string value) { return Normalize(value) == Qaqr ? "QAQR（Mod 管理器）" : "官方启动器"; }
    }

    internal static class QaqrLauncher
    {
        public const string FileName = "QAQ-Revival.exe";
        public static bool Valid(string path)
        {
            try
            {
                if (String.IsNullOrEmpty(path) || !Path.GetFileName(path).Equals(FileName, StringComparison.OrdinalIgnoreCase) || !Detection.Is64BitExecutable(path)) return false;
                string app = Path.Combine(Path.GetDirectoryName(path), "resources", "app");
                var package = ReadObject(Path.Combine(app, "package.json"));
                return KeyValues.String(package, "name") == "qaq-revival" && KeyValues.String(package, "main") == "./out/main/index.js" && File.Exists(Path.Combine(app, "out", "main", "index.js"));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        public static string Find()
        {
            var candidates = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "QAQ-Revival", FileName) };
            foreach (var drive in DriveInfo.GetDrives())
                if (drive.DriveType == DriveType.Fixed && drive.IsReady) candidates.Add(Path.Combine(drive.RootDirectory.FullName, "QAQ-Revival", FileName));
            foreach (string candidate in candidates) if (Valid(candidate)) return candidate;
            return null;
        }
        public static string UserData(string executable, string saved = null)
        { return ResolveUserData(executable, saved, Environment.GetEnvironmentVariable("QAQM_USER_DATA")); }
        internal static string ResolveUserData(string executable, string saved, string environment)
        {
            string path = !String.IsNullOrEmpty(environment) ? environment : !String.IsNullOrEmpty(saved) ? saved : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QAQ-Revival");
            if (!Path.IsPathRooted(path)) path = Path.Combine(String.IsNullOrEmpty(executable) ? Environment.CurrentDirectory : Path.GetDirectoryName(Path.GetFullPath(executable)), path);
            return Path.GetFullPath(path);
        }
        public static string FindGame(GameProfile profile, string executable, string userData)
        {
            foreach (string name in new[] { "config.json", "config.json.bak" })
            {
                var config = ReadObject(Path.Combine(userData, name));
                if (config == null) continue;
                string path = null; object games;
                if (config.TryGetValue("games", out games) && games is IEnumerable)
                    foreach (object item in (IEnumerable)games)
                    {
                        var game = item as Dictionary<string, object>;
                        if (KeyValues.String(game, "id") == profile.Id) { path = KeyValues.String(game, "gamePath"); break; }
                    }
                if (String.IsNullOrWhiteSpace(path) && profile == GameProfile.Endfield) path = KeyValues.String(config, "gamePath");
                if (String.IsNullOrWhiteSpace(path)) return null;
                return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Path.GetDirectoryName(executable), path));
            }
            return null;
        }
        internal static Dictionary<string, object> ReadObject(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) return null;
                return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        public static ProcessStartInfo StartInfo(string executable, string userData, bool steamManaged = false)
        {
            var info = new ProcessStartInfo(executable, "") { WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false };
            info.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE");
            info.EnvironmentVariables["QAQM_USER_DATA"] = userData;
            // Only a fresh manager launched directly by Steam may inherit its app identity.
            if (!steamManaged)
                foreach (string key in new[] { "SteamAppId", "SteamGameId", "SteamOverlayGameId" }) info.EnvironmentVariables.Remove(key);
            return info;
        }
    }

    // Electron hands off to a randomly named main process. Its own runtime marker
    // and file hash identify that process without changing any QAQR settings/files.
    internal sealed class QaqrProcesses
    {
        private readonly string executable;
        private readonly Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public QaqrProcesses(string path) { executable = Path.GetFullPath(path); }
        internal bool IsManager(string path, string commandLine)
        {
            if (String.IsNullOrEmpty(path) || !Detection.Same(Path.GetDirectoryName(path), Path.GetDirectoryName(executable))) return false;
            if (commandLine != null && Regex.IsMatch(commandLine, @"(?:^|\s)--type(?:=|\s)")) return false;
            if (Detection.Same(path, executable)) return true;
            string name = Path.GetFileName(path);
            if (!Regex.IsMatch(name, @"^[a-z][a-f0-9]{8,24}\.exe$", RegexOptions.IgnoreCase)) return false;
            try
            {
                string markerPath = Path.Combine(Path.GetDirectoryName(path), "." + name + ".runtime.json");
                if (!File.Exists(markerPath) || new FileInfo(markerPath).Length > 8192 || !OrdinaryFile(path) || !OrdinaryFile(markerPath)) return false;
                var marker = QaqrLauncher.ReadObject(markerPath);
                string version = KeyValues.String(marker, "version"), digest = KeyValues.String(marker, "sha256");
                if ((version != "1" && version != "2") || KeyValues.String(marker, "source") != QaqrLauncher.FileName || KeyValues.String(marker, "executable") != name || !HashText(digest)) return false;
                if (version == "2" && !HashText(KeyValues.String(marker, "sourceSha256"))) return false;
                var file = new FileInfo(path);
                string fingerprint = file.Length + ":" + file.LastWriteTimeUtc.Ticks + ":" + digest, cached;
                if (hashes.TryGetValue(path, out cached) && cached == fingerprint) return true;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sha = SHA256.Create())
                    if (!BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").Equals(digest, StringComparison.OrdinalIgnoreCase)) return false;
                hashes[path] = fingerprint; return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        private static bool HashText(string text) { return text != null && Regex.IsMatch(text, "^[a-fA-F0-9]{64}$"); }
        private static bool OrdinaryFile(string path) { return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        public bool Running()
        {
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='QAQ-Revival.exe' OR Name LIKE 'r%.exe'"))
            using (var rows = searcher.Get())
                foreach (ManagementObject row in rows) using (row)
                {
                    string path = Convert.ToString(row["ExecutablePath"]);
                    if (String.IsNullOrEmpty(path)) path = BridgeRuntime.ImagePath(Convert.ToInt32(row["ProcessId"]));
                    if (IsManager(path, row["CommandLine"] as string)) return true;
                }
            return false;
        }
    }

    internal sealed class QaqrSession
    {
        private DateTime lastManager, lastGame;
        private readonly bool steamManaged;
        public bool GameObserved { get; private set; }
        public QaqrSession(DateTime now, bool steamManaged = false) { lastManager = now; this.steamManaged = steamManaged; }
        // Steam owns the managed process tree; independent sessions only track the game.
        public bool Finished(DateTime now, bool managerRunning, bool gameRunning)
        {
            if (managerRunning) lastManager = now;
            if (gameRunning) { GameObserved = true; lastGame = now; return false; }
            if (GameObserved) return now - lastGame >= TimeSpan.FromSeconds(2) && (!steamManaged || now - lastManager >= TimeSpan.FromSeconds(2));
            return now - lastManager >= TimeSpan.FromSeconds(30);
        }
    }
}

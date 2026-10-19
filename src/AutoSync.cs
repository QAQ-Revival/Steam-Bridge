using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace EndfieldSteamBridge
{
    internal static class AutoSync
    {
        internal sealed class Result
        {
            public bool success = true;
            public bool changed;
            public bool deferred;
            public string message;
            public string[] warnings = new string[0];
        }
        // Only compare Steam's local metadata; never download game data or edit the real client.
        internal static bool NeedsSync(Installation state, Metadata metadata, Dictionary<string, object> manifest)
        {
            if (state.Phase != "enabled" || state.Executable != "steam-bridge.exe") return false;
            if (!String.Equals(state.SteamExecutable, metadata.Executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Steam 默认启动路径发生变化，请手动撤回并重新设置桥接。");
            if (manifest == null) return true;
            if (!String.Equals(KeyValues.String(manifest, "installdir"), Path.GetFileName(state.BridgeDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Steam 安装目录已改变，自动同步已停止，请手动检查。");
            if (KeyValues.String(manifest, "buildid") != metadata.Build) return true;
            var depots = KeyValues.Child(manifest, "InstalledDepots");
            return metadata.Depots.Any(depot => KeyValues.String(KeyValues.Child(depots, depot.Key), "manifest") != depot.Value);
        }
        internal static bool HasPendingTransfer(Dictionary<string, object> manifest)
        {
            ulong planned, completed;
            foreach (var pair in new[] { new[] { "BytesToDownload", "BytesDownloaded" }, new[] { "BytesToStage", "BytesStaged" } })
                if (UInt64.TryParse(KeyValues.String(manifest, pair[0]), out planned) && planned > 0 &&
                    (!UInt64.TryParse(KeyValues.String(manifest, pair[1]), out completed) || completed < planned)) return true;
            return false;
        }
        private static string BusyReason(string steam, HashSet<uint> managedIds, uint ownAppId)
        {
            // A stale Running flag delays the repair, rather than interrupting another game.
            using (var apps = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps"))
                if (apps != null) foreach (string id in apps.GetSubKeyNames()) using (var app = apps.OpenSubKey(id))
                    if (id != ownAppId.ToString() && Convert.ToInt32(app.GetValue("Running", 0)) != 0) return "Steam 游戏仍在运行，自动同步已延后。";
            foreach (var process in Process.GetProcessesByName("steam-bridge")) using (process)
                if (process.Id != Process.GetCurrentProcess().Id) return "桥接会话仍在运行，自动同步已延后。";
            foreach (string library in Detection.Libraries(steam))
            {
                string apps = Path.Combine(library, "steamapps");
                foreach (string name in new[] { "downloading", "staging" })
                {
                    string directory = Path.Combine(apps, name);
                    if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any())
                        return "Steam 存在下载或暂存文件，自动同步已延后；请先完成或暂停处理下载。";
                }
                if (!Directory.Exists(apps)) continue;
                foreach (string file in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    var manifest = KeyValues.Child(KeyValues.Parse(File.ReadAllText(file)), "AppState");
                    uint id; UInt32.TryParse(KeyValues.String(manifest, "appid"), out id);
                    if (!managedIds.Contains(id) && HasPendingTransfer(manifest)) return "Steam 其他游戏有待处理下载，自动同步已延后。";
                }
            }
            return null;
        }
        internal static void AssertIdle(string steam, GameProfile[] profiles, uint ownAppId = 0)
        {
            string reason = BusyReason(steam, new HashSet<uint>(profiles.Select(p => p.AppId)), ownAppId);
            if (reason != null) throw new InvalidOperationException(reason);
        }
        internal static bool SameProfile(RuntimeConfig config, string userData)
        {
            return String.IsNullOrEmpty(userData) || Detection.Same(QaqrLauncher.ResolveUserData(config.QaqrPath, config.QaqrUserData, null), userData);
        }
        public static Result Run(GameProfile[] profiles, Action<string> progress, uint ownAppId = 0, string userData = null)
        {
            var candidates = new List<DetectionResult>();
            var warnings = new List<string>();
            var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in profiles)
            {
                if (!File.Exists(profile.StatePath) || !File.Exists(profile.ConfigPath)) continue;
                try
                {
                    var config = App.Load<RuntimeConfig>(profile.ConfigPath);
                    if (config.AutoSync == false || !SameProfile(config, userData)) continue;
                    var state = App.Load<Installation>(profile.StatePath);
                    var metadata = KeyValues.ReadAppInfo(Path.Combine(state.SteamRoot, "appcache", "appinfo.vdf"), profile);
                    var manifest = File.Exists(state.ManifestPath) ? KeyValues.Child(KeyValues.Parse(File.ReadAllText(state.ManifestPath)), "AppState") : null;
                    if (!NeedsSync(state, metadata, manifest)) continue;
                    // Keep exact saved choices. Do not import unsaved QAQR UI drafts or switch accounts.
                    LaunchOptions.Validate(state);
                    var scan = Detection.Scan(config.Launcher, config.Game, config.ChannelId, profile.Id,
                        config.LaunchMethod, config.QaqrPath, config.QaqrUserData, config.QaqrSteamManaged, config.AutoSync);
                    if (scan.Error != null || !scan.ClientReady || scan.Account == null || !scan.Account.Known || !scan.Account.Owned || scan.Account.SteamId != state.SteamId)
                    { warnings.Add(profile.Name + "：" + (scan.Error ?? (scan.Account == null ? null : scan.Account.Error) ?? "请确认客户端路径和原 Steam 账号")); continue; }
                    if (!Detection.Same(scan.SteamRoot, state.SteamRoot)) { warnings.Add(profile.Name + "：Steam 安装位置已变化，请手动检查。"); continue; }
                    steamRoots.Add(scan.SteamRoot); candidates.Add(scan);
                }
                catch (Exception error) { warnings.Add(profile.Name + "：" + error.Message); }
            }
            if (candidates.Count == 0) return new Result { warnings = warnings.ToArray() };
            foreach (string steam in steamRoots)
            {
                string busy = BusyReason(steam, new HashSet<uint>(candidates.Select(c => GameProfile.Get(c.GameId).AppId)), ownAppId);
                if (busy != null) return new Result { deferred = true, message = busy, warnings = warnings.ToArray() };
            }
            InstallService.EnableMany(candidates.ToArray(), progress, () => { foreach (string steam in steamRoots) AssertIdle(steam, profiles, ownAppId); });
            return new Result { changed = true, message = "已按保存的配置自动同步" + String.Join("、", candidates.Select(c => GameProfile.Get(c.GameId).Name)) + "的 Steam 版本。", warnings = warnings.ToArray() };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;

namespace EndfieldSteamBridge
{
    // Filesystem transaction is separate from Steam/Task Scheduler for fixture tests.
    internal sealed class InstallFiles
    {
        private readonly string home;
        private readonly GameProfile profile;
        public string StatePath { get { return Path.Combine(home, "installation.json"); } }
        public InstallFiles(string dataDirectory, GameProfile gameProfile = null) { home = dataDirectory; profile = gameProfile ?? GameProfile.Endfield; }
        public Installation Prepare(string steam, string manifest, string exe, Metadata metadata, ulong owner, bool legacy, string channelId = null, string launchSource = null)
        {
            if (File.Exists(StatePath)) throw new InvalidOperationException("已有载入记录，请先点击“撤回载入国服”。");
            metadata.Executable = profile.ValidateExecutable(metadata.Executable);
            if (String.IsNullOrEmpty(launchSource) || !File.Exists(launchSource)) throw new IOException("未找到真实游戏的启动文件，已停止设置。");
            string library = Path.GetDirectoryName(manifest);
            string directory = Path.Combine(library, "common", profile.BridgeFolder);
            if (Directory.Exists(directory)) throw new IOException("桥接目录已存在，请先处理旧安装：" + directory);
            string backup = Path.Combine(home, "backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            var state = new Installation { GameId = profile.Id, ChannelId = GameChannel.Get(channelId).Id, Version = App.Version, SteamRoot = steam, ManifestPath = manifest,
                OriginalManifestBackup = Path.Combine(backup, "original.acf"), PreviousManifestBackup = Path.Combine(backup, "previous.acf"),
                ManagedManifestBackup = Path.Combine(backup, "managed.acf"), BridgeDirectory = directory,
                Token = Guid.NewGuid().ToString("N"), TaskName = profile.TaskName, WasInstalled = File.Exists(manifest) && !legacy, Phase = "prepared", Executable = "steam-bridge.exe",
                RuntimeExecutable = Path.Combine(home, "app", "steam-bridge.exe"), SteamExecutable = metadata.Executable };
            ValidateState(state);
            if (File.Exists(manifest)) File.Copy(manifest, state.PreviousManifestBackup);
            if (state.WasInstalled) File.Copy(manifest, state.OriginalManifestBackup);
            App.Save(StatePath, state);
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, ".endfield-steam-bridge"), state.Token);
                Directory.CreateDirectory(Path.GetDirectoryName(state.RuntimeExecutable));
                if (!Detection.Same(exe, state.RuntimeExecutable)) File.Copy(exe, state.RuntimeExecutable, true);
                // Steam checks its default target before expanding %command%. This is a byte-for-byte
                // copy of the REAL game's starter, never a bridge disguised with the game's name.
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory, state.SteamExecutable)));
                File.Copy(launchSource, Path.Combine(directory, state.SteamExecutable));
                WriteManifest(state, exe, metadata, owner);
                return state;
            }
            catch { Rollback(state); throw; }
        }
        private void WriteManifest(Installation state, string exe, Metadata metadata, ulong owner)
        {
                var depots = new Dictionary<string, object>();
                foreach (var depot in metadata.Depots) depots[depot.Key] = new Dictionary<string, object> { { "manifest", depot.Value }, { "size", "0" } };
                var appState = new Dictionary<string, object> {
                    { "appid", profile.AppId.ToString() }, { "Universe", "1" }, { "name", profile.SteamName }, { "StateFlags", "4" },
                    { "installdir", Path.GetFileName(state.BridgeDirectory) }, { "LastUpdated", ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString() },
                    { "SizeOnDisk", new FileInfo(Path.Combine(state.BridgeDirectory, state.SteamExecutable)).Length.ToString() }, { "buildid", metadata.Build }, { "LastOwner", owner.ToString() },
                    { "UpdateResult", "0" }, { "BytesToDownload", "0" }, { "BytesDownloaded", "0" }, { "BytesToStage", "0" }, { "BytesStaged", "0" },
                    { "AutoUpdateBehavior", "1" }, { "AllowOtherDownloadsWhileRunning", "0" }, { "ScheduledAutoUpdate", "0" },
                    { "InstalledDepots", depots }, { "UserConfig", new Dictionary<string, object> { { "language", "schinese" } } },
                    { "MountedConfig", new Dictionary<string, object> { { "language", "schinese" } } }
                };
                File.WriteAllText(state.ManagedManifestBackup, KeyValues.Render(new Dictionary<string, object> { { "AppState", appState } }), new UTF8Encoding(false));
        }
        public void Synchronize(Installation state, string exe, Metadata metadata, ulong owner, string launchSource)
        {
            ValidateState(state); ValidateManagedManifest(state); ValidateMarker(state);
            if (state.Phase != "enabled") throw new InvalidOperationException("上次操作尚未完成，请先撤回载入。");
            if (state.Executable != "steam-bridge.exe") throw new InvalidOperationException("旧版桥接需要先撤回，再使用新版本设置。");
            if (!String.Equals(profile.ValidateExecutable(metadata.Executable), state.SteamExecutable, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Steam 默认启动路径已变化，请先撤回再重新设置。");
            string target = Path.Combine(state.BridgeDirectory, state.SteamExecutable);
            // Never replace the first pre-bridge backup during a version refresh.
            var snapshots = new Dictionary<string, byte[]>();
            foreach (string file in new[] { state.ManifestPath, state.ManagedManifestBackup, StatePath, target })
                snapshots[file] = File.Exists(file) ? File.ReadAllBytes(file) : null;
            try
            {
                AtomicCopy(launchSource, target);
                WriteManifest(state, exe, metadata, owner);
                state.Version = App.Version; Commit(state);
            }
            catch
            {
                foreach (var snapshot in snapshots)
                    if (snapshot.Value == null) { if (File.Exists(snapshot.Key)) File.Delete(snapshot.Key); }
                    else File.WriteAllBytes(snapshot.Key, snapshot.Value);
                throw;
            }
        }
        public void Commit(Installation state)
        {
            AtomicCopy(state.ManagedManifestBackup, state.ManifestPath);
            state.Phase = "enabled"; App.Save(StatePath, state);
        }
        public void Rollback(Installation state)
        {
            if (File.Exists(state.PreviousManifestBackup)) AtomicCopy(state.PreviousManifestBackup, state.ManifestPath);
            else if (File.Exists(state.ManifestPath)) File.Delete(state.ManifestPath);
            RemoveBridge(state);
            if (File.Exists(StatePath)) File.Delete(StatePath);
        }
        public void Restore(Installation state)
        {
            ValidateState(state);
            ValidateManagedManifest(state);
            if (Directory.Exists(state.BridgeDirectory)) ValidateMarker(state);
            if (state.WasInstalled && !File.Exists(state.OriginalManifestBackup)) throw new IOException("找不到原始安装记录备份：" + state.OriginalManifestBackup);
            state.Phase = "restoring"; App.Save(StatePath, state);
            if (state.WasInstalled) AtomicCopy(state.OriginalManifestBackup, state.ManifestPath);
            else if (File.Exists(state.ManifestPath)) File.Delete(state.ManifestPath);
            RemoveBridge(state);
            // Keep the state until Task Scheduler cleanup succeeds, so retry is possible.
        }
        private static void ValidateManagedManifest(Installation state)
        {
            if (File.Exists(state.ManifestPath) && state.Phase != "restoring")
            {
                var acf = KeyValues.Child(KeyValues.Parse(File.ReadAllText(state.ManifestPath)), "AppState");
                string install = KeyValues.String(acf, "installdir");
                if (state.Phase == "enabled" && !String.Equals(install, Path.GetFileName(state.BridgeDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Steam 安装记录已被其他程序更改，请保留备份并查看使用说明后处理。");
            }
        }
        public void FinishRestore() { if (File.Exists(StatePath)) File.Delete(StatePath); }
        private static void AtomicCopy(string source, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string temp = target + ".esb-tmp"; File.Copy(source, temp, true);
            if (File.Exists(target)) App.Replace(temp, target); else File.Move(temp, target);
        }
        private void ValidateState(Installation state)
        {
            string expected = Path.Combine(Path.GetDirectoryName(state.ManifestPath), "common", profile.BridgeFolder);
            if (GameProfile.Get(state.GameId) != profile || !Detection.Same(state.BridgeDirectory, expected) || Path.GetFileName(state.ManifestPath) != profile.ManifestName)
                throw new InvalidDataException("本地载入记录路径不正确，已停止修改。");
            if (state.Executable != "steam-bridge.exe") profile.ValidateExecutable(state.Executable);
            if (state.SteamExecutable != null) profile.ValidateExecutable(state.SteamExecutable);
            if (state.RuntimeExecutable != null && !Detection.Same(state.RuntimeExecutable, Path.Combine(home, "app", "steam-bridge.exe"))) throw new InvalidDataException("工具程序路径无效。");
            string directory = Path.GetDirectoryName(Path.Combine(state.BridgeDirectory, state.SteamExecutable ?? state.Executable));
            // Refuse junctions/symlinks that could redirect a bridge write into a real game.
            while (directory != null)
            {
                if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("桥接路径包含目录链接，已停止修改：" + directory);
                directory = Path.GetDirectoryName(directory);
            }
        }
        private static void ValidateMarker(Installation state)
        {
            string marker = Path.Combine(state.BridgeDirectory, ".endfield-steam-bridge");
            if (!File.Exists(marker) || File.ReadAllText(marker) != state.Token) throw new IOException("桥接目录标记不匹配，已保留目录：" + state.BridgeDirectory);
        }
        private void RemoveBridge(Installation state)
        {
            ValidateState(state);
            if (!Directory.Exists(state.BridgeDirectory)) return;
            string marker = Path.Combine(state.BridgeDirectory, ".endfield-steam-bridge");
            ValidateMarker(state);
            string exe = Path.Combine(state.BridgeDirectory, state.SteamExecutable ?? state.Executable);
            if (File.Exists(exe)) File.Delete(exe);
            string parent = Path.GetDirectoryName(exe);
            while (!Detection.Same(parent, state.BridgeDirectory) && Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0)
            { Directory.Delete(parent); parent = Path.GetDirectoryName(parent); }
            File.Delete(marker);
            if (Directory.GetFileSystemEntries(state.BridgeDirectory).Length == 0) Directory.Delete(state.BridgeDirectory);
        }
    }
    internal static class InstallService
    {
        public static void Enable(DetectionResult scan, Action<string> progress)
        {
            EnableMany(new[] { scan }, progress);
        }
        public static void EnableMany(DetectionResult[] scans, Action<string> progress, Action assertIdle = null)
        {
            if (scans.Length == 0) return;
            var metadata = new Dictionary<string, Metadata>();
            var owners = new Dictionary<string, ulong>();
            string steam = scans[0].SteamRoot;
            // Validate BOTH games before stopping Steam or changing either installation.
            foreach (var scan in scans)
            {
                var profile = GameProfile.Get(scan.GameId);
                if (scan.Error != null) throw new InvalidOperationException(profile.Name + "：" + scan.Error);
                if (!Detection.Same(steam, scan.SteamRoot) || metadata.ContainsKey(profile.Id)) throw new InvalidOperationException("游戏或 Steam 安装位置重复/不一致。");
                progress("正在检查" + profile.Name + "的账号、客户端和 Steam 版本…");
                var account = SteamAccount.ProbeIsolated(steam, profile);
                if (!account.Known || !account.Owned) throw new InvalidOperationException(account.Error ?? "请先将《" + profile.SteamName + "》免费添加到当前 Steam 账号。");
                bool ready;
                string status = Detection.ClientStatus(profile, scan.Launcher, scan.Game, scan.ChannelId, scan.LaunchMethod, scan.QaqrPath, out ready);
                if (!ready) throw new InvalidOperationException(profile.Name + "：" + status);
                if (File.Exists(profile.StatePath))
                {
                    var state = App.Load<Installation>(profile.StatePath);
                    if (state.TaskName != profile.TaskName || GameProfile.Get(state.GameId) != profile) throw new InvalidOperationException("载入记录与当前用户或游戏不匹配。");
                    if (state.Phase != "enabled" || state.Executable != "steam-bridge.exe" || state.SteamId != account.SteamId || GameChannel.Get(state.ChannelId).Id != scan.ChannelId)
                        throw new InvalidOperationException(profile.Name + "：请先撤回未完成的载入，或撤回后再切换服务器。");
                }
                metadata.Add(profile.Id, KeyValues.ReadAppInfo(Path.Combine(steam, "appcache", "appinfo.vdf"), profile));
                owners.Add(profile.Id, account.SteamId);
                if (owners.Values.Distinct().Count() != 1) throw new InvalidOperationException("Steam 账号在检测期间发生了切换，请重新检测。");
                if (!File.Exists(profile.SteamLaunchSource(metadata[profile.Id].Executable, scan.Launcher, scan.Game)))
                    throw new IOException("Steam 默认入口需要真实启动文件，请先确认官方启动器/客户端路径。");
                FindManifest(steam, profile); EnsureGameClosed(profile, scan.LaunchMethod);
            }
            if (assertIdle != null) assertIdle();
            progress("正在正常退出 Steam…"); StopSteam(steam);
            var completed = new List<string>();
            try
            {
                foreach (var scan in scans)
                {
                    var profile = GameProfile.Get(scan.GameId);
                    progress("正在设置 / 同步" + profile.Name + "…");
                    EnableStopped(scan, metadata[profile.Id], owners[profile.Id]);
                    completed.Add(profile.Name);
                }
            }
            catch (Exception error)
            {
                throw new InvalidOperationException((completed.Count == 0 ? "" : "已完成：" + String.Join("、", completed) + "。其余游戏未完成：") + error.Message, error);
            }
            finally { StartSteam(steam); }
            progress("已设置 / 同步" + String.Join("、", completed) + "。Steam 启动完成后即可从游戏库开始游戏。");
        }
        private static void EnableStopped(DetectionResult scan, Metadata metadata, ulong owner)
        {
            var profile = GameProfile.Get(scan.GameId);
            var files = new InstallFiles(profile.Home, profile);
            Installation state = null; bool newInstall = !File.Exists(profile.StatePath), taskCreated = false, launchPrepared = false;
            byte[] previousConfig = File.Exists(profile.ConfigPath) ? File.ReadAllBytes(profile.ConfigPath) : null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(profile.RuntimeExe));
                string current = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!Detection.Same(current, profile.RuntimeExe)) File.Copy(current, profile.RuntimeExe, true);
                string manifest = FindManifest(scan.SteamRoot, profile);
                string launchSource = profile.SteamLaunchSource(metadata.Executable, scan.Launcher, scan.Game);
                state = newInstall ? files.Prepare(scan.SteamRoot, manifest, profile.RuntimeExe, metadata, owner, IsLegacy(manifest, profile), scan.ChannelId, launchSource) : App.Load<Installation>(profile.StatePath);
                if (newInstall) { LaunchOptions.Prepare(state, owner); launchPrepared = true; App.Save(files.StatePath, state); }
                LaunchOptions.Apply(state);
                state.LaunchMethod = LaunchMethod.Normalize(scan.LaunchMethod);
                App.Save(profile.ConfigPath, RuntimeSettings(scan, state.TaskName));
                RegisterTask(profile); taskCreated = true;
                if (newInstall) files.Commit(state); else files.Synchronize(state, profile.RuntimeExe, metadata, owner, launchSource);
            }
            catch
            {
                if (newInstall && state != null) { if (launchPrepared) LaunchOptions.Restore(state); files.Rollback(state); }
                if (newInstall && taskCreated) DeleteTask(profile.TaskName);
                if (previousConfig != null) File.WriteAllBytes(profile.ConfigPath, previousConfig);
                else if (File.Exists(profile.ConfigPath)) File.Delete(profile.ConfigPath);
                throw;
            }
        }
        internal static RuntimeConfig RuntimeSettings(DetectionResult scan, string taskName)
        {
            return new RuntimeConfig {
                GameId = GameProfile.Get(scan.GameId).Id, ChannelId = scan.ChannelId,
                LaunchMethod = LaunchMethod.Normalize(scan.LaunchMethod),
                Launcher = String.IsNullOrEmpty(scan.Launcher) ? null : Path.GetFullPath(scan.Launcher),
                QaqrPath = scan.QaqrPath, QaqrUserData = scan.QaqrUserData, QaqrSteamManaged = scan.QaqrSteamManaged,
                AutoSync = scan.AutoSync,
                Game = Path.GetFullPath(scan.Game), SteamRoot = scan.SteamRoot, TaskName = taskName
            };
        }
        public static void Restore(Action<string> progress, GameProfile profile = null)
        {
            profile = profile ?? GameProfile.Endfield;
            var state = App.Load<Installation>(profile.StatePath);
            if (state.TaskName != profile.TaskName) throw new InvalidOperationException("该载入记录属于其他 Windows 用户，请用原用户卸载。");
            EnsureGameClosed(profile, state.LaunchMethod); progress("正在正常退出 Steam…"); StopSteam(state.SteamRoot);
            try
            {
                var files = new InstallFiles(profile.Home, profile);
                LaunchOptions.Validate(state);
                progress("正在还原 Steam 安装状态…"); files.Restore(state);
                LaunchOptions.Restore(state);
                DeleteTask(state.TaskName); files.FinishRestore();
                if (File.Exists(profile.ConfigPath)) File.Delete(profile.ConfigPath);
            }
            finally { StartSteam(state.SteamRoot); }
            progress(state.WasInstalled ? "已撤回国服载入，恢复原来的 Steam 客户端。" : "已撤回国服载入，Steam 将恢复为“安装”。");
        }
        public static string FindManifest(string steam, GameProfile profile)
        {
            var matches = Detection.Libraries(steam).Select(p => Path.Combine(p, "steamapps", profile.ManifestName)).Where(File.Exists).ToArray();
            if (matches.Length > 1) throw new IOException("多个 Steam 库中都有" + profile.Name + "安装记录，请先在 Steam 中处理重复安装。");
            return matches.Length == 1 ? matches[0] : Path.Combine(steam, "steamapps", profile.ManifestName);
        }
        public static bool IsLegacy(string manifest, GameProfile profile)
        {
            if (profile != GameProfile.Endfield || !File.Exists(manifest)) return false;
            var state = KeyValues.Child(KeyValues.Parse(File.ReadAllText(manifest)), "AppState");
            string directory = KeyValues.String(state, "installdir");
            if (directory != "Endfield CN Bridge") return false;
            string exe = Path.Combine(Path.GetDirectoryName(manifest), "common", directory, "Endfield.exe");
            if (!File.Exists(exe)) return false;
            try { string name = System.Reflection.AssemblyName.GetAssemblyName(exe).Name; return name == "EndfieldCNV3" || name == "EndfieldCNV2" || name == "EndfieldCN"; }
            catch (BadImageFormatException) { return false; }
        }
        internal static void EnsureGameClosed(GameProfile profile, string method)
        {
            foreach (var p in Process.GetProcessesByName("steam-bridge")) using (p)
                if (p.Id != Process.GetCurrentProcess().Id && Detection.Same(BridgeRuntime.ImagePath(p.Id), profile.RuntimeExe))
                    throw new InvalidOperationException("请先结束" + profile.Name + "的 Steam 桥接会话，再操作。");
            // Steam's registry Running flags can remain set after a game exits.
            // Check actual Endfield/launcher processes here; StopSteam must still
            // complete normally before any Steam installation files are changed.
            var names = LaunchMethod.Normalize(method) == LaunchMethod.Qaqr ? profile.GameProcesses : profile.GameProcesses.Concat(profile.LauncherProcesses.Where(n => n != "msedgewebview2"));
            foreach (string name in names) foreach (var p in Process.GetProcessesByName(name)) using (p)
                throw new InvalidOperationException("请先完全退出" + profile.Name + "与" + profile.LauncherName + "（包括托盘），再操作。");
        }
        private static bool SteamRunning()
        {
            bool found = false; foreach (var p in Process.GetProcessesByName("steam")) using (p) found = true; return found;
        }
        private static void StopSteam(string steam)
        {
            if (!SteamRunning()) return;
            using (var p = Process.Start(new ProcessStartInfo(Path.Combine(steam, "steam.exe"), "-shutdown") { UseShellExecute = false })) { }
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (SteamRunning() && DateTime.UtcNow < deadline) Thread.Sleep(400);
            if (SteamRunning()) throw new InvalidOperationException("Steam 尚未正常退出。请处理 Steam 的提示，完全退出后重试。");
            Thread.Sleep(700);
        }
        private static void StartSteam(string steam)
        { Process.Start(new ProcessStartInfo(Path.Combine(steam, "steam.exe"), "") { UseShellExecute = true }); }
        private static void RegisterTask(GameProfile profile)
        {
            // Queue the normal session assistant if Steam relaunches before a repair assistant exits.
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><RegistrationInfo><Description>Steam Bridge: " + profile.SteamName + " session cleanup.</Description></RegistrationInfo><Triggers/><Principals><Principal id=\"Author\"><UserId>" + sid + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><MultipleInstancesPolicy>Queue</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings><Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(profile.RuntimeExe) + "</Command><Arguments>--assist " + profile.Id + "</Arguments><WorkingDirectory>" + SecurityElement.Escape(profile.Home) + "</WorkingDirectory></Exec></Actions></Task>";
            string file = Path.Combine(profile.Home, "task.xml"); File.WriteAllText(file, xml, Encoding.Unicode);
            try { RunTaskCommand("/create /tn " + Arguments.Quote(profile.TaskName) + " /xml " + Arguments.Quote(file) + " /f", false); }
            finally { if (File.Exists(file)) File.Delete(file); }
        }
        private static void DeleteTask(string name)
        {
            // A missing task is already uninstalled; query before deleting.
            if (RunTaskCommand("/query /tn " + Arguments.Quote(name), true) == 0)
                RunTaskCommand("/delete /tn " + Arguments.Quote(name) + " /f", false);
        }
        private static int RunTaskCommand(string arguments, bool allowFailure)
        {
            using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                string output = p.StandardOutput.ReadToEnd(); string error = p.StandardError.ReadToEnd(); p.WaitForExit();
                if (p.ExitCode != 0 && !allowFailure) throw new InvalidOperationException("计划任务配置失败：" + output + error);
                return p.ExitCode;
            }
        }
    }
}

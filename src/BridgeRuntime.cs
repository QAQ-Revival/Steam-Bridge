using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Steam launches official launchers and opted-in QAQR sessions in its own process
// tree. Task Scheduler handles official cleanup and independent QAQR sessions.
namespace EndfieldSteamBridge
{
internal static class BridgeRuntime
{
    private static GameProfile Profile;
    private static string Root { get { return Profile.Home; } }
    private static RuntimeConfig Config;
    private static string Launcher { get { return Config.Launcher; } }
    private static string Game { get { return Config.Game; } }
    private static string GameImage { get { return Profile == GameProfile.Endfield ? Game : WutheringLauncher.GameProcess(Game); } }
    private static GameChannel Channel { get { return GameChannel.Get(Config.ChannelId); } }
    private static string TaskName { get { return Config.TaskName; } }
    private static string RequestPath { get { return Path.Combine(Root, "request.json"); } }
    private static string QaqrResultPath { get { return Path.Combine(Root, "qaqr-launch.json"); } }
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    internal sealed class Request
    {
        public string LaunchMethod { get; set; }
        public string QaqrUserData { get; set; }
        public int Pid { get; set; }
        public long StartTicks { get; set; }
        public string Id { get; set; }
        public int LauncherPid { get; set; }
        public long LauncherStartTicks { get; set; }
        public bool Repair { get; set; }
        public long CreatedUtcTicks { get; set; }
    }

    public static int Run(string gameId)
    {
        LoadConfig(gameId);
        bool created;
        using (var mutex = new Mutex(true, @"Local\SteamBridge-" + Profile.Id, out created))
        {
            if (!created) return 0;
            // A separate scheduled worker survives Steam shutdown during version repair.
            if (AutoRepair.TryStart(Profile, Config)) return 0;
            if (Environment.GetEnvironmentVariable("SteamAppId") != Profile.AppId.ToString())
                throw new InvalidOperationException("未收到对应的 SteamAppId。请从 Steam 库中的《" + Profile.SteamName + "》官方条目开始游戏。若 Steam 刚更新，请在 QAQR 中检测 / 同步桥接。");
            return LaunchMethod.Normalize(Config.LaunchMethod) == LaunchMethod.Qaqr ? RunQaqr() : Bridge();
        }
    }
    public static int RunAssist(string gameId)
    {
        LoadConfig(gameId);
        return Assist();
    }
    private static void LoadConfig(string gameId)
    {
        Profile = GameProfile.Get(gameId); Config = App.Load<RuntimeConfig>(Profile.ConfigPath);
        LaunchMethod.Normalize(Config.LaunchMethod);
        if (GameProfile.Get(Config.GameId) != Profile || Config.TaskName != Profile.TaskName) throw new InvalidDataException("桥接配置与游戏或当前用户不匹配，请重新设置。");
    }
    private static bool SessionPath(string path)
    {
        return path != null && (path.StartsWith(Path.GetDirectoryName(Launcher) + "\\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith((Profile == GameProfile.Endfield ? Path.GetDirectoryName(Game) : WutheringLauncher.GameRoot(Game)) + "\\", StringComparison.OrdinalIgnoreCase) ||
            (Profile == GameProfile.Wuthering && Path.GetFileName(path).Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)));
    }
    private static int Bridge()
    {
        Request request = null;
        var ownedLaunchers = new Dictionary<int, long>();
        var originalLaunchers = LauncherProcesses();
        bool launchedHere = false;
        bool gameObserved = false;
        try
        {
            bool ready;
            string clientStatus = Detection.ClientStatus(Profile, Launcher, Game, Channel.Id, out ready);
            if (!ready) throw new InvalidOperationException(clientStatus);
            Log("bridge", "启动 " + Channel.Name + "；SteamAppId=" + Environment.GetEnvironmentVariable("SteamAppId"));
            if (GameRunning()) throw new InvalidOperationException("国服已在运行。请正常退出后再从 Steam 启动。");
            if (!GameRunning())
            {
                if (LauncherRunning()) throw new InvalidOperationException("请先完全退出" + Profile.LauncherName + "（包括托盘），再从 Steam 启动。本工具需要由 Steam 重新打开启动器，才能把叠加层传递给国服。");
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new InvalidOperationException("国服启动器需要管理员权限。请先正常退出 Steam，再以管理员身份打开 Steam 后重试。原生叠加层需要 Steam 与国服保持相同权限。");
                using (var self = Process.GetCurrentProcess())
                    request = new Request { LaunchMethod = LaunchMethod.Official, Pid = self.Id, StartTicks = self.StartTime.ToUniversalTime().Ticks, Id = Guid.NewGuid().ToString("N") };
                WriteJson(RequestPath, request);
                var info = new ProcessStartInfo
                {
                    FileName = Launcher, Arguments = Profile == GameProfile.Endfield ? "--game=endfield" : "",
                    WorkingDirectory = Path.GetDirectoryName(Launcher), UseShellExecute = false
                };
                // Keep the environment Steam assigned to this official app entry.
                using (var launcher = Process.Start(info))
                {
                    launchedHere = true;
                    ownedLaunchers[launcher.Id] = launcher.StartTime.ToUniversalTime().Ticks;
                    request.LauncherPid = launcher.Id;
                    request.LauncherStartTicks = ownedLaunchers[launcher.Id];
                    WriteJson(RequestPath, request);
                    Log("bridge", "由 Steam 进程链打开" + Channel.Name + "启动器 PID=" + launcher.Id);
                }
                using (var task = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                    Arguments = "/run /tn \"" + TaskName + "\"",
                    UseShellExecute = false, CreateNoWindow = true
                }))
                {
                    task.WaitForExit();
                    if (task.ExitCode != 0) throw new InvalidOperationException("无法运行结束清理任务。请重新设置当前游戏。");
                }
                Log("bridge", "请在" + Profile.LauncherName + "中手动点击开始游戏。");
                var startupGrace = DateTime.UtcNow.AddSeconds(15);
                while (!GameRunning())
                {
                    TrackLaunchers(originalLaunchers, ownedLaunchers);
                    if (DateTime.UtcNow >= startupGrace && !LauncherRunning()) { Log("bridge", "启动器已关闭，结束本次运行。"); return 0; }
                    Thread.Sleep(500);
                }
            }
            Log("bridge", "检测到国服游戏，跟踪游玩时长。");
            gameObserved = true;
            var nextOverlayCheck = DateTime.UtcNow;
            while (GameRunning())
            {
                if (launchedHere)
                {
                    TrackLaunchers(originalLaunchers, ownedLaunchers);
                    if (DateTime.UtcNow >= nextOverlayCheck)
                    {
                        // Bilibili's platform/crash reporter can create another UI
                        // after the game opens. Include this session's descendants.
                        var overlayTargets = new Dictionary<int, long>(ownedLaunchers);
                        TrackSession(overlayTargets);
                        RemoveLauncherOverlay(overlayTargets);
                        nextOverlayCheck = DateTime.UtcNow.AddSeconds(5);
                    }
                }
                Thread.Sleep(1000);
            }
            Log("bridge", "国服已退出，结束 Steam 本次运行。");
            return 0;
        }
        catch (Exception error)
        {
            Log("bridge", "错误：" + error.Message);
            MessageBox.Show(error.Message, "steam-bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (launchedHere && (!gameObserved || !GameRunning()))
            {
                TrackLaunchers(originalLaunchers, ownedLaunchers);
                CloseOwnedLaunchers(ownedLaunchers);
            }

        }
    }

    internal sealed class QaqrLaunchResult
    {
        public string Id { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }
    private static int RunQaqr()
    {
        try
        {
            bool created;
            using (var shared = new Mutex(true, @"Local\SteamBridge-QAQR-" + WindowsIdentity.GetCurrent().User.Value, out created))
            {
                if (!created) throw new InvalidOperationException("另一个游戏的 QAQR 桥接会话仍在运行，请先在 Steam 中停止该会话。");
                bool ready;
                string status = Detection.ClientStatus(Profile, Launcher, Game, Channel.Id, Config.LaunchMethod, Config.QaqrPath, out ready);
                if (!ready) throw new InvalidOperationException(status);
                bool steamManaged = Config.QaqrSteamManaged;
                if (GameRunning()) throw new InvalidOperationException("所选游戏已经运行。请先手动退出游戏，再从 Steam 启动。" + (steamManaged ? "请同时退出已有 QAQR。" : "QAQR 可以保持打开。"));
                var manager = new QaqrProcesses(Config.QaqrPath);
                bool managerRunning = manager.Running();
                string userData = QaqrLauncher.UserData(Config.QaqrPath, Config.QaqrUserData);
                if (steamManaged)
                {
                    if (managerRunning) throw new InvalidOperationException("已开启“由 Steam 管理 QAQR”。请先完全退出已有 QAQR（包括托盘），再从 Steam 启动，才能传递叠加层。");
                    if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                        throw new InvalidOperationException("Steam 管理 QAQR 需要相同的管理员权限。请先正常退出 Steam，再以管理员身份打开 Steam 后重试。");
                    // Do not use the scheduled helper: Steam must own the fresh manager
                    // and its random-name relaunch, along with the games it starts.
                    using (var launched = Process.Start(QaqrLauncher.StartInfo(Config.QaqrPath, userData, true)))
                        Log("bridge", "由 Steam 进程链打开 QAQR PID=" + launched.Id + "；保留叠加层环境，Steam 管理本次 QAQR 与其游戏。");
                }
                else
                {
                    Log("bridge", (managerRunning ? "唤醒已有" : "独立打开") + " QAQR；不传递 Steam 叠加层，不接管共享管理器生命周期。");
                    StartIndependentQaqr(userData);
                }
                Log("bridge", "请在 QAQR 中选择" + Profile.Name + "并手动开始。" + (steamManaged ? "Steam 停止会结束本次 QAQR 与其游戏；QAQR 未退出时会继续计时。" : "Steam 停止仅结束桥接计时。"));
                var session = new QaqrSession(DateTime.UtcNow, steamManaged);
                while (true)
                {
                    bool playing = GameRunning(), observed = session.GameObserved;
                    bool finished = session.Finished(DateTime.UtcNow, (steamManaged || !session.GameObserved) && manager.Running(), playing);
                    if (!observed && session.GameObserved) Log("bridge", "已检测到所选游戏：" + GameImage);
                    if (finished) break;
                    Thread.Sleep(500);
                }
                Log("bridge", steamManaged ? "QAQR 与所选游戏已退出，结束 Steam 管理会话。" : session.GameObserved ? "所选游戏已退出，结束计时；保留 QAQR 和其他游戏。" : "QAQR 已退出或启动未完成，结束本次等待。");
                return 0;
            }
        }
        catch (Exception error)
        {
            Log("bridge", "QAQR：" + error.Message);
            MessageBox.Show(error.Message, "steam-bridge · QAQR", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
    }
    private static void StartIndependentQaqr(string userData)
    {
        Request request;
        using (var self = Process.GetCurrentProcess())
            request = new Request { LaunchMethod = LaunchMethod.Qaqr, QaqrUserData = userData, Pid = self.Id, StartTicks = self.StartTime.ToUniversalTime().Ticks, Id = Guid.NewGuid().ToString("N") };
        WriteJson(RequestPath, request);
        // Independent QAQR stays outside Steam's process tree, including on Steam Stop.
        using (var task = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/run /tn " + Arguments.Quote(TaskName)) { UseShellExecute = false, CreateNoWindow = true }))
        {
            task.WaitForExit();
            if (task.ExitCode != 0) throw new InvalidOperationException("无法打开 QAQR，请重新应用当前游戏设置。");
        }
        QaqrLaunchResult result = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(QaqrResultPath))
            {
                var candidate = App.Load<QaqrLaunchResult>(QaqrResultPath);
                if (candidate.Id == request.Id) { result = candidate; break; }
            }
            Thread.Sleep(200);
        }
        if (result == null) throw new TimeoutException("QAQR 启动任务未响应，请重新应用当前游戏设置。不会关闭已有 QAQR。");
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }
    private static int OpenQaqr(Request request)
    {
        var result = new QaqrLaunchResult { Id = request.Id };
        try
        {
            if (!BridgeAlive(request)) return 0;
            if (!QaqrLauncher.Valid(Config.QaqrPath)) throw new InvalidOperationException("QAQR 稳定入口不可用，请重新选择 QAQ-Revival.exe。");
            if (String.IsNullOrEmpty(request.QaqrUserData) || !Path.IsPathRooted(request.QaqrUserData)) throw new InvalidDataException("QAQR 用户数据路径无效。");
            using (var manager = Process.Start(QaqrLauncher.StartInfo(Config.QaqrPath, request.QaqrUserData))) { }
            // No fixed-name/PID wait: Electron may immediately exit and relaunch, or
            // activate its existing single instance. The bridge observes verified main images.
            result.Success = true;
        }
        catch (Exception error) { result.Error = error.Message; }
        App.Save(QaqrResultPath, result); return result.Success ? 0 : 1;
    }
    private static int Assist()
    {
        Request request;
        try { request = Json.Deserialize<Request>(File.ReadAllText(RequestPath)); }
        catch (Exception error) { Log("watch", "无法读取本轮启动记录：" + error.Message); return 1; }
        if (request == null || LaunchMethod.Normalize(request.LaunchMethod) != LaunchMethod.Normalize(Config.LaunchMethod)) return 1;
        if (request.Repair) return AutoRepair.Finish(Profile, Config, request);
        // A stale/manual task invocation must not open managed QAQR outside Steam.
        if (LaunchMethod.Normalize(Config.LaunchMethod) == LaunchMethod.Qaqr && Config.QaqrSteamManaged) return 0;
        if (LaunchMethod.Normalize(Config.LaunchMethod) == LaunchMethod.Qaqr) return OpenQaqr(request);
        if (!BridgeAlive(request)) { CleanupStoppedSession(request); return 0; }
        // Stay outside Steam's process tree so Steam Stop can still clean this session.
        WatchBridge(request);
        return 0;
    }
    private static void WatchBridge(Request request)
    {
        var owned = SessionRoot(request);
        Log("watch", "停止监护已就绪；桥接 PID=" + request.Pid + "，启动器根 PID=" + request.LauncherPid);
        while (BridgeAlive(request))
        {
            TrackSession(owned);
            Thread.Sleep(500);
        }
        Log("watch", "桥接已结束，清理本轮残留进程。");
        TrackSession(owned);
        CloseOwnedLaunchers(owned);
        Log("watch", "本轮停止清理完成。");
        // Also dismiss any helper-owned error dialog left open during a stop.
        Environment.Exit(0);
    }

    private static Dictionary<int, long> SessionRoot(Request request)
    {
        var owned = new Dictionary<int, long>();
        if (request.LauncherPid > 0 && request.LauncherStartTicks > 0)
            owned[request.LauncherPid] = request.LauncherStartTicks;
        return owned;
    }

    private static void CleanupStoppedSession(Request request)
    {
        var owned = SessionRoot(request);
        TrackSession(owned);
        CloseOwnedLaunchers(owned);
        Log("watch", "启动期间已取消，清理完成。");
    }

    private static void TrackSession(Dictionary<int, long> owned)
    {
        // Match descendants of this launch's recorded root, not every launcher
        // or game on the machine. Retain exited parents to discover their children.
        try
        {
            var candidates = new List<int[]>();
            string filter = Profile == GameProfile.Endfield ? "Name='Launcher.exe' OR Name='Games.exe' OR Name='QtWebEngineProcess.exe' OR Name='Endfield.exe' OR Name='UnityCrashHandler64.exe' OR Name='PCGamePlatform.exe' OR Name='PlatformProcess.exe' OR Name='game_security_protection.exe' OR Name='BLWebBrowser.exe'" : "Name='launcher.exe' OR Name='launcher_main.exe' OR Name='launcher_updater.exe' OR Name='msedgewebview2.exe' OR Name='Wuthering Waves.exe' OR Name='Client-Win64-Shipping.exe' OR Name='UnrealCEFSubProcess.exe' OR Name='CrashReportClient.exe'";
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process WHERE " + filter))
            using (var rows = searcher.Get())
                foreach (ManagementObject row in rows)
                    using (row) candidates.Add(new[] { Convert.ToInt32(row["ProcessId"]), Convert.ToInt32(row["ParentProcessId"]) });
            bool added;
            do
            {
                added = false;
                foreach (var candidate in candidates)
                {
                    int pid = candidate[0], parent = candidate[1];
                    if (owned.ContainsKey(pid) || !owned.ContainsKey(parent)) continue;
                    long started = CreationTicks(pid);
                    string path = ImagePath(pid);
                    if (started < owned[parent] || !SessionPath(path)) continue;
                    owned[pid] = started;
                    added = true;
                }
            } while (added);
        }
        catch (ManagementException error) { Log("watch", "读取本轮进程树失败：" + error.Message); }
    }

    internal static bool BridgeAlive(Request request)
    {
        if (request == null) return false;
        try
        {
            using (var bridge = Process.GetProcessById(request.Pid))
                return !bridge.HasExited && bridge.StartTime.ToUniversalTime().Ticks == request.StartTicks &&
                    bridge.ProcessName == "steam-bridge";
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    private static bool LauncherRunning()
    {
        return LauncherProcesses().Count != 0;
    }
    private static Dictionary<int, long> LauncherProcesses()
    {
        var result = new Dictionary<int, long>();
        foreach (string name in Profile.LauncherProcesses)
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    try
                    {
                        string path = ImagePath(process.Id);
                        if (path != null && path.StartsWith(Path.GetDirectoryName(Launcher) + "\\", StringComparison.OrdinalIgnoreCase))
                            result[process.Id] = process.StartTime.ToUniversalTime().Ticks;
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
        return result;
    }
    private static void TrackLaunchers(Dictionary<int, long> before, Dictionary<int, long> owned)
    {
        foreach (var pair in LauncherProcesses())
            if (!before.ContainsKey(pair.Key) || before[pair.Key] != pair.Value) owned[pair.Key] = pair.Value;
    }
    private static void CloseOwnedLaunchers(Dictionary<int, long> owned)
    {
        // This bridge's newly opened launcher must finish as well, otherwise
        // Steam keeps the app running after the actual game has exited.
        foreach (var pair in owned)
        {
            try
            {
                using (var process = Process.GetProcessById(pair.Key))
                {
                    if (process.HasExited || CreationTicks(process.Id) != pair.Value) continue;
                    string path = ImagePath(process.Id);
                    if (!SessionPath(path)) continue;
                    string name = process.ProcessName;
                    process.Kill();
                    process.WaitForExit(3000);
                    Log("cleanup", "关闭本轮进程 " + name + " PID=" + process.Id);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception error) { Log("bridge", "启动器收尾失败：" + error.Message); }
        }
    }
    private static void RemoveLauncherOverlay(Dictionary<int, long> owned)
    {
        // Steam creates a UI context in both the Qt launcher and the game. Its
        // notification queue selected the launcher, hiding screenshot toasts.
        // Retire only our launcher's UI once the real game's UI exists. Valve's
        // renderer stays loaded, preserving its normal child-process hooks.
        try
        {
            var launcherUis = new List<int>();
            bool gameUiReady = false;
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='gameoverlayui64.exe'"))
            using (var rows = searcher.Get())
                foreach (ManagementObject row in rows)
                    using (row)
                    {
                        string command = Convert.ToString(row["CommandLine"]);
                        if (!Regex.IsMatch(command, @"(?:^|\s)-gameid\s+" + Profile.AppId + @"(?:\s|$)")) continue;
                        var match = Regex.Match(command, @"(?:^|\s)-pid\s+(\d+)(?:\s|$)");
                        if (!match.Success) continue;
                        int target = Int32.Parse(match.Groups[1].Value);
                        int ui = Convert.ToInt32(row["ProcessId"]);
                        string path = ImagePath(target);
                        if (String.Equals(path, GameImage, StringComparison.OrdinalIgnoreCase)) gameUiReady = true;
                        else if (owned.ContainsKey(target))
                        {
                            using (var launcher = Process.GetProcessById(target))
                                if (launcher.StartTime.ToUniversalTime().Ticks == owned[target] && path != null &&
                                    path.StartsWith(Path.GetDirectoryName(Launcher) + "\\", StringComparison.OrdinalIgnoreCase)) launcherUis.Add(ui);
                        }
                    }
            if (!gameUiReady) return;
            foreach (int ui in launcherUis)
                using (var process = Process.GetProcessById(ui))
                {
                    if (process.ProcessName != "gameoverlayui64") continue;
                    process.Kill();
                    Log("bridge", "关闭启动器重复叠加层 PID=" + ui + "；保留国服叠加层和截图通知。");
                }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (ManagementException error) { Log("bridge", "叠加层状态检查失败：" + error.Message); }
        catch (System.ComponentModel.Win32Exception error) { Log("bridge", "叠加层收尾失败：" + error.Message); }
    }
    private static bool GameRunning()
    {
        int self = Process.GetCurrentProcess().Id;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameImage)))
            using (process)
            {
                if (process.Id == self) continue;
                if (String.Equals(ImagePath(process.Id), GameImage, StringComparison.OrdinalIgnoreCase)) return true;
            }
        return false;
    }
    internal static string ImagePath(int processId)
    {
        IntPtr handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(2048);
            int length = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }
    private static long CreationTicks(int processId)
    {
        IntPtr handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            long created, exited, kernel, user;
            return GetProcessTimes(handle, out created, out exited, out kernel, out user) ? DateTime.FromFileTimeUtc(created).Ticks : 0;
        }
        finally { CloseHandle(handle); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    private static void WriteJson(string path, object value)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, Json.Serialize(value), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }
    private static void Log(string role, string message)
    {
        using (var mutex = new Mutex(false, @"Local\SteamEndfieldBridgeLog"))
        {
            try { mutex.WaitOne(); }
            catch (AbandonedMutexException) { }
            try { File.AppendAllText(Path.Combine(Root, "bridge.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + role + "] " + message + Environment.NewLine); }
            finally { mutex.ReleaseMutex(); }
        }
    }
}

}

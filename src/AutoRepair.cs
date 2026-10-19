using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace EndfieldSteamBridge
{
    // This code is part of every installed bridge EXE, independent of the QAQR UI.
    internal static class AutoRepair
    {
        internal sealed class Ready { public Ready() { } public string Id { get; set; } public bool Success { get; set; } public string Error { get; set; } }
        private static string ReadyPath(GameProfile profile) { return Path.Combine(profile.Home, "repair-ready.json"); }
        internal static bool ValidRequest(BridgeRuntime.Request request, DateTime now)
        {
            Guid id;
            return request != null && request.Repair && request.Pid > 0 && request.StartTicks > 0 && Guid.TryParseExact(request.Id, "N", out id) &&
                request.CreatedUtcTicks > now.AddMinutes(-2).Ticks && request.CreatedUtcTicks <= now.AddSeconds(5).Ticks;
        }
        internal static bool Pending(GameProfile profile, RuntimeConfig config)
        {
            if (config.AutoSync == false || !File.Exists(profile.StatePath)) return false;
            var state = App.Load<Installation>(profile.StatePath);
            var metadata = KeyValues.ReadAppInfo(Path.Combine(state.SteamRoot, "appcache", "appinfo.vdf"), profile);
            var manifest = File.Exists(state.ManifestPath) ? KeyValues.Child(KeyValues.Parse(File.ReadAllText(state.ManifestPath)), "AppState") : null;
            return AutoSync.NeedsSync(state, metadata, manifest);
        }
        public static bool TryStart(GameProfile profile, RuntimeConfig config)
        {
            string steamId = Environment.GetEnvironmentVariable("SteamAppId");
            if (!String.IsNullOrEmpty(steamId) && steamId != profile.AppId.ToString()) return false;
            if (!Detection.Same(System.Reflection.Assembly.GetExecutingAssembly().Location, profile.RuntimeExe)) return false;
            if (!Pending(profile, config)) return false;
            InstallService.EnsureGameClosed(profile, config.LaunchMethod);
            AutoSync.AssertIdle(config.SteamRoot, new[] { profile }, profile.AppId);
            BridgeRuntime.Request request;
            using (var self = Process.GetCurrentProcess()) request = new BridgeRuntime.Request { Repair = true, Id = Guid.NewGuid().ToString("N"),
                Pid = self.Id, StartTicks = self.StartTime.ToUniversalTime().Ticks, CreatedUtcTicks = DateTime.UtcNow.Ticks, LaunchMethod = config.LaunchMethod };
            App.Save(Path.Combine(profile.Home, "request.json"), request);
            using (var task = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/run /tn " + Arguments.Quote(profile.TaskName)) { UseShellExecute = false, CreateNoWindow = true }))
            { task.WaitForExit(); if (task.ExitCode != 0) throw new InvalidOperationException("自动同步任务无法启动，请在 QAQR 中重新应用桥接设置。"); }
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(ReadyPath(profile)))
                {
                    var ready = App.Load<Ready>(ReadyPath(profile));
                    if (ready.Id == request.Id) { if (!ready.Success) throw new InvalidOperationException(ready.Error); return true; }
                }
                Thread.Sleep(100);
            }
            throw new TimeoutException("自动同步任务未响应，请在 QAQR 中重新应用桥接设置。");
        }
        public static int Finish(GameProfile profile, RuntimeConfig config, BridgeRuntime.Request request)
        {
            bool acknowledged = false;
            try
            {
                if (!ValidRequest(request, DateTime.UtcNow) || !BridgeRuntime.BridgeAlive(request) || !Detection.Same(BridgeRuntime.ImagePath(request.Pid), profile.RuntimeExe)) return 1;
                App.Save(ReadyPath(profile), new Ready { Id = request.Id, Success = true }); acknowledged = true;
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (BridgeRuntime.BridgeAlive(request) && DateTime.UtcNow < deadline) Thread.Sleep(100);
                if (BridgeRuntime.BridgeAlive(request)) throw new InvalidOperationException("原桥接进程尚未退出，自动同步已停止。");
                bool created;
                using (var mutex = new Mutex(true, @"Local\EndfieldSteamBridgeManager", out created))
                {
                    if (!created) throw new InvalidOperationException("桥接设置正在使用，请关闭独立 steam-bridge 窗口后再从 Steam 启动。");
                    var result = AutoSync.Run(new[] { profile }, message => App.Save(Path.Combine(profile.Home, "auto-sync-status.json"), new { message = message }), profile.AppId);
                    App.Save(Path.Combine(profile.Home, "auto-sync-status.json"), result);
                    if (result.deferred || result.warnings.Length > 0 || (!result.changed && Pending(profile, config)))
                        throw new InvalidOperationException(result.message ?? String.Join("\n", result.warnings));
                }
                // Re-enter via Steam; the next bridge invocation still requires the correct SteamAppId.
                var start = new ProcessStartInfo(Path.Combine(config.SteamRoot, "steam.exe"), "-applaunch " + profile.AppId) { UseShellExecute = false };
                foreach (string key in new[] { "SteamAppId", "SteamGameId", "SteamOverlayGameId" }) start.EnvironmentVariables.Remove(key);
                using (var steam = Process.Start(start)) { }
                return 0;
            }
            catch (Exception error)
            {
                if (!acknowledged) App.Save(ReadyPath(profile), new Ready { Id = request.Id, Success = false, Error = error.Message });
                else MessageBox.Show(error.Message, "Steam 桥接 · 自动同步", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace EndfieldSteamBridge
{
    internal static class Arguments
    {
        public static string Quote(string value)
        {
            var text = new System.Text.StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); text.Append(c); slashes = 0;
            }
            return text.Append('\\', slashes * 2).Append('"').ToString();
        }
    }

    internal static class BridgeApi
    {
        public sealed class GameRequest
        {
            public string GameId { get; set; }
            public string Game { get; set; }
            public string Launcher { get; set; }
            public string ChannelId { get; set; }
            public string LaunchMethod { get; set; }
            public string QaqrPath { get; set; }
            public string QaqrUserData { get; set; }
            public bool? QaqrSteamManaged { get; set; }
            public bool? AutoSync { get; set; }
        }
        public sealed class Request
        {
            public string Action { get; set; }
            public GameRequest[] Games { get; set; }
        }
        internal static void Validate(Request request)
        {
            if (request == null || !new[] { "status", "apply", "restore", "auto-sync" }.Contains(request.Action) ||
                request.Games == null || request.Games.Length < 1 || request.Games.Length > 2 ||
                request.Games.Any(g => g == null || !GameProfile.All.Any(p => p.Id == g.GameId)) ||
                request.Games.Select(g => g.GameId).Distinct().Count() != request.Games.Length ||
                (request.Action == "restore" && request.Games.Length != 1))
                throw new InvalidDataException("Steam 桥接请求无效。");
            foreach (var game in request.Games)
            {
                if (game.LaunchMethod != null) LaunchMethod.Normalize(game.LaunchMethod);
                if (game.ChannelId != null) GameChannel.Get(game.ChannelId);
            }
        }
        private static DetectionResult Scan(GameRequest game)
        {
            var profile = GameProfile.Get(game.GameId);
            // Existing standalone installations keep their mode and original backups.
            string method = game.LaunchMethod;
            if (method == null && !File.Exists(profile.ConfigPath)) method = LaunchMethod.Qaqr;
            string channel = game.ChannelId;
            if (channel == null && profile == GameProfile.Endfield && !String.IsNullOrEmpty(game.Game)) channel = GameChannel.IdentifyGame(game.Game);
            return Detection.Scan(game.Launcher, game.Game, channel, game.GameId, method,
                game.QaqrPath, game.QaqrUserData, game.QaqrSteamManaged, game.AutoSync);
        }
        private static object Describe(DetectionResult scan)
        {
            var profile = GameProfile.Get(scan.GameId);
            string metadataError = null;
            try { KeyValues.ReadAppInfo(Path.Combine(scan.SteamRoot ?? "", "appcache", "appinfo.vdf"), profile); }
            catch (Exception error) { metadataError = error.Message; }
            return new {
                gameId = profile.Id, name = profile.Name, appId = profile.AppId,
                options = new { launchMethod = scan.LaunchMethod ?? LaunchMethod.Qaqr, channelId = scan.ChannelId ?? "official",
                    launcher = scan.Launcher ?? "", qaqrSteamManaged = scan.QaqrSteamManaged, autoSync = scan.AutoSync },
                gamePath = scan.Game ?? "", qaqrPath = scan.QaqrPath ?? "", steamRoot = scan.SteamRoot ?? "",
                clientReady = scan.ClientReady, clientStatus = scan.GameStatus,
                accountKnown = scan.Account != null && scan.Account.Known, owned = scan.Account != null && scan.Account.Owned,
                accountError = scan.Account == null ? null : scan.Account.Error,
                installed = scan.Installation != null, phase = scan.Installation == null ? null : scan.Installation.Phase,
                installedVersion = scan.Installation == null ? null : scan.Installation.Version,
                metadataReady = metadataError == null, metadataError = metadataError, error = scan.Error
            };
        }
        public static object Execute(Request request, Action<string> progress)
        {
            Validate(request);
            if (request.Action == "status") return new { success = true, games = request.Games.Select(g => Describe(Scan(g))).ToArray() };
            bool created;
            // Also excludes the standalone manager, which owns this same mutex.
            using (var mutex = new Mutex(true, @"Local\EndfieldSteamBridgeManager", out created))
            {
                if (!created) {
                    if (request.Action == "auto-sync") return new { success = true, deferred = true, message = "独立 steam-bridge 正在运行，自动同步已延后。" };
                    throw new InvalidOperationException("Steam 桥接正在处理，请关闭独立 steam-bridge 窗口后重试。");
                }
                if (request.Action == "auto-sync") return AutoSync.Run(request.Games.Select(g => GameProfile.Get(g.GameId)).ToArray(), progress, 0, request.Games[0].QaqrUserData);
                if (request.Action == "apply") InstallService.EnableMany(request.Games.Select(Scan).ToArray(), progress);
                else InstallService.Restore(progress, GameProfile.Get(request.Games[0].GameId));
                return new { success = true, message = request.Action == "apply" ? "已设置 / 同步 Steam 桥接，Steam 已重新启动。" : "已撤回 Steam 桥接并恢复原记录，Steam 已重新启动。" };
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--api")
            {
                try
                {
                    if (new FileInfo(args[1]).Length > 65536) throw new InvalidDataException("Steam 桥接请求过大。");
                    var request = App.Load<BridgeApi.Request>(args[1]);
                    App.Save(args[2], BridgeApi.Execute(request, message => App.Save(args[2], new { success = false, progress = message })));
                    return 0;
                }
                catch (Exception error) { App.Save(args[2], new { success = false, error = error.Message }); return 1; }
            }
            try
            {
                if (args.Length == 3 && args[0] == "--probe") { App.Save(args[1], SteamAccount.Probe(Detection.FindSteam(), GameProfile.Get(args[2]))); return 0; }
                if (args.Length == 2 && args[0] == "--assist") return BridgeRuntime.RunAssist(args[1]);
                if (args.Length >= 2 && args[0] == "--bridge") return BridgeRuntime.Run(args[1]);
                return 1;
            }
            catch (Exception error) { MessageBox.Show(error.Message, "QAQR · Steam 桥接", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
    }
}

using System;
using System.IO;
using System.Linq;

namespace EndfieldSteamBridge
{
    internal sealed class GameProfile
    {
        public static readonly GameProfile Endfield = new GameProfile("endfield", "终末地", "Arknights: Endfield", 4732690, "Endfield Steam CN Bridge");
        public static readonly GameProfile Wuthering = new GameProfile("wuthering-waves", "鸣潮", "Wuthering Waves", 3513350, "Wuthering Waves Steam CN Bridge");
        public static readonly GameProfile[] All = new[] { Endfield, Wuthering };
        public readonly string Id, Name, SteamName, BridgeFolder;
        public readonly uint AppId;
        private GameProfile(string id, string name, string steamName, uint appId, string bridgeFolder)
        { Id = id; Name = name; SteamName = steamName; AppId = appId; BridgeFolder = bridgeFolder; }
        // Keep the original Endfield paths and task name so 0.2.x installs remain restorable.
        public string Home { get { return this == Endfield ? App.Home : Path.Combine(App.Home, Id); } }
        public string StatePath { get { return Path.Combine(Home, "installation.json"); } }
        public string ConfigPath { get { return Path.Combine(Home, "runtime.json"); } }
        public string RuntimeExe { get { return Path.Combine(Home, "app", "steam-bridge.exe"); } }
        public string TaskName { get { return App.TaskName + (this == Endfield ? "" : "-" + Id); } }
        public string ManifestName { get { return "appmanifest_" + AppId + ".acf"; } }
        public string LauncherName { get { return this == Endfield ? "鹰角启动器" : "库洛启动器"; } }
        public string ClientFile { get { return this == Endfield ? "Endfield.exe" : "Wuthering Waves.exe"; } }
        public string[] LauncherProcesses { get { return this == Endfield ? new[] { "Launcher", "Games", "QtWebEngineProcess" } : new[] { "launcher", "launcher_main", "launcher_updater", "msedgewebview2" }; } }
        public string[] GameProcesses { get { return this == Endfield ? new[] { "Endfield" } : new[] { "Wuthering Waves", "Client-Win64-Shipping" }; } }
        public static GameProfile Get(string id)
        {
            if (String.IsNullOrEmpty(id)) return Endfield;
            foreach (var profile in All) if (profile.Id == id || profile.AppId.ToString() == id) return profile;
            throw new InvalidDataException("不支持的游戏：" + id);
        }
        public string ValidateExecutable(string executable)
        {
            if (String.IsNullOrWhiteSpace(executable)) throw new InvalidDataException("Steam 启动文件为空。");
            string path = executable.Replace('/', '\\');
            if (Path.IsPathRooted(path) || path.Split('\\').Any(p => p == ".." || p == "." || p.Length == 0 || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidDataException("Steam 启动路径无效，已停止修改。");
            bool supported = this == Endfield ? path.Equals("Endfield.exe", StringComparison.OrdinalIgnoreCase) :
                new[] { "launcher.exe", "Wuthering Waves.exe", "Client-Win64-Shipping.exe" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
            if (!supported) throw new InvalidDataException(Name + "的 Steam 启动文件已变化，请更新工具：" + path);
            return path;
        }
        public string SteamLaunchSource(string executable, string launcher, string game)
        {
            string name = Path.GetFileName(ValidateExecutable(executable));
            if (name.Equals("launcher.exe", StringComparison.OrdinalIgnoreCase)) return launcher;
            if (this == Endfield) return game;
            return name.Equals("Client-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) ? WutheringLauncher.GameProcess(game) : Path.Combine(WutheringLauncher.GameRoot(game), "Wuthering Waves.exe");
        }
        public override string ToString() { return Name + " / " + SteamName; }
    }
}

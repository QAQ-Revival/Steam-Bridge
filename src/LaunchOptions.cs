using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EndfieldSteamBridge
{
    internal static class LaunchOptions
    {
        private static string[] Keys(Installation state)
        { return new[] { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps", GameProfile.Get(state.GameId).AppId.ToString(), "LaunchOptions" }; }
        public static string ConfigPath(string steam, ulong owner)
        { return Path.Combine(steam, "userdata", ((uint)owner).ToString(), "config", "localconfig.vdf"); }
        public static void Prepare(Installation state, ulong owner)
        {
            state.SteamId = owner; state.LaunchOptionsPath = ConfigPath(state.SteamRoot, owner);
            state.OriginalLaunchOptions = Read(state);
            state.ManagedLaunchOptions = Arguments.Quote(state.RuntimeExecutable) + " --bridge " + GameProfile.Get(state.GameId).Id + " -- %command%";
        }
        public static void Validate(Installation state)
        {
            if (state.LaunchOptionsPath == null) return; // 0.2.x restore
            if (!Detection.Same(state.LaunchOptionsPath, ConfigPath(state.SteamRoot, state.SteamId))) throw new InvalidDataException("Steam 启动选项路径无效。");
            string current = Read(state);
            if (current != state.ManagedLaunchOptions && current != state.OriginalLaunchOptions)
                throw new InvalidOperationException("Steam 启动选项已被手动更改，已保留。请先在游戏属性中恢复工具设置的启动选项再重试。");
        }
        public static void Apply(Installation state) { Validate(state); Write(state, state.ManagedLaunchOptions); }
        public static void Restore(Installation state)
        {
            if (state.LaunchOptionsPath == null) return;
            Validate(state); Write(state, state.OriginalLaunchOptions);
        }
        private static string Read(Installation state)
        {
            var node = KeyValues.Parse(File.ReadAllText(state.LaunchOptionsPath));
            string[] keys = Keys(state);
            for (int i = 0; i < keys.Length - 1; i++) node = KeyValues.Child(node, keys[i]);
            return KeyValues.String(node, keys[keys.Length - 1]);
        }
        private static void Write(Installation state, string value)
        {
            string path = state.LaunchOptionsPath;
            byte[] bytes = File.ReadAllBytes(path);
            string text = File.ReadAllText(path);
            string changed = KeyValues.EditString(text, Keys(state), value);
            if (changed == text) return;
            string temp = path + ".steam-bridge-tmp";
            File.WriteAllText(temp, changed, new UTF8Encoding(bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191));
            App.Replace(temp, path);
        }
    }
}

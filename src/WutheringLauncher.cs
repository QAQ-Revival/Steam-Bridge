using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace EndfieldSteamBridge
{
    internal static class WutheringLauncher
    {
        // Read only the new launcher's public distribution settings, never its account cache.
        public static bool ValidLauncher(string path)
        {
            if (!Detection.ValidLauncher(path)) return false;
            try
            {
                foreach (string directory in Directory.GetDirectories(Path.GetDirectoryName(path)).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    string file = Path.Combine(directory, "Assets", "KRApp.conf");
                    if (!File.Exists(Path.Combine(directory, "launcher_main.dll")) || !File.Exists(file)) continue;
                    string text = File.ReadAllText(file);
                    if (!text.TrimStart().StartsWith("{"))
                    {
                        byte[] data = Convert.FromBase64String(text);
                        for (int i = 0; i < data.Length; i++) data[i] ^= 0x63;
                        text = Encoding.UTF8.GetString(data);
                    }
                    var config = App.Json.Deserialize<Dictionary<string, object>>(text);
                    if (KeyValues.String(config, "gameId") == "G152") return true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (FormatException) { }
            catch (ArgumentException) { }
            return false;
        }
        public static string FindLauncher()
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (key == null) continue;
                        foreach (string name in key.GetSubKeyNames()) using (var item = key.OpenSubKey(name))
                        {
                            if (item == null || Convert.ToString(item.GetValue("DisplayName")) != "鸣潮") continue;
                            string location = Convert.ToString(item.GetValue("InstallLocation"));
                            if (!String.IsNullOrEmpty(location) && ValidLauncher(Path.Combine(location, "launcher.exe"))) return Path.Combine(location, "launcher.exe");
                            string icon = Convert.ToString(item.GetValue("DisplayIcon")).Trim('"');
                            int comma = icon.LastIndexOf(','); if (comma > 0) icon = icon.Substring(0, comma).Trim('"');
                            if (ValidLauncher(icon)) return icon;
                        }
                    }
            return null;
        }
        public static string GameRoot(string path)
        {
            if (String.IsNullOrEmpty(path)) return null;
            return Path.GetFileName(path).Equals("Client-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), "..", "..", "..")) : Path.GetDirectoryName(path);
        }
        public static string GameProcess(string path) { return Path.Combine(GameRoot(path), "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe"); }
        public static bool ValidGame(string path)
        {
            if (String.IsNullOrEmpty(path) || !File.Exists(path) ||
                !(Path.GetFileName(path).Equals("Wuthering Waves.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("Client-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase))) return false;
            string root = GameRoot(path);
            return File.Exists(Path.Combine(root, "Wuthering Waves.exe")) && Directory.Exists(Path.Combine(root, "Client", "Content", "Paks")) && Detection.Is64BitExecutable(GameProcess(path));
        }
        public static string ClientStatus(string launcher, string game, out bool ready)
        {
            ready = false;
            if (!ValidLauncher(launcher)) return "请选择鸣潮国服新版库洛启动器根目录中的 launcher.exe";
            if (!ValidGame(game)) return "已找到鸣潮国服启动器；请选择 Wuthering Waves.exe 或先下载游戏";
            ready = true; return "鸣潮国服启动器与客户端可用 · 保留启动器更新和画质选择";
        }
    }
}

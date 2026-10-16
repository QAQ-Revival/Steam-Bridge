using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EndfieldSteamBridge
{
    internal sealed class GameChannel
    {
        public static readonly GameChannel Official = new GameChannel("official", "官服", "abYeZZ16BPluCFyT", "1", "u8sdk_hg_pc");
        public static readonly GameChannel Bilibili = new GameChannel("bilibili", "Bilibili 服", "RhAij5MdwvTWpQ8N", "2", "u8sdk_bilibili_pc");
        public static readonly GameChannel[] All = new[] { Official, Bilibili };
        public readonly string Id, Name, LauncherCode, Number, SdkMarker;
        private GameChannel(string id, string name, string launcherCode, string number, string sdkMarker)
        { Id = id; Name = name; LauncherCode = launcherCode; Number = number; SdkMarker = sdkMarker; }
        public static GameChannel Get(string id)
        {
            // Runtime configs written before server selection always used the official server.
            if (String.IsNullOrEmpty(id) || id == Official.Id) return Official;
            if (id == Bilibili.Id) return Bilibili;
            throw new InvalidDataException("不支持的服务器类型：" + id);
        }
        public override string ToString() { return Name; }
        public static bool HasMarker(string path, string marker)
        {
            if (!File.Exists(path)) return false;
            // Inspect metadata strings only; never load launcher/game DLLs for detection.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > 32 * 1024 * 1024) return false;
                byte[] data = new byte[(int)stream.Length]; int position = 0, read;
                while (position < data.Length && (read = stream.Read(data, position, data.Length - position)) > 0) position += read;
                return Encoding.ASCII.GetString(data, 0, position).Contains(marker);
            }
        }
        public static string IdentifyLauncher(string path)
        {
            if (!Detection.ValidLauncher(path)) return null;
            foreach (var channel in All) if (HasMarker(path, channel.LauncherCode)) return channel.Id;
            return null;
        }
        public static string IdentifyGame(string path)
        {
            if (!Detection.ValidGame(path)) return null;
            string sdk = Path.Combine(Path.GetDirectoryName(path), "u8_channel.dll");
            foreach (var channel in All) if (HasMarker(sdk, channel.SdkMarker)) return channel.Id;
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace EndfieldSteamBridge
{
    // Steam text KeyValues and binary appinfo caches. No account tokens are read.
    internal static class KeyValues
    {
        public static Dictionary<string, object> Parse(string text)
        {
            int position = 0;
            return ReadObject(text, ref position, false);
        }
        private static Dictionary<string, object> ReadObject(string text, ref int pos, bool nested)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string key = Token(text, ref pos);
                if (key == null) { if (nested) throw new InvalidDataException("KeyValues 对象不完整。"); return result; }
                if (key == "}") { if (!nested) throw new InvalidDataException("KeyValues 括号不匹配。"); return result; }
                string value = Token(text, ref pos);
                if (value == null) throw new InvalidDataException("KeyValues 缺少值。");
                result[key] = value == "{" ? (object)ReadObject(text, ref pos, true) : value;
            }
        }
        private static string Token(string text, ref int pos)
        { int start; return Token(text, ref pos, out start); }
        private static string Token(string text, ref int pos, out int start)
        {
            while (pos < text.Length)
            {
                if (Char.IsWhiteSpace(text[pos]) || text[pos] == '\ufeff') { pos++; continue; }
                if (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '/')
                { while (pos < text.Length && text[pos] != '\n') pos++; continue; }
                break;
            }
            start = pos;
            if (pos >= text.Length) return null;
            char c = text[pos++];
            if (c == '{' || c == '}') return c.ToString();
            var s = new StringBuilder();
            if (c != '"')
            { s.Append(c); while (pos < text.Length && !Char.IsWhiteSpace(text[pos]) && text[pos] != '{' && text[pos] != '}') s.Append(text[pos++]); return s.ToString(); }
            while (pos < text.Length)
            {
                c = text[pos++];
                if (c == '"') return s.ToString();
                if (c == '\\' && pos < text.Length)
                {
                    char next = text[pos++];
                    if (next == '\\' || next == '"') s.Append(next);
                    else if (next == 'n') s.Append('\n');
                    else if (next == 't') s.Append('\t');
                    else { s.Append('\\'); s.Append(next); }
                }
                else s.Append(c);
            }
            throw new InvalidDataException("KeyValues 字符串不完整。");
        }
        // Edit one scalar in place, preserving unrelated settings, comments and duplicate keys.
        public static string EditString(string text, string[] path, string value)
        { return EditAt(text, 0, path, 0, value); }
        private static string EditAt(string text, int position, string[] path, int depth, string value)
        {
            int keyStart, valueStart, foundStart = -1, foundEnd = -1, childStart = -1, close = text.Length;
            bool foundObject = false;
            while (true)
            {
                string key = Token(text, ref position, out keyStart);
                if (key == null || key == "}") { close = keyStart; break; }
                string token = Token(text, ref position, out valueStart);
                if (token == null || token == "}") throw new InvalidDataException("Steam 用户设置不完整。");
                int bodyStart = position;
                if (token == "{")
                {
                    int level = 1;
                    while (level > 0)
                    {
                        string next = Token(text, ref position);
                        if (next == null) throw new InvalidDataException("Steam 用户设置不完整。");
                        if (next == "{") level++; else if (next == "}") level--;
                    }
                }
                if (!key.Equals(path[depth], StringComparison.OrdinalIgnoreCase)) continue;
                if (foundStart >= 0) throw new InvalidDataException("Steam 启动设置有重复键，已停止修改。");
                foundStart = valueStart; foundEnd = position; childStart = bodyStart; foundObject = token == "{";
                if (depth == path.Length - 1 && value == null) { foundStart = keyStart; }
            }
            if (foundStart >= 0)
            {
                if (depth < path.Length - 1)
                {
                    if (!foundObject) throw new InvalidDataException("Steam 用户设置结构无效。");
                    return EditAt(text, childStart, path, depth + 1, value);
                }
                if (foundObject) throw new InvalidDataException("Steam 启动选项应为字符串。");
                return text.Substring(0, foundStart) + (value == null ? "" : Quote(value)) + text.Substring(foundEnd);
            }
            if (value == null) return text;
            string addition = Quote(path[path.Length - 1]) + "\t" + Quote(value);
            for (int i = path.Length - 2; i >= depth; i--) addition = Quote(path[i]) + " { " + addition + " }";
            return text.Insert(close, "\t" + addition + Environment.NewLine);
        }
        public static Dictionary<string, object> Child(Dictionary<string, object> o, string key)
        { object value; return o != null && o.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; }
        public static string String(Dictionary<string, object> o, string key)
        { object value; return o != null && o.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null; }
        public static string Quote(string value) { return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
        public static string Render(Dictionary<string, object> obj, int level = 0)
        {
            var b = new StringBuilder(); string indent = new string('\t', level);
            foreach (var pair in obj)
            {
                b.Append(indent).Append(Quote(pair.Key));
                var child = pair.Value as Dictionary<string, object>;
                if (child != null) b.AppendLine().Append(indent).AppendLine("{").Append(Render(child, level + 1)).Append(indent).AppendLine("}");
                else b.Append('\t').AppendLine(Quote(Convert.ToString(pair.Value, CultureInfo.InvariantCulture)));
            }
            return b.ToString();
        }
        public static Metadata ReadAppInfo(string steam, GameProfile profile)
        {
            var metadata = FindAppInfo(steam, profile);
            if (metadata == null) throw new InvalidDataException("Steam 缓存中没有" + profile.Name + "的版本信息。请先将游戏加入库，并打开它的库页面，再重试检测。");
            return metadata;
        }
        // An installed bridge can still launch while Steam is rebuilding its cache.
        public static Metadata FindAppInfo(string steam, GameProfile profile)
        {
            if (System.String.IsNullOrEmpty(steam)) return null;
            string cache = Path.Combine(steam, "appcache");
            uint shard = AppInfoShard(profile.AppId);
            string shardFile = Path.Combine(cache, "appinfo", "appinfo_" + shard.ToString("x2", CultureInfo.InvariantCulture) + ".vdf");
            using (var r = OpenAppInfo(shardFile) ?? OpenAppInfo(Path.Combine(cache, "appinfo.vdf")))
            {
                if (r == null) return null;
                uint magic = r.ReadUInt32(); r.ReadUInt32();
                if (magic == 0x0756442a) return ReadShard(r, profile, shard);
                if (magic == 0x07564429 || magic == 0x07564428 || magic == 0x07564427) return ReadLegacyAppInfo(r, profile, magic);
                throw new InvalidDataException("暂不支持此 Steam 元数据版本，请更新工具。");
            }
        }
        private static BinaryReader OpenAppInfo(string path)
        {
            try { return new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        private static uint AppInfoShard(uint appId)
        {
            // Steam v42 uses the low byte of the MurmurHash3 fmix32 of the AppID.
            unchecked
            {
                uint hash = (appId ^ (appId >> 16)) * 0x85ebca6bu;
                hash = (hash ^ (hash >> 13)) * 0xc2b2ae35u;
                return (hash ^ (hash >> 16)) & 0xff;
            }
        }
        private static Metadata ReadShard(BinaryReader r, GameProfile profile, uint expectedShard)
        {
            var stream = r.BaseStream;
            uint shard = r.ReadUInt32(), shardCount = r.ReadUInt32(), count = r.ReadUInt32(), indexSize = r.ReadUInt32();
            int keyCount = r.ReadInt32();
            uint keysSize = r.ReadUInt32(), dataSize = r.ReadUInt32();
            r.ReadUInt64(); // Skip the final eight bytes of the v42 header.
            long keysStart = stream.Position + indexSize, dataStart = keysStart + keysSize;
            if (shard != expectedShard || shardCount != 256 || dataStart + dataSize > stream.Length)
                throw new InvalidDataException("Steam 元数据分片头无效。");
            for (uint i = 0; i < count; i++)
            {
                if (stream.Position + 16 > keysStart) throw new InvalidDataException("Steam 元数据分片索引不完整。");
                uint id = r.ReadUInt32(), offset = r.ReadUInt32(), size = r.ReadUInt32(), infoSize = r.ReadUInt32();
                long next = stream.Position + infoSize;
                if (next > keysStart) throw new InvalidDataException("Steam 元数据分片索引无效。");
                if (id == profile.AppId)
                {
                    if (size <= 20 || (long)offset + size > dataSize) throw new InvalidDataException("Steam 元数据分片记录无效。");
                    stream.Position = keysStart;
                    var keys = ReadKeys(r, keyCount, dataStart);
                    long record = dataStart + offset;
                    stream.Position = record + 20; // Each payload starts with a SHA-1 digest.
                    return ReadMetadata(r, keys, record + size, profile);
                }
                stream.Position = next;
            }
            return null;
        }
        private static Metadata ReadLegacyAppInfo(BinaryReader r, GameProfile profile, uint magic)
        {
            var stream = r.BaseStream;
            string[] keys = null;
            long limit = stream.Length;
            if (magic == 0x07564429)
            {
                long table = r.ReadInt64();
                if (table < 16 || table >= stream.Length) throw new InvalidDataException("Steam 元数据字符串表无效。");
                limit = table; stream.Position = table;
                keys = ReadKeys(r, r.ReadInt32(), stream.Length);
                stream.Position = 16;
            }
            int infoSize = magic == 0x07564427 ? 40 : 60;
            while (stream.Position + 8 <= limit)
            {
                uint id = r.ReadUInt32(); if (id == 0) break;
                uint size = r.ReadUInt32(); long next = stream.Position + size;
                if (size < infoSize || next > limit) throw new InvalidDataException("Steam 元数据记录无效。");
                if (id == profile.AppId)
                {
                    stream.Position += infoSize;
                    return ReadMetadata(r, keys, next, profile);
                }
                stream.Position = next;
            }
            return null;
        }
        private static Metadata ReadMetadata(BinaryReader r, string[] keys, long end, GameProfile profile)
        {
            var info = Child(BinaryObject(r, keys, end, 0), "appinfo");
            var config = Child(info, "config"); var depots = Child(info, "depots");
            var m = new Metadata { Build = String(Child(Child(depots, "branches"), "public"), "buildid"), Executable = String(Child(Child(config, "launch"), "0"), "executable") };
            uint build;
            if (!UInt32.TryParse(m.Build, out build) || build == 0 || System.String.IsNullOrEmpty(m.Executable)) throw new InvalidDataException(profile.Name + "元数据缺少启动或公共版本信息。请在 Steam 打开游戏库页面后重试。");
            m.Executable = profile.ValidateExecutable(m.Executable);
            foreach (var depot in depots)
            {
                uint depotId; if (!UInt32.TryParse(depot.Key, out depotId)) continue;
                string gid = String(Child(Child(depot.Value as Dictionary<string, object>, "manifests"), "public"), "gid");
                ulong parsed; if (UInt64.TryParse(gid, out parsed) && parsed > 0) m.Depots[depot.Key] = gid;
            }
            if (m.Depots.Count == 0) throw new InvalidDataException("没有可用的公共 depot 元数据。");
            return m;
        }
        private static string[] ReadKeys(BinaryReader r, int count, long end)
        {
            if (count < 0 || count > 2000000 || count > end - r.BaseStream.Position) throw new InvalidDataException("Steam 元数据字符串表无效。");
            var keys = new string[count];
            for (int i = 0; i < count; i++) keys[i] = CString(r, end);
            return keys;
        }
        private static string CString(BinaryReader r, long end)
        {
            var bytes = new List<byte>();
            while (r.BaseStream.Position < end)
            {
                byte b = r.ReadByte(); if (b == 0) return Encoding.UTF8.GetString(bytes.ToArray());
                if (bytes.Count >= 1048576) throw new InvalidDataException("Steam 字符串过长。");
                bytes.Add(b);
            }
            throw new InvalidDataException("Steam 元数据字符串不完整。");
        }
        private static Dictionary<string, object> BinaryObject(BinaryReader r, string[] keys, long end, int depth)
        {
            if (depth > 64) throw new InvalidDataException("Steam 元数据嵌套过深。");
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (r.BaseStream.Position < end)
            {
                byte type = r.ReadByte(); if (type == 8) return result;
                string key;
                if (keys != null) { uint index = r.ReadUInt32(); if (index >= keys.Length) throw new InvalidDataException("Steam 字符串索引无效。"); key = keys[index]; }
                else key = CString(r, end);
                object value;
                switch (type)
                {
                    case 0: value = BinaryObject(r, keys, end, depth + 1); break;
                    case 1: value = CString(r, end); break;
                    case 2: case 4: case 6: value = r.ReadUInt32(); break;
                    case 3: value = r.ReadSingle(); break;
                    case 7: case 10: value = r.ReadUInt64(); break;
                    default: throw new InvalidDataException("不支持的 Steam 元数据类型：" + type);
                }
                result[key] = value;
            }
            throw new InvalidDataException("Steam 元数据对象不完整。");
        }
    }
}

using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace EndfieldSteamBridge
{
    internal static class App
    {
        public const string Version = "0.3.0";
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        public static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndfieldSteamBridge");
        public static readonly string StatePath = Path.Combine(Home, "installation.json");
        public static readonly string ConfigPath = Path.Combine(Home, "runtime.json");
        public static string TaskName { get { return "EndfieldSteamBridge-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value; } }
        public static void Save(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, Json.Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        public static T Load<T>(string path) { return Json.Deserialize<T>(File.ReadAllText(path)); }
    }
    public sealed class RuntimeConfig
    {
        public string LaunchMethod { get; set; }
        public string QaqrPath { get; set; }
        public string QaqrUserData { get; set; }
        public string GameId { get; set; }
        public string ChannelId { get; set; }
        public string Launcher { get; set; }
        public string Game { get; set; }
        public string SteamRoot { get; set; }
        public string TaskName { get; set; }
    }
    public sealed class Installation
    {
        public string LaunchMethod { get; set; }
        public string GameId { get; set; }
        public string ChannelId { get; set; }
        public string Version { get; set; }
        public string SteamRoot { get; set; }
        public string ManifestPath { get; set; }
        public string OriginalManifestBackup { get; set; }
        public string PreviousManifestBackup { get; set; }
        public string BridgeDirectory { get; set; }
        public string Token { get; set; }
        public string TaskName { get; set; }
        public bool WasInstalled { get; set; }
        public string Phase { get; set; }
        public string ManagedManifestBackup { get; set; }
        public string Executable { get; set; }
        public string RuntimeExecutable { get; set; }
        public string SteamExecutable { get; set; }
        public string LaunchOptionsPath { get; set; }
        public string OriginalLaunchOptions { get; set; }
        public string ManagedLaunchOptions { get; set; }
        public ulong SteamId { get; set; }
    }
    public sealed class Ownership
    {
        public bool Known { get; set; }
        public bool Owned { get; set; }
        public bool Installed { get; set; }
        public ulong SteamId { get; set; }
        public string Name { get; set; }
        public string Error { get; set; }
    }
    internal sealed class DetectionResult
    {
        public string LaunchMethod;
        public string QaqrPath;
        public string QaqrUserData;
        public string GameId;
        public string ChannelId;
        public bool ClientReady;
        public string SteamRoot;
        public Ownership Account;
        public string Launcher;
        public string Game;
        public string GameStatus;
        public Installation Installation;
        public string Error;
    }
    internal sealed class Metadata
    {
        public string Build;
        public string Executable;
        public System.Collections.Generic.Dictionary<string, string> Depots = new System.Collections.Generic.Dictionary<string, string>();
    }
}

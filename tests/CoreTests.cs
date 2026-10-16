using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EndfieldSteamBridge
{
    internal static class CoreTests
    {
        private static int count;
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
        private static void Throws(Action action, string message)
        { bool threw = false; try { action(); } catch (IOException) { threw = true; } catch (InvalidDataException) { threw = true; } catch (InvalidOperationException) { threw = true; } Check(threw, message); }
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--qaqr-fixture")
            {
                if (args[1] == "handoff")
                    using (var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(args[2], "r123456789abc.exe"), "--qaqr-fixture hold " + Arguments.Quote(args[2])) { UseShellExecute = false, CreateNoWindow = true })) { }
                else { File.WriteAllText(Path.Combine(args[2], "pid.txt"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString()); System.Threading.Thread.Sleep(3500); }
                return 0;
            }
            if (args.Length == 3 && args[0] == "--probe") { App.Save(args[1], SteamAccount.Probe(Detection.FindSteam(), GameProfile.Get(args[2]))); return 0; }
            if ((args.Length == 3 || args.Length == 4) && args[0] == "--preview")
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                using (var form = new MainForm()) form.SavePreview(args[1], "official", args[2], args.Length == 4 ? args[3] : null);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--inspect-local")
            {
                var rows = new List<object>();
                foreach (var profile in GameProfile.All)
                {
                    var scan = Detection.Scan(null, null, null, profile.Id);
                    Metadata metadata = null; string error = null;
                    try { metadata = KeyValues.ReadAppInfo(Path.Combine(Detection.FindSteam(), "appcache", "appinfo.vdf"), profile); } catch (Exception e) { error = e.Message; }
                    rows.Add(new { game = profile.Id, scan = scan, metadata = metadata, metadataError = error });
                }
                App.Save(args[1], rows); return 0;
            }
            if (args.Length == 2 && args[0] == "--inspect-qaqr")
            {
                string entry = QaqrLauncher.Find();
                App.Save(args[1], new {
                    entry = entry, valid = QaqrLauncher.Valid(entry), running = entry != null && new QaqrProcesses(entry).Running(),
                    scans = GameProfile.All.Select(p => Detection.Scan(null, null, null, p.Id, LaunchMethod.Qaqr)).ToArray()
                }); return 0;
            }
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EndfieldBridge-tests-空 格-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                ChannelTests(root);
                MultiGameTests(root);
                QaqrTests(root);
                var vdf = KeyValues.Parse("//comment\n\"root\" { \"path\" \"D:\\\\游戏\\\\Endfield\" \"a\" { \"b\" \"quoted\\\"text\" } }");
                var roundtrip = KeyValues.Parse(KeyValues.Render(vdf));
                Check(KeyValues.String(KeyValues.Child(roundtrip, "root"), "path") == @"D:\游戏\Endfield", "VDF Unicode path roundtrip");
                Check(KeyValues.String(KeyValues.Child(KeyValues.Child(roundtrip, "root"), "a"), "b") == "quoted\"text", "VDF escaped quotes");
                Check(Arguments.Quote(@"C:\a b\") == "\"C:\\a b\\\\\"", "Windows trailing slash quoting");
                string exe = Path.Combine(root, "source.exe"); File.WriteAllBytes(exe, new byte[] { 77, 90, 1, 2 });
                var metadata = new Metadata { Executable = "Endfield.exe", Build = "123", Depots = new Dictionary<string, string> { { "4732691", "987" } } };
                for (int mode = 0; mode < 5; mode++)
                {
                    string steam = Path.Combine(root, "Steam " + mode), home = Path.Combine(root, "data " + mode);
                    string manifest = Path.Combine(steam, "steamapps", "appmanifest_4732690.acf"); Directory.CreateDirectory(Path.GetDirectoryName(manifest));
                    byte[] original = Encoding.UTF8.GetBytes("\ufeff\"AppState\"\r\n{\r\n\t\"installdir\" \"Original international\"\r\n}\r\n");
                    string official = Path.Combine(steam, "steamapps", "common", "Original international", "Endfield.exe");
                    if (mode == 1 || mode == 2 || mode == 4) { File.WriteAllBytes(manifest, original); Directory.CreateDirectory(Path.GetDirectoryName(official)); File.WriteAllText(official, "original game"); }
                    var service = new InstallFiles(home); var state = service.Prepare(steam, manifest, exe, metadata, 76561197960265728UL, mode == 4, null, exe);
                    Check(File.Exists(service.StatePath), "transaction state saved before mutation mode=" + mode);
                    if (mode == 2)
                    {
                        service.Commit(state); service.Rollback(state);
                        Check(File.ReadAllBytes(manifest).SequenceEqual(original), "rollback restores exact original bytes"); continue;
                    }
                    if (mode != 3) service.Commit(state);
                    if (mode == 1)
                    {
                        Throws(() => service.Prepare(steam, manifest, exe, metadata, 1, false, null, exe), "repeat enable cannot overwrite original backup");
                        File.WriteAllText(Path.Combine(state.BridgeDirectory, "user-note.txt"), "keep");
                    }
                    service.Restore(state); service.FinishRestore();
                    Check(!File.Exists(service.StatePath), "restore clears state mode=" + mode);
                    Check(state.WasInstalled ? File.ReadAllBytes(manifest).SequenceEqual(original) : !File.Exists(manifest), "restore previous Steam install state mode=" + mode);
                    if (mode == 1 || mode == 4) Check(File.ReadAllText(official) == "original game", "international game files preserved mode=" + mode);
                    if (mode == 1) Check(File.ReadAllText(Path.Combine(state.BridgeDirectory, "user-note.txt")) == "keep", "uninstall preserves unrelated files");
                }
                {
                    string steam = Path.Combine(root, "copy-failure"), home = Path.Combine(root, "copy-failure-data");
                    string manifest = Path.Combine(steam, "steamapps", "appmanifest_4732690.acf");
                    Directory.CreateDirectory(Path.GetDirectoryName(manifest)); File.WriteAllText(manifest, "original manifest");
                    var service = new InstallFiles(home);
                    Throws(() => service.Prepare(steam, manifest, exe + ".missing", metadata, 1, false, null, exe), "failed bridge copy rolls back");
                    Check(File.ReadAllText(manifest) == "original manifest" && !File.Exists(service.StatePath), "failed install leaves original manifest and no active state");
                }
                {
                    string steam = Path.Combine(root, "validation"), home = Path.Combine(root, "validation-data");
                    string manifest = Path.Combine(steam, "steamapps", "appmanifest_4732690.acf");
                    var service = new InstallFiles(home); var state = service.Prepare(steam, manifest, exe, metadata, 1, false, null, exe); service.Commit(state);
                    File.WriteAllText(manifest, "\"AppState\" { \"installdir\" \"External game\" }");
                    Throws(() => service.Restore(state), "external manifest changes are preserved for review");
                    File.Copy(state.ManagedManifestBackup, manifest, true); service.Restore(state); service.FinishRestore();
                }
                Console.WriteLine("ALL " + count + " CHECKS PASSED"); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
            finally
            {
                string full = Path.GetFullPath(root);
                if (full.StartsWith(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("EndfieldBridge-tests-")) Directory.Delete(full, true);
            }
        }
        private static void ChannelTests(string root)
        {
            Check(GameChannel.Get(App.Json.Deserialize<RuntimeConfig>("{\"Launcher\":\"legacy\"}").ChannelId) == GameChannel.Official, "legacy config defaults to official server");
            var games = new Dictionary<string, string>(); var launchers = new Dictionary<string, string>();
            foreach (var channel in GameChannel.All)
            {
                string folder = Path.Combine(root, channel.Id), launcher = Path.Combine(folder, "Launcher.exe"), gameRoot = Path.Combine(folder, "games", "Arknights Endfield");
                Directory.CreateDirectory(gameRoot); File.WriteAllText(launcher, "MZ public-build-marker/" + channel.LauncherCode);
                byte[] pe = new byte[256]; pe[0] = 0x4d; pe[1] = 0x5a; pe[0x3c] = 0x80; pe[0x80] = 0x50; pe[0x81] = 0x45; pe[0x84] = 0x64; pe[0x85] = 0x86;
                string game = Path.Combine(gameRoot, "Endfield.exe"); File.WriteAllBytes(game, pe);
                File.WriteAllText(Path.Combine(gameRoot, "UnityPlayer.dll"), "fixture");
                Directory.CreateDirectory(Path.Combine(gameRoot, "Endfield_Data")); File.WriteAllText(Path.Combine(gameRoot, "Endfield_Data", "app.info"), "Hypergryph\nEndfield\n");
                Directory.CreateDirectory(Path.Combine(gameRoot, "U8Data", "config")); File.WriteAllText(Path.Combine(gameRoot, "U8Data", "config", "config.gryph"), "fixture");
                File.WriteAllText(Path.Combine(gameRoot, "u8_channel.dll"), "MZ/" + channel.SdkMarker);
                games[channel.Id] = game; launchers[channel.Id] = launcher;
                bool ready; string status = Detection.ClientStatus(launcher, game, channel.Id, out ready);
                Check(ready, channel.Id + " launcher and game match without official-only SDK files");
                string wrong = channel == GameChannel.Official ? "bilibili" : "official";
                Detection.ClientStatus(launcher, game, wrong, out ready); Check(!ready, "reject wrong server selection for " + channel.Id);
                Detection.ClientStatus(launcher, null, channel.Id, out ready); Check(!ready, "launcher-only install cannot enable " + channel.Id);
            }
            bool matched;
            Detection.ClientStatus(launchers["official"], games["bilibili"], "official", out matched); Check(!matched, "reject official launcher paired with bilibili game");
            Detection.ClientStatus(launchers["bilibili"], games["official"], "bilibili", out matched); Check(!matched, "reject bilibili launcher paired with official game");
        }
        private static void MultiGameTests(string root)
        {
            Check(GameProfile.Get(null) == GameProfile.Endfield && GameProfile.Endfield.StatePath != GameProfile.Wuthering.StatePath && GameProfile.Endfield.TaskName != GameProfile.Wuthering.TaskName, "legacy defaults and per-game state/task isolation");
            string steam = Path.Combine(root, "two-games"), exe = Path.Combine(root, "steam-bridge.exe");
            File.WriteAllText(exe, "bridge fixture"); string native = Path.Combine(root, "native-game.exe"); File.WriteAllText(native, "real game fixture");
            var services = new List<InstallFiles>(); var states = new List<Installation>();
            ulong owner = 76561197960265729UL;
            string config = LaunchOptions.ConfigPath(steam, owner); Directory.CreateDirectory(Path.GetDirectoryName(config));
            string original = "// keep comments\r\n\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"apps\" { \"4732690\" { \"LaunchOptions\" \"-old \\\"中文\\\"\" } \"730\" { \"LaunchOptions\" \"-untouched\" } } } } } }";
            File.WriteAllText(config, original);
            foreach (var profile in GameProfile.All)
            {
                var metadata = new Metadata { Executable = profile.ClientFile, Build = "100", Depots = new Dictionary<string, string> { { (profile.AppId + 1).ToString(), "123" } } };
                var files = new InstallFiles(Path.Combine(root, profile.Id + "-data"), profile);
                string manifest = Path.Combine(steam, "steamapps", profile.ManifestName);
                Directory.CreateDirectory(Path.GetDirectoryName(manifest)); File.WriteAllText(manifest, "\"AppState\" { \"installdir\" \"Original " + profile.Id + "\" }");
                byte[] originalManifest = File.ReadAllBytes(manifest);
                var state = files.Prepare(steam, manifest, exe, metadata, owner, false, null, native);
                LaunchOptions.Prepare(state, owner); App.Save(files.StatePath, state); LaunchOptions.Apply(state); files.Commit(state);
                Check(Path.GetFileName(state.RuntimeExecutable) == "steam-bridge.exe" && File.ReadAllText(state.RuntimeExecutable) == "bridge fixture" && File.ReadAllText(Path.Combine(state.BridgeDirectory, state.SteamExecutable)) == "real game fixture", "bridge is separate from unchanged native starter for " + profile.Id);
                metadata.Build = "200"; metadata.Depots[(profile.AppId + 1).ToString()] = "456";
                files.Synchronize(state, exe, metadata, owner, native);
                var acf = KeyValues.Child(KeyValues.Parse(File.ReadAllText(manifest)), "AppState");
                Check(KeyValues.String(acf, "appid") == profile.AppId.ToString() && KeyValues.String(acf, "buildid") == "200" && File.ReadAllBytes(state.OriginalManifestBackup).SequenceEqual(originalManifest), "version sync targets correct app and preserves first backup " + profile.Id);
                services.Add(files); states.Add(state);
            }
            Check(File.ReadAllText(config).Contains("// keep comments") && File.ReadAllText(config).Contains("\"730\" { \"LaunchOptions\" \"-untouched\" }"), "launch option edits preserve unrelated text verbatim");
            services[0].Restore(states[0]); LaunchOptions.Restore(states[0]); services[0].FinishRestore();
            Check(File.Exists(services[1].StatePath) && File.Exists(states[1].ManifestPath) && File.ReadAllText(config).Contains("--bridge wuthering-waves"), "Endfield restore leaves Wuthering installation and launch options intact");
            services[1].Restore(states[1]); LaunchOptions.Restore(states[1]); services[1].FinishRestore();
            var options = KeyValues.Child(KeyValues.Child(KeyValues.Child(KeyValues.Child(KeyValues.Child(KeyValues.Parse(File.ReadAllText(config)), "UserLocalConfigStore"), "Software"), "Valve"), "Steam"), "apps");
            Check(KeyValues.String(KeyValues.Child(options, "4732690"), "LaunchOptions") == "-old \"中文\"" && KeyValues.String(KeyValues.Child(options, "3513350"), "LaunchOptions") == null, "restore recovers original options and removes previously absent ones");
            Throws(() => GameProfile.Wuthering.ValidateExecutable(@"..\launcher.exe"), "reject executable path traversal");
            string launcher = Path.Combine(root, "wu", "launcher.exe"), version = Path.Combine(root, "wu", "3.0.1.0");
            Directory.CreateDirectory(Path.Combine(version, "Assets")); File.WriteAllText(launcher, "fixture"); File.WriteAllText(Path.Combine(version, "launcher_main.dll"), "fixture");
            byte[] settings = Encoding.UTF8.GetBytes("{\"gameId\":\"G152\"}"); for (int i = 0; i < settings.Length; i++) settings[i] ^= 0x63;
            File.WriteAllText(Path.Combine(version, "Assets", "KRApp.conf"), Convert.ToBase64String(settings));
            Check(WutheringLauncher.ValidLauncher(launcher), "recognize CN Kuro launcher from public settings without loading DLLs");
            File.WriteAllText(Path.Combine(version, "Assets", "KRApp.conf"), "{\"gameId\":\"G153\"}");
            Check(!WutheringLauncher.ValidLauncher(launcher), "reject other Kuro distribution");
        }
        private static void QaqrTests(string root)
        {
            var legacy = App.Json.Deserialize<RuntimeConfig>("{\"GameId\":\"endfield\",\"Launcher\":\"original.exe\"}");
            Check(LaunchMethod.Normalize(legacy.LaunchMethod) == LaunchMethod.Official, "legacy config retains official launcher mode");
            string folder = Path.Combine(root, "QAQR 空 格"), source = Path.Combine(folder, QaqrLauncher.FileName), random = Path.Combine(folder, "r123456789abc.exe");
            string app = Path.Combine(folder, "resources", "app"), userData = Path.Combine(root, "existing-profile");
            Directory.CreateDirectory(Path.Combine(app, "out", "main")); Directory.CreateDirectory(userData);
            File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, source); File.Copy(source, random);
            File.WriteAllText(Path.Combine(app, "package.json"), "{\"name\":\"qaq-revival\",\"main\":\"./out/main/index.js\"}");
            File.WriteAllText(Path.Combine(app, "out", "main", "index.js"), "fixture");
            string configPath = Path.Combine(userData, "config.json");
            string endfield = Path.Combine(root, "official", "games", "Arknights Endfield", "Endfield.exe");
            string config = App.Json.Serialize(new { activeGameId = "wuthering-waves", randomLaunch = new { manager = true, xxmi = true }, games = new[] { new { id = "endfield", gamePath = endfield }, new { id = "wuthering-waves", gamePath = "D:\\fixture\\Wuthering Waves.exe" } } });
            File.WriteAllText(configPath, config);
            bool ready; Detection.ClientStatus(GameProfile.Endfield, null, endfield, "official", LaunchMethod.Qaqr, source, out ready);
            Check(ready && QaqrLauncher.FindGame(GameProfile.Endfield, source, userData) == endfield && File.ReadAllText(configPath) == config, "QAQR detects chosen game's real path without changing its profile or random settings");
            Check(QaqrLauncher.ResolveUserData(source, "ignored", userData) == userData && QaqrLauncher.ResolveUserData(source, userData, null) == userData && QaqrLauncher.ResolveUserData(source, null, null) == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QAQ-Revival"), "QAQR respects environment/saved profile and uses roaming default");
            var launch = QaqrLauncher.StartInfo(source, userData);
            Check(launch.FileName == source && launch.WorkingDirectory == folder && launch.Arguments == "" && !launch.UseShellExecute && !launch.EnvironmentVariables.ContainsKey("ELECTRON_RUN_AS_NODE") && !launch.EnvironmentVariables.ContainsKey("SteamAppId") && launch.EnvironmentVariables["QAQM_USER_DATA"] == userData, "QAQR launches stable entry with no game arguments or inherited Node/Steam app identity");
            var scan = new DetectionResult { GameId = "endfield", ChannelId = "official", LaunchMethod = LaunchMethod.Qaqr, QaqrPath = source, QaqrUserData = userData, Game = endfield };
            var saved = App.Json.Deserialize<RuntimeConfig>(App.Json.Serialize(InstallService.RuntimeSettings(scan, "fixture-task")));
            Check(saved.LaunchMethod == LaunchMethod.Qaqr && saved.QaqrPath == source && saved.QaqrUserData == userData && saved.Launcher == null && saved.Game == endfield, "QAQR configuration roundtrip preserves independent launcher/profile fields");
            string digest;
            using (var stream = File.OpenRead(random)) using (var sha = System.Security.Cryptography.SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            string marker = Path.Combine(folder, ".r123456789abc.exe.runtime.json");
            App.Save(marker, new { version = 2, source = QaqrLauncher.FileName, executable = Path.GetFileName(random), sha256 = digest, sourceSha256 = digest, activated = true });
            var processes = new QaqrProcesses(source);
            Check(processes.IsManager(random, Arguments.Quote(random)) && !processes.IsManager(random, Arguments.Quote(random) + " --type=renderer") && !processes.IsManager(Path.Combine(root, "elsewhere", Path.GetFileName(random)), ""), "random manager identity excludes renderer and another installation");
            App.Save(marker, new { version = 2, source = QaqrLauncher.FileName, executable = Path.GetFileName(random), sha256 = new string('0', 64), sourceSha256 = digest });
            Check(!processes.IsManager(random, ""), "random image digest mismatch is not a QAQR main process");
            App.Save(marker, new { version = 2, source = QaqrLauncher.FileName, executable = Path.GetFileName(random), sha256 = digest, sourceSha256 = digest, activated = false });
            using (var bootstrap = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(source, "--qaqr-fixture handoff " + Arguments.Quote(folder)) { UseShellExecute = false, CreateNoWindow = true }))
            {
                Check(bootstrap.WaitForExit(10000) && bootstrap.ExitCode == 0, "stable QAQR fixture hands off and exits");
                string pidPath = Path.Combine(folder, "pid.txt"); var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!File.Exists(pidPath) && DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(50);
                using (var child = System.Diagnostics.Process.GetProcessById(Int32.Parse(File.ReadAllText(pidPath))))
                {
                    try { Check(processes.Running(), "live random main process is tracked after initial process exits"); }
                    finally { if (!child.WaitForExit(8000) && Detection.Same(BridgeRuntime.ImagePath(child.Id), random)) { child.Kill(); child.WaitForExit(); } }
                }
            }
            DateTime now = DateTime.UtcNow; var session = new QaqrSession(now);
            Check(!session.Finished(now, true, false) && !session.Finished(now.AddSeconds(20), false, false) && !session.Finished(now.AddSeconds(21), true, true), "session survives random-name handoff until selected game starts");
            Check(!session.Finished(now.AddSeconds(22), false, true) && session.Finished(now.AddSeconds(25), true, false), "game can outlive manager and session ends even while shared QAQR remains open");
            var cancelled = new QaqrSession(now);
            Check(cancelled.Finished(now.AddSeconds(31), false, false), "failed/closed QAQR launch stops waiting after handoff grace");
        }
    }
}

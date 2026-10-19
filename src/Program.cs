using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Threading;
using System.Linq;

namespace EndfieldSteamBridge
{
    internal static class Arguments
    {
        public static string Quote(string s)
        {
            var b = new System.Text.StringBuilder("\""); int slashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { slashes++; continue; }
                b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); b.Append(c); slashes = 0;
            }
            return b.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Keep the Steam launch environment intact; only the setup UI/commands elevate.
            bool background = args.Length > 0 && new[] { "--bridge", "--assist", "--probe", "--diagnose", "--metadata" }.Contains(args[0]);
            var principal = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent());
            if (!background && !principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                try
                {
                    using (var elevated = Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
                        String.Join(" ", args.Select(Arguments.Quote))) { UseShellExecute = true, Verb = "runas" })) { }
                    return 0;
                }
                catch (System.ComponentModel.Win32Exception error)
                {
                    if (error.NativeErrorCode == 1223) return 0;
                    MessageBox.Show(error.Message, "steam-bridge", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1;
                }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length >= 2 && args.Length <= 4 && (args[0] == "--enable" || args[0] == "--restore" || args[0] == "--enable-all"))
            {
                try
                {
                    Action<string> progress = message => App.Save(args[1], new { success = false, message = message });
                    if (args[0] == "--enable") InstallService.Enable(Detection.Scan(null, null, args.Length >= 3 ? args[2] : null, args.Length == 4 ? args[3] : null), progress);
                    else if (args[0] == "--enable-all") InstallService.EnableMany(new[] { Detection.Scan(null, null, args.Length >= 3 ? args[2] : null), Detection.Scan(null, null, "official", GameProfile.Wuthering.Id) }, progress);
                    else InstallService.Restore(progress, GameProfile.Get(args.Length >= 3 ? args[2] : null));
                    App.Save(args[1], new { success = true, message = "completed" }); return 0;
                }
                catch (Exception e) { App.Save(args[1], new { success = false, message = e.ToString() }); return 1; }
            }
            try
            {
                if ((args.Length == 2 || args.Length == 3) && args[0] == "--probe") { App.Save(args[1], SteamAccount.Probe(Detection.FindSteam(), GameProfile.Get(args.Length == 3 ? args[2] : null))); return 0; }
                if (args.Length >= 2 && args.Length <= 4 && args[0] == "--diagnose") { App.Save(args[1], Detection.Scan(null, null, args.Length >= 3 ? args[2] : null, args.Length == 4 ? args[3] : null)); return 0; }
                if ((args.Length == 2 || args.Length == 3) && args[0] == "--metadata") { App.Save(args[1], KeyValues.ReadAppInfo(Path.Combine(Detection.FindSteam(), "appcache", "appinfo.vdf"), GameProfile.Get(args.Length == 3 ? args[2] : null))); return 0; }
                if ((args.Length == 1 || args.Length == 2) && args[0] == "--assist") return BridgeRuntime.RunAssist(args.Length == 2 ? args[1] : null);
                // The original Steam command after -- is deliberately ignored. Never start a copied game-named EXE.
                if (args.Length >= 2 && args[0] == "--bridge") return BridgeRuntime.Run(args[1]);
                bool created;
                using (var mutex = new Mutex(true, @"Local\EndfieldSteamBridgeManager", out created))
                {
                    if (!created) return 0;
                    using (var form = new MainForm())
                    {
                        if (args.Length >= 2 && args.Length <= 4 && args[0] == "--preview") { form.SavePreview(args[1], args.Length >= 3 ? args[2] : null, args.Length == 4 ? args[3] : null); return 0; }
                        Application.Run(form);
                    }
                }
                return 0;
            }
            catch (Exception e) { MessageBox.Show(e.Message, "steam-bridge", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
    }
}

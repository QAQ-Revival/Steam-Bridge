using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EndfieldSteamBridge
{
    // Fixture-only rendering: never scan or modify a real Steam installation.
    internal static class UiSmokeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr dc, IntPtr parameter);
        private static void Set(MainForm form, string name, object value) { typeof(MainForm).GetField(name, Private).SetValue(form, value); }
        private static object Get(MainForm form, string name) { return typeof(MainForm).GetField(name, Private).GetValue(form); }
        private static void Call(MainForm form, string name, params object[] args) { typeof(MainForm).GetMethod(name, Private).Invoke(form, args); }
        private static Control Find(MainForm form, string name) { return form.Controls.Find(name, true)[0]; }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            try
            {
                Render(output, GameProfile.Endfield, false, false);
                Render(output, GameProfile.Wuthering, true, false);
                Render(output, GameProfile.Wuthering, true, false, true);
                Render(output, GameProfile.Endfield, true, true);
                Console.WriteLine("PASS DPI-aware UI: layout, native repaint, clipping, launch-mode changes and minimum window size.");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }

        private static void Render(string output, GameProfile profile, bool useQaqr, bool missing, bool steamManaged = false)
        {
            using (var form = new MainForm())
            {
                Set(form, "previewRendering", true); Set(form, "selectedGame", profile);
                var method = (ComboBox)Find(form, "LaunchMethod"); method.SelectedIndex = useQaqr ? 1 : 0;
                var scan = new DetectionResult {
                    GameId = profile.Id, ChannelId = "official", LaunchMethod = useQaqr ? LaunchMethod.Qaqr : LaunchMethod.Official,
                    Account = new Ownership { Known = true, Owned = true, Name = "Steam 玩家" },
                    Launcher = missing ? null : @"D:\Games\" + profile.LauncherName + @"\launcher.exe",
                    Game = missing ? null : @"D:\Games\" + profile.Name + @"\" + profile.ClientFile,
                    QaqrPath = useQaqr ? @"D:\Apps\QAQR\QAQ-Revival.exe" : null,
                    QaqrSteamManaged = steamManaged,
                    ClientReady = !missing,
                    GameStatus = missing ? "尚未找到国服客户端，请选择本地文件。" : "已找到国服客户端 · 路径已就绪",
                    Error = missing ? "未找到国服客户端。请选择完整的游戏路径，然后重新检测。" : null,
                    Installation = missing ? null : new Installation { Executable = "steam-bridge.exe", Phase = "enabled", ChannelId = "official", LaunchMethod = useQaqr ? LaunchMethod.Qaqr : LaunchMethod.Official }
                };
                Set(form, "current", scan);
                var scans = (Dictionary<string, DetectionResult>)Get(form, "scans");
                foreach (var game in GameProfile.All) scans[game.Id] = scan;
                Call(form, "Populate"); Call(form, "SetBusy", false);
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.ShowInTaskbar = false;
                form.Show(); Application.DoEvents();
                using (var graphics = form.CreateGraphics())
                    Check(Math.Abs(form.LayoutScale - graphics.DpiX / 96f) < .01f, "Layout must scale once to the real process DPI.");
                // Detection completes after first display in the real application.
                Call(form, "Populate"); Application.DoEvents();

                Check(Find(form, "EnableCurrent").Enabled == !missing, "Current-game action must follow readiness.");
                Check(Find(form, "EnableBoth").Enabled == !missing, "Batch action must require both games to be ready.");
                Check(Find(form, "ServerChoice").Enabled == (profile == GameProfile.Endfield), "Wuthering server choice must stay locked.");
                Check(Find(form, "QaqrPath").Visible == useQaqr, "QAQR path must only appear in QAQR mode.");
                var managed = (CheckBox)Find(form, "QaqrSteamManaged");
                Check(managed.Visible == useQaqr && managed.Enabled == useQaqr && managed.Checked == steamManaged, "Steam management switch must reflect the saved selection and launch mode.");
                if (useQaqr)
                {
                    managed.Checked = !steamManaged;
                    Check(scan.QaqrSteamManaged == managed.Checked && Find(form, "OperationStatus").Text.Contains(managed.Checked ? "QAQR 未退出时会继续计时" : "Steam“停止”仅结束计时"), "Toggling Steam management must update pending settings and lifecycle guidance.");
                    CheckLayout(form); managed.Checked = steamManaged;
                }
                Check(((FluentButton)Find(form, "Game_" + profile.Id)).Selected, "Navigation must match the displayed game.");
                CheckLayout(form);
                CheckNativeButtonPaint(Find(form, "Game_wuthering-waves"));
                CheckTranslatedPaint(Find(form, "Game_endfield"));
                string stem = profile.Id + (useQaqr ? "-qaqr" : "-official") + (steamManaged ? "-steam-managed" : "") + (missing ? "-missing" : "");
                Save(form, Path.Combine(output, stem + ".png"));

                Call(form, "SetBusy", true);
                Check(!Find(form, "EnableCurrent").Enabled && !Find(form, "EnableBoth").Enabled && !method.Enabled && !managed.Enabled && !Find(form, "Game_endfield").Enabled && !Find(form, "Game_wuthering-waves").Enabled, "Busy state must prevent duplicate actions and settings changes.");
                method.SelectedIndex = useQaqr ? 0 : 1; Application.DoEvents(); CheckLayout(form);
                method.SelectedIndex = useQaqr ? 1 : 0; Application.DoEvents(); CheckLayout(form);
                Call(form, "SetBusy", false);
                form.Size = form.MinimumSize; Application.DoEvents(); CheckLayout(form);
                Save(form, Path.Combine(output, stem + "-compact.png"));
                Console.WriteLine("PASS " + profile.Id + " / " + (useQaqr ? "QAQR" : "official") + (missing ? " / missing client" : "") + " at native " + (form.LayoutScale * 100) + "% DPI");
            }
        }

        private static void CheckLayout(MainForm form)
        {
            var first = Find(form, "EnableCurrent"); var batch = Find(form, "EnableBoth"); var restore = Find(form, "RestoreCurrent");
            Check(first.Right <= batch.Left && batch.Right <= restore.Left, "Action buttons must not overlap.");
            foreach (var name in new[] { "EnableCurrent", "EnableBoth", "RestoreCurrent", "RefreshDetection" })
            {
                var control = Find(form, name);
                var origin = form.PointToClient(control.PointToScreen(Point.Empty));
                Check(form.ClientRectangle.Contains(new Rectangle(origin, control.Size)), name + " must remain visible when the window shrinks.");
            }
            var scroll = (Panel)Get(form, "scroll"); Check(!scroll.HorizontalScroll.Visible, "Settings must fit without horizontal scrolling.");
            foreach (var name in new[] { "LauncherPath", "QaqrPath", "GamePath" })
            {
                var path = Find(form, name);
                if (!path.Visible) continue;
                Check(path.Parent.Parent.Parent.ClientRectangle.Contains(path.Parent.Parent.Bounds), name + " row must fit its settings card.");
                foreach (Control child in path.Parent.Parent.Controls)
                    Check(child.Parent.ClientRectangle.Contains(child.Bounds), name + " label, field and browse button must fit their row.");
            }
            var status = Find(form, "OperationStatus");
            var managed = Find(form, "QaqrSteamManaged");
            if (managed.Visible)
                Check(managed.Parent.ClientRectangle.Contains(managed.Bounds) && managed.GetPreferredSize(Size.Empty).Width <= managed.Width, "Steam management switch must fit without clipping.");
            Check(status.Parent.ClientRectangle.Contains(status.Bounds), "Status text must fit its notice card.");
            var measured = TextRenderer.MeasureText(status.Text, status.Font, new Size(status.Width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Check(status.Height >= measured.Height, "Full status and QAQR guidance must fit without truncation.");
        }

        private static void Save(MainForm form, string path)
        {
            using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            { form.Controls[0].DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(path); }
        }

        private static void CheckNativeButtonPaint(Control button)
        {
            using (var bitmap = new Bitmap(button.Width, button.Height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                IntPtr dc = graphics.GetHdc();
                try { SendMessage(button.Handle, 0x000F, dc, IntPtr.Zero); }
                finally { graphics.ReleaseHdc(dc); }
                Check(bitmap.GetPixel(0, 0).GetBrightness() > .5f, "Native button repaint must paint its transparent corners, not leave a black buffer.");
            }
        }

        private static void CheckTranslatedPaint(Control button)
        {
            var bounds = new Rectangle(80, 40, button.Width, button.Height);
            using (var bitmap = new Bitmap(bounds.Right + 20, bounds.Bottom + 20))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Magenta);
                graphics.TranslateTransform(bounds.X, bounds.Y);
                graphics.SetClip(button.ClientRectangle);
                typeof(FluentButton).GetMethod("OnPaint", Private).Invoke(button, new object[] { new PaintEventArgs(graphics, button.ClientRectangle) });
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++)
                        if (!bounds.Contains(x, y)) Check(bitmap.GetPixel(x, y).ToArgb() == Color.Magenta.ToArgb(), "Translated control text must not paint onto adjacent controls.");
            }
        }
    }
}

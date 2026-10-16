using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EndfieldSteamBridge
{
    internal sealed class MainForm : Form
    {
        private Label account, client, installed, status, headline;
        private ComboBox server, launchMethod;
        private TextBox launcher, game, qaqr;
        private FluentButton enable, enableBoth, restore, refresh, chooseLauncher, chooseGame, chooseQaqr;
        private FluentBadge accountBadge, bridgeBadge;
        private FluentCard launchCard, notice;
        private Control qaqrRow;
        private Panel scroll;
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 500, ReshowDelay = 100 };
        private readonly Dictionary<string, FluentButton> navigation = new Dictionary<string, FluentButton>();
        private readonly Dictionary<string, DetectionResult> scans = new Dictionary<string, DetectionResult>();
        private GameProfile selectedGame = GameProfile.Endfield;
        private DetectionResult current;
        private GameProfile SelectedGame { get { return selectedGame; } }
        private string SelectedMethod { get { return launchMethod.SelectedIndex == 1 ? LaunchMethod.Qaqr : LaunchMethod.Official; } }
        private bool busy, previewRendering;
        private float layoutScale = 1f;
        internal float LayoutScale { get { return layoutScale; } }

        public MainForm()
        {
            SuspendLayout();
            DoubleBuffered = true; ResizeRedraw = true;
            Text = "steam-bridge";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = FluentTheme.Font(10);
            ForeColor = FluentTheme.Ink; BackColor = FluentTheme.Canvas;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1040, 740); MinimumSize = new Size(920, 640);
            var shell = new FluentPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 224));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(shell); shell.Controls.Add(BuildNavigation(), 0, 0);

            var body = new FluentPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 18, 24, 12), Margin = new Padding(0, 8, 8, 8), ColumnCount = 1, RowCount = 3 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            shell.Controls.Add(body, 1, 0);
            var header = new FluentPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 12) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            headline = MakeLabel("终末地", 22, FluentTheme.Ink); headline.Name = "GameHeading";
            refresh = MakeButton("重新检测", 122, false); refresh.Glyph = "\uE72C"; refresh.Name = "RefreshDetection";
            refresh.Dock = DockStyle.Fill; refresh.Margin = new Padding(0, 10, 0, 16);
            header.Controls.Add(headline, 0, 0);
            header.Controls.Add(MakeLabel("从 Steam 游戏库，启动你的国服客户端。", 9.5f, FluentTheme.Muted), 0, 1);
            header.Controls.Add(refresh, 1, 0); header.SetRowSpan(refresh, 2);
            body.Controls.Add(header, 0, 0);

            scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent, Margin = Padding.Empty, TabStop = false };
            var content = new FluentPanel { Name = "SettingsContent", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 4 };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            scroll.Controls.Add(content); body.Controls.Add(scroll, 0, 1);
            var steamCard = SummaryCard("Steam 账号", "\uE77B", out account, out accountBadge);
            steamCard.Name = "SteamAccountCard"; account.Text = "正在读取当前账号与游戏库…"; accountBadge.Text = "检测中";
            content.Controls.Add(steamCard, 0, 0);
            content.Controls.Add(BuildLaunchCard(), 0, 1);
            var stateCard = SummaryCard("接入状态", "\uE71B", out installed, out bridgeBadge);
            stateCard.Name = "BridgeStateCard"; installed.Text = "检测完成后，即可设置 Steam 接入。"; bridgeBadge.Text = "待检测";
            content.Controls.Add(stateCard, 0, 2);

            notice = new FluentCard { Name = "StatusNotice", Dock = DockStyle.Top, Height = 76, SurfaceColor = SystemInformation.HighContrast ? FluentTheme.Surface : Color.FromArgb(120, 235, 245, 253), Padding = new Padding(14, 12, 14, 12), ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 4) };
            notice.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28)); notice.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            notice.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            notice.Controls.Add(MakeIcon("\uE946", FluentTheme.Accent), 0, 0);
            status = MakeLabel("首次使用请先登录 Steam，并将游戏添加入库。", 9.5f, FluentTheme.Ink);
            status.Name = "OperationStatus"; status.AutoEllipsis = false;
            notice.Controls.Add(status, 1, 0); content.Controls.Add(notice, 0, 3);
            status.TextChanged += delegate { UpdateNoticeHeight(); tips.SetToolTip(status, status.Text); };
            scroll.ClientSizeChanged += delegate { UpdateNoticeHeight(); };

            var actions = new FluentPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Padding = new Padding(0, 14, 0, 8) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
            enable = MakeButton("设置当前游戏", 168, true); enable.Name = "EnableCurrent";
            enableBoth = MakeButton("一键设置 / 同步两款", 176, false); enableBoth.Name = "EnableBoth";
            restore = MakeButton("撤回当前游戏", 136, false); restore.Name = "RestoreCurrent";
            enable.Dock = enableBoth.Dock = restore.Dock = DockStyle.Fill;
            enable.Margin = enableBoth.Margin = new Padding(0, 0, 8, 0); restore.Margin = Padding.Empty;
            actions.Controls.Add(enable, 0, 0); actions.Controls.Add(enableBoth, 1, 0); actions.Controls.Add(restore, 3, 0);
            body.Controls.Add(actions, 0, 2);

            chooseLauncher.Click += delegate { Choose(true); }; chooseGame.Click += delegate { Choose(false); };
            chooseQaqr.Click += async delegate
            {
                using (var dialog = new OpenFileDialog { Filter = "QAQR 稳定入口|QAQ-Revival.exe", Title = "选择 QAQR 安装目录中的 QAQ-Revival.exe", CheckFileExists = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) { qaqr.Text = dialog.FileName; await RefreshDetection(); }
            };
            launchMethod.SelectedIndexChanged += async delegate
            {
                UpdateLaunchLayout();
                if (!busy) { game.Clear(); await RefreshDetection(); }
            };
            server.SelectedIndexChanged += async delegate
            {
                if (busy) return;
                launcher.Clear(); game.Clear(); await RefreshDetection();
            };
            enable.Click += async delegate { await Mutate(false); };
            restore.Click += async delegate { await Mutate(true); };
            refresh.Click += async delegate { await RefreshDetection(); };
            enableBoth.Click += async delegate { await Mutate(false, true); };
            tips.SetToolTip(enableBoth, "两款游戏均检测就绪后可用；统一设置，并重启一次 Steam。");
            Shown += async delegate { if (!previewRendering) await RefreshDetection(); };
            FormClosing += delegate(object s, FormClosingEventArgs e) { if (busy) { e.Cancel = true; status.Text = "正在处理，请等待当前操作完成。"; } };
            UpdateGameHeading(); UpdateLaunchLayout(); SetBusy(true);
            ResumeLayout(true);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); FluentTheme.ApplyWindow(this); }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            layoutScale *= factor.Height;
            base.ScaleControl(factor, specified);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (SystemInformation.HighContrast || ClientSize.Width == 0 || ClientSize.Height == 0) { base.OnPaintBackground(e); return; }
            // A wallpaper-independent backdrop keeps the same quiet tint on Windows 10 and 11.
            using (var horizontal = new LinearGradientBrush(ClientRectangle, Color.FromArgb(233, 245, 250), Color.FromArgb(251, 242, 238), 0f))
                e.Graphics.FillRectangle(horizontal, ClientRectangle);
            using (var vertical = new LinearGradientBrush(ClientRectangle, Color.FromArgb(0, 246, 239, 252), Color.FromArgb(205, 246, 239, 252), 90f))
                e.Graphics.FillRectangle(vertical, ClientRectangle);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }

        private Control BuildNavigation()
        {
            var sidebar = new FluentPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(12, 24, 12, 14) };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 78, 32, 70, 70 }) sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 38, 38, 38, 34 }) sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            var brand = new FluentPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 0, 18), ColumnCount = 1, RowCount = 2 };
            brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); brand.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var name = MakeLabel("steam-bridge", 16, FluentTheme.Ink); name.Font = new Font("Segoe UI Semibold", 16);
            brand.Controls.Add(name, 0, 0); brand.Controls.Add(MakeLabel("国服游戏 · Steam 接入", 9, FluentTheme.Muted), 0, 1);
            sidebar.Controls.Add(brand, 0, 0);
            var section = MakeLabel("游戏库", 9, FluentTheme.Muted); section.Padding = new Padding(12, 0, 0, 0); sidebar.Controls.Add(section, 0, 1);
            for (int i = 0; i < GameProfile.All.Length; i++)
            {
                var profile = GameProfile.All[i];
                var button = new FluentButton { Name = "Game_" + profile.Id, Text = profile.Name, Subtitle = profile.SteamName, Glyph = "\uE7FC", Navigation = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4), AccessibleName = "切换到" + profile.Name };
                button.Click += async delegate { await SelectGame(profile); };
                navigation.Add(profile.Id, button); sidebar.Controls.Add(button, 0, i + 2);
            }
            var library = NavigationLink("Steam 游戏库", "\uE8F1");
            library.Click += delegate { Process.Start("steam://nav/games/details/" + SelectedGame.AppId); };
            var logs = NavigationLink("本地记录与备份", "\uE8B7");
            logs.Click += delegate { Directory.CreateDirectory(SelectedGame.Home); Process.Start("explorer.exe", Arguments.Quote(SelectedGame.Home)); };
            var project = NavigationLink("GitHub 开源项目", "\uE774");
            project.Click += delegate { Process.Start(new ProcessStartInfo("https://github.com/QAQ-Revival/Steam-Bridge") { UseShellExecute = true }); };
            sidebar.Controls.Add(library, 0, 5); sidebar.Controls.Add(logs, 0, 6); sidebar.Controls.Add(project, 0, 7);
            var version = MakeLabel("v" + App.Version + "  ·  Windows", 8.5f, FluentTheme.Muted); version.Padding = new Padding(14, 8, 0, 0); sidebar.Controls.Add(version, 0, 8);
            return sidebar;
        }

        private FluentButton NavigationLink(string text, string glyph)
        { return new FluentButton { Text = text, Glyph = glyph, Quiet = true, Dock = DockStyle.Fill, Margin = Padding.Empty, Font = FluentTheme.Font(9) }; }

        private FluentCard SummaryCard(string title, string glyph, out Label detail, out FluentBadge badge)
        {
            var card = new FluentCard { Dock = DockStyle.Top, Height = 82, Padding = new Padding(16, 12, 16, 12), ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 0, 0, 6) };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38)); card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var icon = MakeIcon(glyph, FluentTheme.Ink); card.Controls.Add(icon, 0, 0); card.SetRowSpan(icon, 2);
            card.Controls.Add(MakeLabel(title, 10.5f, FluentTheme.Ink), 1, 0);
            detail = MakeLabel("", 9, FluentTheme.Muted); card.Controls.Add(detail, 1, 1);
            detail.TextChanged += delegate(object sender, EventArgs e) { var label = (Label)sender; tips.SetToolTip(label, label.Text); };
            badge = new FluentBadge { Width = 76, Height = 26, Anchor = AnchorStyles.Right, Font = FluentTheme.Font(8.5f), Margin = Padding.Empty };
            card.Controls.Add(badge, 2, 0); card.SetRowSpan(badge, 2);
            return card;
        }

        private Control BuildLaunchCard()
        {
            launchCard = new FluentCard { Name = "LaunchSettingsCard", Dock = DockStyle.Top, Height = 234, Padding = new Padding(16, 12, 16, 12), ColumnCount = 1, RowCount = 6, Margin = new Padding(0, 0, 0, 6) };
            launchCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 32, 70, 28, 40, 0, 40 }) launchCard.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            launchCard.Controls.Add(MakeLabel("启动设置", 11, FluentTheme.Ink), 0, 0);
            var options = new FluentPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 8) };
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
            options.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            options.Controls.Add(MakeLabel("服务器", 9, FluentTheme.Muted), 0, 0);
            options.Controls.Add(MakeLabel("启动方式", 9, FluentTheme.Muted), 1, 0);
            server = new FluentComboBox { Name = "ServerChoice", Dock = DockStyle.Top, Margin = new Padding(0, 0, 16, 0), AccessibleName = "服务器" };
            server.Items.AddRange(GameChannel.All); server.SelectedItem = GameChannel.Official;
            launchMethod = new FluentComboBox { Name = "LaunchMethod", Dock = DockStyle.Top, Margin = Padding.Empty, AccessibleName = "启动方式" };
            launchMethod.Items.AddRange(new object[] { "官方启动器", "QAQR（Mod 管理器）" }); launchMethod.SelectedIndex = 0;
            if (File.Exists(App.ConfigPath))
            {
                try { var config = App.Load<RuntimeConfig>(App.ConfigPath); server.SelectedItem = GameChannel.Get(config.ChannelId); launchMethod.SelectedIndex = LaunchMethod.Normalize(config.LaunchMethod) == LaunchMethod.Qaqr ? 1 : 0; }
                catch (Exception) { /* Normal detection reports an unreadable existing config. */ }
            }
            options.Controls.Add(server, 0, 1); options.Controls.Add(launchMethod, 1, 1); launchCard.Controls.Add(options, 0, 1);
            client = MakeLabel("正在查找本地启动器与国服客户端…", 9, FluentTheme.Muted); launchCard.Controls.Add(client, 0, 2);
            client.TextChanged += delegate { tips.SetToolTip(client, client.Text); };
            launchCard.Controls.Add(PathRow("官方启动器", "LauncherPath", out launcher, out chooseLauncher), 0, 3);
            qaqrRow = PathRow("QAQR", "QaqrPath", out qaqr, out chooseQaqr); launchCard.Controls.Add(qaqrRow, 0, 4);
            launchCard.Controls.Add(PathRow("游戏客户端", "GamePath", out game, out chooseGame), 0, 5);
            return launchCard;
        }

        private Label MakeLabel(string text, float size, Color color)
        { return new Label { Text = text, Font = FluentTheme.Font(size), ForeColor = color, BackColor = Color.Transparent, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty, UseMnemonic = false }; }
        private Label MakeIcon(string glyph, Color color)
        { var label = MakeLabel(glyph, 16, color); label.Font = new Font(FluentTheme.IconFamily, 16); return label; }
        private FluentButton MakeButton(string text, int width, bool primary)
        { return new FluentButton { Text = text, Width = width, Height = 36, Primary = primary, Margin = Padding.Empty, Font = FluentTheme.Font(9.5f) }; }

        private Control PathRow(string title, string name, out TextBox text, out FluentButton browse)
        {
            var row = new FluentPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 2, 0, 4) };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            row.Controls.Add(MakeLabel(title, 9, FluentTheme.Muted), 0, 0);
            var field = new FluentCard { Dock = DockStyle.Fill, CornerRadius = 4, InputSurface = true, SurfaceColor = FluentTheme.Surface, Padding = new Padding(8, 6, 8, 4), Margin = new Padding(0, 0, 8, 0), ColumnCount = 1, RowCount = 1 };
            field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            text = new FluentPathBox { Name = name, AccessibleName = title + "路径", Dock = DockStyle.Fill, ReadOnly = true, BackColor = FluentTheme.Surface, ForeColor = FluentTheme.Ink, BorderStyle = BorderStyle.None, Margin = Padding.Empty, Font = new Font("Segoe UI", 9.5f) };
            text.Enter += delegate { field.Invalidate(); }; text.Leave += delegate { field.Invalidate(); };
            tips.SetToolTip(text, "未检测到路径，请选择本地文件。");
            text.TextChanged += delegate(object sender, EventArgs e) { var box = (TextBox)sender; tips.SetToolTip(box, String.IsNullOrEmpty(box.Text) ? "未检测到路径，请选择本地文件。" : box.Text); };
            field.Controls.Add(text, 0, 0);
            browse = MakeButton("浏览…", 78, false); browse.AccessibleName = "选择" + title; browse.Dock = DockStyle.Fill;
            row.Controls.Add(field, 1, 0); row.Controls.Add(browse, 2, 0); return row;
        }

        private void UpdateNoticeHeight()
        {
            if (notice == null || status == null || scroll == null) return;
            int width = Math.Max(100, scroll.ClientSize.Width - FluentTheme.Px(this, 62));
            var measured = TextRenderer.MeasureText(status.Text, status.Font, new Size(width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int height = Math.Max(FluentTheme.Px(this, 66), measured.Height + FluentTheme.Px(this, 28));
            if (notice.Height != height) notice.Height = height;
        }

        private void UpdateLaunchLayout()
        {
            bool useQaqr = SelectedMethod == LaunchMethod.Qaqr;
            launchCard.SuspendLayout();
            qaqrRow.Visible = useQaqr; launchCard.RowStyles[4].Height = FluentTheme.Px(this, useQaqr ? 40 : 0);
            launchCard.Height = FluentTheme.Px(this, useQaqr ? 274 : 234);
            launchCard.ResumeLayout(true);
        }

        private void UpdateGameHeading()
        {
            headline.Text = SelectedGame == GameProfile.Endfield ? "明日方舟：终末地" : SelectedGame.Name;
            foreach (var pair in navigation) pair.Value.Selected = pair.Key == SelectedGame.Id;
        }

        private async Task SelectGame(GameProfile profile)
        {
            if (busy || SelectedGame == profile) return;
            SetBusy(true); selectedGame = profile; UpdateGameHeading();
            DetectionResult saved; scans.TryGetValue(profile.Id, out saved);
            server.SelectedItem = GameChannel.Get(saved == null ? null : saved.ChannelId);
            launchMethod.SelectedIndex = saved != null && LaunchMethod.Normalize(saved.LaunchMethod) == LaunchMethod.Qaqr ? 1 : 0;
            qaqr.Text = saved == null ? "" : saved.QaqrPath;
            launcher.Text = saved == null ? "" : saved.Launcher; game.Text = saved == null ? "" : saved.Game;
            scroll.AutoScrollPosition = Point.Empty;
            await RefreshDetection();
        }

        private async void Choose(bool isLauncher)
        {
            using (var dialog = new OpenFileDialog { Filter = isLauncher ? "国服启动器 (launcher.exe)|launcher.exe" : SelectedGame == GameProfile.Endfield ? "终末地客户端|Endfield.exe" : "鸣潮客户端|Wuthering Waves.exe;Client-Win64-Shipping.exe", Title = isLauncher ? "选择" + SelectedGame.LauncherName + "根目录中的 launcher.exe" : "选择国服游戏目录中的 " + SelectedGame.ClientFile, CheckFileExists = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) { if (isLauncher) launcher.Text = dialog.FileName; else game.Text = dialog.FileName; await RefreshDetection(); }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            refresh.Enabled = chooseLauncher.Enabled = chooseGame.Enabled = launchMethod.Enabled = !value;
            foreach (var button in navigation.Values) button.Enabled = !value;
            chooseQaqr.Enabled = qaqr.Enabled = !value && SelectedMethod == LaunchMethod.Qaqr;
            server.Enabled = !value && SelectedGame == GameProfile.Endfield;
            bool stateExists = File.Exists(SelectedGame.StatePath);
            enable.Enabled = !value && Ready(current);
            enableBoth.Enabled = !value && GameProfile.All.All(p => scans.ContainsKey(p.Id) && Ready(scans[p.Id]));
            restore.Enabled = !value && stateExists;
            refresh.Text = value ? "请稍候…" : "重新检测";
        }

        private async Task RefreshDetection()
        {
            SetBusy(true); status.Text = "正在读取当前 Steam 账号与国服安装位置…";
            string l = String.IsNullOrWhiteSpace(launcher.Text) ? null : launcher.Text, g = String.IsNullOrWhiteSpace(game.Text) ? null : game.Text;
            string channelId = ((GameChannel)server.SelectedItem).Id;
            var profile = SelectedGame;
            string method = SelectedMethod, q = String.IsNullOrWhiteSpace(qaqr.Text) ? null : qaqr.Text;
            DetectionResult previous; scans.TryGetValue(profile.Id, out previous);
            try
            {
                current = await Task.Run(() => Detection.Scan(l, g, channelId, profile.Id, method, q, previous == null ? null : previous.QaqrUserData)); scans[profile.Id] = current;
                foreach (var other in GameProfile.All.Where(p => p != profile))
                {
                    DetectionResult saved; scans.TryGetValue(other.Id, out saved);
                    scans[other.Id] = await Task.Run(() => Detection.Scan(saved == null ? null : saved.Launcher, saved == null ? null : saved.Game, saved == null ? null : saved.ChannelId, other.Id, saved == null ? null : saved.LaunchMethod, saved == null ? null : saved.QaqrPath, saved == null ? null : saved.QaqrUserData));
                }
                Populate();
            }
            finally { SetBusy(false); }
        }

        private void Populate()
        {
            var a = current.Account;
            var profile = SelectedGame;
            UpdateGameHeading();
            bool owned = a != null && a.Known && a.Owned;
            account.Text = a == null || !a.Known ? (a == null ? "Steam 未就绪" : a.Error) : (a.Name ?? a.SteamId.ToString()) + "  ·  " + (a.Owned ? "已拥有 " : "尚未拥有 ") + profile.Name;
            accountBadge.Text = owned ? "已入库" : "待检查"; accountBadge.Success = owned;
            launcher.Text = current.Launcher ?? launcher.Text; game.Text = current.Game ?? game.Text;
            qaqr.Text = current.QaqrPath ?? qaqr.Text;
            client.Text = current.GameStatus ?? "等待 Steam 检测完成";
            client.ForeColor = current.ClientReady ? FluentTheme.Success : FluentTheme.Muted;
            var state = current.Installation;
            bool active = state != null && state.Executable == "steam-bridge.exe" && state.Phase == "enabled";
            installed.Text = state == null ? "设置后，即可从 Steam 启动国服并记录游戏时长。" : state.Executable != "steam-bridge.exe" ? "检测到旧版桥接，请先撤回，再用新版设置。" : active ? GameChannel.Get(state.ChannelId).Name + " · " + LaunchMethod.Name(state.LaunchMethod) + " · 可同步 Steam 版本" : "上次操作尚未完成，请撤回当前游戏。";
            bridgeBadge.Text = active ? "已接入" : state == null ? "未设置" : "待处理"; bridgeBadge.Success = active;
            enable.Text = state == null ? "设置当前游戏" : "应用设置 / 同步版本";
            status.Text = current.Error ?? (a == null || !a.Known ? "请确认 Steam 已在线登录，再点击“重新检测”。" : !a.Owned ? "请先在 Steam 商店将 " + profile.SteamName + " 免费添加入库，再重新检测。" : "设置、同步或撤回会退出并重启 Steam，请先关闭游戏与启动器。\nSteam 需以管理员身份运行；两款均就绪后，可一键设置。");
            if (SelectedMethod == LaunchMethod.Qaqr)
                status.Text = "QAQR：请手动选择" + profile.Name + "并开始游戏。此模式仅记录时长，不支持 Steam 原生叠加层。\nSteam“停止”仅结束计时，请手动退出游戏。\n" + (current.Error ?? (owned ? "应用设置后才会更改启动方式，并重启 Steam。" : "请确认 Steam 已登录并拥有此游戏。"));
            UpdateLaunchLayout(); UpdateNoticeHeight();
        }

        private static bool Ready(DetectionResult scan) { return scan != null && scan.Error == null && scan.Account != null && scan.Account.Known && scan.Account.Owned && scan.ClientReady; }
        private async Task Mutate(bool uninstall, bool both = false)
        {
            SetBusy(true);
            Action<string> report = message => { if (!IsDisposed) BeginInvoke((Action)(() => status.Text = message)); };
            string outcome = null;
            try
            {
                var profile = SelectedGame;
                if (uninstall) await Task.Run(() => InstallService.Restore(report, profile));
                else if (both) await Task.Run(() => InstallService.EnableMany(GameProfile.All.Select(p => scans[p.Id]).ToArray(), report));
                else await Task.Run(() => InstallService.Enable(current, report));
                outcome = uninstall ? "已撤回当前游戏，恢复 Steam 安装记录与启动选项。" : (both ? "两款游戏" : profile.Name) + "已设置 / 同步。从 Steam 打开对应启动器后，手动点击开始游戏。";
            }
            catch (Exception e) { outcome = e.Message; MessageBox.Show(this, e.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            finally
            {
                if (current == null) current = new DetectionResult();
                foreach (var pair in scans)
                    try { var p = GameProfile.Get(pair.Key); pair.Value.Installation = File.Exists(p.StatePath) ? App.Load<Installation>(p.StatePath) : null; }
                    catch (Exception e) { pair.Value.Error = e.Message; }
                Populate(); status.Text = outcome; SetBusy(false);
            }
        }

        public void SavePreview(string path, string channelId = null, string gameId = null, string method = null)
        {
            previewRendering = true; SetBusy(true); selectedGame = GameProfile.Get(gameId); server.SelectedItem = GameChannel.Get(channelId); launchMethod.SelectedIndex = LaunchMethod.Normalize(method) == LaunchMethod.Qaqr ? 1 : 0;
            CreateControl(); current = Detection.Scan(null, null, ((GameChannel)server.SelectedItem).Id, SelectedGame.Id, SelectedMethod); scans[SelectedGame.Id] = current; Populate(); SetBusy(false);
            Show(); ActiveControl = refresh; launcher.SelectionLength = 0; Application.DoEvents();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path); }
            Hide();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WorkBuddyDaily {
    public static class Program {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [STAThread] public static void Main(string[] args) {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
            if (args.Length == 2 && args[0] == "--self-test") { Tests.Run(args[1]); return; }
            bool created;
            using (var mutex = new Mutex(true, "Local\\WorkBuddyDailyDesktop", out created)) {
                if (!created) { Forms.MessageBox.Show("签到助手已在运行，请查看托盘图标。", "每日签到"); return; }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { e.Handled = true; Forms.MessageBox.Show("操作未完成，请查看设置后重试。", "每日签到"); };
                var window = new MainWindow(false, null);
                if (args.Contains("--associate")) window.ContentRendered += delegate { window.BeginLink(); };
                app.Run(window);
            }
        }
    }
    public sealed class MainWindow : Window {
        readonly Store store;
        Preferences prefs;
        Account account;
        readonly Api api = new Api();
        readonly TextBox path = new TextBox(), time = new TextBox();
        readonly CheckBox enabled = new CheckBox(), startup = new CheckBox();
        readonly TextBlock result = new TextBlock(), detail = new TextBlock(), accountText = new TextBlock(), next = new TextBlock();
        readonly TextBlock log = new TextBlock();
        readonly DispatcherTimer timer = new DispatcherTimer();
        readonly List<Button> actions = new List<Button>();
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        CancellationTokenSource loginCancellation;
        Forms.NotifyIcon tray;
        System.Drawing.Icon trayIcon;
        bool exiting, busy, testing;
        int retries;
        string retryDay = "";
        DateTime retryAt = DateTime.MinValue;
        static readonly Brush Ink = Color("#182539"), Muted = Color("#66758B"), Accent = Color("#2369E8");
        static Brush Color(string value) { return new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(value)); }
        static TextBlock Text(string value, double size, Brush brush) { return new TextBlock { Text = value, FontSize = size, Foreground = brush, TextWrapping = TextWrapping.Wrap }; }
        static Border Card(UIElement child) { return new Border { Background = Brushes.White, BorderBrush = Color("#E1E7F0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 12), Child = child }; }
        static void Box(TextBox box) { box.Height = 36; box.Padding = new Thickness(10, 7, 10, 6); box.BorderBrush = Color("#D3DCE9"); box.Background = Color("#FCFDFF"); box.VerticalContentAlignment = VerticalAlignment.Center; }
        Button Button(string title, Action action, bool primary = false) {
            var button = new Button { Content = title, Height = 38, MinWidth = title == "选择" ? 64 : 108, Padding = new Thickness(15, 0, 15, 0), Margin = new Thickness(0, 0, 8, 0), Background = primary ? Accent : Brushes.White, Foreground = primary ? Brushes.White : Ink, BorderBrush = primary ? Accent : Color("#CCD7E7"), Cursor = System.Windows.Input.Cursors.Hand };
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty)); border.SetValue(Border.BorderThicknessProperty, new Thickness(1)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content); button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            button.Click += delegate { action(); }; actions.Add(button); return button;
        }
        public MainWindow(bool test, string directory) {
            testing = test;
            store = new Store(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBuddyDaily"));
            string loadProblem = "";
            try { prefs = store.Load(); Schedule.Parse(prefs.Time); } catch { prefs = new Preferences(); loadProblem = "设置无法读取，已停止自动执行"; }
            try { account = store.LoadAccount(); } catch { account = null; loadProblem = "账号无法解密，请重新关联"; }
            Title = "每日签到 · WorkBuddy"; Width = 600; Height = 742; MinWidth = 560; MinHeight = 700;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Color("#F3F6FB"); Foreground = Ink; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            string iconFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "daily.ico");
            if (File.Exists(iconFile)) Icon = BitmapFrame.Create(new Uri(iconFile));
            var scroll = new ScrollViewer { Background = Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var root = new StackPanel { Margin = new Thickness(24, 22, 24, 18) }; scroll.Content = root; Content = scroll;
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
            var badge = new Border { Width = 46, Height = 46, Background = Accent, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 13, 0) };
            badge.Child = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 13,13 L 33,13 L 33,33 L 13,33 Z M 18,9 L 18,17 M 28,9 L 28,17 M 13,20 L 33,20 M 18,26 L 22,30 L 29,23"), Stroke = Brushes.White, StrokeThickness = 2, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(badge, Dock.Left); head.Children.Add(badge);
            var titles = new StackPanel(); titles.Children.Add(Text("每日签到", 25, Ink)); titles.Children.Add(Text("WorkBuddy · 本地定时助手", 12, Muted)); head.Children.Add(titles); root.Children.Add(head);
            var state = new StackPanel();
            state.Children.Add(Text("今日状态", 12, Muted)); result.Text = "等待签到"; result.FontSize = 22; result.FontWeight = FontWeights.SemiBold; result.Margin = new Thickness(0, 6, 0, 5); state.Children.Add(result);
            detail.Text = "关联账号后即可执行每日签到"; detail.Foreground = Muted; detail.TextWrapping = TextWrapping.Wrap; state.Children.Add(detail);
            next.Foreground = Accent; next.Margin = new Thickness(0, 12, 0, 0); state.Children.Add(next); root.Children.Add(Card(state));
            var accountRow = new DockPanel();
            var connect = Button("关联账号", delegate { Login(); }); DockPanel.SetDock(connect, Dock.Right); accountRow.Children.Add(connect);
            var accountBlock = new StackPanel(); accountBlock.Children.Add(Text("签到账号", 12, Muted)); accountText.Text = account == null ? "尚未关联" : DisplayName(); accountText.FontSize = 15; accountText.Margin = new Thickness(0, 5, 0, 0); accountText.TextTrimming = TextTrimming.CharacterEllipsis; accountBlock.Children.Add(accountText); accountRow.Children.Add(accountBlock); root.Children.Add(Card(accountRow));
            var config = new StackPanel(); config.Children.Add(Text("执行设置", 15, Ink));
            var clock = new DockPanel { Margin = new Thickness(0, 13, 0, 13) }; time.Text = prefs.Time; time.Width = 88; Box(time); DockPanel.SetDock(time, Dock.Right); clock.Children.Add(time); clock.Children.Add(Text("每日时间（北京时间）", 13, Muted)); config.Children.Add(clock);
            config.Children.Add(Text("WorkBuddy 程序", 12, Muted));
            var file = new DockPanel { Margin = new Thickness(0, 6, 0, 14) };
            var browse = Button("选择", ChooseApplication); browse.Margin = new Thickness(8, 0, 0, 0); browse.Height = 36; DockPanel.SetDock(browse, Dock.Right); file.Children.Add(browse);
            Box(path); path.Text = testing ? "" : prefs.ApplicationPath == "" ? FindWorkBuddy() : prefs.ApplicationPath; file.Children.Add(path); config.Children.Add(file);
            var switches = new StackPanel { Orientation = Orientation.Horizontal };
            enabled.Content = "每日自动签到"; enabled.IsChecked = prefs.Enabled; enabled.Margin = new Thickness(0, 0, 26, 0); switches.Children.Add(enabled);
            startup.Content = "开机自动启动"; startup.IsChecked = prefs.Startup; switches.Children.Add(startup); config.Children.Add(switches); root.Children.Add(Card(config));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 17) };
            buttons.Children.Add(Button("立即签到", delegate { Execute(false); }, true)); buttons.Children.Add(Button("保存设置", Save)); buttons.Children.Add(Button("打开 WorkBuddy", delegate { try { Launch(path.Text); } catch { SetResult("未能打开", "请检查 WorkBuddy 程序路径", true); } })); root.Children.Add(buttons);
            root.Children.Add(Text("最近运行", 12, Muted)); log.Margin = new Thickness(0, 6, 0, 0); log.FontSize = 11; log.Foreground = Muted; log.TextWrapping = TextWrapping.Wrap; root.Children.Add(log);
            timer.Tick += delegate { timer.Stop(); Execute(true); };
            if (!testing) {
                MakeTray(); SystemEvents.PowerModeChanged += PowerChanged;
                Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!exiting) { e.Cancel = true; Hide(); } };
                if (prefs.Enabled) ContentRendered += delegate { Hide(); };
            }
            Closed += delegate { lifetime.Cancel(); if (loginCancellation != null) loginCancellation.Cancel(); timer.Stop(); if (!testing) SystemEvents.PowerModeChanged -= PowerChanged; if (tray != null) tray.Dispose(); if (trayIcon != null) trayIcon.Dispose(); api.Dispose(); };
            UpdateNext();
            if (loadProblem != "") SetResult("需要检查", loadProblem, true);
            else if (Schedule.Done(prefs, account, DateTime.UtcNow)) SetResult("今日已完成", "下次将在明天按时执行", false);
        }
        string DisplayName() { return account.Nickname == "" ? "已关联国内账号" : account.Nickname; }
        static string StartupFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "WorkBuddy 每日签到.lnk"); } }
        static string FindWorkBuddy() {
            foreach (RegistryKey parent in new[] { Registry.CurrentUser, Registry.LocalMachine }) {
                try { using (RegistryKey list = parent.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall")) {
                    if (list == null) continue;
                    foreach (string sub in list.GetSubKeyNames()) using (RegistryKey key = list.OpenSubKey(sub)) {
                        if (key == null || !Convert.ToString(key.GetValue("DisplayName")).StartsWith("WorkBuddy", StringComparison.OrdinalIgnoreCase)) continue;
                        string candidate = Convert.ToString(key.GetValue("DisplayIcon")).Trim('"');
                        int end = candidate.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (end >= 0) candidate = candidate.Substring(0, end + 4).Trim('"');
                        if (File.Exists(candidate)) return candidate;
                    }
                } } catch { }
            }
            return "";
        }
        void ChooseApplication() { var dialog = new OpenFileDialog { Filter = "WorkBuddy 程序|WorkBuddy.exe", Title = "选择 WorkBuddy.exe", CheckFileExists = true }; if (dialog.ShowDialog(this) == true) path.Text = dialog.FileName; }
        static void ValidatePath(string file) { if (!File.Exists(file) || !String.Equals(Path.GetFileName(file), "WorkBuddy.exe", StringComparison.OrdinalIgnoreCase)) throw new Exception("请选择已安装的 WorkBuddy.exe。"); }
        static void Launch(string file) {
            ValidatePath(file); string full = Path.GetFullPath(file);
            foreach (Process process in Process.GetProcessesByName("WorkBuddy")) {
                using (process) { try { if (String.Equals(process.MainModule.FileName, full, StringComparison.OrdinalIgnoreCase)) return; } catch { } }
            }
            Process.Start(new ProcessStartInfo(full) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(full) });
        }
        void SetStartup(bool value) {
            if (!value) { if (File.Exists(StartupFile)) File.Delete(StartupFile); return; }
            Type shellType = Type.GetTypeFromProgID("WScript.Shell"); object shell = Activator.CreateInstance(shellType), shortcut = null;
            try {
                shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { StartupFile }); Type type = shortcut.GetType();
                type.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Process.GetCurrentProcess().MainModule.FileName });
                type.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { AppDomain.CurrentDomain.BaseDirectory });
                type.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            } finally { if (shortcut != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        void Save() {
            try {
                Schedule.Parse(time.Text.Trim()); ValidatePath(path.Text.Trim());
                if (enabled.IsChecked == true && account == null) throw new Exception("请先关联签到账号。");
                SetStartup(startup.IsChecked == true);
                prefs.Time = time.Text.Trim(); prefs.ApplicationPath = Path.GetFullPath(path.Text.Trim()); prefs.Enabled = enabled.IsChecked == true; prefs.Startup = startup.IsChecked == true;
                store.Save(prefs); retryAt = DateTime.MinValue; retries = 0;
                SetResult("设置已保存", prefs.Enabled ? "后台等待每日执行；关闭窗口会保留托盘" : "自动签到已关闭，可手动执行", false); UpdateNext();
            } catch (Exception ex) { SetResult("设置未保存", ex.Message, true); }
        }
        void SetBusy(bool value) { busy = value; foreach (var button in actions) button.IsEnabled = !value; time.IsEnabled = path.IsEnabled = enabled.IsEnabled = startup.IsEnabled = !value; }
        void SetResult(string title, string message, bool error) {
            result.Text = title; result.Foreground = error ? Color("#A65E12") : Ink; detail.Text = message;
            if (tray != null) tray.Text = ("WorkBuddy · " + title).Substring(0, Math.Min(63, ("WorkBuddy · " + title).Length));
        }
        void Record(string message) { store.Log(message); string file = Path.Combine(store.DirectoryPath, "activity.log"); log.Text = String.Join(Environment.NewLine, File.ReadAllLines(file).Reverse().Take(3).Reverse()); }
        void UpdateNext() {
            timer.Stop();
            if (!prefs.Enabled || account == null) { next.Text = "自动执行已关闭"; return; }
            DateTime now = DateTime.UtcNow, due = Schedule.DueUtc(prefs, account, now);
            if (retryAt > due) due = retryAt;
            next.Text = "下次执行  " + Schedule.Now(due).ToString("MM-dd HH:mm") + "  北京时间";
            timer.Interval = TimeSpan.FromMilliseconds(Math.Max(2000, Math.Min(43200000, (due - now).TotalMilliseconds)));
            if (!testing) timer.Start();
        }
        void PowerChanged(object sender, PowerModeChangedEventArgs e) {
            if (e.Mode != PowerModes.Resume) return;
            Dispatcher.BeginInvoke(new Action(delegate { if (!busy && prefs.Enabled) { retryAt = DateTime.UtcNow.AddSeconds(20); UpdateNext(); } }));
        }
        async void Login() {
            if (busy) return;
            SetBusy(true); timer.Stop(); loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); loginCancellation.CancelAfter(TimeSpan.FromMinutes(5));
            var ct = loginCancellation.Token;
            try {
                SetResult("准备关联", "正在连接腾讯登录服务", false);
                var session = await api.Call(Api.AuthBase + "/v2/plugin/auth/state?platform=CLI", "POST", new Dictionary<string, string> { { "X-Domain", "copilot.tencent.com" } }, ct);
                string state = Api.Text(session, "state"), url = Api.Text(session, "authUrl");
                if (state == "" || state.Length > 4096 || !Api.TrustedLogin(url)) throw new Exception("未取得可信的登录入口。");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                SetResult("等待浏览器授权", "请在腾讯页面完成登录；助手会自动接收结果", false);
                while (!ct.IsCancellationRequested) {
                    await Task.Delay(2500, ct);
                    try {
                        Account associated = await api.FinishLogin(state, ct);
                        store.SaveAccount(associated); account = associated; accountText.Text = DisplayName(); retries = 0; retryAt = DateTime.MinValue;
                        string application = path.Text.Trim();
                        if (File.Exists(application) && String.Equals(Path.GetFileName(application), "WorkBuddy.exe", StringComparison.OrdinalIgnoreCase)) {
                            prefs.ApplicationPath = Path.GetFullPath(application); prefs.Enabled = true; enabled.IsChecked = true; store.Save(prefs);
                            SetResult("账号已关联", "每日执行已启用，可在设置中调整时间", false);
                        } else SetResult("账号已关联", "请选择 WorkBuddy 程序，再保存每日执行设置", false);
                        Record("账号关联完成"); break;
                    } catch (ServiceException ex) { if (ex.Pending) continue; throw; }
                }
            } catch (OperationCanceledException) { SetResult("关联已结束", "授权已取消或超时，请重新关联", true); }
            catch { SetResult("关联未完成", "网络或登录服务未确认成功，请重试", true); }
            finally { loginCancellation.Dispose(); loginCancellation = null; SetBusy(false); UpdateNext(); }
        }
        public void BeginLink() { Login(); }
        async void Execute(bool automatic) {
            if (busy || exiting) return;
            if (automatic && (!prefs.Enabled || DateTime.UtcNow < Schedule.DueUtc(prefs, account, DateTime.UtcNow) || DateTime.UtcNow < retryAt)) { UpdateNext(); return; }
            if (account == null) { SetResult("尚未关联账号", "请先点击关联账号", true); return; }
            if (Schedule.Done(prefs, account, DateTime.UtcNow)) { SetResult("今日已完成", "同一账号每天只执行一次", false); UpdateNext(); return; }
            SetBusy(true); timer.Stop();
            try {
                Launch(prefs.ApplicationPath == "" ? path.Text.Trim() : prefs.ApplicationPath);
                SetResult("正在签到", "已启动 WorkBuddy，等待签到服务确认", false);
                string day = Schedule.Day(DateTime.UtcNow);
                string message = await api.Checkin(account, store, lifetime.Token);
                prefs.LastAccount = account.Identity; prefs.LastDay = day; store.Save(prefs);
                retries = 0; retryAt = DateTime.MinValue; SetResult("今日已完成", message, false); Record(message);
            } catch (ServiceException ex) {
                if (ex.Auth) { prefs.Enabled = false; enabled.IsChecked = false; store.Save(prefs); SetResult("需要重新关联", "登录状态已失效，自动执行已暂停", true); Record("登录状态失效，自动执行暂停"); }
                else Failed();
            } catch (OperationCanceledException) { }
            catch { Failed(); }
            finally { SetBusy(false); UpdateNext(); }
        }
        void Failed() {
            string today = Schedule.Day(DateTime.UtcNow);
            if (retryDay != today) { retries = 0; retryDay = today; }
            retries++;
            if (retries >= 3) { retryAt = Schedule.Now(DateTime.UtcNow).Date.AddDays(1).Add(Schedule.Parse(prefs.Time)); retryAt = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(retryAt, DateTimeKind.Unspecified), Schedule.Zone); }
            else retryAt = DateTime.UtcNow.AddMinutes(15);
            SetResult("未确认签到成功", !prefs.Enabled ? "请检查网络和账号后重试" : retries >= 3 ? "今天的自动重试已停止，可稍后手动执行" : "自动模式将在 15 分钟后重试", true); Record("签到未确认成功");
        }
        void Open() { Show(); WindowState = WindowState.Normal; Activate(); }
        void MakeTray() {
            string icon = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "daily.ico");
            trayIcon = File.Exists(icon) ? new System.Drawing.Icon(icon, 32, 32) : (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "WorkBuddy 每日签到", Visible = true };
            var menu = new Forms.ContextMenuStrip(); menu.Items.Add("打开助手", null, delegate { Dispatcher.BeginInvoke(new Action(Open)); });
            menu.Items.Add("立即签到", null, delegate { Dispatcher.BeginInvoke(new Action(delegate { Execute(false); })); });
            menu.Items.Add("暂停自动签到", null, delegate { Dispatcher.BeginInvoke(new Action(delegate { prefs.Enabled = false; enabled.IsChecked = false; store.Save(prefs); UpdateNext(); })); });
            menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出", null, delegate { Dispatcher.BeginInvoke(new Action(delegate { exiting = true; Close(); Application.Current.Shutdown(); })); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(Open)); };
        }
        public void SavePreview(string file) {
            ShowInTaskbar = false; Left = -12000; Top = -12000; Show(); UpdateLayout();
            var visual = (FrameworkElement)Content;
            var image = new RenderTargetBitmap((int)(visual.ActualWidth * 2), (int)(visual.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32); image.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var output = File.Create(file)) encoder.Save(output); Hide();
        }
    }
}

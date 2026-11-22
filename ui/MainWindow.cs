using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Duyu
{
    public sealed class MainWindow : Window
    {
        private static readonly string[] NavNames = { "Mikser", "Ses", "Mikrofon", "Ayarlar" };
        private static readonly string[] NavIcons = { "", "", "", "" };

        public readonly Model M;
        public Remote Remote; // null in preview

        private readonly Page[] _pages = new Page[4];
        private readonly RadioButton[] _nav = new RadioButton[4];
        private readonly TextBlock[] _navState = new TextBlock[4];
        private readonly ContentControl _view = new ContentControl { Focusable = false };
        private readonly TextBlock _status = Kit.Text("", 13, "Muted", true);
        private readonly LevelMeter _out = new LevelMeter("Master", true);
        private readonly TextBlock _toastText = Kit.Text("", 14, null, true);
        private readonly Border _toast;
        private readonly DispatcherTimer _frame = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        private readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        private readonly DispatcherTimer _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        private readonly bool[] _hotkeyFailed = new bool[Model.HotkeyNames.Length];
        private Forms.NotifyIcon _tray;
        private HwndSource _source;
        private int _page;
        private bool _quitting;

        public MainWindow(Model model)
        {
            M = model;
            Title = "Duyu";
            Width = Math.Min(1280, SystemParameters.WorkArea.Width);
            Height = Math.Min(860, SystemParameters.WorkArea.Height);
            MinWidth = 900;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Resources = Kit.Theme;
            Background = Kit.Brush("Bg");
            Foreground = Kit.Brush("Text");
            FontFamily = Kit.Font("Body");
            FontSize = 14;
            UseLayoutRounding = true;
            AllowDrop = false;
            try { Icon = BitmapFrame.Create(new Uri(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Duyu.ico"))); }
            catch (IOException) { } // the icon is cosmetic

            _toast = new Border
            {
                Background = Kit.Brush("Raised"), BorderBrush = Kit.Brush("Edge"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 11, 16, 11), MaxWidth = 520,
                Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed, Child = _toastText
            };
            AutomationProperties.SetLiveSetting(_toastText, AutomationLiveSetting.Polite);

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            var rail = Rail();
            Grid.SetColumn(_view, 1);
            Grid.SetColumn(_toast, 1);
            Content = root.With(rail, _view, _toast);

            M.Changed += OnModelChanged;
            M.DevicesChanged += () => { if (_pages[_page] != null) _pages[_page].Refresh(); };
            M.Notice += Toast;

            _frame.Tick += (s, e) => Frame();
            _poll.Tick += (s, e) =>
            {
                M.PollMeters(); // also while hidden in the tray: protection depends on it
                M.PollLists();
                M.AutoSwitch();
                M.UpdateProtection();
            };
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); _toast.Visibility = Visibility.Collapsed; };

            M.PollLists();
            Navigate(0);
            OnModelChanged();
            _frame.Start();
            _poll.Start();
        }

        private FrameworkElement Rail()
        {
            var brand = new StackPanel { Orientation = Orientation.Horizontal };
            brand.Children.Add(new Image { Source = Mark(), Width = 30, Height = 30 });
            brand.Children.Add(Kit.Title("duyu", 28).Margin(12, 0, 0, 0));

            var nav = new StackPanel();
            for (var i = 0; i < NavNames.Length; i++)
            {
                var index = i;
                _navState[i] = Kit.Text("", 12, "Muted");
                _navState[i].Visibility = Visibility.Collapsed;
                var text = Kit.Stack(Kit.Text(NavNames[i]), _navState[i]);
                text.VerticalAlignment = VerticalAlignment.Center;
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(Kit.Icon(NavIcons[i], 18).Margin(0, 0, 14, 0));
                row.Children.Add(text);
                _nav[i] = Kit.Named(new RadioButton { Content = row, GroupName = "nav", Style = Kit.Style("Nav"), ToolTip = "Ctrl+" + (i + 1) }, NavNames[i]);
                _nav[i].Checked += (s, e) => Navigate(index);
                nav.Children.Add(_nav[i].Margin(0, 2, 0, 2));
            }

            var foot = Kit.Stack(Kit.Text("Çıkış", 12, "Muted").Margin(0, 0, 0, 6), _out, _status.Margin(0, 10, 0, 0));
            DockPanel.SetDock(brand, Dock.Top);
            DockPanel.SetDock(foot, Dock.Bottom);
            var dock = new DockPanel { LastChildFill = true };
            dock.With(brand.Margin(12, 8, 0, 24), foot.Margin(12, 12, 12, 4),
                new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
            return new Border { Background = Kit.Brush("Rail"), Padding = new Thickness(12, 16, 12, 16), Child = dock };
        }

        private static ImageSource Mark()
        {
            var g = new DrawingGroup();
            var accent = Kit.Brush("Accent");
            g.Children.Add(new GeometryDrawing(accent, null, new EllipseGeometry(new Point(16, 16), 5, 5)));
            g.Children.Add(new GeometryDrawing(null, new Pen(accent, 2.5), new EllipseGeometry(new Point(16, 16), 9.5, 9.5)));
            g.Children.Add(new GeometryDrawing(null, new Pen(Draw.Alpha(accent, 0.45), 2), new EllipseGeometry(new Point(16, 16), 14.5, 14.5)));
            g.Freeze();
            return new DrawingImage(g);
        }

        private Page Create(int i)
        {
            switch (i)
            {
                case 0: return new MixerPage(this);
                case 1: return new SoundPage(this);
                case 2: return new MicPage(this);
                default: return new SettingsPage(this);
            }
        }

        public void Navigate(int i)
        {
            if (_pages[_page] != null && i != _page) _pages[_page].Leave();
            _page = i;
            var p = _pages[i] ?? (_pages[i] = Create(i));
            p.Refresh();
            _view.Content = p;
            if (_nav[i].IsChecked != true) _nav[i].IsChecked = true;
            p.Tick();
        }

        private void OnModelChanged()
        {
            if (_pages[_page] != null) _pages[_page].Refresh();
            SetState(0, M.On(P.MixerOn), "Good");
            SetState(2, M.On(P.MicOn), "Good");
        }

        private void SetState(int i, bool on, string color)
        {
            _navState[i].Text = "Açık";
            _navState[i].Foreground = Kit.Brush(color);
            _navState[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Frame()
        {
            if (!IsVisible || WindowState == WindowState.Minimized) return;
            M.PollMeters();
            _pages[_page].Tick();
            _out.Show(M.Meter(Duyu.M.OutL), M.Meter(Duyu.M.OutR));

            if (M.On(P.MixerOn) && M.Meter(Duyu.M.MixerRunning) < 0.5f) _status.Text = "Çıkış açılamadı. Sesler Windows'a bırakıldı.";
            else if (M.On(P.MixerOn)) _status.Text = (int)M.Meter(Duyu.M.Captured) + " uygulama işleniyor";
            else _status.Text = "Ses motoru kapalı";
        }

        public void Toast(string text)
        {
            _toastText.Text = text;
            _toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        // ------------------------------------------------------------ actions

        public void SetMixer(bool on)
        {
            if (on && !M.S.SeenMixerInfo && !M.Preview)
            {
                var output = M.Effective(Role.OutMain);
                var text = "Duyu, ses çıkaran uygulamaların özgün sesini kısar ve işlenmiş halini kulaklığına çalar. Windows mikserindeki sürgülerine dokunmaz."
                         + "\n\nSonar veya benzeri bir mikser açıksa önce onu kapat. Yoksa efektler üst üste biner."
                         + (output != null ? "\n\nÇıkış: " + output.Name : "");
                if (!Dialog.Confirm(this, "Ses motoru açılsın mı?", text, "Motoru aç")) return;
                M.S.SeenMixerInfo = true;
            }
            M.Set(P.MixerOn, on ? 1 : 0);
        }

        private void RunHotkey(int i)
        {
            switch (i)
            {
                case 0: M.Toggle(P.Mute, Ch.Mic); break;
                case 1: M.Toggle(P.MasterMute); break;
                case 2: M.NudgeMix(-Model.MixStep); break;
                case 3: M.NudgeMix(Model.MixStep); break;
            }
        }

        // ------------------------------------------------------------ window, hotkeys, tray

        public bool HotkeyFailed(int i) { return _hotkeyFailed[i]; }

        public void RegisterHotkeys()
        {
            if (M.Preview || M.Sandboxed) return;
            var h = new WindowInteropHelper(this).EnsureHandle();
            for (var i = 0; i < _hotkeyFailed.Length; i++)
            {
                Native.UnregisterHotKey(h, 100 + i);
                uint mods, vk;
                _hotkeyFailed[i] = Hotkeys.Parse(M.S.Hotkeys[i], out mods, out vk) && !Native.RegisterHotKey(h, 100 + i, mods, vk);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Chrome.Dark(this);
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (_source != null) _source.AddHook(Hook);
        }

        private IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            const int WmHotkey = 0x0312;
            var id = w.ToInt64() - 100;
            if (msg == WmHotkey && id >= 0 && id < _hotkeyFailed.Length)
            {
                handled = true;
                Dispatcher.BeginInvoke(new Action(() => RunHotkey((int)id)));
            }
            return IntPtr.Zero;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key >= Key.D1 && e.Key <= Key.D4)
            {
                Navigate(e.Key - Key.D1);
                e.Handled = true;
            }
        }

        public void CreateTray()
        {
            if (M.Preview || M.Sandboxed) return;
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Duyu'yu aç", null, (s, e) => Reveal());
            var mixer = menu.Items.Add("Ses motoru", null, (s, e) => SetMixer(!M.On(P.MixerOn))) as Forms.ToolStripMenuItem;
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Çıkış", null, (s, e) => Quit());
            menu.Opening += (s, e) => mixer.Checked = M.On(P.MixerOn);

            _tray = new Forms.NotifyIcon
            {
                Text = "Duyu", Visible = true, ContextMenuStrip = menu,
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location)
            };
            _tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) Reveal(); };
        }

        public void Reveal()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        public void Quit()
        {
            _quitting = true;
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_quitting && _tray != null && M.S.CloseToTray)
            {
                e.Cancel = true;
                _pages[_page].Leave();
                Hide();
                return;
            }
            _frame.Stop();
            _poll.Stop();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            base.OnClosing(e);
        }
    }
}

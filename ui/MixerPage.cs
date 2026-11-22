using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Duyu
{
    public sealed class MixerPage : Page
    {
        private const string DragFormat = "DuyuApp";
        private readonly StackPanel _apps = new StackPanel();
        private readonly List<Action> _meters = new List<Action>();
        private bool _stale;

        public MixerPage(MainWindow host)
            : base(host, "Mikser", "Her uygulama kendi kanalında. Sanal ses sürücüsü kurulmaz.", PowerSwitch(host))
        {
            var strips = new Tiles();
            foreach (var ch in new[] { Ch.Master, Ch.Game, Ch.Chat, Ch.Media, Ch.Aux, Ch.Mic }) strips.Children.Add(Strip(ch));
            Add(strips);

            var mixEnds = Kit.Row(Kit.Text("Oyun", 13, "Game"), Kit.Text("Sohbet", 13, "Chat"));
            var mix = B.Slider("Oyun ile sohbet dengesi", P.ChatMix, -1, 1, 0.05,
                v => Math.Abs(v) < 0.03 ? "Eşit" : (v < 0 ? "Oyun " : "Sohbet ") + "+" + Format.Percent(Math.Abs(v)));
            var chatMix = Kit.Card("ChatMix", null, B.Switch("Tek sürgüyle denge", null, P.ChatMixOn), mix, mixEnds);
            B.On(() => mix.IsEnabled = M.On(P.ChatMixOn));

            var streamExtra = Kit.Stack(
                B.Switch("Yayın miksini dinle", "Kulaklığında izleyicinin duyduğunu duyarsın.", P.StreamListen),
                B.Device("Yayın miksi çıkışı", Role.OutStream, false, "Seçilmedi"));
            var streamer = Kit.Card("Yayıncı modu", null,
                B.Switch("İkinci, bağımsız miks", "Her kanalda yayın için ayrı bir sürgü açılır.", P.StreamerOn),
                streamExtra);
            B.On(() => streamExtra.Visibility = M.On(P.StreamerOn) ? Visibility.Visible : Visibility.Collapsed);

            Add(new Columns().With(chatMix, streamer));
            Add(Kit.Card("Uygulamalar", "Kanal seç ya da satırı kanalın üstüne sürükle. İşlenen ses 65–75 ms'de kulağa varır, rekabetçi oyunda o oyun için “Windows'a bırak” seçebilirsin.", _apps));

            BuildApps();
            M.AppsChanged += BuildApps;
        }

        private static CheckBox PowerSwitch(MainWindow host)
        {
            var box = Kit.Named(new CheckBox { Style = Kit.Style("Switch"), Content = Kit.Text("Ses motoru") }, "Ses motoru");
            Action sync = () => box.IsChecked = host.M.On(P.MixerOn);
            sync();
            RoutedEventHandler changed = (s, e) =>
            {
                if ((box.IsChecked == true) == host.M.On(P.MixerOn)) return;
                host.SetMixer(box.IsChecked == true);
                host.Dispatcher.BeginInvoke(sync); // the confirmation may have been cancelled
            };
            box.Checked += changed;
            box.Unchecked += changed;
            host.M.Changed += sync;
            return box;
        }

        private FrameworkElement Strip(int ch)
        {
            var master = ch == Ch.Master;
            var mic = ch == Ch.Mic;
            var key = Ch.Keys[ch];
            var name = master ? "Ana" : Ch.Names[ch];

            var readout = Kit.Title("", 16);
            readout.HorizontalAlignment = HorizontalAlignment.Center;
            var fader = master ? B.Fader("Ana ses", P.MasterVol, 0, key, readout)
                      : mic ? B.Fader("Mikrofonu kendinde duyma", P.Sidetone, 0, key, readout)
                      : B.Fader(name + " sesi", P.Vol, ch, key, readout);

            var meter = new LevelMeter(key).Margin(2, 10, 2, 10);
            var left = master ? Duyu.M.OutL : (Duyu.M)((int)Duyu.M.GameL + 2 * ch);
            _meters.Add(() => meter.Show(M.Meters[(int)left], M.Meters[(int)left + 1]));

            var tracks = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 220 };
            tracks.With(fader, meter);

            var streamRead = Kit.Text("", 13, "Muted");
            streamRead.HorizontalAlignment = HorizontalAlignment.Center;
            streamRead.ToolTip = "Yayın miksi";
            if (!master)
            {
                var streamFader = B.Fader(name + " yayın sesi", P.StreamVol, ch, "Master", streamRead);
                streamFader.Opacity = 0.75;
                tracks.Children.Add(streamFader);
                B.On(() => streamFader.Visibility = streamRead.Visibility = M.On(P.StreamerOn) ? Visibility.Visible : Visibility.Collapsed);
            }
            else streamRead.Visibility = Visibility.Collapsed;

            var mutes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            // With two mixes on screen every control says whose mix it is.
            mutes.Children.Add(Labelled(master ? B.Mute("Tüm sesi sustur", P.MasterMute) : B.Mute(name + ": sustur", P.Mute, ch), "Sen"));
            if (!master)
            {
                var streamMute = Labelled(B.Mute(name + ": yayında sustur", P.StreamMute, ch), "Yayın").Margin(6, 0, 0, 0);
                mutes.Children.Add(streamMute);
                B.On(() => streamMute.Visibility = M.On(P.StreamerOn) ? Visibility.Visible : Visibility.Collapsed);
            }

            var head = Kit.Title(name, 16);
            head.HorizontalAlignment = HorizontalAlignment.Center;
            var sub = Kit.Text(mic ? "Kendini duy" : master ? "Çıkış" : " ", 12, "Muted");
            sub.HorizontalAlignment = HorizontalAlignment.Center;

            var rule = new Border { Height = 3, Width = 28, CornerRadius = new CornerRadius(2), Background = Kit.Brush(key) };
            var card = new Border
            {
                Background = Kit.Brush("Surface"),
                BorderBrush = Kit.Brush("Surface"),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(4, 12, 4, 12),
                Child = Kit.Stack(rule.Margin(0, 0, 0, 10), head, sub.Margin(0, 2, 0, 4), tracks, readout.Margin(0, 4, 0, 2), streamRead, mutes.Margin(0, 8, 0, 0))
            };
            if (ch >= Ch.Mic) return card;

            card.AllowDrop = true;
            Action<bool> highlight = on => card.BorderBrush = Kit.Brush(on ? key : "Surface");
            card.DragEnter += (s, e) => highlight(e.Data.GetDataPresent(DragFormat));
            card.DragLeave += (s, e) => highlight(false);
            card.Drop += (s, e) =>
            {
                highlight(false);
                if (e.Data.GetDataPresent(DragFormat)) M.Assign((string)e.Data.GetData(DragFormat), ch);
            };
            return card;
        }

        private StackPanel Labelled(FrameworkElement control, string caption)
        {
            var text = Kit.Text(caption, 12, "Muted").Margin(0, 4, 0, 0);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            B.On(() => text.Visibility = M.On(P.StreamerOn) ? Visibility.Visible : Visibility.Hidden);
            return Kit.Stack(control, text);
        }

        private void BuildApps()
        {
            _stale = true;
            // Never rebuild under an open dropdown.
            if (_apps.Children.OfType<DockPanel>().SelectMany(d => d.Children.OfType<ComboBox>()).Any(c => c.IsDropDownOpen)) return;
            _stale = false;
            _apps.Children.Clear();
            if (M.Apps.Count == 0)
            {
                _apps.Children.Add(Kit.Text("Şu an ses açan uygulama yok. Bir oyun, sohbet ya da müzik uygulaması ses çıkarınca burada belirir.", 13, "Muted", true));
                return;
            }
            foreach (var a in M.Apps) _apps.Children.Add(AppLine(a));
        }

        private static string Describe(AppState s)
        {
            switch (s)
            {
                case AppState.Captured: return "İşleniyor";
                case AppState.Starting: return "Bağlanıyor…";
                case AppState.SharedTree: return "Doğrudan: başka uygulama başlatmış";
                case AppState.Direct: return "Windows'a bırakıldı";
                case AppState.Error: return "Yakalanamadı, doğrudan çalıyor";
                default: return "Ses motoru kapalı";
            }
        }

        private DockPanel AppLine(AppRow app)
        {
            var dot = new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
                Background = Kit.Brush(app.Active ? "Good" : "Hover"), ToolTip = app.Active ? "Şu an ses çıkarıyor" : "Sessiz"
            };
            var exe = app.Exe.EndsWith(".exe") ? app.Exe.Substring(0, app.Exe.Length - 4) : app.Exe;
            var name = Kit.Text(exe);
            name.ToolTip = app.Exe;
            var text = Kit.Stack(name, Kit.Text(Describe(app.State), 12, "Muted"));
            text.VerticalAlignment = VerticalAlignment.Center;

            var pick = Kit.Named(new ComboBox { Width = 190 }, exe + " kanalı");
            foreach (var c in Ch.Names.Take(4)) pick.Items.Add(c);
            pick.Items.Add("Windows'a bırak");
            pick.Items.Add("Otomatik");
            pick.SelectedIndex = app.Channel < 0 ? 4 : app.Channel;
            pick.IsEnabled = app.State != AppState.SharedTree;
            pick.SelectionChanged += (s, e) =>
            {
                var i = pick.SelectedIndex;
                // Deferred: assigning rebuilds the list this combo lives in.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    pick.IsDropDownOpen = false;
                    M.Assign(app.Exe, i < 4 ? i : i == 4 ? -1 : -2);
                }));
            };

            var row = Kit.Row(text.Margin(0, 0, 12, 0), pick);
            row.Children.Insert(0, dot.Margin(0, 0, 12, 0));
            row.Margin = new Thickness(0, 4, 0, 4);
            row.Background = Kit.Brush("Surface");
            row.Cursor = Cursors.SizeAll;

            Point? from = null;
            row.MouseLeftButtonDown += (s, e) => from = e.GetPosition(row);
            row.MouseLeftButtonUp += (s, e) => from = null;
            row.MouseMove += (s, e) =>
            {
                if (from == null || e.LeftButton != MouseButtonState.Pressed || pick.IsMouseOver) return;
                if ((e.GetPosition(row) - from.Value).Length < 6) return;
                from = null;
                DragDrop.DoDragDrop(row, new DataObject(DragFormat, app.Exe), DragDropEffects.Move);
            };
            return row;
        }

        public override void Tick()
        {
            foreach (var m in _meters) m();
            if (_stale) BuildApps();
        }
    }
}

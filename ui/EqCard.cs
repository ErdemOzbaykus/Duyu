using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Duyu
{
    /// Parametric EQ editor with presets. Used by the sound and microphone pages.
    internal sealed class EqCard
    {
        private static readonly string[] Types = { "Çan", "Alçak raf", "Yüksek raf", "Alçak geçiren", "Yüksek geçiren", "Çentik" };

        private readonly MainWindow _host;
        private readonly Model _m;
        private readonly Binder _b;
        private readonly Func<int> _ch;
        private readonly EqGraph _graph;
        private readonly StackPanel _list = new StackPanel();
        private readonly TextBox _search = Kit.Named(new TextBox(), "Hazır ayar ara");
        private string _listed;
        private int _limit = 14;

        public readonly FrameworkElement View;

        public EqCard(MainWindow host, Binder b, Func<int> ch)
        {
            _host = host; _m = host.M; _b = b; _ch = ch;
            _graph = new EqGraph(_m);
            View = new Columns { Breakpoint = 840, Ratio = 0.68 }.With(Editor(), Library());
        }

        private float[] Band { get { return _m.S.Bands[_ch()][_graph.Selected]; } }

        private void Edit(int k, double v)
        {
            var band = (float[])Band.Clone();
            band[k] = (float)v;
            _m.SetBand(_ch(), _graph.Selected, band);
        }

        private Border Editor()
        {
            _b.On(() =>
            {
                _graph.Channel = _ch();
                _graph.InvalidateVisual();
            });
            _graph.SelectedChanged += _b.Refresh;

            var name = Kit.Title("", 18);
            _b.On(() => name.Text = _m.S.PresetNames[_ch()]);
            var enabled = _b.Switch("EQ açık", null, P.EqOn, _ch);
            enabled.MinWidth = 120;
            name.VerticalAlignment = VerticalAlignment.Center;

            var bandName = Kit.Title("", 15);
            _b.On(() => bandName.Text = "Bant " + (_graph.Selected + 1));
            var bandOn = _b.Switch("Etkin", null, () => Band[0] > 0.5f, v => Edit(0, v ? 1 : 0));

            var type = Kit.Named(new ComboBox(), "Filtre türü");
            foreach (var t in Types) type.Items.Add(t);
            var syncing = false;
            type.SelectionChanged += (s, e) => { if (!syncing) Edit(1, type.SelectedIndex); };
            _b.On(() => { syncing = true; type.SelectedIndex = (int)Band[1]; syncing = false; });

            var freq = _b.Slider("Frekans", Math.Log10(20), Math.Log10(20000), 0.005,
                () => Math.Log10(Math.Max(20, Band[2])), v => Edit(2, Math.Round(Math.Pow(10, v))), v => Format.Hz(Math.Pow(10, v)));
            var gain = _b.Slider("Kazanç", -18, 18, 0.5, () => Band[3], v => Edit(3, v), Format.Db);
            var q = _b.Slider("Genişlik (Q)", 0.1, 12, 0.05, () => Band[4], v => Edit(4, v), Format.Q);
            _b.On(() => gain.IsEnabled = Band[1] < 3);

            var prev = Kit.IconButton("", "Önceki bant", () => Step(-1));
            var next = Kit.IconButton("", "Sonraki bant", () => Step(1));
            bandName.VerticalAlignment = VerticalAlignment.Center;
            var bandHead = Kit.Row(bandName, prev, next);

            var bands = new Columns { Breakpoint = 420 }.With(
                Kit.Stack(bandHead, bandOn, type),
                Kit.Stack(freq, gain, q));

            var tone = Kit.Equal(
                _b.Slider("Bas", P.Bass, -12, 12, 0.5, Format.Db, _ch).Margin(0, 8, 16, 0),
                _b.Slider("Ses", P.Voice, -12, 12, 0.5, Format.Db, _ch).Margin(0, 8, 16, 0),
                _b.Slider("Tiz", P.Treble, -12, 12, 0.5, Format.Db, _ch).Margin(0, 8, 0, 0));

            return Kit.Card(null, null,
                Kit.Row(name, enabled).Margin(0, 0, 0, 10),
                _graph,
                tone.Margin(0, 6, 0, 0),
                _b.Slider("Ön kazanç", P.Preamp, -24, 12, 0.1, Format.Db, _ch),
                new Border { Height = 1, Background = Kit.Brush("Line") }.Margin(0, 14, 0, 10),
                bands);
        }

        private void Step(int by)
        {
            _graph.Selected = (_graph.Selected + by + Presets.Bands) % Presets.Bands;
            _b.Refresh();
        }

        private Border Library()
        {
            AutomationProperties.SetHelpText(_search, "Ada veya türe göre süzer");
            var placeholder = Kit.Text("Ara: adım, caz, yayıncı…", 13, "Muted");
            placeholder.IsHitTestVisible = false;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            placeholder.Margin = new Thickness(14, 0, 8, 0);
            _search.TextChanged += (s, e) =>
            {
                placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _limit = 14;
                Rebuild(true);
            };

            _b.On(() => Rebuild(false));

            var actions = Kit.Wrap(
                Kit.Button("Kaydet", Save, null, ""),
                Kit.Button("Dosyadan", FromFile, null, ""),
                Kit.Button("Kodu kopyala", Copy, null, ""),
                Kit.Button("Yapıştır", Paste, null, ""));

            return Kit.Card("Hazır ayarlar", "Equalizer APO ve AutoEq dosyalarını da okur.",
                actions,
                new Grid().With(_search, placeholder).Margin(0, 4, 0, 8),
                _list);
        }

        private void Rebuild(bool force)
        {
            var ch = _ch();
            var s = _m.S;
            var key = ch + "|" + s.PresetNames[ch] + "|" + s.UserPresets.Count + "|" + string.Join(",", s.Favorites);
            if (!force && key == _listed) return;
            _listed = key;

            var group = ch == Ch.Game ? "Oyun" : ch == Ch.Chat ? "Sohbet" : ch == Ch.Mic ? "Mikrofon" : ch == Ch.Master ? "Kulaklık" : "Medya";
            var culture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR").CompareInfo;
            var query = _search.Text.Trim();
            var ignore = System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
            var hits = _m.AllPresets
                .Where(p => query.Length == 0 || culture.IndexOf(p.Name + " " + p.Group, query, ignore) >= 0)
                .OrderBy(p => s.Favorites.Contains(p.Name) ? 0 : p.Group == "Benim" ? 1 : p.Group == group ? 2 : p.Group == "Genel" ? 3 : 4)
                .ToList();

            _list.Children.Clear();
            foreach (var p in hits.Take(_limit)) _list.Children.Add(Row(p, p.Name == s.PresetNames[ch]));
            if (hits.Count == 0) _list.Children.Add(Kit.Text("Eşleşen ayar yok.", 13, "Muted", true));
            if (hits.Count > _limit)
                _list.Children.Add(Kit.Button("Tümünü göster (" + hits.Count + ")", () => { _limit = 1000; Rebuild(true); }, "Quiet"));
        }

        private DockPanel Row(Preset p, bool current)
        {
            var fav = _m.S.Favorites.Contains(p.Name);
            var star = Kit.IconButton(fav ? "" : "", p.Name + (fav ? ": favoriden çıkar" : ": favorilere ekle"), () =>
            {
                if (!_m.S.Favorites.Remove(p.Name)) _m.S.Favorites.Add(p.Name);
                _m.SaveNow();
                Rebuild(true);
            });
            if (fav) star.Foreground = Kit.Brush("Accent");
            var label = Kit.Row(Kit.Text(p.Name, 14, current ? "Accent" : null), Kit.Text(p.Group, 12, "Muted").Margin(8, 1, 0, 0));
            var apply = Kit.Named(new Button { Content = label, Style = Kit.Style("Quiet"), HorizontalContentAlignment = HorizontalAlignment.Stretch }, p.Name + " ayarını uygula");
            apply.Click += (s, e) => _m.ApplyPreset(_ch(), p);
            return Kit.Row(apply, star);
        }

        private void Save()
        {
            var current = _m.S.PresetNames[_ch()];
            var name = Dialog.Ask(_host, "Hazır ayarı kaydet", "Ad", current == "Özel" ? "" : current);
            if (string.IsNullOrWhiteSpace(name)) return;
            _m.AddPreset(_ch(), name.Trim());
            Rebuild(true);
        }

        private void FromFile()
        {
            var d = new OpenFileDialog { Filter = "EQ dosyası (*.txt)|*.txt|Tüm dosyalar|*.*" };
            if (d.ShowDialog(_host) != true) return;
            try
            {
                if (new FileInfo(d.FileName).Length > 64 * 1024) { _host.Toast("Dosya bir EQ ayarı için çok büyük."); return; }
                if (_m.ImportPreset(_ch(), File.ReadAllText(d.FileName), Path.GetFileNameWithoutExtension(d.FileName))) Rebuild(true);
            }
            catch (IOException e) { _host.Toast("Dosya okunamadı: " + e.Message); }
            catch (UnauthorizedAccessException e) { _host.Toast("Dosya okunamadı: " + e.Message); }
        }

        private void Copy()
        {
            try { Clipboard.SetText(_m.ShareCode(_ch())); _host.Toast("Paylaşım kodu kopyalandı."); }
            catch (System.Runtime.InteropServices.COMException) { _host.Toast("Pano meşgul, tekrar dene."); }
        }

        private void Paste()
        {
            try { if (_m.ImportPreset(_ch(), Clipboard.GetText(), "Yapıştırılan")) Rebuild(true); }
            catch (System.Runtime.InteropServices.COMException) { _host.Toast("Pano meşgul, tekrar dene."); }
        }
    }
}

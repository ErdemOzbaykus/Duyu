using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Duyu
{
    public sealed class SettingsPage : Page
    {
        private readonly StackPanel _profiles = new StackPanel();
        private readonly TextBlock _health = Kit.Text("", 13, "Muted", true);
        private readonly TextBlock _remoteUrl = Kit.Title("", 17);
        private readonly TextBlock _remotePin = Kit.Title("", 34);
        private readonly TextBlock _remoteInfo = Kit.Text("", 13, "Muted", true);
        private StackPanel _remoteDetails;

        public SettingsPage(MainWindow host)
            : base(host, "Ayarlar", "Aygıtlar, oyun profilleri, kısayollar ve güvenlik.")
        {
            var devices = Kit.Card("Ses çıkışları", "Bir kanalı ayrı aygıta yollayabilirsin, örneğin medyayı hoparlöre.",
                B.Device("Ana çıkış (kulaklık)", Role.OutMain, false, "Otomatik"),
                B.Device("Oyun", Role.OutGame, false, "Ana çıkış"),
                B.Device("Sohbet", Role.OutChat, false, "Ana çıkış"),
                B.Device("Medya", Role.OutMedia, false, "Ana çıkış"),
                B.Device("Diğer", Role.OutAux, false, "Ana çıkış"));

            var profiles = Kit.Card("Oyun profilleri", "Oyun öne gelince ayarları yükler, oyun kapanınca geri alır.",
                _profiles,
                Kit.Button("Şimdiki sesi profil yap", NewProfile, null, "").Margin(0, 10, 0, 0));
            BuildProfiles();

            var keys = Kit.Card("Genel kısayollar", "Değiştirmek için tıkla ve yeni tuşlara bas. Esc vazgeçer, Delete kaldırır.");
            for (var i = 0; i < Model.HotkeyNames.Length; i++) ((StackPanel)keys.Child).Children.Add(HotkeyRow(i));

            var general = Kit.Card("Genel", null,
                B.Switch("Windows ile başlat", "Tepside açılır.", () => M.StartWithWindows, v => M.StartWithWindows = v),
                B.Switch("Son durumla aç", "Mikser ve mikrofon işleme.",
                    () => M.S.Resume, v => { M.S.Resume = v; M.SaveNow(); }),
                B.Switch("Kapatınca tepside çalış", null, () => M.S.CloseToTray, v => { M.S.CloseToTray = v; M.SaveNow(); }),
                Kit.Wrap(Kit.Button("Dışa aktar", Export, null, ""), Kit.Button("İçe aktar", Import, null, "")).Margin(0, 12, 0, 0));

            var safety = Kit.Card("Ses güvenliği",
                "Motor açıkken uygulamaların özgün sesi 80 dB kısılır, işlenmiş kopyası çalınır. Kapanışta geri yüklenir. Çökme olursa bekçi, elektrik giderse sonraki oturum açılışı onarır.",
                _health,
                Kit.Button("Motoru kapat ve sesleri geri ver", () => host.SetMixer(false), null, "").Margin(0, 12, 0, 0));

            Add(new Columns().With(
                Kit.Stack(devices.Margin(0, 0, 0, 16), general.Margin(0, 0, 0, 16), safety),
                Kit.Stack(RemoteCard().Margin(0, 0, 0, 16), profiles.Margin(0, 0, 0, 16), keys)));
        }

        private Border RemoteCard()
        {
            var enabled = B.Switch("Uzaktan ayar", "Yalnızca kendi Tailscale ağından erişilir. Ses gönderilmez.",
                () => M.S.RemoteOn, v => { if (Host.Remote != null) Host.Remote.SetEnabled(v); });
            _remoteUrl.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetName(_remotePin, "Eşleştirme kodu");
            _remoteDetails = Kit.Stack(
                Kit.Text("Safari'de aç, Paylaş > Ana Ekrana Ekle, sonra simgeden açıp kodu gir.", 13, "Muted", true).Margin(0, 10, 0, 8),
                _remoteUrl,
                _remotePin.Margin(0, 8, 0, 0),
                _remoteInfo.Margin(0, 0, 0, 10),
                Kit.Wrap(
                    Kit.Button("Yeni kod", () => Host.Remote.NewPin(), null, "\uE72C"),
                    Kit.Button("Adresi kopyala", CopyAddress, null, "\uE8C8"),
                    Kit.Button("Tüm cihazların bağlantısını kes", Revoke, "Quiet")));
            return Kit.Card("Telefon ve tablet", "iPhone ve iPad'den ayar yap.", enabled, _remoteDetails);
        }

        private void CopyAddress()
        {
            var url = Host.Remote.Url;
            if (url == null) return;
            try { Clipboard.SetText(url); Host.Toast("Adres kopyalandı."); }
            catch (System.Runtime.InteropServices.COMException) { Host.Toast("Pano meşgul, tekrar dene."); }
        }

        private void Revoke()
        {
            if (Dialog.Confirm(Host, "Cihazların bağlantısını kes", "Tüm telefon ve tabletler yeniden kod ister.", "Bağlantıyı kes"))
                Host.Remote.RevokeAll();
        }

        private void TickRemote()
        {
            var r = Host.Remote;
            var show = r != null && M.S.RemoteOn;
            _remoteDetails.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            _remoteUrl.Text = r.Url ?? "";
            var pin = r.Url == null ? null : r.Pin;
            _remotePin.Text = pin == null ? "" : pin.Substring(0, 3) + " " + pin.Substring(3);
            _remotePin.Visibility = pin == null ? Visibility.Collapsed : Visibility.Visible;
            var seconds = r.PinSeconds;
            _remoteInfo.Text = r.Error
                ?? (pin != null ? "Kod " + seconds / 60 + ":" + (seconds % 60).ToString("00") + " daha geçerli. " : "Cihaz eklemek için yeni kod al. ")
                   + M.S.RemoteDevices.Count + " eşleşme kayıtlı.";
        }

        private DockPanel HotkeyRow(int i)
        {
            var label = Kit.Text(Model.HotkeyNames[i], 14, null, true);
            label.VerticalAlignment = VerticalAlignment.Center;
            var key = Kit.Named(new Button { MinWidth = 170, ToolTip = "Değiştir" }, Model.HotkeyNames[i]);
            var listening = false;
            Action show = () =>
            {
                var value = M.S.Hotkeys[i];
                key.Content = listening ? "Tuşlara bas…"
                    : string.IsNullOrEmpty(value) ? "Yok"
                    : Host.HotkeyFailed(i) ? value + " (kullanımda)" : value;
                AutomationProperties.SetItemStatus(key, key.Content.ToString());
            };
            key.Click += (s, e) => { listening = true; show(); };
            key.LostKeyboardFocus += (s, e) => { listening = false; show(); };
            key.PreviewKeyDown += (s, e) =>
            {
                if (!listening) return;
                var k = e.Key == Key.System ? e.SystemKey : e.Key;
                if (k == Key.Tab) return;
                e.Handled = true;
                string value;
                if (k == Key.Escape) { listening = false; show(); return; }
                if (k == Key.Delete) value = "";
                else
                {
                    value = Hotkeys.Describe(k, Keyboard.Modifiers);
                    if (value == null) return; // only modifiers so far, or a key that would break typing
                }
                listening = false;
                M.S.Hotkeys[i] = value;
                M.SaveNow();
                Host.RegisterHotkeys();
                B.Refresh();
            };
            B.On(show);
            return Kit.Row(label.Margin(0, 0, 10, 0), key).Margin(0, 3, 0, 3);
        }

        private void NewProfile()
        {
            var name = Dialog.Ask(Host, "Yeni profil", "Profil adı", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            var guess = M.Apps.Where(a => a.Channel == Ch.Game).Select(a => a.Exe).FirstOrDefault() ?? "";
            var exe = Dialog.Ask(Host, "Otomatik geçiş", "Oyunun exe adı (boş bırakırsan elle seçilir)", guess);
            if (exe == null) return;
            M.SaveProfile(name.Trim(), exe);
            BuildProfiles();
        }

        private void BuildProfiles()
        {
            _profiles.Children.Clear();
            if (M.S.Profiles.Count == 0)
                _profiles.Children.Add(Kit.Text("Henüz profil yok. Sesi ayarla, sonra profil olarak kaydet.", 13, "Muted", true));
            foreach (var item in M.S.Profiles)
            {
                var p = item;
                var text = Kit.Stack(Kit.Text(p.Name), Kit.Text(string.IsNullOrEmpty(p.Exe) ? "Elle" : p.Exe, 12, "Muted"));
                text.VerticalAlignment = VerticalAlignment.Center;
                _profiles.Children.Add(Kit.Row(text,
                    Kit.Button("Uygula", () => M.ApplyProfile(p), "Quiet"),
                    Kit.IconButton("", p.Name + ": sil", () =>
                    {
                        if (!Dialog.Confirm(Host, "Profili sil", "“" + p.Name + "” silinsin mi?", "Sil")) return;
                        M.DeleteProfile(p);
                        BuildProfiles();
                    })).Margin(0, 2, 0, 2));
            }
        }

        private void Export()
        {
            var d = new SaveFileDialog { Filter = "Duyu ayarları (*.json)|*.json", FileName = "duyu-ayarlar.json" };
            if (d.ShowDialog(Host) != true) return;
            try { M.Export(d.FileName); Host.Toast("Dışa aktarıldı."); }
            catch (Exception e) { Host.Toast("Yazılamadı: " + e.Message); }
        }

        private void Import()
        {
            var d = new OpenFileDialog { Filter = "Duyu ayarları (*.json)|*.json" };
            if (d.ShowDialog(Host) != true) return;
            if (!Dialog.Confirm(Host, "Ayarları değiştir", "Şu anki tüm ayarların bu dosyadakilerle değişecek.", "Değiştir")) return;
            try { M.Import(d.FileName); Host.RegisterHotkeys(); BuildProfiles(); Host.Toast("İçe aktarıldı."); }
            catch (Exception e) { Host.Toast("Okunamadı: " + e.Message); }
        }

        public override void Tick()
        {
            TickRemote();
            var pending = (int)M.Meter(Duyu.M.Pending);
            var trips = (int)M.Meter(Duyu.M.GuardTrips);
            _health.Text = (pending == 0 ? "Onarım bekleyen ses yok." : pending + " ses oturumunun özgün seviyesi kayıtlı. Motor kapanınca geri verilir.")
                + (trips > 0 ? " Patlama koruması " + trips + " kez devreye girdi." : "");
        }
    }
}

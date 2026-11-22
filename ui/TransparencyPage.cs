using System.Windows;
using System.Windows.Controls;

namespace Duyu
{
    public sealed class TransparencyPage : Page
    {
        private static readonly string[] Hints =
        {
            "Odayı olduğu gibi, hafif netlikle duyarsın.",
            "İnsan sesi öne çıkar, uğultu ve tıslama geri çekilir.",
            "Kapı, adım, telefon gibi kısık sesler yükselir. Yüksek sesler sınırlanır."
        };

        private readonly HearingDial _dial = new HearingDial();
        private readonly TextBlock _latency = Kit.Title("", 22);
        private readonly TextBlock _state = Kit.Text("", 13, "Muted", true);

        public TransparencyPage(MainWindow host)
            : base(host, "Transparency", "Kulaklığı çıkarmadan odayı duy. Mikrofonu olan her kurulumda, her kulaklıkla çalışır.")
        {
            _dial.ValueChanged += (s, e) => M.Set(P.TransGain, (float)_dial.Value);
            B.On(() => _dial.Value = M.Get(P.TransGain));

            var power = Kit.Button("", Host.ToggleTransparency, "Primary");
            power.MinHeight = 52;
            B.On(() =>
            {
                var on = M.On(P.TransOn);
                var label = on ? "Transparency'yi kapat" : "Transparency'yi aç";
                power.Content = label;
                power.Style = on ? (Style)Kit.Theme[typeof(Button)] : Kit.Style("Primary");
                System.Windows.Automation.AutomationProperties.SetName(power, label);
            });

            var hint = Kit.Text("", 13, "Muted", true);
            B.On(() => hint.Text = Hints[(int)M.Get(P.TransMode)]);
            var modes = B.Segments("Mod", new[] { "Doğal", "Konuşma", "Farkındalık" },
                () => (int)M.Get(P.TransMode), i => M.Set(P.TransMode, i));

            var hero = Kit.Card(null, null,
                _dial,
                modes.Margin(0, 12, 0, 6),
                hint.Margin(4, 0, 4, 16),
                power,
                _latency.Margin(0, 16, 0, 2),
                _state);

            var warning = Kit.Text("", 13, null, true);
            var note = Kit.Note(warning);
            B.On(() =>
            {
                var w = Warning();
                warning.Text = w ?? "";
                note.Visibility = w == null ? Visibility.Collapsed : Visibility.Visible;
            });

            var devices = Kit.Card("Aygıtlar", "Mikrofon odaya ne kadar açıksa sonuç o kadar doğal olur.",
                B.Device("Odayı dinleyen mikrofon", Role.InTrans, true, "Otomatik"),
                B.Device("Kulaklık", Role.OutMain, false, "Otomatik"),
                note);

            var sound = Kit.Card("Ses karakteri", null,
                B.Slider("Ton", P.TransTone, -1, 1, 0.02, Format.Tone),
                B.Slider("Denge", P.TransBalance, -1, 1, 0.02, Format.Balance),
                B.Slider("Stereo genişlik (stereo mikrofonda)", P.TransWidth, 0, 1, 0.02, Format.Percent),
                B.Slider("Kendi sesimi kıs", P.TransOwnVoice, 0, 1, 0.02, Format.Percent),
                B.Slider("Ortam gürültüsünü azalt (+11 ms)", P.TransDenoise, 0, 1, 0.02, Format.Percent));

            var smart = Kit.Card("Akıllı davranış", null,
                B.Switch("Konuşma algılama", "Biri konuşunca oyunu ve medyayı kısar.", P.TransAware),
                B.Slider("Konuşurken kısma", P.TransDuck, -30, 0, 1, Format.Db),
                B.Switch("Ötme koruması", "Geri besleme frekansını bulup keser.", P.TransGuard).Margin(0, 8, 0, 0),
                B.Switch("Düşük gecikme (ham mikrofon)", "Sürücü efektlerini atlar. Ses kötüleşirse kapat.", P.TransRaw));

            Add(new Columns { Breakpoint = 700, Ratio = 0.48 }.With(hero, Stacked(devices, sound, smart)));
        }

        private static StackPanel Stacked(params FrameworkElement[] cards)
        {
            var s = new StackPanel();
            foreach (var c in cards) s.Children.Add(c.Margin(0, 0, 0, 16));
            return s;
        }

        private string Warning()
        {
            var mic = M.Effective(Role.InTrans);
            var output = M.Effective(Role.OutMain);
            if (mic == null) return "Mikrofon bulunamadı. Transparency için odayı duyacak bir mikrofon gerekli: kulaklık, webcam, USB ya da dizüstü mikrofonu olur.";
            if (output == null) return "Ses çıkışı bulunamadı.";
            if (mic.Virtual) return "Sanal bir mikrofon seçili. Gecikme artar ve gürültü engelleme odayı silebilir. Fiziksel mikrofon seç.";
            if (output.Virtual) return "Çıkış sanal bir aygıt. Gecikmeyi düşük tutmak için kulaklığın kendisini seç.";
            if (mic.Rate > 0 && mic.Rate <= 16000) return "Bu mikrofon telefon kalitesinde (Bluetooth eller serbest olabilir). Kulaklığın müzik kalitesini de düşürür. USB veya webcam mikrofonu önerilir.";
            if (output.Form == 9) return "Çıkış bir monitör veya TV. Hoparlörden çalarsa ses öter. Kulaklık seç.";
            return "Boom mikrofon ağzına odaklıdır, oda kısık kalırsa kazancı artır ya da webcam mikrofonu dene. Hoparlörle kullanma.";
        }

        public override void Tick()
        {
            var want = M.On(P.TransOn);
            var running = want && M.Meter(Duyu.M.TransRunning) > 0.5f;
            _dial.Show(running, M.Meter(Duyu.M.Trans));
            _latency.Text = running ? "≈ " + M.Meter(Duyu.M.TransLatencyMs).ToString("0") + " ms" : "";
            _latency.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

            if (!want) _state.Text = "Mikrofon kapalı. Sen açana kadar dinlenmez, hiçbir zaman kaydedilmez.";
            else if (!running) _state.Text = "Mikrofon açılamadı. Windows Ayarları > Gizlilik > Mikrofon iznini ve aygıt seçimini kontrol et.";
            else if (M.Meter(Duyu.M.Feedback) > 0) _state.Text = "Ötme algılandı ve kesildi. Kazancı biraz düşür.";
            else if (M.Meter(Duyu.M.Speech) > 0.5f) _state.Text = "Konuşma algılandı, diğer sesler kısıldı.";
            else _state.Text = "Tahmini yazılım gecikmesi. Bluetooth kulaklık buna 100–250 ms ekler.";
        }
    }
}

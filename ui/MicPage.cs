using System.Windows;
using System.Windows.Controls;

namespace Duyu
{
    public sealed class MicPage : Page
    {
        private readonly LevelMeter _meter = new LevelMeter("Mic", true);
        private readonly TextBlock _reduction = Kit.Text("", 12, "Muted");
        private readonly Button _test;

        public MicPage(MainWindow host)
            : base(host, "Mikrofon", "Gürültü azaltma, kapı, kompresör, EQ ve ses sabitleme.", PowerSwitch(host))
        {
            _test = Kit.Button("4 sn kaydet ve dinle", M.MicTest, null, "");
            var input = Kit.Card("Giriş", null,
                B.Device("Mikrofon", Role.InMic, "Otomatik"),
                Kit.Text("İşlenmiş seviye", 13, "Muted").Margin(0, 14, 0, 6),
                _meter,
                B.Slider("Kazanç", P.MicGain, -24, 24, 0.5, Format.Db, null, "Mic"),
                B.Slider("Kendini duy", P.Sidetone, 0, 1, 0.02, Format.Percent, null, "Mic"),
                _test.Margin(0, 14, 0, 4),
                Kit.Text("Test kaydı yalnızca bellekte tutulur, diske yazılmaz.", 12, "Muted", true));

            var hint = Kit.Text("", 13, null, true);
            B.On(() => hint.Text = M.Effective(Role.OutMic) != null
                ? "Otomatik bağlanır. Discord, OBS ve oyunda mikrofon olarak CABLE Output seç."
                : "Başka uygulamaların bunu mikrofon olarak görmesi için bir sanal kablo sürücüsü (VB-CABLE) gerekir. Kurulunca Duyu kendiliğinden bağlanır. Duyu sürücü kurmaz.");
            var output = Kit.Card("Discord, OBS ve oyunlara gönder", null,
                B.Device("İşlenmiş mikrofonun çıkışı", Role.OutMic, "Otomatik"),
                Kit.Note(hint));

            var cleanup = Kit.Card("Gürültü", "Yapay zeka değil, klasik sinyal işleme. Sabit gürültüde güçlü, ani ve karmaşık seste sınırlıdır.",
                B.Slider("Arka plan gürültüsü", P.NsAmount, 0, 1, 0.02, Format.Percent, null, "Mic"),
                B.Slider("Darbe sesleri (tuş, tık)", P.ImpactAmount, 0, 1, 0.02, Format.Percent, null, "Mic"));

            var threshold = B.Slider("Eşik", P.GateThreshold, -70, -15, 1, Format.Db, null, "Mic");
            var gate = Kit.Card("Gürültü kapısı", null,
                B.Switch("Kapı", "Konuşmadığında mikrofonu kapatır.", P.GateOn, null, "Mic"),
                B.Switch("Otomatik eşik", null, P.GateAuto, null, "Mic"),
                threshold);
            B.On(() => threshold.IsEnabled = !M.On(P.GateAuto));

            var dynamics = Kit.Card("Dinamik", null,
                B.Switch("Kompresör", "Fısıltı ile bağırma arasını kapatır.", P.CompOn, null, "Mic"),
                B.Slider("Sıkıştırma", P.CompAmount, 0, 1, 0.02, Format.Percent, null, "Mic"),
                _reduction,
                B.Switch("Ses sabitleme", "Ortalama seviyeni aynı tutar.", P.StabilizerOn, null, "Mic").Margin(0, 8, 0, 0));

            var processing = Kit.Stack(cleanup.Margin(0, 0, 0, 16), gate.Margin(0, 0, 0, 16), dynamics);
            Add(new Columns().With(Kit.Stack(input.Margin(0, 0, 0, 16), output), processing));
            Add(new EqCard(host, B, () => Ch.Mic).View);
        }

        private static CheckBox PowerSwitch(MainWindow host)
        {
            var box = Kit.Named(new CheckBox { Style = Kit.Style("Switch"), Content = Kit.Text("Mikrofon işleme"), Tag = Kit.Brush("Mic") }, "Mikrofon işleme");
            System.Action sync = () => box.IsChecked = host.M.On(P.MicOn);
            sync();
            RoutedEventHandler changed = (s, e) => host.M.Set(P.MicOn, box.IsChecked == true ? 1 : 0);
            box.Checked += changed;
            box.Unchecked += changed;
            host.M.Changed += sync;
            return box;
        }

        public override void Tick()
        {
            _meter.Show(M.Meter(Duyu.M.MicL), M.Meter(Duyu.M.MicR));
            _reduction.Text = M.On(P.CompOn) ? "Azaltma: " + M.Meter(Duyu.M.MicReduction).ToString("0.0") + " dB" : "";

            var state = (int)M.Meter(Duyu.M.TestState);
            var label = state == 1 ? "Kaydediliyor, konuş…" : state == 2 ? "Çalınıyor…" : "4 sn kaydet ve dinle";
            var text = (TextBlock)((StackPanel)_test.Content).Children[1];
            if (text.Text != label) text.Text = label;
        }
    }
}

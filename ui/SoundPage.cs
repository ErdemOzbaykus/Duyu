using System;
using System.Windows;
using System.Windows.Controls;

namespace Duyu
{
    public sealed class SoundPage : Page
    {
        private static readonly int[] Tabs = { Ch.Game, Ch.Chat, Ch.Media, Ch.Aux, Ch.Master };
        private readonly RoomMap _room;
        private int _ch = Ch.Game;

        public SoundPage(MainWindow host)
            : base(host, "Ses", "Kanal başına EQ, uzamsal ses, güçlendirme ve akıllı ses.")
        {
            var names = new[] { "Oyun", "Sohbet", "Medya", "Diğer", "Kulaklık" };
            Add(B.Segments("Kanal", names, () => Array.IndexOf(Tabs, _ch), i => _ch = Tabs[i], i => Ch.Keys[Tabs[i]]));

            var off = Kit.Text("Ses motoru kapalı. Yaptığın ayarlar saklanır, motoru açınca duyulur. Mikser sayfasından açabilirsin.", 13, null, true);
            var offNote = Kit.Note(off);
            B.On(() => offNote.Visibility = M.On(P.MixerOn) ? Visibility.Collapsed : Visibility.Visible);
            Add(offNote);

            Add(new EqCard(host, B, () => _ch).View);

            Func<int> ch = () => _ch;
            var level = Kit.Card("Seviye", null,
                B.Slider("Ses güçlendirme", P.Boost, 0, 12, 0.5, Format.Db, ch),
                B.Switch("Akıllı ses", "Kısık sesleri yükseltir, patlamaları indirir.", P.SmartOn, ch).Margin(0, 10, 0, 0),
                B.Segments("Akıllı ses hedefi", new[] { "Sessiz", "Dengeli", "Yüksek" },
                    () => M.Get(P.SmartTarget, _ch) < -23 ? 0 : M.Get(P.SmartTarget, _ch) > -18 ? 2 : 1,
                    i => M.Set(P.SmartTarget, new[] { -26f, -20f, -16f }[i], _ch)),
                B.Slider("Akıllı ses gücü", P.SmartAmount, 0, 1, 0.05, Format.Percent, ch));

            var clean = Kit.Card("Gelen ses temizleme", "Karşı tarafın fan ve klavye uğultusunu azaltır. +11 ms.",
                B.Switch("Gürültü azaltma", null, P.DenoiseOn, ch),
                B.Slider("Güç", P.DenoiseAmount, 0, 1, 0.05, Format.Percent, ch));

            _room = new RoomMap(M);
            B.On(_room.InvalidateVisual);
            var test = Kit.Button("", () => M.Toggle(P.SpatialTest), null);
            B.On(() =>
            {
                test.Content = M.On(P.SpatialTest) ? "Testi durdur" : "Yön testi çal";
                System.Windows.Automation.AutomationProperties.SetName(test, (string)test.Content);
            });
            var reset = Kit.Button("Yerleşimi sıfırla", () =>
            {
                var def = new float[] { -30, 30, 0, 0, -145, 145, -90, 90 };
                for (var i = 0; i < def.Length; i++) M.Set(P.SpeakerAngle, def[i], i);
            }, "Quiet");

            var spatial = Kit.Card("Uzamsal ses",
                "7.1 sesi kulaklıkta yöne çevirir. Gerçek 7.1 için oyunun çıkış aygıtı 8 kanallı olmalı, stereo kaynakta sanal hoparlör olur.",
                B.Switch("Bu kanalda açık", null, P.SpatialOn, ch),
                B.Slider("Performans ↔ Sinematik", P.Immersion, 0, 1, 0.05, Format.Percent, ch),
                B.Slider("Mesafe", P.Distance, 0, 1, 0.05, Format.Percent, ch),
                _room.Margin(0, 10, 0, 10),
                Kit.Wrap(test, reset));

            var perChannel = new Columns().With(spatial, Kit.Stack(level.Margin(0, 0, 0, 16), clean));
            B.On(() => perChannel.Visibility = _ch == Ch.Master ? Visibility.Collapsed : Visibility.Visible);
            Add(perChannel);

            var about = Kit.Card("Kulaklık düzeltmesi",
                "Bu EQ kulaklığa giden her şeye, kendini duyma dahil, en son uygulanır. Kulaklığının AutoEq “ParametricEQ.txt” dosyasını “Dosyadan” ile yükleyebilirsin.",
                B.Slider("Güvenlik tavanı", P.Ceiling, -24, -0.5, 0.5, Format.Db));
            B.On(() => about.Visibility = _ch == Ch.Master ? Visibility.Visible : Visibility.Collapsed);
            Add(about);
        }

        public override void Tick() { _room.Show((int)M.Meter(Duyu.M.Speaker)); }
        public override void Leave() { M.Set(P.SpatialTest, 0); }
    }
}

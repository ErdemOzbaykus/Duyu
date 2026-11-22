using System;
using System.Linq;

namespace Duyu
{
    /// Data for --preview and --shots. Worst-case mode feeds long names and crowded lists.
    internal static class Demo
    {
        public static bool WorstCase;
        private static double _t;

        public static void Fill(Model m)
        {
            var longName = WorstCase ? " (Ultra Uzun İsimli Kablosuz Oyuncu Kulaklığı Pro Max Edition 7.1 Surround)" : "";
            m.Devices.Add(new Device { Id = "o1", Name = "Kulaklık (USB Audio)" + longName, Form = 3, Default = true, Rate = 48000 });
            m.Devices.Add(new Device { Id = "o2", Name = "Monitör (HDMI)", Form = 9, Rate = 48000 });
            m.Devices.Add(new Device { Id = "i1", Name = "Mikrofon (USB Audio)" + longName, Input = true, Form = 4, Default = true, Rate = WorstCase ? 16000 : 48000 });

            var exes = WorstCase
                ? Enumerable.Range(0, 22).Select(i => i + "_cok_uzun_bir_uygulama_adi_win64_shipping_client_launcher.exe").ToArray()
                : new[] { "cs2.exe", "discord.exe", "spotify.exe", "chrome.exe", "steam.exe" };
            for (var i = 0; i < exes.Length; i++)
                m.Apps.Add(new AppRow
                {
                    Exe = exes[i], Pid = 100 + i, Active = i < 3,
                    Channel = new[] { 0, 1, 2, 2, 3 }[i % 5],
                    State = i % 5 == 4 ? AppState.SharedTree : AppState.Captured
                });

            Action<P, int, float> put = (p, i, v) => m.S.Values[(int)p][i] = v;
            foreach (var p in new[] { P.MixerOn, P.MicOn, P.StreamerOn, P.ChatMixOn, P.CompOn }) put(p, 0, 1);
            put(P.Vol, Ch.Chat, 0.72f); put(P.Vol, Ch.Media, 0.44f); put(P.StreamVol, Ch.Media, 0.2f);
            put(P.ChatMix, 0, 0.15f); put(P.SpatialOn, 0, 1); put(P.Sidetone, 0, 0.2f);

            var game = Presets.BuiltIn.First(p => p.Name == "Adım sesleri");
            m.S.Bands[0] = game.Bands.Select(b => (float[])b.Clone()).ToArray();
            m.S.PresetNames[0] = game.Name;
            put(P.Preamp, 0, game.Preamp);
        }

        public static void Animate(Model m)
        {
            _t += 0.033;
            Func<double, double, float> wave = (speed, size) => (float)(size * (0.55 + 0.45 * Math.Abs(Math.Sin(_t * speed + speed))));
            for (var i = 0; i < 14; i++) m.Meters[i] = wave(1.3 + i * 0.4, 0.5 / (1 + i % 5 * 0.4));
            m.Meters[(int)M.MicReduction] = 3.5f;
            m.Meters[(int)M.Captured] = m.Apps.Count(a => a.State == AppState.Captured);
            m.Meters[(int)M.Speaker] = -1;
            foreach (var x in new[] { M.MixerRunning, M.MainRunning, M.MicRunning }) m.Meters[(int)x] = 1;
        }
    }
}

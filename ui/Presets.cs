using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Duyu
{
    public sealed class Preset
    {
        public string Name { get; set; }
        public string Group { get; set; }
        public float Preamp { get; set; }
        public float[][] Bands { get; set; } // [on, type, hz, gain, q]
    }

    public static class Presets
    {
        public const int Bands = 10;
        public static readonly float[] Hz = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

        public static float[][] Flat()
        {
            return Hz.Select(f => new[] { 1f, 0f, f, 0f, 1.2f }).ToArray();
        }

        private static Preset Make(string group, string name, params float[] gains)
        {
            var bands = Flat();
            for (var i = 0; i < Bands; i++) bands[i][3] = gains[i];
            var top = gains.Max();
            return new Preset { Name = name, Group = group, Preamp = top > 0 ? (float)Math.Round(-top * 0.8, 1) : 0, Bands = bands };
        }

        private const string G = "Oyun", S = "Sohbet", Md = "Medya", Mi = "Mikrofon", K = "Kulaklık";

        //                                        31    62   125   250   500    1k    2k    4k    8k   16k
        public static readonly List<Preset> BuiltIn = new List<Preset>
        {
            Make("Genel", "Düz",                   0,    0,    0,    0,    0,    0,    0,    0,    0,    0),
            Make("Genel", "Bas güçlü",             6,    5,    3,    1,    0,    0,    0,    0,    0,    0),
            Make("Genel", "Bas kısık",            -8,   -6,   -3,   -1,    0,    0,    0,    0,    0,    0),
            Make("Genel", "Dengeli V",             4,    3,    1,   -1,   -2,   -1,    1,    3,    3,    2),
            Make("Genel", "Düşük ses",             5,    4,    2,    0,   -1,    0,    1,    2,    3,    3),
            Make("Genel", "Parlak",                0,    0,    0,    0,    0,    1,    2,    3,    4,    4),
            Make("Genel", "Sıcak",                 2,    3,    3,    2,    1,    0,   -1,   -2,   -2,   -2),
            Make("Genel", "Yorgunluk azaltma",     0,    1,    1,    0,    0,    0,   -2,   -4,   -3,   -1),
            Make(G, "Adım sesleri",               -7,   -5,   -1,    2,    1,    0,    3,    6,    3,   -1),
            Make(G, "Adım, yüksek zemin",         -8,   -6,   -3,    0,    1,    1,    4,    5,    5,    0),
            Make(G, "Adım, toprak ve çim",        -6,   -3,    2,    4,    1,    0,    2,    3,    0,   -2),
            Make(G, "Taktik FPS",                 -6,   -4,   -2,    0,    1,    2,    4,    4,    2,    0),
            Make(G, "Arena FPS",                  -4,   -3,    0,    1,    0,    1,    3,    4,    3,    1),
            Make(G, "Battle royale",              -5,   -4,   -1,    1,    1,    2,    3,    5,    2,   -1),
            Make(G, "Extraction shooter",         -5,   -3,    1,    2,    2,    1,    2,    4,    2,    0),
            Make(G, "Silah sesi yumuşatma",       -3,   -4,   -3,   -1,    0,    0,    1,    0,   -3,   -2),
            Make(G, "Uzak detay",                 -4,   -3,   -2,   -1,    0,    2,    4,    4,    3,    2),
            Make(G, "Sızma ve gizlilik",          -2,   -1,    1,    2,    1,    1,    3,    3,    2,    0),
            Make(G, "MOBA",                        1,    2,    1,   -1,    0,    2,    3,    2,    1,    0),
            Make(G, "MMO",                         2,    2,    1,    0,    0,    1,    2,    1,    1,    1),
            Make(G, "RPG ve hikaye",               3,    3,    1,    0,    0,    1,    2,    1,    2,    2),
            Make(G, "Açık dünya",                  3,    2,    0,   -1,    0,    1,    1,    2,    3,    3),
            Make(G, "Korku",                       4,    3,    1,    0,    0,    0,    2,    3,    4,    2),
            Make(G, "Yarış",                       5,    4,    2,    1,   -1,    0,    1,    2,    1,    0),
            Make(G, "Simülasyon",                  2,    3,    2,    0,    0,    1,    1,    1,    0,    0),
            Make(G, "Spor",                        1,    2,    1,    0,    1,    3,    3,    2,    0,    0),
            Make(G, "Dövüş",                       4,    3,    1,    0,   -1,    1,    2,    3,    2,    0),
            Make(G, "Strateji",                    1,    1,    0,    0,    1,    2,    2,    1,    0,    0),
            Make(G, "Retro",                      -2,    0,    2,    2,    1,    1,    0,   -1,   -2,   -4),
            Make(G, "Ritim",                       3,    4,    2,    0,    0,    0,    2,    4,    3,    2),
            Make(S, "Net konuşma",                -8,   -6,   -3,   -1,    1,    3,    5,    3,    0,   -2),
            Make(S, "Kalın ses",                  -6,   -3,    2,    3,    1,    1,    2,    1,    0,   -2),
            Make(S, "Tıslama azaltma",            -4,   -2,    0,    0,    1,    2,    1,   -3,   -6,   -4),
            Make(S, "Kötü mikrofon kurtarma",     -9,   -7,   -2,   -3,    0,    2,    3,    1,   -3,   -6),
            Make(S, "Telsiz",                    -18,  -16,   -9,   -2,    2,    5,    4,    0,  -10,  -18),
            Make(Md, "Pop",                        2,    3,    1,    0,   -1,    0,    2,    2,    3,    2),
            Make(Md, "Rock",                       4,    3,    1,   -1,   -2,    0,    2,    3,    3,    2),
            Make(Md, "Hip hop",                    6,    5,    2,    1,   -1,   -1,    1,    1,    2,    2),
            Make(Md, "Elektronik",                 6,    4,    1,   -1,   -2,    0,    1,    2,    4,    4),
            Make(Md, "Caz",                        2,    2,    1,    1,   -1,   -1,    0,    1,    2,    3),
            Make(Md, "Klasik",                     1,    1,    0,    0,    0,    0,    0,    1,    2,    3),
            Make(Md, "Metal",                      4,    3,    0,   -2,   -3,    0,    3,    4,    2,    1),
            Make(Md, "Akustik",                    1,    2,    2,    1,    0,    1,    2,    2,    2,    1),
            Make(Md, "Vokal",                     -3,   -2,   -1,    1,    2,    3,    3,    2,    0,   -1),
            Make(Md, "Film",                       5,    4,    1,    0,    0,    2,    3,    2,    2,    1),
            Make(Md, "Podcast",                   -5,   -3,    0,    1,    1,    2,    3,    1,   -1,   -2),
            Make(Md, "Gece",                      -6,   -4,   -1,    0,    1,    2,    2,    0,   -2,   -3),
            Make(Mi, "Yayıncı",                   -4,    1,    3,   -1,   -2,    0,    2,    3,    1,    0),
            Make(Mi, "Radyo",                     -2,    3,    4,    0,   -2,   -1,    2,    3,    2,    0),
            Make(Mi, "Temiz ve net",              -9,   -5,   -1,   -2,    0,    1,    3,    3,    1,    0),
            Make(Mi, "Boom mikrofon",             -6,    2,    4,    1,   -2,   -1,    1,    2,    2,    1),
            Make(Mi, "Dizüstü mikrofonu",         -9,   -4,    2,    2,   -1,   -2,    1,    2,   -1,   -3),
            Make(Mi, "Patlama azaltma",          -12,   -8,   -3,    0,    0,    1,    2,    2,    0,   -2),
            Make(Mi, "Sert S yumuşatma",          -4,    0,    1,    0,    0,    1,    1,   -2,   -6,   -3),
            Make(K, "Boğuk kulaklık",             -1,   -2,   -3,   -3,   -1,    0,    2,    3,    2,    1),
            Make(K, "Cırtlak kulaklık",            2,    2,    1,    0,    0,    0,   -2,   -4,   -4,   -1),
            Make(K, "Bassız kulaklık",             7,    5,    3,    1,    0,    0,    0,    0,    0,    0),
            Make(K, "Kulak içi",                   2,    1,    0,    0,    0,    1,    1,   -2,   -2,    1),
            Make(K, "Açık arka",                   4,    3,    1,    0,    0,    0,   -1,   -1,    0,    0),
        };

        private static readonly Regex Filter = new Regex(
            @"^Filter\s*\d*\s*:\s*ON\s+(PK|PEQ|LS\w*|HS\w*|LP\w*|HP\w*|NO)\s+Fc\s+([\d.,]+)\s*Hz(?:\s+Gain\s+([-+\d.,]+)\s*dB)?(?:\s+Q\s+([\d.,]+))?",
            RegexOptions.IgnoreCase);
        private static readonly Regex PreampLine = new Regex(@"^Preamp:\s*([-+\d.,]+)\s*dB", RegexOptions.IgnoreCase);

        private static float Num(string s, float fallback)
        {
            float v;
            return float.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        /// Reads Equalizer APO / AutoEq "ParametricEQ.txt". Returns null when nothing usable is found.
        public static Preset ParseApo(string text, string name, out int dropped)
        {
            dropped = 0;
            var preset = new Preset { Name = name, Group = "Benim", Bands = Flat() };
            foreach (var b in preset.Bands) b[0] = 0;
            var n = 0;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                var pre = PreampLine.Match(line);
                if (pre.Success) { preset.Preamp = Num(pre.Groups[1].Value, 0); continue; }
                var m = Filter.Match(line);
                if (!m.Success) continue;
                if (n == Bands) { dropped++; continue; }
                var t = m.Groups[1].Value.ToUpperInvariant();
                var type = t.StartsWith("LS") ? 1 : t.StartsWith("HS") ? 2 : t.StartsWith("LP") ? 3 : t.StartsWith("HP") ? 4 : t == "NO" ? 5 : 0;
                preset.Bands[n++] = new[] { 1f, type, Num(m.Groups[2].Value, 1000), Num(m.Groups[3].Value, 0), Num(m.Groups[4].Value, 0.707f) };
            }
            return n == 0 ? null : preset;
        }

        public static bool Valid(float[][] bands)
        {
            return bands != null && bands.Length == Bands
                && bands.All(b => b != null && b.Length == 5 && b.All(v => !float.IsNaN(v) && !float.IsInfinity(v)) && b[2] > 0 && b[4] > 0);
        }
    }
}

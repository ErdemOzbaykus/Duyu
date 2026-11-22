using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Duyu
{
    public sealed class Profile
    {
        public string Name { get; set; }
        public string Exe { get; set; }
        public float[][] Values { get; set; }
        public float[][][] Bands { get; set; }
        public string[] PresetNames { get; set; }
    }

    public sealed class Settings
    {
        public float[][] Values { get; set; }
        public float[][][] Bands { get; set; }
        public string[] PresetNames { get; set; }
        public string[] Devices { get; set; }
        public Dictionary<string, int> Assign { get; set; }
        public List<Preset> UserPresets { get; set; }
        public List<string> Favorites { get; set; }
        public List<Profile> Profiles { get; set; }
        public string[] Hotkeys { get; set; }
        public bool CloseToTray { get; set; }
        public bool Resume { get; set; }
        public bool SeenMixerInfo { get; set; }
        public float SidetoneBefore { get; set; }
        public bool RemoteOn { get; set; }
        public List<string> RemoteDevices { get; set; } // hashes of paired device keys
    }

    public sealed class Device
    {
        public string Id, Name;
        public bool Input, Virtual, Default;
        public int Form, Rate;
    }

    public enum AppState { Idle, Captured, Direct, Starting, SharedTree, Error }

    public sealed class AppRow
    {
        public string Exe;
        public int Pid, Channel;
        public AppState State;
        public bool Active;
    }

    public sealed class Model : IDisposable
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
        private const string RestoreName = "DuyuRestore";

        // What a game profile captures: the sound, never power switches or devices.
        private static readonly P[] Sound =
        {
            P.Vol, P.Mute, P.StreamVol, P.StreamMute, P.Boost, P.Bass, P.Voice, P.Treble, P.EqOn, P.Preamp,
            P.SmartOn, P.SmartAmount, P.SmartTarget, P.DenoiseOn, P.DenoiseAmount, P.SpatialOn, P.Immersion,
            P.Distance, P.SpeakerAngle, P.ChatMixOn, P.ChatMix
        };

        public static readonly string[] HotkeyNames =
            { "Mikrofonu sustur", "Tüm sesi sustur", "ChatMix oyuna", "ChatMix sohbete" };
        private static readonly string[] HotkeyDefaults =
            { "Ctrl+Shift+F11", "Ctrl+Shift+F12", "Ctrl+Shift+F7", "Ctrl+Shift+F8" };

        private readonly string _dir, _file;
        private readonly DispatcherTimer _save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private IntPtr _engine;
        private Process _guard;
        private bool? _restoreArmed;
        private Profile _before, _active;
        private string _deviceText, _appText;

        public readonly bool Preview;   // screenshots and UI tests: no engine, no disk, no registry
        public readonly bool Sandboxed; // smoke tests: real engine, no registry or watchdog
        public Settings S;
        public List<Device> Devices = new List<Device>();
        public List<AppRow> Apps = new List<AppRow>();
        public readonly float[] Meters = new float[(int)M.COUNT];

        public event Action Changed = delegate { };
        public event Action DevicesChanged = delegate { };
        public event Action AppsChanged = delegate { };
        public event Action<string> Notice = delegate { };

        public Model(string dir, bool preview, bool sandboxed)
        {
            Preview = preview;
            Sandboxed = sandboxed;
            _dir = dir;
            _file = Path.Combine(dir, "settings.json");
            S = Load();
            _save.Tick += (s, e) => { _save.Stop(); SaveNow(); };
            if (preview) { Demo.Fill(this); return; }

            Directory.CreateDirectory(dir);
            _engine = Native.duyu_create(dir, sandboxed ? (uint)Process.GetCurrentProcess().Id : 0);
            if (_engine == IntPtr.Zero) throw new InvalidOperationException("Ses motoru başlatılamadı.");

            Put(P.SpatialTest, 0, 0);
            if (!S.Resume) { Put(P.MixerOn, 0, 0); Put(P.MicOn, 0, 0); }
            PushAll();
            UpdateProtection();
        }

        // ------------------------------------------------------------ settings

        private static float[][] DefaultValues()
        {
            var v = Enumerable.Range(0, (int)P.COUNT).Select(i => new float[8]).ToArray();
            Action<P, float> all = (p, x) => { for (var i = 0; i < Ch.Count; i++) v[(int)p][i] = x; };
            Action<P, float> one = (p, x) => v[(int)p][0] = x;
            all(P.Vol, 1); all(P.StreamVol, 1); all(P.EqOn, 1);
            all(P.SmartAmount, 0.6f); all(P.SmartTarget, -20); all(P.DenoiseAmount, 0.6f);
            all(P.Immersion, 0.35f); all(P.Distance, 0.3f);
            new float[] { -30, 30, 0, 0, -145, 145, -90, 90 }.CopyTo(v[(int)P.SpeakerAngle], 0);
            one(P.MasterVol, 1); one(P.Ceiling, -1);
            one(P.NsAmount, 0.5f); one(P.GateAuto, 1); one(P.GateThreshold, -45); one(P.CompAmount, 0.4f);
            return v;
        }

        private static Settings Defaults()
        {
            return new Settings
            {
                Values = DefaultValues(),
                Bands = Enumerable.Range(0, Ch.Count).Select(i => Presets.Flat()).ToArray(),
                PresetNames = Enumerable.Repeat("Düz", Ch.Count).ToArray(),
                Devices = Enumerable.Repeat("", (int)Role.COUNT).ToArray(),
                Assign = new Dictionary<string, int>(),
                UserPresets = new List<Preset>(),
                Favorites = new List<string>(),
                Profiles = new List<Profile>(),
                RemoteDevices = new List<string>(),
                Hotkeys = (string[])HotkeyDefaults.Clone(),
                CloseToTray = true,
                Resume = true
            };
        }

        // Anything missing or malformed falls back field by field.
        private static Settings Sanitize(Settings s)
        {
            var d = Defaults();
            if (s == null) return d;
            s.Values = MergeValues(s.Values, d.Values);
            if (s.Bands == null || s.Bands.Length != Ch.Count || !s.Bands.All(Presets.Valid)) s.Bands = d.Bands;
            if (s.PresetNames == null || s.PresetNames.Length != Ch.Count) s.PresetNames = d.PresetNames;
            // Files saved before transparency was removed: one more device role, two more hotkeys in front.
            if (s.Devices != null && s.Devices.Length == d.Devices.Length + 1) s.Devices = s.Devices.Take(d.Devices.Length).ToArray();
            if (s.Hotkeys != null && s.Hotkeys.Length == d.Hotkeys.Length + 2) s.Hotkeys = s.Hotkeys.Skip(2).ToArray();
            if (s.Devices == null || s.Devices.Length != (int)Role.COUNT) s.Devices = d.Devices;
            if (s.Hotkeys == null || s.Hotkeys.Length != HotkeyNames.Length) s.Hotkeys = d.Hotkeys;
            s.Assign = s.Assign ?? d.Assign;
            s.Favorites = s.Favorites ?? d.Favorites;
            s.RemoteDevices = (s.RemoteDevices ?? d.RemoteDevices).Where(x => x != null).ToList();
            if (!(s.SidetoneBefore > 0 && s.SidetoneBefore <= 1)) s.SidetoneBefore = 0;
            s.UserPresets = (s.UserPresets ?? d.UserPresets).Where(p => p != null && p.Name != null && Presets.Valid(p.Bands)).ToList();
            s.Profiles = (s.Profiles ?? d.Profiles).Where(p => p != null && p.Name != null && p.Values != null
                && p.Bands != null && p.Bands.Length == Ch.Count && p.Bands.All(Presets.Valid)
                && p.PresetNames != null && p.PresetNames.Length == Ch.Count).ToList();
            foreach (var p in s.Profiles) p.Values = MergeValues(p.Values, d.Values);
            return s;
        }

        private static float[][] MergeValues(float[][] from, float[][] defaults)
        {
            var to = defaults.Select(r => (float[])r.Clone()).ToArray();
            if (from == null) return to;
            for (var i = 0; i < Math.Min(from.Length, to.Length); i++)
            {
                if (from[i] == null || from[i].Length != 8) continue;
                for (var j = 0; j < 8; j++)
                    if (!float.IsNaN(from[i][j]) && !float.IsInfinity(from[i][j])) to[i][j] = from[i][j];
            }
            return to;
        }

        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }; }

        private Settings Load()
        {
            if (Preview || !File.Exists(_file)) return Defaults();
            try { return Sanitize(Json().Deserialize<Settings>(File.ReadAllText(_file))); }
            catch (Exception)
            {
                try { File.Copy(_file, _file + ".bozuk", true); } catch (IOException) { }
                return Defaults();
            }
        }

        public void SaveNow()
        {
            if (Preview) return;
            try
            {
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, Json().Serialize(S), new UTF8Encoding(false));
                if (File.Exists(_file)) File.Replace(tmp, _file, null); else File.Move(tmp, _file);
            }
            catch (Exception e) { Notice("Ayarlar kaydedilemedi: " + e.Message); }
        }

        private void Touch()
        {
            Changed();
            if (Preview) return;
            _save.Stop();
            _save.Start();
        }

        public void Export(string path)
        {
            var copy = Json().Deserialize<Settings>(Json().Serialize(S));
            copy.RemoteOn = false;
            copy.RemoteDevices = new List<string>();
            File.WriteAllText(path, Json().Serialize(copy), new UTF8Encoding(false));
        }

        public void Import(string path)
        {
            var incoming = Sanitize(Json().Deserialize<Settings>(File.ReadAllText(path)));
            // An imported file must not be able to switch a microphone on.
            foreach (var p in new[] { P.MixerOn, P.MicOn, P.SpatialTest })
                incoming.Values[(int)p][0] = S.Values[(int)p][0];
            // Nor may it pair a device or open the network port.
            incoming.RemoteOn = S.RemoteOn;
            incoming.RemoteDevices = S.RemoteDevices;
            S = incoming;
            PushAll();
            Touch();
        }

        // ------------------------------------------------------------ values

        public float Get(P p, int i = 0) { return S.Values[(int)p][i]; }
        public bool On(P p, int i = 0) { return Get(p, i) > 0.5f; }

        private void Put(P p, int i, float v)
        {
            S.Values[(int)p][i] = v;
            if (_engine != IntPtr.Zero) Native.duyu_set(_engine, (int)p, i, v);
        }

        public void Set(P p, float v, int i = 0)
        {
            if (Get(p, i) == v) return;
            Put(p, i, v);
            if (p == P.MixerOn) UpdateProtection();
            Touch();
        }

        public void Toggle(P p, int i = 0) { Set(p, On(p, i) ? 0 : 1, i); }

        public const float MixStep = 0.1f;

        public void NudgeMix(float by)
        {
            Set(P.ChatMixOn, 1);
            Set(P.ChatMix, Math.Max(-1, Math.Min(1, Get(P.ChatMix) + by)));
        }

        public void ToggleSidetone()
        {
            var now = Get(P.Sidetone);
            if (now > 0) S.SidetoneBefore = now;
            Set(P.Sidetone, now > 0 ? 0 : S.SidetoneBefore > 0 ? S.SidetoneBefore : 0.25f);
        }

        /// EQ shape shown on the desktop graph and on the phone.
        public double CurveDb(int ch, float hz)
        {
            double sum = 0; // pre-amp is a level, not part of the shape
            foreach (var b in S.Bands[ch])
                if (b[0] > 0.5f && (b[1] >= 3 || b[3] != 0))
                    sum += Native.duyu_filter_db((int)b[1], b[2], b[3], b[4], hz);
            float lo = Get(P.Bass, ch), mid = Get(P.Voice, ch), hi = Get(P.Treble, ch);
            if (lo != 0) sum += Native.duyu_filter_db(1, 120, lo, 0.707f, hz);
            if (mid != 0) sum += Native.duyu_filter_db(0, 2200, mid, 0.8f, hz);
            if (hi != 0) sum += Native.duyu_filter_db(2, 6500, hi, 0.707f, hz);
            return sum;
        }

        public void SetBand(int ch, int band, float[] v)
        {
            S.Bands[ch][band] = v;
            S.PresetNames[ch] = "Özel";
            PushBand(ch, band);
            Touch();
        }

        private void PushBand(int ch, int band)
        {
            var b = S.Bands[ch][band];
            if (_engine != IntPtr.Zero) Native.duyu_set_band(_engine, ch, band, b[0], b[1], b[2], b[3], b[4]);
        }

        private void PushAll()
        {
            for (var p = 0; p < (int)P.COUNT; p++)
                for (var i = 0; i < 8; i++) Put((P)p, i, S.Values[p][i]);
            for (var c = 0; c < Ch.Count; c++)
                for (var b = 0; b < Presets.Bands; b++) PushBand(c, b);
            if (_engine == IntPtr.Zero) return;
            for (var r = 0; r < (int)Role.COUNT; r++) Native.duyu_set_device(_engine, r, S.Devices[r] ?? "");
            foreach (var kv in S.Assign) Native.duyu_assign(_engine, kv.Key, kv.Value);
        }

        public IEnumerable<Preset> AllPresets { get { return S.UserPresets.Concat(Presets.BuiltIn); } }

        public void ApplyPreset(int ch, Preset p)
        {
            S.Bands[ch] = p.Bands.Select(b => (float[])b.Clone()).ToArray();
            S.PresetNames[ch] = p.Name;
            Put(P.Preamp, ch, p.Preamp);
            Put(P.EqOn, ch, 1);
            for (var b = 0; b < Presets.Bands; b++) PushBand(ch, b);
            Touch();
        }

        public void AddPreset(int ch, string name)
        {
            S.UserPresets.RemoveAll(p => p.Name == name);
            S.UserPresets.Insert(0, new Preset
            {
                Name = name, Group = "Benim", Preamp = Get(P.Preamp, ch),
                Bands = S.Bands[ch].Select(b => (float[])b.Clone()).ToArray()
            });
            S.PresetNames[ch] = name;
            Touch();
        }

        public string ShareCode(int ch)
        {
            var p = new Preset { Name = S.PresetNames[ch], Group = "Benim", Preamp = Get(P.Preamp, ch), Bands = S.Bands[ch] };
            return "DUYU1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Json().Serialize(p)));
        }

        /// Accepts a Duyu share code or Equalizer APO text.
        public bool ImportPreset(int ch, string text, string name)
        {
            Preset p = null;
            var dropped = 0;
            var t = (text ?? "").Trim();
            if (t.StartsWith("DUYU1:") && t.Length < 20000)
            {
                try { p = Json().Deserialize<Preset>(Encoding.UTF8.GetString(Convert.FromBase64String(t.Substring(6)))); }
                catch (Exception) { p = null; } // not a valid code: reported below
                if (p != null && !Presets.Valid(p.Bands)) p = null;
            }
            else p = Presets.ParseApo(t, name, out dropped);

            if (p == null) { Notice("Tanınmadı. Duyu kodu veya Equalizer APO / AutoEq metni gerekli."); return false; }
            p.Group = "Benim";
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? "İçe aktarılan" : p.Name;
            S.UserPresets.RemoveAll(x => x.Name == p.Name);
            S.UserPresets.Insert(0, p);
            ApplyPreset(ch, p);
            Notice(dropped > 0 ? "İçe aktarıldı. 10 bandı aşan " + dropped + " filtre alınmadı." : "İçe aktarıldı: " + p.Name);
            return true;
        }

        public void SetDevice(Role r, string id)
        {
            S.Devices[(int)r] = id ?? "";
            if (_engine != IntPtr.Zero) Native.duyu_set_device(_engine, (int)r, id ?? "");
            Touch();
        }

        public void Assign(string exe, int ch)
        {
            if (ch < -1) S.Assign.Remove(exe); else S.Assign[exe] = ch;
            if (_engine != IntPtr.Zero) Native.duyu_assign(_engine, exe, ch);
            foreach (var a in Apps.Where(a => a.Exe == exe && ch >= -1)) a.Channel = ch;
            Touch();
            AppsChanged();
        }

        public void MicTest() { if (_engine != IntPtr.Zero) Native.duyu_mic_test(_engine); }

        // ------------------------------------------------------------ profiles

        private Profile Snapshot(string name, string exe)
        {
            return new Profile
            {
                Name = name, Exe = exe,
                Values = S.Values.Select(r => (float[])r.Clone()).ToArray(),
                Bands = S.Bands.Select(c => c.Select(b => (float[])b.Clone()).ToArray()).ToArray(),
                PresetNames = (string[])S.PresetNames.Clone()
            };
        }

        public void SaveProfile(string name, string exe)
        {
            exe = (exe ?? "").Trim().ToLowerInvariant();
            if (exe.Length > 0 && !exe.EndsWith(".exe")) exe += ".exe";
            S.Profiles.RemoveAll(p => p.Name == name);
            S.Profiles.Add(Snapshot(name, exe));
            Touch();
        }

        public void ApplyProfile(Profile p)
        {
            foreach (var id in Sound)
                for (var i = 0; i < 8; i++)
                    if (i != Ch.Mic || id == P.SpeakerAngle) Put(id, i, p.Values[(int)id][i]); // the microphone is not a game setting
            for (var c = 0; c < Ch.Mic; c++)
            {
                S.Bands[c] = p.Bands[c].Select(b => (float[])b.Clone()).ToArray();
                S.PresetNames[c] = p.PresetNames[c];
                for (var b = 0; b < Presets.Bands; b++) PushBand(c, b);
            }
            Touch();
        }

        public void DeleteProfile(Profile p)
        {
            if (_active == p) { _active = null; _before = null; }
            S.Profiles.Remove(p);
            Touch();
        }

        /// Switches when a profiled game comes to the front, reverts when it exits (not on alt-tab).
        public void AutoSwitch()
        {
            if (Preview || S.Profiles.All(p => string.IsNullOrEmpty(p.Exe))) return;
            if (_active != null && !IsRunning(_active.Exe))
            {
                ApplyProfile(_before);
                Notice("Önceki ayarlara dönüldü.");
                _active = _before = null;
            }
            uint pid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            string exe;
            try { using (var proc = Process.GetProcessById((int)pid)) exe = proc.ProcessName.ToLowerInvariant() + ".exe"; }
            catch (Exception) { return; } // the window closed under us
            var hit = S.Profiles.FirstOrDefault(p => p.Exe == exe);
            if (hit == null || hit == _active) return;
            if (_before == null) _before = Snapshot("", "");
            _active = hit;
            ApplyProfile(hit);
            Notice("Profil: " + hit.Name);
        }

        private static bool IsRunning(string exe)
        {
            var found = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            foreach (var p in found) p.Dispose();
            return found.Length > 0;
        }

        // ------------------------------------------------------------ engine polling

        public void PollMeters()
        {
            if (Preview) Demo.Animate(this);
            else if (_engine != IntPtr.Zero) Native.duyu_meters(_engine, Meters, Meters.Length);
        }

        public float Meter(M m) { return Meters[(int)m]; }

        public void PollLists()
        {
            if (_engine == IntPtr.Zero) return;
            var d = Native.Devices(_engine);
            if (d != _deviceText)
            {
                _deviceText = d;
                Devices = Rows(d, 7).Select(c => new Device
                {
                    Input = c[0] == "1", Id = c[1], Name = c[2], Form = int.Parse(c[3]),
                    Virtual = c[4] == "1", Default = c[5] == "1", Rate = int.Parse(c[6])
                }).ToList();
                DevicesChanged();
            }
            var a = Native.Sessions(_engine);
            if (a != _appText)
            {
                _appText = a;
                Apps = Rows(a, 5).Select(c => new AppRow
                {
                    Exe = c[0], Pid = int.Parse(c[1]), Channel = int.Parse(c[2]),
                    State = (AppState)int.Parse(c[3]), Active = c[4] == "1"
                }).GroupBy(r => r.Exe).Select(g => g.OrderByDescending(r => r.Active).First())
                  .OrderByDescending(r => r.Active).ThenBy(r => r.Exe).ToList();
                AppsChanged();
            }
        }

        private static IEnumerable<string[]> Rows(string text, int columns)
        {
            return text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).Where(c => c.Length == columns);
        }

        /// The device the engine uses for a role, including the automatic pick (see system.h).
        public Device Effective(Role role)
        {
            var input = role == Role.InMic;
            var hit = Devices.FirstOrDefault(d => d.Input == input && d.Id == S.Devices[(int)role]);
            if (hit != null) return hit;
            if (role != Role.OutMain && role != Role.InMic) return null;
            return Devices.Where(d => d.Input == input).OrderByDescending(d => (d.Default ? 50 : 0) - (d.Virtual ? 100 : 0)
                + (input ? 0 : d.Form == 3 || d.Form == 5 ? 30 : d.Form == 1 ? 20 : 0)).FirstOrDefault();
        }

        // ------------------------------------------------------------ crash protection

        private static string ExePath { get { return System.Reflection.Assembly.GetExecutingAssembly().Location; } }

        public void UpdateProtection()
        {
            if (Preview || Sandboxed || _engine == IntPtr.Zero) return;
            var risky = On(P.MixerOn) || Meter(M.Pending) > 0;
            if (On(P.MixerOn) && (_guard == null || _guard.HasExited))
            {
                try
                {
                    _guard = Process.Start(new ProcessStartInfo(ExePath, "--guard " + Process.GetCurrentProcess().Id)
                        { UseShellExecute = false, CreateNoWindow = true });
                }
                catch (Exception e) { Notice("Çökme bekçisi başlatılamadı: " + e.Message); }
            }
            // After a power cut nothing of ours is running, so the next sign-in repairs volumes.
            if (_restoreArmed == risky) return;
            _restoreArmed = risky;
            ArmRestore(risky);
        }

        public static void ArmRestore(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunOnceKey))
                {
                    if (on) k.SetValue(RestoreName, "\"" + ExePath + "\" --guard 0");
                    else k.DeleteValue(RestoreName, false);
                }
            }
            catch (Exception e) { Trace.WriteLine("RunOnce: " + e.Message); }
        }

        public bool StartWithWindows
        {
            get
            {
                if (Preview) return false;
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("Duyu") != null;
            }
            set
            {
                if (Preview || Sandboxed) return;
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("Duyu", "\"" + ExePath + "\" --tray");
                    else k.DeleteValue("Duyu", false);
                }
            }
        }

        public void Dispose()
        {
            _save.Stop();
            SaveNow();
            if (_engine == IntPtr.Zero) return;
            Native.duyu_destroy(_engine); // restores every app before returning
            _engine = IntPtr.Zero;
        }
    }
}

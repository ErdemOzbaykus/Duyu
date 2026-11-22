using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace Duyu
{
    /// Settings remote for iPhone and iPad. Off by default, bound to the Tailscale address only, carries no audio.
    /// HttpListener is not used because it needs an administrator URL reservation.
    public sealed class Remote : IDisposable
    {
        public const int DefaultPort = 47800;
        private const int MaxHead = 8192, MaxBody = 4096, MaxClients = 32, MaxDevices = 20, MaxWrongPins = 5;
        private const string Cookie = "duyu";
        private static readonly TimeSpan PinLife = TimeSpan.FromMinutes(5);

        private struct Rule
        {
            public readonly int Count;
            public readonly float Min, Max;
            public Rule(int count, float min, float max) { Count = count; Min = min; Max = max; }
            public bool IsSwitch { get { return Min == 0 && Max == 0; } }
        }

        // Everything a phone may write. A parameter that is not listed cannot be changed remotely.
        private static readonly Dictionary<P, Rule> Allowed = new Dictionary<P, Rule>
        {
            { P.MixerOn, new Rule(1, 0, 0) }, { P.MicOn, new Rule(1, 0, 0) },
            { P.MasterVol, new Rule(1, 0, 1) }, { P.MasterMute, new Rule(1, 0, 0) }, { P.Ceiling, new Rule(1, -24, -0.5f) },
            { P.Vol, new Rule(4, 0, 1) }, { P.Mute, new Rule(5, 0, 0) },
            { P.StreamVol, new Rule(5, 0, 1) }, { P.StreamMute, new Rule(5, 0, 0) },
            { P.Boost, new Rule(4, 0, 12) },
            { P.Bass, new Rule(6, -12, 12) }, { P.Voice, new Rule(6, -12, 12) }, { P.Treble, new Rule(6, -12, 12) },
            { P.EqOn, new Rule(6, 0, 0) }, { P.Preamp, new Rule(6, -24, 12) },
            { P.SmartOn, new Rule(4, 0, 0) }, { P.SmartAmount, new Rule(4, 0, 1) }, { P.SmartTarget, new Rule(4, -32, -10) },
            { P.DenoiseOn, new Rule(4, 0, 0) }, { P.DenoiseAmount, new Rule(4, 0, 1) },
            { P.SpatialOn, new Rule(4, 0, 0) }, { P.Immersion, new Rule(4, 0, 1) }, { P.Distance, new Rule(4, 0, 1) },
            { P.ChatMixOn, new Rule(1, 0, 0) }, { P.ChatMix, new Rule(1, -1, 1) },
            { P.StreamerOn, new Rule(1, 0, 0) }, { P.StreamListen, new Rule(1, 0, 0) },
            { P.MicGain, new Rule(1, -24, 24) }, { P.NsAmount, new Rule(1, 0, 1) }, { P.ImpactAmount, new Rule(1, 0, 1) },
            { P.GateOn, new Rule(1, 0, 0) }, { P.GateAuto, new Rule(1, 0, 0) }, { P.GateThreshold, new Rule(1, -70, -15) },
            { P.CompOn, new Rule(1, 0, 0) }, { P.CompAmount, new Rule(1, 0, 1) },
            { P.StabilizerOn, new Rule(1, 0, 0) }, { P.Sidetone, new Rule(1, 0, 1) }
        };

        private sealed class Reject : Exception
        {
            public readonly int Status;
            public Reject(int status, string message) : base(message) { Status = status; }
        }

        private sealed class Request
        {
            public string Method, Path, Query;
            public Dictionary<string, string> Headers;
            public byte[] Body;

            public string Header(string name)
            {
                string v;
                return Headers.TryGetValue(name, out v) ? v : null;
            }
        }

        private readonly Model _m;
        private readonly Dispatcher _ui;
        private readonly int _port;
        private readonly IPAddress _testBind; // --remote-test: loopback instead of Tailscale
        private readonly DispatcherTimer _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private readonly SemaphoreSlim _slots = new SemaphoreSlim(MaxClients);
        private readonly string _page;
        private readonly byte[] _icon;

        private TcpListener _listener;
        private IPAddress _bound;
        private string _pin;
        private DateTime _pinUntil;
        private int _wrong;
        private volatile bool _disposed;

        public string Error { get; private set; }

        // Construct on the UI thread: the model is only ever touched from there.
        public Remote(Model model, IPAddress testBind = null, int port = DefaultPort)
        {
            _m = model;
            _ui = Dispatcher.CurrentDispatcher;
            _testBind = testBind;
            _port = port;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Duyu.remote.html"))
            using (var r = new StreamReader(s, Encoding.UTF8))
                _page = r.ReadToEnd();
            _icon = MakeIcon();
            _watch.Tick += (s, e) => Sync();
            _watch.Start();
            Sync();
        }

        // ------------------------------------------------------------ desktop side

        public string Url { get { return _bound == null ? null : "http://" + _bound + ":" + _port; } }
        public string Pin { get { return PinSeconds > 0 ? _pin : null; } }
        public int PinSeconds { get { return _pin == null ? 0 : Math.Max(0, (int)(_pinUntil - DateTime.UtcNow).TotalSeconds); } }

        public void SetEnabled(bool on)
        {
            _m.S.RemoteOn = on;
            _m.SaveNow();
            Sync();
            if (on) NewPin(); else _pin = null;
        }

        public void NewPin()
        {
            var b = new byte[4];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            _pin = (BitConverter.ToUInt32(b, 0) % 1000000).ToString("D6", CultureInfo.InvariantCulture);
            _pinUntil = DateTime.UtcNow + PinLife;
            _wrong = 0;
        }

        public void RevokeAll()
        {
            _m.S.RemoteDevices.Clear();
            _pin = null;
            _m.SaveNow();
        }

        // ------------------------------------------------------------ listener

        private static bool InTailnet(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = ip.GetAddressBytes();
            return b[0] == 100 && b[1] >= 64 && b[1] < 128;
        }

        private static IPAddress TailscaleAddress()
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus == OperationalStatus.Down) continue;
                if ((n.Name + " " + n.Description).IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var a in n.GetIPProperties().UnicastAddresses)
                    if (InTailnet(a.Address)) return a.Address;
            }
            return null;
        }

        private void Sync()
        {
            if (_disposed) return;
            try
            {
                var want = _m.S.RemoteOn ? (_testBind ?? TailscaleAddress()) : null;
                if (want == null)
                {
                    Stop();
                    Error = _m.S.RemoteOn ? "Tailscale bağlı değil. Bağlanınca kendiliğinden açılır." : null;
                    return;
                }
                if (_listener != null && want.Equals(_bound)) return;
                Stop();
                var listener = new TcpListener(want, _port) { ExclusiveAddressUse = true };
                listener.Start(64);
                _listener = listener;
                _bound = want;
                Error = null;
                Task.Run(() => AcceptLoop(listener, want));
            }
            catch (Exception e) when (e is SocketException || e is NetworkInformationException)
            {
                Stop();
                Error = "Adres açılamadı: " + e.Message;
            }
        }

        private void Stop()
        {
            var l = _listener;
            _listener = null;
            _bound = null;
            if (l != null) l.Stop();
        }

        private void AcceptLoop(TcpListener listener, IPAddress bound)
        {
            for (;;)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { if (listener != _listener) return; Thread.Sleep(50); continue; }
                catch (InvalidOperationException) { return; } // stopped
                if (!_slots.Wait(0)) { client.Close(); continue; }
                Task.Factory.StartNew(() =>
                {
                    try { Serve(client, bound); }
                    finally { client.Close(); _slots.Release(); }
                }, TaskCreationOptions.LongRunning);
            }
        }

        private void Serve(TcpClient client, IPAddress bound)
        {
            // An absolute deadline: per-read timeouts alone let a client trickle one byte at a time.
            using (new Timer(_ => client.Close(), null, 5000, Timeout.Infinite))
            {
                try
                {
                    var from = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                    if (_testBind == null ? !InTailnet(from) : !IPAddress.IsLoopback(from)) return;
                    var stream = client.GetStream();
                    try { Route(stream, Read(stream), bound + ":" + _port); }
                    catch (Reject r) { SendJson(stream, r.Status, new { error = r.Message }); }
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                // Client is null once the deadline timer has closed the connection.
                catch (Exception e) when (client.Client == null || e is IOException || e is SocketException
                                       || e is ObjectDisposedException || e is InvalidOperationException)
                {
                    // the peer left or ran into the deadline
                }
            }
        }

        private static Request Read(Stream s)
        {
            var buf = new byte[MaxHead];
            int have = 0, end = -1;
            while (end < 0)
            {
                if (have == buf.Length) throw new Reject(431, "Başlıklar çok büyük.");
                var n = s.Read(buf, have, buf.Length - have);
                if (n == 0) throw new IOException();
                have += n;
                for (var i = 0; i + 3 < have; i++)
                    if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10) { end = i; break; }
            }

            var lines = Encoding.ASCII.GetString(buf, 0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length != 3 || !first[1].StartsWith("/", StringComparison.Ordinal)) throw new Reject(400, "Geçersiz istek.");
            var r = new Request { Method = first[0], Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) throw new Reject(400, "Geçersiz başlık.");
                var name = line.Substring(0, colon);
                if (r.Headers.ContainsKey(name)) throw new Reject(400, "Yinelenen başlık.");
                r.Headers[name] = line.Substring(colon + 1).Trim();
            }
            var q = first[1].IndexOf('?');
            r.Path = q < 0 ? first[1] : first[1].Substring(0, q);
            r.Query = q < 0 ? "" : first[1].Substring(q + 1);

            if (r.Header("Transfer-Encoding") != null) throw new Reject(400, "Desteklenmeyen gövde.");
            var length = 0;
            var cl = r.Header("Content-Length");
            if (cl != null && !int.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out length)) throw new Reject(400, "Geçersiz uzunluk.");
            if (length > MaxBody) throw new Reject(413, "İstek çok büyük.");
            r.Body = new byte[length];
            var got = Math.Min(length, have - end - 4);
            Array.Copy(buf, end + 4, r.Body, 0, got);
            while (got < length)
            {
                var n = s.Read(r.Body, got, length - got);
                if (n == 0) throw new IOException();
                got += n;
            }
            return r;
        }

        private void Route(Stream s, Request r, string authority)
        {
            // An exact Host defeats DNS rebinding.
            if (r.Header("Host") != authority) throw new Reject(421, "Geçersiz adres.");

            if (r.Method == "GET" && r.Path == "/")
            {
                var nonce = Random256();
                Send(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(_page.Replace("__NONCE__", nonce)),
                    "Content-Security-Policy: default-src 'none'; script-src 'nonce-" + nonce + "'; style-src 'nonce-" + nonce
                    + "'; img-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'\r\n");
                return;
            }
            if (r.Method == "GET" && (r.Path == "/icon.png" || r.Path == "/favicon.ico")) { Send(s, 200, "image/png", _icon); return; }
            if (!r.Path.StartsWith("/api/", StringComparison.Ordinal)) throw new Reject(404, "Bulunamadı.");

            // A custom header forces a CORS preflight, which is never granted.
            var origin = r.Header("Origin");
            if (r.Header("X-Duyu") != "1" || (origin != null && origin != "http://" + authority)) throw new Reject(403, "İzin yok.");

            var post = r.Method == "POST";
            if (!post && r.Method != "GET") throw new Reject(405, "Desteklenmeyen yöntem.");
            Dictionary<string, object> body = null;
            if (post)
            {
                if (!(r.Header("Content-Type") ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                    throw new Reject(415, "JSON gerekli.");
                body = Parse(r.Body);
            }

            if (post && r.Path == "/api/pair")
            {
                var key = OnUi(() => Pair(body));
                SendJson(s, 200, new { ok = true },
                    "Set-Cookie: " + Cookie + "=" + key + "; Path=/; Max-Age=31536000; HttpOnly; SameSite=Strict\r\n");
                return;
            }

            var hash = Hash(CookieValue(r.Header("Cookie")));
            var reply = OnUi(() =>
            {
                var known = false;
                foreach (var d in _m.S.RemoteDevices) known |= SameText(d, hash);
                if (!known) throw new Reject(401, "Eşleştirme gerekli.");
                return post ? Write(r.Path, body) : Get(r.Path, r.Query);
            });
            SendJson(s, 200, reply);
        }

        private T OnUi<T>(Func<T> f)
        {
            var result = default(T);
            Reject reject = null;
            try
            {
                var op = _ui.InvokeAsync(() =>
                {
                    if (_disposed || !_m.S.RemoteOn) { reject = new Reject(503, "Uzaktan kontrol kapalı."); return; }
                    try { result = f(); }
                    catch (Reject r) { reject = r; }
                });
                if (op.Wait(TimeSpan.FromSeconds(3)) != DispatcherOperationStatus.Completed)
                {
                    op.Abort();
                    throw new Reject(503, "Duyu meşgul.");
                }
            }
            catch (TaskCanceledException) { throw new Reject(503, "Duyu kapanıyor."); }
            if (reject != null) throw reject;
            return result;
        }

        // ------------------------------------------------------------ API, always on the UI thread

        private string Pair(Dictionary<string, object> body)
        {
            if (Pin == null) throw new Reject(410, "Kod geçersiz. PC'de yeni kod al.");
            if (!SameText(Str(body, "pin"), _pin))
            {
                if (++_wrong < MaxWrongPins) throw new Reject(401, "Kod yanlış.");
                _pin = null;
                throw new Reject(410, "Çok fazla deneme. PC'de yeni kod al.");
            }
            // Stays valid until it expires, so Safari and the home-screen app can both pair.
            var key = Random256();
            var list = _m.S.RemoteDevices;
            list.Add(Hash(key));
            while (list.Count > MaxDevices) list.RemoveAt(0);
            _m.SaveNow();
            return key;
        }

        private object Get(string path, string query)
        {
            if (path == "/api/meters")
            {
                _m.PollMeters();
                return new { levels = _m.Meters.Take((int)M.MicReduction + 1).Select(Finite).ToArray() };
            }
            if (path != "/api/state") throw new Reject(404, "Bulunamadı.");

            int ch;
            if (!query.StartsWith("ch=", StringComparison.Ordinal)
                || !int.TryParse(query.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out ch) || ch >= Ch.Count) ch = 0;

            _m.PollMeters();
            var curve = new float[97];
            for (var i = 0; i < curve.Length; i++)
                curve[i] = Finite((float)Math.Round(_m.CurveDb(ch, (float)(20 * Math.Pow(1000, i / (curve.Length - 1.0)))), 2));

            return new
            {
                v = Allowed.ToDictionary(a => a.Key.ToString(), a => Enumerable.Range(0, a.Value.Count).Select(i => Finite(_m.Get(a.Key, i))).ToArray()),
                bands = _m.S.Bands,
                names = _m.S.PresetNames,
                presets = _m.AllPresets.Select(p => new { name = p.Name, group = p.Group }).ToArray(),
                favorites = _m.S.Favorites,
                profiles = _m.S.Profiles.Select(p => new { name = p.Name, exe = p.Exe }).ToArray(),
                apps = _m.Apps.Select(a => new { exe = a.Exe, ch = a.Channel, state = (int)a.State, active = a.Active }).ToArray(),
                curveCh = ch,
                curve,
                mixerAllowed = _m.S.SeenMixerInfo,
                running = _m.Meter(M.MixerRunning) > 0.5f,
                captured = (int)_m.Meter(M.Captured)
            };
        }

        private object Write(string path, Dictionary<string, object> b)
        {
            switch (path)
            {
                case "/api/set": SetValue(b); break;
                case "/api/band": SetBand(b); break;

                case "/api/preset":
                {
                    var ch = Int(b, "ch", 0, Ch.Count - 1);
                    var all = _m.AllPresets.ToList();
                    var at = Int(b, "id", 0, all.Count - 1);
                    if (all[at].Name != Str(b, "name")) throw new Reject(409, "Liste değişti, tekrar dene."); // an index alone could hit the wrong preset
                    _m.ApplyPreset(ch, all[at]);
                    break;
                }
                case "/api/profile":
                {
                    var name = Str(b, "name");
                    var profile = _m.S.Profiles.FirstOrDefault(p => p.Name == name);
                    if (profile == null) throw new Reject(409, "Profil artık yok.");
                    _m.ApplyProfile(profile);
                    break;
                }
                case "/api/assign":
                {
                    var exe = Str(b, "exe");
                    var app = _m.Apps.FirstOrDefault(a => a.Exe == exe);
                    if (app == null || app.State == AppState.SharedTree) throw new Reject(409, "Bu uygulama atanamıyor.");
                    _m.Assign(exe, Int(b, "ch", -2, Ch.Aux));
                    break;
                }
                case "/api/nudge":
                    _m.NudgeMix(Int(b, "dir", -1, 1) * Model.MixStep);
                    break;
                case "/api/sidetone":
                    _m.ToggleSidetone();
                    break;
                default: throw new Reject(404, "Bulunamadı.");
            }
            return new { ok = true };
        }

        private void SetValue(Dictionary<string, object> b)
        {
            var name = Str(b, "p");
            P p;
            Rule rule;
            // ToString() must match, otherwise TryParse would accept "7" as a parameter.
            if (!Enum.TryParse(name, out p) || p.ToString() != name || !Allowed.TryGetValue(p, out rule)) throw new Reject(403, "Bu ayar telefondan değişmez.");
            var i = Int(b, "i", 0, rule.Count - 1);
            var v = rule.IsSwitch ? Int(b, "v", 0, 1) : Num(b, "v", rule.Min, rule.Max);
            if (v > 0.5f && p == P.MicOn && !_m.On(p)) throw new Reject(403, "Mikrofon işleme yalnızca PC'den açılır.");
            if (v > 0.5f && p == P.MixerOn && !_m.S.SeenMixerInfo) throw new Reject(403, "Ses motorunu ilk kez PC'de aç.");
            _m.Set(p, v, i);
        }

        private void SetBand(Dictionary<string, object> b)
        {
            int ch = Int(b, "ch", 0, Ch.Count - 1), i = Int(b, "i", 0, Presets.Bands - 1);
            _m.SetBand(ch, i, new float[]
            {
                Int(b, "on", 0, 1), Int(b, "type", 0, 5), Num(b, "f", 10, 22000), Num(b, "gain", -18, 18), Num(b, "q", 0.1f, 30)
            });
        }

        // ------------------------------------------------------------ input and output helpers

        private static Dictionary<string, object> Parse(byte[] body)
        {
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = MaxBody, RecursionLimit = 4 };
                var d = json.DeserializeObject(Encoding.UTF8.GetString(body)) as Dictionary<string, object>;
                if (d != null) return d;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is FormatException || e is OverflowException)
            {
                // malformed JSON: rejected below
            }
            throw new Reject(400, "Geçersiz JSON.");
        }

        private static string Str(Dictionary<string, object> b, string key)
        {
            object o;
            var s = b.TryGetValue(key, out o) ? o as string : null;
            if (s == null) throw new Reject(400, "Geçersiz alan: " + key);
            return s;
        }

        private static float Num(Dictionary<string, object> b, string key, float min, float max)
        {
            object o;
            if (!b.TryGetValue(key, out o) || !(o is int || o is long || o is decimal || o is double)) throw new Reject(400, "Geçersiz alan: " + key);
            var v = Convert.ToDouble(o, CultureInfo.InvariantCulture);
            if (!(v >= min - 1e-4 && v <= max + 1e-4)) throw new Reject(400, "Aralık dışı: " + key); // also rejects NaN
            return (float)Math.Max(min, Math.Min(max, v));
        }

        private static int Int(Dictionary<string, object> b, string key, int min, int max)
        {
            object o;
            if (!b.TryGetValue(key, out o) || !(o is int) || (int)o < min || (int)o > max) throw new Reject(400, "Geçersiz alan: " + key);
            return (int)o;
        }

        private static float Finite(float v) { return float.IsNaN(v) || float.IsInfinity(v) ? 0 : v; }

        private static string CookieValue(string header)
        {
            foreach (var part in (header ?? "").Split(';'))
            {
                var p = part.Trim();
                if (p.StartsWith(Cookie + "=", StringComparison.Ordinal)) return p.Substring(Cookie.Length + 1);
            }
            return "";
        }

        private static string Random256()
        {
            var b = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        private static string Hash(string s)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
        }

        private static bool SameText(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static void SendJson(Stream s, int status, object body, string headers = "")
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(body);
            Send(s, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), headers);
        }

        private static void Send(Stream s, int status, string type, byte[] body, string headers = "")
        {
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + (status < 400 ? " OK" : " Error")
                + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length
                + "\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n"
                + headers + "\r\n");
            s.Write(head, 0, head.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        // iOS fills transparent home-screen icons with black, so this one is opaque.
        private static byte[] MakeIcon()
        {
            using (var bmp = new System.Drawing.Bitmap(180, 180))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            using (var ms = new MemoryStream())
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.FromArgb(16, 19, 20));
                var accent = System.Drawing.Color.FromArgb(255, 176, 103);
                using (var fill = new System.Drawing.SolidBrush(accent))
                using (var ring = new System.Drawing.Pen(accent, 11))
                using (var faint = new System.Drawing.Pen(System.Drawing.Color.FromArgb(120, accent), 9))
                {
                    g.FillEllipse(fill, 69, 69, 42, 42);
                    g.DrawEllipse(ring, 50, 50, 80, 80);
                    g.DrawEllipse(faint, 29, 29, 122, 122);
                }
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _watch.Stop();
            Stop();
        }
    }
}

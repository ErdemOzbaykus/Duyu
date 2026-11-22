using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: AssemblyTitle("Duyu")]
[assembly: AssemblyProduct("Duyu")]
[assembly: AssemblyDescription("Windows ses mikseri")]
[assembly: AssemblyVersion("1.1.0.0")]

namespace Duyu
{
    public static class App
    {
        private static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Duyu"); }
        }

        private static string Option(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            var guard = Option(args, "--guard");
            if (guard != null) return Guard(guard);

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Duyu.Theme.xaml"))
                Kit.Theme = (ResourceDictionary)XamlReader.Load(stream);
            Kit.WheelScrollsPage();

            var shots = Option(args, "--shots");
            if (shots != null) return Shots(shots, args.Contains("--worst"));

            var smoke = Option(args, "--smoke"); // real engine in a throwaway folder: touches no other app
            if (smoke != null) return Smoke(smoke);

            var remoteTest = Option(args, "--remote-test");
            if (remoteTest != null) return RemoteTest(remoteTest, int.Parse(Option(args, "--port") ?? "47801"), args.Contains("--worst"));

            return Run(args.Contains("--preview"), args.Contains("--tray"));
        }

        private static int Guard(string parent)
        {
            uint pid;
            if (!uint.TryParse(parent, out pid) || !Directory.Exists(DataDir)) return 0;
            Model.ArmRestore(true); // stay armed until the job is really done
            Native.duyu_guard(DataDir, pid, 0);
            var journal = new FileInfo(Path.Combine(DataDir, "restore.journal"));
            bool noAppRunning;
            using (new Mutex(false, @"Local\Duyu.App", out noAppRunning))
            {
                if (noAppRunning && (!journal.Exists || journal.Length == 0)) Model.ArmRestore(false);
            }
            return 0;
        }

        private static int Run(bool preview, bool trayOnly)
        {
            bool first;
            using (var mutex = new Mutex(false, preview ? @"Local\Duyu.Preview" : @"Local\Duyu.App", out first))
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, preview ? @"Local\Duyu.Preview.Show" : @"Local\Duyu.Show"))
            {
                if (!first) { show.Set(); return 0; }

                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                Model model;
                try { model = new Model(DataDir, preview, false); }
                catch (Exception e)
                {
                    MessageBox.Show("Duyu başlatılamadı.\n\n" + e.Message + "\n\nWindows 10 20348+ veya Windows 11 x64 gerekir.", "Duyu");
                    return 1;
                }

                using (model)
                using (var remote = preview ? null : new Remote(model))
                {
                    var window = new MainWindow(model) { Remote = remote };
                    app.MainWindow = window;
                    app.DispatcherUnhandledException += (s, e) =>
                    {
                        Log(e.Exception, preview);
                        window.Toast("Beklenmeyen hata: " + e.Exception.Message);
                        e.Handled = true;
                    };
                    // Audio must be handed back before Windows signs the user out.
                    app.SessionEnding += (s, e) => { if (remote != null) remote.Dispose(); window.Quit(); model.Dispose(); };

                    window.CreateTray();
                    window.RegisterHotkeys();
                    var wait = ThreadPool.RegisterWaitForSingleObject(show,
                        (s, t) => window.Dispatcher.BeginInvoke(new Action(window.Reveal)), null, Timeout.Infinite, false);
                    if (!trayOnly || preview) window.Show();
                    app.Run();
                    wait.Unregister(null);
                }
                GC.KeepAlive(mutex);
                return 0;
            }
        }

        private static void Log(Exception e, bool preview)
        {
            if (preview) return;
            try { File.AppendAllText(Path.Combine(DataDir, "error.log"), DateTime.Now.ToString("s") + " " + e + Environment.NewLine); }
            catch (IOException) { } // logging must never take the audio down
        }

        // ------------------------------------------------------------ test entry points

        private static void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); frame.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(frame);
        }

        /// Demo model, real server on loopback. The test drives the desktop side through files in `dir`:
        /// write cmd.txt, wait for it to disappear, read out.txt.
        private static int RemoteTest(string dir, int port, bool worst)
        {
            Directory.CreateDirectory(dir);
            Demo.WorstCase = worst;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (var model = new Model(dir, true, false))
            {
                model.S.SeenMixerInfo = true;
                model.S.RemoteOn = true;
                model.S.Favorites.AddRange(new[] { "Adım sesleri", "Gece" });
                model.SaveProfile(worst ? "Çok uzun isimli rekabetçi profil <img src=x onerror=alert(1)> 2026" : "CS2", "cs2.exe");
                using (var remote = new Remote(model, IPAddress.Loopback, port))
                {
                    var w = new MainWindow(model)
                    {
                        Remote = remote, WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -8000, Width = 1320, Height = 900, ShowActivated = false, ShowInTaskbar = false
                    };
                    w.Show();
                    remote.NewPin();
                    File.WriteAllText(Path.Combine(dir, "out.txt"), remote.Pin);
                    var cmd = Path.Combine(dir, "cmd.txt");
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    timer.Tick += (s, e) =>
                    {
                        if (!File.Exists(cmd)) return;
                        string[] c;
                        try { c = File.ReadAllText(cmd).Split(' '); }
                        catch (IOException) { return; } // still being written
                        var result = "";
                        switch (c[0])
                        {
                            case "pin": remote.NewPin(); result = remote.Pin; break;
                            case "revoke": remote.RevokeAll(); break;
                            case "enable": remote.SetEnabled(c[1] == "1"); result = remote.Pin ?? ""; break;
                            case "seen": model.S.SeenMixerInfo = c[1] == "1"; break;
                            case "set":
                                model.Set((P)Enum.Parse(typeof(P), c[1]), float.Parse(c[3], System.Globalization.CultureInfo.InvariantCulture), int.Parse(c[2]));
                                break;
                            case "get":
                                result = model.Get((P)Enum.Parse(typeof(P), c[1]), int.Parse(c[2])).ToString(System.Globalization.CultureInfo.InvariantCulture);
                                break;
                            case "devices": result = model.S.RemoteDevices.Count.ToString(); break;
                            case "export": model.Export(Path.Combine(dir, "export.json")); break;
                            case "import": model.Import(Path.Combine(dir, "import.json")); break;
                            case "shot":
                                w.Navigate(int.Parse(c[1]));
                                Pump(300);
                                Capture((FrameworkElement)w.Content, Path.Combine(dir, "desktop.png"));
                                break;
                            case "quit": w.Quit(); app.Shutdown(); break;
                        }
                        File.WriteAllText(Path.Combine(dir, "out.txt"), result);
                        File.Delete(cmd);
                    };
                    timer.Start();
                    app.Run();
                }
            }
            return 0;
        }

        private static int Shots(string dir, bool worst)
        {
            Directory.CreateDirectory(dir);
            Demo.WorstCase = worst;
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (var model = new Model(dir, true, false))
            {
                var w = new MainWindow(model)
                {
                    Width = worst ? 900 : 1320, Height = worst ? 700 : 900,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -8000, Top = 0,
                    ShowActivated = false, ShowInTaskbar = false
                };
                w.Show();
                var names = new[] { "mikser", "ses", "mikrofon", "ayarlar" };
                for (var i = 0; i < names.Length; i++)
                {
                    w.Navigate(i);
                    Pump(450);
                    var name = Path.Combine(dir, (worst ? "worst-" : "") + (i + 1) + "-" + names[i]);
                    Capture((FrameworkElement)w.Content, name + ".png");
                    var view = ((Grid)w.Content).Children.OfType<ContentControl>().First(c => c.Content is Page).Content as Page;
                    if (view == null || view.ScrollableHeight < 20) continue;
                    view.ScrollToEnd();
                    Pump(250);
                    Capture((FrameworkElement)w.Content, name + "-end.png");
                }
                w.Quit();
            }
            return 0;
        }

        private static void Capture(FrameworkElement e, string file)
        {
            var bounds = new Rect(new Point(), e.RenderSize);
            var backed = new DrawingVisual();
            using (var dc = backed.RenderOpen())
            {
                dc.DrawRectangle(Kit.Brush("Bg"), null, bounds);
                dc.DrawRectangle(new VisualBrush(e) { Stretch = Stretch.None }, null, bounds);
            }
            var bmp = new RenderTargetBitmap((int)bounds.Width, (int)bounds.Height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(backed);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(file)) png.Save(f);
        }

        private static int Smoke(string dir)
        {
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (var model = new Model(dir, false, true))
            {
                var w = new MainWindow(model) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -8000, ShowActivated = false, ShowInTaskbar = false };
                w.Show();
                for (var i = 0; i < 4; i++) { w.Navigate(i); Pump(400); }
                w.Navigate(0);

                var cpu = System.Diagnostics.Process.GetCurrentProcess();
                var before = cpu.TotalProcessorTime;
                Pump(10000);
                cpu.Refresh();
                var percent = (cpu.TotalProcessorTime - before).TotalMilliseconds / 10000 * 100;

                model.PollMeters();
                model.SaveNow();
                var lines = new[]
                {
                    "devices=" + model.Devices.Count,
                    "main_stream=" + model.Meter(M.MainRunning) + " mic=" + model.Meter(M.MicRunning),
                    "auto_out=" + (model.Effective(Role.OutMain) == null ? "-" : model.Effective(Role.OutMain).Name),
                    "auto_mic=" + (model.Effective(Role.InMic) == null ? "-" : model.Effective(Role.InMic).Name),
                    "cpu_one_core_percent=" + percent.ToString("0.0"),
                    "private_mb=" + cpu.PrivateMemorySize64 / 1048576,
                    "presets=" + Presets.BuiltIn.Count
                };
                File.WriteAllLines(Path.Combine(dir, "smoke.txt"), lines);
                w.Quit();
            }
            return 0;
        }
    }
}

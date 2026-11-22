using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Duyu
{
    internal static class Draw
    {
        private static readonly Typeface BodyFace = new Typeface("Segoe UI");
        private static readonly Typeface DisplayFace = new Typeface("Bahnschrift");

        public static FormattedText Text(string s, double size, Brush b, bool display = false)
        {
            return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, display ? DisplayFace : BodyFace, size, b, 1.0);
        }

        public static void Centered(DrawingContext dc, FormattedText t, Point at)
        {
            dc.DrawText(t, new Point(at.X - t.Width / 2, at.Y - t.Height / 2));
        }

        public static Pen Pen(Brush b, double width, bool round = false)
        {
            return round ? new Pen(b, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }
                         : new Pen(b, width);
        }

        public static Brush Alpha(Brush b, double opacity)
        {
            var c = b.CloneCurrentValue();
            c.Opacity = opacity;
            c.Freeze();
            return c;
        }

        public static Point Polar(Point c, double r, double degreesClockwiseFromTop)
        {
            var a = degreesClockwiseFromTop * Math.PI / 180;
            return new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        }

        public static void FocusRing(DrawingContext dc, UIElement e)
        {
            if (e.IsKeyboardFocused)
                dc.DrawRoundedRectangle(null, Pen(Kit.Brush("Text"), 2), new Rect(1, 1, Math.Max(0, e.RenderSize.Width - 2), Math.Max(0, e.RenderSize.Height - 2)), 10, 10);
        }
    }

    public sealed class LevelMeter : FrameworkElement
    {
        private readonly Brush _fill, _track, _hot;
        private readonly bool _horizontal;
        private double _l, _r;

        public LevelMeter(string color, bool horizontal = false)
        {
            _fill = Kit.Brush(color); _track = Kit.Brush("Hover"); _hot = Kit.Brush("Danger");
            _horizontal = horizontal;
            if (horizontal) Height = 12; else Width = 12;
            IsHitTestVisible = false;
        }

        private static double Scale(float v)
        {
            return Math.Max(0, Math.Min(1, (20 * Math.Log10(Math.Max(v, 1e-6)) + 60) / 60));
        }

        public void Show(float left, float right)
        {
            double l = Scale(left), r = Scale(right);
            if (Math.Abs(l - _l) < 0.003 && Math.Abs(r - _r) < 0.003) return;
            _l = l; _r = r;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            for (var i = 0; i < 2; i++)
            {
                var v = i == 0 ? _l : _r;
                var b = v > 0.975 ? _hot : _fill;
                if (_horizontal)
                {
                    dc.DrawRoundedRectangle(_track, null, new Rect(0, i * 7, w, 5), 2.5, 2.5);
                    if (v > 0) dc.DrawRoundedRectangle(b, null, new Rect(0, i * 7, w * v, 5), 2.5, 2.5);
                }
                else
                {
                    dc.DrawRoundedRectangle(_track, null, new Rect(i * 7, 0, 5, h), 2.5, 2.5);
                    if (v > 0) dc.DrawRoundedRectangle(b, null, new Rect(i * 7, h * (1 - v), 5, h * v), 2.5, 2.5);
                }
            }
        }
    }

    /// The signature element: a "hearing field" around the head that widens with how much room you let in.
    public sealed class HearingDial : RangeBase
    {
        private const double From = -135, Sweep = 270;
        private bool _drag, _on;
        private double _level;

        public HearingDial()
        {
            Minimum = -12; Maximum = 24; SmallChange = 1; LargeChange = 3;
            Focusable = true;
            FocusVisualStyle = null;
            Cursor = Cursors.Hand;
            Height = 330; MinWidth = 280;
            AutomationProperties.SetName(this, "Dış ses kazancı, desibel");
            GotKeyboardFocus += (s, e) => InvalidateVisual();
            LostKeyboardFocus += (s, e) => InvalidateVisual();
        }

        protected override AutomationPeer OnCreateAutomationPeer() { return new DialPeer(this); }

        private sealed class DialPeer : RangeBaseAutomationPeer
        {
            public DialPeer(RangeBase owner) : base(owner) { }
            protected override AutomationControlType GetAutomationControlTypeCore() { return AutomationControlType.Slider; }
        }

        public void Show(bool on, float level)
        {
            var target = on ? Math.Max(0, Math.Min(1, (20 * Math.Log10(Math.Max(level, 1e-6)) + 60) / 54)) : 0;
            if (!SystemParameters.ClientAreaAnimation) target = 0; // reduced motion
            var next = _level + (target - _level) * 0.3;
            if (on == _on && Math.Abs(next - _level) < 0.002) return;
            _on = on; _level = next;
            InvalidateVisual();
        }

        protected override void OnValueChanged(double oldValue, double newValue)
        {
            base.OnValueChanged(oldValue, newValue);
            InvalidateVisual();
        }

        private Point Centre { get { return new Point(ActualWidth / 2, ActualHeight / 2); } }

        private void FromPoint(Point p)
        {
            var a = Math.Atan2(p.X - Centre.X, Centre.Y - p.Y) * 180 / Math.PI;
            var f = Math.Max(0, Math.Min(1, (a - From) / Sweep));
            Value = Math.Round(Minimum + f * (Maximum - Minimum));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            _drag = CaptureMouse();
            FromPoint(e.GetPosition(this));
            e.Handled = true;
        }
        protected override void OnMouseMove(MouseEventArgs e) { if (_drag) FromPoint(e.GetPosition(this)); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { _drag = false; ReleaseMouseCapture(); }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (!IsKeyboardFocused) return; // never steal page scrolling
            Value += e.Delta > 0 ? 1 : -1;
            e.Handled = true;
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            var d = e.Key == Key.Up || e.Key == Key.Right ? SmallChange
                  : e.Key == Key.Down || e.Key == Key.Left ? -SmallChange
                  : e.Key == Key.PageUp ? LargeChange
                  : e.Key == Key.PageDown ? -LargeChange : 0;
            if (e.Key == Key.Home) Value = Minimum;
            else if (e.Key == Key.End) Value = Maximum;
            else if (d == 0) return;
            else Value += d;
            e.Handled = true;
        }

        private static void Arc(DrawingContext dc, Pen pen, Point c, double r, double start, double sweep)
        {
            if (sweep < 0.5) return;
            var g = new StreamGeometry();
            using (var x = g.Open())
            {
                x.BeginFigure(Draw.Polar(c, r, start), false, false);
                x.ArcTo(Draw.Polar(c, r, start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, pen, g);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            Brush accent = Kit.Brush("Accent"), edge = Kit.Brush("Edge"), text = Kit.Brush("Text"), muted = Kit.Brush("Muted");
            var c = Centre;
            var r = Math.Max(40, Math.Min(w, h) / 2 - 18);
            var f = (Value - Minimum) / (Maximum - Minimum);

            // The room: rings that open with gain and brighten with what the microphone hears.
            var reach = 0.58 + 0.34 * f;
            for (var i = 0; i < 4; i++)
            {
                var ring = r * (0.5 + (reach - 0.5) * (i + 1) / 4.0) * (1 + 0.04 * _level);
                var o = _on ? Math.Max(0.05, 0.10 + 0.5 * _level - 0.04 * i) : 0.07;
                dc.DrawEllipse(_on ? Draw.Alpha(accent, 0.035) : null, Draw.Pen(Draw.Alpha(_on ? accent : edge, o), 1.5), c, ring, ring);
            }

            Arc(dc, Draw.Pen(Kit.Brush("Hover"), 7, true), c, r, From, Sweep);
            Arc(dc, Draw.Pen(_on ? accent : edge, 7, true), c, r, From, Sweep * f);
            for (var db = -12; db <= 24; db += 6)
            {
                var a = From + Sweep * (db + 12) / 36.0;
                dc.DrawLine(Draw.Pen(edge, 1.5), Draw.Polar(c, r - 9, a), Draw.Polar(c, r - 14, a));
            }
            dc.DrawEllipse(text, null, Draw.Polar(c, r, From + Sweep * f), 11, 11);

            // Head seen from above, wearing headphones.
            var head = r * 0.43;
            var cup = _on ? accent : edge;
            dc.DrawEllipse(Kit.Brush("Raised"), null, c, head, head);
            dc.DrawGeometry(Kit.Brush("Raised"), null, Geometry.Parse(string.Format(CultureInfo.InvariantCulture,
                "M{0},{1} L{2},{3} L{4},{1} Z", c.X - head * 0.2, c.Y - head * 0.92, c.X, c.Y - head * 1.2, c.X + head * 0.2)));
            dc.DrawRoundedRectangle(cup, null, new Rect(c.X - head - 9, c.Y - head * 0.38, 14, head * 0.76), 6, 6);
            dc.DrawRoundedRectangle(cup, null, new Rect(c.X + head - 5, c.Y - head * 0.38, 14, head * 0.76), 6, 6);

            Draw.Centered(dc, Draw.Text((Value > 0 ? "+" : "") + Value.ToString("0"), r * 0.3, text, true), new Point(c.X, c.Y - 6));
            Draw.Centered(dc, Draw.Text(_on ? "dB" : "kapalı", 14, muted), new Point(c.X, c.Y + r * 0.19));
            Draw.Centered(dc, Draw.Text("DIŞ SES", 12, muted, true), new Point(c.X, c.Y + r * 0.86));
            Draw.FocusRing(dc, this);
        }
    }

    public sealed class EqGraph : FrameworkElement
    {
        private const double L = 38, R = 14, T = 14, B = 26, Range = 18, Lo = 20, Hi = 20000;
        private readonly Model _m;
        private bool _drag;

        public int Channel;
        public int Selected = 5;
        public event Action SelectedChanged = delegate { };

        public EqGraph(Model m)
        {
            _m = m;
            Focusable = true;
            FocusVisualStyle = null;
            Height = 280;
            ClipToBounds = true;
            AutomationProperties.SetName(this, "Ekolayzer eğrisi. Ok tuşları bandı taşır, Page Up ve Page Down band seçer.");
            GotKeyboardFocus += (s, e) => InvalidateVisual();
            LostKeyboardFocus += (s, e) => InvalidateVisual();
        }

        private double X(double f) { return L + Math.Log(f / Lo) / Math.Log(Hi / Lo) * (ActualWidth - L - R); }
        private double Y(double db) { return T + (Range - db) / (2 * Range) * (ActualHeight - T - B); }
        private double F(double x) { return Lo * Math.Pow(Hi / Lo, Math.Max(0, Math.Min(1, (x - L) / (ActualWidth - L - R)))); }
        private double Db(double y) { return Math.Max(-Range, Math.Min(Range, Range - (y - T) / (ActualHeight - T - B) * 2 * Range)); }

        private Point Node(int i)
        {
            var b = _m.S.Bands[Channel][i];
            return new Point(X(Math.Max(Lo, Math.Min(Hi, b[2]))), Y(b[1] >= 3 ? 0 : b[3]));
        }

        private void Pick(int i)
        {
            if (i == Selected) return;
            Selected = i;
            SelectedChanged();
            InvalidateVisual();
        }

        private void Write(Action<float[]> edit)
        {
            var b = (float[])_m.S.Bands[Channel][Selected].Clone();
            edit(b);
            b[0] = 1;
            _m.SetBand(Channel, Selected, b);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            e.Handled = true;
            var p = e.GetPosition(this);
            var best = Enumerable.Range(0, Presets.Bands).OrderBy(i => (Node(i) - p).Length).First();
            if ((Node(best) - p).Length > 26) return;
            Pick(best);
            if (e.ClickCount == 2) { Write(b => b[3] = 0); return; }
            _drag = CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_drag) return;
            var p = e.GetPosition(this);
            Write(b => { b[2] = (float)Math.Round(F(p.X)); if (b[1] < 3) b[3] = (float)Math.Round(Db(p.Y), 1); });
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { _drag = false; ReleaseMouseCapture(); }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (!IsKeyboardFocused) return;
            Write(b => b[4] = (float)Math.Round(Math.Max(0.1, Math.Min(12, b[4] * (e.Delta > 0 ? 1.12 : 0.89))), 2));
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.PageUp: Pick((Selected + 1) % Presets.Bands); break;
                case Key.PageDown: Pick((Selected + Presets.Bands - 1) % Presets.Bands); break;
                case Key.Up: Write(b => b[3] = Math.Min(18, b[3] + 0.5f)); break;
                case Key.Down: Write(b => b[3] = Math.Max(-18, b[3] - 0.5f)); break;
                case Key.Left: Write(b => b[2] = (float)Math.Max(Lo, Math.Round(b[2] / 1.06))); break;
                case Key.Right: Write(b => b[2] = (float)Math.Min(Hi, Math.Round(b[2] * 1.06) + 1)); break;
                default: return;
            }
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 100) return;
            dc.DrawRoundedRectangle(Kit.Brush("Bg"), null, new Rect(0, 0, w, h), 10, 10);
            Brush muted = Kit.Brush("Muted"), color = Kit.Brush(Ch.Keys[Channel]);
            var grid = Draw.Pen(Kit.Brush("Line"), 1);
            foreach (var db in new[] { -12, -6, 0, 6, 12 })
            {
                dc.DrawLine(db == 0 ? Draw.Pen(Kit.Brush("Edge"), 1) : grid, new Point(L, Y(db)), new Point(w - R, Y(db)));
                var t = Draw.Text((db > 0 ? "+" : "") + db, 12, muted);
                dc.DrawText(t, new Point(L - 7 - t.Width, Y(db) - 8));
            }
            foreach (var f in new[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
            {
                dc.DrawLine(grid, new Point(X(f), T), new Point(X(f), h - B));
                var t = Draw.Text(f >= 1000 ? f / 1000 + "k" : f.ToString(), 12, muted);
                dc.DrawText(t, new Point(X(f) - t.Width / 2, h - B + 5));
            }

            var bands = _m.S.Bands[Channel];
            var enabled = _m.On(P.EqOn, Channel);
            float lo = _m.Get(P.Bass, Channel), mid = _m.Get(P.Voice, Channel), hi = _m.Get(P.Treble, Channel);
            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var a = line.Open())
            using (var b = area.Open())
            {
                b.BeginFigure(new Point(L, Y(0)), true, true);
                var first = true;
                for (var x = L; x <= w - R; x += 3)
                {
                    var f = (float)F(x);
                    double sum = 0; // pre-amp is a level, not part of the shape
                    foreach (var band in bands)
                        if (band[0] > 0.5f && (band[1] >= 3 || band[3] != 0))
                            sum += Native.duyu_filter_db((int)band[1], band[2], band[3], band[4], f);
                    if (lo != 0) sum += Native.duyu_filter_db(1, 120, lo, 0.707f, f);
                    if (mid != 0) sum += Native.duyu_filter_db(0, 2200, mid, 0.8f, f);
                    if (hi != 0) sum += Native.duyu_filter_db(2, 6500, hi, 0.707f, f);
                    var p = new Point(x, Y(Math.Max(-Range, Math.Min(Range, sum))));
                    if (first) a.BeginFigure(p, false, false); else a.LineTo(p, true, true);
                    b.LineTo(p, false, false);
                    first = false;
                }
                b.LineTo(new Point(w - R, Y(0)), false, false);
            }
            dc.DrawGeometry(Draw.Alpha(color, enabled ? 0.12 : 0.03), null, area);
            dc.DrawGeometry(null, Draw.Pen(enabled ? color : muted, 2.5, true), line);

            for (var i = 0; i < Presets.Bands; i++)
            {
                var sel = i == Selected;
                var used = bands[i][0] > 0.5f;
                dc.DrawEllipse(sel ? color : Kit.Brush("Raised"), Draw.Pen(used ? color : muted, 1.5), Node(i), sel ? 12 : 10, sel ? 12 : 10);
                Draw.Centered(dc, Draw.Text((i + 1).ToString(), 12, sel ? Kit.Brush("OnAccent") : Kit.Brush("Text"), true), Node(i));
            }
            Draw.FocusRing(dc, this);
        }
    }

    public sealed class RoomMap : FrameworkElement
    {
        private static readonly string[] Labels = { "Sol", "Sağ", "Orta", "", "A. Sol", "A. Sağ", "Y. Sol", "Y. Sağ" };
        private static readonly string[] Long = { "ön sol", "ön sağ", "orta", "", "arka sol", "arka sağ", "yan sol", "yan sağ" };
        private static readonly int[] Order = { 0, 2, 1, 7, 5, 4, 6 };
        private readonly Model _m;
        private bool _drag;
        private int _selected = 1, _playing = -1;

        public RoomMap(Model m)
        {
            _m = m;
            Height = 270;
            Focusable = true;
            FocusVisualStyle = null;
            GotKeyboardFocus += (s, e) => InvalidateVisual();
            LostKeyboardFocus += (s, e) => InvalidateVisual();
            Describe();
        }

        private void Describe()
        {
            AutomationProperties.SetName(this, "Sanal hoparlör: " + Long[_selected] + ". Ok tuşları döndürür, Page Up ve Page Down seçer.");
        }

        public void Show(int playing)
        {
            if (playing == _playing) return;
            _playing = playing;
            InvalidateVisual();
        }

        private Point Centre { get { return new Point(ActualWidth / 2, ActualHeight / 2); } }
        private double Radius { get { return Math.Max(30, Math.Min(ActualWidth, ActualHeight) / 2 - 26); } }
        private Point At(int i) { return Draw.Polar(Centre, Radius, _m.Get(P.SpeakerAngle, i)); }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            e.Handled = true;
            var p = e.GetPosition(this);
            var best = Order.OrderBy(i => (At(i) - p).Length).First();
            if ((At(best) - p).Length > 28) return;
            _selected = best;
            _drag = CaptureMouse();
            Describe();
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_drag) return;
            var p = e.GetPosition(this);
            _m.Set(P.SpeakerAngle, (float)Math.Round(Math.Atan2(p.X - Centre.X, Centre.Y - p.Y) * 180 / Math.PI), _selected);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { _drag = false; ReleaseMouseCapture(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.PageUp || e.Key == Key.PageDown)
            {
                _selected = Order[(Array.IndexOf(Order, _selected) + (e.Key == Key.PageUp ? 1 : Order.Length - 1)) % Order.Length];
                Describe();
                InvalidateVisual();
            }
            else if (e.Key == Key.Left || e.Key == Key.Right)
            {
                var a = _m.Get(P.SpeakerAngle, _selected) + (e.Key == Key.Right ? 5 : -5);
                _m.Set(P.SpeakerAngle, a > 180 ? a - 360 : a < -180 ? a + 360 : a, _selected);
            }
            else return;
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRoundedRectangle(Kit.Brush("Bg"), null, new Rect(0, 0, ActualWidth, ActualHeight), 10, 10);
            Brush game = Kit.Brush("Game"), text = Kit.Brush("Text"), dark = Kit.Brush("OnAccent");
            var c = Centre;
            dc.DrawEllipse(null, Draw.Pen(Kit.Brush("Line"), 1.5), c, Radius, Radius);
            dc.DrawEllipse(null, Draw.Pen(Kit.Brush("Line"), 1), c, Radius * 0.55, Radius * 0.55);
            dc.DrawEllipse(Kit.Brush("Raised"), null, c, 26, 26);
            Draw.Centered(dc, Draw.Text("Sen", 12, text), c);
            foreach (var i in Order)
            {
                var p = At(i);
                if (i == _playing) dc.DrawLine(Draw.Pen(Draw.Alpha(game, 0.5), 2), c, p);
                var lit = i == _selected || i == _playing;
                dc.DrawEllipse(lit ? game : Kit.Brush("Raised"), Draw.Pen(game, 1.5), p, 24, 24);
                Draw.Centered(dc, Draw.Text(Labels[i], 12, lit ? dark : text, true), p);
            }
            Draw.FocusRing(dc, this);
        }
    }
}

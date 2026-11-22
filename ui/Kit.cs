using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Duyu
{
    /// Two columns while there is room, stacked when there is not.
    public sealed class Columns : Panel
    {
        private const double Gap = 16;
        public double Breakpoint = 820;
        public double Ratio = 0.5;

        private bool Stacked(double width) { return width < Breakpoint || InternalChildren.Count < 2; }

        private double Width0(double width, int i)
        {
            var left = Math.Max(0, (width - Gap) * Ratio);
            return i % 2 == 0 ? left : Math.Max(0, width - Gap - left);
        }

        protected override Size MeasureOverride(Size available)
        {
            var w = double.IsInfinity(available.Width) ? Breakpoint : available.Width;
            var stack = Stacked(w);
            double[] h = { 0, 0 };
            for (var i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                child.Measure(new Size(stack ? w : Width0(w, i), double.PositiveInfinity));
                var col = stack ? 0 : i % 2;
                if (child.DesiredSize.Height > 0) h[col] += (h[col] > 0 ? Gap : 0) + child.DesiredSize.Height;
            }
            return new Size(w, Math.Max(h[0], h[1]));
        }

        protected override Size ArrangeOverride(Size final)
        {
            var stack = Stacked(final.Width);
            double[] y = { 0, 0 };
            for (var i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                var col = stack ? 0 : i % 2;
                var w = stack ? final.Width : Width0(final.Width, i);
                var x = col == 0 ? 0 : Width0(final.Width, 0) + Gap;
                var h = child.DesiredSize.Height;
                child.Arrange(new Rect(x, y[col], w, h));
                if (h > 0) y[col] += h + Gap;
            }
            return final;
        }
    }

    /// Equal tiles: six across, or three or two when six would clip.
    public sealed class Tiles : Panel
    {
        private const double Gap = 10;
        public double MinTile = 124;

        private int Count(double width)
        {
            var n = InternalChildren.Count;
            foreach (var c in new[] { n, 3, 2 })
                if ((width - Gap * (c - 1)) / c >= MinTile) return c;
            return 1;
        }

        protected override Size MeasureOverride(Size available)
        {
            var w = available.Width;
            var cols = Count(w);
            var tile = (w - Gap * (cols - 1)) / cols;
            double h = 0;
            foreach (UIElement c in InternalChildren)
            {
                c.Measure(new Size(tile, double.PositiveInfinity));
                h = Math.Max(h, c.DesiredSize.Height);
            }
            var rows = (InternalChildren.Count + cols - 1) / cols;
            return new Size(w, rows * h + Math.Max(0, rows - 1) * Gap);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var cols = Count(final.Width);
            var rows = (InternalChildren.Count + cols - 1) / cols;
            var w = (final.Width - Gap * (cols - 1)) / cols;
            var h = (final.Height - Gap * (rows - 1)) / rows;
            for (var i = 0; i < InternalChildren.Count; i++)
                InternalChildren[i].Arrange(new Rect(i % cols * (w + Gap), i / cols * (h + Gap), w, h));
            return final;
        }
    }

    public static class Kit
    {
        public static ResourceDictionary Theme;

        public static Brush Brush(string key) { return (Brush)Theme[key]; }
        public static Style Style(string key) { return (Style)Theme[key]; }
        public static FontFamily Font(string key) { return (FontFamily)Theme[key]; }

        /// WPF lets the wheel change a closed ComboBox that has focus, so scrolling a page rewrote device choices.
        public static void WheelScrollsPage()
        {
            EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((s, e) =>
            {
                var box = (ComboBox)s;
                if (box.IsDropDownOpen) return;
                e.Handled = true;
                var parent = VisualTreeHelper.GetParent(box) as UIElement;
                if (parent != null)
                    parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent });
            }));
        }

        public static T Named<T>(T e, string name) where T : DependencyObject
        {
            AutomationProperties.SetName(e, name);
            return e;
        }

        public static T With<T>(this T panel, params UIElement[] children) where T : Panel
        {
            foreach (var c in children) if (c != null) panel.Children.Add(c);
            return panel;
        }

        public static T Margin<T>(this T e, double left, double top, double right, double bottom) where T : FrameworkElement
        {
            e.Margin = new Thickness(left, top, right, bottom);
            return e;
        }

        public static TextBlock Text(string s, double size = 14, string color = null, bool wrap = false)
        {
            var t = new TextBlock { Text = s, FontSize = size, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis };
            if (wrap) { t.TextWrapping = TextWrapping.Wrap; t.LineHeight = size * 1.45; }
            if (color != null) t.Foreground = Brush(color);
            return t;
        }

        public static TextBlock Title(string s, double size = 18)
        {
            var t = Text(s, size);
            t.FontFamily = Font("Display");
            return t;
        }

        public static TextBlock Icon(string glyph, double size = 16, string color = null)
        {
            var t = Text(glyph, size, color);
            t.FontFamily = Font("Icons");
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        public static StackPanel Stack(params UIElement[] children) { return new StackPanel().With(children); }

        public static WrapPanel Wrap(params UIElement[] children)
        {
            var p = new WrapPanel();
            foreach (var c in children)
            {
                var fe = c as FrameworkElement;
                if (fe != null) fe.Margin = new Thickness(0, 0, 8, 8);
                p.Children.Add(c);
            }
            return p;
        }

        public static Grid Equal(params UIElement[] children)
        {
            var g = new Grid();
            for (var i = 0; i < children.Length; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(children[i], i);
                g.Children.Add(children[i]);
            }
            return g;
        }

        public static DockPanel Row(UIElement main, params UIElement[] right)
        {
            var d = new DockPanel();
            foreach (var r in right.Reverse())
            {
                DockPanel.SetDock(r, Dock.Right);
                d.Children.Add(r);
            }
            d.Children.Add(main);
            return d;
        }

        public static Border Card(string title, string subtitle, params UIElement[] children)
        {
            var s = new StackPanel();
            if (title != null) s.Children.Add(Title(title).Margin(0, 0, 0, subtitle == null ? 10 : 4));
            if (subtitle != null) s.Children.Add(Text(subtitle, 13, "Muted", true).Margin(0, 0, 0, 10));
            s.With(children);
            return new Border { Background = Brush("Surface"), CornerRadius = new CornerRadius(14), Padding = new Thickness(20), Child = s };
        }

        public static Border Note(TextBlock text, string color = "Accent")
        {
            return new Border
            {
                BorderBrush = Brush(color), BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(12, 4, 8, 4), Margin = new Thickness(0, 10, 0, 4), Child = text
            };
        }

        public static Button Button(string label, Action click, string style = null, string glyph = null)
        {
            object content = label;
            if (glyph != null)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(Icon(glyph, 15).Margin(0, 0, 9, 0));
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                content = row;
            }
            var b = Named(new Button { Content = content }, label);
            if (style != null) b.Style = Style(style);
            b.Click += (s, e) => click();
            return b;
        }

        public static Button IconButton(string glyph, string name, Action click)
        {
            var b = Named(new Button { Content = Icon(glyph), Style = Style("Quiet"), ToolTip = name }, name);
            b.Click += (s, e) => click();
            return b;
        }
    }

    /// Controls bound to the model. Refresh runs on every model change.
    public sealed class Binder
    {
        public readonly Model M;
        private readonly List<Action> _refresh = new List<Action>();
        private bool _updating;

        public Binder(Model m) { M = m; }

        public void Refresh()
        {
            _updating = true;
            try { foreach (var r in _refresh.ToArray()) r(); }
            finally { _updating = false; }
        }

        public void On(Action refresh)
        {
            _refresh.Add(refresh);
            var was = _updating;
            _updating = true;
            refresh();
            _updating = was;
        }

        public CheckBox Switch(string label, string hint, Func<bool> get, Action<bool> set, string color = "Accent")
        {
            var content = Kit.Stack(Kit.Text(label, 14, null, true));
            if (hint != null) content.Children.Add(Kit.Text(hint, 12, "Muted", true));
            content.Margin = new Thickness(0, 4, 0, 4);
            var box = Kit.Named(new CheckBox { Style = Kit.Style("Switch"), Content = content, Tag = Kit.Brush(color) }, label);
            if (hint != null) AutomationProperties.SetHelpText(box, hint);
            RoutedEventHandler h = (s, e) => { if (!_updating) { set(box.IsChecked == true); Refresh(); } };
            box.Checked += h;
            box.Unchecked += h;
            On(() => box.IsChecked = get());
            return box;
        }

        public CheckBox Switch(string label, string hint, P p, Func<int> ch = null, string color = "Accent")
        {
            var i = ch ?? (() => 0);
            return Switch(label, hint, () => M.On(p, i()), v => M.Set(p, v ? 1 : 0, i()), color);
        }

        public CheckBox Mute(string name, P p, int i = 0)
        {
            var box = Kit.Named(new CheckBox { Style = Kit.Style("Mute"), ToolTip = name }, name);
            RoutedEventHandler h = (s, e) => { if (!_updating) M.Set(p, box.IsChecked == true ? 1 : 0, i); };
            box.Checked += h;
            box.Unchecked += h;
            On(() => box.IsChecked = M.On(p, i));
            return box;
        }

        public FrameworkElement Slider(string label, double min, double max, double step,
            Func<double> get, Action<double> set, Func<double, string> format, string color = "Accent")
        {
            var value = Kit.Text("", 13, "Muted");
            var s = Kit.Named(new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, Foreground = Kit.Brush(color) }, label);
            s.ValueChanged += (o, e) =>
            {
                var v = Math.Round(s.Value / step) * step;
                value.Text = format(v);
                AutomationProperties.SetItemStatus(s, value.Text);
                if (!_updating) set(v);
            };
            On(() => { s.Value = get(); value.Text = format(get()); });
            return Kit.Stack(Kit.Row(Kit.Text(label), value), s.Margin(-10, 0, -10, 0)).Margin(0, 8, 0, 0);
        }

        public FrameworkElement Slider(string label, P p, double min, double max, double step,
            Func<double, string> format, Func<int> ch = null, string color = "Accent")
        {
            var i = ch ?? (() => 0);
            return Slider(label, min, max, step, () => M.Get(p, i()), v => M.Set(p, (float)v, i()), format, color);
        }

        public Slider Fader(string name, P p, int i, string color, TextBlock readout)
        {
            var s = Kit.Named(new Slider
            {
                Style = Kit.Style("Fader"), Minimum = 0, Maximum = 1, SmallChange = 0.01, LargeChange = 0.1, Foreground = Kit.Brush(color)
            }, name);
            s.ValueChanged += (o, e) =>
            {
                readout.Text = Format.Percent(s.Value);
                if (!_updating) M.Set(p, (float)s.Value, i);
            };
            On(() => { s.Value = M.Get(p, i); readout.Text = Format.Percent(s.Value); });
            return s;
        }

        public Grid Segments(string group, string[] labels, Func<int> get, Action<int> set, Func<int, string> color = null)
        {
            var id = Guid.NewGuid().ToString();
            var items = labels.Select((label, i) =>
            {
                var r = Kit.Named(new RadioButton
                {
                    Content = Kit.Text(label), GroupName = id, Style = Kit.Style("Segment"),
                    Tag = Kit.Brush(color == null ? "Accent" : color(i))
                }, group + ": " + label);
                r.Checked += (s, e) => { if (!_updating) { set(i); Refresh(); } };
                On(() => r.IsChecked = get() == i);
                return (UIElement)r;
            }).ToArray();
            return Kit.Equal(items);
        }

        public FrameworkElement Device(string label, Role role, bool input, string none)
        {
            var box = Kit.Named(new ComboBox(), label);
            var ids = new List<string>();
            string shown = null;
            box.SelectionChanged += (s, e) =>
            {
                if (!_updating && box.SelectedIndex >= 0) M.SetDevice(role, ids[box.SelectedIndex]);
            };
            On(() =>
            {
                var list = M.Devices.Where(d => d.Input == input).ToList();
                var auto = role == Role.OutMain || role == Role.InMic;
                var picked = M.Effective(role);
                var first = none;
                if (auto && picked != null && string.IsNullOrEmpty(M.S.Devices[(int)role])) first += " · " + picked.Name;
                var key = first + string.Join("|", list.Select(d => d.Id + d.Name));
                if (key != shown)
                {
                    shown = key;
                    ids.Clear();
                    box.Items.Clear();
                    ids.Add("");
                    box.Items.Add(first);
                    foreach (var d in list) { ids.Add(d.Id); box.Items.Add(d.Name); }
                }
                box.SelectedIndex = Math.Max(0, ids.IndexOf(M.S.Devices[(int)role]));
                box.ToolTip = box.SelectedItem;
            });
            return Kit.Stack(Kit.Text(label, 13, "Muted").Margin(0, 12, 0, 6), box);
        }
    }

    public static class Format
    {
        public static string Percent(double v) { return "%" + Math.Round(v * 100).ToString("0"); }
        public static string Db(double v) { return (v > 0.049 ? "+" : "") + v.ToString("0.#") + " dB"; }
        public static string Hz(double v) { return v >= 1000 ? (v / 1000).ToString("0.##") + " kHz" : v.ToString("0") + " Hz"; }
        public static string Q(double v) { return v.ToString("0.00"); }
    }
}

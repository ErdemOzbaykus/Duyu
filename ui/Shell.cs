using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Duyu
{
    internal static class Hotkeys
    {
        public static string Describe(Key key, ModifierKeys mods)
        {
            switch (key)
            {
                case Key.LeftCtrl: case Key.RightCtrl: case Key.LeftShift: case Key.RightShift:
                case Key.LeftAlt: case Key.RightAlt: case Key.LWin: case Key.RWin: case Key.None:
                    return null;
            }
            var function = key >= Key.F1 && key <= Key.F24;
            if (!function && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0) return null;
            // Ctrl+Alt is AltGr on Turkish keyboards (@, ₺, €), so it may only pair with function keys.
            var altGr = ModifierKeys.Control | ModifierKeys.Alt;
            if (!function && (mods & altGr) == altGr) return null;

            return ((mods & ModifierKeys.Control) != 0 ? "Ctrl+" : "")
                 + ((mods & ModifierKeys.Alt) != 0 ? "Alt+" : "")
                 + ((mods & ModifierKeys.Shift) != 0 ? "Shift+" : "")
                 + key;
        }

        public static bool Parse(string text, out uint mods, out uint vk)
        {
            mods = 0x4000; // no auto-repeat
            vk = 0;
            foreach (var part in (text ?? "").Split('+'))
            {
                Key k;
                if (part == "Ctrl") mods |= 2;
                else if (part == "Alt") mods |= 1;
                else if (part == "Shift") mods |= 4;
                else if (Enum.TryParse(part, out k)) vk = (uint)KeyInterop.VirtualKeyFromKey(k);
            }
            return vk != 0;
        }
    }

    internal static class Chrome
    {
        public static void Dark(Window w)
        {
            var on = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(w).EnsureHandle(), 20, ref on, 4);
        }
    }

    internal static class Dialog
    {
        private static Window Make(Window owner, string title, UIElement body, string ok, UIElement focus, out Func<bool> show)
        {
            var w = new Window
            {
                Title = title, Width = 480, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Background = Kit.Brush("Surface"), Foreground = Kit.Brush("Text"),
                FontFamily = Kit.Font("Body"), FontSize = 14, Resources = Kit.Theme,
                WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
            };
            if (owner.IsVisible) w.Owner = owner;
            var yes = Kit.Button(ok, () => w.DialogResult = true, "Primary");
            yes.IsDefault = true;
            var no = Kit.Button("Vazgeç", () => w.DialogResult = false, "Quiet");
            no.IsCancel = true;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.With(no.Margin(0, 0, 8, 0), yes);
            w.Content = Kit.Stack(Kit.Title(title, 22), body, buttons.Margin(0, 22, 0, 0)).Margin(24, 24, 24, 24);
            w.SourceInitialized += (s, e) => Chrome.Dark(w);
            w.Loaded += (s, e) => (focus ?? yes).Focus();
            show = () => w.ShowDialog() == true;
            return w;
        }

        public static bool Confirm(Window owner, string title, string text, string ok)
        {
            Func<bool> show;
            Make(owner, title, Kit.Text(text, 14, "Muted", true).Margin(0, 12, 0, 0), ok, null, out show);
            return show();
        }

        public static string Ask(Window owner, string title, string label, string value)
        {
            var box = Kit.Named(new TextBox { Text = value, MaxLength = 120 }, label);
            box.SelectAll();
            Func<bool> show;
            Make(owner, title, Kit.Stack(Kit.Text(label, 13, "Muted").Margin(0, 14, 0, 6), box), "Tamam", box, out show);
            return show() ? box.Text : null;
        }
    }
}

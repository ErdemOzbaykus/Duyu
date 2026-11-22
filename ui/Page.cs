using System;
using System.Windows;
using System.Windows.Controls;

namespace Duyu
{
    public abstract class Page : ScrollViewer
    {
        protected readonly Model M;
        protected readonly MainWindow Host;
        protected readonly Binder B;
        private readonly StackPanel _body = new StackPanel { Margin = new Thickness(28, 22, 28, 28) };

        protected Page(MainWindow host, string title, string subtitle, FrameworkElement action = null)
        {
            Host = host;
            M = host.M;
            B = new Binder(M);
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Focusable = false;

            var text = Kit.Stack(Kit.Title(title, 30), Kit.Text(subtitle, 14, "Muted", true).Margin(0, 4, 24, 0));
            if (action != null) action.VerticalAlignment = VerticalAlignment.Center;
            _body.Children.Add((action == null ? (FrameworkElement)text : Kit.Row(text, action)).Margin(0, 0, 0, 20));
            Content = _body;
        }

        protected void Add(FrameworkElement e, double gap = 16)
        {
            e.Margin = new Thickness(0, 0, 0, gap);
            _body.Children.Add(e);
        }

        public void Refresh() { B.Refresh(); }
        public virtual void Tick() { }
        public virtual void Leave() { }
    }
}

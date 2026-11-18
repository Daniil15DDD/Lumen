using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Lumen
{
    /// <summary>
    /// A real, interruptible spring (Apple-style "damping ratio + response" parameters).
    /// Retargeting keeps the current value *and* velocity, so motion never jumps or
    /// hits a "brick wall" when the user changes their mind mid-animation.
    /// Only ticks while something is moving; idle cost is zero.
    /// </summary>
    public sealed class Spring
    {
        /// <summary>Follows Windows "Animation effects" (Settings → Accessibility → Visual effects).</summary>
        public static bool Enabled = SystemParameters.ClientAreaAnimation;

        static readonly List<Spring> live = new List<Spring>();
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static double lastT;
        static bool hooked;

        readonly Action<double> apply;
        double value, velocity, target;
        public double Damping, Response;
        public double Value { get { return value; } }
        public double Target { get { return target; } }

        public Spring(double initial, double damping, double response, Action<double> apply)
        {
            value = target = initial; Damping = damping; Response = response; this.apply = apply;
            apply(initial);
        }

        public void To(double t)
        {
            target = t;
            if (!Enabled) { Snap(t); return; }
            if (!live.Contains(this)) live.Add(this);
            if (!hooked)
            {
                hooked = true; lastT = clock.Elapsed.TotalSeconds;
                CompositionTarget.Rendering += Tick;
            }
        }

        public void To(double t, double damping, double response) { Damping = damping; Response = response; To(t); }

        public void Snap(double v)
        {
            value = target = v; velocity = 0;
            live.Remove(this);
            apply(v);
        }

        static void Tick(object s, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(1 / 30.0, now - lastT);
            lastT = now;
            for (int i = live.Count - 1; i >= 0; i--)
            {
                var sp = live[i];
                if (sp.Step(dt)) live.RemoveAt(i);
            }
            if (live.Count == 0) { CompositionTarget.Rendering -= Tick; hooked = false; }
        }

        bool Step(double dt)
        {
            double w = 2 * Math.PI / Math.Max(0.01, Response);
            double k = w * w, c = 2 * Damping * w;
            int n = Math.Max(1, (int)Math.Ceiling(dt / (1 / 480.0)));
            double h = dt / n;
            for (int i = 0; i < n; i++)
            {
                double a = -k * (value - target) - c * velocity;
                velocity += a * h;
                value += velocity * h;
            }
            double scale = Math.Max(1, Math.Abs(target));
            if (Math.Abs(value - target) < 0.0005 * scale && Math.Abs(velocity) < 0.01 * scale)
            {
                value = target; velocity = 0; apply(value);
                return true;
            }
            apply(value);
            return false;
        }
    }

    public static class Motion
    {
        static readonly ConditionalWeakTable<UIElement, Spring> press = new ConditionalWeakTable<UIElement, Spring>();

        /// <summary>
        /// Instant press feedback on pointer-down for every button-like control,
        /// with a soft spring back on release.
        /// </summary>
        public static void EnablePressFeedback()
        {
            EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) => Press(s as ButtonBase, true)), true);
            EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((s, e) => Press(s as ButtonBase, false)), true);
            EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.MouseLeaveEvent, new MouseEventHandler((s, e) => Press(s as ButtonBase, false)), true);
        }

        static void Press(ButtonBase b, bool down)
        {
            if (b == null || b is RepeatButton || b is CheckBox || "nopress".Equals(b.Tag as string)) return;
            Spring sp;
            if (!press.TryGetValue(b, out sp))
            {
                if (!down) return;
                var st = new ScaleTransform(1, 1);
                b.RenderTransformOrigin = new Point(0.5, 0.5);
                b.RenderTransform = st;
                sp = new Spring(1, 1, 0.12, v => { st.ScaleX = v; st.ScaleY = v; });
                press.Add(b, sp);
            }
            double size = Math.Max(b.ActualWidth, b.ActualHeight);
            double pressed = Math.Max(0.94, 1 - 6 / Math.Max(1, size));
            if (down) sp.To(pressed, 1.0, 0.1);           // immediate, no overshoot
            else if (sp.Target != 1) sp.To(1, 0.72, 0.32); // released: gentle spring home
        }

        public static TranslateTransform Translate(UIElement e)
        {
            var t = e.RenderTransform as TranslateTransform;
            if (t == null) { t = new TranslateTransform(); e.RenderTransform = t; }
            return t;
        }
    }

    /// <summary>
    /// iOS-style segmented control: one accent pill that springs between options.
    /// </summary>
    public sealed class Segmented : Border
    {
        readonly Grid grid = new Grid();
        readonly UniformGrid items = new UniformGrid { Rows = 1 };
        readonly Border pill;
        readonly Spring pillX;
        readonly TranslateTransform pillT = new TranslateTransform();
        public int Selected { get; private set; }
        public event Action<int> Changed;

        public Segmented(IList<string> labels, int selected, Brush accent, Style segStyle)
        {
            CornerRadius = new CornerRadius(10);
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            Padding = new Thickness(3);
            HorizontalAlignment = HorizontalAlignment.Stretch;
            pill = new Border
            {
                CornerRadius = new CornerRadius(8), Background = accent, HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = pillT, IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0x8B, 0x5C, 0xF6), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.45 },
            };
            grid.Children.Add(pill);
            grid.Children.Add(items);
            Child = grid;
            Selected = Math.Max(0, Math.Min(labels.Count - 1, selected));
            string group = "seg" + Guid.NewGuid().ToString("N");
            for (int i = 0; i < labels.Count; i++)
            {
                int idx = i;
                var rb = new RadioButton { Content = labels[i], Style = segStyle, GroupName = group, IsChecked = i == Selected, Tag = "nopress" };
                rb.Checked += delegate { Select(idx, true); };
                items.Children.Add(rb);
            }
            pillX = new Spring(0, 1.0, 0.34, v => pillT.X = v);
            grid.SizeChanged += delegate { Layout(false); };
        }

        double SegW { get { return items.Children.Count == 0 ? 0 : items.ActualWidth / items.Children.Count; } }

        void Layout(bool animate)
        {
            pill.Width = SegW;
            double x = SegW * Selected;
            if (animate) pillX.To(x); else pillX.Snap(x);
        }

        void Select(int i, bool raise)
        {
            if (i == Selected && raise) return;
            Selected = i;
            Layout(true);
            if (raise && Changed != null) Changed(i);
        }
    }
}

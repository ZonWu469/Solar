using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Solar.Core;
using Solar.Rendering;

namespace Solar.UI
{
    /// <summary>Shared immediate-mode UI helpers and number formatting.</summary>
    public static class UiDraw
    {
        public static readonly Color PanelBg = new Color(12, 18, 30, 215);
        public static readonly Color PanelBorder = new Color(70, 95, 130);
        public static readonly Color TextDim = new Color(150, 165, 185);
        public static readonly Color Accent = new Color(120, 210, 255);

        public static void Panel(PrimitiveBatch pb, Rectangle r)
        {
            pb.FillRect(r, PanelBg);
            pb.RectOutline(r, 1, PanelBorder);
        }

        public static bool Button(PrimitiveBatch pb, SpriteBatch sb, SpriteFont f, Rectangle r,
                                  string label, InputState inp, bool enabled = true)
        {
            bool hover = enabled && r.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y);
            Color bg = !enabled ? new Color(25, 30, 40, 200)
                     : hover ? new Color(45, 70, 105, 230)
                     : new Color(28, 42, 64, 220);
            pb.FillRect(r, bg);
            pb.RectOutline(r, 1, enabled ? PanelBorder : new Color(50, 55, 65));
            var sz = f.MeasureString(label);
            sb.DrawString(f, label, new Vector2(r.Center.X - sz.X / 2, r.Center.Y - sz.Y / 2),
                          enabled ? Color.White : new Color(110, 115, 125));
            return hover && inp.LeftClick;
        }

        /// <summary>An editable single-line text box. Appends this frame's typed characters and
        /// handles backspace while focused; draws a caret. Returns true if the box was clicked
        /// (so callers can move focus to it). ASCII-only, capped at <paramref name="maxLen"/>.</summary>
        public static bool TextField(PrimitiveBatch pb, SpriteBatch sb, SpriteFont f, Rectangle r,
                                     ref string text, bool focused, InputState inp, int maxLen = 24)
        {
            text ??= "";
            if (focused)
            {
                foreach (char c in inp.Typed)
                    if (text.Length < maxLen) text += c;
                if (inp.Pressed(Microsoft.Xna.Framework.Input.Keys.Back) && text.Length > 0)
                    text = text.Substring(0, text.Length - 1);
            }

            pb.FillRect(r, new Color(8, 12, 20, 230));
            pb.RectOutline(r, 1, focused ? Accent : PanelBorder);
            string shown = text + (focused && (System.Environment.TickCount / 500) % 2 == 0 ? "_" : "");
            sb.DrawString(f, shown, new Vector2(r.X + 6, r.Center.Y - f.MeasureString("X").Y / 2), Color.White);

            return r.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y) && inp.LeftClick;
        }

        /// <summary>A reusable vertical scrollbar. Draws a groove + thumb in <paramref name="track"/> and
        /// returns the (possibly drag-adjusted) scroll offset, clamped to [0, contentH-viewH]. The caller
        /// owns the wheel (it knows its own hover region) and a persistent <paramref name="dragging"/> flag
        /// so a grab keeps tracking even when the cursor drifts off the track horizontally.</summary>
        public static float VScrollbar(PrimitiveBatch pb, Rectangle track, float scroll, float viewH,
                                       float contentH, InputState inp, ref bool dragging)
        {
            float max = Math.Max(0, contentH - viewH);
            pb.FillRect(track, new Color(16, 22, 34, 220));     // groove (always drawn so the gutter reads as scrollable)
            if (max <= 0) { dragging = false; return 0; }

            scroll = Math.Clamp(scroll, 0, max);
            float thumbH = Math.Max(24f, track.Height * (viewH / contentH));
            float travel = track.Height - thumbH;

            if (inp.LeftClick && track.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y)) dragging = true;
            if (!inp.LeftDown) dragging = false;
            if (dragging && travel > 0)
            {
                float top = inp.MousePos.Y - track.Y - thumbH / 2f;   // center the thumb on the cursor
                scroll = Math.Clamp(top / travel, 0, 1) * max;
            }

            float thumbY = track.Y + (max > 0 ? scroll / max : 0) * travel;
            var thumb = new Rectangle(track.X + 1, (int)thumbY, track.Width - 2, (int)thumbH);
            pb.FillRect(thumb, dragging ? new Color(110, 160, 210, 240) : new Color(70, 95, 130, 230));
            pb.RectOutline(thumb, 1, PanelBorder);
            return scroll;
        }

        public static void Bar(PrimitiveBatch pb, Rectangle r, float frac, Color fill)
        {
            pb.FillRect(r, new Color(20, 26, 38, 220));
            frac = Math.Clamp(frac, 0, 1);
            if (frac > 0) pb.FillRect(r.X + 1, r.Y + 1, (r.Width - 2) * frac, r.Height - 2, fill);
            pb.RectOutline(r, 1, PanelBorder);
        }

        public static string Dist(double m)
        {
            double a = Math.Abs(m);
            if (a < 10_000) return $"{m:0} m";
            if (a < 10_000_000) return $"{m / 1e3:0.0} km";
            if (a < 1e10) return $"{m / 1e6:0.0} Mm";
            return $"{m / 1e9:0.00} Gm";
        }

        public static string Speed(double v) =>
            Math.Abs(v) < 10_000 ? $"{v:0.0} m/s" : $"{v / 1000:0.00} km/s";

        public static string Force(double n)
        {
            double a = Math.Abs(n);
            if (a < 1_000) return $"{n:0} N";
            if (a < 1_000_000) return $"{n / 1e3:0.0} kN";
            return $"{n / 1e6:0.00} MN";
        }

        public static string Accel(double a) => $"{a:0.0} m/s2";

        public static string Pressure(double pa)
        {
            double a = Math.Abs(pa);
            if (a < 1_000) return $"{pa:0} Pa";
            return $"{pa / 1e3:0.0} kPa";
        }

        public static string Time(double s)
        {
            if (s < 0) s = 0;
            long t = (long)s;
            long d = t / 86400, h = t / 3600 % 24, m = t / 60 % 60, sec = t % 60;
            return d > 0 ? $"{d}d {h:00}:{m:00}:{sec:00}" : $"{h:00}:{m:00}:{sec:00}";
        }
    }
}

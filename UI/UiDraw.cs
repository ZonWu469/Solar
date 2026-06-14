using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Solar.Core;
using Solar.Parts;
using Solar.Rendering;
using Solar.Vessels;

namespace Solar.UI
{
    /// <summary>Shared immediate-mode UI helpers and number formatting.</summary>
    public static class UiDraw
    {
        public static readonly Color PanelBg = new Color(12, 18, 30, 215);
        public static readonly Color PanelBorder = new Color(70, 95, 130);
        public static readonly Color TextDim = new Color(150, 165, 185);
        public static readonly Color Accent = new Color(120, 210, 255);
        public static readonly Color StatusOn = new Color(120, 220, 120);
        public static readonly Color StatusOff = new Color(210, 90, 80);

        /// <summary>Draw a module/part icon fitted to <paramref name="rect"/>: the texture if present,
        /// else a flat <paramref name="tint"/> swatch. When <paramref name="dim"/> the colour is darkened
        /// (used to show an inactive/non-functioning module).</summary>
        public static void Icon(PrimitiveBatch pb, Texture2D tex, Rectangle rect, Color tint, bool dim = false)
        {
            Color c = dim ? new Color((int)(tint.R * 0.38f), (int)(tint.G * 0.38f), (int)(tint.B * 0.38f), (int)tint.A) : tint;
            if (tex != null)
                pb.TexturedQuad(tex, new Vector2(rect.Left, rect.Top), new Vector2(rect.Right, rect.Top),
                                new Vector2(rect.Right, rect.Bottom), new Vector2(rect.Left, rect.Bottom), c);
            else
                pb.FillRect(rect, c);
        }

        /// <summary>Floating tooltip for a slot module: name, flavour description, and effect line.
        /// Repositions to stay on screen. Reused by the editor slot list, the module picker, and the
        /// flight HUD so the three never describe a module differently.</summary>
        public static void ModuleTooltip(PrimitiveBatch pb, SpriteBatch sb, SpriteFont f, ModuleDef m,
                                         Vector2 anchor, int w, int h)
        {
            const float small = 0.8f;
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(m.Description)) lines.Add(m.Description);
            lines.Add(m.StatLine);

            float lhTitle = f.MeasureString("X").Y + 2;
            float lhSmall = f.MeasureString("X").Y * small + 2;
            float tw = f.MeasureString(m.Name).X;
            foreach (var ln in lines) tw = Math.Max(tw, f.MeasureString(ln).X * small);
            int bw = (int)tw + 18, bh = (int)(lhTitle + lines.Count * lhSmall) + 12;
            int bx = (int)anchor.X + 16, by = (int)anchor.Y + 14;
            if (bx + bw > w - 4) bx = (int)anchor.X - bw - 12;
            if (by + bh > h - 4) by = h - 4 - bh;
            if (bx < 4) bx = 4; if (by < 4) by = 4;

            Panel(pb, new Rectangle(bx, by, bw, bh));
            float ty = by + 6;
            sb.DrawString(f, m.Name, new Vector2(bx + 9, ty), Color.White);
            ty += lhTitle;
            for (int i = 0; i < lines.Count; i++)
            {
                SmallText(sb, f, lines[i], new Vector2(bx + 9, ty), i == 0 ? TextDim : Accent, small);
                ty += lhSmall;
            }
        }

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

        /// <summary>Draw text at a reduced scale (the bundled SpriteFont has a single baked size, so
        /// smaller UI text is done by scaling). Returns the rendered width so callers can lay out beside it.</summary>
        public static float SmallText(SpriteBatch sb, SpriteFont f, string text, Vector2 pos, Color c, float scale = 0.8f)
        {
            sb.DrawString(f, text, pos, c, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            return f.MeasureString(text).X * scale;
        }

        // ---- staging list (shared by the editor stats panel and the flight HUD so they read identically) ----

        /// <summary>A small procedural glyph for what a stage does, leading with its separation event:
        /// a strap-on jettison = two side bars (the radial tell), an axial decoupler = split bars, an
        /// ignition = a flame triangle, a parachute = a dome. Drawn inside <paramref name="r"/>.</summary>
        public static void StageIcon(PrimitiveBatch pb, Rectangle r, StageStat st)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            if (st.RadialEvent)   // two vertical side bars = strap-ons flying off
            {
                Color c = new Color(120, 210, 255);
                pb.FillRect(x, y, w * 0.28f, h, c);
                pb.FillRect(x + w * 0.72f, y, w * 0.28f, h, c);
                pb.FillRect(x + w * 0.44f, y + h * 0.35f, w * 0.12f, h * 0.3f, new Color(90, 110, 140));
            }
            else if (st.AxialDecouple)   // a horizontal split = stack separation
            {
                Color c = new Color(210, 195, 90);
                pb.FillRect(x, y, w, h * 0.32f, c);
                pb.FillRect(x, y + h * 0.62f, w, h * 0.32f, c);
            }
            else if (st.Chute && st.Ignites.Count == 0)   // dome = parachute
            {
                Color c = new Color(235, 140, 70);
                pb.FillCircle(new Vector2(x + w / 2, y + h * 0.5f), w * 0.42f, c);
                pb.FillRect(x + w * 0.46f, y + h * 0.5f, w * 0.08f, h * 0.5f, new Color(180, 180, 180));
            }
            else   // flame triangle = engines ignite / burn
            {
                Color c = new Color(255, 170, 70);
                pb.Tri(new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w / 2, y + h), c);
            }
        }

        /// <summary>One-line description of the parts a stage ignites/drops, e.g.
        /// "drop 2x Thumper-R   ignite Terrier" — empty when the stage neither ignites nor drops anything.</summary>
        public static string StageDetail(StageStat st)
        {
            var parts = new List<string>();
            if (st.Drops.Count > 0) parts.Add("drop " + string.Join(" + ", st.Drops));
            if (st.Ignites.Count > 0) parts.Add("ignite " + string.Join(" + ", st.Ignites));
            return string.Join("   ", parts);
        }

        /// <summary>A procedural SAS-mode button glyph drawn inside <paramref name="r"/>. <paramref name="icon"/>
        /// is the SasMode index. The glyph is a bright mode colour when usable, dimmed when unavailable; the
        /// box border highlights when the mode is active and lightens on hover. Font is ASCII-only, so every
        /// symbol is built from primitives.</summary>
        public static void SasIcon(PrimitiveBatch pb, Rectangle r, int icon, bool active, bool available, bool hover)
        {
            Color bg = active ? new Color(40, 60, 90, 230) : hover ? new Color(28, 38, 56, 230) : new Color(20, 26, 38, 220);
            pb.FillRect(r, bg);
            pb.RectOutline(r, 1, active ? Accent : available ? PanelBorder : new Color(50, 60, 75));

            Color modeCol = icon switch
            {
                2 => new Color(120, 255, 120),    // prograde
                3 => new Color(255, 110, 100),    // retrograde
                4 or 5 => new Color(120, 210, 255), // radial in/out
                6 or 7 => new Color(235, 130, 235), // target / anti-target
                8 => new Color(255, 170, 235),    // kill relative
                9 => new Color(120, 210, 255),    // maneuver
                _ => Accent,                       // stability
            };
            Color fg = !available ? new Color(70, 80, 95) : modeCol;

            float x = r.X, y = r.Y, w = r.Width, h = r.Height, cx = x + w / 2, cy = y + h / 2;
            switch (icon)
            {
                case 0: // Off: a stop square
                    pb.FillRect(x + w * 0.32f, y + h * 0.32f, w * 0.36f, h * 0.36f, !available ? fg : TextDim);
                    break;
                case 1: // Stability: hollow box (hold current attitude)
                    pb.RectOutline(new Rectangle((int)(x + w * 0.28f), (int)(y + h * 0.28f), (int)(w * 0.44f), (int)(h * 0.44f)), 2, fg);
                    break;
                case 2: // Prograde: up triangle
                    pb.Tri(new Vector2(cx, y + h * 0.2f), new Vector2(x + w * 0.22f, y + h * 0.78f), new Vector2(x + w * 0.78f, y + h * 0.78f), fg);
                    break;
                case 3: // Retrograde: down triangle
                    pb.Tri(new Vector2(x + w * 0.22f, y + h * 0.22f), new Vector2(x + w * 0.78f, y + h * 0.22f), new Vector2(cx, y + h * 0.8f), fg);
                    break;
                case 4: // Radial in: left triangle
                    pb.Tri(new Vector2(x + w * 0.22f, cy), new Vector2(x + w * 0.74f, y + h * 0.24f), new Vector2(x + w * 0.74f, y + h * 0.76f), fg);
                    break;
                case 5: // Radial out: right triangle
                    pb.Tri(new Vector2(x + w * 0.78f, cy), new Vector2(x + w * 0.26f, y + h * 0.24f), new Vector2(x + w * 0.26f, y + h * 0.76f), fg);
                    break;
                case 6: // Target: crosshair ring with filled centre
                    pb.CircleOutline(new Vector2(cx, cy), w * 0.32f, 1.5f, fg);
                    pb.FillCircle(new Vector2(cx, cy), w * 0.1f, fg);
                    break;
                case 7: // Anti-target: hollow crosshair ring with tick marks
                    pb.CircleOutline(new Vector2(cx, cy), w * 0.32f, 1.5f, fg);
                    pb.Line(new Vector2(x + w * 0.18f, cy), new Vector2(x + w * 0.82f, cy), 1.5f, fg);
                    break;
                case 8: // Kill relative: two opposed chevrons (converge)
                    pb.Line(new Vector2(x + w * 0.2f, y + h * 0.3f), new Vector2(cx, cy), 1.5f, fg);
                    pb.Line(new Vector2(x + w * 0.8f, y + h * 0.3f), new Vector2(cx, cy), 1.5f, fg);
                    pb.Line(new Vector2(x + w * 0.2f, y + h * 0.7f), new Vector2(cx, cy), 1.5f, fg);
                    pb.Line(new Vector2(x + w * 0.8f, y + h * 0.7f), new Vector2(cx, cy), 1.5f, fg);
                    break;
                case 9: // Maneuver: filled dot inside a ring (the burn marker)
                    pb.CircleOutline(new Vector2(cx, cy), w * 0.3f, 1.5f, fg);
                    pb.FillCircle(new Vector2(cx, cy), w * 0.16f, fg);
                    break;
            }
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

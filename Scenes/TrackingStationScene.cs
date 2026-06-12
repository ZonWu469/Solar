using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Solar.Core;
using Solar.Physics;
using Solar.Rendering;
using Solar.UI;
using Solar.Vessels;

namespace Solar.Scenes
{
    /// <summary>KSP-style tracking station: a map of the solar system with every saved ship at its
    /// current orbit. Click a ship (marker or list) to resume flying it.</summary>
    public sealed class TrackingStationScene : Scene
    {
        private const int PanelW = 260;

        private readonly Camera2D _cam = new();
        private int _focus;                 // 0 = system (Sun), else body index + 1
        private double _zoom;
        private readonly List<Vessel> _ships = new();   // live rebuilds for drawing/picking

        public TrackingStationScene(GameContext ctx) : base(ctx) { }

        public override void Enter()
        {
            RebuildShips();
            var earth = Ctx.Universe["Earth"];
            // frame the inner system by default
            _zoom = (earth.Orbit.A * 2.6) / Math.Min(Ctx.W, Ctx.H);
            _focus = 0;
        }

        private void RebuildShips()
        {
            _ships.Clear();
            double ut = Ctx.Clock.UT;
            foreach (var s in Ctx.State.Ships)
            {
                var v = s.ToVessel(Ctx.Universe, Ctx.State.Roster);
                if (v.Body == null) continue;
                if (v.OnRails) v.UpdateFromRails(ut);
                _ships.Add(v);
            }
        }

        public override void Update(double dt)
        {
            var inp = Ctx.Input;
            if (inp.Pressed(Keys.Escape)) { Ctx.Scenes.SwitchTo(new HubScene(Ctx)); return; }
            if (inp.Pressed(Keys.Tab)) _focus = (_focus + 1) % (Ctx.Universe.Bodies.Count + 1);

            if (inp.WheelDelta != 0)
            {
                double f = inp.WheelDelta > 0 ? 0.85 : 1.0 / 0.85;
                _zoom = Math.Clamp(_zoom * f, 5.0, 5e9);
            }

            // pick a ship marker (right of the list panel)
            if (inp.LeftClick && inp.MousePos.X > PanelW)
            {
                int hit = PickShip(inp.MousePos);
                if (hit >= 0) { ResumeShip(hit); return; }
            }
        }

        private int PickShip(Vector2 mouse)
        {
            double ut = Ctx.Clock.UT;
            for (int i = 0; i < _ships.Count; i++)
            {
                var v = _ships[i];
                var s = _cam.WorldToScreen(v.AbsolutePosition(ut));
                if (Vector2.Distance(s, mouse) <= 12f) return i;
            }
            return -1;
        }

        private void ResumeShip(int index)
        {
            if (index < 0 || index >= Ctx.State.Ships.Count) return;
            Ctx.Scenes.SwitchTo(new FlightScene(Ctx, Ctx.State.Ships[index]));
        }

        public override void Draw()
        {
            var pb = Ctx.Pb; var sb = Ctx.Sb; var f = Ctx.Font;
            int w = Ctx.W, h = Ctx.H;
            double ut = Ctx.Clock.UT;

            _cam.SetViewport(w, h);
            _cam.MetersPerPixel = _zoom;
            _cam.Center = _focus == 0
                ? Ctx.Universe["Sun"].AbsolutePositionAt(ut)
                : Ctx.Universe.Bodies[_focus - 1].AbsolutePositionAt(ut);

            pb.Begin();
            sb.Begin();

            Ctx.Stars.Draw(pb, w, h, _cam.Center);
            DrawSystem(pb, sb, ut);
            DrawShips(pb, sb, ut);
            DrawPanel(pb, sb, f, w, h);

            pb.End();
            sb.End();
        }

        private void DrawSystem(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            var u = Ctx.Universe;
            foreach (var b in u.Bodies)
            {
                if (b.Parent == null) continue;
                OrbitRenderer.DrawConic(pb, _cam, b.Orbit, b.Parent.AbsolutePositionAt(ut), b.BodyColor * 0.4f, 1.2f);
            }
            foreach (var b in u.Bodies)
                PlanetRenderer.Draw(pb, _cam, b, ut, true, sb, Ctx.Textures.Body(b.TextureId));
            foreach (var b in u.Bodies)
            {
                var s = _cam.WorldToScreenD(b.AbsolutePositionAt(ut));
                if (!_cam.OnScreen(s, 0)) continue;
                double orbitPx = b.Parent == null ? double.MaxValue : b.Orbit.A / _cam.MetersPerPixel;
                if (orbitPx < 40) continue;
                sb.DrawString(Ctx.Font, b.Name, new Vector2((float)s.X + 8, (float)s.Y - 18), b.BodyColor * 0.9f);
            }
        }

        private void DrawShips(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            var col = new Color(120, 220, 255);
            for (int i = 0; i < _ships.Count; i++)
            {
                var v = _ships[i];
                Vec2d primaryAbs = v.Body.AbsolutePositionAt(ut);
                if (!v.Landed && !v.Destroyed)
                {
                    var el = v.CurrentElements(ut);
                    if (!double.IsNaN(el.A) && !double.IsNaN(el.E))
                        OrbitRenderer.DrawConic(pb, _cam, el, primaryAbs, col * 0.7f, 1.4f, v.Body.SoiRadius);
                }
                var s = _cam.WorldToScreen(v.AbsolutePosition(ut));
                bool hover = Vector2.Distance(s, Ctx.Input.MousePos) <= 12f;
                pb.FillCircle(s, hover ? 7f : 5f, col);
                sb.DrawString(Ctx.Font, Ctx.State.Ships[i].Name, s + new Vector2(9, -8), col);
            }
        }

        private void DrawPanel(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
                               Microsoft.Xna.Framework.Graphics.SpriteFont f, int w, int h)
        {
            UiDraw.Panel(pb, new Rectangle(8, 8, PanelW - 16, h - 16));
            sb.DrawString(Ctx.FontBig, "TRACKING", new Vector2(20, 16), new Color(200, 215, 235));
            sb.DrawString(f, "[Tab] cycle focus  [wheel] zoom", new Vector2(20, 48), UiDraw.TextDim);

            int y = 76;
            if (Ctx.State.Ships.Count == 0)
                sb.DrawString(f, "No ships yet. Build one!", new Vector2(20, y), UiDraw.TextDim);

            for (int i = 0; i < Ctx.State.Ships.Count && i < 16; i++)
            {
                var ship = Ctx.State.Ships[i];
                string label = ship.Landed ? $"{ship.Name}  (landed {ship.BodyName})"
                                           : $"{ship.Name}  ({ship.BodyName})";
                if (UiDraw.Button(pb, sb, f, new Rectangle(16, y, PanelW - 32, 34), label, Ctx.Input))
                { ResumeShip(i); return; }
                y += 40;
            }

            if (UiDraw.Button(pb, sb, f, new Rectangle(16, h - 52, PanelW - 32, 36), "BACK  [Esc]", Ctx.Input))
                Ctx.Scenes.SwitchTo(new HubScene(Ctx));
        }
    }
}

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Solar.Core;
using Solar.Progression;
using Solar.UI;

namespace Solar.Scenes
{
    /// <summary>In-game hub for a loaded save: build a rocket, open the tracking station,
    /// save, or return to the title screen.</summary>
    public sealed class HubScene : Scene
    {
        private string _toast;
        private double _toastTimer;

        public HubScene(GameContext ctx) : base(ctx) { }

        public override void Update(double dt)
        {
            if (_toastTimer > 0) _toastTimer -= dt;
            if (Ctx.Input.Pressed(Keys.Escape)) Ctx.Scenes.SwitchTo(new TitleScene(Ctx));
        }

        private void SaveNow()
        {
            // pull the working design + clock back into the savegame, then persist
            Ctx.State.Design = DesignState.From(Ctx.Design);
            Ctx.State.UT = Ctx.Clock.UT;
            SaveGame.Save(Ctx.State);
            _toast = $"Saved \"{Ctx.State.Name}\"";
            _toastTimer = 2.5;
        }

        public override void Draw()
        {
            var pb = Ctx.Pb; var sb = Ctx.Sb; var f = Ctx.Font;
            int w = Ctx.W, h = Ctx.H;
            pb.Begin();
            sb.Begin();

            Ctx.Stars.Draw(pb, w, h, Vec2d.Zero);
            pb.FillCircle(new Vector2(w / 2f, h + h * 0.55f), h * 0.78f, new Color(255, 215, 120) * 0.9f, new Color(255, 120, 40) * 0.0f);

            string title = "S O L A R";
            var tsz = Ctx.FontBig.MeasureString(title);
            sb.DrawString(Ctx.FontBig, title, new Vector2(w / 2 - tsz.X / 2, h * 0.18f), Color.White);
            string sub = $"game: {Ctx.State.Name}    ships: {Ctx.State.Ships.Count}";
            var ssz = f.MeasureString(sub);
            sb.DrawString(f, sub, new Vector2(w / 2 - ssz.X / 2, h * 0.18f + tsz.Y + 8), UiDraw.TextDim);

            int bw = 300, bh = 44, bx = w / 2 - bw / 2;
            int by = (int)(h * 0.38f);

            // Defer scene switches until after End() so the shared batch is balanced this frame.
            Scene next = null;
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "BUILD ROCKET", Ctx.Input))
                next = new EditorScene(Ctx);
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by + bh + 10, bw, bh), "TRACKING STATION", Ctx.Input))
                next = new TrackingStationScene(Ctx);
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by + 2 * (bh + 10), bw, bh), "RESEARCH & DEVELOPMENT", Ctx.Input))
                next = new RDScene(Ctx);
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by + 3 * (bh + 10), bw, bh), "SAVE GAME", Ctx.Input))
                SaveNow();
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by + 4 * (bh + 10), bw, bh), "TITLE  [Esc]", Ctx.Input))
                next = new TitleScene(Ctx);

            if (_toastTimer > 0 && _toast != null)
            {
                var z = f.MeasureString(_toast);
                sb.DrawString(f, _toast, new Vector2(w / 2 - z.X / 2, by + 5 * (bh + 10) + 6), UiDraw.Accent);
            }

            // objectives panel: shows exactly what pays out science (milestones + instrument transmits)
            {
                var gs = Ctx.State;
                var remaining = Milestones.All.FindAll(m => !gs.CompletedMilestones.Contains(m.Id));
                int px = 20, py = 96, panW = 340;
                int panH = 78 + System.Math.Min(14, System.Math.Max(remaining.Count, 1)) * 20;
                UiDraw.Panel(pb, new Rectangle(px, py, panW, panH));
                sb.DrawString(f, $"SCIENCE: {gs.Science:0}", new Vector2(px + 12, py + 10), new Color(150, 230, 150));
                sb.DrawString(f, "OBJECTIVES  (earn science)", new Vector2(px + 12, py + 30), UiDraw.TextDim);
                int yy = py + 52;
                foreach (var m in remaining)
                {
                    sb.DrawString(f, $"+{m.Reward:0}", new Vector2(px + 12, yy), UiDraw.Accent);
                    sb.DrawString(f, m.Title, new Vector2(px + 52, yy), Color.White);
                    yy += 20;
                }
                sb.DrawString(f, remaining.Count == 0 ? "All milestones complete!" : "...plus instrument readings via Antenna",
                              new Vector2(px + 12, yy + 2), UiDraw.TextDim);
            }

            sb.DrawString(f, Ctx.SelfTest, new Vector2(12, h - 26), UiDraw.TextDim);

            pb.End();
            sb.End();

            if (next != null) Ctx.Scenes.SwitchTo(next);
        }
    }
}

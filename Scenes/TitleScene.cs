using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Solar.Core;
using Solar.UI;

namespace Solar.Scenes
{
    /// <summary>Top-level menu shown at launch: New Game, Load Game, Exit.</summary>
    public sealed class TitleScene : Scene
    {
        private bool _naming;
        private string _newName = "game";

        public TitleScene(GameContext ctx) : base(ctx) { }

        public TitleScene(GameContext ctx, bool naming) : base(ctx) { _naming = naming; }

        public override void Update(double dt)
        {
            var inp = Ctx.Input;
            if (inp.Pressed(Keys.Escape))
            {
                if (_naming) _naming = false;
                else Ctx.Game.Exit();
            }
            if (_naming && inp.Pressed(Keys.Enter)) StartNewGame();
        }

        private void StartNewGame()
        {
            Ctx.State = GameState.NewGame(_newName);
            Ctx.State.Design.ApplyTo(Ctx.Design);
            Ctx.Clock.UT = Ctx.State.UT;
            Ctx.Clock.DropToRealtime();
            Ctx.Scenes.SwitchTo(new HubScene(Ctx));
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
            sb.DrawString(Ctx.FontBig, title, new Vector2(w / 2 - tsz.X / 2, h * 0.22f), Color.White);
            string sub = "build a rocket - project orbits - explore the solar system";
            var ssz = f.MeasureString(sub);
            sb.DrawString(f, sub, new Vector2(w / 2 - ssz.X / 2, h * 0.22f + tsz.Y + 8), UiDraw.TextDim);

            int bw = 280, bh = 44, bx = w / 2 - bw / 2;
            int by = (int)(h * 0.46f);

            if (_naming)
            {
                sb.DrawString(f, "Name this game:", new Vector2(bx, by - 6), UiDraw.TextDim);
                UiDraw.TextField(pb, sb, f, new Rectangle(bx, by + 16, bw, 38), ref _newName, true, Ctx.Input);
                if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by + 64, bw, bh), "CREATE  [Enter]", Ctx.Input))
                    StartNewGame();
                if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by + 64 + bh + 10, bw, bh), "BACK  [Esc]", Ctx.Input))
                    _naming = false;
            }
            else
            {
                if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "NEW GAME", Ctx.Input))
                { _naming = true; _newName = "game"; }
                if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by + bh + 12, bw, bh), "LOAD GAME", Ctx.Input))
                    Ctx.Scenes.SwitchTo(new LoadGameScene(Ctx));
                if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by + 2 * (bh + 12), bw, bh), "EXIT  [Esc]", Ctx.Input))
                    Ctx.Game.Exit();
            }

            sb.DrawString(f, Ctx.SelfTest, new Vector2(12, h - 26), UiDraw.TextDim);

            pb.End();
            sb.End();
        }
    }
}

using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Solar.Core;
using Solar.UI;

namespace Solar.Scenes
{
    /// <summary>Lists the JSON saves under saves/ and resumes the chosen one.</summary>
    public sealed class LoadGameScene : Scene
    {
        private List<string> _saves;

        public LoadGameScene(GameContext ctx) : base(ctx) { }

        public override void Enter() => _saves = SaveGame.List();

        public override void Update(double dt)
        {
            if (Ctx.Input.Pressed(Keys.Escape)) Ctx.Scenes.SwitchTo(new TitleScene(Ctx));
        }

        private void LoadAndPlay(string name)
        {
            var state = SaveGame.Load(name);
            if (state == null) return;
            Ctx.State = state;
            state.Design.ApplyTo(Ctx.Design);
            Ctx.Clock.UT = state.UT;
            Ctx.Clock.DropToRealtime();
            Solar.Physics.AsteroidField.Sync(Ctx.Universe, Ctx.State);
            Ctx.Scenes.SwitchTo(new HubScene(Ctx));
        }

        public override void Draw()
        {
            var pb = Ctx.Pb; var sb = Ctx.Sb; var f = Ctx.Font;
            int w = Ctx.W, h = Ctx.H;
            pb.Begin();
            sb.Begin();

            Ctx.Stars.Draw(pb, w, h, Vec2d.Zero);
            sb.DrawString(Ctx.FontBig, "LOAD GAME", new Vector2(w / 2 - 110, h * 0.14f), Color.White);

            int bw = 360, bh = 40, bx = w / 2 - bw / 2;
            int by = (int)(h * 0.26f);

            if (_saves == null || _saves.Count == 0)
            {
                string msg = "No saved games yet.";
                var sz = f.MeasureString(msg);
                sb.DrawString(f, msg, new Vector2(w / 2 - sz.X / 2, by), UiDraw.TextDim);
            }
            string loadName = null;
            if (_saves != null)
            {
                int shown = 0;
                foreach (var name in _saves)
                {
                    if (shown >= 10) break;
                    if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by + shown * (bh + 8), bw, bh), name, Ctx.Input))
                        loadName = name;
                    shown++;
                }
            }

            bool back = UiDraw.Button(pb, sb, f, new Rectangle(bx, h - 70, bw, bh), "BACK  [Esc]", Ctx.Input);

            pb.End();
            sb.End();

            // Switch only after End() so the shared batch is balanced this frame.
            if (loadName != null) LoadAndPlay(loadName);
            else if (back) Ctx.Scenes.SwitchTo(new TitleScene(Ctx));
        }
    }
}

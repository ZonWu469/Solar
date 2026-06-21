using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Solar.Core;
using Solar.Parts;
using Solar.Physics;
using Solar.Rendering;
using Solar.Scenes;
using Solar.Tests;
using Solar.Vessels;

namespace Solar
{
    public class SolarGame : Game
    {
        private readonly GraphicsDeviceManager _graphics;
        private GameContext _ctx;

        public SolarGame()
        {
            _graphics = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = 1600,
                PreferredBackBufferHeight = 900,
            };
            Content.RootDirectory = "Content";
            IsMouseVisible = true;
            Window.AllowUserResizing = true;
            Window.Title = "Solar";
        }

        protected override void LoadContent()
        {
            PartCatalog.Load();   // load Content/parts.json (or seed from built-in catalog)
            ModuleCatalog.Load(); // load Content/modules.json (or seed from built-in catalog)
            Solar.Physics.BodyCatalog.Load(); // load Content/bodies.json (or seed from built-in solar system)
            Solar.Core.Balance.Load();        // load Content/balance.json hazard/life-support tunables (or keep code defaults)
            _ctx = new GameContext
            {
                Game = this,
                Gd = GraphicsDevice,
                Pb = new PrimitiveBatch(GraphicsDevice),
                Sb = new SpriteBatch(GraphicsDevice),
                Font = Content.Load<SpriteFont>("Hud"),
                FontBig = Content.Load<SpriteFont>("HudBig"),
                Universe = SolarSystemData.Create(),
                Stars = new StarfieldRenderer(),
            };
            _ctx.Textures = new TextureStore(Content);
            _ctx.Design.Stack = PartCatalog.DefaultDesign().ConvertAll(d => new StackEntry(d));
            _ctx.SelfTest = SanityChecks.Run();
            Window.TextInput += (_, e) => _ctx.Input.OnTextInput(e.Character);
            _ctx.Scenes.SwitchTo(new TitleScene(_ctx));
        }

        protected override void Update(GameTime gameTime)
        {
            _ctx.Input.Update();
            _ctx.Scenes.Current?.Update(gameTime.ElapsedGameTime.TotalSeconds);
            base.Update(gameTime);
        }

        protected override void Draw(GameTime gameTime)
        {
            GraphicsDevice.Clear(new Color(5, 7, 13));
            _ctx.Scenes.Current?.Draw();
            base.Draw(gameTime);
        }
    }
}

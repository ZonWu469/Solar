using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Solar.Physics;
using Solar.Rendering;
using Solar.Vessels;

namespace Solar.Core
{
    /// <summary>Shared services passed to every scene.</summary>
    public sealed class GameContext
    {
        public Game Game;
        public GraphicsDevice Gd;
        public PrimitiveBatch Pb;
        public SpriteBatch Sb;
        public SpriteFont Font;
        public SpriteFont FontBig;
        public InputState Input = new();
        public SimClock Clock = new();
        public Universe Universe;
        public VesselDesign Design = new();
        public GameState State = new();   // the active savegame (ships, UT, design)
        public SceneManager Scenes = new();
        public StarfieldRenderer Stars;
        public TextureStore Textures;
        public string SelfTest = "";

        public int W => Gd.Viewport.Width;
        public int H => Gd.Viewport.Height;
    }
}

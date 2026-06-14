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

        /// <summary>Set by a colony's "Build vessel" action so the next editor launch spawns the new craft
        /// landed beside that base instead of on the home pad; consumed (cleared) by <c>FlightScene</c>.</summary>
        public LaunchSite? PendingLaunchSite;

        public int W => Gd.Viewport.Width;
        public int H => Gd.Viewport.Height;
    }

    /// <summary>Where a freshly-built vessel should be placed at launch: on a body's surface at a given
    /// angle, a few metres beside an existing base so it can immediately surface-dock.</summary>
    public struct LaunchSite
    {
        public string BodyName;
        public Vec2d Position;   // relative to the body centre (on the surface)
        public double Heading;   // world angle of the vessel's up axis
    }
}

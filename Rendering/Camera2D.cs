using Microsoft.Xna.Framework;
using Solar.Core;

namespace Solar.Rendering
{
    /// <summary>
    /// Double-precision world camera. World->screen conversion happens entirely in
    /// doubles (floating origin at Center), so float precision never touches
    /// planetary-scale coordinates.
    /// </summary>
    public sealed class Camera2D
    {
        public Vec2d Center;
        public double MetersPerPixel = 1.0;
        public int ScreenW, ScreenH;

        public void SetViewport(int w, int h) { ScreenW = w; ScreenH = h; }

        /// <summary>Screen position in pixel coordinates, computed in doubles (Y down).</summary>
        public Vec2d WorldToScreenD(Vec2d world)
        {
            double sx = ScreenW * 0.5 + (world.X - Center.X) / MetersPerPixel;
            double sy = ScreenH * 0.5 - (world.Y - Center.Y) / MetersPerPixel;
            return new Vec2d(sx, sy);
        }

        public Vector2 WorldToScreen(Vec2d world)
        {
            var s = WorldToScreenD(world);
            return new Vector2((float)s.X, (float)s.Y);
        }

        public Vec2d ScreenToWorld(Vector2 screen)
        {
            return new Vec2d(
                Center.X + (screen.X - ScreenW * 0.5) * MetersPerPixel,
                Center.Y + (ScreenH * 0.5 - screen.Y) * MetersPerPixel);
        }

        /// <summary>True if a screen-space point (doubles) is within the screen expanded by margin px.</summary>
        public bool OnScreen(Vec2d s, double margin) =>
            s.X > -margin && s.X < ScreenW + margin && s.Y > -margin && s.Y < ScreenH + margin;
    }
}

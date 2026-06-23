using System;
using Microsoft.Xna.Framework;
using Solar.Core;
using Solar.Vessels;

namespace Solar.Rendering
{
    /// <summary>Draws an EVA kerbal: a role sprite from the Player/ content folder, or a procedural
    /// astronaut when no art is present. Sprites are authored facing LEFT, so they are mirrored
    /// (<c>flipX</c>) when the kerbal is moving/looking right.</summary>
    public static class EvaRenderer
    {
        public static void Draw(PrimitiveBatch pb, Camera2D cam, Vessel v, double ut, TextureStore tex = null)
        {
            if (v == null || v.Destroyed || !v.IsEva) return;
            Vector2 s = cam.WorldToScreen(v.AbsolutePosition(ut));
            float px = (float)(Eva.KerbalDef.Height / cam.MetersPerPixel);
            float half = Math.Clamp(px, 6f, 160f) * 0.5f;

            // face the direction of travel (default art faces left); fall back to the heading's x-sign
            double face = v.Velocity.LengthSquared > 0.04 ? v.Velocity.X : Math.Cos(v.Heading);
            bool right = face > 0;

            var sprite = tex?.Player(RoleId(v.EvaRole));
            if (sprite != null)
            {
                var a = new Vector2(s.X - half, s.Y - half);
                var b = new Vector2(s.X + half, s.Y - half);
                var c = new Vector2(s.X + half, s.Y + half);
                var d = new Vector2(s.X - half, s.Y + half);
                pb.TexturedQuad(sprite, a, b, c, d, Color.White, flipX: right);
            }
            else
            {
                // procedural astronaut: helmet + torso + a little jetpack nub
                Color suit = RoleColor(v.EvaRole);
                pb.FillCircle(new Vector2(s.X, s.Y - half * 0.4f), half * 0.55f, new Color(180, 210, 230)); // helmet
                pb.FillRect(s.X - half * 0.45f, s.Y - half * 0.1f, half * 0.9f, half * 1.0f, suit);          // torso
                pb.FillRect(s.X + (right ? half * 0.45f : -half * 0.7f), s.Y - half * 0.05f, half * 0.25f, half * 0.7f, new Color(150, 150, 160)); // pack
            }
        }

        private static string RoleId(CrewRole r) => r switch
        {
            CrewRole.Pilot => "pilot",
            CrewRole.Engineer => "engineer",
            _ => "scientist",
        };

        private static Color RoleColor(CrewRole r) => r switch
        {
            CrewRole.Pilot => new Color(230, 200, 90),
            CrewRole.Engineer => new Color(230, 140, 70),
            _ => new Color(120, 190, 230),
        };
    }
}

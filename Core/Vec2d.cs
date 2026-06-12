using System;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace Solar.Core
{
    /// <summary>Double-precision 2D vector for physics; convert to Vector2 only at the render boundary.</summary>
    public struct Vec2d
    {
        public double X, Y;
        public Vec2d(double x, double y) { X = x; Y = y; }

        public static readonly Vec2d Zero = new Vec2d(0, 0);

        [JsonIgnore] public double Length => Math.Sqrt(X * X + Y * Y);
        [JsonIgnore] public double LengthSquared => X * X + Y * Y;

        public static Vec2d operator +(Vec2d a, Vec2d b) => new Vec2d(a.X + b.X, a.Y + b.Y);
        public static Vec2d operator -(Vec2d a, Vec2d b) => new Vec2d(a.X - b.X, a.Y - b.Y);
        public static Vec2d operator -(Vec2d a) => new Vec2d(-a.X, -a.Y);
        public static Vec2d operator *(Vec2d a, double s) => new Vec2d(a.X * s, a.Y * s);
        public static Vec2d operator *(double s, Vec2d a) => new Vec2d(a.X * s, a.Y * s);
        public static Vec2d operator /(Vec2d a, double s) => new Vec2d(a.X / s, a.Y / s);

        public double Dot(Vec2d b) => X * b.X + Y * b.Y;
        public double Cross(Vec2d b) => X * b.Y - Y * b.X;
        public Vec2d Normalized() { double l = Length; return l > 1e-300 ? this / l : Zero; }
        public Vec2d Perp() => new Vec2d(-Y, X); // 90 degrees CCW
        public double Angle() => Math.Atan2(Y, X);
        public static Vec2d FromAngle(double a, double len = 1.0) => new Vec2d(Math.Cos(a) * len, Math.Sin(a) * len);

        public Vector2 ToVector2() => new Vector2((float)X, (float)Y);
        public override string ToString() => $"({X:G6}, {Y:G6})";
    }
}

using System;

namespace Solar.Core
{
    /// <summary>Universal time and time-warp state. Scenes advance UT themselves
    /// (FlightScene needs exact-time stepping for SOI handoffs).</summary>
    public sealed class SimClock
    {
        public static readonly double[] Levels = { 1, 2, 4, 10, 100, 1_000, 10_000, 100_000, 1_000_000 };
        public const int PhysicsMaxIndex = 4; // 4x cap while off-rails

        public double UT;
        public int WarpIndex;
        public int MaxWarpIndex = Levels.Length - 1; // re-set every frame by the scene

        public int EffectiveIndex => Math.Min(WarpIndex, MaxWarpIndex);
        public double Warp => Levels[EffectiveIndex];

        public void WarpUp() { if (WarpIndex < Levels.Length - 1) WarpIndex++; }
        public void WarpDown() { if (WarpIndex > 0) WarpIndex--; }
        public void DropToRealtime() => WarpIndex = 0;
    }
}

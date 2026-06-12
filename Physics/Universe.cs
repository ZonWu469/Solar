using System;
using System.Collections.Generic;

namespace Solar.Physics
{
    /// <summary>The full body hierarchy plus SOI setup.</summary>
    public sealed class Universe
    {
        public CelestialBody Root; // the Sun
        public readonly List<CelestialBody> Bodies = new();

        public CelestialBody this[string name] => Bodies.Find(b => b.Name == name);

        public void Add(CelestialBody b)
        {
            Bodies.Add(b);
            if (b.Parent != null) b.Parent.Children.Add(b);
            else Root = b;
        }

        public void ComputeSoiRadii()
        {
            foreach (var b in Bodies)
                if (b.Parent != null)
                    b.SoiRadius = b.Orbit.A * Math.Pow(b.Mu / b.Parent.Mu, 0.4);
        }
    }
}

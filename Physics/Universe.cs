using System;
using System.Collections.Generic;

namespace Solar.Physics
{
    /// <summary>The full body hierarchy plus SOI setup.</summary>
    public sealed class Universe
    {
        public CelestialBody Root; // the Sun
        public readonly List<CelestialBody> Bodies = new();

        /// <summary>Every generated asteroid (hidden + discovered) for the active game. Hidden asteroids live
        /// here only; discovering one promotes it into <see cref="Bodies"/> (and its parent's Children) via
        /// <see cref="Add"/>, so the heavy systems (rendering, targeting, encounter search) only ever see the
        /// handful the player has found. Populated by <see cref="AsteroidField"/>.</summary>
        public readonly List<CelestialBody> AsteroidCatalog = new();

        public CelestialBody this[string name] => Bodies.Find(b => b.Name == name);

        public void Add(CelestialBody b)
        {
            Bodies.Add(b);
            if (b.Parent != null) b.Parent.Children.Add(b);
            else Root = b;
        }

        /// <summary>Remove every asteroid from the live hierarchy and clear the catalog, so re-syncing a
        /// (different) game starts from a clean universe. The <see cref="Universe"/> instance is shared and
        /// persists across game sessions, so this undoes any previously-promoted asteroids.</summary>
        public void ClearAsteroids()
        {
            foreach (var a in AsteroidCatalog) a.Parent?.Children.Remove(a);
            Bodies.RemoveAll(b => b.IsAsteroid);
            AsteroidCatalog.Clear();
        }

        public void ComputeSoiRadii()
        {
            foreach (var b in Bodies)
                if (b.Parent != null && !b.IsAsteroid)   // asteroids keep their authored SOI (Hill radius would be metres)
                    b.SoiRadius = b.Orbit.A * Math.Pow(b.Mu / b.Parent.Mu, 0.4);
        }
    }
}

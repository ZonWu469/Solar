using System;
using System.Collections.Generic;
using Solar.Core;

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

        /// <summary>The stars: the light-emitting children of the galactic barycenter (<see cref="Root"/>).
        /// In a single-system universe with no barycenter this is empty and <see cref="StarOf"/> falls back
        /// to the root.</summary>
        public IEnumerable<CelestialBody> Stars
        {
            get { if (Root != null) foreach (var c in Root.Children) if (c.IsStar) yield return c; }
        }

        /// <summary>The star whose system <paramref name="b"/> lives in: walk up the parent chain to the
        /// body just below the galactic barycenter. Returns the root itself when the root is a star (a
        /// single-system universe with no barycenter), or null for the barycenter.</summary>
        public CelestialBody StarOf(CelestialBody b)
        {
            if (b == null) return null;
            if (b == Root) return Root.IsStar ? Root : null;          // root is the lone star, or the barycenter
            var cur = b;
            while (cur.Parent != null && cur.Parent != Root) cur = cur.Parent;
            // cur.Parent is null (cur == root, single-system) or Root (cur is a star/under the barycenter)
            return cur.Parent == null ? (cur.IsStar ? cur : null) : cur;
        }

        /// <summary>The star nearest a given absolute position — the dominant light/storm source for a
        /// vessel. Falls back to <see cref="Root"/> when there are no separate stars (a single-system
        /// universe whose root <em>is</em> the star).</summary>
        public CelestialBody NearestStar(Vec2d absPos, double ut)
        {
            CelestialBody best = null;
            double bestD = double.PositiveInfinity;
            foreach (var s in Stars)
            {
                double d = (s.AbsolutePositionAt(ut) - absPos).Length;
                if (d < bestD) { bestD = d; best = s; }
            }
            return best ?? Root;
        }

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

using System;
using System.Collections.Generic;
using Solar.Core;
using Solar.Physics;
using Solar.Vessels;

namespace Solar.Progression
{
    /// <summary>A one-shot achievement that awards science the first time its condition holds.</summary>
    public sealed class Milestone
    {
        public string Id;
        public string Title;
        public double Reward;
        public Func<Vessel, double, GameState, bool> Done;
    }

    /// <summary>Flight milestones that feed the science economy. Evaluated each frame by the flight
    /// scene against the live vessel; <see cref="GameState.CompletedMilestones"/> guards award-once.</summary>
    public static class Milestones
    {
        /// <summary>In a stable closed orbit clear of the atmosphere (not landed) around <paramref name="body"/>.</summary>
        private static bool InOrbit(Vessel v, double ut, string body)
        {
            if (v.Landed || v.Body == null || v.Body.Name != body) return false;
            var el = v.CurrentElements(ut);
            if (el.E >= 1) return false;
            double atmoTop = v.Body.Atmo?.Top ?? 0;
            return el.Periapsis - v.Body.Radius > Math.Max(atmoTop, 5000);
        }

        /// <summary>Vessel is in the SOI of the named body (fly-by condition).</summary>
        private static bool AtBody(Vessel v, string body) =>
            !v.Landed && v.Body != null && v.Body.Name == body;

        /// <summary>Vessel is landed on the named body.</summary>
        private static bool LandedOn(Vessel v, string body) =>
            v.Landed && v.Body != null && v.Body.Name == body;

        public static readonly List<Milestone> All = new()
        {
            // ---- Earth milestones ----
            new Milestone { Id = "suborbital", Title = "First suborbital hop", Reward = 3,
                Done = (v, ut, gs) => !v.Landed && v.Altitude > 5000 && v.Altitude < 30000 },
            new Milestone { Id = "liftoff", Title = "First liftoff", Reward = 5,
                Done = (v, ut, gs) => !v.Landed && v.Altitude > 1000 },
            new Milestone { Id = "space", Title = "Reach space", Reward = 8,
                Done = (v, ut, gs) => v.Body?.Name == "Earth" && v.Altitude > (v.Body.Atmo?.Top ?? 56000) },
            new Milestone { Id = "orbit", Title = "Orbit Earth", Reward = 15,
                Done = (v, ut, gs) => InOrbit(v, ut, "Earth") },
            new Milestone { Id = "return", Title = "Safe return from space", Reward = 20,
                Done = (v, ut, gs) => v.Landed && v.Body?.Name == "Earth" && gs.CompletedMilestones.Contains("space") },

            // ---- Moon milestones ----
            new Milestone { Id = "moon", Title = "Reach the Moon", Reward = 25,
                Done = (v, ut, gs) => AtBody(v, "Moon") },
            new Milestone { Id = "moon-orbit", Title = "Orbit the Moon", Reward = 30,
                Done = (v, ut, gs) => InOrbit(v, ut, "Moon") },
            new Milestone { Id = "moon-land", Title = "Land on the Moon", Reward = 50,
                Done = (v, ut, gs) => LandedOn(v, "Moon") },

            // ---- Sun / interplanetary ----
            new Milestone { Id = "interplanetary", Title = "Escape to interplanetary space", Reward = 40,
                Done = (v, ut, gs) => v.Body?.Name == "Sun" },
            new Milestone { Id = "solar-orbit", Title = "Orbit the Sun", Reward = 60,
                Done = (v, ut, gs) => InOrbit(v, ut, "Sun") },

            // ---- Mercury ----
            new Milestone { Id = "mercury-flyby", Title = "Fly by Mercury", Reward = 70,
                Done = (v, ut, gs) => AtBody(v, "Mercury") },
            new Milestone { Id = "mercury-orbit", Title = "Orbit Mercury", Reward = 100,
                Done = (v, ut, gs) => InOrbit(v, ut, "Mercury") },
            new Milestone { Id = "mercury-land", Title = "Land on Mercury", Reward = 150,
                Done = (v, ut, gs) => LandedOn(v, "Mercury") },

            // ---- Venus ----
            new Milestone { Id = "venus-flyby", Title = "Fly by Venus", Reward = 45,
                Done = (v, ut, gs) => AtBody(v, "Venus") },
            new Milestone { Id = "venus-orbit", Title = "Orbit Venus", Reward = 70,
                Done = (v, ut, gs) => InOrbit(v, ut, "Venus") },
            new Milestone { Id = "venus-land", Title = "Land on Venus", Reward = 120,
                Done = (v, ut, gs) => LandedOn(v, "Venus") },

            // ---- Mars ----
            new Milestone { Id = "mars-flyby", Title = "Fly by Mars", Reward = 55,
                Done = (v, ut, gs) => AtBody(v, "Mars") },
            new Milestone { Id = "mars-orbit", Title = "Orbit Mars", Reward = 80,
                Done = (v, ut, gs) => InOrbit(v, ut, "Mars") },
            new Milestone { Id = "mars-land", Title = "Land on Mars", Reward = 120,
                Done = (v, ut, gs) => LandedOn(v, "Mars") },

            // ---- Jupiter system ----
            new Milestone { Id = "jupiter-flyby", Title = "Fly by Jupiter", Reward = 90,
                Done = (v, ut, gs) => AtBody(v, "Jupiter") },
            new Milestone { Id = "jupiter-orbit", Title = "Orbit Jupiter", Reward = 140,
                Done = (v, ut, gs) => InOrbit(v, ut, "Jupiter") },
            new Milestone { Id = "io-flyby", Title = "Fly by Io", Reward = 80,
                Done = (v, ut, gs) => AtBody(v, "Io") },
            new Milestone { Id = "europa-flyby", Title = "Fly by Europa", Reward = 85,
                Done = (v, ut, gs) => AtBody(v, "Europa") },
            new Milestone { Id = "ganymede-flyby", Title = "Fly by Ganymede", Reward = 85,
                Done = (v, ut, gs) => AtBody(v, "Ganymede") },
            new Milestone { Id = "callisto-flyby", Title = "Fly by Callisto", Reward = 75,
                Done = (v, ut, gs) => AtBody(v, "Callisto") },

            // ---- Saturn system ----
            new Milestone { Id = "saturn-flyby", Title = "Fly by Saturn", Reward = 110,
                Done = (v, ut, gs) => AtBody(v, "Saturn") },
            new Milestone { Id = "saturn-orbit", Title = "Orbit Saturn", Reward = 160,
                Done = (v, ut, gs) => InOrbit(v, ut, "Saturn") },
            new Milestone { Id = "titan-flyby", Title = "Fly by Titan", Reward = 100,
                Done = (v, ut, gs) => AtBody(v, "Titan") },
            new Milestone { Id = "titan-land", Title = "Land on Titan", Reward = 200,
                Done = (v, ut, gs) => LandedOn(v, "Titan") },

            // ---- Outer planets ----
            new Milestone { Id = "uranus-flyby", Title = "Fly by Uranus", Reward = 180,
                Done = (v, ut, gs) => AtBody(v, "Uranus") },
            new Milestone { Id = "neptune-flyby", Title = "Fly by Neptune", Reward = 200,
                Done = (v, ut, gs) => AtBody(v, "Neptune") },
            new Milestone { Id = "outer-planets", Title = "Reach the outer planets", Reward = 250,
                Done = (v, ut, gs) => v.Body != null && (v.Body.Name == "Uranus" || v.Body.Name == "Neptune")
                                      && gs.CompletedMilestones.Contains("saturn-flyby") },

            // ---- Activity milestones ----
            new Milestone { Id = "rendezvous", Title = "Rendezvous two ships in orbit", Reward = 60,
                Done = (v, ut, gs) => gs.Ships.Count >= 2 && v.Body != null && !v.Landed
                                      && FindNearbyShips(gs, v, ut, 2000) },
            new Milestone { Id = "docking", Title = "Dock two vessels", Reward = 90,
                Done = (v, ut, gs) => gs.Ships.Count >= 2 && v.Body != null && !v.Landed
                                      && FindNearbyShips(gs, v, ut, 50) },
            new Milestone { Id = "colony", Title = "Establish an off-world colony", Reward = 150,
                Done = (v, ut, gs) => v.Landed && v.Body != null && v.Body.Name != "Earth"
                                      && v.CrewCount >= 2 && HasFoodRegen(v) },
            new Milestone { Id = "deep-space", Title = "Venture beyond Mars", Reward = 80,
                Done = (v, ut, gs) => v.Body != null && v.Body.Parent?.Name == "Sun" &&
                                      v.Body.Name != "Earth" && v.Body.Name != "Mars" && v.Body.Name != "Venus" && v.Body.Name != "Mercury" &&
                                      v.CurrentElements(ut).A > 2.5e11  /* ~1.67 AU, past Mars */ },
            new Milestone { Id = "grand-tour", Title = "Grand Tour", Reward = 200,
                Done = (v, ut, gs) => gs.Ships.Count >= 1 && CountPlanetsVisited(gs) >= 3 },

            // ---- other-planet (kept for save compatibility) ----
            new Milestone { Id = "other-planet", Title = "Reach another planet", Reward = 60,
                Done = (v, ut, gs) => v.Body != null && v.Body.Parent != null && v.Body.Parent.Name == "Sun"
                                      && v.Body.Name != "Earth" },
        };

        /// <summary>Check whether there is another ship within <paramref name="rangeM"/> of vessel v.
        /// Both ships must be in the same SOI body — their positions are directly comparable.</summary>
        private static bool FindNearbyShips(GameState gs, Vessel v, double ut, double rangeM)
        {
            if (v.Body == null) return false;
            foreach (var s in gs.Ships)
            {
                if (s.Destroyed || s.BodyName != v.Body.Name) continue;
                double d = (v.Position - s.Position).Length;
                if (d > 1 && d < rangeM) return true;   // d>1 skips self (same position within ~1m)
            }
            return false;
        }

        /// <summary>True when the vessel carries an active food-regenerating recycler (e.g. a Hydroponics
        /// Bay): the mark of a self-sustaining base rather than a one-off landing.</summary>
        private static bool HasFoodRegen(Vessel v)
        {
            foreach (var p in v.AllParts())
                foreach (var m in p.Modules)
                    if (m.Def.Kind == Solar.Parts.ModuleKind.LifeSupport && m.Def.FoodRegen > 0
                        && (!m.Def.Activatable || m.Active)) return true;
            return false;
        }

        /// <summary>Count distinct celestial bodies that have been visited.</summary>
        private static int CountPlanetsVisited(GameState gs)
        {
            var visited = new HashSet<string>();
            string[] planetIds = { "mercury-flyby", "venus-flyby", "mars-flyby", "jupiter-flyby",
                                   "saturn-flyby", "uranus-flyby", "neptune-flyby", "moon" };
            foreach (var id in planetIds)
                if (gs.CompletedMilestones.Contains(id))
                {
                    // map milestone id to planet name
                    string planet = id switch
                    {
                        "mercury-flyby" => "Mercury", "venus-flyby" => "Venus", "mars-flyby" => "Mars",
                        "jupiter-flyby" => "Jupiter", "saturn-flyby" => "Saturn",
                        "uranus-flyby" => "Uranus", "neptune-flyby" => "Neptune", "moon" => "Moon",
                        _ => id,
                    };
                    visited.Add(planet);
                }
            return visited.Count;
        }

        /// <summary>Check every not-yet-completed milestone; award + record any newly satisfied one.
        /// Returns the milestone that fired (for a toast), or null. At most one per call.</summary>
        public static Milestone Evaluate(Vessel v, double ut, GameState gs)
        {
            if (v == null || v.Destroyed) return null;
            foreach (var m in All)
            {
                if (gs.CompletedMilestones.Contains(m.Id)) continue;
                bool ok;
                try { ok = m.Done(v, ut, gs); } catch { ok = false; }
                if (ok)
                {
                    gs.CompletedMilestones.Add(m.Id);
                    gs.Science += m.Reward;
                    return m;
                }
            }
            return null;
        }

        /// <summary>Award a milestone by id directly (for events that aren't a per-frame predicate, like a
        /// successful dock). No-op if already completed or unknown. Returns it for a toast, or null.</summary>
        public static Milestone Award(GameState gs, string id)
        {
            if (gs.CompletedMilestones.Contains(id)) return null;
            var m = All.Find(x => x.Id == id);
            if (m == null) return null;
            gs.CompletedMilestones.Add(m.Id);
            gs.Science += m.Reward;
            return m;
        }
    }
}
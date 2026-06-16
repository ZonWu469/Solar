using System;
using System.Collections.Generic;
using Solar.Core;
using Solar.Physics;

namespace Solar.Vessels
{
    /// <summary>A colony is a landed (usually docked-together) vessel the player has established as a
    /// surface base. Its production modules — drills/ISRU, life-support recyclers, generators — keep
    /// running while the player is off flying something else; this catches that production up on load.</summary>
    public static class Colony
    {
        /// <summary>Fuel (kg) spent as raw material per kilogram of part/module fabricated at a base.</summary>
        public const double MaterialPerKg = 1.0;
        /// <summary>Fuel a base must hold in reserve before it can fabricate at all (so building can't
        /// strand a base with empty tanks).</summary>
        public const double BuildReserve = 200.0;

        /// <summary>Whether a vessel may be promoted to a colony: a crewed craft sitting on a surface.</summary>
        public static bool CanEstablish(Vessel v) =>
            v != null && v.Landed && !v.Destroyed && !v.IsColony && v.CrewCount > 0;

        /// <summary>An engineer must be aboard to fabricate parts at a base.</summary>
        public static bool HasEngineer(Vessel v)
        {
            if (v == null) return false;
            foreach (var p in v.Parts)
                foreach (var c in p.Crew)
                    if (c.Role == CrewRole.Engineer) return true;
            return false;
        }

        /// <summary>Whether the base can currently fabricate something of the given dry mass: it has an
        /// engineer and enough fuel (above the reserve) to pay the material cost.</summary>
        public static bool CanFabricate(Vessel v, double dryMass) =>
            v != null && v.IsColony && v.Landed && HasEngineer(v)
            && v.TotalLiquidFuel >= BuildReserve + dryMass * MaterialPerKg;

        /// <summary>Charge the material cost for fabricating <paramref name="dryMass"/> kg. Returns false
        /// (charging nothing) if the base can't currently afford it.</summary>
        public static bool PayFabrication(Vessel v, double dryMass)
        {
            if (!CanFabricate(v, dryMass)) return false;
            return v.TrySpendFuel(dryMass * MaterialPerKg);
        }

        /// <summary>Self-sustaining time (s) a colony must bank before a new colonist is born. ~30 in-game
        /// days at the current 1/10 time scale.</summary>
        // TODO(balance.json): colony crew-growth interval.
        public const double GrowthInterval = 30 * 24 * 3600;

        private static readonly string[] FirstNames =
            { "Gus", "Ada", "Lin", "Mac", "Ned", "Pip", "Rae", "Sven", "Tig", "Uma", "Wes", "Zara" };

        /// <summary>Grow a self-sustaining surface colony's crew: while its recyclers fully cover the crew's
        /// life-support draw and it has a spare seat, it banks time and mints a new colonist each
        /// <see cref="GrowthInterval"/>. Handles a long offline catch-up step (adds several at once, capped by
        /// free seats). The new crew joins the shared <paramref name="gs"/> roster and a free seat aboard, so
        /// a colony pays off in people as well as fuel. Returns the number of colonists born (0 if none).</summary>
        public static int TryGrowCrew(Vessel v, GameState gs, double dt)
        {
            if (v == null || gs == null || !v.IsColony || !v.Landed || v.Destroyed) return 0;
            if (dt <= 0 || double.IsNaN(dt) || double.IsInfinity(dt)) return 0;
            // need an existing crew to run the base, a spare seat to fill, and a self-sustaining balance
            if (v.CrewCount == 0 || v.CrewCount >= v.SeatCount || !v.SelfSustaining)
            { v.ColonyGrowthTimer = 0; return 0; }

            v.ColonyGrowthTimer += dt;
            int born = 0;
            while (v.ColonyGrowthTimer >= GrowthInterval && v.CrewCount < v.SeatCount)
            {
                v.ColonyGrowthTimer -= GrowthInterval;
                if (SeatColonist(v, gs)) born++;
                else break;
            }
            return born;
        }

        /// <summary>Mint a colonist (role chosen to balance the roster) and seat them in a free seat aboard.</summary>
        private static bool SeatColonist(Vessel v, GameState gs)
        {
            foreach (var p in v.AllParts())
            {
                if (p.Crew.Count >= p.SeatCount) continue;
                var c = new CrewMember(NextName(gs), NextRole(v));
                gs.Roster.Add(c);
                p.Crew.Add(c);
                return true;
            }
            return false;
        }

        /// <summary>Pick the role the colony is shortest on (engineers run ISRU, scientists boost science,
        /// pilots fly) so a growing base rounds out its crew.</summary>
        private static CrewRole NextRole(Vessel v)
        {
            int pilots = v.CrewCountOfRole(CrewRole.Pilot);
            int engs = v.CrewCountOfRole(CrewRole.Engineer);
            int scis = v.CrewCountOfRole(CrewRole.Scientist);
            if (engs <= pilots && engs <= scis) return CrewRole.Engineer;
            if (scis <= pilots) return CrewRole.Scientist;
            return CrewRole.Pilot;
        }

        /// <summary>A colonist name not already on the roster.</summary>
        private static string NextName(GameState gs)
        {
            foreach (var first in FirstNames)
            {
                string name = first + " Kerman";
                if (!gs.Roster.Exists(c => c.Name == name)) return name;
            }
            for (int n = 2; ; n++)
            {
                string name = $"Colonist {n}";
                if (!gs.Roster.Exists(c => c.Name == name)) return name;
            }
        }

        /// <summary>Advance a landed colony's resources over the unsimulated span
        /// <paramref name="fromUT"/>..<paramref name="toUT"/>. Reuses <see cref="Vessel.UpdateResources"/>
        /// so offline rates match live flight exactly; accumulation is clamped per resource, so a single
        /// large catch-up step stays bounded (no need to sub-step).</summary>
        public static void AdvanceProduction(Vessel v, double fromUT, double toUT, Universe u)
        {
            if (v == null || !v.IsColony || !v.Landed || v.Destroyed) return;
            double dt = toUT - fromUT;
            if (dt <= 0 || double.IsNaN(dt) || double.IsInfinity(dt)) return;
            v.UpdateResources(dt, toUT, u);
            // age the base over the unattended gap too: wear/repair, radiation and illness. The warp-safe
            // formulation in Threats keeps one large dt equivalent to many small steps.
            Threats.Tick(v, dt, toUT, u, _rng);
        }

        private static readonly System.Random _rng = new();
    }
}

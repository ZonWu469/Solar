using System;
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
        }
    }
}

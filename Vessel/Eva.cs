using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Solar.Core;
using Solar.Parts;

namespace Solar.Vessels
{
    /// <summary>Extravehicular activity: a crew member leaving a craft as a tiny jetpack "vessel". An EVA
    /// kerbal is a normal <see cref="Vessel"/> carrying one synthetic single-seat part, flagged
    /// <see cref="Vessel.IsEva"/> so the flight scene keeps it off-rails, drives it with a direct
    /// monopropellant jetpack, and renders it as an astronaut sprite. Boarding moves the crew back into any
    /// craft with a free seat — the basis of crew rescue. EVA kerbals are transient: they are never saved
    /// (the crew member stays Active in the shared roster, so nothing is lost if one is left floating).</summary>
    public static class Eva
    {
        /// <summary>Synthetic one-seat part the EVA "vessel" is built from. A Pod-kind part seats one crew
        /// (<see cref="PartDef.BaseCrew"/>) and provides a little control authority for the jetpack.</summary>
        public static readonly PartDef KerbalDef = new PartDef
        {
            Name = "Kerbal", Id = "kerbal", Kind = PartKind.Pod,
            DryMass = 90, Width = 0.6, Height = 1.0, CdA = 0.4,
            ControlAuthority = 3.0, Sas = true, ImpactTolerance = 8,
            Tint = new Color(225, 220, 130),
        };

        public const double Monoprop = 30;          // jetpack propellant budget (kg)
        public const double JetpackAccel = 3.0;     // m/s^2 of jetpack thrust at full input
        public const double JetpackFlow = 0.12;     // monoprop kg/s consumed at full thrust
        public const double BoardRange = 12;        // m: how close a kerbal must be to board a craft
        public const double BoardSpeed = 3.0;       // m/s: max relative speed to board safely

        /// <summary>Build an EVA kerbal leaving <paramref name="source"/>, carrying <paramref name="crew"/>.
        /// The caller is responsible for having already removed the crew from the source craft.</summary>
        public static Vessel Spawn(Vessel source, CrewMember crew)
        {
            var part = new Part(KerbalDef);
            part.Crew.Add(crew);

            var eva = new Vessel
            {
                Body = source.Body,
                Heading = source.Heading,
                Velocity = source.Velocity,
                Landed = false,
                OnRails = false,
                IsEva = true,
                EvaRole = crew.Role,
                Monoprop = Monoprop,
                ElectricCharge = 0,
            };
            eva.Parts.Add(part);
            // shove off a few metres to the side so the kerbal doesn't spawn inside the hull
            Vec2d side = source.Up.Perp();
            eva.Position = source.Position + side * 3.0 + source.Up * 1.0;
            return eva;
        }

        /// <summary>The single crew member aboard an EVA vessel (null if none / not an EVA).</summary>
        public static CrewMember Occupant(Vessel eva)
        {
            if (eva == null || !eva.IsEva) return null;
            foreach (var p in eva.Parts)
                foreach (var c in p.Crew) return c;
            return null;
        }

        /// <summary>A part on <paramref name="target"/> with a free seat, or null if the craft is full.</summary>
        public static Part FreeSeat(Vessel target)
        {
            if (target == null || target.Destroyed) return null;
            foreach (var p in target.AllParts())
                if (p.Crew.Count < p.SeatCount) return p;
            return null;
        }

        /// <summary>Whether <paramref name="eva"/> is close and slow enough to board <paramref name="target"/>,
        /// which must have a free seat.</summary>
        public static bool CanBoard(Vessel eva, Vessel target, double ut)
        {
            if (eva == null || !eva.IsEva || target == null || target.Destroyed || target.IsEva) return false;
            if (FreeSeat(target) == null) return false;
            double dist = (eva.AbsolutePosition(ut) - target.AbsolutePosition(ut)).Length;
            if (dist > BoardRange) return false;
            double rel = (eva.Velocity - target.Velocity).Length;
            return eva.Body == target.Body ? rel <= BoardSpeed : dist <= BoardRange;
        }

        /// <summary>Move the EVA crew into a free seat on <paramref name="target"/>. Returns true on success.</summary>
        public static bool Board(Vessel eva, Vessel target)
        {
            var crew = Occupant(eva);
            var seat = FreeSeat(target);
            if (crew == null || seat == null) return false;
            foreach (var p in eva.Parts) p.Crew.Remove(crew);
            seat.Crew.Add(crew);
            return true;
        }
    }
}

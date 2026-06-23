using System;
using System.Collections.Generic;
using Solar.Core;
using Solar.Parts;
using Solar.Physics;

namespace Solar.Vessels
{
    /// <summary>Environmental and equipment hazards that put a mission at risk over time: module
    /// malfunctions (wear + random failure), radiation belts (crew dose), and biological contagion
    /// (crew illness). One <see cref="Tick"/> runs every frame right after <see cref="Vessel.UpdateResources"/>
    /// (so it shares the warp-scaled dt) and again over the offline gap in <see cref="Colony.AdvanceProduction"/>.
    /// Mirrors the static, stateless style of <see cref="Colony"/>: all condition state lives on the
    /// <see cref="ModuleInstance"/>/<see cref="CrewMember"/> instances, which persist with the save.</summary>
    public static class Threats
    {
        // All hazard rates are global tunables sourced from Content/balance.json via Core.Balance (with
        // in-code defaults). Aliased here so the rest of the file reads unchanged.
        static double WearPerSec        => Core.Balance.WearPerSec;        // a running module fully wears over ~8 months
        static double BreakBaseRate     => Core.Balance.BreakBaseRate;     // break chance/s at zero wear, reliability 1
        static double RepairPerSec      => Core.Balance.RepairPerSec;      // an engineer repairs a broken module in ~2 h
        public static double RadDeathDose => Core.Balance.RadDeathDose;    // accumulated dose that kills a crew member
        static double RadDecayPerSec    => Core.Balance.RadDecayPerSec;    // dose clears slowly outside any belt
        static double InfectBaseRate    => Core.Balance.InfectBaseRate;    // base infection chance/s per healthy crew
        static double IllnessGrowPerSec => Core.Balance.IllnessGrowPerSec; // untreated sickness worsens over ~72 h
        static double IllnessDeathRate  => Core.Balance.IllnessDeathRate;  // death chance/s scaled by illness once terminal

        /// <summary>Whether a per-second hazard of rate <paramref name="ratePerSec"/> fires across a step of
        /// <paramref name="dt"/> seconds, using the exponential survival law so one big time-warp step is
        /// equivalent to many small ones (P = 1 - e^(-rate*dt)).</summary>
        public static bool Fires(Random rng, double ratePerSec, double dt)
            => ratePerSec > 0 && dt > 0 && rng.NextDouble() < 1.0 - Math.Exp(-ratePerSec * dt);

        public static void Tick(Vessel v, double dt, double ut, Universe u, Random rng)
        {
            if (v == null || v.Destroyed || dt <= 0 || rng == null) return;
            Malfunctions(v, dt, ut, u, rng);
            Radiation(v, dt, ut, u);
            Biological(v, dt, rng);
        }

        // ----- 1. malfunctions & wear -----
        private static void Malfunctions(Vessel v, double dt, double ut, Universe u, Random rng)
        {
            double engineer = v.CrewSkill(CrewRole.Engineer);   // >= 1; cuts failure rate, speeds repair
            // an engineer can repair anywhere there's power (e.g. a crewed orbital station), not only when landed
            bool canRepair = engineer > 1 && v.ElectricCharge > 0;
            foreach (var p in v.AllParts())
                foreach (var m in p.Modules)
                {
                    if (m.Broken)
                    {
                        if (!canRepair) continue;
                        m.Wear -= RepairPerSec * engineer * dt;
                        if (m.Wear <= 0) { m.Wear = 0; m.Broken = false; v.RecentRepairs.Add(m.Def.Name); }
                        continue;
                    }
                    if (!v.ModuleFunctioning(m, ut, u)) continue;   // only working equipment wears out
                    m.Wear = Math.Min(1, m.Wear + WearPerSec * dt);
                    double reliability = m.Def.Reliability > 0 ? m.Def.Reliability : 1;
                    double rate = BreakBaseRate * (1 + 5 * m.Wear) / reliability / engineer;
                    if (Fires(rng, rate, dt)) { m.Broken = true; v.RecentFailures.Add(m.Def.Name); }
                }
        }

        // ----- 2. radiation: belts (omnidirectional) + solar storms (directional) -----
        private static void Radiation(Vessel v, double dt, double ut, Universe u)
        {
            // belt dose is omnidirectional and cut by the strongest functioning shield (unchanged)
            double beltDose = v.Body?.RadiationAt(v.Altitude) ?? 0;
            double shieldedBelt = beltDose * (1 - BestShield(v));

            // storm dose comes from the Sun's actual direction: cut by a shield only when its face is turned
            // sunward, by the atmosphere when flying deep in air, and falling off inverse-square with distance.
            double stormDose = 0;
            if (u != null)
            {
                var storm = SpaceWeather.ForVessel(SpaceWeather.ActiveSeed, ut, v.AbsolutePosition(ut), u);
                if (storm.IsActive && storm.DoseRate > 0)
                    stormDose = storm.DoseRate * StormExposure(v, u) * (1 - BestStormShield(v, storm.SunDir));
            }

            double rate = shieldedBelt + stormDose;
            if (rate > 0)
            {
                double perCrew = rate * dt;
                foreach (var c in CrewSnapshot(v))
                {
                    c.RadDose += perCrew;
                    if (c.RadDose >= RadDeathDose) v.KillCrewMember(c);   // acute dose is fatal
                }
            }
            else
            {
                foreach (var c in v.AllCrew())
                    c.RadDose = Math.Max(0, c.RadDose - RadDecayPerSec * dt);
            }
        }

        /// <summary>Strongest radiation shielding fraction (0..1) over the vessel's functioning shields.</summary>
        private static double BestShield(Vessel v)
        {
            double best = 0;
            foreach (var p in v.AllParts())
                foreach (var m in p.Modules)
                    if (m.Def.Kind == ModuleKind.RadShield && !m.Broken && m.Def.ShieldFactor > best)
                        best = m.Def.ShieldFactor;
            return Math.Clamp(best, 0, 1);
        }

        /// <summary>Best storm shielding fraction (0..1): a shield only protects against a storm when its
        /// face (the vessel's <see cref="Vessel.Up"/> axis) is turned toward the Sun. Full inside ~25 deg of
        /// the sun line, tapering to nothing by ~80 deg, so the player must orient to ride a storm out.</summary>
        public static double BestStormShield(Vessel v, Vec2d sunDir)
        {
            double align = Math.Max(0, v.Up.Dot(sunDir));   // cos of the angle between the shielded face and the Sun
            const double full = 0.906, none = 0.174;        // cos 25 deg .. cos 80 deg
            double cone = Math.Clamp((align - none) / (full - none), 0, 1);
            if (cone <= 0) return 0;
            double best = 0;
            foreach (var p in v.AllParts())
                foreach (var m in p.Modules)
                    if (m.Def.Kind == ModuleKind.RadShield && !m.Broken && m.Def.ShieldFactor > best)
                        best = m.Def.ShieldFactor;
            return Math.Clamp(best * cone, 0, 1);
        }

        /// <summary>How exposed the vessel is to a solar storm given the air column above it: 1 in vacuum or
        /// above the atmosphere, falling toward 0 as it descends into a thick atmosphere (so a craft landed
        /// on Earth/Venus/Titan/Mars is largely sheltered, while an airless surface gives no protection). The
        /// shelter reference is a quarter of Earth's sea-level column, so anything Earth-like fully shelters.</summary>
        public static double StormExposure(Vessel v, Universe u)
        {
            var atmo = v.Body?.Atmo;
            if (atmo == null) return 1;                                  // airless: fully exposed
            double alt = v.Altitude;
            if (alt >= atmo.Top) return 1;                              // above the atmosphere
            double colAbove = atmo.DensityAt(Math.Max(0, alt)) * atmo.ScaleHeight;   // ~column mass above (kg/m^2)
            var earth = u?["Earth"]?.Atmo;
            double cref = earth != null ? earth.SeaLevelDensity * earth.ScaleHeight * 0.25 : colAbove;
            double sheltered = cref > 0 ? Math.Clamp(colAbove / cref, 0, 1) : 0;
            return 1 - sheltered;
        }

        // ----- storm electronics damage (live flight only; offline colonies are abstracted) -----
        /// <summary>While a solar storm is Active, exposed powered electronics can be fried (set
        /// <see cref="ModuleInstance.Broken"/>). Powering a module off (it stops functioning) takes it out of
        /// the line of fire; a sunward shield and a radiator / hardened bay cut the risk for what must stay on.
        /// Called once per frame from the flight scene with the live (warp-capped) dt — not from offline colony
        /// catch-up, so it never perturbs the deterministic offline-production parity.</summary>
        public static void StormDamage(Vessel v, double dt, double ut, Universe u, Random rng)
        {
            if (v == null || v.Destroyed || dt <= 0 || rng == null || u == null) return;
            var storm = SpaceWeather.ForVessel(SpaceWeather.ActiveSeed, ut, v.AbsolutePosition(ut), u);
            if (!storm.IsActive || storm.Intensity <= 0) return;

            double exposure = StormExposure(v, u) * (1 - BestStormShield(v, storm.SunDir));
            if (exposure <= 0) return;
            double harden = BestHardening(v);
            double baseRate = Core.Balance.StormFryPerSec * storm.Intensity * exposure * (1 - harden);
            if (baseRate <= 0) return;

            foreach (var p in v.AllParts())
                foreach (var m in p.Modules)
                    if (!m.Broken && IsElectronics(m.Def.Kind) && v.ModuleFunctioning(m, ut, u)
                        && Fires(rng, baseRate / (m.Def.Reliability > 0 ? m.Def.Reliability : 1), dt))
                    { m.Broken = true; v.RecentFailures.Add(m.Def.Name); }
        }

        /// <summary>Best storm-hardening fraction (0..1) over the vessel's functioning radiator / hardened modules.</summary>
        private static double BestHardening(Vessel v)
        {
            double best = 0;
            foreach (var p in v.AllParts())
                foreach (var m in p.Modules)
                    if (!m.Broken && m.Def.StormHardening > best) best = m.Def.StormHardening;
            return Math.Clamp(best, 0, 1);
        }

        /// <summary>Active electronics a storm can fry: powered, semiconductor-bearing equipment. Passive
        /// structure (tanks, batteries, shields, radiators, legs) and the rugged RTG are immune.</summary>
        private static bool IsElectronics(ModuleKind k) => k switch
        {
            ModuleKind.SolarPanel or ModuleKind.ReactionWheel or ModuleKind.Science or ModuleKind.Antenna
            or ModuleKind.OreScanner or ModuleKind.Light or ModuleKind.RCS or ModuleKind.Medbay
            or ModuleKind.FuelCell or ModuleKind.IsruConverter or ModuleKind.Harvester => true,
            _ => false,
        };

        // ----- 3. biological contagion -----
        private static void Biological(Vessel v, double dt, Random rng)
        {
            var crew = CrewSnapshot(v);
            if (crew.Count == 0) return;

            int sick = 0;
            foreach (var c in crew) if (c.Illness > 0) sick++;
            double sickFrac = (double)sick / crew.Count;

            // cure capacity: best functioning medbay, amplified by scientists aboard
            double cure = 0;
            foreach (var p in v.AllParts())
                foreach (var m in p.Modules)
                    if (m.Def.Kind == ModuleKind.Medbay && (!m.Def.Activatable || m.Active) && !m.Broken
                        && v.ElectricCharge > 0 && m.Def.CureRate > cure)
                        cure = m.Def.CureRate;
            cure *= v.CrewSkill(CrewRole.Scientist);

            foreach (var c in crew)
            {
                if (c.Illness <= 0)
                {
                    // healthy crew can catch an illness; spread accelerates with how many are already sick
                    double rate = InfectBaseRate * (1 + 4 * sickFrac);
                    if (Fires(rng, rate, dt)) c.Illness = 0.1;
                    continue;
                }
                c.Illness = Math.Clamp(c.Illness + (IllnessGrowPerSec - cure) * dt, 0, 1);
                if (c.Illness >= 1 && Fires(rng, IllnessDeathRate, dt)) v.KillCrewMember(c);
            }
        }

        /// <summary>Snapshot of the living crew, so a hazard may remove a member (death) without mutating
        /// the collection it is iterating.</summary>
        private static List<CrewMember> CrewSnapshot(Vessel v)
        {
            var list = new List<CrewMember>();
            foreach (var c in v.AllCrew()) list.Add(c);
            return list;
        }
    }
}

using System;
using System.Collections.Generic;
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
            Radiation(v, dt, rng);
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

        // ----- 2. radiation belts -----
        private static void Radiation(Vessel v, double dt, Random rng)
        {
            double dose = v.Body?.RadiationAt(v.Altitude) ?? 0;

            if (dose > 0)
            {
                double shield = BestShield(v);
                double perCrew = dose * (1 - shield) * dt;
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

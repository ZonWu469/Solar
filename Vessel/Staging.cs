using System;
using System.Collections.Generic;
using Solar.Parts;

namespace Solar.Vessels
{
    public sealed class StageStat
    {
        public int Number;          // 1 = first to fire (bottom)
        public string Engines = ""; // engine names summary
        public double DeltaV;       // m/s
        public double Twr;          // at Earth surface gravity
        public double BurnTime;     // s at full throttle
        public double Fuel;         // kg in the stage segment
    }

    /// <summary>Stage logic: stages are auto-generated from stack order (decouplers are boundaries).</summary>
    public static class Staging
    {
        public const double G0 = 9.81;

        /// <summary>
        /// Fire the next stage on a flying vessel. First press ignites the bottom segment's
        /// engines; subsequent presses decouple at the bottom-most decoupler and ignite the
        /// next segment; the final press deploys the parachute. Returns jettisoned debris or null.
        /// </summary>
        public static Vessel FireNext(Vessel v)
        {
            if (!v.EnginesIgnited)
            {
                IgniteBottomSegment(v);
                v.EnginesIgnited = true;
                return null;
            }

            // drop any radial groups on the active (bottom) segment before decoupling the segment itself
            var radDebris = JettisonBottomRadials(v);
            if (radDebris != null) return radDebris;

            int dec = v.Parts.FindLastIndex(p => p.Def.Kind == PartKind.Decoupler);
            if (dec >= 0)
            {
                var debris = new Vessel { Body = v.Body, IsDebris = true, Heading = v.Heading, EnginesIgnited = true };
                var jettisoned = v.Parts.GetRange(dec, v.Parts.Count - dec);
                v.Parts.RemoveRange(dec, v.Parts.Count - dec);
                debris.Parts.AddRange(jettisoned);

                double jh = 0;
                foreach (var p in jettisoned) jh += p.Def.Height;
                debris.Position = v.Position;
                v.Position += v.Up * jh;                      // remaining stack's base moves up
                debris.Velocity = v.Velocity - v.Up * 2.0;    // gentle separation push
                debris.OnRails = false;

                IgniteBottomSegment(v);
                return debris;
            }

            var chute = v.Parts.Find(p => p.Def.Kind == PartKind.Parachute && !p.Deployed);
            if (chute != null) chute.Deployed = true;
            return null;
        }

        private static void IgniteBottomSegment(Vessel v)
        {
            var segs = v.Segments();
            if (segs.Count == 0) return;
            var bottom = segs[segs.Count - 1];
            for (int i = bottom.start; i <= bottom.end; i++)
            {
                var p = v.Parts[i];
                if (p.Def.Kind == PartKind.Engine || p.Def.Kind == PartKind.SolidBooster) p.Ignited = true;
                foreach (var r in p.Radials)
                    if (r.Def.Kind == PartKind.Engine || r.Def.Kind == PartKind.SolidBooster) r.Ignited = true;
            }
        }

        /// <summary>Detach the separate-stage radial groups mounted on the active (bottom) segment, returning
        /// them as a debris vessel (or null if none). Radials flagged "included" (<see cref="Part.RadialSeparate"/>
        /// false) stay welded to their host part and ride it until the core stage decouples.</summary>
        private static Vessel JettisonBottomRadials(Vessel v)
        {
            var segs = v.Segments();
            if (segs.Count == 0) return null;
            var bottom = segs[segs.Count - 1];
            var dropped = new List<Part>();
            for (int i = bottom.start; i <= bottom.end; i++)
            {
                var host = v.Parts[i];
                for (int k = host.Radials.Count - 1; k >= 0; k--)
                    if (host.Radials[k].RadialSeparate) { dropped.Add(host.Radials[k]); host.Radials.RemoveAt(k); }
            }
            if (dropped.Count == 0) return null;

            var debris = new Vessel
            {
                Body = v.Body, IsDebris = true, Heading = v.Heading, EnginesIgnited = true,
                Position = v.Position, OnRails = false,
            };
            debris.Parts.AddRange(dropped);
            var side = new Solar.Core.Vec2d(-v.Up.Y, v.Up.X);   // gentle outward separation push
            debris.Velocity = v.Velocity + side * 3.0;
            return debris;
        }

        /// <summary>Estimated time (s) to burn <paramref name="dv"/> m/s, walking stages bottom-up;
        /// -1 if the vessel lacks the delta-v. Shared by the HUD readout and the warp-to-node logic.</summary>
        public static double BurnTime(Vessel v, double dv)
        {
            double remaining = dv, t = 0;
            foreach (var st in ComputeStages(v.Parts))
            {
                if (remaining <= 0) break;
                if (st.DeltaV <= 0 || st.BurnTime <= 0) continue;
                double use = Math.Min(remaining, st.DeltaV);
                t += st.BurnTime * (use / st.DeltaV);
                remaining -= use;
            }
            return remaining > 1e-6 ? -1 : t;
        }

        /// <summary>Per-stage delta-v / TWR breakdown for a part stack (used by the editor and the HUD).</summary>
        public static List<StageStat> ComputeStages(IReadOnlyList<PartDef> stack, double surfaceG = G0)
        {
            var parts = new List<Part>();
            foreach (var d in stack) parts.Add(new Part(d));
            return ComputeStages(parts, surfaceG);
        }

        /// <summary>Editor overload: builds parts (incl. attached module dry mass) from design entries.</summary>
        public static List<StageStat> ComputeStages(IReadOnlyList<StackEntry> stack, double surfaceG = G0)
        {
            var parts = new List<Part>();
            foreach (var e in stack)
            {
                var p = new Part(e.Def);
                foreach (var m in e.Modules) p.Modules.Add(new ModuleInstance(m));
                VesselDesign.MaterializeRadials(e, p);   // symmetric-pair sub-stacks
                parts.Add(p);
            }
            return ComputeStages(parts, surfaceG);
        }

        /// <summary>Running thrust/fuel/flow accumulator for a stage's engines.</summary>
        private sealed class Agg
        {
            public double Fuel, Thrust, Flow;
            public readonly List<string> Engines = new();
            public void Add(Part part)
            {
                Fuel += part.Fuel;
                if (part.Def.Kind == PartKind.Engine || part.Def.Kind == PartKind.SolidBooster)
                {
                    Thrust += part.Def.Thrust;
                    Flow += part.Def.FuelFlowAtMax;
                    if (!Engines.Contains(part.Def.Name)) Engines.Add(part.Def.Name);
                }
            }
        }

        /// <summary>Build a <see cref="StageStat"/> from an accumulator and the mass it lifts.</summary>
        private static StageStat MakeStage(ref int number, Agg a, double m0, double surfaceG)
        {
            var st = new StageStat
            {
                Number = number++,
                Engines = a.Engines.Count > 0 ? string.Join("+", a.Engines) : "(no engine)",
                Fuel = a.Fuel,
            };
            if (a.Thrust > 0 && a.Fuel > 0 && m0 > a.Fuel)
            {
                double isp = a.Thrust / a.Flow / G0; // thrust-weighted Isp
                st.DeltaV = isp * G0 * Math.Log(m0 / (m0 - a.Fuel));
                st.Twr = a.Thrust / (m0 * surfaceG);
                st.BurnTime = a.Fuel / a.Flow;
            }
            return st;
        }

        public static List<StageStat> ComputeStages(List<Part> partsIn, double surfaceG = G0)
        {
            var parts = new List<Part>(partsIn);
            var stats = new List<StageStat>();
            int number = 1;

            while (parts.Count > 0)
            {
                // bottom segment = parts after the last decoupler
                int dec = parts.FindLastIndex(p => p.Def.Kind == PartKind.Decoupler);
                int segStart = dec + 1;

                // total mass still attached (incl. all radials) lifts the bottom segment
                double m0 = 0;
                foreach (var p in parts) { m0 += p.Mass; foreach (var r in p.Radials) m0 += r.Mass; }

                // accumulate the bottom segment, splitting separate-stage radials (jettisoned as their own
                // stage, matching the runtime JettisonBottomRadials) from the core (axial parts + radials
                // flagged "ride core"). New mounts default their staging choice by part type (see AddRadial).
                var core = new Agg();
                var sep = new Agg();
                double sepMass = 0;          // mass of the bottom segment's separate radials (they leave first)
                for (int i = segStart; i < parts.Count; i++)
                {
                    core.Add(parts[i]);
                    foreach (var r in parts[i].Radials)
                    {
                        if (r.RadialSeparate) { sep.Add(r); sepMass += r.Mass; }
                        else core.Add(r);
                    }
                }

                // separate radials fire first (lower stage number): they lift the full current mass, then drop
                if (sep.Thrust > 0)
                    stats.Add(MakeStage(ref number, sep, m0, surfaceG));

                // core stage burns after the separate radials are gone, so its initial mass excludes them
                if (core.Thrust > 0 || dec >= 0) // skip a trivial top "stage" with no engine and nothing below
                    stats.Add(MakeStage(ref number, core, m0 - sepMass, surfaceG));

                if (dec < 0) break;
                parts.RemoveRange(dec, parts.Count - dec);
            }
            return stats;
        }
    }
}

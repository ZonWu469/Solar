using System;
using System.Collections.Generic;
using Solar.Parts;

namespace Solar.Vessels
{
    public sealed class StageStat
    {
        public int Number;          // stage index (0 fires first, at the bottom)
        public string Engines = ""; // engine names summary (or an action label)
        public double DeltaV;       // m/s
        public double Twr;          // at Earth surface gravity
        public double BurnTime;     // s at full throttle
        public double Fuel;         // kg burned by this stage
        public double FuelCap;      // kg capacity of that fuel (for the HUD fuel bar)
        public bool Decouples;      // this stage fires a decoupler / drops parts

        // ---- KSP-style readout: what this stage actually does (drives the stage-list labels + icons) ----
        public string Action = "";  // short verb: Liftoff / Ignite / Drop boosters / Decouple / Parachute
        public List<string> Ignites = new();  // friendly grouped names of engines igniting (e.g. "2x Thumper-R")
        public List<string> Drops = new();    // friendly grouped names of parts jettisoned this stage
        public bool RadialEvent;    // a separate radial mount (strap-on) jettisons this stage
        public bool AxialDecouple;  // an axial decoupler fires this stage
        public bool Chute;          // a parachute deploys this stage
    }

    /// <summary>KSP-style staging: every stageable element carries an explicit <see cref="Part.Stage"/>
    /// (0 fires first). Engines/boosters ignite, parachutes deploy, and decouplers separate when their
    /// stage fires; a separate radial mount is jettisoned at its stage. Default stages are derived from
    /// geometry by <see cref="AssignDefaultStages"/> and are then editable in the VAB.</summary>
    public static class Staging
    {
        public const double G0 = 9.81;

        /// <summary>Fill any unassigned (<c>Stage &lt; 0</c>) stage indices from stack geometry, leaving
        /// explicit (player-edited) stages untouched. Bottom segment fires at stage 0; if a segment carries
        /// separate radial strap-ons they jettison on their own dedicated stage (one after the segment
        /// ignites), and only then does the decoupler above shed the segment and expose the next one's
        /// engines. So a core with side boosters defaults to: launch -> drop boosters -> stage the core,
        /// matching KSP. Parachutes deploy last.</summary>
        public static void AssignDefaultStages(List<Part> parts)
        {
            int n = parts.Count;
            if (n == 0) return;

            // segment index of each axial part: 0 = bottom, +1 above each decoupler.
            var seg = new int[n];
            int segCount = 0;
            for (int i = n - 1; i >= 0; i--)
            {
                if (parts[i].Def.Kind == PartKind.Decoupler) segCount++;  // decoupler + parts above ride the next segment up
                seg[i] = segCount;
            }
            int maxSeg = segCount;

            // which segments shed separate strap-ons (boosters / drop tanks) — each needs an extra stage
            // so the strap-ons jettison on their own, before the decoupler that sheds the whole segment.
            var segDrops = new bool[maxSeg + 1];
            for (int i = 0; i < n; i++)
                foreach (var r in parts[i].Radials)
                    if (r.RadialSeparate && r.Def.Kind != PartKind.Parachute) segDrops[seg[i]] = true;

            // base (launch/ignite) stage per segment, bottom up: one stage to fire the segment, plus one
            // more if it drops strap-ons, before the decoupler exposes the next segment up.
            var baseStage = new int[maxSeg + 1];
            for (int g = 1; g <= maxSeg; g++)
                baseStage[g] = baseStage[g - 1] + (segDrops[g - 1] ? 1 : 0) + 1;
            int maxStage = baseStage[maxSeg];
            for (int g = 0; g <= maxSeg; g++) if (segDrops[g]) maxStage = Math.Max(maxStage, baseStage[g] + 1);

            for (int i = 0; i < n; i++)
            {
                var p = parts[i];
                if (p.Stage < 0)
                    p.Stage = p.Def.Kind == PartKind.Parachute ? maxStage + 1 : baseStage[seg[i]];
                foreach (var r in p.Radials)
                {
                    if (r.Stage < 0)
                        r.Stage = r.Def.Kind == PartKind.Parachute ? maxStage + 1
                                : r.RadialSeparate ? baseStage[seg[i]] + 1 : baseStage[seg[i]];
                    // fire/ignite stage: a separate strap-on ignites when its segment first lights
                    // (liftoff for the bottom segment) rather than inheriting a possibly-late host
                    // stage; a welded ("KEEP") radial fires with its host; a chute deploys last.
                    if (r.FireStage < 0)
                        r.FireStage = r.Def.Kind == PartKind.Parachute ? maxStage + 1
                                    : r.RadialSeparate ? baseStage[seg[i]] : p.Stage;
                    // A separate strap-on can never be jettisoned at or before the stage it ignites,
                    // or it would vanish before firing. Enforced unconditionally (covers explicit and
                    // derived stages, and normalizes old designs loaded with a bad drop/fire pair).
                    if (r.RadialSeparate && r.Def.Kind != PartKind.Parachute && r.Stage <= r.FireStage)
                        r.Stage = r.FireStage + 1;
                }
            }
        }

        /// <summary>The highest stage index in use across the vessel's parts (and radials). Radials count
        /// their <see cref="Part.FireStage"/> too: a welded booster can ignite at a stage above its drop
        /// <see cref="Part.Stage"/>, and that ignition event must still be reachable by the stage walk.</summary>
        public static int MaxStage(IReadOnlyList<Part> parts)
        {
            int m = 0;
            foreach (var p in parts)
            {
                if (p.Stage > m) m = p.Stage;
                foreach (var r in p.Radials) { if (r.Stage > m) m = r.Stage; if (r.FireStage > m) m = r.FireStage; }
            }
            return m;
        }

        /// <summary>Fire the vessel's current stage: ignite engines/boosters tagged with it (axial, plus the
        /// radials of any axial part igniting now), deploy its parachutes, jettison its separate radials, and
        /// separate its decouplers (dropping everything below the lowest firing decoupler). Advances
        /// <see cref="Vessel.CurrentStage"/> and returns the jettisoned debris (or null).</summary>
        public static Vessel FireNext(Vessel v)
        {
            // Snap to the next stage that actually does something (NextEventStage assigns default stages),
            // so [Space]/click always fires the stage the HUD highlights and never wastes a press on a gap.
            int s = NextEventStage(v);
            if (s < 0) return null;            // nothing left to stage
            v.CurrentStage = s + 1;

            // ---- ignitions + chute deploys ----
            bool ignitedAny = false;
            foreach (var p in v.Parts)
            {
                bool hostFires = p.Stage == s;
                if (hostFires && (p.Def.Kind == PartKind.Engine || p.Def.Kind == PartKind.SolidBooster)) { p.Ignited = true; ignitedAny = true; }
                foreach (var r in p.Radials)
                {
                    // radials ignite / deploy at their own fire stage, independent of the host
                    if (r.FireStage == s && (r.Def.Kind == PartKind.Engine || r.Def.Kind == PartKind.SolidBooster)) { r.Ignited = true; ignitedAny = true; }
                    if (r.Def.Kind == PartKind.Parachute && r.FireStage == s && !r.Deployed) r.Deployed = true;
                }
                if (p.Def.Kind == PartKind.Parachute && p.Stage == s && !p.Deployed) p.Deployed = true;
            }
            if (ignitedAny) v.EnginesIgnited = true;

            // ---- jettisons ----
            var freed = new List<Part>();      // standalone parts (separate radials dropped from a kept host)
            for (int i = 0; i < v.Parts.Count; i++)
            {
                var host = v.Parts[i];
                for (int k = host.Radials.Count - 1; k >= 0; k--)
                    // parachutes deploy (above) and are never jettisoned as a strap-on
                    if (host.Radials[k].RadialSeparate && host.Radials[k].Def.Kind != PartKind.Parachute && host.Radials[k].Stage == s)
                    { freed.Add(host.Radials[k]); host.Radials.RemoveAt(k); }
            }

            double jh = 0;
            List<Part> decGroup = null;
            int firstDec = -1;
            for (int i = 0; i < v.Parts.Count; i++)
                if (v.Parts[i].Def.Kind == PartKind.Decoupler && v.Parts[i].Stage == s) { firstDec = i; break; }
            if (firstDec >= 0)
            {
                decGroup = v.Parts.GetRange(firstDec, v.Parts.Count - firstDec);
                v.Parts.RemoveRange(firstDec, v.Parts.Count - firstDec);
                foreach (var p in decGroup) jh += p.Def.Height;
            }

            if (freed.Count == 0 && decGroup == null) return null;

            var debris = new Vessel
            {
                Body = v.Body, IsDebris = true, Heading = v.Heading, EnginesIgnited = true,
                Position = v.Position, OnRails = false,
            };
            if (decGroup != null)
            {
                debris.Parts.AddRange(decGroup);           // axial parts keep their own radials
                v.Position += v.Up * jh;                   // remaining stack's base moves up
                debris.Velocity = v.Velocity - v.Up * 2.0; // gentle separation push
            }
            else
            {
                debris.Velocity = v.Velocity + new Solar.Core.Vec2d(-v.Up.Y, v.Up.X) * 3.0; // sideways for strap-ons
            }
            foreach (var r in freed) debris.Parts.Add(r);  // freed radials become standalone debris parts
            return debris;
        }

        /// <summary>The next stage index (&gt;= the vessel's <see cref="Vessel.CurrentStage"/>) that actually
        /// does something — ignites an engine/booster, fires a decoupler, jettisons a separate strap-on, or
        /// deploys a parachute — or -1 if none remain. Both the HUD's active row and <see cref="FireNext"/>
        /// key off this, so the highlighted stage is always exactly what the next press fires (no drift, no
        /// wasted presses on empty stage indices). Fills default stages as a side effect.</summary>
        public static int NextEventStage(Vessel v)
        {
            AssignDefaultStages(v.Parts);
            int max = MaxStage(v.Parts);
            for (int s = Math.Max(0, v.CurrentStage); s <= max; s++)
                if (HasEventAt(v.Parts, s)) return s;
            return -1;
        }

        /// <summary>Whether firing stage <paramref name="s"/> would activate anything on these parts. Mirrors
        /// the action set in <see cref="FireNext"/>: axial engines/boosters/decouplers/parachutes at
        /// <see cref="Part.Stage"/>; radial engines/boosters/parachutes at their <see cref="Part.FireStage"/>;
        /// and separate (non-parachute) strap-ons jettisoned at their <see cref="Part.Stage"/>.</summary>
        private static bool HasEventAt(IReadOnlyList<Part> parts, int s)
        {
            foreach (var p in parts)
            {
                if (p.Stage == s && (p.Def.Kind == PartKind.Engine || p.Def.Kind == PartKind.SolidBooster
                    || p.Def.Kind == PartKind.Decoupler || p.Def.Kind == PartKind.Parachute)) return true;
                foreach (var r in p.Radials)
                {
                    if (r.Def.Kind == PartKind.Parachute) { if (r.FireStage == s) return true; }
                    else if ((r.Def.Kind == PartKind.Engine || r.Def.Kind == PartKind.SolidBooster) && r.FireStage == s) return true;
                    else if (r.RadialSeparate && r.Stage == s) return true;   // strap-on jettison
                }
            }
            return false;
        }

        /// <summary>Fire one specific decoupler regardless of the current stage pointer (the in-flight part
        /// popup's "Decouple" action). Drops that decoupler and everything below it in the stack as debris,
        /// pushing the kept stack up by the jettisoned height — the same separation the decoupler branch of
        /// <see cref="FireNext"/> performs. Returns the debris vessel, or null if <paramref name="decoupler"/>
        /// isn't an attached axial decoupler. The live stage list is derived by <see cref="ComputeStages"/>
        /// from the remaining parts, so the dropped stage disappears on its own.</summary>
        public static Vessel DecoupleAt(Vessel v, Part decoupler)
        {
            if (decoupler == null || decoupler.Def.Kind != PartKind.Decoupler) return null;
            int idx = v.Parts.IndexOf(decoupler);
            if (idx < 0) return null;

            var decGroup = v.Parts.GetRange(idx, v.Parts.Count - idx);
            v.Parts.RemoveRange(idx, v.Parts.Count - idx);
            double jh = 0;
            foreach (var p in decGroup) jh += p.Def.Height;

            var debris = new Vessel
            {
                Body = v.Body, IsDebris = true, Heading = v.Heading, EnginesIgnited = true,
                Position = v.Position, OnRails = false,
            };
            debris.Parts.AddRange(decGroup);           // axial parts keep their own radials
            v.Position += v.Up * jh;                    // remaining stack's base moves up
            debris.Velocity = v.Velocity - v.Up * 2.0; // gentle separation push
            return debris;
        }

        /// <summary>Estimated time (s) to burn <paramref name="dv"/> m/s, walking stages in fire order;
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

        /// <summary>Editor overload: builds parts (incl. attached module dry mass + stage tags) from design entries.</summary>
        public static List<StageStat> ComputeStages(IReadOnlyList<StackEntry> stack, double surfaceG = G0)
        {
            var parts = new List<Part>();
            foreach (var e in stack)
            {
                var p = new Part(e.Def) { Stage = e.Stage, Fuel = e.CurrentFuel };
                foreach (var m in e.Modules) p.Modules.Add(new ModuleInstance(m));
                VesselDesign.MaterializeRadials(e, p);   // symmetric-pair sub-stacks (carry mount stage)
                parts.Add(p);
            }
            return ComputeStages(parts, surfaceG);
        }

        /// <summary>Running thrust/fuel/flow accumulator for a stage's engines.</summary>
        private sealed class Agg
        {
            public double Thrust, Flow;
            public readonly List<string> Engines = new();
            public void AddEngine(Part part)
            {
                if (part.Def.Kind == PartKind.Engine || part.Def.Kind == PartKind.SolidBooster)
                {
                    // Liquid engines honour the in-flight power limiter / on-off (solids are not throttleable).
                    // Scaling thrust and flow together keeps Isp (hence dV) constant while burn time grows.
                    double pw = part.Def.Kind == PartKind.Engine ? (part.EngineOn ? part.PowerLimit : 0) : 1.0;
                    Thrust += part.Def.Thrust * pw;
                    Flow += part.Def.FuelFlowAtMax * pw;
                    if (!Engines.Contains(part.Def.Name)) Engines.Add(part.Def.Name);
                }
            }
        }

        private static double MassOf(List<Part> present)
        {
            double m = 0;
            foreach (var p in present) { m += p.Mass; foreach (var r in p.Radials) m += r.Mass; }
            return m;
        }

        /// <summary>The parts (axial group below the lowest firing decoupler, plus any separate radials)
        /// jettisoned when stage <paramref name="stage"/> fires. Does not mutate <paramref name="present"/>.</summary>
        private static List<Part> DropSet(List<Part> present, int stage)
        {
            var res = new List<Part>();
            int dec = -1;
            for (int i = 0; i < present.Count; i++)
                if (present[i].Def.Kind == PartKind.Decoupler && present[i].Stage == stage) { dec = i; break; }
            int decStart = dec >= 0 ? dec : present.Count;
            if (dec >= 0)
                for (int i = dec; i < present.Count; i++) { res.Add(present[i]); foreach (var r in present[i].Radials) res.Add(r); }
            // separate radials firing this stage on hosts that are NOT part of the dropped axial group
            // (parachutes deploy instead of jettisoning, so they never count as a drop)
            for (int i = 0; i < decStart; i++)
                foreach (var r in present[i].Radials)
                    if (r.RadialSeparate && r.Def.Kind != PartKind.Parachute && r.Stage == stage) res.Add(r);
            return res;
        }

        /// <summary>Remove stage <paramref name="stage"/>'s jettisoned parts from <paramref name="present"/>.</summary>
        private static void ApplyDrops(List<Part> present, int stage)
        {
            int dec = -1;
            for (int i = 0; i < present.Count; i++)
                if (present[i].Def.Kind == PartKind.Decoupler && present[i].Stage == stage) { dec = i; break; }
            int decStart = dec >= 0 ? dec : present.Count;
            for (int i = 0; i < decStart; i++)
            {
                var host = present[i];
                for (int k = host.Radials.Count - 1; k >= 0; k--)
                    if (host.Radials[k].RadialSeparate && host.Radials[k].Def.Kind != PartKind.Parachute && host.Radials[k].Stage == stage) host.Radials.RemoveAt(k);
            }
            if (dec >= 0) present.RemoveRange(dec, present.Count - dec);
        }

        private static double FuelOf(List<Part> parts)
        {
            double f = 0; foreach (var p in parts) f += p.Fuel; return f;
        }

        /// <summary>A staging-only shallow clone of a part: enough fields for mass/thrust/stage math, with
        /// its own (cloned) radial list so the drop simulation can mutate it without touching the live vessel.
        /// Module instances are shared (read-only here); crew is irrelevant to staging.</summary>
        private static Part ClonePart(Part p)
        {
            var c = new Part(p.Def)
            {
                Fuel = p.Fuel, Ignited = p.Ignited, Deployed = p.Deployed,
                PowerLimit = p.PowerLimit, EngineOn = p.EngineOn,
                Stage = p.Stage, FireStage = p.FireStage, RadialSeparate = p.RadialSeparate,
                RadialMountId = p.RadialMountId, RadialSide = p.RadialSide, RadialSlot = p.RadialSlot,
            };
            foreach (var m in p.Modules) c.Modules.Add(m);
            foreach (var r in p.Radials) c.Radials.Add(ClonePart(r));
            return c;
        }

        public static List<StageStat> ComputeStages(List<Part> partsIn, double surfaceG = G0)
        {
            // Clone before simulating: ApplyDrops removes radials from the parts it walks, and a live
            // vessel passes its real Part list here (the HUD recomputes the stage list every frame), so
            // mutating shared parts would silently strip a craft's strap-on boosters on the pad.
            var present = new List<Part>();
            foreach (var p in partsIn) present.Add(ClonePart(p));
            AssignDefaultStages(present);
            int maxStage = MaxStage(present);

            var stats = new List<StageStat>();
            bool remainderDone = false;   // the never-dropped fuel is burned by exactly one (final) stage
            int lastDropAttributed = -1;  // a dropped group's fuel is credited to exactly one emitted stage
            for (int s = 0; s <= maxStage; s++)
            {
                // actions occurring at stage s (evaluated before its drops are applied)
                bool axialDec = ActionAt(present, s, PartKind.Decoupler);
                bool radialDrop = SepRadialAt(present, s);
                bool dropsThis = axialDec || radialDrop;
                bool chuteThis = ChuteAt(present, s);
                var dropNow = dropsThis ? DropSet(present, s) : null;   // the parts THIS stage jettisons (for the label)

                ApplyDrops(present, s);
                if (present.Count == 0) break;

                double m0 = MassOf(present);

                // engines burning during stage s: ignited by now (axial Stage <= s, radials whose host
                // axial part ignited at Stage <= s) and still attached
                var agg = new Agg();
                for (int i = 0; i < present.Count; i++)
                {
                    var p = present[i];
                    if ((p.Def.Kind == PartKind.Engine || p.Def.Kind == PartKind.SolidBooster) && p.Stage <= s) agg.AddEngine(p);
                    foreach (var r in p.Radials)
                        if ((r.Def.Kind == PartKind.Engine || r.Def.Kind == PartKind.SolidBooster) && r.FireStage <= s)
                            agg.AddEngine(r);
                }

                // parts that newly ignite THIS stage (axial Stage == s, radials FireStage == s) -> the ignite list
                var igniteNow = new List<Part>();
                for (int i = 0; i < present.Count; i++)
                {
                    var p = present[i];
                    if ((p.Def.Kind == PartKind.Engine || p.Def.Kind == PartKind.SolidBooster) && p.Stage == s) igniteNow.Add(p);
                    foreach (var r in p.Radials)
                        if ((r.Def.Kind == PartKind.Engine || r.Def.Kind == PartKind.SolidBooster) && r.FireStage == s) igniteNow.Add(r);
                }

                // Events-only stage list (KSP-style): a row exists only when something activates this stage
                // -- an engine/booster ignites, a decoupler/strap-on drops, or a parachute deploys. A pure
                // burn-continuation gets no "Burn" row; its dV is folded into the igniting stage (below).
                if (igniteNow.Count == 0 && !dropsThis && !chuteThis) continue;

                // Fuel this stage burns = fuel of the next group it drops, credited to exactly ONE emitted
                // stage (the first event at/after the previous drop) so a second event before the same drop
                // -- e.g. a chute deploying mid-burn -- can't double-count it. If nothing drops later, the
                // engines burn the remaining fuel, attributed once at the first thrust-bearing final stage.
                int nd = NextDropStage(present, s, maxStage);
                bool finalHere = false;
                double fuel = 0, fuelCap = 0;
                if (nd >= 0 && nd != lastDropAttributed)
                {
                    var g = DropSet(present, nd);
                    fuel = FuelOf(g); foreach (var p in g) fuelCap += p.Def.FuelCapacity;
                    lastDropAttributed = nd;
                }
                else if (nd < 0 && !remainderDone && agg.Thrust > 0)
                {
                    var ff = FlattenFuel(present);
                    fuel = FuelOf(ff); foreach (var p in ff) fuelCap += p.Def.FuelCapacity;
                    finalHere = true;
                }

                string label = agg.Engines.Count > 0 ? string.Join("+", agg.Engines)
                             : chuteThis ? "(parachute)"
                             : dropsThis ? "(decouple)" : "(no engine)";
                var st = new StageStat
                {
                    Number = s, Engines = label, Fuel = fuel, FuelCap = fuelCap, Decouples = dropsThis,
                    RadialEvent = radialDrop, AxialDecouple = axialDec, Chute = chuteThis,
                    Ignites = GroupCount(igniteNow),
                    Drops = dropNow != null ? GroupCount(dropNow) : new List<string>(),
                    Action = DeriveAction(s, igniteNow.Count > 0, radialDrop, axialDec, chuteThis),
                };
                if (agg.Thrust > 0 && fuel > 0 && m0 > fuel)
                {
                    double isp = agg.Thrust / agg.Flow / G0;
                    st.DeltaV = isp * G0 * Math.Log(m0 / (m0 - fuel));
                    st.Twr = agg.Thrust / (m0 * surfaceG);
                    st.BurnTime = fuel / agg.Flow;
                }
                stats.Add(st);
                if (finalHere) remainderDone = true;
            }
            return stats;
        }

        /// <summary>Friendly grouped part names with counts in first-seen order, e.g. a pair of boosters
        /// becomes "2x Thumper-R" (ASCII 'x' per the font constraint), a lone part stays "Terrier".</summary>
        private static List<string> GroupCount(List<Part> parts)
        {
            var order = new List<string>();
            var count = new Dictionary<string, int>();
            foreach (var p in parts)
            {
                var n = p.Def.Name;
                if (!count.ContainsKey(n)) { count[n] = 0; order.Add(n); }
                count[n]++;
            }
            var res = new List<string>();
            foreach (var n in order) res.Add(count[n] > 1 ? count[n] + "x " + n : n);
            return res;
        }

        /// <summary>Short verb for what a stage does, leading with the separation event (KSP-style): a strap-on
        /// jettison reads "Drop boosters", an axial decoupler "Decouple", an ignition "Liftoff" (stage 0) or
        /// "Ignite", a chute "Parachute". Only event stages are emitted (see <see cref="ComputeStages"/>), so
        /// the trailing "Stage" is just a defensive default.</summary>
        private static string DeriveAction(int stage, bool ignites, bool radialDrop, bool axialDec, bool chute)
        {
            if (radialDrop) return "Drop boosters";
            if (axialDec) return "Decouple";
            if (ignites) return stage == 0 ? "Liftoff" : "Ignite";
            if (chute) return "Parachute";
            return "Stage";
        }

        private static bool ActionAt(List<Part> present, int stage, PartKind kind)
        {
            foreach (var p in present) if (p.Def.Kind == kind && p.Stage == stage) return true;
            return false;
        }

        private static bool SepRadialAt(List<Part> present, int stage)
        {
            foreach (var p in present) foreach (var r in p.Radials)
                if (r.RadialSeparate && r.Def.Kind != PartKind.Parachute && r.Stage == stage) return true;
            return false;
        }

        /// <summary>Whether a parachute deploys at stage <paramref name="stage"/>: an axial chute at its
        /// <see cref="Part.Stage"/>, or a radial chute at its <see cref="Part.FireStage"/>.</summary>
        private static bool ChuteAt(List<Part> present, int stage)
        {
            foreach (var p in present)
            {
                if (p.Def.Kind == PartKind.Parachute && p.Stage == stage) return true;
                foreach (var r in p.Radials)
                    if (r.Def.Kind == PartKind.Parachute && r.FireStage == stage) return true;
            }
            return false;
        }

        /// <summary>Smallest stage &gt; <paramref name="s"/> (up to maxStage) at which something is jettisoned.</summary>
        private static int NextDropStage(List<Part> present, int s, int maxStage)
        {
            for (int t = s + 1; t <= maxStage; t++)
                if (ActionAt(present, t, PartKind.Decoupler) || SepRadialAt(present, t)) return t;
            return -1;
        }

        /// <summary>The fuel-bearing parts (axial + radials) currently present, for the final-stage burn.</summary>
        private static List<Part> FlattenFuel(List<Part> present)
        {
            var res = new List<Part>();
            foreach (var p in present) { if (p.Fuel > 0) res.Add(p); foreach (var r in p.Radials) if (r.Fuel > 0) res.Add(r); }
            return res;
        }
    }
}

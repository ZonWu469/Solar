using System;
using System.Collections.Generic;
using Solar.Core;
using Solar.Parts;
using Solar.Physics;

namespace Solar.Vessels
{
    /// <summary>
    /// A flying (or landed) rocket. Position is the base of the stack, relative to the
    /// current SOI body's center; Heading is the world angle of the rocket's "up" axis.
    /// </summary>
    public sealed class Vessel
    {
        public readonly List<Part> Parts = new();
        public CelestialBody Body;
        public Vec2d Position;
        public Vec2d Velocity;
        public double Heading = Math.PI / 2;
        public double AngularVelocity;   // rad/s, signed (+ CCW). Persists as momentum in vacuum; damped in atmosphere.
        public double Throttle;
        public bool OnRails;
        public OrbitalElements Orbit;
        public bool Landed;
        public bool Destroyed;
        public bool IsColony;        // a landed base the player has established (see Colony); enables offline production
        public double ColonyGrowthTimer;  // accumulated self-sustaining time toward the next colonist (see Colony.TryGrowCrew)
        public bool EnginesIgnited;  // first stage has been fired
        public int CurrentStage;     // next stage index to fire (0 = first); advanced by Staging.FireNext
        public bool IsDebris;
        public bool IsEva;            // a kerbal on EVA: a one-crew jetpack "vessel" (see Eva); always off-rails
        public CrewRole EvaRole;      // the role of the kerbal aboard, for the EVA sprite
        // Mission can be cancelled (flight scrapped, design reopened in the editor) from launch until the
        // ship first touches another object (lands/crashes after liftoff) or docks. HasLeftLaunchSite guards
        // the touch test against pad jitter so resting on the launch pad never locks cancel.
        public bool MissionCancelable = true;
        public bool HasLeftLaunchSite;

        // RCS translation: per-frame body-frame command (set by the flight scene from input),
        // x = right(+)/left(-), y = fore(+, along Up)/aft(-); each component in [-1,1].
        public Vec2d RcsCommand;
        public bool RcsEnabled;

        // resources (ship-wide pools fed by slot modules)
        public double ElectricCharge;
        public double Monoprop;             // monopropellant for RCS translation
        public double Ore;                  // raw ore mined by drills, refined into fuel by an ISRU converter
        public double PendingScience;       // queued experiment data still being transmitted (transient)
        public double Water, Oxygen, Food;  // life-support resources (consumed per crew member)
        public bool LifeSupportOk = true;   // false once crew run out of any life-support resource
        private double _lsDeprivedFor;      // seconds any LS resource has been empty while crewed
        /// <summary>Names of crew that died this tick; the flight scene drains these to raise toasts.</summary>
        public readonly List<string> RecentDeaths = new();
        /// <summary>Modules that broke (malfunctioned) this tick; the flight scene drains these to toast.</summary>
        public readonly List<string> RecentFailures = new();
        /// <summary>Modules an engineer repaired this tick; the flight scene drains these to toast.</summary>
        public readonly List<string> RecentRepairs = new();

        // per-crew life-support consumption (units/s) and how long crew survive total deprivation.
        // Global tunables sourced from Content/balance.json via Core.Balance (with in-code defaults).
        public static double OxygenPerCrew => Core.Balance.OxygenPerCrew;
        public static double WaterPerCrew => Core.Balance.WaterPerCrew;
        public static double FoodPerCrew => Core.Balance.FoodPerCrew;
        public static double LsDeathTime => Core.Balance.LsDeathTime;  // 6 h of an empty resource kills one crew member

        public const double G0 = 9.81;   // TODO(balance.json): standard gravity used by the rocket equation

        public Vec2d Up => Vec2d.FromAngle(Heading);
        public double Altitude => Position.Length - Body.SurfaceRadiusAt(Position.Angle());

        /// <summary>The axial stack plus every radially-attached part — the single iteration source for
        /// mass / drag / power / thrust aggregates. Height and stage boundaries use the axial stack only.</summary>
        public IEnumerable<Part> AllParts()
        {
            foreach (var p in Parts)
            {
                yield return p;
                foreach (var r in p.Radials) yield return r;
            }
        }

        public double TotalMass
        {
            get { double m = 0; foreach (var p in AllParts()) m += p.Mass; return Math.Max(m, 1); }
        }

        public double TotalHeight
        {
            get { double h = 0; foreach (var p in Parts) h += p.Def.Height; return h; }
        }

        /// <summary>Axial distance (m) from the nose (stack index 0) to the centre of <paramref name="target"/>,
        /// growing down the stack. A radially-attached part inherits its host's offset. Used by the solar-shield
        /// shadow: a part with a larger offset sits "below" (farther from the Sun when the nose is sun-aligned).
        /// Returns the full stack height if the part isn't found.</summary>
        public double AxialOffset(Part target)
        {
            double y = 0;
            foreach (var p in Parts)
            {
                double centre = y + p.Def.Height * 0.5;
                if (p == target) return centre;
                foreach (var r in p.Radials) if (r == target) return centre;
                y += p.Def.Height;
            }
            return y;
        }

        public double TotalCdA
        {
            get
            {
                double c = 0;
                foreach (var p in AllParts())
                {
                    c += p.Def.CdA;
                    if (p.Def.Kind == PartKind.Parachute && p.Deployed) c += p.Def.DeployedCdA;
                }
                // a nose cone at the top of the stack streamlines the whole vehicle
                if (Parts.Count > 0 && Parts[0].Def.Kind == PartKind.Aero) c *= 0.8;
                return c;
            }
        }

        /// <summary>Count of fitted landing-leg modules; each raises the survivable touchdown speed.</summary>
        public int LandingLegs
        {
            get { int n = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) if (m.Def.Kind == ModuleKind.LandingLeg) n++; return n; }
        }

        /// <summary>A bare hull survives only a gentle touchdown; landing legs/gear are what make a
        /// hard landing survivable. Tuned so a pod alone clears ~6 m/s, while a properly legged lander
        /// (gear's authored <see cref="PartDef.ImpactTolerance"/> from parts.json + a margin per leg
        /// module) clears ~20-30 m/s.</summary>
        public const double BareLandingSpeed = 6.0;
        public const double LandingLegMargin = 5.0;

        /// <summary>Touchdown speed the vessel survives (see <see cref="BareLandingSpeed"/>).</summary>
        public double SafeLandingSpeed
        {
            get
            {
                double t = BareLandingSpeed + LandingLegMargin * LandingLegs;
                foreach (var p in AllParts()) t += p.Def.ImpactTolerance;
                return t;
            }
        }

        /// <summary>Whether a touchdown at <paramref name="impactSpeed"/> (m/s) is survivable. A small
        /// tolerance keeps the boundary from being brittle (an impact shown as "6" doesn't crash a craft
        /// rated for 6). Pure so it can be exercised by the self-tests.</summary>
        public bool SurvivesTouchdown(double impactSpeed) => impactSpeed <= SafeLandingSpeed + 0.05;

        public bool ChuteDeployed
        {
            get { foreach (var p in AllParts()) if (p.Def.Kind == PartKind.Parachute && p.Deployed) return true; return false; }
        }

        /// <summary>Mass-weighted axial position of the center of mass (local +Y, up the stack). Radials are
        /// taken at their host part's axial station. Used to place the chute drag point relative to the CoM.</summary>
        public double CenterOfMassY
        {
            get
            {
                double m = 0, my = 0;
                foreach (var p in Parts)
                {
                    double y = PartLocalCenter(p).Y;
                    double pm = p.Mass; foreach (var r in p.Radials) pm += r.Mass;
                    m += pm; my += pm * y;
                }
                return m > 0 ? my / m : 0;
            }
        }

        /// <summary>The deployed parachutes' drag application point as a signed axial offset from the center
        /// of mass (local +Y): positive when the chutes sit above the CoM. <paramref name="bothEnds"/> is
        /// true when significant chute drag acts both above and below the CoM (top+bottom chutes), the cue
        /// to hold the craft broadside (flat) for a horizontal descent. Returns false if no chute is deployed.</summary>
        public bool DeployedChuteOffset(out double offsetFromCoM, out bool bothEnds)
        {
            offsetFromCoM = 0; bothEnds = false;
            double com = CenterOfMassY;
            double w = 0, wy = 0, above = 0, below = 0;
            foreach (var p in Parts)
            {
                double y = PartLocalCenter(p).Y;
                AccumChute(p, y, com, ref w, ref wy, ref above, ref below);
                foreach (var r in p.Radials) AccumChute(r, y, com, ref w, ref wy, ref above, ref below);
            }
            if (w <= 0) return false;
            offsetFromCoM = wy / w - com;
            bothEnds = above > 0.15 * w && below > 0.15 * w;
            return true;
        }

        private static void AccumChute(Part p, double y, double com,
                                       ref double w, ref double wy, ref double above, ref double below)
        {
            if (p.Def.Kind != PartKind.Parachute || !p.Deployed) return;
            double cda = p.Def.DeployedCdA;
            if (cda <= 0) return;
            w += cda; wy += cda * y;
            if (y >= com) above += cda; else below += cda;
        }

        public bool HasFins
        {
            get { foreach (var p in Parts) if (p.Def.Kind == PartKind.Fins) return true; return false; }
        }

        /// <summary>Count of fitted reaction-wheel modules. They provide attitude authority in vacuum,
        /// but only while the vessel has electric charge (see <see cref="ControlTorque"/>).</summary>
        public int ReactionWheels
        {
            get { int n = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) if (m.Def.Kind == ModuleKind.ReactionWheel) n++; return n; }
        }

        /// <summary>Total reaction-wheel torque (N*m), summed from each wheel module's data-driven
        /// <see cref="ModuleDef.Torque"/> (modules.json). Gated on power by <see cref="ControlTorque"/>.</summary>
        public double ReactionWheelTorque
        {
            get { double t = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) if (m.Def.Kind == ModuleKind.ReactionWheel) t += m.Def.Torque; return t; }
        }

        /// <summary>Total rotational torque (N*m) RCS blocks contribute, from each block's data-driven
        /// <see cref="ModuleDef.Torque"/>. Gated like translation in <see cref="ControlTorque"/>.</summary>
        public double RcsTorque
        {
            get { double t = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) if (m.Def.Kind == ModuleKind.RCS) t += m.Def.Torque; return t; }
        }

        /// <summary>Dynamic pressure (Pa) on the vessel right now: zero in vacuum, rising with air
        /// density and speed. Drives how much authority aerodynamic fins provide.</summary>
        public double DynamicPressure
        {
            get { double rho = Body?.Atmo?.DensityAt(Altitude) ?? 0; return 0.5 * rho * Velocity.LengthSquared; }
        }

        /// <summary>Approximate moment of inertia (kg·m²) of the stack about its center, modeled as a
        /// slender box of the total mass with the stack's height and widest part. Heavier and longer
        /// craft resist rotation more, so angular acceleration falls off with size and mass.</summary>
        public double MomentOfInertia
        {
            get
            {
                double maxW = 0;
                foreach (var p in Parts) maxW = Math.Max(maxW, p.Def.Width);
                double L = Math.Max(TotalHeight, 1), W = Math.Max(maxW, 1);
                return Math.Max(TotalMass * (L * L + W * W) / 12.0, 1);
            }
        }

        /// <summary>Best command-part steering authority (rad/s², the authored <see cref="PartDef.ControlAuthority"/>
        /// from parts.json) over all fitted pods — the minimum angular acceleration the craft can command
        /// regardless of its mass. Zero if no command part is present.</summary>
        public double PodControlAccel
        {
            get { double a = 0; foreach (var p in AllParts()) if (p.Def.Kind == PartKind.Pod && p.Def.ControlAuthority > a) a = p.Def.ControlAuthority; return a; }
        }

        /// <summary>Total attitude-control torque (N·m): a command-part minimum (its authored authority
        /// times the moment of inertia, so it holds at any mass), plus reaction wheels (only while
        /// powered), RCS blocks (gated like translation), and aerodynamic fins (atmosphere only).
        /// Angular acceleration is this divided by <see cref="MomentOfInertia"/>.</summary>
        public double ControlTorque
        {
            get
            {
                double podMin = PodControlAccel * MomentOfInertia;                        // size-independent minimum
                if (HasPilot) podMin *= 1 + PilotControlGain;                              // a hands-on pilot wrings out more authority
                double wheels = ElectricCharge > 0 ? ReactionWheelTorque : 0;             // gyros (per-module torque), only with power
                double rcs = (RcsEnabled && Monoprop > 0 && ElectricCharge > 0) ? RcsTorque : 0;
                double fins = HasFins ? 30000.0 * Math.Clamp(DynamicPressure / 4000.0, 0, 1) : 0;   // TODO(balance.json): fin torque scale
                return podMin + wheels + rcs + fins;
            }
        }

        /// <summary>Rotation-rate ceiling (rad/s). Reaction wheels let the craft spin up faster and reach
        /// a higher cap, but it stays bounded so fine attitude control is preserved.</summary>
        // TODO(balance.json): turn-rate base / per-wheel gain / pilot bonus / ceiling are global tunables.
        public double MaxTurnRate => Math.Min(0.35 + 0.22 * (ElectricCharge > 0 ? ReactionWheels : 0) + (HasPilot ? PilotTurnRateBonus : 0), 1.6);

        // ----- pilot bonuses (a living Pilot improves attitude control) -----
        // TODO(balance.json): pilot control gain and turn-rate bonus.
        public const double PilotControlGain = 0.5;     // +50% command-pod attitude authority with a pilot aboard
        public const double PilotTurnRateBonus = 0.3;   // rad/s added to the turn-rate ceiling with a pilot aboard

        /// <summary>Whether a living pilot is aboard (grants hands-on attitude control / SAS).</summary>
        public bool HasPilot
        {
            get { foreach (var c in AllCrew()) if (c.Role == CrewRole.Pilot) return true; return false; }
        }

        /// <summary>Whether the craft can run attitude hold (SAS) at all: a fitted SAS-capable command
        /// part (<see cref="PartDef.Sas"/>), any reaction wheel, or a pilot aboard. Power is checked by
        /// <see cref="SasAvailable"/>.</summary>
        public bool HasSas
        {
            get { if (HasPilot) return true; foreach (var p in AllParts()) if (p.Def.Sas) return true; return ReactionWheels > 0; }
        }

        /// <summary>SAS can be engaged right now: it needs a SAS-capable part and electric charge,
        /// mirroring how reaction wheels in <see cref="ControlTorque"/> are gated on power.</summary>
        public bool SasAvailable => HasSas && ElectricCharge > 0;

        // Cached Segments() result. Boundaries depend only on the part layout (which kinds sit where),
        // never on fuel, so the many per-frame thrust/flow/burn-fuel readers can share one build. Parts
        // are only ever added or removed in flight (staging, docking, undocking) -- never swapped in place
        // -- so Parts.Count is an exact invalidation key.
        List<(int start, int end)> _segCache;
        int _segCacheCount = -1;

        /// <summary>Inclusive index ranges between decouplers (decouplers belong to no segment).</summary>
        public List<(int start, int end)> Segments()
        {
            if (_segCache != null && _segCacheCount == Parts.Count) return _segCache;
            var res = new List<(int, int)>();
            int s = 0;
            for (int i = 0; i < Parts.Count; i++)
            {
                // decouplers are stage boundaries; docking ports isolate docked modules so the active
                // stage never fires or drains across a dock (undocking is a separate, explicit action).
                if (Parts[i].Def.Kind == PartKind.Decoupler)
                {
                    if (i > s) res.Add((s, i - 1));
                    s = i + 1;
                }
                else if (Parts[i].Def.Kind == PartKind.DockingPort)
                {
                    if (i > s) res.Add((s, i - 1));
                    s = i + 1;   // split here (like a gap) without making the port a stage-fire point
                }
            }
            if (s < Parts.Count) res.Add((s, Parts.Count - 1));
            _segCache = res;
            _segCacheCount = Parts.Count;
            return res;
        }

        /// <summary>Liquid fuel in a segment (solid boosters carry self-contained fuel that does not
        /// feed liquid engines, so it is excluded here). Radial tanks attached to parts in the segment
        /// cross-feed into the same pool.</summary>
        public double SegmentFuel((int start, int end) seg)
        {
            double f = 0;
            for (int i = seg.start; i <= seg.end; i++)
            {
                if (Parts[i].Def.Kind != PartKind.SolidBooster) f += Parts[i].Fuel;
                foreach (var r in Parts[i].Radials)
                    if (r.Def.Kind != PartKind.SolidBooster) f += r.Fuel;
            }
            return f;
        }

        /// <summary>Per-engine thrust/flow scale from the in-flight power limiter: 0 if switched off,
        /// else its 0..1 power output. Applies to liquid engines (solids are not throttleable).</summary>
        private static double EnginePower(Part p) => p.EngineOn ? p.PowerLimit : 0;

        /// <summary>Current thrust (N): throttleable liquid engines plus solid boosters (always full).</summary>
        public double CurrentThrust => LiquidThrust * Throttle + SolidThrust;

        /// <summary>An engine is "axial" when it fires (near) along the craft +Up axis; otherwise it is an
        /// off-axis (radial / lateral) engine driven by the Q/E translation command rather than the throttle.</summary>
        private static bool IsAxial(Part p) => Math.Abs(p.Def.ThrustAngle) < 1e-6;

        /// <summary>Signed lateral throttle for off-axis engines, from the Q/E translation command
        /// (<see cref="RcsCommand"/>.X): +1 fires toward craft +Right, -1 toward -Right (bidirectional).</summary>
        private double LateralCommand => Math.Clamp(RcsCommand.X, -1, 1);

        /// <summary>True while off-axis (radial) engines are actively firing on the lateral command: an ignited,
        /// fuelled off-axis engine plus a nonzero Q/E command. Gates physics/rails like a throttle burn.</summary>
        public bool RadialThrusting
        {
            get
            {
                double lateral = LateralCommand;
                if (lateral == 0) return false;
                foreach (var seg in Segments())
                {
                    if (SegmentFuel(seg) <= 0) continue;
                    for (int i = seg.start; i <= seg.end; i++)
                    {
                        if (IsOffAxisEngineLive(Parts[i], lateral)) return true;
                        foreach (var r in Parts[i].Radials) if (IsOffAxisEngineLive(r, lateral)) return true;
                    }
                }
                return false;
            }
        }

        // an ignited off-axis engine that actually fires on the current lateral command (its mount side
        // must match the commanded direction, so a single-sided thruster only burns toward where it pushes)
        private bool IsOffAxisEngineLive(Part p, double lateral) =>
            p.Def.Kind == PartKind.Engine && p.Ignited && !IsAxial(p) && EngineLevel(p, lateral) != 0;

        /// <summary>Signed drive level for an engine under the current lateral command (sign matches craft
        /// +Right). Axial -> throttle. A single-sided off-axis thruster (<see cref="Part.RadialSide"/> &gt;= 0)
        /// only fires when the Q/E command points the way it pushes (left-mounted/side 1 pushes +Right,
        /// right-mounted/side 0 pushes -Right), so it never reverses. Untagged off-axis engines
        /// (RadialSide &lt; 0) keep the old bidirectional behavior.</summary>
        private double EngineLevel(Part p, double lateral)
        {
            if (IsAxial(p)) return Throttle;
            if (p.RadialSide >= 0 && Math.Sign(lateral) != (p.RadialSide == 1 ? 1 : -1)) return 0;
            return lateral;
        }

        /// <summary>World-frame direction an engine fires, given its authored <see cref="PartDef.ThrustAngle"/>
        /// (deg, 0 = craft <see cref="Up"/>; +90 = <see cref="Right"/>, -90 = left), per the angle/screen
        /// convention (Right = Up rotated -90 deg = world angle Heading - 90).</summary>
        public Vec2d ThrustDir(double angleDeg) => Vec2d.FromAngle(Heading - angleDeg * (Math.PI / 180.0));

        /// <summary>World-frame thrust force vector (N). Axial engines fire along craft +Up scaled by
        /// <see cref="Throttle"/>; off-axis (radial) engines fire along their <see cref="PartDef.ThrustAngle"/>
        /// scaled by the signed Q/E command (<see cref="LateralCommand"/>), so the player aims them
        /// left/right independently of the throttle. The magnitude equals <see cref="CurrentThrust"/> for an
        /// all-axial stack at full throttle. Same fuel/segment/power gating as
        /// <see cref="LiquidThrust"/> + <see cref="SolidThrust"/>.</summary>
        public Vec2d ThrustVector
        {
            get
            {
                Vec2d sum = Vec2d.Zero;
                double lateral = LateralCommand;
                foreach (var seg in Segments())
                {
                    if (SegmentFuel(seg) <= 0) continue;
                    for (int i = seg.start; i <= seg.end; i++)
                    {
                        AddEngineThrust(ref sum, Parts[i], lateral);
                        foreach (var r in Parts[i].Radials) AddEngineThrust(ref sum, r, lateral);
                    }
                }
                foreach (var p in AllParts())
                    if (p.Def.Kind == PartKind.SolidBooster && p.Ignited && p.Fuel > 0)
                        sum += ThrustDir(p.Def.ThrustAngle) * p.Def.Thrust;
                return sum;
            }
        }

        private void AddEngineThrust(ref Vec2d sum, Part p, double lateral)
        {
            if (p.Def.Kind != PartKind.Engine || !p.Ignited) return;
            double level = EngineLevel(p, lateral);   // axial -> throttle; off-axis -> side-gated Q/E command
            if (level == 0) return;
            sum += ThrustDir(p.Def.ThrustAngle) * (p.Def.Thrust * EnginePower(p) * level);
        }

        /// <summary>Thrust achievable right now (axial liquid at full throttle + solids), for TWR/preview.
        /// Off-axis (radial) engines are a separate lateral system and are excluded.</summary>
        public double MaxAvailableThrust => LiquidThrust + SolidThrust;

        /// <summary>Throttleable thrust from ignited <em>axial</em> liquid engines whose segment still has fuel
        /// (radial engines on a segment's parts draw from the same cross-fed pool). Off-axis engines fire on
        /// the Q/E command instead, so they are excluded from this main-throttle figure.</summary>
        public double LiquidThrust
        {
            get
            {
                double t = 0;
                foreach (var seg in Segments())
                {
                    if (SegmentFuel(seg) <= 0) continue;
                    for (int i = seg.start; i <= seg.end; i++)
                    {
                        if (Parts[i].Def.Kind == PartKind.Engine && Parts[i].Ignited && IsAxial(Parts[i])) t += Parts[i].Def.Thrust * EnginePower(Parts[i]);
                        foreach (var r in Parts[i].Radials)
                            if (r.Def.Kind == PartKind.Engine && r.Ignited && IsAxial(r)) t += r.Def.Thrust * EnginePower(r);
                    }
                }
                return t;
            }
        }

        /// <summary>Full thrust from ignited solid boosters (axial or radial) that still have fuel.</summary>
        public double SolidThrust
        {
            get
            {
                double t = 0;
                foreach (var p in AllParts())
                    if (p.Def.Kind == PartKind.SolidBooster && p.Ignited && p.Fuel > 0)
                        t += p.Def.Thrust;
                return t;
            }
        }

        /// <summary>Propellant mass flow right now (kg/s): solids at full rate, liquids scaled by
        /// throttle. Mirrors <see cref="DrainFuel"/> so a finite-burn projection can derive the
        /// effective exhaust velocity and remaining burn time without re-deriving staging.</summary>
        public double CurrentMassFlow
        {
            get
            {
                double flow = 0;
                foreach (var p in AllParts())
                    if (p.Def.Kind == PartKind.SolidBooster && p.Ignited && p.Fuel > 0)
                        flow += p.Def.FuelFlowAtMax;
                if (Throttle > 0)
                    foreach (var seg in Segments())
                    {
                        if (SegmentFuel(seg) <= 0) continue;
                        for (int i = seg.start; i <= seg.end; i++)
                        {
                            if (Parts[i].Def.Kind == PartKind.Engine && Parts[i].Ignited && IsAxial(Parts[i])) flow += Parts[i].Def.FuelFlowAtMax * Throttle * EnginePower(Parts[i]);
                            foreach (var r in Parts[i].Radials)
                                if (r.Def.Kind == PartKind.Engine && r.Ignited && IsAxial(r)) flow += r.Def.FuelFlowAtMax * Throttle * EnginePower(r);
                        }
                    }
                return flow;
            }
        }

        /// <summary>Propellant the currently-firing engines can still draw before a staging event
        /// (burning solids' own fuel + the cross-fed pool of each segment with an ignited liquid
        /// engine). Divided by <see cref="CurrentMassFlow"/> this gives the max burn time.</summary>
        public double ActiveBurnFuel
        {
            get
            {
                double fuel = 0;
                foreach (var p in AllParts())
                    if (p.Def.Kind == PartKind.SolidBooster && p.Ignited && p.Fuel > 0)
                        fuel += p.Fuel;
                foreach (var seg in Segments())
                {
                    bool hasLiquidEngine = false;
                    for (int i = seg.start; i <= seg.end && !hasLiquidEngine; i++)
                    {
                        if (Parts[i].Def.Kind == PartKind.Engine && Parts[i].Ignited && IsAxial(Parts[i])) { hasLiquidEngine = true; break; }
                        foreach (var r in Parts[i].Radials)
                            if (r.Def.Kind == PartKind.Engine && r.Ignited && IsAxial(r)) { hasLiquidEngine = true; break; }
                    }
                    if (hasLiquidEngine) fuel += SegmentFuel(seg);
                }
                return fuel;
            }
        }

        /// <summary>Drain fuel for dt seconds: liquid engines scale with throttle; solids burn their
        /// own fuel at full rate once ignited (throttle-independent).</summary>
        public void DrainFuel(double dt)
        {
            // solid boosters (axial or radial): each consumes its own fuel at full flow until empty
            foreach (var p in AllParts())
                if (p.Def.Kind == PartKind.SolidBooster && p.Ignited && p.Fuel > 0)
                    p.Fuel = Math.Max(0, p.Fuel - p.Def.FuelFlowAtMax * dt);

            // liquid engines draw from their segment's cross-fed pool: axial engines flow at the main
            // throttle, off-axis (radial) engines at the magnitude of the signed Q/E command.
            double lateral = Math.Abs(LateralCommand);
            if (Throttle <= 0 && lateral <= 0) return;
            foreach (var seg in Segments())
            {
                double flow = 0;
                for (int i = seg.start; i <= seg.end; i++)
                {
                    AccumEngineFlow(Parts[i], ref flow, lateral);
                    foreach (var r in Parts[i].Radials) AccumEngineFlow(r, ref flow, lateral);
                }
                if (flow <= 0) continue;
                double amount = flow * dt;
                double avail = SegmentFuel(seg);
                if (avail <= 0) continue;
                double factor = Math.Max(0, 1 - amount / avail);
                for (int i = seg.start; i <= seg.end; i++)
                {
                    if (Parts[i].Def.Kind != PartKind.SolidBooster) Parts[i].Fuel *= factor;
                    foreach (var r in Parts[i].Radials)
                        if (r.Def.Kind != PartKind.SolidBooster) r.Fuel *= factor;
                }
            }
        }

        private void AccumEngineFlow(Part p, ref double flow, double lateral)
        {
            if (p.Def.Kind != PartKind.Engine || !p.Ignited) return;
            double level = Math.Abs(EngineLevel(p, lateral));   // a thruster burns whichever way it fires
            if (level > 0) flow += p.Def.FuelFlowAtMax * level;
        }

        // ----- RCS translation -----

        public const double RcsThrustPerBlock = 1000.0;  // N of translation authority per RCS block (per-module default)
        public const double RcsIsp = 240.0;              // s, monopropellant specific impulse (per-module default)

        /// <summary>Count of fitted RCS thruster-block modules.</summary>
        public int RcsBlocks
        {
            get { int n = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) if (m.Def.Kind == ModuleKind.RCS) n++; return n; }
        }

        /// <summary>Raw RCS translation thrust (N) from each block's data-driven <see cref="ModuleDef.RcsThrust"/>
        /// (modules.json), before the enable/fuel/power gate.</summary>
        public double RcsRawThrust
        {
            get { double t = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) if (m.Def.Kind == ModuleKind.RCS) t += m.Def.RcsThrust; return t; }
        }

        /// <summary>Thrust-weighted effective monopropellant Isp (s) across the fitted RCS blocks; falls back
        /// to <see cref="RcsIsp"/> when no blocks declare one.</summary>
        public double RcsEffectiveIsp
        {
            get
            {
                double thrust = 0, flowPerG = 0;
                foreach (var p in AllParts()) foreach (var m in p.Modules)
                    if (m.Def.Kind == ModuleKind.RCS && m.Def.RcsThrust > 0 && m.Def.RcsIsp > 0)
                    { thrust += m.Def.RcsThrust; flowPerG += m.Def.RcsThrust / m.Def.RcsIsp; }
                return flowPerG > 0 ? thrust / flowPerG : RcsIsp;
            }
        }

        /// <summary>Total RCS translation thrust (N) available right now: blocks only count while RCS is
        /// enabled, monopropellant remains, and the avionics have power.</summary>
        public double RcsThrust =>
            RcsEnabled && Monoprop > 0 && ElectricCharge > 0 ? RcsRawThrust : 0;

        /// <summary>True while RCS is actively translating this frame (gates physics + warp).</summary>
        public bool RcsActive => RcsThrust > 0 && (RcsCommand.X != 0 || RcsCommand.Y != 0);

        /// <summary>World-frame RCS acceleration (m/s2) for the current command: <see cref="Up"/> is the
        /// fore axis; right is Up rotated clockwise in screen space, per the angle/screen convention.</summary>
        public Vec2d RcsAccel
        {
            get
            {
                if (!RcsActive) return Vec2d.Zero;
                Vec2d up = Up;                       // (cos H, sin H) -- fore (+y command)
                Vec2d right = new Vec2d(up.Y, -up.X); // Up rotated -90 deg -- right (+x command)
                Vec2d dir = right * RcsCommand.X + up * RcsCommand.Y;
                double dl = dir.Length;
                if (dl <= 0) return Vec2d.Zero;
                if (dl > 1) dir /= dl;               // clamp diagonal command magnitude to full authority
                return dir * (RcsThrust / TotalMass);
            }
        }

        /// <summary>Burn monopropellant for dt seconds of the current RCS command (thrust/Isp/g0 flow).</summary>
        public void DrainMonoprop(double dt)
        {
            if (!RcsActive || dt <= 0) return;
            double cmd = Math.Min(1, RcsCommand.Length);
            double flow = RcsThrust * cmd / (RcsEffectiveIsp * G0);
            Monoprop = Math.Max(0, Monoprop - flow * dt);
        }

        // ----- resources -----

        /// <summary>Built-in command-pod battery and the constant avionics draw it spends to run
        /// reaction wheels / instruments, so electric charge always matters even on a bare pod.</summary>
        public const double PodEcCapacity = 100;
        public const double PodEcDraw = 0.05;

        public double EcCapacity
        {
            get
            {
                double c = 0;
                foreach (var p in AllParts())
                {
                    if (p.Def.Kind == PartKind.Pod) c += PodEcCapacity;
                    foreach (var m in p.Modules) c += m.Def.EcCapacity;
                }
                return c;
            }
        }

        /// <summary>Monopropellant storage (fed by tank modules), the propellant RCS translation burns.</summary>
        public double MonopropCapacity
        {
            get { double c = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) if (m.Def.Kind == ModuleKind.Tank) c += m.Def.FuelCapacity; return c; }
        }

        /// <summary>Ore storage (ore tanks plus any buffer built into drills/ISRU modules).</summary>
        public double OreCapacity
        {
            get { double c = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) c += m.Def.OreCapacity; return c; }
        }

        /// <summary>True when a powered, active ore scanner is aboard, so the vessel can survey its body.</summary>
        public bool ScannerOperational
        {
            get
            {
                if (ElectricCharge <= 0) return false;
                foreach (var p in AllParts())
                    foreach (var m in p.Modules)
                        if (m.Def.Kind == ModuleKind.OreScanner && m.Active) return true;
                return false;
            }
        }

        /// <summary>True when a powered, active survey telescope is aboard, so the vessel can discover asteroids.</summary>
        public bool TelescopeOperational
        {
            get
            {
                if (ElectricCharge <= 0) return false;
                foreach (var p in AllParts())
                    foreach (var m in p.Modules)
                        if (m.Def.Kind == ModuleKind.Telescope && m.Active) return true;
                return false;
            }
        }

        /// <summary>Best detection reach (m) among active telescopes aboard; 0 if none.</summary>
        public double TelescopeRange
        {
            get
            {
                double r = 0;
                foreach (var p in AllParts())
                    foreach (var m in p.Modules)
                        if (m.Def.Kind == ModuleKind.Telescope && m.Active && m.Def.ScanRange > r) r = m.Def.ScanRange;
                return r;
            }
        }

        /// <summary>Combined detection progress per second from active telescopes aboard.</summary>
        public double TelescopeRate
        {
            get
            {
                double rate = 0;
                foreach (var p in AllParts())
                    foreach (var m in p.Modules)
                        if (m.Def.Kind == ModuleKind.Telescope && m.Active) rate += m.Def.ScanRate;
                return rate;
            }
        }

        /// <summary>True when landed inside a body's natural livable niche, where life support is halved.</summary>
        public bool InNiche => Landed && Body != null && Body.NicheAt(Position.Angle()) != null;

        public double WaterCapacity
        {
            get { double c = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) c += m.Def.WaterCapacity; return c; }
        }

        public double OxygenCapacity
        {
            get { double c = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) c += m.Def.OxygenCapacity; return c; }
        }

        public double FoodCapacity
        {
            get { double c = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) c += m.Def.FoodCapacity; return c; }
        }

        /// <summary>Any life-support storage at all (drives whether the HUD shows the LS panel).</summary>
        public double LsCapacity => WaterCapacity + OxygenCapacity + FoodCapacity;

        /// <summary>Net per-second recycler regen of each life-support resource from active, powered
        /// recyclers (same gate <see cref="UpdateResources"/> applies). Used for endurance + growth checks.</summary>
        public void LifeSupportRegen(out double water, out double oxygen, out double food)
        {
            water = oxygen = food = 0;
            foreach (var p in AllParts())
                foreach (var m in p.Modules)
                    if (m.Def.Kind == ModuleKind.LifeSupport && (!m.Def.Activatable || m.Active))
                    { water += m.Def.WaterRegen; oxygen += m.Def.OxygenRegen; food += m.Def.FoodRegen; }
        }

        /// <summary>Worst-case life-support endurance (seconds): the soonest oxygen, water or food runs out
        /// at the current crew + recycler balance. PositiveInfinity when uncrewed or fully self-sustaining.</summary>
        public double LifeSupportEndurance()
        {
            int crew = CrewCount;
            if (crew == 0) return double.PositiveInfinity;
            LifeSupportRegen(out double wR, out double oR, out double fR);
            double TimeTo(double amount, double drain) => drain <= 1e-9 ? double.PositiveInfinity : amount / drain;
            return Math.Min(TimeTo(Oxygen, crew * OxygenPerCrew - oR),
                   Math.Min(TimeTo(Water, crew * WaterPerCrew - wR),
                            TimeTo(Food, crew * FoodPerCrew - fR)));
        }

        /// <summary>Whether recyclers fully cover the crew's life-support draw on all three resources — the
        /// mark of a base that can sustain (and grow) its crew indefinitely. Vacuously false when uncrewed.</summary>
        public bool SelfSustaining
        {
            get
            {
                int crew = CrewCount;
                if (crew == 0) return false;
                LifeSupportRegen(out double wR, out double oR, out double fR);
                return oR >= crew * OxygenPerCrew && wR >= crew * WaterPerCrew && fR >= crew * FoodPerCrew;
            }
        }

        /// <summary>Total seats across the vessel (pods + crew cabins).</summary>
        public int SeatCount { get { int n = 0; foreach (var p in AllParts()) n += p.SeatCount; return n; } }

        /// <summary>Crew aboard right now.</summary>
        public int CrewCount { get { int n = 0; foreach (var p in AllParts()) n += p.Crew.Count; return n; } }

        public IEnumerable<CrewMember> AllCrew()
        {
            foreach (var p in AllParts()) foreach (var c in p.Crew) yield return c;
        }

        // ----- crew-role bonuses -----
        // TODO(balance.json): per-specialist gains and the diminishing-returns caps.
        public const double EngineerGainPerCrew = 0.25;   // +25% ISRU/drill throughput per engineer aboard
        public const int EngineerGainCap = 3;
        public const double ScientistGainPerCrew = 0.20;  // +20% science yield per scientist aboard
        public const int ScientistGainCap = 3;

        /// <summary>Living crew of a given specialty aboard.</summary>
        public int CrewCountOfRole(CrewRole role)
        {
            int n = 0; foreach (var c in AllCrew()) if (c.Role == role) n++; return n;
        }

        /// <summary>A specialty's productivity multiplier (>= 1): 1 with none aboard, rising per specialist
        /// up to a cap. Engineers speed ISRU/drilling (<see cref="TickConverters"/>); scientists boost
        /// science yield. Other roles return 1.</summary>
        public double CrewSkill(CrewRole role)
        {
            double per = role == CrewRole.Engineer ? EngineerGainPerCrew
                       : role == CrewRole.Scientist ? ScientistGainPerCrew : 0;
            if (per <= 0) return 1;
            int cap = role == CrewRole.Engineer ? EngineerGainCap : ScientistGainCap;
            // a sick specialist contributes less: weight each by health (1 - illness)
            double effective = 0;
            foreach (var c in AllCrew())
                if (c.Role == role) effective += Math.Clamp(1 - c.Illness, 0, 1);
            return 1 + per * Math.Min(effective, cap);
        }

        /// <summary>Engineer-equivalent repair capability from functioning maintenance drones (0 if none).
        /// Lets a crewless craft self-repair, slowly: it adds to (or stands in for) a crew engineer in
        /// <see cref="Threats"/>. Capped like crew engineers.</summary>
        public double AutoRepairSkill(double ut, Universe u)
        {
            double s = 0;
            foreach (var p in AllParts())
                foreach (var m in p.Modules)
                    if (m.Def.Kind == ModuleKind.MaintenanceDrone && ModuleFunctioning(m, ut, u))
                        s += m.Def.RepairSkill;
            return Math.Min(s, EngineerGainCap);   // reuse the existing diminishing-returns cap
        }

        /// <summary>Total liquid (non-solid) fuel aboard, across axial and radial tanks.</summary>
        public double TotalLiquidFuel
        {
            get { double f = 0; foreach (var p in AllParts()) if (p.Def.Kind != PartKind.SolidBooster) f += p.Fuel; return f; }
        }

        /// <summary>Pull <paramref name="amount"/> kg of liquid fuel from wherever it's available
        /// (used by fuel cells, which draw from any tank, not a single stage).</summary>
        private void DrainAnyFuel(double amount)
        {
            foreach (var p in AllParts())
            {
                if (amount <= 0) break;
                if (p.Def.Kind == PartKind.SolidBooster) continue;
                double take = Math.Min(p.Fuel, amount);
                p.Fuel -= take; amount -= take;
            }
        }

        /// <summary>Spend <paramref name="amount"/> kg of liquid fuel from anywhere on the vessel as a raw
        /// material cost (colony construction). Returns false and spends nothing if the stock is short.</summary>
        public bool TrySpendFuel(double amount)
        {
            if (amount <= 0) return true;
            if (TotalLiquidFuel < amount) return false;
            DrainAnyFuel(amount);
            return true;
        }

        /// <summary>Instantaneous electric-charge production and draw (per second) given the current
        /// solar panel / drill states and sunlight. Single source of truth shared by the live tick
        /// (<see cref="UpdateResources"/>) and the HUD readout.</summary>
        public void EcRates(double ut, Universe u, out double prod, out double draw)
        {
            prod = 0; draw = 0;
            double sun = SolarFactor(ut, u);
            bool hasFuel = TotalLiquidFuel > 0;
            foreach (var p in AllParts())
            {
                if (p.Def.Kind == PartKind.Pod) draw += PodEcDraw;   // avionics
                foreach (var m in p.Modules)
                {
                    if (m.Broken) continue;   // a broken module neither produces nor draws power
                    switch (m.Def.Kind)
                    {
                        case ModuleKind.Rtg: prod += m.Def.EcProduce; break;
                        case ModuleKind.SolarPanel: if (m.Active) prod += m.Def.EcProduce * sun; break;
                        case ModuleKind.FuelCell: if (m.Active && hasFuel) prod += m.Def.EcProduce; break;
                        case ModuleKind.LifeSupport: if (!m.Def.Activatable || m.Active) draw += m.Def.EcDraw; break;
                        case ModuleKind.Harvester: if (m.Active && Landed) draw += m.Def.EcDraw; break;
                        case ModuleKind.IsruConverter: if (m.Active && Ore > 0) draw += m.Def.EcDraw; break;
                        case ModuleKind.OreScanner: if (m.Active) draw += m.Def.EcDraw; break;
                        case ModuleKind.ReactionWheel: draw += m.Def.EcDraw; break;
                        case ModuleKind.Science: if (m.Active) draw += m.Def.EcDraw; break;
                        case ModuleKind.Antenna: if (m.Active) draw += m.Def.EcDraw; break;
                        case ModuleKind.Light: if (m.Active) draw += m.Def.EcDraw; break;
                        case ModuleKind.MaintenanceDrone: if (!m.Def.Activatable || m.Active) draw += m.Def.EcDraw; break;
                    }
                }
            }
        }

        /// <summary>Whether a fitted module is currently doing its job, applying the same resource gates
        /// (EC / fuel / landed / deployed) that <see cref="EcRates"/> uses. Drives the flight HUD status
        /// lights, so keep the per-kind gates here in sync with EcRates. Passive parts with no on/off
        /// concept (batteries, tanks, storage, landing legs) report true ("installed/ready").</summary>
        public bool ModuleFunctioning(ModuleInstance m, double ut, Universe u)
        {
            if (m.Broken) return false;   // a malfunctioning module does nothing until repaired
            bool ec = ElectricCharge > 0;
            switch (m.Def.Kind)
            {
                case ModuleKind.Rtg: return true;                                  // passive generator, always on
                case ModuleKind.SolarPanel: return m.Active && SolarFactor(ut, u) > 0;
                case ModuleKind.FuelCell: return m.Active && TotalLiquidFuel > 0;
                case ModuleKind.Harvester: return m.Active && Landed && ec && (Body?.OreRichness ?? 0) > 0;
                case ModuleKind.IsruConverter: return m.Active && ec && Ore > 0;
                case ModuleKind.OreScanner: return m.Active && ec;
                case ModuleKind.Telescope: return m.Active && ec;
                case ModuleKind.ReactionWheel: return ec;
                case ModuleKind.Science: return m.Active && ec;
                case ModuleKind.Antenna: return m.Active && ec;
                case ModuleKind.Light: return m.Active && ec;
                case ModuleKind.LifeSupport: return (!m.Def.Activatable || m.Active) && (m.Def.EcDraw <= 0 || ec);
                case ModuleKind.RCS: return RcsEnabled && Monoprop > 0 && ec;
                case ModuleKind.Medbay: return (!m.Def.Activatable || m.Active) && (m.Def.EcDraw <= 0 || ec);
                case ModuleKind.RadShield: return true;                            // passive shielding, always on
                case ModuleKind.MaintenanceDrone: return (!m.Def.Activatable || m.Active) && ec;
                default: return true;                                              // Battery / Tank / Storage / LandingLeg
            }
        }

        public bool HasCrew => CrewCount > 0;

        /// <summary>Tick electric charge, life support and harvesting for dt seconds (dt includes warp).</summary>
        public void UpdateResources(double dt, double ut, Universe u)
        {
            if (dt <= 0 || Destroyed) return;
            double ecCap = EcCapacity;

            // electric charge production vs draw (shared with the HUD readout)
            EcRates(ut, u, out double prod, out double draw);

            // life-support recycler regen (water/oxygen/food per second) from active, powered recyclers
            double waterRegen = 0, oxygenRegen = 0, foodRegen = 0;
            foreach (var p in AllParts())
                foreach (var m in p.Modules)
                    if (m.Def.Kind == ModuleKind.LifeSupport && (!m.Def.Activatable || m.Active))
                    { waterRegen += m.Def.WaterRegen; oxygenRegen += m.Def.OxygenRegen; foodRegen += m.Def.FoodRegen; }

            ElectricCharge = Math.Clamp(ElectricCharge + (prod - draw) * dt, 0, Math.Max(ecCap, 0));
            bool ecOk = ElectricCharge > 0 || prod >= draw;

            // life support: each crew member consumes water/oxygen/food; recyclers regen (when powered).
            // A vessel landed inside a body's natural niche shelters its crew, halving consumption.
            int crew = CrewCount;
            double regenGate = ecOk ? 1 : 0;   // recyclers stop without power; stored resources still drain
            double lsFactor = InNiche ? Core.Balance.NicheLifeSupportFactor : 1.0;
            Oxygen = Math.Clamp(Oxygen + (oxygenRegen * regenGate - crew * OxygenPerCrew * lsFactor) * dt, 0, OxygenCapacity);
            Water = Math.Clamp(Water + (waterRegen * regenGate - crew * WaterPerCrew * lsFactor) * dt, 0, WaterCapacity);
            Food = Math.Clamp(Food + (foodRegen * regenGate - crew * FoodPerCrew * lsFactor) * dt, 0, FoodCapacity);

            bool depleted = crew > 0 && (Oxygen <= 0 || Water <= 0 || Food <= 0);
            LifeSupportOk = crew == 0 || !depleted;
            if (depleted)
            {
                _lsDeprivedFor += dt;
                while (_lsDeprivedFor >= LsDeathTime && CrewCount > 0)
                {
                    _lsDeprivedFor -= LsDeathTime;
                    KillOneCrew();
                }
            }
            else _lsDeprivedFor = 0;

            TickConverters(dt);
        }

        /// <summary>Mass-resource conversions for dt seconds — one home for the fuel/ore flows that used
        /// to be three separate loops. Fuel cells burn a fuel trickle (their EC output is in
        /// <see cref="EcRates"/>); drills mine ore while landed over an ore-bearing body; ISRU converters
        /// refine stored ore back into liquid fuel (anywhere). A connected surface base (compound vessel)
        /// pools fuel across the whole assembly, so its miners/ISRU can refuel a docked lander from stock.</summary>
        private void TickConverters(double dt)
        {
            if (dt <= 0) return;
            bool compound = DockLinks.Count > 0;

            // fuel cells: consume a liquid-fuel trickle while active and fuel remains
            double fcDraw = 0;
            foreach (var p in AllParts())
                foreach (var m in p.Modules)
                    if (m.Def.Kind == ModuleKind.FuelCell && m.Active) fcDraw += m.Def.FuelDraw;
            if (fcDraw > 0 && TotalLiquidFuel > 0) DrainAnyFuel(fcDraw * dt);

            if (ElectricCharge <= 0) return;   // mining and refining both need power

            // drills: mine ore (ship-wide pool) while landed over an ore-bearing body, scaled by richness
            // and engineer skill, capped at storage capacity.
            double richness = Body?.OreRichness ?? 0;
            if (Landed && richness > 0)
            {
                double oreRoom = OreCapacity - Ore;
                if (oreRoom > 0)
                {
                    double rate = 0;
                    foreach (var p in AllParts())
                        foreach (var m in p.Modules)
                            if (m.Def.Kind == ModuleKind.Harvester && m.Active && m.Def.OreProduce > 0)
                                rate += m.Def.OreProduce;
                    if (rate > 0) Ore = Math.Min(OreCapacity, Ore + rate * richness * CrewSkill(CrewRole.Engineer) * dt);
                }
            }

            // ISRU: refine stored ore into fuel, bottlenecked by ore on hand and by free fuel capacity
            // (so full tanks never waste ore). Iterates the axial stack for the segment a converter feeds.
            double skill = CrewSkill(CrewRole.Engineer);
            for (int i = 0; i < Parts.Count && Ore > 0; i++)
                foreach (var m in Parts[i].Modules)
                {
                    if (m.Def.Kind != ModuleKind.IsruConverter || !m.Active || m.Def.OreDraw <= 0 || m.Def.FuelProduce <= 0) continue;
                    if (Ore <= 0) break;
                    double ratio = m.Def.FuelProduce / m.Def.OreDraw;     // fuel produced per kg of ore
                    double oreUsed = Math.Min(Ore, m.Def.OreDraw * skill * dt);
                    double fuelOut = oreUsed * ratio;
                    double fuelRoom = compound ? FuelRoomAnywhere() : FuelRoomInSegment(i);
                    if (fuelRoom <= 0) continue;
                    if (fuelOut > fuelRoom) { fuelOut = fuelRoom; oreUsed = fuelOut / ratio; }
                    Ore -= oreUsed;
                    if (compound) AddFuelAnywhere(fuelOut); else AddFuelToSegment(i, fuelOut);
                }
        }

        /// <summary>Free liquid-fuel capacity (kg) across every tank on the vessel.</summary>
        private double FuelRoomAnywhere()
        {
            double r = 0;
            foreach (var p in AllParts())
                if (p.Def.Kind != PartKind.SolidBooster) r += Math.Max(0, p.Def.FuelCapacity - p.Fuel);
            return r;
        }

        /// <summary>Free liquid-fuel capacity (kg) in the stage segment containing the given part index.</summary>
        private double FuelRoomInSegment(int partIndex)
        {
            foreach (var seg in Segments())
            {
                if (partIndex < seg.start || partIndex > seg.end) continue;
                double r = 0;
                for (int i = seg.start; i <= seg.end; i++) r += Math.Max(0, Parts[i].Def.FuelCapacity - Parts[i].Fuel);
                return r;
            }
            return 0;
        }

        /// <summary>Comm signal strength in [0,1]: full within the strongest active antenna's range,
        /// falling to zero at twice that range. The link is to the home ground station (Earth) or to any
        /// other vessel carrying an active relay antenna (one hop, KSP-CommNet-lite). 0 with no antenna.</summary>
        public double SignalStrength(double ut, Universe u, IEnumerable<Vessel> others)
        {
            double best = 0;
            foreach (var p in AllParts())
                foreach (var m in p.Modules)
                    if (m.Def.Kind == ModuleKind.Antenna && m.Active && m.Def.Range > best) best = m.Def.Range;
            if (best <= 0) return 0;

            Vec2d me = AbsolutePosition(ut);
            double s = 0;

            // home ground station at Earth (effectively unlimited transmit power)
            var home = u?["Earth"];
            if (home != null)
                s = Math.Max(s, LinkStrength((me - home.AbsolutePositionAt(ut)).Length, best));

            // other vessels with an active relay antenna can rebroadcast (assumed connected onward)
            if (others != null)
                foreach (var o in others)
                {
                    if (o == null || o == this || s >= 1) continue;
                    double relay = 0;
                    foreach (var p in o.AllParts())
                        foreach (var m in p.Modules)
                            if (m.Def.Kind == ModuleKind.Antenna && m.Active && m.Def.Relay && m.Def.Range > relay) relay = m.Def.Range;
                    if (relay <= 0) continue;
                    double combined = Math.Sqrt(best * relay);   // both ends limit the link
                    s = Math.Max(s, LinkStrength((me - o.AbsolutePosition(ut)).Length, combined));
                }
            return Math.Clamp(s, 0, 1);
        }

        private static double LinkStrength(double dist, double range)
            => range <= 0 ? 0 : Math.Clamp(2.0 - dist / range, 0, 1);

        /// <summary>Sunlight intensity factor (inverse-square vs Earth's distance), clamped.</summary>
        private double SolarFactor(double ut, Universe u)
        {
            // In a star's system use that star; coasting in interstellar space (Body == barycenter) fall
            // back to the nearest star, so panels dim realistically with distance between the stars.
            var star = u?.StarOf(Body) ?? u?.NearestStar(AbsolutePosition(ut), ut);
            if (star == null) return 1;
            double d = (AbsolutePosition(ut) - star.AbsolutePositionAt(ut)).Length;
            if (d <= 1) return 4;
            double refD = u["Earth"]?.Orbit.A ?? d;   // a universal reference distance (1 AU equivalent) for all stars
            double f = (refD / d) * (refD / d);
            return Math.Clamp(f, 0.02, 4);
        }

        /// <summary>Add fuel to any tank on the vessel that has room (used by a connected surface base,
        /// where mined fuel pools across all docked modules).</summary>
        private void AddFuelAnywhere(double amount)
        {
            foreach (var p in AllParts())
            {
                if (amount <= 0) break;
                if (p.Def.Kind == PartKind.SolidBooster) continue;
                double room = p.Def.FuelCapacity - p.Fuel;
                if (room <= 0) continue;
                double add = Math.Min(room, amount);
                p.Fuel += add; amount -= add;
            }
        }

        /// <summary>Add fuel to the tanks in the stage segment that contains the given part index.</summary>
        private void AddFuelToSegment(int partIndex, double amount)
        {
            foreach (var seg in Segments())
            {
                if (partIndex < seg.start || partIndex > seg.end) continue;
                for (int i = seg.start; i <= seg.end && amount > 0; i++)
                {
                    double room = Parts[i].Def.FuelCapacity - Parts[i].Fuel;
                    if (room <= 0) continue;
                    double add = Math.Min(room, amount);
                    Parts[i].Fuel += add;
                    amount -= add;
                }
                return;
            }
        }

        /// <summary>Remove one living crew member (last-boarded first), mark them KIA on the shared
        /// roster instance, and record the name for the scene to toast.</summary>
        private void KillOneCrew()
        {
            var c = NextLifeSupportVictim();
            if (c == null) return;
            foreach (var p in AllParts())
                if (p.Crew.Remove(c)) break;
            c.Status = CrewStatus.KIA;
            RecentDeaths.Add(c.Name);
        }

        /// <summary>The crew member who will die next from life-support deprivation (last-boarded crew of
        /// the first occupied part — the same pick <see cref="KillOneCrew"/> makes). Null if none aboard.
        /// Lets the HUD put the "time to death" countdown on the right person.</summary>
        public CrewMember NextLifeSupportVictim()
        {
            foreach (var p in AllParts())
                if (p.Crew.Count > 0) return p.Crew[p.Crew.Count - 1];
            return null;
        }

        /// <summary>Seconds until the next life-support death, or +inf while life support is OK. Each empty
        /// resource kills one crew every <see cref="LsDeathTime"/>; this is the time left on that cadence.</summary>
        public double LifeSupportDeathEta =>
            LifeSupportOk ? double.PositiveInfinity : Math.Max(0, LsDeathTime - _lsDeprivedFor);

        /// <summary>Kill a specific crew member (e.g. from radiation or illness): remove them from
        /// whichever part holds them, mark them KIA on the shared roster instance, and record the name
        /// for the scene to toast. No-op if they aren't aboard.</summary>
        public void KillCrewMember(CrewMember c)
        {
            if (c == null) return;
            foreach (var p in AllParts())
                if (p.Crew.Remove(c))
                {
                    c.Status = CrewStatus.KIA;
                    RecentDeaths.Add(c.Name);
                    return;
                }
        }

        /// <summary>Move a crew member to another part on this vessel that has a free seat. Returns false
        /// if the source has no crew or the destination is full.</summary>
        public bool TransferCrew(Part from, Part to)
        {
            if (from == null || to == null || from == to || from.Crew.Count == 0) return false;
            if (to.Crew.Count >= to.SeatCount) return false;
            var c = from.Crew[from.Crew.Count - 1];
            from.Crew.RemoveAt(from.Crew.Count - 1);
            to.Crew.Add(c);
            return true;
        }

        /// <summary>Move a specific crew member into <paramref name="to"/>, anywhere on this vessel (including
        /// docked modules, since they share one part list). Returns false if the target is full, is the member's
        /// current part, or the member is not aboard.</summary>
        public bool TransferCrew(CrewMember c, Part to)
        {
            if (c == null || to == null || to.Crew.Count >= to.SeatCount) return false;
            foreach (var from in AllParts())
            {
                if (!from.Crew.Contains(c)) continue;
                if (from == to) return false;
                from.Crew.Remove(c);
                to.Crew.Add(c);
                return true;
            }
            return false;
        }

        public void GoOnRails(double ut)
        {
            Orbit = Kepler.ElementsFromState(Position, Velocity, Body.Mu, ut);
            OnRails = true;
        }

        public void GoOffRails(double ut)
        {
            (Position, Velocity) = Kepler.StateAtTime(Orbit, ut);
            OnRails = false;
        }

        public void UpdateFromRails(double ut) => (Position, Velocity) = Kepler.StateAtTime(Orbit, ut);

        public Vec2d AbsolutePosition(double ut) => Body.AbsolutePositionAt(ut) + Position;

        public Vec2d AbsoluteVelocity(double ut) => Body.AbsoluteVelocityAt(ut) + Velocity;

        /// <summary>Elements for display: live elements when on rails, computed on the fly otherwise.</summary>
        public OrbitalElements CurrentElements(double ut) =>
            OnRails ? Orbit : Kepler.ElementsFromState(Position, Velocity, Body.Mu, ut);

        // ----- docking / compound assembly -----

        /// <summary>A join between two docking-port parts that were merged into this vessel. The docked
        /// module is the contiguous run of parts starting at <see cref="ModuleStart"/>, so it can be
        /// split back off on undock.</summary>
        public sealed class DockLink
        {
            public Part PortA;       // port that belongs to the part of the stack we keep
            public Part PortB;       // port on the docked-on module
            public int ModuleStart;  // index in Parts where the docked module begins
            // Pose of the docked module's sub-stack in THIS vessel's local frame:
            // vesselLocal = RotQuarter(QuarterTurns, subStackLocal) + Offset.
            public int QuarterTurns; // 0..3 (CCW 90-degree steps)
            public Vec2d Offset;     // meters, in the vessel local frame (base at origin, +Y up the stack)
        }

        /// <summary>Joins recorded when other vessels docked onto this one (newest last).</summary>
        public readonly List<DockLink> DockLinks = new();

        /// <summary>Rotate a local vector by <paramref name="q"/> quarter-turns CCW (Perp = +90 deg).</summary>
        public static Vec2d RotQuarter(int q, Vec2d v)
        {
            q &= 3;
            for (int i = 0; i < q; i++) v = v.Perp();
            return v;
        }

        /// <summary>World-space unit vector pointing right of the stack (perpendicular to <see cref="Up"/>),
        /// matching the renderer's screen-space <c>rightS</c> convention.</summary>
        public Vec2d Right { get { var u = Up; return new Vec2d(u.Y, -u.X); } }

        /// <summary>The contiguous part ranges that make up this vessel: the root stack [0, firstModuleStart),
        /// then each docked module [ModuleStart, nextModuleStart). The link is null for the root stack.</summary>
        public IEnumerable<(int start, int end, DockLink link)> SubStacks()
        {
            int firstModule = Parts.Count;
            foreach (var dl in DockLinks) if (dl.ModuleStart < firstModule) firstModule = dl.ModuleStart;
            yield return (0, firstModule, null);
            var links = new List<DockLink>(DockLinks);
            links.Sort((a, b) => a.ModuleStart.CompareTo(b.ModuleStart));
            for (int k = 0; k < links.Count; k++)
            {
                int start = links[k].ModuleStart;
                int end = (k + 1 < links.Count) ? links[k + 1].ModuleStart : Parts.Count;
                yield return (start, end, links[k]);
            }
        }

        /// <summary>Center of an axial part in this vessel's local frame, honoring any docked-module
        /// transform. Parts stack from the sub-stack base (highest index, local y=0) toward its nose.</summary>
        public Vec2d PartLocalCenter(Part p)
        {
            int idx = Parts.IndexOf(p);
            if (idx < 0) return Vec2d.Zero;
            foreach (var (start, end, link) in SubStacks())
            {
                if (idx < start || idx >= end) continue;
                double y = 0;
                for (int i = end - 1; i > idx; i--) y += Parts[i].Def.Height;
                y += Parts[idx].Def.Height * 0.5;
                Vec2d c = new Vec2d(0, y);
                if (link != null) c = RotQuarter(link.QuarterTurns, c) + link.Offset;
                return c;
            }
            return Vec2d.Zero;
        }

        /// <summary>World-space center of a docking port (used for port-to-port proximity / overlap).</summary>
        public Vec2d PortWorldCenter(Part p, double ut)
        {
            Vec2d c = PartLocalCenter(p);
            return AbsolutePosition(ut) + Right * c.X + Up * c.Y;
        }

        /// <summary>The nearest pair of free docking ports between two vessels, by world-space center
        /// distance. Returns (null, null, +inf) if either vessel has no free port.</summary>
        public static (Part mine, Part theirs, double dist) ClosestFreePortPair(Vessel a, Vessel b, double ut)
        {
            Part bestA = null, bestB = null; double best = double.MaxValue;
            foreach (var pa in a.FreeDockingPorts())
            {
                Vec2d wa = a.PortWorldCenter(pa, ut);
                foreach (var pb in b.FreeDockingPorts())
                {
                    double d = (wa - b.PortWorldCenter(pb, ut)).Length;
                    if (d < best) { best = d; bestA = pa; bestB = pb; }
                }
            }
            return (bestA, bestB, best);
        }

        private bool PortOccupied(Part port)
        {
            foreach (var dl in DockLinks) if (dl.PortA == port || dl.PortB == port) return true;
            return false;
        }

        /// <summary>Docking ports not already joined to another vessel.</summary>
        public IEnumerable<Part> FreeDockingPorts()
        {
            foreach (var p in AllParts())
                if (p.Def.Kind == PartKind.DockingPort && !PortOccupied(p)) yield return p;
        }

        public Part FirstFreeDockingPort()
        {
            foreach (var p in FreeDockingPorts()) return p;
            return null;
        }

        public bool HasFreeDockingPort => FirstFreeDockingPort() != null;

        /// <summary>Dock <paramref name="other"/> onto this vessel: append its parts as a new module,
        /// record the join, and pool the shared resources. The caller is responsible for the rigid-body
        /// snap (position/velocity). Returns false if either port is missing.</summary>
        public bool DockWith(Vessel other, Part myPort, Part theirPort, int quarterTurns, Vec2d offset)
        {
            if (other == null || myPort == null || theirPort == null) return false;
            quarterTurns &= 3;
            int moduleStart = Parts.Count;
            foreach (var p in other.Parts) Parts.Add(p);
            DockLinks.Add(new DockLink { PortA = myPort, PortB = theirPort, ModuleStart = moduleStart, QuarterTurns = quarterTurns, Offset = offset });
            // carry over the other vessel's own joins, shifted into this stack's index space and composing
            // their module pose with the new attach transform so they land in THIS vessel's local frame
            foreach (var dl in other.DockLinks)
                DockLinks.Add(new DockLink
                {
                    PortA = dl.PortA,
                    PortB = dl.PortB,
                    ModuleStart = dl.ModuleStart + moduleStart,
                    QuarterTurns = (quarterTurns + dl.QuarterTurns) & 3,
                    Offset = RotQuarter(quarterTurns, dl.Offset) + offset,
                });
            other.DockLinks.Clear();
            // pool shared resources (capacities recompute from the merged parts automatically)
            ElectricCharge += other.ElectricCharge;
            Monoprop += other.Monoprop;
            Ore += other.Ore;
            Oxygen += other.Oxygen; Water += other.Water; Food += other.Food;
            return true;
        }

        /// <summary>Whether the most-recently docked module can be cleanly detached (it is always the
        /// outermost leaf, so this is true whenever any join exists).</summary>
        public bool CanUndock => DockLinks.Count > 0;

        /// <summary>Detach the most-recently docked module into a new standalone vessel, splitting the
        /// pooled resources by capacity. Returns the detached vessel (already off-rails), or null.</summary>
        public Vessel Undock(double sepImpulse = 0.4)
        {
            if (DockLinks.Count == 0) return null;
            // Detach the outermost leaf module: the link with the greatest ModuleStart. Its parts are a
            // clean suffix of the stack, so no other join references them and no sub-links travel with it.
            int li = 0;
            for (int i = 1; i < DockLinks.Count; i++) if (DockLinks[i].ModuleStart > DockLinks[li].ModuleStart) li = i;
            var link = DockLinks[li];
            DockLinks.RemoveAt(li);

            // reconstruct the detached vessel's pose from the stored module transform (so it pops off
            // exactly where it was rendered): its base is at the link Offset, rotated by the quarter-turns
            var detached = new Vessel
            {
                Body = Body,
                Position = Position + Right * link.Offset.X + Up * link.Offset.Y,
                Velocity = Velocity,
                Heading = Heading + link.QuarterTurns * (Math.PI / 2),
                OnRails = false,
            };
            for (int i = Parts.Count - 1; i >= link.ModuleStart; i--)
            {
                detached.Parts.Insert(0, Parts[i]);
                Parts.RemoveAt(i);
            }

            // split pooled resources by the two halves' capacities
            SplitPool(ref ElectricCharge, detached, (v) => v.EcCapacity, x => detached.ElectricCharge = x);
            SplitPool(ref Monoprop, detached, (v) => v.MonopropCapacity, x => detached.Monoprop = x);
            SplitPool(ref Ore, detached, (v) => v.OreCapacity, x => detached.Ore = x);
            SplitPool(ref Oxygen, detached, (v) => v.OxygenCapacity, x => detached.Oxygen = x);
            SplitPool(ref Water, detached, (v) => v.WaterCapacity, x => detached.Water = x);
            SplitPool(ref Food, detached, (v) => v.FoodCapacity, x => detached.Food = x);

            // small separation push along the line from this vessel's base to the detached module
            Vec2d dir = (detached.Position - Position).Length > 1e-6 ? (detached.Position - Position).Normalized() : Up;
            detached.Velocity += dir * sepImpulse;
            Velocity -= dir * sepImpulse;
            return detached;
        }

        /// <summary>Move the share of a pooled resource that belongs to <paramref name="detached"/>
        /// (proportional to its capacity) out of this vessel's pool and into it.</summary>
        private void SplitPool(ref double pool, Vessel detached, Func<Vessel, double> cap, Action<double> setOther)
        {
            double mine = cap(this), theirs = cap(detached), total = mine + theirs;
            if (total <= 0) { setOther(0); return; }
            double give = pool * (theirs / total);
            setOther(give);
            pool -= give;
        }
    }
}


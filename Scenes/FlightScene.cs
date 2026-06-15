using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Solar.Core;
using Solar.Physics;
using Solar.Rendering;
using Solar.UI;
using Solar.Vessels;

namespace Solar.Scenes
{
    /// <summary>
    /// Flight: numeric integration under thrust/atmosphere, analytic on-rails coasting with
    /// exact-time SOI handoffs, time warp, debris, and a KSP-style toggleable map view.
    /// </summary>
    public sealed class FlightScene : Scene
    {
        private const double SubstepDt = 1.0 / 120;
        private const int MaxDebris = 20;
        private const float HandleDist = 52f;   // px from node to a delta-v handle
        private const float HandleHit = 12f;    // px hit radius for handles
        private const float NodeHit = 12f;      // px hit radius for the node body

        private Vessel _vessel;
        private string _shipName = "Ship";
        private readonly ShipState _resume;   // non-null when resuming a saved ship
        private readonly List<Vessel> _debris = new();
        private readonly List<TrackedShip> _others = new();  // other saved ships, co-orbiting passively

        /// <summary>A named, passive co-orbiting ship (drawn + targetable, never simulated under physics).</summary>
        private sealed class TrackedShip { public string Name; public Vessel V; }
        private readonly Camera2D _cam = new();
        private readonly DustField _dust = new();

        private bool _map;
        private bool _slopeView;             // terrain colored by slope (landing-spot finder) instead of elevation

        // map-view ship popup (click a ship -> Switch / Set as target)
        private Vessel _menuVessel;
        private string _menuName;
        private bool _menuControllable;
        private Vector2 _menuPos;
        private double _flightZoom = 0.35;   // m per pixel
        private double _mapZoom;
        private int _focus;                  // 0 = vessel, otherwise body index + 1

        // ----- target / rendezvous -----
        private CelestialBody _targetBody;   // one of _targetBody / _targetVessel is non-null when targeting
        private Vessel _targetVessel;
        private string _targetName;
        private bool _showTargetWindow;
        private bool _showCrew;              // in-flight crew roster / transfer panel
        private bool _showColony;            // colony / surface-base management panel (landed only)
        private bool _showAddModule;         // colony "add module" sub-list
        private bool _buildAtColony;         // deferred: open the editor to build a vessel at this base
        private bool _caValid;               // closest-approach solution for this frame
        private double _caUT, _caSep, _caRelSpeed;
        private List<ProximityPoint> _prox = new();  // geometric orbit-curve proximities (throttled)
        private double _proxTimer;

        // ----- attitude hold (SAS) -----
        private enum SasMode { Off, Stability, Prograde, Retrograde, RadialIn, RadialOut, Target, AntiTarget, RelRetro, Maneuver }
        private const int SasModeCount = 10;
        private SasMode _sas = SasMode.Off;
        private double _sasHold;            // captured world heading for Stability mode

        private Prediction _pred;

        private readonly List<Maneuver> _nodes = new();   // chained planned burns (KSP-style), sorted by UT
        private int _dragHandle = -1;       // -1 none; 0/1 prograde +/-; 2/3 radial out/in; 4 = move node
        private int _dragNode = -1;         // index into _nodes being dragged
        private int _hoverHandle = -1;
        private int _hoverNode = -1;        // node under the cursor (handle or body)
        private int _hoverX = -1;           // node whose X delete button is hovered
        private double _dragStartProj, _dragStartValue;
        private double _burnSpent;          // delta-v consumed against the current (nearest future) node
        private double _burnTargetUT = double.NaN;  // UT of the node _burnSpent is keyed to
        private double? _warpTo;            // target UT for an active "warp to maneuver"
        private string _endMessage;
        private string _toast;               // transient milestone notification
        private double _toastT;              // seconds remaining on the toast
        private double _anim;
        private Vec2d _lastCamRel;           // camera position relative to body (kept after a crash)
        private CelestialBody _lastBody;
        private bool _showExitDialog;        // pause / exit-confirmation modal (freezes the sim)
        private bool _exitToTitle;           // deferred: leave to the Title screen on the next Update
        private bool _exitToHub;             // deferred: return to the Space Center hub on the next Update
        private bool _cancelMission;         // deferred: scrap a pre-launch ship and reopen the editor

        public FlightScene(GameContext ctx) : base(ctx) { }

        /// <summary>Resume a previously saved ship (from the tracking station) rather than a fresh launch.</summary>
        public FlightScene(GameContext ctx, ShipState resume) : base(ctx) { _resume = resume; }

        public override void Enter()
        {
            Ctx.Clock.DropToRealtime();
            _endMessage = null;

            if (_resume != null)
            {
                _vessel = _resume.ToVessel(Ctx.Universe, Ctx.State.Roster);
                _shipName = _resume.Name;
                LoadNodes(_resume);
                _burnSpent = 0;
                var b = _vessel.Body ?? Ctx.Universe["Earth"];
                _vessel.Body = b;
                // a colony kept producing while we were away: catch its tanks up to now
                Colony.AdvanceProduction(_vessel, _resume.LastUT, Ctx.Clock.UT, Ctx.Universe);
                if (_vessel.OnRails) _vessel.UpdateFromRails(Ctx.Clock.UT);
                _lastBody = b;
                _lastCamRel = _vessel.Position;
                _mapZoom = b.SoiRadius * 2.4 / Math.Min(Ctx.W, Ctx.H);
                if (!_vessel.Landed && !_vessel.Destroyed) RefreshPrediction(Ctx.Clock.UT);
                SpawnOthers();
                RestoreTarget(_resume.TargetName);
                return;
            }

            _vessel = Ctx.Design.Instantiate(Ctx.State.Roster);
            // a duplicate of an existing ship's name gets a progressive suffix so both persist
            _shipName = Ctx.State.UniqueShipName(Ctx.Design.Name);

            CelestialBody site;
            if (Ctx.PendingLaunchSite is LaunchSite ls && Ctx.Universe[ls.BodyName] != null)
            {
                // built at a colony: spawn landed a few metres beside the base, at rest, ready to surface-dock
                site = Ctx.Universe[ls.BodyName];
                _vessel.Body = site;
                _vessel.Position = ls.Position;
                _vessel.Heading = ls.Heading;
                _vessel.Throttle = 0;
                Ctx.PendingLaunchSite = null;
            }
            else
            {
                site = Ctx.Universe["Earth"];
                _vessel.Body = site;
                // spawn on the surface (a flat pad plain sits here), never buried in terrain relief
                double a = SolarSystemData.LaunchPadAngle;
                _vessel.Position = Vec2d.FromAngle(a, site.SurfaceRadiusAt(a));
                _vessel.Heading = a;   // radial-up on the (flat) pad
                _vessel.Throttle = 1.0;   // pad launch starts at full throttle so firing the first stage lifts off
            }
            _vessel.Velocity = Vec2d.Zero;
            _vessel.Landed = true;
            _vessel.OnRails = false;
            _lastBody = site;
            _mapZoom = site.SoiRadius * 2.4 / Math.Min(Ctx.W, Ctx.H);
            SpawnOthers();
        }

        /// <summary>Load every other saved ship as a passive, always-on-rails co-orbiting target.</summary>
        private void SpawnOthers()
        {
            _others.Clear();
            foreach (var s in Ctx.State.Ships)
            {
                if (s.Name == _shipName || s.Destroyed) continue;
                var v = s.ToVessel(Ctx.Universe, Ctx.State.Roster);
                if (v.Body == null) continue;
                Colony.AdvanceProduction(v, s.LastUT, Ctx.Clock.UT, Ctx.Universe);
                // skip a ship sitting on the *same spot* as the active vessel (e.g. another ship saved landed
                // on the launch pad): it would render right on top of ours and read as stray parts. Kept tight
                // (true co-location) so a craft built a few metres beside a base still shows and can connect.
                // It's still persisted by PersistOthers, so this only suppresses the overlapping spawn/render.
                if (_vessel != null && v.Body == _vessel.Body && v.Landed && _vessel.Landed
                    && (v.Position - _vessel.Position).Length < 8.0) continue;
                if (!v.Landed && !v.OnRails) v.GoOnRails(Ctx.Clock.UT);
                _others.Add(new TrackedShip { Name = s.Name, V = v });
            }
        }

        /// <summary>Snapshot the active ship into the savegame (dropping it if destroyed).</summary>
        private void PersistShip()
        {
            if (_vessel == null) return;
            Ctx.State.UT = Ctx.Clock.UT;
            Ctx.State.UpsertShip(ShipState.From(_vessel, _shipName, _nodes, _targetName, Ctx.Clock.UT));
        }

        /// <summary>Snapshot every tracked co-orbiting ship back into the savegame (so undocked modules
        /// and stations the player isn't currently flying are saved on exit too).</summary>
        private void PersistOthers()
        {
            foreach (var ts in _others)
            {
                var v = ts.V;
                if (v.Destroyed) { Ctx.State.RemoveShip(ts.Name); continue; }
                if (!v.Landed && !v.OnRails) v.GoOnRails(Ctx.Clock.UT);
                Ctx.State.UpsertShip(ShipState.From(v, ts.Name, ut: Ctx.Clock.UT));
            }
        }

        /// <summary>Restore planned nodes from a saved ship (supports the legacy single-node field).</summary>
        private void LoadNodes(ShipState s)
        {
            _nodes.Clear();
            if (s.Nodes != null)
                foreach (var n in s.Nodes)
                {
                    var m = n?.ToManeuver();
                    if (m == null) continue;
                    if (!string.IsNullOrEmpty(n.BodyName)) m.Body = Ctx.Universe[n.BodyName];
                    _nodes.Add(m);
                }
            else if (s.Node != null)
                _nodes.Add(s.Node.ToManeuver());
        }

        /// <summary>The nearest node that has not yet passed (the current burn target), or null.</summary>
        private Maneuver NextNode(double ut)
        {
            Maneuver best = null;
            foreach (var n in _nodes)
                if (n.UT >= ut && (best == null || n.UT < best.UT)) best = n;
            return best;
        }

        /// <summary>Nodes sorted by time (a fresh list; safe to iterate while mutating _nodes is avoided).</summary>
        private List<Maneuver> Sorted(double ut)
        {
            var list = new List<Maneuver>(_nodes);
            list.Sort((a, b) => a.UT.CompareTo(b.UT));
            return list;
        }

        /// <summary>One drawn/clickable conic of the planned trajectory, in its own primary's frame.</summary>
        private readonly struct ProjSegment
        {
            public ProjSegment(in OrbitalElements el, CelestialBody body, Vec2d primaryAbs,
                               double startUT, double endUT, bool closed, bool planned)
            { El = el; Body = body; PrimaryAbs = primaryAbs; StartUT = startUT; EndUT = endUT; Closed = closed; Planned = planned; }
            public readonly OrbitalElements El;
            public readonly CelestialBody Body;
            public readonly Vec2d PrimaryAbs;   // body's absolute position snapshot at StartUT
            public readonly double StartUT;
            public readonly double EndUT;        // transition / node time, or +inf if the segment is a full orbit
            public readonly bool Closed;         // a full ellipse (no transition ends it)
            public readonly bool Planned;        // lies after at least one burn (drawn as the orange plan)
        }

        /// <summary>Walk the planned trajectory through SOI transitions and pending-node burns (KSP-style
        /// patched conics), producing the ordered conic segments that are both drawn and click-hit-tested.
        /// When <paramref name="refreshNodes"/> is set, each future node's Source/Body frame is assigned
        /// here (skipped during a burn so the planned orbit stays put).</summary>
        private List<ProjSegment> BuildProjection(double ut, bool refreshNodes)
        {
            var segs = new List<ProjSegment>();
            if (!LiveOrbit(ut, out var live, out var liveBody, out _)) return segs;

            var future = new List<Maneuver>();
            foreach (var n in Sorted(ut)) if (n.UT >= ut) future.Add(n);

            OrbitalElements src = live;
            CelestialBody body = liveBody;
            double segStart = ut;
            double bodyRefUT = ut;   // time the current body's SOI was entered: the conic's draw origin
            bool planned = false;    // becomes true once a node burn has been applied
            int ni = 0, guard = 0;
            while (guard++ < 16)
            {
                Maneuver node = ni < future.Count ? future[ni] : null;
                var pred = TrajectoryPredictor.Predict(src, body, segStart);
                double transUT = (pred.Type != TransitionType.None && pred.NextBody != null)
                    ? pred.TransitionUT : double.PositiveInfinity;

                if (node != null && node.UT <= transUT)
                {
                    // a burn happens before the next SOI change: the orbit shape holds until the node
                    segs.Add(new ProjSegment(src, body, body.AbsolutePositionAt(bodyRefUT), segStart, node.UT, false, planned));
                    if (refreshNodes) { node.Source = src; node.Body = body; node.HasSource = true; node.FrameUT = bodyRefUT; }
                    // While this node is being burned, project its *remaining* delta-v so the planned
                    // orbit holds steady as the live orbit rises to meet it, instead of drifting.
                    OrbitalElements res;
                    bool activeBurn = node.UT == _burnTargetUT && _burnSpent > 0 && node.DeltaV > 0
                                      && _vessel != null && !_vessel.OnRails && _vessel.CurrentThrust > 0
                                      && segStart == ut && body == liveBody;
                    if (activeBurn && _vessel.CurrentMassFlow > 1e-9)
                    {
                        // finite-burn projection of the node's remaining delta-v from the live state
                        double remaining = Math.Max(0, node.DeltaV - _burnSpent);
                        double flow = _vessel.CurrentMassFlow;
                        var captured = node;
                        res = BurnProjector.Project(
                            _vessel.Position, _vessel.Velocity, body.Mu, ut,
                            _vessel.CurrentThrust, flow, _vessel.TotalMass,
                            _vessel.ActiveBurnFuel / flow, remaining,
                            (r, vv) => captured.BurnDelta(r, vv).Normalized(), out _);
                    }
                    else
                    {
                        var burnNode = node;
                        if (node.UT == _burnTargetUT && _burnSpent > 0 && node.DeltaV > 0)
                            burnNode = node.Scaled(Math.Clamp((node.DeltaV - _burnSpent) / node.DeltaV, 0, 1));
                        res = burnNode.ResultOrbit(src, body.Mu);
                    }
                    ni++;
                    if (double.IsNaN(res.A)) break;
                    src = res; segStart = node.UT; planned = true;   // same body: bodyRefUT unchanged
                    continue;
                }

                bool hasTrans = !double.IsInfinity(transUT);
                segs.Add(new ProjSegment(src, body, body.AbsolutePositionAt(bodyRefUT), segStart,
                                         hasTrans ? transUT : double.PositiveInfinity, !hasTrans, planned));
                if (!hasTrans) break;
                src = pred.NextOrbit; body = pred.NextBody; segStart = pred.TransitionUT; bodyRefUT = pred.TransitionUT;
            }
            return segs;
        }

        /// <summary>Re-chain every pending node across SOI transitions so each is planned against the
        /// trajectory reaching it. Past nodes keep their last (frozen) source.</summary>
        private void RefreshNodeChain(double ut) => BuildProjection(ut, refreshNodes: true);

        public override void Update(double dt)
        {
            double realDt = Math.Min(dt, 0.1);
            _anim += realDt;
            var inp = Ctx.Input;
            var clock = Ctx.Clock;

            // ---------- input ----------
            // Escape opens a pause/exit modal; the sim is frozen while it's up (handled in Draw).
            // Scene switches are deferred out of Draw (where the buttons are evaluated) to here.
            if (_exitToTitle) { PersistShip(); PersistOthers(); Ctx.Scenes.SwitchTo(new TitleScene(Ctx)); return; }
            if (_exitToHub) { PersistShip(); PersistOthers(); Ctx.Scenes.SwitchTo(new HubScene(Ctx)); return; }
            if (_cancelMission)
            {
                Ctx.State.Ships.RemoveAll(s => s.Name == _shipName);   // discard the flight instance
                Ctx.Design = VesselDesign.FromVessel(_vessel, _shipName);
                Ctx.Scenes.SwitchTo(new EditorScene(Ctx));
                return;
            }
            if (inp.Pressed(Keys.Escape)) _showExitDialog = !_showExitDialog;
            if (_showExitDialog) return;
            if (inp.Pressed(Keys.M)) _map = !_map;
            if (inp.Pressed(Keys.N)) _slopeView = !_slopeView;   // toggle the slope (landing-spot) overlay
            if (inp.Pressed(Keys.Tab)) CycleTarget(inp.Down(Keys.LeftShift) || inp.Down(Keys.RightShift) ? -1 : 1);
            if (inp.Pressed(Keys.T)) _showTargetWindow = !_showTargetWindow;
            if (inp.Pressed(Keys.V)) TakeControlOfTarget();
            if (inp.Pressed(Keys.U)) Undock();
            if (inp.Pressed(Keys.K)) ConnectSurface(clock.UT);   // confirm a surface connection into a base
            if (inp.Pressed(Keys.C)) _showCrew = !_showCrew;
            // colony / base management, available while landed
            if (inp.Pressed(Keys.B) && _vessel != null && _vessel.Landed && !_vessel.Destroyed)
            { _showColony = !_showColony; if (!_showColony) _showAddModule = false; }
            if (!(_vessel != null && _vessel.Landed)) { _showColony = false; _showAddModule = false; }
            if (_buildAtColony) { _buildAtColony = false; BuildAtColony(); return; }
            if (_map && inp.Pressed(Keys.F)) _focus = (_focus + 1) % (Ctx.Universe.Bodies.Count + 1);

            // map-view ship popup is handled first so its clicks don't fall through to node editing
            bool menuConsumed = UpdateShipMenu(clock.UT);
            bool maneuverWheel = false;
            UpdateManeuverInput(clock.UT, menuConsumed, out maneuverWheel);

            if (inp.WheelDelta != 0 && !maneuverWheel)
            {
                double factor = Math.Pow(1.18, -inp.WheelDelta / 120.0);
                if (_map) _mapZoom = Math.Clamp(_mapZoom * factor, 50, 5e8);
                else _flightZoom = Math.Clamp(_flightZoom * factor, 0.02, 400);
            }
            if (inp.Pressed(Keys.OemComma)) { clock.WarpDown(); _warpTo = null; }
            if (inp.Pressed(Keys.OemPeriod)) { clock.WarpUp(); _warpTo = null; }
            if (NextNode(clock.UT) == null) _warpTo = null; // no pending node -> nothing to warp to

            bool alive = _vessel != null && !_vessel.Destroyed;
            if (alive)
            {
                if (inp.Down(Keys.LeftShift) || inp.Down(Keys.RightShift)) _vessel.Throttle = Math.Min(1, _vessel.Throttle + 0.7 * realDt);
                if (inp.Down(Keys.LeftControl) || inp.Down(Keys.RightControl)) _vessel.Throttle = Math.Max(0, _vessel.Throttle - 0.7 * realDt);
                if (inp.Pressed(Keys.Z)) _vessel.Throttle = 1;
                if (inp.Pressed(Keys.X)) _vessel.Throttle = 0;
                if (inp.Pressed(Keys.G)) ToggleSolar();
                if (inp.Pressed(Keys.L)) ToggleGear();
                if (inp.Pressed(Keys.H)) CycleSas();
                if (inp.Pressed(Keys.R)) _vessel.RcsEnabled = !_vessel.RcsEnabled;
                _vessel.RcsCommand = Vec2d.Zero;
                if (_vessel.Landed)
                {
                    _vessel.AngularVelocity = 0;             // no spinning while sitting on the surface
                }
                else
                {
                    // torque/inertia attitude model: input accelerates the angular velocity (capped),
                    // which then carries as momentum in vacuum and is bled off by drag in atmosphere.
                    var rotBody = _vessel.Body;
                    bool rotInAtmo = rotBody?.Atmo != null && _vessel.Altitude < rotBody.Atmo.Top + 500;
                    double alpha = _vessel.ControlTorque / _vessel.MomentOfInertia;   // available angular accel (rad/s^2)
                    double maxRate = _vessel.MaxTurnRate;
                    bool left = inp.Down(Keys.A) || inp.Down(Keys.Left);
                    bool right = inp.Down(Keys.D) || inp.Down(Keys.Right);

                    if (left ^ right)
                    {
                        double dir = left ? 1 : -1;          // +CCW
                        _vessel.AngularVelocity = Math.Clamp(_vessel.AngularVelocity + dir * alpha * realDt, -maxRate, maxRate);
                        _sas = SasMode.Off;                  // manual input releases the hold
                    }
                    else if (_sas != SasMode.Off && !_vessel.SasAvailable)
                    {
                        _sas = SasMode.Off;                  // power lost (or SAS part gone): drop the hold
                    }
                    else if (_sas != SasMode.Off && SasHoldAngle(clock.UT) is double hold)
                    {
                        // steer the angular velocity toward the hold angle with the available torque
                        double diff = Kepler.WrapPi(hold - _vessel.Heading);
                        double desired = Math.Clamp(diff * 3.0, -maxRate, maxRate);
                        double dv = Math.Clamp(desired - _vessel.AngularVelocity, -alpha * realDt, alpha * realDt);
                        _vessel.AngularVelocity += dv;
                    }
                    else if (rotInAtmo)
                    {
                        // aerodynamic damping bleeds rotation toward zero (stronger with dynamic pressure)
                        double damp = 0.6 + _vessel.DynamicPressure / 2000.0;
                        _vessel.AngularVelocity *= Math.Max(0, 1 - damp * realDt);
                    }
                    // else: vacuum coast — angular velocity persists as momentum

                    // advance heading from angular velocity (real-time paced); high time warp freezes rotation
                    if (clock.WarpIndex <= SimClock.PhysicsMaxIndex)
                        _vessel.Heading += _vessel.AngularVelocity * realDt;
                    else
                        _vessel.AngularVelocity = 0;

                    // RCS translation: I/K fore-aft (along Up axis), J/L left-right
                    double rx = 0, ry = 0;
                    if (inp.Down(Keys.I)) ry += 1;
                    if (inp.Down(Keys.K)) ry -= 1;
                    if (inp.Down(Keys.L)) rx += 1;
                    if (inp.Down(Keys.J)) rx -= 1;
                    _vessel.RcsCommand = new Vec2d(rx, ry);
                }
                if (inp.Pressed(Keys.Space)) FireNextStage();
            }

            // ---------- simulation ----------
            if (alive)
            {
                var body = _vessel.Body;
                bool inAtmo = body.Atmo != null && _vessel.Altitude < body.Atmo.Top + 500;
                bool thrusting = _vessel.CurrentThrust > 0;
                bool needsPhysics = !_vessel.Landed && (thrusting || inAtmo || _vessel.RcsActive);

                clock.MaxWarpIndex = _vessel.Landed
                    ? (thrusting ? SimClock.PhysicsMaxIndex : SimClock.Levels.Length - 1)
                    : needsPhysics ? SimClock.PhysicsMaxIndex : SimClock.Levels.Length - 1;
                // a tracked ship in close proximity is simulated under physics: cap warp so it stays exact
                if (AnyOtherNear()) clock.MaxWarpIndex = Math.Min(clock.MaxWarpIndex, SimClock.PhysicsMaxIndex);

                // "warp to maneuver": force max warp while coasting toward the target, else cancel
                if (_warpTo.HasValue)
                {
                    if (_vessel.Landed || needsPhysics || _warpTo.Value <= clock.UT) _warpTo = null;
                    else clock.WarpIndex = SimClock.Levels.Length - 1;
                }

                double sdt = realDt * clock.Warp;

                if (_vessel.Landed)
                {
                    clock.UT += sdt;
                    if (thrusting)
                    {
                        _vessel.DrainFuel(sdt);
                        double g = body.Mu / (body.Radius * body.Radius);
                        if (_vessel.CurrentThrust > _vessel.TotalMass * g)
                        {
                            _vessel.Landed = false;
                            _vessel.OnRails = false;
                            _endMessage = null;
                        }
                    }
                }
                else if (needsPhysics)
                {
                    if (_vessel.OnRails) _vessel.GoOffRails(clock.UT);
                    int steps = Math.Clamp((int)Math.Ceiling(sdt / SubstepDt), 1, 64);
                    double h = sdt / steps;
                    for (int s = 0; s < steps; s++)
                    {
                        // the speed entering the step is the true approach speed at the surface; reading
                        // velocity after a step that dips below ground would include gravity gained over
                        // that substep, inflating the impact figure past the survivable threshold.
                        double approach = _vessel.Velocity.Length;
                        Integrator.Step(_vessel, h);
                        clock.UT += h;
                        CheckSoiOffRails(clock.UT);
                        if (CheckSurface(approach)) break;
                    }
                    // Recompute every physics frame so the drawn conic tracks the live state instead of
                    // lagging and snapping (most visible mid-burn / at the 4x physics-warp cap).
                    if (!_vessel.Destroyed && !_vessel.Landed)
                        _pred = TrajectoryPredictor.Predict(_vessel.CurrentElements(clock.UT), _vessel.Body, clock.UT);
                }
                else // coasting in vacuum: on rails
                {
                    if (!_vessel.OnRails)
                    {
                        _vessel.GoOnRails(clock.UT);
                        RefreshPrediction(clock.UT);
                    }
                    double target = clock.UT + sdt;
                    // Stop a warp just before an upcoming maneuver node so it never sails past it.
                    // The burn is centred on the node, so the latest safe stop is when it should start.
                    double nodeStop = double.PositiveInfinity;
                    var stopNode = NextNode(clock.UT);
                    if (stopNode != null && _burnSpent == 0)
                    {
                        double bt = Staging.BurnTime(_vessel, stopNode.DeltaV);
                        nodeStop = stopNode.UT - (bt > 0 ? bt / 2 : 0);
                    }
                    if (_pred != null && _pred.Type != TransitionType.None && _pred.TransitionUT <= target
                        && _pred.TransitionUT <= nodeStop)
                    {
                        clock.UT = _pred.TransitionUT;
                        ApplyTransition();
                        clock.DropToRealtime();
                    }
                    else if (nodeStop > clock.UT && nodeStop <= target)
                    {
                        clock.UT = nodeStop;
                        _warpTo = null;
                        clock.DropToRealtime();
                    }
                    else if (_warpTo.HasValue && _warpTo.Value <= target)
                    {
                        clock.UT = _warpTo.Value;
                        _warpTo = null;
                        clock.DropToRealtime();
                    }
                    else
                    {
                        clock.UT = target;
                    }
                    if (_vessel.OnRails) _vessel.UpdateFromRails(clock.UT);
                }

                _vessel.UpdateResources(sdt, clock.UT, Ctx.Universe);
                _lastBody = _vessel.Body;
                _lastCamRel = _vessel.Position;
            }
            else
            {
                clock.MaxWarpIndex = SimClock.Levels.Length - 1;
                clock.UT += realDt * clock.Warp;
            }

            // maneuver burn tracking: count delta-v spent against the current node. Once a node's
            // burn time passes it is consumed (KSP-style) and removed -- the live predicted
            // trajectory then stands in as the achieved orbit, so nothing lingers/drifts on screen.
            _nodes.RemoveAll(n => n.HasSource && n.UT < clock.UT);

            var burnTarget = NextNode(clock.UT);
            if (burnTarget == null) { _burnSpent = 0; _burnTargetUT = double.NaN; }
            else
            {
                if (burnTarget.UT != _burnTargetUT) { _burnSpent = 0; _burnTargetUT = burnTarget.UT; }
                if (alive)
                {
                    // Count delta-v across the *whole* burn (it's centred on the node, starting at
                    // node - bt/2), not just its last second -- otherwise the projected orbit grows
                    // for the first half of the burn before the projection kicks in.
                    double bt = Staging.BurnTime(_vessel, burnTarget.DeltaV);
                    double winStart = burnTarget.UT - (bt > 0 ? bt : 1.0);
                    bool burning = _vessel.Throttle > 0 && _vessel.MaxAvailableThrust > 0 && clock.UT >= winStart;
                    if (burning) _burnSpent += _vessel.CurrentThrust / _vessel.TotalMass * realDt * clock.Warp;
                }
            }

            UpdateDebris(realDt);
            UpdateOthers(clock.UT, realDt);
            TryDock(clock.UT);
            PruneTarget();
            UpdateClosestApproach(clock.UT);

            // progression: award science for newly reached flight milestones + transmitted experiments
            var hit = Solar.Progression.Milestones.Evaluate(_vessel, clock.UT, Ctx.State);
            if (hit != null) { _toast = $"+{hit.Reward:0} science  -  {hit.Title}"; _toastT = 5; }
            else CollectScience(clock.UT, realDt * clock.Warp);
            // crew that ran out of life support this tick (overrides other toasts: it's important)
            if (_vessel != null && _vessel.RecentDeaths.Count > 0)
            {
                _toast = $"Crew lost: {string.Join(", ", _vessel.RecentDeaths)}";
                _toastT = 6;
                _vessel.RecentDeaths.Clear();
            }
            if (_toastT > 0) _toastT -= realDt;

            UpdateCamera();
        }

        /// <summary>Within this range of the active vessel (same SOI), a tracked ship is simulated under
        /// physics so close-range relative motion is exact (rendezvous / docking); beyond it, on rails.</summary>
        private const double ProximityRange = 30_000;

        /// <summary>True when a tracked ship is close enough to the active vessel to need physics.</summary>
        private bool OtherNear(Vessel o) =>
            _vessel != null && !_vessel.Destroyed && o != null && !o.Landed && !o.Destroyed
            && o.Body == _vessel.Body && (o.Position - _vessel.Position).Length < ProximityRange;

        private bool AnyOtherNear()
        {
            foreach (var ts in _others) if (OtherNear(ts.V)) return true;
            return false;
        }

        private void UpdateOthers(double ut, double realDt)
        {
            var clock = Ctx.Clock;
            bool physWarp = clock.Warp <= SimClock.Levels[SimClock.PhysicsMaxIndex];
            double sdt = realDt * clock.Warp;
            foreach (var ts in _others)
            {
                var v = ts.V;
                if (v.Destroyed) continue;
                if (v.Landed)
                {
                    // a parked ship stays put; a colony's miners/recyclers keep running while we fly nearby
                    if (v.IsColony && sdt > 0) v.UpdateResources(sdt, ut, Ctx.Universe);
                    continue;
                }

                if (physWarp && OtherNear(v))
                {
                    if (v.OnRails) v.GoOffRails(ut);
                    int steps = Math.Clamp((int)Math.Ceiling(sdt / SubstepDt), 1, 32);
                    double h = sdt / steps;
                    for (int s = 0; s < steps; s++) Integrator.Step(v, h);
                }
                else
                {
                    if (!v.OnRails) v.GoOnRails(ut);
                    v.UpdateFromRails(ut);
                }
            }
        }

        /// <summary>Capture distance / closing-speed limit for an orbital dock, and the (looser) range
        /// for connecting two craft that are both parked on a surface into a compound base.</summary>
        private const double CaptureDist = 15.0;
        private const double SoftDockSpeed = 2.5;
        private const double ConnectDist = 75.0;   // hand-landing tolerance: park within this of a base to connect

        /// <summary>A surface-connect candidate found this frame: a landed ship in connect range that the
        /// player can join into a base by pressing the connect key. Null when none is in range.</summary>
        private TrackedShip _surfaceConnect;

        /// <summary>Auto-perform an orbital dock (a soft capture has no decision to make), and surface every
        /// eligible surface connection as a candidate the player confirms with a key — so two craft don't
        /// silently weld together the instant they touch down near each other.</summary>
        private void TryDock(double ut)
        {
            _surfaceConnect = null;
            if (_vessel == null || _vessel.Destroyed || !_vessel.HasFreeDockingPort) return;
            foreach (var ts in _others)
            {
                var o = ts.V;
                if (o.Destroyed || o.Body != _vessel.Body || !o.HasFreeDockingPort) continue;

                if (_vessel.Landed && o.Landed)
                {
                    // surface connection: don't merge automatically — record it for a confirmed [K] connect
                    if ((o.Position - _vessel.Position).Length < ConnectDist) { _surfaceConnect = ts; return; }
                    continue;
                }
                if (_vessel.Landed || o.Landed) continue;   // can't dock a flying craft to a landed one
                if ((o.Position - _vessel.Position).Length >= CaptureDist) continue;
                if ((o.Velocity - _vessel.Velocity).Length >= SoftDockSpeed) continue;
                DoDock(ts, o, false, ut);
                return;
            }
        }

        /// <summary>Connect the active landed vessel to the pending surface candidate (the [K] action).</summary>
        private void ConnectSurface(double ut)
        {
            if (_surfaceConnect == null) return;
            var ts = _surfaceConnect; _surfaceConnect = null;
            if (ts.V == null || ts.V.Destroyed || !_vessel.Landed || !ts.V.Landed) return;
            if (!_vessel.HasFreeDockingPort || !ts.V.HasFreeDockingPort) return;
            DoDock(ts, ts.V, true, ut);
        }

        /// <summary>Merge a tracked ship into the active vessel as one compound vessel, dropping it from the
        /// tracked/saved set. Shared by an orbital dock and a surface connection.</summary>
        private void DoDock(TrackedShip ts, Vessel o, bool surface, double ut)
        {
            var myPort = _vessel.FirstFreeDockingPort();
            var theirPort = o.FirstFreeDockingPort();
            if (_vessel.OnRails) _vessel.GoOffRails(ut);
            if (!surface)
            {
                // conserve momentum into the merged rigid body (landed craft are already at rest)
                double m1 = _vessel.TotalMass, m2 = o.TotalMass;
                _vessel.Velocity = (_vessel.Velocity * m1 + o.Velocity * m2) / (m1 + m2);
            }
            _vessel.DockWith(o, myPort, theirPort);
            _others.Remove(ts);
            Ctx.State.RemoveShip(ts.Name);
            ClearTarget();
            if (!_vessel.Landed) RefreshPrediction(ut);
            if (surface)
            {
                var ms = Solar.Progression.Milestones.Evaluate(_vessel, ut, Ctx.State);
                _toast = ms != null ? $"+{ms.Reward:0} science  -  {ms.Title}" : $"Connected {ts.Name} to base";
            }
            else
            {
                var ms = Solar.Progression.Milestones.Award(Ctx.State, "docking");
                _toast = ms != null ? $"+{ms.Reward:0} science  -  {ms.Title}" : $"Docked with {ts.Name}";
            }
            _toastT = 5;
        }

        /// <summary>Detach the most-recently docked module; it becomes a tracked co-orbiting ship.</summary>
        private void Undock()
        {
            if (_vessel == null || !_vessel.CanUndock) return;
            double ut = Ctx.Clock.UT;
            if (_vessel.OnRails) _vessel.GoOffRails(ut);
            var detached = _vessel.Undock();
            if (detached == null) return;
            detached.GoOnRails(ut);
            string name = UniqueShipName(_shipName + " module");
            _others.Add(new TrackedShip { Name = name, V = detached });
            Ctx.State.UpsertShip(ShipState.From(detached, name, ut: ut));
            if (!_vessel.Landed && !_vessel.Destroyed) RefreshPrediction(ut);
            _toast = $"Undocked {name}"; _toastT = 4;
        }

        /// <summary>A ship name not already taken by the active vessel, a tracked ship, or the savegame.</summary>
        private string UniqueShipName(string baseName)
        {
            bool Taken(string n) => n == _shipName || _others.Exists(o => o.Name == n) || Ctx.State.Ships.Exists(s => s.Name == n);
            if (!Taken(baseName)) return baseName;
            for (int i = 2; ; i++) { var n = $"{baseName} {i}"; if (!Taken(n)) return n; }
        }

        /// <summary>Drop a target that no longer exists (e.g. debris that decayed).</summary>
        private void PruneTarget()
        {
            if (_targetVessel != null && !_debris.Contains(_targetVessel) && _others.TrueForAll(o => o.V != _targetVessel))
                ClearTarget();
        }

        private void ClearTarget() { _targetBody = null; _targetVessel = null; _targetName = null; _caValid = false; _prox.Clear(); _proxTimer = 0; }

        /// <summary>Deploy all solar panels if any are stowed, otherwise retract them all.</summary>
        private void ToggleSolar()
        {
            if (_vessel == null) return;
            bool anyStowed = false;
            foreach (var p in _vessel.Parts)
                foreach (var m in p.Modules)
                    if (m.Def.Kind == Parts.ModuleKind.SolarPanel && !m.Active) anyStowed = true;
            foreach (var p in _vessel.Parts)
                foreach (var m in p.Modules)
                    if (m.Def.Kind == Parts.ModuleKind.SolarPanel) m.Active = anyStowed;
        }

        /// <summary>Deploy all landing gear if any are retracted, otherwise retract them all.</summary>
        private void ToggleGear()
        {
            if (_vessel == null) return;
            bool anyRetracted = false;
            foreach (var p in _vessel.AllParts())
                if (p.Def.Kind == Solar.Parts.PartKind.LandingGear && !p.Deployed) anyRetracted = true;
            foreach (var p in _vessel.AllParts())
                if (p.Def.Kind == Solar.Parts.PartKind.LandingGear) p.Deployed = anyRetracted;
        }

        private void UpdateClosestApproach(double ut)
        {
            _caValid = false;
            if (!HasTarget || _vessel == null || _vessel.Destroyed || _vessel.Landed) { _prox.Clear(); return; }
            if (!YourPlannedOrbit(ut, out var you, out var youPrimary)) { _prox.Clear(); return; }
            if (!TargetOrbitBody(ut, out var tEl, out var tPrimary)) { _prox.Clear(); return; }

            double window = you.Hyperbolic || double.IsNaN(you.Period) ? 6 * 3600.0 : you.Period;
            Func<double, Vec2d> tpos = t => tPrimary.AbsolutePositionAt(t) + Kepler.StateAtTime(tEl, t).pos;
            _caValid = Rendezvous.ClosestApproach(you, youPrimary, tpos, ut, window, out _caUT, out _caSep, out _caRelSpeed);

            // geometric orbit-curve proximities (intersections + closest points) -- recompute every ~8 frames
            if (--_proxTimer <= 0)
            {
                _proxTimer = 8;
                _prox = Rendezvous.OrbitProximity(you, youPrimary.AbsolutePositionAt(ut), tEl, tPrimary.AbsolutePositionAt(ut));
            }
        }

        private void RefreshPrediction(double ut)
        {
            _pred = _vessel.OnRails
                ? TrajectoryPredictor.Predict(_vessel.Orbit, _vessel.Body, ut)
                : TrajectoryPredictor.Predict(_vessel.CurrentElements(ut), _vessel.Body, ut);
        }

        private void ApplyTransition()
        {
            double t = _pred.TransitionUT;
            var v = _vessel;
            v.UpdateFromRails(t);
            switch (_pred.Type)
            {
                case TransitionType.Escape:
                    v.Position += v.Body.LocalPositionAt(t);
                    v.Velocity += v.Body.LocalVelocityAt(t);
                    v.Body = v.Body.Parent;
                    v.GoOnRails(t);
                    break;
                case TransitionType.Encounter:
                    v.Position -= _pred.NextBody.LocalPositionAt(t);
                    v.Velocity -= _pred.NextBody.LocalVelocityAt(t);
                    v.Body = _pred.NextBody;
                    v.GoOnRails(t);
                    break;
                case TransitionType.AtmoEntry:
                    v.OnRails = false; // physics takes over next frame
                    break;
            }
            // pending nodes were planned around the old primary and can't be re-based; reached nodes
            // carry an absolute snapshot, so keep them visible across the handoff.
            if (_pred.Type != TransitionType.AtmoEntry) { _nodes.RemoveAll(n => !n.Reached); _burnSpent = 0; }
            RefreshPrediction(t);
        }

        private void CheckSoiOffRails(double ut)
        {
            var v = _vessel;
            if (v.Body.Parent != null && v.Position.Length > v.Body.SoiRadius)
            {
                v.Position += v.Body.LocalPositionAt(ut);
                v.Velocity += v.Body.LocalVelocityAt(ut);
                v.Body = v.Body.Parent;
                Ctx.Clock.DropToRealtime();
                _nodes.RemoveAll(n => !n.Reached); _burnSpent = 0;
                return;
            }
            foreach (var c in v.Body.Children)
            {
                if (double.IsInfinity(c.SoiRadius)) continue;
                if ((v.Position - c.LocalPositionAt(ut)).Length < c.SoiRadius)
                {
                    v.Position -= c.LocalPositionAt(ut);
                    v.Velocity -= c.LocalVelocityAt(ut);
                    v.Body = c;
                    Ctx.Clock.DropToRealtime();
                    _nodes.RemoveAll(n => !n.Reached); _burnSpent = 0;
                    return;
                }
            }
        }

        /// <summary>Resolve a surface contact. <paramref name="impactSpeed"/> is the approach speed
        /// measured before the penetrating step, so the figure shown matches the land/crash verdict.</summary>
        private bool CheckSurface(double impactSpeed)
        {
            var v = _vessel;
            double ang = v.Position.Angle();
            double surfR = v.Body.SurfaceRadiusAt(ang);
            if (v.Position.Length > surfR) return false;
            v.Position = v.Position.Normalized() * surfR;
            double slope = v.Body.Terrain?.SlopeAt(ang) ?? 0;
            if (slope > Solar.Physics.Terrain.LandableSlope)
            {
                v.Destroyed = true;
                _endMessage = $"CRASHED on {v.Body.Name}: terrain too steep to land   [Esc] to base";
            }
            else if (v.SurvivesTouchdown(impactSpeed))
            {
                v.Velocity = Vec2d.Zero;
                v.Landed = true;
                v.Throttle = 0;
                v.Heading = SurfaceNormalAngle(v.Body, ang);   // rest aligned to the local slope
                _endMessage = $"Landed safely on {v.Body.Name}!   [Esc] to base";
            }
            else
            {
                v.Destroyed = true;
                _endMessage = $"CRASHED into {v.Body.Name} at {impactSpeed:0.0} m/s   [Esc] to base";
            }
            return true;
        }

        /// <summary>Outward surface-normal angle at a body-local angle, tilted by the terrain slope so a
        /// lander rests leaning on a hill rather than always pointing straight up.</summary>
        private static double SurfaceNormalAngle(Solar.Physics.CelestialBody body, double ang)
        {
            const double e = 1e-3;
            Vec2d pA = Vec2d.FromAngle(ang - e, body.SurfaceRadiusAt(ang - e));
            Vec2d pB = Vec2d.FromAngle(ang + e, body.SurfaceRadiusAt(ang + e));
            Vec2d tangent = pB - pA;
            return new Vec2d(tangent.Y, -tangent.X).Angle();   // tangent rotated -90 deg -> outward normal
        }

        private void UpdateDebris(double realDt)
        {
            var clock = Ctx.Clock;
            for (int i = _debris.Count - 1; i >= 0; i--)
            {
                var d = _debris[i];
                if (d.OnRails)
                {
                    d.UpdateFromRails(clock.UT);
                    if (d.Position.Length < d.Body.Radius + 100) _debris.RemoveAt(i);
                    continue;
                }

                bool inAtmo = d.Body.Atmo != null && d.Altitude < d.Body.Atmo.Top;
                if (clock.Warp > SimClock.Levels[SimClock.PhysicsMaxIndex] ||
                    (_vessel != null && (d.AbsolutePosition(clock.UT) - _vessel.AbsolutePosition(clock.UT)).Length > 30_000))
                {
                    if (inAtmo) { _debris.RemoveAt(i); continue; }
                    d.GoOnRails(clock.UT);
                    continue;
                }

                double sdt = realDt * clock.Warp;
                int steps = Math.Clamp((int)Math.Ceiling(sdt / SubstepDt), 1, 32);
                double h = sdt / steps;
                for (int s = 0; s < steps; s++) Integrator.Step(d, h);
                if (d.Position.Length <= d.Body.Radius) _debris.RemoveAt(i);
            }
        }

        private void UpdateCamera()
        {
            _cam.SetViewport(Ctx.W, Ctx.H);
            double ut = Ctx.Clock.UT;
            bool alive = _vessel != null && !_vessel.Destroyed;
            Vec2d vesselPos = alive ? _vessel.AbsolutePosition(ut) : _lastBody.AbsolutePositionAt(ut) + _lastCamRel;

            if (_map)
            {
                _cam.Center = _focus == 0 ? vesselPos : Ctx.Universe.Bodies[_focus - 1].AbsolutePositionAt(ut);
                _cam.MetersPerPixel = _mapZoom;
            }
            else
            {
                _cam.Center = vesselPos;
                _cam.MetersPerPixel = _flightZoom;
            }
        }

        // =====================================================================  targeting

        private bool HasTarget => _targetBody != null || _targetVessel != null;

        /// <summary>All targetable objects in cycle order: bodies, then debris, then other ships.</summary>
        private List<(string name, CelestialBody body, Vessel vessel)> TargetItems()
        {
            var list = new List<(string, CelestialBody, Vessel)>();
            foreach (var b in Ctx.Universe.Bodies) list.Add((b.Name, b, null));
            for (int i = 0; i < _debris.Count; i++) list.Add(($"Debris {i + 1}", null, _debris[i]));
            foreach (var ts in _others) list.Add((ts.Name, null, ts.V));
            return list;
        }

        private void SetTarget(CelestialBody b, Vessel v, string name)
        {
            _targetBody = b; _targetVessel = v; _targetName = name; _caValid = false; _prox.Clear(); _proxTimer = 0;
        }

        /// <summary>Re-select a saved target by name on resume: a body first, then a co-orbiting ship.</summary>
        private void RestoreTarget(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            var body = Ctx.Universe[name];
            if (body != null) { SetTarget(body, null, name); return; }
            foreach (var ts in _others)
                if (ts.Name == name) { SetTarget(null, ts.V, name); return; }
        }

        /// <summary>Take control of the currently-targeted co-orbiting ship: persist the active vessel,
        /// demote it into the tracked list in the target's slot, and promote the target to the active
        /// vessel. Lets the player fly any of their craft (needed for station / colony assembly).</summary>
        private void TakeControlOfTarget()
        {
            if (_targetVessel == null) return;
            TrackedShip sel = null;
            foreach (var ts in _others) if (ts.V == _targetVessel) { sel = ts; break; }
            if (sel == null) return;   // only co-orbiting ships are controllable (not bodies/debris)

            double ut = Ctx.Clock.UT;
            PersistShip();                          // save the ship we are leaving
            var prevName = _shipName;
            var prevVessel = _vessel;

            // promote the target to the controlled vessel
            _vessel = sel.V;
            _shipName = sel.Name;
            _nodes.Clear(); _burnSpent = 0; _burnTargetUT = double.NaN;
            _sas = SasMode.Off;
            _vessel.RcsCommand = Vec2d.Zero;
            if (_vessel.OnRails) _vessel.UpdateFromRails(ut);
            _lastBody = _vessel.Body;
            _lastCamRel = _vessel.Position;
            if (!_vessel.Landed && !_vessel.Destroyed) RefreshPrediction(ut);

            // the ship we left becomes the tracked co-orbiter in the freed slot
            sel.Name = prevName;
            sel.V = prevVessel;
            if (prevVessel.Body != null && !prevVessel.Landed && !prevVessel.OnRails) prevVessel.GoOnRails(ut);

            ClearTarget();
            _toast = $"Now controlling {_shipName}"; _toastT = 4;
        }

        // ----- map-view ship popup (click a ship icon -> Switch / Set as target) -----
        private const int MenuW = 156, MenuTitleH = 18, MenuBtnH = 18, MenuPad = 6, MenuGap = 3;
        private int MenuBtnCount => _menuControllable ? 2 : 1;
        private int MenuH => MenuPad + MenuTitleH + MenuBtnCount * (MenuBtnH + MenuGap) - MenuGap + MenuPad;
        private Rectangle MenuRect() => new Rectangle((int)_menuPos.X, (int)_menuPos.Y, MenuW, MenuH);
        private Rectangle MenuBtnRect(int i) => new Rectangle((int)_menuPos.X + MenuPad,
            (int)_menuPos.Y + MenuPad + MenuTitleH + i * (MenuBtnH + MenuGap), MenuW - 2 * MenuPad, MenuBtnH);

        /// <summary>Hit-test the controllable ships and debris in map view against the mouse.</summary>
        private bool PickShip(Vector2 mouse, double ut, out Vessel ship, out string name, out bool controllable)
        {
            ship = null; name = null; controllable = false;
            float best = 14f;
            foreach (var ts in _others)
            {
                if (ts.V == null || ts.V.Destroyed) continue;
                float d = Vector2.Distance(_cam.WorldToScreen(ts.V.AbsolutePosition(ut)), mouse);
                if (d < best) { best = d; ship = ts.V; name = ts.Name; controllable = true; }
            }
            for (int i = 0; i < _debris.Count; i++)
            {
                if (_debris[i].Destroyed) continue;
                float d = Vector2.Distance(_cam.WorldToScreen(_debris[i].AbsolutePosition(ut)), mouse);
                if (d < best) { best = d; ship = _debris[i]; name = $"Debris {i + 1}"; controllable = false; }
            }
            return ship != null;
        }

        /// <summary>Map-view ship menu: open on a click over a ship, run its buttons, close on an outside
        /// click. Returns true when it consumed this frame's click so node editing doesn't also fire.</summary>
        private bool UpdateShipMenu(double ut)
        {
            var inp = Ctx.Input;
            if (!_map || _cam.ScreenW == 0) { _menuVessel = null; return false; }
            if (_menuVessel != null && (_menuVessel == _vessel || _menuVessel.Destroyed)) _menuVessel = null;

            if (_menuVessel != null)
            {
                if (!inp.LeftClick) return false;
                for (int i = 0; i < MenuBtnCount; i++)
                    if (MenuBtnRect(i).Contains((int)inp.MousePos.X, (int)inp.MousePos.Y))
                    {
                        bool doSwitch = _menuControllable && i == 0;
                        var v = _menuVessel; var nm = _menuName;
                        _menuVessel = null;
                        SetTarget(null, v, nm);
                        if (doSwitch) TakeControlOfTarget();
                        return true;
                    }
                _menuVessel = null;   // clicked outside: close (and swallow the click)
                return true;
            }

            if (inp.LeftClick && PickShip(inp.MousePos, ut, out var ship, out var name, out bool controllable))
            {
                _menuVessel = ship; _menuName = name; _menuControllable = controllable;
                _menuPos = new Vector2(Math.Min(inp.MousePos.X, _cam.ScreenW - MenuW - 4),
                                       Math.Min(inp.MousePos.Y, _cam.ScreenH - MenuH - 4));
                return true;
            }
            return false;
        }

        private void DrawShipMenu(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb)
        {
            if (_menuVessel == null) return;
            var f = Ctx.Font; var mouse = Ctx.Input.MousePos;
            UiDraw.Panel(pb, MenuRect());
            sb.DrawString(f, _menuName ?? "Ship", new Vector2(_menuPos.X + MenuPad, _menuPos.Y + 3), UiDraw.Accent);
            for (int i = 0; i < MenuBtnCount; i++)
            {
                var br = MenuBtnRect(i);
                bool hover = br.Contains((int)mouse.X, (int)mouse.Y);
                pb.FillRect(br, hover ? new Color(60, 95, 140, 230) : new Color(40, 60, 90, 210));
                string label = (_menuControllable && i == 0) ? "Switch to" : "Set as target";
                sb.DrawString(f, label, new Vector2(br.X + 6, br.Y + 2), Color.White);
            }
        }

        /// <summary>Advance the target through none -> each item -> none.</summary>
        private void CycleTarget(int dir)
        {
            var items = TargetItems();
            int cur = -1;
            for (int i = 0; i < items.Count; i++)
                if (items[i].body == _targetBody && items[i].vessel == _targetVessel && HasTarget) { cur = i; break; }
            int state = cur < 0 ? 0 : cur + 1;                     // 0 = none, 1..n = items
            state = (state + dir + items.Count + 1) % (items.Count + 1);
            if (state == 0) ClearTarget();
            else { var it = items[state - 1]; SetTarget(it.body, it.vessel, it.name); }
        }

        private Vec2d TargetPos(double ut) =>
            _targetBody != null ? _targetBody.AbsolutePositionAt(ut) : _targetVessel.AbsolutePosition(ut);

        private Vec2d TargetVel(double ut) =>
            _targetBody != null ? _targetBody.AbsoluteVelocityAt(ut) : _targetVessel.AbsoluteVelocity(ut);

        /// <summary>Advance the attitude-hold mode, skipping modes unavailable this frame. No-op without power.</summary>
        private void CycleSas()
        {
            if (!_vessel.SasAvailable) { _sas = SasMode.Off; return; }
            double ut = Ctx.Clock.UT;
            var m = _sas;
            do { m = (SasMode)(((int)m + 1) % SasModeCount); }
            while (m != SasMode.Off && !SasModeAvailable(m, ut));
            SetSas(m);
        }

        /// <summary>Engage a SAS mode (from the H key or an icon click): ignored without power, toggles off
        /// when re-selecting the active mode, and captures the current heading for Stability.</summary>
        private void SetSas(SasMode m)
        {
            if (m != SasMode.Off && !_vessel.SasAvailable) return;
            if (m == _sas) m = SasMode.Off;                 // re-click deactivates
            if (m == SasMode.Stability) _sasHold = _vessel.Heading;
            _sas = m;
        }

        /// <summary>Whether a SAS mode can be engaged right now (its target/node/velocity precondition is met).</summary>
        private bool SasModeAvailable(SasMode m, double ut)
        {
            if (m == SasMode.Off) return true;
            if (!_vessel.SasAvailable) return false;
            return m switch
            {
                SasMode.Stability or SasMode.RadialIn or SasMode.RadialOut => true,
                _ => HoldAngleFor(m, ut) != null,
            };
        }

        /// <summary>World angle the current SAS mode points at, or null if unavailable this frame.</summary>
        private double? SasHoldAngle(double ut) => HoldAngleFor(_sas, ut);

        /// <summary>World angle a given SAS mode points at, or null if it can't be computed this frame.</summary>
        private double? HoldAngleFor(SasMode m, double ut)
        {
            if (_vessel == null || _vessel.Destroyed) return null;
            switch (m)
            {
                case SasMode.Stability: return _sasHold;
                case SasMode.Prograde: return _vessel.Velocity.Length > 0.1 ? _vessel.Velocity.Angle() : (double?)null;
                case SasMode.Retrograde: return _vessel.Velocity.Length > 0.1 ? (-_vessel.Velocity).Angle() : (double?)null;
                case SasMode.RadialOut: return _vessel.Position.Length > 1 ? _vessel.Position.Angle() : (double?)null;
                case SasMode.RadialIn: return _vessel.Position.Length > 1 ? (-_vessel.Position).Angle() : (double?)null;
                case SasMode.Target: return HasTarget ? (TargetPos(ut) - _vessel.AbsolutePosition(ut)).Angle() : (double?)null;
                case SasMode.AntiTarget: return HasTarget ? (_vessel.AbsolutePosition(ut) - TargetPos(ut)).Angle() : (double?)null;
                case SasMode.RelRetro:
                    if (!HasTarget) return null;
                    Vec2d rel = _vessel.AbsoluteVelocity(ut) - TargetVel(ut);
                    return rel.Length > 0.1 ? (-rel).Angle() : (double?)null;
                case SasMode.Maneuver:
                    double bd = BurnDirAngle(ut);
                    return double.IsNaN(bd) ? (double?)null : bd;
                default: return null;
            }
        }

        private string SasLabel() => _sas switch
        {
            SasMode.Stability => "STAB", SasMode.Prograde => "PRO", SasMode.Retrograde => "RETRO",
            SasMode.RadialIn => "RAD-IN", SasMode.RadialOut => "RAD-OUT",
            SasMode.Target => "TGT", SasMode.AntiTarget => "ANTI-TGT",
            SasMode.RelRetro => "KILL REL", SasMode.Maneuver => "MNVR", _ => "",
        };

        /// <summary>Bottom-left readout telling the player what the fitted instruments can collect here and
        /// whether an antenna will transmit it — so the science economy is legible in flight.</summary>
        /// <summary>Fire the next stage (ignite the bottom segment, then decouple/jettison on later calls),
        /// dropping off rails for the discrete event and back on if it was coasting. Shared by the [Space]
        /// key and clicking the active row in the HUD stage list.</summary>
        private void FireNextStage()
        {
            double ut = Ctx.Clock.UT;
            bool wasOnRails = _vessel.OnRails;
            if (wasOnRails) _vessel.GoOffRails(ut);
            var debris = Staging.FireNext(_vessel);
            if (debris != null)
            {
                _debris.Add(debris);
                if (_debris.Count > MaxDebris) _debris.RemoveAt(0);
            }
            if (wasOnRails) { _vessel.GoOnRails(ut); RefreshPrediction(ut); }
        }

        private void DrawScienceStatus(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            var v = _vessel;
            if (v == null || v.Destroyed || v.Body == null) return;
            bool hasSci = false, antenna = false, anyNew = false;
            string sit = SciSituation(v);
            foreach (var p in v.AllParts())
                foreach (var m in p.Modules)
                {
                    if (m.Def.Kind == Parts.ModuleKind.Science)
                    {
                        hasSci = true;
                        if (m.Active && !Ctx.State.ScienceCollected.Contains($"{v.Body.Name}|{sit}|{m.Def.Name}")) anyNew = true;
                    }
                    if (m.Def.Kind == Parts.ModuleKind.Antenna && m.Active) antenna = true;
                }

            var f = Ctx.Font;
            double signal = v.SignalStrength(ut, Ctx.Universe, OtherVessels());
            // right side, bottom-anchored, so the bottom-left stage list stays unobstructed
            var r = new Rectangle(Ctx.W - 256 - 10, Ctx.H - 168, 256, 68);
            UiDraw.Panel(pb, r);
            // running total is always shown (milestones pay out even with no instruments)
            sb.DrawString(f, $"SCIENCE: {Ctx.State.Science:0}", new Vector2(r.X + 10, r.Y + 8), new Color(150, 230, 150));
            // signal strength governs transmission speed
            string sigTxt = !antenna ? "Signal: no antenna"
                          : signal <= 0 ? "Signal: none (out of range)"
                          : $"Signal: {signal * 100:0}%";
            Color sigC = !antenna ? new Color(255, 190, 90)
                       : signal <= 0 ? new Color(255, 120, 90)
                       : signal < 0.5 ? new Color(255, 210, 120) : new Color(150, 220, 150);
            sb.DrawString(f, sigTxt, new Vector2(r.X + 10, r.Y + 26), sigC);
            string l2; Color c2;
            if (v.PendingScience > 0)
            {
                l2 = signal > 0 ? $"transmitting... {v.PendingScience:0} data left"
                                : $"{v.PendingScience:0} data queued (no signal)";
                c2 = signal > 0 ? new Color(150, 230, 150) : new Color(255, 190, 90);
            }
            else if (!hasSci) { l2 = "fit an instrument + antenna, or hit milestones"; c2 = UiDraw.TextDim; }
            else if (anyNew) { l2 = $"{v.Body.Name} {sit}: new reading"; c2 = new Color(150, 230, 150); }
            else { l2 = $"{v.Body.Name} {sit}: nothing new here"; c2 = UiDraw.TextDim; }
            sb.DrawString(f, l2, new Vector2(r.X + 10, r.Y + 46), c2);
        }

        /// <summary>Seconds until a resource pool empties at the given draw, or +inf if it isn't draining.</summary>
        private static double TimeLeft(double amount, double ratePerSec)
            => ratePerSec > 0 ? amount / ratePerSec : double.PositiveInfinity;

        // TODO(balance.json): the 250 km low/high-orbit threshold is a global tunable.
        private static string SciSituation(Vessel v) =>
            v.Landed ? "landed" : v.Altitude < 250_000 ? "low orbit" : "high orbit";

        // TODO(balance.json): situation multipliers are global tunables.
        private static double SciSituationFactor(string sit) => sit switch
        {
            "landed" => 1.5, "low orbit" => 0.8, _ => 1.0,   // high orbit = 1.0
        };

        // TODO(balance.json / bodies.json): per-body science multiplier belongs in bodies.json.
        private static double SciBodyFactor(string body) => body switch
        {
            "Earth" => 1.0, "Moon" => 2.0, "Sun" => 2.5, _ => 3.0,
        };

        /// <summary>Science points awarded for running one instrument in a given situation/body. The
        /// instrument's base worth comes from <see cref="Parts.ModuleDef.ScienceValue"/> (data-driven via
        /// modules.json); situation and body apply global multipliers.</summary>
        public static double SciPoints(Parts.ModuleDef def, string sit, string body) =>
            Math.Round(def.ScienceValue * SciSituationFactor(sit) * SciBodyFactor(body));

        /// <summary>Science point/s transmitted at full signal; weak signal scales this down (so distant
        /// vessels transmit slowly) and zero signal stops transmission entirely.</summary>
        private const double TransmitBaseRate = 8.0;   // TODO(balance.json): global transmit rate.

        private IEnumerable<Vessel> OtherVessels()
        {
            foreach (var o in _others) if (o?.V != null) yield return o.V;
        }

        /// <summary>Record new experiments into the vessel's transmit buffer (needs an active instrument +
        /// antenna), then drain that buffer into the science total at a rate scaled by signal strength —
        /// so transmission speed degrades with distance from the nearest antenna / home station.</summary>
        private void CollectScience(double ut, double dt)
        {
            var v = _vessel;
            if (v == null || v.Destroyed || v.Body == null) return;
            bool antenna = false, anyScience = false;
            foreach (var p in v.AllParts())
                foreach (var m in p.Modules)
                {
                    if (m.Def.Kind == Parts.ModuleKind.Antenna && m.Active) antenna = true;
                    if (m.Def.Kind == Parts.ModuleKind.Science && m.Active) anyScience = true;
                }

            var gs = Ctx.State;
            // queue the next unrecorded experiment for transmission
            if (antenna && anyScience)
            {
                string sit = SciSituation(v);
                bool queued = false;
                foreach (var p in v.AllParts())
                {
                    if (queued) break;
                    foreach (var m in p.Modules)
                    {
                        if (m.Def.Kind != Parts.ModuleKind.Science || !m.Active) continue;
                        string key = $"{v.Body.Name}|{sit}|{m.Def.Name}";
                        if (gs.ScienceCollected.Contains(key)) continue;
                        gs.ScienceCollected.Add(key);
                        v.PendingScience += SciPoints(m.Def, sit, v.Body.Name);
                        _toast = $"recorded {m.Def.Name}: {v.Body.Name} {sit}";
                        _toastT = 5;
                        queued = true;
                        break;   // one new experiment per frame
                    }
                }
            }

            // transmit the buffer; rate scales with signal strength, so distance slows it down
            if (v.PendingScience > 0 && dt > 0)
            {
                double signal = v.SignalStrength(ut, Ctx.Universe, OtherVessels());
                if (signal > 0)
                {
                    double xfer = Math.Min(v.PendingScience, TransmitBaseRate * signal * dt);
                    v.PendingScience -= xfer;
                    gs.Science += xfer;
                }
            }
        }

        /// <summary>Build the target navball cues (markers + readout) for the HUD.</summary>
        private NavMarkers BuildNavMarkers(double ut)
        {
            var nav = new NavMarkers { SasMode = (int)_sas, SasLabel = SasLabel(), Target = double.NaN, RelPro = double.NaN };
            bool sasOn = _vessel != null && !_vessel.Destroyed && _vessel.SasAvailable;
            nav.SasEnabled = sasOn;
            var icons = new SasIconInfo[SasModeCount];
            for (int i = 0; i < SasModeCount; i++)
                icons[i] = new SasIconInfo { Icon = i, Available = sasOn && SasModeAvailable((SasMode)i, ut), Active = (int)_sas == i };
            nav.SasIcons = icons;
            if (HasTarget && _vessel != null && !_vessel.Destroyed && !_vessel.Landed)
            {
                nav.Active = true;
                Vec2d sep = TargetPos(ut) - _vessel.AbsolutePosition(ut);
                nav.Target = sep.Angle();
                Vec2d rel = _vessel.AbsoluteVelocity(ut) - TargetVel(ut);
                if (rel.Length > 0.1) nav.RelPro = rel.Angle();
                double closing = sep.Length > 1 ? -rel.Dot(sep.Normalized()) : 0;  // + = closing distance
                nav.Readout = $"TGT {UiDraw.Dist(sep.Length)}  {UiDraw.Speed(closing)}";
            }
            return nav;
        }

        /// <summary>Target's conic + the body it orbits (for prediction). False if the target has no orbit.</summary>
        private bool TargetOrbitBody(double ut, out OrbitalElements el, out CelestialBody primary)
        {
            el = default; primary = null;
            if (_targetBody != null)
            {
                if (_targetBody.Parent == null) return false; // the Sun has no orbit
                el = _targetBody.Orbit; primary = _targetBody.Parent; return true;
            }
            if (_targetVessel != null)
            {
                el = _targetVessel.CurrentElements(ut); primary = _targetVessel.Body;
                return !double.IsNaN(el.A);
            }
            return false;
        }

        /// <summary>Your final planned conic (after all pending nodes) and the body you orbit.</summary>
        private bool YourPlannedOrbit(double ut, out OrbitalElements el, out CelestialBody primary)
        {
            el = default; primary = null;
            var segs = BuildProjection(ut, refreshNodes: false);
            if (segs.Count == 0) return false;
            var last = segs[segs.Count - 1];
            el = last.El; primary = last.Body;
            return !double.IsNaN(el.A);
        }

        // =====================================================================  maneuver planning

        /// <summary>The vessel's live coast orbit + primary (the trajectory currently drawn).</summary>
        private bool LiveOrbit(double ut, out OrbitalElements orbit, out CelestialBody body, out Vec2d primaryAbs)
        {
            orbit = default; primaryAbs = default; body = null;
            if (_vessel == null || _vessel.Destroyed || _vessel.Landed) return false;
            orbit = _vessel.CurrentElements(ut);
            if (double.IsNaN(orbit.A) || double.IsNaN(orbit.E)) return false;
            body = _vessel.Body;
            primaryAbs = body.AbsolutePositionAt(ut);
            return true;
        }

        /// <summary>Orbit a given node is *projected* against: its (chained) source snapshot when
        /// available, else the live orbit.</summary>
        private bool ManeuverContext(Maneuver node, double ut, out OrbitalElements orbit, out CelestialBody body, out Vec2d primaryAbs)
        {
            if (!LiveOrbit(ut, out orbit, out body, out primaryAbs)) return false;
            if (node != null && node.HasSource)
            {
                orbit = node.Source;
                if (node.Body != null)
                {
                    // live-SOI node: anchor to the body's CURRENT position so the marker stays put as the
                    // body moves (e.g. during a burn); a future encounter/escape patch uses its SOI-entry
                    // (ghost) time, matching how BuildProjection draws that conic.
                    double anchor = ReferenceEquals(node.Body, body) ? ut
                                  : (double.IsNaN(node.FrameUT) ? ut : node.FrameUT);
                    body = node.Body;
                    primaryAbs = body.AbsolutePositionAt(anchor);
                }
            }
            return true;
        }

        /// <summary>Screen position of a node and the (unit) screen-space prograde/radial-out axes.</summary>
        private bool ManeuverGeometry(Maneuver node, double ut, out Vector2 nodeScreen, out Vector2 proDir, out Vector2 radDir)
        {
            nodeScreen = default; proDir = default; radDir = default;
            if (node == null || !ManeuverContext(node, ut, out var orbit, out _, out var primaryAbs)) return false;
            var (rN, vN) = Kepler.StateAtTime(orbit, node.UT);
            nodeScreen = _cam.WorldToScreen(primaryAbs + rN);
            Vec2d pro = vN.Normalized();
            Vec2d radOut = pro.Perp();
            if (radOut.Dot(rN) < 0) radOut = -radOut;
            proDir = new Vector2((float)pro.X, -(float)pro.Y);      // world->screen flips Y
            radDir = new Vector2((float)radOut.X, -(float)radOut.Y);
            if (proDir.Length() > 1e-4f) proDir.Normalize();
            if (radDir.Length() > 1e-4f) radDir.Normalize();
            return true;
        }

        /// <summary>Nearest true anomaly on the orbit to the mouse, if within the pick threshold.
        /// <paramref name="soiRadius"/> bounds the drawn arc of a hyperbola (the frame's SOI).</summary>
        private bool PickOrbitNu(in OrbitalElements el, Vec2d primaryAbs, Vector2 mouse, double soiRadius, out double nuBest)
            => PickOrbitNuDist(el, primaryAbs, mouse, soiRadius, out nuBest) <= 10f;

        /// <summary>Screen distance (px) from the mouse to the nearest point on the orbit, plus that nu.</summary>
        private float PickOrbitNuDist(in OrbitalElements el, Vec2d primaryAbs, Vector2 mouse, double soiRadius, out double nuBest)
        {
            nuBest = 0; double best = double.MaxValue;
            const int n = 360;
            bool closed = !el.Hyperbolic;
            double nuMax = Math.PI;
            if (el.Hyperbolic)
            {
                double rLim = double.IsInfinity(soiRadius) ? el.Periapsis * 50 : soiRadius;
                double cnu = (el.SemiLatus / rLim - 1) / el.E;
                nuMax = cnu <= -1 ? Math.PI - 1e-3 : cnu >= 1 ? 0.1 : Math.Acos(cnu);
            }
            for (int s = 0; s <= n; s++)
            {
                double nu = closed ? -Math.PI + 2 * Math.PI * s / n : -nuMax + 2 * nuMax * s / n;
                double r = el.SemiLatus / (1 + el.E * Math.Cos(nu));
                if (r <= 0) continue;
                Vector2 sp = _cam.WorldToScreen(primaryAbs + Vec2d.FromAngle(el.ArgPe + el.Dir * nu) * r);
                float d = Vector2.Distance(sp, mouse);
                if (d < best) { best = d; nuBest = nu; }
            }
            return (float)best;
        }

        // X delete button placement relative to a node marker
        private const float XOffX = 14f, XOffY = -14f, XHit = 9f;
        private static Vector2 XButtonPos(Vector2 nodeScreen) => nodeScreen + new Vector2(XOffX, XOffY);

        private void UpdateManeuverInput(double ut, bool suppressClick, out bool wheelConsumed)
        {
            wheelConsumed = false;
            if (!_map || _cam.ScreenW == 0) { _dragHandle = -1; _dragNode = -1; _hoverHandle = -1; _hoverNode = -1; _hoverX = -1; return; }
            var inp = Ctx.Input;
            if (!LiveOrbit(ut, out var live, out var body, out var primaryAbs))
            {
                if (_vessel == null || _vessel.Destroyed || _vessel.Landed) { _nodes.Clear(); _burnSpent = 0; }
                _dragHandle = -1; _dragNode = -1; _hoverHandle = -1; _hoverNode = -1; _hoverX = -1;
                return;
            }

            // re-chain pending nodes across SOI transitions while coasting; freeze during a burn
            bool thrusting = _vessel.CurrentThrust > 0;
            if (!thrusting) RefreshNodeChain(ut);

            Vector2 mouse = inp.MousePos;

            // [Del] removes the active/next node
            if (_nodes.Count > 0 && inp.Pressed(Keys.Delete))
            {
                var del = _nodes[Math.Max(0, _hoverNode)];
                if (_hoverNode < 0) del = NextNode(ut) ?? _nodes[0];
                _nodes.Remove(del); _burnSpent = 0; _dragHandle = -1; _dragNode = -1;
            }

            // hover scan across all nodes (handles, body, X)
            _hoverHandle = -1; _hoverNode = -1; _hoverX = -1;
            for (int n = 0; n < _nodes.Count && _hoverHandle < 0 && _hoverX < 0; n++)
            {
                if (!ManeuverGeometry(_nodes[n], ut, out var ns, out var pd, out var rd)) continue;
                if (Vector2.Distance(mouse, XButtonPos(ns)) <= XHit) { _hoverX = n; break; }
                var h = new[] { ns + pd * HandleDist, ns - pd * HandleDist, ns + rd * HandleDist, ns - rd * HandleDist };
                for (int k = 0; k < 4; k++)
                    if (Vector2.Distance(mouse, h[k]) <= HandleHit) { _hoverHandle = k; _hoverNode = n; break; }
                if (_hoverHandle < 0 && Vector2.Distance(mouse, ns) <= NodeHit) _hoverNode = n;
            }

            // scroll-wheel fine-tune when hovering a handle (takes priority over zoom)
            if (_hoverHandle >= 0 && _hoverNode >= 0 && inp.WheelDelta != 0)
            {
                double step = (inp.Down(Keys.LeftShift) || inp.Down(Keys.RightShift)) ? 0.5 : 5.0;
                AdjustComponent(_nodes[_hoverNode], _hoverHandle, Math.Sign(inp.WheelDelta) * step);
                wheelConsumed = true;
            }

            // keyboard fine-tune targets the next node
            var kbNode = NextNode(ut);
            if (kbNode != null)
            {
                double ks = (inp.Down(Keys.LeftShift) || inp.Down(Keys.RightShift)) ? 0.5 : 5.0;
                if (inp.Pressed(Keys.OemCloseBrackets)) kbNode.Prograde += ks;
                if (inp.Pressed(Keys.OemOpenBrackets)) kbNode.Prograde -= ks;
                if (inp.Pressed(Keys.OemQuotes)) kbNode.Radial += ks;
                if (inp.Pressed(Keys.OemSemicolon)) kbNode.Radial -= ks;
            }

            // begin interaction on click (suppressed when the ship popup consumed this click)
            if (inp.LeftClick && !suppressClick)
            {
                if (_hoverX >= 0)
                {
                    _nodes.RemoveAt(_hoverX); _burnSpent = 0; _dragHandle = -1; _dragNode = -1;
                }
                else if (_hoverHandle >= 0 && _hoverNode >= 0)
                {
                    _dragHandle = _hoverHandle; _dragNode = _hoverNode;
                    ManeuverGeometry(_nodes[_dragNode], ut, out var ns, out var pd, out var rd);
                    Vector2 axis = _dragHandle < 2 ? pd : rd;
                    _dragStartProj = Vector2.Dot(mouse - ns, axis);
                    _dragStartValue = _dragHandle < 2 ? _nodes[_dragNode].Prograde : _nodes[_dragNode].Radial;
                }
                else if (_hoverNode >= 0)
                {
                    _dragHandle = 4; _dragNode = _hoverNode; // move node along its orbit
                }
                else if (!_showTargetWindow)
                {
                    // place a new node on whichever projected conic is clicked -- including the path
                    // inside a body encountered/escaped to (KSP patched-conic node planning)
                    var segs = BuildProjection(ut, refreshNodes: false);
                    double bestNu = 0; float bestD = 10f; ProjSegment bestSeg = default; bool hit = false;
                    foreach (var sg in segs)
                    {
                        float d = PickOrbitNuDist(sg.El, sg.PrimaryAbs, mouse, sg.Body.SoiRadius, out double nu);
                        if (d < bestD) { bestD = d; bestNu = nu; bestSeg = sg; hit = true; }
                    }
                    if (hit)
                    {
                        _nodes.Add(new Maneuver
                        {
                            UT = Kepler.TimeAtTrueAnomaly(bestSeg.El, bestNu, bestSeg.StartUT),
                            Source = bestSeg.El, Body = bestSeg.Body, HasSource = true
                        });
                        _burnSpent = 0; _dragHandle = -1; _dragNode = -1;
                    }
                }
            }

            // continue drag of the active node
            if (inp.LeftDown && _dragHandle >= 0 && _dragNode >= 0 && _dragNode < _nodes.Count)
            {
                var node = _nodes[_dragNode];
                var src = node.HasSource ? node.Source : live;
                var nodeBody = node.Body ?? body;
                var nodeAbs = node.Body != null ? node.Body.AbsolutePositionAt(ut) : primaryAbs;
                if (_dragHandle == 4)
                {
                    if (PickOrbitNu(src, nodeAbs, mouse, nodeBody.SoiRadius, out double nu))
                        node.UT = Kepler.TimeAtTrueAnomaly(src, nu, ut);
                }
                else if (ManeuverGeometry(node, ut, out var ns, out var pd, out var rd))
                {
                    Vector2 axis = _dragHandle < 2 ? pd : rd;
                    double proj = Vector2.Dot(mouse - ns, axis);
                    double sens = Math.Max(1.0, Kepler.StateAtTime(src, node.UT).vel.Length * 0.01);
                    double val = _dragStartValue + (proj - _dragStartProj) * sens;
                    if (_dragHandle < 2) node.Prograde = val; else node.Radial = val;
                }
            }
            if (!inp.LeftDown) { _dragHandle = -1; _dragNode = -1; }
        }

        /// <summary>World angle of the next node's burn-delta vector, or NaN if there is none.</summary>
        private double BurnDirAngle(double ut)
        {
            var node = NextNode(ut);
            if (node == null || !ManeuverContext(node, ut, out var orbit, out _, out _)) return double.NaN;
            var (rN, vN) = Kepler.StateAtTime(orbit, node.UT);
            Vec2d bd = node.BurnDelta(rN, vN);
            return bd.Length > 1e-6 ? bd.Angle() : double.NaN;
        }

        private static void AdjustComponent(Maneuver node, int handle, double d)
        {
            switch (handle)
            {
                case 0: node.Prograde += d; break;
                case 1: node.Prograde -= d; break;
                case 2: node.Radial += d; break;
                case 3: node.Radial -= d; break;
            }
        }

        // =====================================================================  draw

        public override void Draw()
        {
            var pb = Ctx.Pb; var sb = Ctx.Sb;
            double ut = Ctx.Clock.UT;
            pb.Begin();
            sb.Begin();

            Ctx.Stars.Draw(pb, Ctx.W, Ctx.H, _cam.Center);

            if (_map) DrawMap(pb, sb, ut);
            else DrawWorld(pb, ut);

            var hud = Hud.Draw(Ctx, _vessel, _pred, _map, FocusName(), NextNode(ut), BurnDirAngle(ut), _burnSpent, _nodes.Count, BuildNavMarkers(ut));
            if (hud.WarpToUT.HasValue) _warpTo = hud.WarpToUT;
            if (hud.FireStage) FireNextStage();
            if (hud.RequestedSas.HasValue) SetSas((SasMode)hud.RequestedSas.Value);

            DrawTargetPanel(pb, sb, ut);
            DrawTargetWindow(pb, sb);
            DrawScienceStatus(pb, sb, ut);
            DrawCrewPanel(pb, sb);
            DrawColonyPanel(pb, sb, ut);
            DrawCancelMission(pb, sb);
            if (_map) DrawShipMenu(pb, sb);

            if (_toastT > 0 && _toast != null)
            {
                var f = Ctx.FontBig;
                var sz = f.MeasureString(_toast);
                float alpha = (float)Math.Clamp(_toastT, 0, 1);
                var pos = new Vector2(Ctx.W / 2 - sz.X / 2, 70);
                pb.FillRect((int)pos.X - 14, (int)pos.Y - 6, (int)sz.X + 28, (int)sz.Y + 12, new Color(20, 30, 22, (int)(200 * alpha)));
                sb.DrawString(f, _toast, pos, new Color(150, 230, 150) * alpha);
            }

            // surface-rendezvous prompt: a parked ship is in connect range — confirm the merge with [K]
            if (_surfaceConnect != null && _vessel != null && _vessel.Landed && !_vessel.Destroyed && !_showExitDialog)
            {
                var f = Ctx.FontBig;
                string msg = $"[K] connect to {_surfaceConnect.Name}";
                var sz = f.MeasureString(msg);
                var pos = new Vector2(Ctx.W / 2 - sz.X / 2, Ctx.H - 150);
                pb.FillRect((int)pos.X - 12, (int)pos.Y - 6, (int)sz.X + 24, (int)sz.Y + 12, new Color(20, 30, 40, 200));
                sb.DrawString(f, msg, pos, new Color(150, 230, 150));
            }

            DrawExitDialog(pb, sb);

            pb.End();
            sb.End();

            if (_endMessage != null)
            {
                pb.Begin();
                sb.Begin();
                pb.FillRect(0, Ctx.H / 2 - 60, Ctx.W, 120, new Color(0, 0, 0, 160));
                var sz = Ctx.FontBig.MeasureString(_endMessage);
                sb.DrawString(Ctx.FontBig, _endMessage, new Vector2(Ctx.W / 2 - sz.X / 2, Ctx.H / 2 - sz.Y / 2), Color.White);
                pb.End();
                sb.End();
            }
        }

        /// <summary>The pause/exit modal: Save game / Return to game / Exit to main menu. Button clicks
        /// are evaluated here (immediate-mode); the actual scene switch is deferred to the next Update
        /// (via <see cref="_exitToTitle"/>) so it doesn't run mid-draw.</summary>
        private void DrawExitDialog(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb)
        {
            if (!_showExitDialog) return;
            var f = Ctx.Font;
            var inp = Ctx.Input;
            pb.FillRect(0, 0, Ctx.W, Ctx.H, new Color(0, 0, 0, 150));   // dim the world behind
            bool onPad = _vessel is { Destroyed: false, Landed: true, EnginesIgnited: false };
            int w = 320, h = onPad ? 310 : 262;
            var r = new Rectangle(Ctx.W / 2 - w / 2, Ctx.H / 2 - h / 2, w, h);
            UiDraw.Panel(pb, r);
            const string title = "PAUSED";
            var ts = f.MeasureString(title);
            sb.DrawString(f, title, new Vector2(r.Center.X - ts.X / 2, r.Y + 16), UiDraw.Accent);

            int bx = r.X + 30, bw = w - 60, bh = 38, by = r.Y + 52;
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "Save game", inp))
            { PersistShip(); PersistOthers(); SaveGame.Save(Ctx.State); _toast = "Game saved"; _toastT = 3; }
            by += bh + 10;
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "Return to game", inp))
                _showExitDialog = false;
            by += bh + 10;
            // on the pad (landed, never launched): scrap the launch and return to the editor
            if (onPad && UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "Back to editor (scrap launch)", inp))
            { _cancelMission = true; _showExitDialog = false; }
            if (onPad) by += bh + 10;
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "Space Center", inp))
                _exitToHub = true;
            by += bh + 10;
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "Exit to main menu", inp))
                _exitToTitle = true;
        }

        /// <summary>A "Cancel mission" button shown only while the ship is on the pad (landed and never
        /// launched): scraps the flight and reopens the design in the editor. The scene switch is
        /// deferred to the next Update via <see cref="_cancelMission"/>.</summary>
        private void DrawCancelMission(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb)
        {
            if (_showExitDialog) return;   // don't sit under the modal
            var v = _vessel;
            if (v == null || v.Destroyed || !v.Landed || v.EnginesIgnited) return;
            var rect = new Rectangle(Ctx.W / 2 - 90, 8, 180, 28);
            if (UiDraw.Button(pb, sb, Ctx.Font, rect, "Cancel mission", Ctx.Input)) _cancelMission = true;
        }

        /// <summary>In-flight crew panel: lists crew per crewable axial part, with Up/Dn buttons to
        /// transfer one crew member to the adjacent crewable part when it has a free seat.</summary>
        private void DrawCrewPanel(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb)
        {
            if (!_showCrew) return;
            var v = _vessel;
            var f = Ctx.Font;
            var inp = Ctx.Input;
            var seated = new List<Solar.Parts.Part>();
            if (v != null && !v.Destroyed) foreach (var p in v.Parts) if (p.SeatCount > 0) seated.Add(p);

            int crew = v?.CrewCount ?? 0;
            int wWin = 280, rows = 0;
            foreach (var p in seated) rows += 1 + p.Crew.Count;
            var r = new Rectangle(Ctx.W - wWin - 10, 120, wWin, 78 + Math.Max(1, rows) * 20);
            UiDraw.Panel(pb, r);
            float y = r.Y + 8;
            sb.DrawString(f, "CREW  [C] close", new Vector2(r.X + 10, y), UiDraw.Accent); y += 22;

            // life-support wellbeing summary (which resource is the limiter, and time left)
            if (v != null && crew > 0)
            {
                bool ok = v.LifeSupportOk;
                string ls;
                if (!ok) ls = "Life support: CRITICAL";
                else
                {
                    // the resource that will run out first, by time-to-empty per current crew draw
                    double tO = TimeLeft(v.Oxygen, crew * Vessel.OxygenPerCrew);
                    double tW = TimeLeft(v.Water, crew * Vessel.WaterPerCrew);
                    double tF = TimeLeft(v.Food, crew * Vessel.FoodPerCrew);
                    double min = Math.Min(tO, Math.Min(tW, tF));
                    string which = min == tO ? "Oxygen" : min == tW ? "Water" : "Food";
                    ls = double.IsPositiveInfinity(min) ? "Life support: OK" : $"{which}: {UiDraw.Time(min)} left";
                }
                sb.DrawString(f, ls, new Vector2(r.X + 10, y), ok ? new Color(150, 220, 150) : new Color(255, 100, 90));
            }
            else sb.DrawString(f, "No crew aboard", new Vector2(r.X + 10, y), UiDraw.TextDim);
            y += 22;

            if (seated.Count == 0)
            { sb.DrawString(f, "No crew-capable parts", new Vector2(r.X + 10, y), UiDraw.TextDim); return; }

            Color crewCol = (v != null && !v.LifeSupportOk) ? new Color(255, 100, 90) : Color.White;
            Solar.Parts.Part moveFrom = null, moveTo = null;
            for (int k = 0; k < seated.Count; k++)
            {
                var p = seated[k];
                sb.DrawString(f, $"{p.Def.Name}  ({p.Crew.Count}/{p.SeatCount})", new Vector2(r.X + 10, y), crewCol);
                if (p.Crew.Count > 0)
                {
                    if (k > 0 && seated[k - 1].Crew.Count < seated[k - 1].SeatCount
                        && UiDraw.Button(pb, sb, f, new Rectangle(r.Right - 72, (int)y - 2, 30, 20), "Up", inp))
                    { moveFrom = p; moveTo = seated[k - 1]; }
                    if (k < seated.Count - 1 && seated[k + 1].Crew.Count < seated[k + 1].SeatCount
                        && UiDraw.Button(pb, sb, f, new Rectangle(r.Right - 38, (int)y - 2, 30, 20), "Dn", inp))
                    { moveFrom = p; moveTo = seated[k + 1]; }
                }
                y += 20;
                foreach (var c in p.Crew)
                { sb.DrawString(f, $"  - {c.Name} ({c.Role})", new Vector2(r.X + 10, y), UiDraw.TextDim); y += 20; }
            }
            if (moveFrom != null && moveTo != null) v.TransferCrew(moveFrom, moveTo);
        }

        /// <summary>Free module slots on a part (its capacity minus what fitted modules already consume).</summary>
        private static int FreeSlots(Solar.Parts.Part p)
        {
            int used = 0; foreach (var m in p.Modules) used += m.Def.SlotCost;
            return p.Def.Slots - used;
        }

        /// <summary>Surface-base management (landed only): establish a colony, then fabricate vessels and
        /// modules at it from the base's pooled fuel. The heart of the build-a-base loop.</summary>
        private void DrawColonyPanel(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            if (!_showColony) return;
            var v = _vessel;
            if (v == null || v.Destroyed || !v.Landed) return;
            var f = Ctx.Font; var inp = Ctx.Input;

            const int wWin = 300;
            var r = new Rectangle(Ctx.W - wWin - 10, 320, wWin, 232);
            UiDraw.Panel(pb, r);
            float y = r.Y + 8;
            sb.DrawString(f, "BASE  [B] close", new Vector2(r.X + 10, y), UiDraw.Accent); y += 24;
            void Row(string label, string value, Color? c = null)
            {
                sb.DrawString(f, label, new Vector2(r.X + 12, y), UiDraw.TextDim);
                sb.DrawString(f, value, new Vector2(r.X + 120, y), c ?? Color.White);
                y += 18;
            }
            bool eng = Colony.HasEngineer(v);
            Row("Body", v.Body.Name);
            Row("Status", v.IsColony ? "COLONY" : "Landed craft", v.IsColony ? new Color(255, 190, 90) : Color.White);
            Row("Modules", (v.DockLinks.Count + 1).ToString());
            Row("Crew", v.CrewCount.ToString(), eng ? new Color(150, 220, 150) : Color.White);
            Row("Fuel (material)", $"{v.TotalLiquidFuel:0} kg");
            y += 6;

            var btn = new Rectangle(r.X + 12, (int)y, wWin - 24, 26);
            if (!v.IsColony)
            {
                bool can = Colony.CanEstablish(v);
                if (UiDraw.Button(pb, sb, f, btn, "Establish Colony", inp, can))
                { v.IsColony = true; _toast = "Colony established at " + v.Body.Name; _toastT = 4; }
                y += 30;
                if (!can) sb.DrawString(f, "needs crew aboard a landed craft", new Vector2(r.X + 12, y), UiDraw.TextDim);
            }
            else
            {
                bool canBuild = eng && v.TotalLiquidFuel >= Colony.BuildReserve;
                if (UiDraw.Button(pb, sb, f, btn, "Build Vessel", inp, canBuild))
                { _buildAtColony = true; _showColony = false; _showAddModule = false; }
                y += 30;
                var btn2 = new Rectangle(r.X + 12, (int)y, wWin - 24, 26);
                if (UiDraw.Button(pb, sb, f, btn2, _showAddModule ? "Add Module  (close)" : "Add Module", inp, eng))
                    _showAddModule = !_showAddModule;
                y += 30;
                sb.DrawString(f, eng ? "[C] manage crew" : "needs an engineer to fabricate",
                              new Vector2(r.X + 12, y), eng ? UiDraw.TextDim : new Color(255, 170, 90));
            }

            if (_showAddModule && v.IsColony) DrawAddModule(pb, sb, r);
        }

        /// <summary>The "add module" sub-list: every tech-available module the base can fit and afford,
        /// fabricated into the first part with a free slot for the base's fuel as raw material.</summary>
        private void DrawAddModule(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, Rectangle basePanel)
        {
            var v = _vessel; var f = Ctx.Font; var inp = Ctx.Input;
            const int wWin = 300, rowH = 24, maxRows = 12;
            var r = new Rectangle(basePanel.X - wWin - 6, basePanel.Y, wWin, 40 + maxRows * rowH);
            UiDraw.Panel(pb, r);
            sb.DrawString(f, "FABRICATE MODULE", new Vector2(r.X + 10, r.Y + 8), UiDraw.Accent);
            float y = r.Y + 32;
            int shown = 0;
            foreach (var md in Solar.Parts.ModuleCatalog.All)
            {
                if (shown >= maxRows) break;
                if (!Solar.Progression.TechTree.ModuleAvailable(Ctx.State, md.Id)) continue;
                bool fits = false;
                foreach (var p in v.AllParts()) if (FreeSlots(p) >= md.SlotCost) { fits = true; break; }
                if (!fits) continue;
                bool afford = Colony.CanFabricate(v, md.DryMass);
                var br = new Rectangle(r.X + 10, (int)y, wWin - 20, rowH - 2);
                string label = $"{md.Name}   {md.DryMass * Colony.MaterialPerKg:0} kg";
                if (UiDraw.Button(pb, sb, f, br, label, inp, afford))
                    FabricateModule(md);
                y += rowH; shown++;
            }
            if (shown == 0)
                sb.DrawString(f, "nothing fits / affordable", new Vector2(r.X + 10, y), UiDraw.TextDim);
        }

        /// <summary>Fit a fabricated module into the base's first part with a free slot, charging the
        /// material cost. No-op if it no longer fits or the base can't pay.</summary>
        private void FabricateModule(Solar.Parts.ModuleDef md)
        {
            var v = _vessel;
            Solar.Parts.Part target = null;
            foreach (var p in v.AllParts()) if (FreeSlots(p) >= md.SlotCost) { target = p; break; }
            if (target == null) return;
            if (!Colony.PayFabrication(v, md.DryMass)) return;
            target.Modules.Add(new Solar.Parts.ModuleInstance(md));
            _toast = $"Fabricated {md.Name}"; _toastT = 4;
        }

        /// <summary>Open the editor to build a new vessel at this colony: the next launch spawns it landed
        /// a few metres beside the base (ready to surface-dock), rather than on the home pad.</summary>
        private void BuildAtColony()
        {
            var v = _vessel;
            if (v == null || !v.IsColony || !v.Landed || v.Destroyed) return;
            var up = v.Position.Normalized();
            var tangent = new Vec2d(-up.Y, up.X);                       // along the local surface
            var pos = (v.Position + tangent * 25).Normalized() * v.Body.Radius;   // 25 m beside, on the surface
            Ctx.PendingLaunchSite = new LaunchSite { BodyName = v.Body.Name, Position = pos, Heading = pos.Angle() };
            PersistShip();
            PersistOthers();
            Ctx.Scenes.SwitchTo(new EditorScene(Ctx));
        }

        private string FocusName() =>
            _focus == 0 ? "Vessel" : Ctx.Universe.Bodies[_focus - 1].Name;

        /// <summary>Compact target readout (below the left HUD panel): name, distance, rendezvous.</summary>
        private void DrawTargetPanel(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            if (!HasTarget) return;
            var f = Ctx.Font;
            var r = new Rectangle(10, 286, 256, 188);
            UiDraw.Panel(pb, r);
            float y = r.Y + 8;
            sb.DrawString(f, "TARGET  [Tab] cycle  [T] list", new Vector2(r.X + 10, y), UiDraw.Accent); y += 20;
            void Row(string label, string value, Color? c = null)
            {
                sb.DrawString(f, label, new Vector2(20, y), UiDraw.TextDim);
                sb.DrawString(f, value, new Vector2(120, y), c ?? Color.White);
                y += 18;
            }
            Row("Name", _targetName ?? "-", TargetColor);
            bool alive = _vessel != null && !_vessel.Destroyed;
            if (alive)
            {
                Vec2d sep = TargetPos(ut) - _vessel.AbsolutePosition(ut);
                Row("Distance", UiDraw.Dist(sep.Length));
                Vec2d rel = _vessel.AbsoluteVelocity(ut) - TargetVel(ut);
                bool closing = rel.Dot(sep) > 0;   // velocity points toward the target
                Row("Rel. vel.", UiDraw.Speed(rel.Length), closing ? new Color(150, 220, 150) : new Color(255, 170, 90));
                Row("", closing ? "closing" : "opening", closing ? new Color(150, 220, 150) : new Color(255, 170, 90));
            }
            // docking cue: when both craft have a free port and share an SOI, show capture readiness
            // (orbital dock) or surface-connect readiness (both landed)
            if (alive && _targetVessel != null
                && _vessel.HasFreeDockingPort && _targetVessel.HasFreeDockingPort
                && _targetVessel.Body == _vessel.Body)
            {
                double d = (_targetVessel.Position - _vessel.Position).Length;
                var green = new Color(150, 230, 150); var blue = new Color(120, 190, 230);
                if (_vessel.Landed && _targetVessel.Landed)
                {
                    bool ready = d < ConnectDist;
                    Row("Connect", ready ? "[K] to connect" : $"approach < {ConnectDist:0} m", ready ? green : blue);
                }
                else if (!_vessel.Landed && !_targetVessel.Landed)
                {
                    double rs = (_targetVessel.Velocity - _vessel.Velocity).Length;
                    bool ready = d < CaptureDist && rs < SoftDockSpeed;
                    string txt = ready ? "capturing..."
                               : d >= CaptureDist ? $"close to {CaptureDist:0} m"
                               : $"slow below {SoftDockSpeed:0.0} m/s";
                    Row("Dock", txt, ready ? green : blue);
                }
            }
            if (_caValid)
            {
                Row("Close App.", UiDraw.Dist(_caSep), new Color(150, 220, 150));
                Row("  in", UiDraw.Time(_caUT - ut));
                Row("  rel. vel.", UiDraw.Speed(_caRelSpeed));
            }
            if (_prox.Count > 0)
            {
                bool meet = false; double nearest = double.MaxValue;
                foreach (var p in _prox) { if (p.Intersect) meet = true; if (p.Sep < nearest) nearest = p.Sep; }
                if (meet) Row("Orbits", "intersect", new Color(255, 190, 70));
                else Row("Orbit gap", UiDraw.Dist(nearest), new Color(140, 230, 160));
            }
        }

        /// <summary>Togglable list window for picking a target by clicking.</summary>
        private void DrawTargetWindow(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb)
        {
            if (!_showTargetWindow) return;
            var f = Ctx.Font; var inp = Ctx.Input;
            var items = TargetItems();
            const int rowH = 20;
            int maxRows = Math.Max(6, (Ctx.H - 160) / rowH);
            int rows = Math.Min(items.Count + 1, maxRows);   // +1 for the "None" row
            int wWin = 230, hWin = rows * rowH + 34;
            var r = new Rectangle(Ctx.W / 2 - wWin / 2, 70, wWin, hWin);
            UiDraw.Panel(pb, r);
            sb.DrawString(f, "SELECT TARGET  [T] close", new Vector2(r.X + 8, r.Y + 6), UiDraw.Accent);

            int y = r.Y + 28;
            void RowBox(bool selected, string label, Action onClick)
            {
                var rr = new Rectangle(r.X + 6, y, wWin - 12, rowH - 1);
                bool hover = rr.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y);
                if (selected) pb.FillRect(rr, new Color(60, 95, 140, 200));
                else if (hover) pb.FillRect(rr, new Color(45, 70, 105, 200));
                sb.DrawString(f, label, new Vector2(rr.X + 6, rr.Y + 2), selected ? Color.White : UiDraw.TextDim);
                if (hover && inp.LeftClick) onClick();
                y += rowH;
            }

            RowBox(!HasTarget, "(none)", ClearTarget);
            for (int i = 0; i < items.Count && i < rows - 1; i++)
            {
                var it = items[i];
                bool sel = it.body == _targetBody && it.vessel == _targetVessel && HasTarget;
                RowBox(sel, it.name, () => SetTarget(it.body, it.vessel, it.name));
            }
        }

        private void DrawWorld(PrimitiveBatch pb, double ut)
        {
            foreach (var b in Ctx.Universe.Bodies)
                PlanetRenderer.Draw(pb, _cam, b, ut, false, Ctx.Sb, Ctx.Textures.Body(b.TextureId),
                                    slopeOverlay: _slopeView);

            if (_vessel != null && !_vessel.Destroyed && !_vessel.Landed)
                _dust.Draw(pb, _cam, _vessel.Velocity);

            foreach (var d in _debris)
                VesselRenderer.Draw(pb, _cam, d, ut, _anim, tex: Ctx.Textures);

            foreach (var ts in _others)
                VesselRenderer.Draw(pb, _cam, ts.V, ut, _anim, tex: Ctx.Textures);

            if (_vessel != null && !_vessel.Destroyed)
                VesselRenderer.Draw(pb, _cam, _vessel, ut, _anim, tex: Ctx.Textures);

            if (HasTarget && _vessel != null && !_vessel.Destroyed)
                DrawTargetIndicator(pb, ut);
        }

        /// <summary>Flight-view aid: a magenta reticle on the target when it is on screen, or an
        /// arrow pinned to the screen edge pointing toward it when off screen, with a distance label.</summary>
        private void DrawTargetIndicator(PrimitiveBatch pb, double ut)
        {
            var sb = Ctx.Sb;
            var tScreen = _cam.WorldToScreenD(TargetPos(ut));
            double dist = (TargetPos(ut) - _vessel.AbsolutePosition(ut)).Length;
            const float margin = 36f;
            bool onScreen = tScreen.X >= margin && tScreen.X <= Ctx.W - margin &&
                            tScreen.Y >= margin && tScreen.Y <= Ctx.H - margin;
            if (onScreen)
            {
                var p = new Vector2((float)tScreen.X, (float)tScreen.Y);
                pb.CircleOutline(p, 11, 1.6f, TargetColor);
                pb.Line(p + new Vector2(-16, 0), p + new Vector2(-11, 0), 1.5f, TargetColor);
                pb.Line(p + new Vector2(11, 0), p + new Vector2(16, 0), 1.5f, TargetColor);
                pb.Line(p + new Vector2(0, -16), p + new Vector2(0, -11), 1.5f, TargetColor);
                pb.Line(p + new Vector2(0, 11), p + new Vector2(0, 16), 1.5f, TargetColor);
                sb.DrawString(Ctx.Font, $"{_targetName}  {UiDraw.Dist(dist)}", p + new Vector2(14, 10), TargetColor);
            }
            else
            {
                // clamp the direction to the screen-edge margin box and draw an arrow toward it
                var center = new Vector2(Ctx.W / 2f, Ctx.H / 2f);
                var dir = new Vector2((float)tScreen.X - center.X, (float)tScreen.Y - center.Y);
                if (dir.LengthSquared() < 1e-3f) dir = new Vector2(0, -1);
                dir.Normalize();
                float halfW = Ctx.W / 2f - margin, halfH = Ctx.H / 2f - margin;
                float scale = Math.Min(halfW / Math.Max(1e-3f, Math.Abs(dir.X)), halfH / Math.Max(1e-3f, Math.Abs(dir.Y)));
                var edge = center + dir * scale;
                var perp = new Vector2(-dir.Y, dir.X);
                pb.Line(edge - dir * 10 + perp * 7, edge, 2f, TargetColor);
                pb.Line(edge - dir * 10 - perp * 7, edge, 2f, TargetColor);
                sb.DrawString(Ctx.Font, $"{_targetName}  {UiDraw.Dist(dist)}", edge - dir * 18 + new Vector2(6, 6), TargetColor);
            }
        }

        private void DrawMap(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            var u = Ctx.Universe;

            // body orbits
            foreach (var b in u.Bodies)
            {
                if (b.Parent == null) continue;
                Vec2d parentAbs = b.Parent.AbsolutePositionAt(ut);
                OrbitRenderer.DrawConic(pb, _cam, b.Orbit, parentAbs, b.BodyColor * 0.4f, 1.2f);
                OrbitRenderer.DrawDirectionArrows(pb, _cam, b.Orbit, parentAbs, b.BodyColor * 0.7f);
            }

            // SOI circles
            foreach (var b in u.Bodies)
            {
                if (double.IsInfinity(b.SoiRadius)) continue;
                double soiPx = b.SoiRadius / _cam.MetersPerPixel;
                if (soiPx < 12 || soiPx > 40000) continue;
                var s = _cam.WorldToScreenD(b.AbsolutePositionAt(ut));
                if (!_cam.OnScreen(s, soiPx + 50)) continue;
                pb.DashedCircleOutline(new Vector2((float)s.X, (float)s.Y), (float)soiPx, 1f, b.BodyColor * 0.45f);
            }

            // bodies (+ landing-site markers on the guaranteed flat plains)
            foreach (var b in u.Bodies)
            {
                PlanetRenderer.Draw(pb, _cam, b, ut, true, Ctx.Sb, Ctx.Textures.Body(b.TextureId),
                                    slopeOverlay: _slopeView);
                DrawPlainMarkers(pb, b, ut);
            }

            // body labels
            foreach (var b in u.Bodies)
            {
                var s = _cam.WorldToScreenD(b.AbsolutePositionAt(ut));
                if (!_cam.OnScreen(s, 0)) continue;
                double orbitPx = b.Parent == null ? double.MaxValue : b.Orbit.A / _cam.MetersPerPixel;
                if (orbitPx < 40) continue; // too cluttered at this zoom
                sb.DrawString(Ctx.Font, b.Name, new Vector2((float)s.X + 8, (float)s.Y - 18), b.BodyColor * 0.9f);
            }

            // vessel trajectory + markers
            bool alive = _vessel != null && !_vessel.Destroyed;
            if (alive && !_vessel.Landed && _pred != null)
            {
                var el = _pred.Orbit;
                Vec2d primaryAbs = _pred.Body.AbsolutePositionAt(ut);
                var trajColor = new Color(110, 220, 255);

                if (_pred.Type == TransitionType.None)
                {
                    OrbitRenderer.DrawConicGlow(pb, _cam, el, primaryAbs, trajColor, _pred.Body.SoiRadius);
                    OrbitRenderer.DrawDirectionArrows(pb, _cam, el, primaryAbs, trajColor);
                    DrawApPeMarkers(pb, sb, el, primaryAbs, _pred.Body.Radius, PeColor, ApColor);
                }
                else
                {
                    OrbitRenderer.DrawTrajectory(pb, _cam, el, primaryAbs, ut, _pred.TransitionUT, trajColor, 1.6f);
                    // transition marker
                    Vec2d mPos = primaryAbs + Kepler.StateAtTime(el, _pred.TransitionUT).pos;
                    var ms = _cam.WorldToScreen(mPos);
                    pb.CircleOutline(ms, 6, 1.5f, new Color(255, 200, 110));
                    if (!el.Hyperbolic && _pred.TransitionUT - ut > el.Period * 0.4)
                        DrawApPeMarkers(pb, sb, el, primaryAbs, _pred.Body.Radius, PeColor, ApColor);

                    // post-transition conic (ghost) around the next body at transition time
                    if (_pred.NextBody != null)
                    {
                        Vec2d nbAbs = _pred.NextBody.AbsolutePositionAt(_pred.TransitionUT);
                        var c2 = new Color(255, 170, 90);
                        OrbitRenderer.DrawConic(pb, _cam, _pred.NextOrbit, nbAbs, c2, 1.4f, _pred.NextBody.SoiRadius);
                        if (_pred.Type == TransitionType.Encounter)
                        {
                            // ghost of the body at encounter time
                            var gs = _cam.WorldToScreenD(nbAbs);
                            if (_cam.OnScreen(gs, 100))
                                pb.CircleOutline(new Vector2((float)gs.X, (float)gs.Y),
                                    (float)Math.Max(4, _pred.NextBody.Radius / _cam.MetersPerPixel), 1.2f, c2 * 0.8f);
                        }
                    }
                }
            }

            if (HasTarget) DrawTarget(pb, sb, ut);
            if (alive && _nodes.Count > 0) DrawManeuver(pb, sb, ut);

            foreach (var d in _debris)
                VesselRenderer.Draw(pb, _cam, d, ut, _anim, forceIcon: true);

            foreach (var ts in _others)
                VesselRenderer.Draw(pb, _cam, ts.V, ut, _anim, forceIcon: true);

            if (alive)
                VesselRenderer.Draw(pb, _cam, _vessel, ut, _anim, forceIcon: true);
        }

        /// <summary>Small green ticks on a body's rim at its guaranteed flat plains, so a safe landing
        /// site is visible in map view even before zooming in to read the slope shading.</summary>
        private void DrawPlainMarkers(PrimitiveBatch pb, Solar.Physics.CelestialBody b, double ut)
        {
            if (b.Terrain == null) return;
            double rPx = b.Radius / _cam.MetersPerPixel;
            if (rPx < 18 || rPx > 4000) return;   // too small to matter / too zoomed in (horizon view)
            Vec2d pos = b.AbsolutePositionAt(ut);
            Vector2 cs = _cam.WorldToScreen(pos);
            var col = new Color(90, 230, 120);
            foreach (double a in b.Terrain.PlainCenters)
            {
                Vec2d dir = Vec2d.FromAngle(a);
                Vector2 baseS = _cam.WorldToScreen(pos + dir * b.SurfaceRadiusAt(a));
                if (!_cam.OnScreen(new Vec2d(baseS.X, baseS.Y), 30)) continue;
                Vector2 outDir = baseS - cs;
                float l = outDir.Length();
                if (l < 1e-3f) continue;
                outDir /= l;
                pb.Line(baseS, baseS + outDir * 9f, 2f, col);
                pb.FillCircle(baseS + outDir * 13f, 3f, col);
            }
        }

        private static readonly Color TargetColor = new Color(235, 130, 235);

        /// <summary>Highlights the target's orbit, marks its current position, and (when a rendezvous
        /// solution exists) draws closest-approach markers on both orbits with a separation label.</summary>
        private void DrawTarget(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            // target body/vessel orbit
            if (TargetOrbitBody(ut, out var tEl, out var tPrimary))
                OrbitRenderer.DrawConic(pb, _cam, tEl, tPrimary.AbsolutePositionAt(ut), TargetColor * 0.8f, 1.4f);

            // current target position marker
            var tNow = _cam.WorldToScreenD(TargetPos(ut));
            if (_cam.OnScreen(tNow, 30))
            {
                var p = new Vector2((float)tNow.X, (float)tNow.Y);
                pb.CircleOutline(p, 9, 1.6f, TargetColor);
                pb.Line(p + new Vector2(-12, 0), p + new Vector2(12, 0), 1f, TargetColor * 0.7f);
                pb.Line(p + new Vector2(0, -12), p + new Vector2(0, 12), 1f, TargetColor * 0.7f);
                sb.DrawString(Ctx.Font, _targetName ?? "Target", p + new Vector2(12, 8), TargetColor);
            }

            // closest-approach markers
            if (_caValid && YourPlannedOrbit(ut, out var you, out var youPrimary))
            {
                Vec2d youCa = youPrimary.AbsolutePositionAt(_caUT) + Kepler.StateAtTime(you, _caUT).pos;
                Vec2d tgtCa = TargetCaPos(_caUT);
                var ys = _cam.WorldToScreen(youCa);
                var tsScreen = _cam.WorldToScreen(tgtCa);
                pb.Line(ys, tsScreen, 1f, new Color(180, 180, 200, 160));
                pb.FillCircle(ys, 4, new Color(120, 220, 255));
                pb.CircleOutline(tsScreen, 5, 1.5f, TargetColor);
                var mid = (ys + tsScreen) / 2;
                sb.DrawString(Ctx.Font, UiDraw.Dist(_caSep), mid + new Vector2(6, -6), new Color(200, 210, 230));
            }

            // geometric orbit-curve proximities: intersections ("Meet") + up-to-2 closest points
            var meet = new Color(255, 190, 70);
            var close = new Color(140, 230, 160);
            foreach (var p in _prox)
            {
                var ys = _cam.WorldToScreenD(p.YouPos);
                if (!_cam.OnScreen(ys, 40)) continue;
                var yp = new Vector2((float)ys.X, (float)ys.Y);
                if (p.Intersect)
                {
                    pb.CircleOutline(yp, 6, 1.8f, meet);
                    pb.Line(yp + new Vector2(-4, -4), yp + new Vector2(4, 4), 1.4f, meet);
                    pb.Line(yp + new Vector2(-4, 4), yp + new Vector2(4, -4), 1.4f, meet);
                    sb.DrawString(Ctx.Font, "Meet", yp + new Vector2(8, -8), meet);
                }
                else
                {
                    var ts2 = _cam.WorldToScreen(p.TgtPos);
                    pb.Line(yp, ts2, 1f, close * 0.6f);
                    pb.CircleOutline(yp, 5, 1.6f, close);
                    pb.FillCircle(ts2, 3, close * 0.85f);
                    sb.DrawString(Ctx.Font, UiDraw.Dist(p.Sep), yp + new Vector2(8, -8), close);
                }
            }
        }

        /// <summary>Target's absolute position at an arbitrary UT, propagated along its conic.</summary>
        private Vec2d TargetCaPos(double ut)
        {
            if (TargetOrbitBody(ut, out var tEl, out var tPrimary))
                return tPrimary.AbsolutePositionAt(ut) + Kepler.StateAtTime(tEl, ut).pos;
            return TargetPos(ut);
        }

        private void DrawApPeMarkers(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
                                     in OrbitalElements el, Vec2d primaryAbs, double bodyRadius, Color peCol, Color apCol)
        {
            var peW = OrbitRenderer.PeriapsisPoint(el, primaryAbs);
            var peS = _cam.WorldToScreenD(peW);
            if (_cam.OnScreen(peS, 50))
            {
                var p = new Vector2((float)peS.X, (float)peS.Y);
                pb.FillCircle(p, 4, peCol);
                sb.DrawString(Ctx.Font, $"Pe {UiDraw.Dist(el.Periapsis - bodyRadius)}", p + new Vector2(8, -8), peCol);
            }
            if (!el.Hyperbolic)
            {
                var apW = OrbitRenderer.ApoapsisPoint(el, primaryAbs);
                var apS = _cam.WorldToScreenD(apW);
                if (_cam.OnScreen(apS, 50))
                {
                    var p = new Vector2((float)apS.X, (float)apS.Y);
                    pb.FillCircle(p, 4, apCol);
                    sb.DrawString(Ctx.Font, $"Ap {UiDraw.Dist(el.Apoapsis - bodyRadius)}", p + new Vector2(8, -8), apCol);
                }
            }
        }

        private static readonly Color PeColor = new Color(120, 220, 255);
        private static readonly Color ApColor = new Color(170, 150, 255);

        /// <summary>Draws every planned node: chained orbit previews, Ap/Pe + encounter for the
        /// final patch, plus each node's marker, delta-v handles and X delete button. Nodes are
        /// consumed the moment their burn time passes, so only pending future nodes are ever drawn.</summary>
        private void DrawManeuver(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            var orange = new Color(255, 170, 90);

            // Planned conics across every SOI patch (the orange route). Pre-burn segments belong to the
            // live/predicted path (drawn cyan elsewhere), so only Planned segments are drawn here.
            var segs = BuildProjection(ut, refreshNodes: false);
            int lastPlanned = -1;
            for (int i = 0; i < segs.Count; i++) if (segs[i].Planned) lastPlanned = i;
            for (int i = 0; i < segs.Count; i++)
            {
                var sg = segs[i];
                if (!sg.Planned) continue;
                if (i == lastPlanned)
                {
                    OrbitRenderer.DrawConicGlow(pb, _cam, sg.El, sg.PrimaryAbs, orange, sg.Body.SoiRadius);
                    DrawApPeMarkers(pb, sb, sg.El, sg.PrimaryAbs, sg.Body.Radius, orange, new Color(255, 210, 140));
                }
                else
                {
                    OrbitRenderer.DrawConic(pb, _cam, sg.El, sg.PrimaryAbs, orange * 0.8f, 1.3f, sg.Body.SoiRadius);
                }
                // transition marker where this patch hands off to a different SOI
                if (i + 1 < segs.Count && segs[i + 1].Body != sg.Body && !double.IsInfinity(sg.EndUT))
                {
                    Vec2d mPos = sg.PrimaryAbs + Kepler.StateAtTime(sg.El, sg.EndUT).pos;
                    pb.CircleOutline(_cam.WorldToScreen(mPos), 6, 1.5f, orange);
                }
            }

            foreach (var node in Sorted(ut))
            {
                if (!node.HasSource || node.UT < ut) continue;
                if (!ManeuverGeometry(node, ut, out var nodeScreen, out var proDir, out var radDir)) continue;

                // burn-direction arrow + delta-v handles
                var (rN, vN) = Kepler.StateAtTime(node.Source, node.UT);
                Vec2d bd = node.BurnDelta(rN, vN);
                if (bd.Length > 1e-6)
                {
                    Vec2d u = bd.Normalized();
                    var dir = new Vector2((float)u.X, -(float)u.Y);
                    float len = (float)Math.Min(64, 18 + node.DeltaV * 0.05);
                    pb.Line(nodeScreen, nodeScreen + dir * len, 2f, PeColor);
                }

                Vector2[] hp = { nodeScreen + proDir * HandleDist, nodeScreen - proDir * HandleDist,
                                 nodeScreen + radDir * HandleDist, nodeScreen - radDir * HandleDist };
                Color[] hc = { new Color(120, 255, 140), new Color(255, 120, 110),
                               new Color(120, 210, 255), new Color(255, 200, 120) };
                int ni = _nodes.IndexOf(node);
                for (int k = 0; k < 4; k++)
                {
                    pb.Line(nodeScreen, hp[k], 1f, hc[k] * 0.5f);
                    float r = (_dragNode == ni && _dragHandle == k) || (_hoverNode == ni && _hoverHandle == k) ? 7f : 5f;
                    pb.FillCircle(hp[k], r, hc[k]);
                }

                // node marker
                pb.CircleOutline(nodeScreen, 8, 2f, Color.White);
                pb.FillCircle(nodeScreen, 3, PeColor);

                // X delete button
                var xp = XButtonPos(nodeScreen);
                bool xHot = _hoverX == _nodes.IndexOf(node);
                Color xc = xHot ? new Color(255, 120, 110) : new Color(220, 200, 200);
                pb.FillCircle(xp, 7, new Color(40, 20, 24, 220));
                pb.CircleOutline(xp, 7, 1.5f, xc);
                pb.Line(xp + new Vector2(-3, -3), xp + new Vector2(3, 3), 1.6f, xc);
                pb.Line(xp + new Vector2(-3, 3), xp + new Vector2(3, -3), 1.6f, xc);
            }
        }
    }
}

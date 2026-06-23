using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Solar.Core;
using Solar.Parts;
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
        private readonly ExhaustTrail _exhaust = new();
        private double _shake;                 // B4: decaying camera-shake amplitude (screen pixels)
        private readonly Random _shakeRnd = new(23);
        private double _stagePuffUt = double.NegativeInfinity;  // B4: one-shot decoupler smoke puff
        private Vec2d _stagePuffPos;

        private bool _map;
        private bool _slopeView;             // terrain colored by slope (landing-spot finder) instead of elevation

        // ---- TEMPORARY save/reload diagnostics (remove once report-4 revert is confirmed fixed) ----
        private static readonly string _dbgPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "solar_debug.log");
        private static void Dbg(string line) { try { System.IO.File.AppendAllText(_dbgPath, line + "\n"); } catch { } }
        private static string DbgShip(string tag, string name, double clockUT, Vessel v)
        {
            var o = v.Orbit;
            var ap = v.Body != null ? v.AbsolutePosition(clockUT) : v.Position;
            var av = v.Body != null ? v.AbsoluteVelocity(clockUT) : v.Velocity;
            return $"{tag} name={name} clockUT={clockUT:F3} onRails={v.OnRails} landed={v.Landed} " +
                   $"pos=({v.Position.X:F2},{v.Position.Y:F2}) abs=({ap.X:F2},{ap.Y:F2}) absV=({av.X:F2},{av.Y:F2}) heading={v.Heading:F3} " +
                   $"orbit[A={o.A:F2} E={o.E:F5} epoch={o.Epoch:F3} dEpoch={clockUT - o.Epoch:F3}]";
        }

        /// <summary>Frames remaining to dump tracked-ship positions after an undock (diagnostic).</summary>
        private int _dbgFrames;

        // map-view ship popup (click a ship -> Switch / Set as target)
        private Vessel _menuVessel;
        private string _menuName;
        private bool _menuControllable;

        // flight-view part popup (click a part -> info panel; a decoupler can be fired from here).
        // _partHits is rebuilt each frame by VesselRenderer.Draw; the click test reads last frame's quads.
        private readonly List<(Part part, Vector2[] quad)> _partHits = new();
        private Part _popupPart;
        private Vector2 _popupPos;
        private Rectangle _popupCloseRect, _popupDecoupleRect;
        private Rectangle _popupEngineToggleRect, _popupPowerRect;   // engine on/off + power slider
        private Rectangle _popupIgniteRect, _popupChuteRect;          // ignite an engine / deploy-cut a chute
        private bool _powerDragging;
        private Vector2 _menuPos;
        private double _flightZoom = 0.35;   // m per pixel
        private double _mapZoom;
        private int _focus;                  // 0 = vessel, otherwise body index + 1
        private Vec2d _mapPan;               // map-view arrow-key pan offset (world meters), added on top of the focus
        // "Focus the encounter": when set, the map centers on this body at its (fixed) transition time
        // so the tiny inside-SOI flyby leg fills the screen and a capture node can be dropped on it.
        // Refreshed each frame from the current projection while active; cleared by the normal focus cycle.
        private CelestialBody _encFocusBody;
        private double _encFocusUT;

        // ----- target / rendezvous -----
        private CelestialBody _targetBody;   // one of _targetBody / _targetVessel is non-null when targeting
        private Vessel _targetVessel;
        private string _targetName;
        private bool _showTargetWindow;
        private bool _showCrew;              // in-flight crew roster / transfer panel
        private int _rightColBottom;         // screen-Y below the HUD's right-column stack (set each Draw)
        private bool _showColony;            // colony / surface-base management panel (landed only)
        private bool _showAddModule;         // colony "add module" sub-list
        private bool _buildAtColony;         // deferred: open the editor to build a vessel at this base
        private bool _caValid;               // closest-approach solution for this frame
        private double _caUT, _caSep, _caRelSpeed;
        // The planned patch the closest-approach was measured on (may be a pre-encounter leg around a
        // different body than the final planned conic), plus the target-position function used, so the
        // on-map approach markers render against exactly what the readout reports.
        private OrbitalElements _caYouEl;
        private CelestialBody _caYouPrimary;
        private Func<double, Vec2d> _caTpos;
        private double _caStart;             // search-window start the approach/arrival times anchor to
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
        private double _dragFineMult = 1.0;  // active handle-drag sensitivity factor (Alt/Alt+Shift = finer)
        private double _burnSpent;          // delta-v consumed against the current (nearest future) node
        private double _burnTargetUT = double.NaN;  // UT of the node _burnSpent is keyed to
        private double? _warpTo;            // target UT for an active "warp to maneuver"
        private string _endMessage;
        private string _toast;               // transient milestone notification
        private double _toastT;              // seconds remaining on the toast
        private readonly System.Random _rng = new();   // drives probabilistic threats (malfunctions/illness)
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
                Colony.TryGrowCrew(_vessel, Ctx.State, Ctx.Clock.UT - _resume.LastUT);
                Dbg(DbgShip("LOAD ", _shipName, Ctx.Clock.UT, _vessel));
                // Only re-derive position from the conic when the world has advanced past the save; otherwise
                // the saved Position is authoritative -- re-deriving it reverts the ship if Position/Orbit
                // disagree, or if the clock sits at/just before the orbit epoch.
                if (_vessel.OnRails && Ctx.Clock.UT > _vessel.Orbit.Epoch + 1e-6) _vessel.UpdateFromRails(Ctx.Clock.UT);
                Dbg(DbgShip("LOADpost", _shipName, Ctx.Clock.UT, _vessel));
                _lastBody = b;
                _lastCamRel = _vessel.Position;
                _mapZoom = b.SoiRadius * 2.4 / Math.Min(Ctx.W, Ctx.H);
                if (!_vessel.Landed && !_vessel.Destroyed) RefreshPrediction(Ctx.Clock.UT);
                SpawnOthers();
                RestoreTarget(_resume.TargetName);
                _dbgFrames = 5;   // dump the active vessel + near others for the first frames after resume
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
                Colony.TryGrowCrew(v, Ctx.State, Ctx.Clock.UT - s.LastUT);
                // skip a ship sitting on the *same spot* as the active vessel (e.g. another ship saved landed
                // on the launch pad): it would render right on top of ours and read as stray parts. Kept tight
                // (true co-location) so a craft built a few metres beside a base still shows and can connect.
                // It's still persisted by PersistOthers, so this only suppresses the overlapping spawn/render.
                if (_vessel != null && v.Body == _vessel.Body && v.Landed && _vessel.Landed
                    && (v.Position - _vessel.Position).Length < 8.0) continue;
                if (!v.Landed && !v.OnRails) v.GoOnRails(Ctx.Clock.UT);
                _others.Add(new TrackedShip { Name = s.Name, V = v });
                Dbg(DbgShip("LOAD-other", s.Name, Ctx.Clock.UT, v));
            }
        }

        /// <summary>Snapshot the active ship into the savegame (dropping it if destroyed).</summary>
        private void PersistShip()
        {
            if (_vessel == null) return;
            double ut = Ctx.Clock.UT;
            // Mirror PersistOthers: a vessel held off rails only by close-proximity physics (KSP-style)
            // has a stale Orbit; re-derive it from the live state so the save is self-consistent and
            // reloads/propagates like every other ship. Don't disturb a genuine physics state (burn/atmo).
            if (!_vessel.Landed && !_vessel.Destroyed && _vessel.Body != null)
            {
                var body = _vessel.Body;
                bool inAtmo = body.Atmo != null && _vessel.Altitude < body.Atmo.Top + 500;
                bool thrusting = _vessel.CurrentThrust > 0;
                // off-rails only by close-proximity physics? convert to rails so the save self-propagates.
                if (!_vessel.OnRails && !inAtmo && !thrusting && !_vessel.RcsActive && !_vessel.RadialThrusting) _vessel.GoOnRails(ut);
                // otherwise keep the live state authoritative, but refresh the stored conic from it so an
                // on-rails save is never stale (a saved-off-rails burn/atmo ship reloads from Position).
                else _vessel.Orbit = Kepler.ElementsFromState(_vessel.Position, _vessel.Velocity, body.Mu, ut);
            }
            Ctx.State.UT = ut;
            Ctx.State.UpsertShip(ShipState.From(_vessel, _shipName, _nodes, _targetName, ut));
            Dbg(DbgShip("SAVE ", _shipName, ut, _vessel));
        }

        /// <summary>Snapshot every tracked co-orbiting ship back into the savegame (so undocked modules
        /// and stations the player isn't currently flying are saved on exit too).</summary>
        private void PersistOthers()
        {
            foreach (var ts in _others)
            {
                var v = ts.V;
                if (v.Destroyed) { Ctx.State.RemoveShip(ts.Name); continue; }
                if (!v.Landed && v.Body != null)
                {
                    if (!v.OnRails) v.GoOnRails(Ctx.Clock.UT);
                    else v.Orbit = Kepler.ElementsFromState(v.Position, v.Velocity, v.Body.Mu, Ctx.Clock.UT);
                }
                Ctx.State.UpsertShip(ShipState.From(v, ts.Name, ut: Ctx.Clock.UT));
                Dbg(DbgShip("SAVE-other", ts.Name, Ctx.Clock.UT, v));
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

        /// <summary>The node whose info the HUD panel shows: the next (future) burn target if any,
        /// otherwise the most recently reached node so its burn time / dV / timing stay visible (in a
        /// "done" state) until the player deletes it. Burn tracking and warp keep using <see cref="NextNode"/>.</summary>
        private Maneuver DisplayNode(double ut)
        {
            var next = NextNode(ut);
            if (next != null) return next;
            Maneuver last = null;
            foreach (var n in _nodes)
                if (n.Reached && (last == null || n.UT > last.UT)) last = n;
            return last;
        }

        // Reused by Sorted()/BuildProjection so the per-frame node walk doesn't allocate. Both are only
        // ever read sequentially within one frame (no re-entrant Sorted/BuildProjection call iterates one
        // while another rebuilds it), so a shared scratch buffer is safe. Re-sorting a handful of nodes is
        // cheaper than the old per-call List copy, and stays correct when a node's UT is dragged in place.
        private readonly List<Maneuver> _sortedScratch = new List<Maneuver>();
        private readonly List<Maneuver> _futureScratch = new List<Maneuver>();
        private static readonly Comparison<Maneuver> ByUT = (a, b) => a.UT.CompareTo(b.UT);

        // The planned-trajectory projection is needed several times per frame (node refresh, closest
        // approach, hit-testing, drawing) and is expensive for long-horizon orbits (each rebuild runs
        // TrajectoryPredictor.Predict per patch). It only depends on the live conic, its body, the node set,
        // and whether we are thrusting — none of which change while coasting on rails. So cache by a content
        // signature and rebuild only when that signature changes, or when time-warp carries us past the next
        // pending burn / SOI transition. _projSig == NaN forces a rebuild (set when an edit changes nodes).
        private List<ProjSegment> _projSegs = new List<ProjSegment>();
        private double _projSig = double.NaN;
        private double _projNextEventUT = double.PositiveInfinity;

        /// <summary>Nodes sorted by time. Returns a shared scratch list — iterate it immediately; do not
        /// retain it across another Sorted()/BuildProjection call.</summary>
        private List<Maneuver> Sorted(double ut)
        {
            _sortedScratch.Clear();
            _sortedScratch.AddRange(_nodes);
            _sortedScratch.Sort(ByUT);
            return _sortedScratch;
        }

        /// <summary>One drawn/clickable conic of the planned trajectory, in its own primary's frame.</summary>
        private readonly struct ProjSegment
        {
            public ProjSegment(in OrbitalElements el, CelestialBody body, Vec2d primaryAbs,
                               double startUT, double endUT, bool closed, bool planned, bool liveFrame)
            { El = el; Body = body; PrimaryAbs = primaryAbs; StartUT = startUT; EndUT = endUT; Closed = closed; Planned = planned; LiveFrame = liveFrame; }

            /// <summary>A copy with a new primary anchor — used to re-anchor live-frame segments to the
            /// body's current position each frame without rebuilding the (cached) conic.</summary>
            public ProjSegment WithPrimary(Vec2d p) => new ProjSegment(El, Body, p, StartUT, EndUT, Closed, Planned, LiveFrame);
            public readonly OrbitalElements El;
            public readonly CelestialBody Body;
            public readonly Vec2d PrimaryAbs;   // body's absolute position snapshot at StartUT
            public readonly double StartUT;
            public readonly double EndUT;        // transition / node time, or +inf if the segment is a full orbit
            public readonly bool Closed;         // a full ellipse (no transition ends it)
            public readonly bool Planned;        // lies after at least one burn (drawn as the orange plan)
            public readonly bool LiveFrame;      // still in the live body's SOI (pre-transition): re-anchored to "now" each frame
        }

        /// <summary>Walk the planned trajectory through SOI transitions and pending-node burns (KSP-style
        /// patched conics), producing the ordered conic segments that are both drawn and click-hit-tested.
        /// When <paramref name="refreshNodes"/> is set, each future node's Source/Body frame is assigned
        /// here (skipped during a burn so the planned orbit stays put).</summary>
        private List<ProjSegment> BuildProjection(double ut, bool refreshNodes)
        {
            var segs = new List<ProjSegment>();
            if (!LiveOrbit(ut, out var live, out var liveBody, out _)) return segs;

            var future = _futureScratch;
            future.Clear();
            foreach (var n in Sorted(ut)) if (n.UT >= ut) future.Add(n);

            OrbitalElements src = live;
            CelestialBody body = liveBody;
            double segStart = ut;
            double bodyRefUT = ut;   // time the current body's SOI was entered: the conic's draw origin
            bool planned = false;    // becomes true once a node burn has been applied
            bool liveFrame = true;   // still in the live body's SOI: anchor tracks "now", flipped off after an SOI transition
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
                    segs.Add(new ProjSegment(src, body, body.AbsolutePositionAt(bodyRefUT), segStart, node.UT, false, planned, liveFrame));
                    if (refreshNodes) { node.Source = src; node.Body = body; node.HasSource = true; node.FrameUT = bodyRefUT; }
                    // Anchor the planned orbit to the node's frozen pre-burn source so it stays exactly
                    // where the user set it. While coasting Source is refreshed each frame (== src here);
                    // during a burn it is held frozen, so the orange orbit no longer drifts as the live
                    // orbit rises to meet it.
                    OrbitalElements res = node.HasSource
                        ? node.ResultOrbit(node.Source, body.Mu)
                        : node.ResultOrbit(src, body.Mu);
                    ni++;
                    if (double.IsNaN(res.A)) break;
                    src = res; segStart = node.UT; planned = true;   // same body: bodyRefUT unchanged
                    continue;
                }

                bool hasTrans = !double.IsInfinity(transUT);
                segs.Add(new ProjSegment(src, body, body.AbsolutePositionAt(bodyRefUT), segStart,
                                         hasTrans ? transUT : double.PositiveInfinity, !hasTrans, planned, liveFrame));
                if (!hasTrans) break;
                src = pred.NextOrbit; body = pred.NextBody; segStart = pred.TransitionUT; bodyRefUT = pred.TransitionUT;
                liveFrame = false;   // past an SOI transition: this body's anchor is its fixed transition time
            }
            return segs;
        }

        /// <summary>The planned-trajectory projection for this frame, built once and reused. The first
        /// build of the frame (from UpdateManeuverInput) passes refreshNodes:true to re-chain pending
        /// nodes across SOI transitions and reassign each node's frozen Source; later consumers reuse
        /// the cache. The cache is invalidated by resetting <see cref="_projSig"/> when nodes change.</summary>
        private List<ProjSegment> Projection(double ut, bool refreshNodes)
        {
            double sig = ProjectionSig(ut);
            if (double.IsNaN(_projSig) || sig != _projSig || ut >= _projNextEventUT)
            {
                _projSegs = BuildProjection(ut, refreshNodes);
                _projSig = sig;
                // Earliest segment end (a node burn or SOI transition) still ahead: rebuild once warp passes it.
                _projNextEventUT = double.PositiveInfinity;
                foreach (var s in _projSegs)
                    if (s.EndUT > ut && s.EndUT < _projNextEventUT) _projNextEventUT = s.EndUT;
            }
            // Re-anchor live-frame segments to the body's CURRENT position every call. The cache holds the
            // (rails-stable) conic shapes, but the body moves as time/warp advances and the map camera tracks
            // it (UpdateCamera) — a baked anchor would make the planned (orange) route drift off its node
            // marker until the node is reached. Cheap: a handful of structs, memoized AbsolutePositionAt, no
            // TrajectoryPredictor work and no allocation (in-place struct replace).
            for (int i = 0; i < _projSegs.Count; i++)
            {
                var sg = _projSegs[i];
                if (sg.LiveFrame) _projSegs[i] = sg.WithPrimary(sg.Body.AbsolutePositionAt(ut));
            }
            return _projSegs;
        }

        /// <summary>Content signature of everything the projection depends on: the live conic, its body, the
        /// node set, and the on-rails flag. While coasting on rails this is stable frame to frame, so the
        /// (expensive) projection is built once and reused.</summary>
        private double ProjectionSig(double ut)
        {
            double s = NodeSignature();
            if (LiveOrbit(ut, out var live, out var body, out _))
            {
                s += live.A * 1.0 + live.E * 31.0 + live.ArgPe * 131.0 + live.Dir * 7.0;
                if (body != null) s += body.GetHashCode() * 1e-3;
            }
            if (_vessel != null && !_vessel.OnRails) s += 1e9;   // off-rails: rebuild each frame, freeze node Source
            return s;
        }

        /// <summary>The route's SOI encounters (descents into a child body's SOI): the body entered, the
        /// fixed transition time the inside-SOI leg is drawn around, and the SOI-entry world point (where
        /// the encounter marker sits). Drives the encounter ghost/marker drawing and the "focus the
        /// encounter" affordance so a capture node can be planned on the otherwise-tiny flyby leg.</summary>
        private IEnumerable<(CelestialBody body, double transUT, Vec2d entryPos)> PlannedEncounters(double ut)
        {
            var segs = Projection(ut, refreshNodes: false);
            for (int i = 0; i + 1 < segs.Count; i++)
            {
                var a = segs[i]; var b = segs[i + 1];
                if (b.Body != a.Body && b.Body.Parent == a.Body && !double.IsInfinity(a.EndUT))
                    yield return (b.Body, a.EndUT, a.PrimaryAbs + Kepler.StateAtTime(a.El, a.EndUT).pos);
            }
        }

        /// <summary>The earliest route encounter (descent into a child SOI) as its flyby conic + body, or
        /// false. Covers both the live predicted encounter and any planned-node encounter (both appear as
        /// SOI transitions in the projection). Feeds the HUD's flyby-periapsis danger readout.</summary>
        private bool RouteFlyby(double ut, out OrbitalElements el, out CelestialBody body)
        {
            el = default; body = null;
            var segs = Projection(ut, refreshNodes: false);
            for (int i = 0; i + 1 < segs.Count; i++)
                if (segs[i + 1].Body.Parent == segs[i].Body && !double.IsNaN(segs[i + 1].El.A))
                { el = segs[i + 1].El; body = segs[i + 1].Body; return true; }
            return false;
        }

        /// <summary>Center + zoom the map on an encounter so its inside-SOI flyby leg fills the screen.</summary>
        private void FocusEncounter(CelestialBody body, double transUT)
        {
            _encFocusBody = body; _encFocusUT = transUT; _mapPan = default;
            double screen = Math.Max(200, Math.Min(_cam.ScreenW, _cam.ScreenH));
            _mapZoom = Math.Clamp(2 * body.SoiRadius / (0.6 * screen), 50, 2e9);  // SOI diameter ~ 60% of view
        }

        /// <summary>Whether the mouse is over a route encounter marker; yields the body + transition time.</summary>
        private bool ClickedEncounterMarker(double ut, Vector2 mouse, out CelestialBody body, out double transUT)
        {
            body = null; transUT = 0; float best = 12f;
            foreach (var (eb, et, entry) in PlannedEncounters(ut))
            {
                float d = Vector2.Distance(mouse, _cam.WorldToScreen(entry));
                if (d < best) { best = d; body = eb; transUT = et; }
            }
            return body != null;
        }

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
            if (inp.Pressed(Keys.Escape)) { if (_popupPart != null) _popupPart = null; else _showExitDialog = !_showExitDialog; }
            if (_showExitDialog) return;
            if (inp.Pressed(Keys.M)) { _map = !_map; _mapPan = default; }   // recenter on view switch
            if (inp.Pressed(Keys.N)) _slopeView = !_slopeView;   // toggle the slope (landing-spot) overlay
            if (inp.Pressed(Keys.Tab)) CycleTarget(inp.Down(Keys.LeftShift) || inp.Down(Keys.RightShift) ? -1 : 1);
            if (inp.Pressed(Keys.T)) _showTargetWindow = !_showTargetWindow;
            if (inp.Pressed(Keys.V)) TakeControlOfTarget();
            if (inp.Pressed(Keys.U)) Undock();
            if (inp.Pressed(Keys.P)) ConfirmDock(clock.UT);      // confirm an orbital dock or surface connection (P = Port; K is RCS)
            if (inp.Pressed(Keys.C)) _showCrew = !_showCrew;
            // colony / base management, available while landed
            if (inp.Pressed(Keys.B) && _vessel != null && _vessel.Landed && !_vessel.Destroyed)
            { _showColony = !_showColony; if (!_showColony) _showAddModule = false; }
            if (!(_vessel != null && _vessel.Landed)) { _showColony = false; _showAddModule = false; }
            if (_buildAtColony) { _buildAtColony = false; BuildAtColony(); return; }
            if (_map && inp.Pressed(Keys.F)) { _focus = (_focus + 1) % (Ctx.Universe.Bodies.Count + 1); _mapPan = default; _encFocusBody = null; }
            // arrow-key map panning: constant screen-pixel speed (scaled by zoom). Screen Y is flipped,
            // so Down moves the view content up -> negative world Y.
            if (_map)
            {
                const double panPxPerSec = 420;
                double panStep = panPxPerSec * realDt * _mapZoom;
                if (inp.Down(Keys.Right)) _mapPan.X += panStep;
                if (inp.Down(Keys.Left)) _mapPan.X -= panStep;
                if (inp.Down(Keys.Up)) _mapPan.Y += panStep;
                if (inp.Down(Keys.Down)) _mapPan.Y -= panStep;
            }

            // map-view ship popup is handled first so its clicks don't fall through to node editing;
            // the flight-view part popup is the analogous click-handler for the non-map view
            bool menuConsumed = UpdateShipMenu(clock.UT);
            UpdatePartPopup(clock.UT);
            bool maneuverWheel = false;
            UpdateManeuverInput(clock.UT, menuConsumed, out maneuverWheel);

            if (inp.WheelDelta != 0 && !maneuverWheel)
            {
                double factor = Math.Pow(1.18, -inp.WheelDelta / 120.0);
                // max m/px (2e9) frames the whole system out to Neptune (~4.5e11 m) with margin even when
                // the camera is focused on the vessel/an inner planet rather than the Sun
                if (_map) _mapZoom = Math.Clamp(_mapZoom * factor, 50, 2e9);
                // min m/px (max zoom-in) capped at ~100 px/m so part textures (~64-135 px/m native) stay crisp
                else _flightZoom = Math.Clamp(_flightZoom * factor, 0.01, 400);
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
                // translation command: Q/E left-right (drives the off-axis lateral thrusters and RCS) is
                // read even while landed so the thruster responds the instant the craft lifts off; the I/K
                // fore-aft RCS and the rotation model below are flight-only.
                double rx = 0, ry = 0;
                if (inp.Down(Keys.E)) rx += 1;
                if (inp.Down(Keys.Q)) rx -= 1;
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
                    // arrows pan the map view, so only A/D rotate there; both rotate in flight view
                    bool left = inp.Down(Keys.A) || (!_map && inp.Down(Keys.Left));
                    bool right = inp.Down(Keys.D) || (!_map && inp.Down(Keys.Right));

                    if (left ^ right)
                    {
                        double dir = left ? 1 : -1;          // +CCW
                        _vessel.AngularVelocity = Math.Clamp(_vessel.AngularVelocity + dir * alpha * realDt, -maxRate, maxRate);
                        _sas = SasMode.Off;                  // manual input releases the hold
                    }
                    else if (ChuteAttitudeTarget() is double chuteHold)
                    {
                        // deployed chutes weathervane the craft aerodynamically (no power needed, scaled by
                        // dynamic pressure): a single chute points its end retrograde (KSP-style); balanced
                        // top+bottom chutes hold the craft broadside for a horizontal descent / landing.
                        double diff = Kepler.WrapPi(chuteHold - _vessel.Heading);
                        double aero = Math.Clamp(_vessel.DynamicPressure / 400.0, 0, 1);   // 0 in thin air -> full near the ground
                        double target = Math.Clamp(diff * 2.5, -maxRate, maxRate);
                        _vessel.AngularVelocity += (target - _vessel.AngularVelocity) * Math.Min(1, 3.0 * aero * realDt);
                        _vessel.AngularVelocity *= Math.Max(0, 1 - 0.8 * aero * realDt);   // damp the oscillation
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

                    // RCS fore-aft translation: I/K along the Up axis (left-right Q/E is read above)
                    if (inp.Down(Keys.I)) ry += 1;
                    if (inp.Down(Keys.K)) ry -= 1;
                }
                _vessel.RcsCommand = new Vec2d(rx, ry);
                if (inp.Pressed(Keys.Space)) FireNextStage();
            }

            // ---------- simulation ----------
            if (alive)
            {
                var body = _vessel.Body;
                bool inAtmo = body.Atmo != null && _vessel.Altitude < body.Atmo.Top + 500;
                bool thrusting = _vessel.CurrentThrust > 0;
                // A ship in close proximity is RK4-integrated; integrate the focused vessel the same
                // way so the pair stays rigidly co-located (matching an analytic conic against RK4
                // makes them slowly drift and jump across rails round-trips at pause/scene boundaries).
                bool needsPhysics = !_vessel.Landed && (thrusting || inAtmo || _vessel.RcsActive || _vessel.RadialThrusting || AnyOtherNear());

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
                            _vessel.HasLeftLaunchSite = true;
                            _vessel.OnRails = false;
                            _endMessage = null;
                        }
                    }
                }
                else if (needsPhysics)
                {
                    // frame-start ut: keep the exact live state if still at the conic epoch (just loaded),
                    // then the substeps below advance exactly one frame -- matching the tracked others.
                    if (_vessel.OnRails) LeaveRails(_vessel, clock.UT);
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
                    // Keep the stored conic synced to the live state every off-rails frame. Otherwise
                    // Orbit holds the stale pre-physics conic (it's only rewritten by GoOnRails), and any
                    // later reader of it -- a save, GoOffRails, a reload -- snaps the ship back onto that
                    // old conic (e.g. undoing an RCS nudge during a rendezvous).
                    if (!_vessel.Destroyed && !_vessel.Landed)
                    {
                        _vessel.Orbit = Kepler.ElementsFromState(_vessel.Position, _vessel.Velocity, _vessel.Body.Mu, clock.UT);
                        // Recompute every physics frame so the drawn conic tracks the live state instead of
                        // lagging and snapping (most visible mid-burn / at the 4x physics-warp cap).
                        _pred = TrajectoryPredictor.Predict(_vessel.CurrentElements(clock.UT), _vessel.Body, clock.UT);
                    }
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
                    // "Warp to maneuver" stops a comfortable lead before the burn; honour it BEFORE the
                    // burn-start safety stop below, or a single huge max-warp step that overshoots both would
                    // land on the burn start (nodeStop) instead, eating the lead. warpTarget < nodeStop always.
                    else if (_warpTo.HasValue && _warpTo.Value <= target)
                    {
                        clock.UT = _warpTo.Value;
                        _warpTo = null;
                        clock.DropToRealtime();
                    }
                    else if (nodeStop > clock.UT && nodeStop <= target)
                    {
                        clock.UT = nodeStop;
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
                Threats.Tick(_vessel, sdt, clock.UT, Ctx.Universe, _rng);
                if (_vessel.IsColony && _vessel.Landed && Colony.TryGrowCrew(_vessel, Ctx.State, sdt) > 0)
                { _toast = "A colonist was born at the base"; _toastT = 5; }
                _lastBody = _vessel.Body;
                _lastCamRel = _vessel.Position;
            }
            else
            {
                clock.MaxWarpIndex = SimClock.Levels.Length - 1;
                clock.UT += realDt * clock.Warp;
            }

            // maneuver burn tracking: count delta-v spent against the current node. Once a node's burn
            // time passes it becomes a frozen reference plot (the plan the player set), NOT auto-cancelled:
            // mark it reached and snapshot its absolute world position at its own time so the node + its
            // orange orbit stay drawn (and correctly placed across later orbit/SOI changes) until the player
            // deletes it. NextNode/burn tracking only look at future nodes, so a reached node never re-arms.
            foreach (var n in _nodes)
                if (n.HasSource && !n.Reached && n.UT < clock.UT && n.Body != null)
                {
                    n.Reached = true;
                    n.ReachedAbsPos = n.Body.AbsolutePositionAt(n.UT) + Kepler.StateAtTime(n.Source, n.UT).pos;
                }

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
            TrySurveyBody(clock.UT);
            // threat events this tick: repairs (low priority), then malfunctions, then deaths (highest).
            if (_vessel != null && _vessel.RecentRepairs.Count > 0)
            {
                _toast = $"Repaired: {string.Join(", ", _vessel.RecentRepairs)}";
                _toastT = 5;
                _vessel.RecentRepairs.Clear();
            }
            if (_vessel != null && _vessel.RecentFailures.Count > 0)
            {
                _toast = $"Malfunction: {string.Join(", ", _vessel.RecentFailures)}";
                _toastT = 6;
                _vessel.RecentFailures.Clear();
            }
            // crew that died this tick (life support, radiation or illness) overrides other toasts.
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

        /// <summary>Take a vessel off rails for physics. If the conic is still at its epoch (just loaded or
        /// just synced) the live Position/Velocity are exact — keep them; only catch up from the conic when
        /// real time has elapsed past the epoch. Caller must not also integrate the same frame after this
        /// (the vessel is already at <paramref name="ut"/>; integrating again overshoots by one step).</summary>
        private static void LeaveRails(Vessel v, double ut)
        {
            if (ut > v.Orbit.Epoch + 1e-6) v.GoOffRails(ut);   // stale conic: propagate to now
            else v.OnRails = false;                            // fresh: preserve the exact live state
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
                    if (v.IsColony && sdt > 0) { v.UpdateResources(sdt, ut, Ctx.Universe); Colony.TryGrowCrew(v, Ctx.State, sdt); }
                    continue;
                }

                if (physWarp && OtherNear(v))
                {
                    bool integrate = true;
                    if (v.OnRails)
                    {
                        // Stale conic (coasting other just entered range): snap it to the current ut and do
                        // NOT integrate this frame -- it's already at ut, so integrating would push it ~one
                        // step past the active vessel. Fresh conic at its epoch (just loaded): keep the exact
                        // live state and integrate this frame exactly like the active vessel, so a reloaded
                        // close formation stays put to the bit (this is the save/reload "gap reopens" bug).
                        if (ut > v.Orbit.Epoch + 1e-6) { v.GoOffRails(ut); integrate = false; }
                        else v.OnRails = false;
                    }
                    if (integrate)
                    {
                        int steps = Math.Clamp((int)Math.Ceiling(sdt / SubstepDt), 1, 32);
                        double h = sdt / steps;
                        for (int s = 0; s < steps; s++) Integrator.Step(v, h);
                        // Keep the stored conic synced to the live state, exactly as the active vessel does
                        // (see the comment near _vessel's Orbit re-sync): otherwise a later GoOffRails / save /
                        // reload snaps this ship back onto the stale pre-physics conic, reopening a closed gap.
                        v.Orbit = Kepler.ElementsFromState(v.Position, v.Velocity, v.Body.Mu, ut);
                    }
                }
                else
                {
                    if (!v.OnRails) v.GoOnRails(ut);
                    v.UpdateFromRails(ut);
                }
                if (_dbgFrames > 0) Dbg(DbgShip("OTHER-frame", ts.Name, ut, v));
            }
            if (_dbgFrames > 0)
            {
                if (_vessel != null) Dbg(DbgShip("ACTIVE-frame", _shipName, ut, _vessel));
                _dbgFrames--;
            }
        }

        /// <summary>Port-to-port distance for an orbital dock and the closing-speed limit (soft dock), plus
        /// the (looser) center range for connecting two craft parked on a surface into a compound base.</summary>
        private const double PortDockDist = 1.0;   // meters between the two nearest free docking ports
        private const double SoftDockSpeed = 2.5;
        private const double ConnectDist = 75.0;   // hand-landing tolerance: park within this of a base to connect

        /// <summary>A surface-connect candidate found this frame: a landed ship in connect range that the
        /// player can join into a base by pressing the connect key. Null when none is in range.</summary>
        private TrackedShip _surfaceConnect;

        /// <summary>An orbital-dock candidate found this frame: a tracked ship whose nearest free port is
        /// within capture distance of ours and is closing slowly. The player confirms it with [P].</summary>
        private struct DockCandidate { public TrackedShip Ship; public Part Mine; public Part Theirs; }
        private DockCandidate? _dockCandidate;

        /// <summary>Surface every eligible dock / surface connection as a candidate the player confirms with
        /// a key — nothing welds together automatically. Orbital docking keys off the two nearest free
        /// docking ports being within <see cref="PortDockDist"/> and closing slower than the soft-dock speed.</summary>
        private void TryDock(double ut)
        {
            _surfaceConnect = null;
            _dockCandidate = null;
            if (_vessel == null || _vessel.Destroyed || !_vessel.HasFreeDockingPort) return;
            foreach (var ts in _others)
            {
                var o = ts.V;
                if (o.Destroyed || o.Body != _vessel.Body || !o.HasFreeDockingPort) continue;

                if (_vessel.Landed && o.Landed)
                {
                    // surface connection: don't merge automatically — record it for a confirmed [P] connect
                    if ((o.Position - _vessel.Position).Length < ConnectDist) { _surfaceConnect = ts; return; }
                    continue;
                }
                if (_vessel.Landed || o.Landed) continue;   // can't dock a flying craft to a landed one
                if ((o.Velocity - _vessel.Velocity).Length >= SoftDockSpeed) continue;
                var (mine, theirs, d) = Vessel.ClosestFreePortPair(_vessel, o, ut);
                if (mine == null || d > PortDockDist) continue;
                _dockCandidate = new DockCandidate { Ship = ts, Mine = mine, Theirs = theirs };
                return;
            }
        }

        /// <summary>Confirm a pending dock — orbital (two ports within range) or surface (two landed craft).
        /// The [P] action.</summary>
        private void ConfirmDock(double ut)
        {
            if (_dockCandidate is DockCandidate dc)
            {
                _dockCandidate = null;
                var ts = dc.Ship;
                if (ts.V == null || ts.V.Destroyed || _vessel == null || _vessel.Destroyed) return;
                if (PortOccupiedOrGone(_vessel, dc.Mine) || PortOccupiedOrGone(ts.V, dc.Theirs)) return;
                DoDock(ts, ts.V, dc.Mine, dc.Theirs, false, ut);
                return;
            }
            if (_surfaceConnect != null)
            {
                var ts = _surfaceConnect; _surfaceConnect = null;
                if (ts.V == null || ts.V.Destroyed || !_vessel.Landed || !ts.V.Landed) return;
                if (!_vessel.HasFreeDockingPort || !ts.V.HasFreeDockingPort) return;
                DoDock(ts, ts.V, _vessel.FirstFreeDockingPort(), ts.V.FirstFreeDockingPort(), true, ut);
            }
        }

        private static bool PortOccupiedOrGone(Vessel v, Part port)
        {
            if (port == null) return true;
            foreach (var p in v.FreeDockingPorts()) if (p == port) return false;
            return true;
        }

        /// <summary>Merge a tracked ship into the active vessel as one compound vessel, dropping it from the
        /// tracked/saved set. Shared by an orbital dock and a surface connection. The docked module is placed
        /// so the two chosen ports overlap, at the approach orientation snapped to the nearest 90 degrees.</summary>
        private void DoDock(TrackedShip ts, Vessel o, Part myPort, Part theirPort, bool surface, double ut)
        {
            if (_vessel.OnRails) _vessel.GoOffRails(ut);
            // snap the relative heading to the nearest quarter turn, then offset the docked module so its
            // port center lands on ours (the two ports overlap)
            int q = ((int)Math.Round((o.Heading - _vessel.Heading) / (Math.PI / 2))) & 3;
            Vec2d offset = _vessel.PartLocalCenter(myPort) - Vessel.RotQuarter(q, o.PartLocalCenter(theirPort));
            if (!surface)
            {
                // conserve momentum into the merged rigid body (landed craft are already at rest)
                double m1 = _vessel.TotalMass, m2 = o.TotalMass;
                _vessel.Velocity = (_vessel.Velocity * m1 + o.Velocity * m2) / (m1 + m2);
            }
            _vessel.DockWith(o, myPort, theirPort, q, offset);
            _vessel.MissionCancelable = false;   // docked with another ship: lock out mission cancel
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
            bool parentOnRails = _vessel.OnRails;
            if (_vessel.OnRails) _vessel.GoOffRails(ut);
            var detached = _vessel.Undock();
            if (detached == null) return;
            // Keep the freshly-detached module on the SAME footing as the active vessel: when we're flying
            // off-rails (the normal proximity/rendezvous case), leave it off-rails so UpdateOthers RK4-
            // integrates the pair together and they stay rigidly co-located. Round-tripping it onto an
            // analytic conic built from its off-center point would shift it apart (the undock "teleport").
            if (parentOnRails) detached.GoOnRails(ut);
            string name = UniqueShipName(_shipName + " module");
            _others.Add(new TrackedShip { Name = name, V = detached });
            Ctx.State.UpsertShip(ShipState.From(detached, name, ut: ut));
            if (!_vessel.Landed && !_vessel.Destroyed) RefreshPrediction(ut);
            Dbg(DbgShip("UNDOCK-keep", _shipName, ut, _vessel));
            Dbg(DbgShip("UNDOCK-det ", name, ut, detached));
            _dbgFrames = 4;   // dump the pair for the next few frames to catch any post-undock snap
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
            _caValid = false; _caTpos = null; _caYouPrimary = null;
            if (!HasTarget || _vessel == null || _vessel.Destroyed || _vessel.Landed) { _prox.Clear(); return; }
            if (!TargetOrbitBody(ut, out var tEl, out var tPrimary)) { _prox.Clear(); return; }
            var segs = Projection(ut, refreshNodes: false);
            if (segs.Count == 0) { _prox.Clear(); return; }

            // The body whose SOI an encounter would enter: the target itself if it's a body, else the
            // body the target vessel orbits. Tuning a transfer means shrinking the miss to this body.
            CelestialBody approachBody = _targetBody != null ? _targetBody : _targetVessel?.Body;

            // Pick which planned patch to measure on, and what to measure against:
            //  1. a patch that shares the target's primary  -> measure the true rendezvous to the target
            //     conic there (we're planned into the same SOI as it);
            //  2. otherwise the pre-encounter leg that shares the approach body's primary -> measure the
            //     miss distance to the approach body itself, so the readout converges as the node is tuned
            //     toward an SOI entry (instead of chasing a post-escape heliocentric patch off to deep space).
            // Each patch is searched over its OWN validity window, not the final conic's full period.
            ProjSegment seg = default; bool haveSeg = false;
            OrbitalElements proxTgtEl = tEl; CelestialBody proxTgtPrimary = tPrimary;

            for (int i = segs.Count - 1; i >= 0; i--)
                if (segs[i].Body == tPrimary && !double.IsNaN(segs[i].El.A)) { seg = segs[i]; haveSeg = true; break; }
            if (haveSeg)
            {
                var te = tEl; var tp = tPrimary;
                _caTpos = t => tp.AbsolutePositionAt(t) + Kepler.StateAtTime(te, t).pos;
            }
            else if (approachBody != null && approachBody.Parent != null)
            {
                for (int i = segs.Count - 1; i >= 0; i--)
                    if (segs[i].Body == approachBody.Parent && !double.IsNaN(segs[i].El.A)) { seg = segs[i]; haveSeg = true; break; }
                if (haveSeg)
                {
                    var ab = approachBody;
                    _caTpos = t => ab.AbsolutePositionAt(t);          // miss distance to the approach body
                    proxTgtEl = ab.Orbit; proxTgtPrimary = ab.Parent; // "Orbit gap" vs the body's own path
                }
            }
            if (!haveSeg)
            {
                // fallback (e.g. same-SOI rendezvous, no usable transfer leg): last conic vs the target.
                var last = segs[segs.Count - 1];
                if (double.IsNaN(last.El.A)) { _prox.Clear(); return; }
                seg = last; haveSeg = true;
                var te = tEl; var tp = tPrimary;
                _caTpos = t => tp.AbsolutePositionAt(t) + Kepler.StateAtTime(te, t).pos;
            }

            _caYouEl = seg.El; _caYouPrimary = seg.Body;
            _caStart = Math.Max(ut, seg.StartUT);
            double winEnd = double.IsInfinity(seg.EndUT)
                ? _caStart + (seg.El.Hyperbolic || double.IsNaN(seg.El.Period) ? 6 * 3600.0 : seg.El.Period)
                : seg.EndUT;
            double window = winEnd - _caStart;
            _caValid = Rendezvous.ClosestApproach(seg.El, seg.Body, _caTpos, _caStart, window,
                                                  out _caUT, out _caSep, out _caRelSpeed);

            // geometric orbit-curve proximities (intersections + closest points) -- recompute every ~8 frames
            if (--_proxTimer <= 0)
            {
                _proxTimer = 8;
                _prox = Rendezvous.OrbitProximity(seg.El, seg.Body.AbsolutePositionAt(ut),
                                                  proxTgtEl, proxTgtPrimary.AbsolutePositionAt(ut));
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
            if (v.HasLeftLaunchSite) v.MissionCancelable = false;   // touched an object: lock out mission cancel
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
                // rest at whichever of {upright (Up along the surface normal), flat-on-its-side (Up along the
                // tangent, either way)} is nearest the craft's touchdown heading, so a craft coming in broadside
                // under top+bottom chutes settles horizontally instead of snapping upright.
                double normal = SurfaceNormalAngle(v.Body, ang);
                v.Heading = NearestAngle(v.Heading, normal, normal + Math.PI / 2, normal - Math.PI / 2);
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

        /// <summary>The candidate angle closest to <paramref name="current"/> (smallest wrapped difference).</summary>
        private static double NearestAngle(double current, params double[] candidates)
        {
            double best = candidates[0], bestD = double.MaxValue;
            foreach (double c in candidates)
            {
                double d = Math.Abs(Kepler.WrapPi(c - current));
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
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
                // encounter focus pins the camera to the encountered body at the (fixed) transition time,
                // where the inside-SOI flyby leg is actually drawn; otherwise the chosen body / the vessel.
                Vec2d focusPos = _encFocusBody != null
                    ? _encFocusBody.AbsolutePositionAt(_encFocusUT)
                    : _focus == 0 ? vesselPos : Ctx.Universe.Bodies[_focus - 1].AbsolutePositionAt(ut);
                _cam.Center = focusPos + _mapPan;
                _cam.MetersPerPixel = _mapZoom;
            }
            else
            {
                _cam.Center = vesselPos;
                _cam.MetersPerPixel = _flightZoom;
                if (_shake > 1e-3)
                {
                    double mag = _shake * _flightZoom;   // shake scaled in screen pixels regardless of zoom
                    _cam.Center += new Vec2d((_shakeRnd.NextDouble() * 2 - 1) * mag, (_shakeRnd.NextDouble() * 2 - 1) * mag);
                    _shake *= 0.82;
                    if (_shake < 1e-3) _shake = 0;
                }
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

        /// <summary>Flight-view part inspector: click a part on the active vessel to open a static, closable
        /// info panel; a decoupler can be fired straight from it. Mirrors the map-view ship menu. The panel's
        /// button rects are laid out by <see cref="DrawPartPopup"/> and consumed here a frame later (the same
        /// immediate-mode pattern the ship menu uses). Returns true when it consumed this frame's click.</summary>
        private bool UpdatePartPopup(double ut)
        {
            var inp = Ctx.Input;
            if (_map || _vessel == null || _vessel.Destroyed) { _popupPart = null; return false; }

            // close if the inspected part left the vessel (decoupled, staged away, undocked)
            if (_popupPart != null && !PartPresent(_popupPart)) _popupPart = null;

            if (_popupPart != null)
            {
                if (!inp.LeftClick) return false;
                var m = inp.MousePos;
                if (_popupCloseRect.Contains((int)m.X, (int)m.Y)) { _popupPart = null; return true; }
                if (_popupPart.Def.Kind == PartKind.Decoupler && _popupDecoupleRect.Contains((int)m.X, (int)m.Y))
                { DecoupleSelected(ut); return true; }
                // parachute: force deploy or cut/repack on demand, independent of staging
                if (_popupPart.Def.Kind == PartKind.Parachute && _popupChuteRect.Contains((int)m.X, (int)m.Y))
                { _popupPart.Deployed = !_popupPart.Deployed; return true; }
                bool thruster = _popupPart.Def.Kind == PartKind.Engine || _popupPart.Def.Kind == PartKind.SolidBooster;
                // ignite this engine/booster now, without waiting for its stage to come up
                if (thruster && !_popupPart.Ignited && _popupIgniteRect.Contains((int)m.X, (int)m.Y))
                { _popupPart.Ignited = true; _vessel.EnginesIgnited = true; return true; }
                if (_popupPart.Def.Kind == PartKind.Engine && _popupPart.Ignited)
                {
                    if (_popupEngineToggleRect.Contains((int)m.X, (int)m.Y)) { _popupPart.EngineOn = !_popupPart.EngineOn; return true; }
                    if (_popupPowerRect.Contains((int)m.X, (int)m.Y)) return true;   // slider drag owned by HSlider in DrawPartPopup
                }
                // a click landing on another part re-targets the popup; clicks elsewhere leave it open (static)
                if (PickPartAt(m, out var other)) { _popupPart = other; _popupPos = m; }
                return true;
            }

            if (inp.LeftClick && PickPartAt(inp.MousePos, out var hit))
            { _popupPart = hit; _popupPos = inp.MousePos; return true; }
            return false;
        }

        /// <summary>Whether <paramref name="part"/> is still attached to the active vessel.</summary>
        private bool PartPresent(Part part)
        {
            foreach (var p in _vessel.AllParts()) if (p == part) return true;
            return false;
        }

        /// <summary>Topmost vessel part under <paramref name="screen"/>, using last frame's drawn footprints.</summary>
        private bool PickPartAt(Vector2 screen, out Part part)
        {
            for (int i = _partHits.Count - 1; i >= 0; i--)   // last drawn = topmost
                if (PointInQuad(screen, _partHits[i].quad)) { part = _partHits[i].part; return true; }
            part = null; return false;
        }

        /// <summary>Point-in-convex-quad test (consistent edge-cross sign around the four corners).</summary>
        private static bool PointInQuad(Vector2 p, Vector2[] q)
        {
            int sign = 0;
            for (int i = 0; i < 4; i++)
            {
                Vector2 a = q[i], b = q[(i + 1) & 3];
                float cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
                int s = cross > 0 ? 1 : cross < 0 ? -1 : 0;
                if (s == 0) continue;
                if (sign == 0) sign = s; else if (s != sign) return false;
            }
            return true;
        }

        /// <summary>Fire the decoupler shown in the popup: drop it (and everything below it) as debris,
        /// like <see cref="FireNextStage"/> but targeting one specific decoupler.</summary>
        private void DecoupleSelected(double ut)
        {
            var part = _popupPart;
            _popupPart = null;
            if (part == null || _vessel == null || _vessel.Destroyed) return;
            bool wasOnRails = _vessel.OnRails;
            if (wasOnRails) _vessel.GoOffRails(ut);
            foreach (var debris in Staging.DecoupleAt(_vessel, part))
            {
                _debris.Add(debris);
                if (_debris.Count > MaxDebris) _debris.RemoveAt(0);
            }
            if (wasOnRails) { _vessel.GoOnRails(ut); RefreshPrediction(ut); }
        }

        /// <summary>Draw the static part-info popup (info lines + close glyph, plus a Decouple button for a
        /// decoupler). Lays out the close/decouple rects for next frame's click handling in
        /// <see cref="UpdatePartPopup"/>. Live fuel/mass are read from the part itself.</summary>
        private void DrawPartPopup(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb)
        {
            if (_popupPart == null || _map) return;
            var f = Ctx.Font; var inp = Ctx.Input;
            var p = _popupPart; var d = p.Def;
            const float small = 0.8f;

            var detail = new List<string> { d.Kind.ToString() };
            double dry = p.Mass - p.Fuel;
            detail.Add(d.FuelCapacity > 0 ? $"Mass: {p.Mass / 1000:0.00} t  (dry {dry / 1000:0.00} t)"
                                          : $"Mass: {dry / 1000:0.00} t");
            if (d.Kind == PartKind.Engine || d.Kind == PartKind.SolidBooster)
            {
                detail.Add($"Thrust: {d.Thrust / 1000:0} kN   Isp: {d.Isp:0} s");
                detail.Add(p.Ignited ? "Status: ignited" : "Status: not ignited");
            }
            if (d.Kind == PartKind.Parachute) detail.Add(p.Deployed ? "Status: deployed" : "Status: stowed");
            if (d.FuelCapacity > 0) detail.Add($"Fuel: {p.Fuel:0} / {d.FuelCapacity:0} kg");
            detail.Add($"Size: {d.Width:0.0} x {d.Height:0.0} m");
            if (p.Modules.Count > 0)
            {
                var names = new List<string>();
                foreach (var mod in p.Modules) names.Add(mod.Def.Name);
                detail.Add("Modules: " + string.Join(", ", names));
            }

            bool decoupler = d.Kind == PartKind.Decoupler;
            bool thruster = d.Kind == PartKind.Engine || d.Kind == PartKind.SolidBooster;
            bool igniteBtn = thruster && !p.Ignited;     // not yet lit: offer an Ignite button
            bool engineCtl = d.Kind == PartKind.Engine && p.Ignited;  // lit liquid engine: on/off + power limiter
            bool chuteBtn = d.Kind == PartKind.Parachute;
            float lhTitle = f.MeasureString("X").Y + 2;
            float lhSmall = f.MeasureString("X").Y * small + 2;
            float tw = f.MeasureString(d.Name).X + 22;   // leave room for the close glyph
            foreach (var ln in detail) tw = Math.Max(tw, f.MeasureString(ln).X * small);
            if (engineCtl) tw = Math.Max(tw, 140);       // keep the slider usably wide
            const int btnH = 26;
            int engineH = engineCtl ? btnH + 6 + (int)lhSmall + 12 + 4 : 0;
            int btnRowH = (decoupler || igniteBtn || chuteBtn) ? btnH + 6 : 0;   // one bottom-anchored action button
            int bw = (int)tw + 18;
            int bh = (int)(lhTitle + detail.Count * lhSmall) + 12 + btnRowH + engineH;
            int bx = (int)Math.Clamp(_popupPos.X, 4, Math.Max(4, Ctx.W - bw - 4));
            int by = (int)Math.Clamp(_popupPos.Y, 4, Math.Max(4, Ctx.H - bh - 4));

            UiDraw.Panel(pb, new Rectangle(bx, by, bw, bh));

            _popupCloseRect = new Rectangle(bx + bw - 18, by + 4, 14, 14);
            bool hov = _popupCloseRect.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y);
            pb.FillRect(_popupCloseRect, hov ? new Color(160, 70, 70, 235) : new Color(60, 45, 45, 215));
            pb.RectOutline(_popupCloseRect, 1, UiDraw.PanelBorder);
            sb.DrawString(f, "x", new Vector2(_popupCloseRect.X + 3, _popupCloseRect.Y - 4), Color.White);

            float ty = by + 6;
            sb.DrawString(f, d.Name, new Vector2(bx + 9, ty), Color.White);
            ty += lhTitle;
            for (int i = 0; i < detail.Count; i++)
            {
                UiDraw.SmallText(sb, f, detail[i], new Vector2(bx + 9, ty), i == 0 ? UiDraw.Accent : UiDraw.TextDim, small);
                ty += lhSmall;
            }

            // one bottom-anchored action button per part kind (clicks handled in UpdatePartPopup)
            _popupDecoupleRect = Rectangle.Empty; _popupIgniteRect = Rectangle.Empty; _popupChuteRect = Rectangle.Empty;
            int btnY = by + bh - btnH - 6;
            if (decoupler)
            {
                _popupDecoupleRect = new Rectangle(bx + 9, btnY, bw - 18, btnH);
                UiDraw.Button(pb, sb, f, _popupDecoupleRect, "Decouple", inp);
            }
            else if (igniteBtn)
            {
                _popupIgniteRect = new Rectangle(bx + 9, btnY, bw - 18, btnH);
                UiDraw.Button(pb, sb, f, _popupIgniteRect, "Ignite", inp);
            }
            else if (chuteBtn)
            {
                _popupChuteRect = new Rectangle(bx + 9, btnY, bw - 18, btnH);
                UiDraw.Button(pb, sb, f, _popupChuteRect, p.Deployed ? "Cut / Repack" : "Deploy", inp);
            }

            if (engineCtl)
            {
                // on/off toggle (click handled in UpdatePartPopup) + power-limiter slider (HSlider drives itself)
                ty += 2;
                _popupEngineToggleRect = new Rectangle(bx + 9, (int)ty, bw - 18, btnH);
                UiDraw.Button(pb, sb, f, _popupEngineToggleRect, p.EngineOn ? "Engine: ON" : "Engine: OFF", inp);
                ty += btnH + 6;
                UiDraw.SmallText(sb, f, $"Power: {p.PowerLimit * 100:0}%", new Vector2(bx + 9, ty), UiDraw.TextDim, small);
                ty += lhSmall;
                _popupPowerRect = new Rectangle(bx + 9, (int)ty, bw - 18, 12);
                p.PowerLimit = UiDraw.HSlider(pb, _popupPowerRect, p.PowerLimit, inp, ref _powerDragging);
            }
            else { _popupEngineToggleRect = Rectangle.Empty; _popupPowerRect = Rectangle.Empty; }
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

        /// <summary>Heading deployed chutes aerodynamically weathervane the craft toward, or null when no
        /// chute is deployed / there's no airflow. A single (one-ended) chute trails its end downwind so
        /// the craft points retrograde; balanced top+bottom chutes hold broadside (flat across the airflow),
        /// the nearer of velocity +/- 90 deg, for a horizontal descent / landing.</summary>
        private double? ChuteAttitudeTarget()
        {
            var v = _vessel;
            if (v == null || v.Landed || v.Velocity.Length < 1.0 || v.DynamicPressure < 1.0) return null;
            if (!v.DeployedChuteOffset(out double offset, out bool bothEnds)) return null;
            double prograde = v.Velocity.Angle();
            if (bothEnds)
            {
                double a = prograde + Math.PI / 2, b = prograde - Math.PI / 2;
                return Math.Abs(Kepler.WrapPi(a - v.Heading)) <= Math.Abs(Kepler.WrapPi(b - v.Heading)) ? a : b;
            }
            return offset >= 0 ? (-v.Velocity).Angle() : prograde;   // chute end trails downwind (retrograde)
        }

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
            double thrustAtFire = _vessel.CurrentThrust;
            var dropped = Staging.FireNext(_vessel);
            if (dropped.Count > 0)
            {
                foreach (var debris in dropped)
                {
                    _debris.Add(debris);
                    if (_debris.Count > MaxDebris) _debris.RemoveAt(0);
                }
                // B4: staging punch — a brief camera shake and a smoke puff at the jettison point.
                _shake = Math.Clamp(thrustAtFire / 1e5, 1.0, 8.0);
                _stagePuffUt = ut;
                _stagePuffPos = _vessel.AbsolutePosition(ut) - new Vec2d(Math.Cos(_vessel.Heading), Math.Sin(_vessel.Heading)) * (_vessel.TotalHeight * 0.5);
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
            UiDraw.TexPanel(pb, Ctx, "gameplay_modules_panel", r);
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
        /// <summary>Base science awarded the first time an ore scanner surveys a body (scaled by scientists).</summary>
        private const double OreSurveyScience = 10;

        /// <summary>A powered, active ore scanner reveals the current body's ore richness the first time the
        /// vessel reaches it, awarding a one-time survey bonus (scaled by scientists aboard). Until a body
        /// is surveyed the HUD shows its richness as unknown — so scanning is a real scouting step before
        /// committing a mining base.</summary>
        private void TrySurveyBody(double ut)
        {
            var v = _vessel; var gs = Ctx.State;
            if (v == null || v.Destroyed || v.Body == null || v.Body.Parent == null) return;  // skip the star
            if (!v.ScannerOperational || gs.SurveyedBodies.Contains(v.Body.Name)) return;
            gs.SurveyedBodies.Add(v.Body.Name);
            double reward = OreSurveyScience * v.CrewSkill(CrewRole.Scientist);
            gs.Science += reward;
            _toast = $"+{reward:0} science  -  Surveyed {v.Body.Name}: {v.Body.OreRichness * 100:0}% ore";
            _toastT = 5;
        }

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
                        v.PendingScience += SciPoints(m.Def, sit, v.Body.Name) * v.CrewSkill(CrewRole.Scientist);
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

        // Reused each frame: the HUD reads SasIcons immediately in the same Draw, so the buffer never
        // needs to outlive the call -- no reason to allocate a fresh SAS-icon array every frame.
        private readonly SasIconInfo[] _sasIconBuf = new SasIconInfo[SasModeCount];

        /// <summary>Build the target navball cues (markers + readout) for the HUD.</summary>
        private NavMarkers BuildNavMarkers(double ut)
        {
            var nav = new NavMarkers { SasMode = (int)_sas, SasLabel = SasLabel(), Target = double.NaN, RelPro = double.NaN };
            bool sasOn = _vessel != null && !_vessel.Destroyed && _vessel.SasAvailable;
            nav.SasEnabled = sasOn;
            var icons = _sasIconBuf;
            for (int i = 0; i < SasModeCount; i++)
                icons[i] = new SasIconInfo { Icon = i, Available = sasOn && SasModeAvailable((SasMode)i, ut), Active = (int)_sas == i };
            nav.SasIcons = icons;
            if (HasTarget && _vessel != null && !_vessel.Destroyed && !_vessel.Landed)
            {
                nav.Active = true;
                nav.Name = _targetName;
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

        /// <summary>Screen position of a reached (frozen) node, from its stored absolute world position.
        /// Reached nodes are frozen reference plots: anchored here, not re-derived from the live orbit.</summary>
        private Vector2 ReachedNodeScreen(Maneuver node) => _cam.WorldToScreen(node.ReachedAbsPos);

        /// <summary>The frozen primary position a reached node's conic is drawn around. Derived from the
        /// stored absolute node position (reload-safe: does not depend on the runtime-only FrameUT).</summary>
        private Vec2d ReachedPrimaryAbs(Maneuver node) =>
            node.ReachedAbsPos - Kepler.StateAtTime(node.Source, node.UT).pos;

        private void UpdateManeuverInput(double ut, bool suppressClick, out bool wheelConsumed)
        {
            wheelConsumed = false;
            if (!_map || _cam.ScreenW == 0) { _dragHandle = -1; _dragNode = -1; _hoverHandle = -1; _hoverNode = -1; _hoverX = -1; return; }
            var inp = Ctx.Input;
            if (!LiveOrbit(ut, out var live, out var body, out var primaryAbs))
            {
                if (_vessel == null || _vessel.Destroyed || _vessel.Landed) { _nodes.Clear(); _burnSpent = 0; }
                _dragHandle = -1; _dragNode = -1; _hoverHandle = -1; _hoverNode = -1; _hoverX = -1; _encFocusBody = null;
                return;
            }

            // Build this frame's planned projection once, here, before any other consumer. refreshNodes
            // re-chains pending nodes across SOI transitions and reassigns each node's frozen Source. The
            // live conic is constant *iff* the vessel is on-rails, so we re-source only then; whenever the
            // ship is off-rails for any reason (thrust, RCS, radial, atmosphere) the node Source is held
            // frozen and the planned (orange) orbit stays exactly where the player set it.
            Projection(ut, refreshNodes: _vessel.OnRails);
            double nodeSig0 = NodeSignature();   // invalidate the cache below if any edit changes the nodes

            // keep an active encounter focus tracking the encounter as the node is tuned; drop it once the
            // route no longer reaches that body's SOI.
            if (_encFocusBody != null)
            {
                bool still = false;
                foreach (var (eb, et, _) in PlannedEncounters(ut))
                    if (eb == _encFocusBody) { _encFocusUT = et; still = true; break; }
                if (!still) _encFocusBody = null;
            }

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
                // reached nodes are frozen reference plots: only their X (delete) is interactive
                if (_nodes[n].Reached)
                {
                    if (Vector2.Distance(mouse, XButtonPos(ReachedNodeScreen(_nodes[n]))) <= XHit) { _hoverX = n; break; }
                    continue;
                }
                if (!ManeuverGeometry(_nodes[n], ut, out var ns, out var pd, out var rd)) continue;
                if (Vector2.Distance(mouse, XButtonPos(ns)) <= XHit) { _hoverX = n; break; }
                var h = new[] { ns + pd * HandleDist, ns - pd * HandleDist, ns + rd * HandleDist, ns - rd * HandleDist };
                for (int k = 0; k < 4; k++)
                    if (Vector2.Distance(mouse, h[k]) <= HandleHit) { _hoverHandle = k; _hoverNode = n; break; }
                if (_hoverHandle < 0 && Vector2.Distance(mouse, ns) <= NodeHit) _hoverNode = n;
            }

            // scroll-wheel fine-tune when hovering a handle (takes priority over zoom). The wheel is the
            // precise tool: Shift/Alt/Alt+Shift step ever finer (down to 0.001 m/s) for SOI-grazing edits.
            if (_hoverHandle >= 0 && _hoverNode >= 0 && inp.WheelDelta != 0)
            {
                double step = FineTier(inp, 1.0, 0.1, 0.01, 0.001);
                AdjustComponent(_nodes[_hoverNode], _hoverHandle, Math.Sign(inp.WheelDelta) * step);
                wheelConsumed = true;
            }

            // keyboard fine-tune targets the next node
            var kbNode = NextNode(ut);
            if (kbNode != null)
            {
                double ks = FineTier(inp, 5.0, 0.5, 0.05, 0.005);
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
                    _dragHandle = _hoverHandle; _dragNode = _hoverNode; _dragFineMult = 1.0;
                    ManeuverGeometry(_nodes[_dragNode], ut, out var ns, out var pd, out var rd);
                    Vector2 axis = _dragHandle < 2 ? pd : rd;
                    _dragStartProj = Vector2.Dot(mouse - ns, axis);
                    _dragStartValue = _dragHandle < 2 ? _nodes[_dragNode].Prograde : _nodes[_dragNode].Radial;
                }
                else if (_hoverNode >= 0)
                {
                    _dragHandle = 4; _dragNode = _hoverNode; // move node along its orbit
                }
                else if (ClickedEncounterMarker(ut, mouse, out var encBody, out var encUT))
                {
                    // clicking an encounter marker frames that flyby (toggles off if already framed) so the
                    // inside-SOI leg fills the screen and a capture node can be dropped on it
                    if (_encFocusBody == encBody) _encFocusBody = null;
                    else FocusEncounter(encBody, encUT);
                    _dragHandle = -1; _dragNode = -1;
                }
                else if (!_showTargetWindow)
                {
                    // place a new node on whichever projected conic is clicked -- including the path
                    // inside a body encountered/escaped to (KSP patched-conic node planning)
                    var segs = Projection(ut, refreshNodes: false);
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
                    // Alt / Alt+Shift make the drag 10x / 100x finer; re-anchor when the tier changes so the
                    // value never jumps (the wheel is the truly precise tool — drag stays pixel-limited).
                    double mult = FineTier(inp, 1.0, 1.0, 0.1, 0.01);
                    if (mult != _dragFineMult)
                    {
                        _dragStartValue = _dragHandle < 2 ? node.Prograde : node.Radial;
                        _dragStartProj = proj;
                        _dragFineMult = mult;
                    }
                    double sens = Math.Max(0.4, Kepler.StateAtTime(src, node.UT).vel.Length * 0.004) * mult;
                    double val = _dragStartValue + (proj - _dragStartProj) * sens;
                    if (_dragHandle < 2) node.Prograde = val; else node.Radial = val;
                }
            }
            if (!inp.LeftDown) { _dragHandle = -1; _dragNode = -1; }

            // a node was added/removed/tuned/dragged this frame: drop the cached projection so the rest of
            // the frame (closest approach, drawing) rebuilds against the edited nodes.
            if (NodeSignature() != nodeSig0) _projSig = double.NaN;   // an edit changed the nodes: rebuild
        }

        /// <summary>A cheap value that changes whenever the set of nodes or any node's time/components
        /// changes; used to detect in-frame edits that should invalidate the projection cache.</summary>
        private double NodeSignature()
        {
            double s = _nodes.Count;
            foreach (var n in _nodes) s += n.UT + n.Prograde * 7.0 + n.Radial * 13.0;
            return s;
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

        /// <summary>Maneuver fine-tune amount selected by held modifiers: none / Shift / Alt / Alt+Shift
        /// (finest). Shared by the scroll-wheel, keyboard and drag tuning so the tiers stay consistent.</summary>
        private static double FineTier(InputState inp, double none, double shift, double alt, double altShift)
        {
            bool s = inp.Down(Keys.LeftShift) || inp.Down(Keys.RightShift);
            bool a = inp.Down(Keys.LeftAlt) || inp.Down(Keys.RightAlt);
            return a ? (s ? altShift : alt) : (s ? shift : none);
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

            OrbitalElements? flybyEl = RouteFlyby(ut, out var fEl, out var fBody) ? fEl : (OrbitalElements?)null;
            var hud = Hud.Draw(Ctx, _vessel, _pred, _map, FocusName(), DisplayNode(ut), BurnDirAngle(ut), _burnSpent, _nodes.Count, BuildNavMarkers(ut), flybyEl, fBody);
            _rightColBottom = hud.RightColumnBottom;
            if (hud.WarpToUT.HasValue) _warpTo = hud.WarpToUT;
            if (hud.FireStage) FireNextStage();
            if (hud.RequestedSas.HasValue) SetSas((SasMode)hud.RequestedSas.Value);

            DrawTargetPanel(pb, sb, ut);
            DrawTargetWindow(pb, sb);
            DrawScienceStatus(pb, sb, ut);
            DrawCrewPanel(pb, sb);
            DrawColonyPanel(pb, sb, ut);
            if (_map) DrawShipMenu(pb, sb);
            else DrawPartPopup(pb, sb);

            if (_toastT > 0 && _toast != null)
            {
                var f = Ctx.FontBig;
                var sz = f.MeasureString(_toast);
                float alpha = (float)Math.Clamp(_toastT, 0, 1);
                var pos = new Vector2(Ctx.W / 2 - sz.X / 2, 70);
                pb.FillRect((int)pos.X - 14, (int)pos.Y - 6, (int)sz.X + 28, (int)sz.Y + 12, new Color(20, 30, 22, (int)(200 * alpha)));
                sb.DrawString(f, _toast, pos, new Color(150, 230, 150) * alpha);
            }

            // rendezvous prompt: a craft is in range to dock/connect — confirm the merge with [P]
            string dockMsg = null;
            if (_dockCandidate is DockCandidate dcp) dockMsg = $"[P] dock with {dcp.Ship.Name}";
            else if (_surfaceConnect != null && _vessel != null && _vessel.Landed) dockMsg = $"[P] connect to {_surfaceConnect.Name}";
            if (dockMsg != null && _vessel != null && !_vessel.Destroyed && !_showExitDialog)
            {
                var f = Ctx.FontBig;
                var sz = f.MeasureString(dockMsg);
                var pos = new Vector2(Ctx.W / 2 - sz.X / 2, Ctx.H - 150);
                pb.FillRect((int)pos.X - 12, (int)pos.Y - 6, (int)sz.X + 24, (int)sz.Y + 12, new Color(20, 30, 40, 200));
                sb.DrawString(f, dockMsg, pos, new Color(150, 230, 150));
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
            // a mission can be scrapped back to the editor until the ship first touches an object or docks
            bool canCancel = _vessel is { Destroyed: false, MissionCancelable: true };
            int w = 320, h = canCancel ? 310 : 262;
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
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "Space Center", inp))
                _exitToHub = true;
            by += bh + 10;
            // until first touch/dock: scrap the flight and reopen the design in the editor
            if (canCancel && UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "Cancel mission", inp))
            { _cancelMission = true; _showExitDialog = false; }
            if (canCancel) by += bh + 10;
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "Exit to main menu", inp))
                _exitToTitle = true;
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
            int wWin = 230, rows = 0;   // match the HUD right-column width (rColW)
            foreach (var p in seated) rows += 1 + p.Crew.Count;
            // sit below the right-column stack (systems/modules/science) so it no longer overlaps them
            var r = new Rectangle(Ctx.W - wWin - 10, _rightColBottom, wWin, 78 + Math.Max(1, rows) * 20);
            UiDraw.TexPanel(pb, Ctx, "gameplay_modules_panel", r);
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
            var r = new Rectangle(10, 286, 256, 260);
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
                var green = new Color(150, 230, 150); var blue = new Color(120, 190, 230);
                if (_vessel.Landed && _targetVessel.Landed)
                {
                    bool ready = (_targetVessel.Position - _vessel.Position).Length < ConnectDist;
                    Row("Connect", ready ? "[P] to connect" : $"approach < {ConnectDist:0} m", ready ? green : blue);
                }
                else if (!_vessel.Landed && !_targetVessel.Landed)
                {
                    // key off the two nearest free ports, the same geometry the dock itself uses
                    var (_, _, pd) = Vessel.ClosestFreePortPair(_vessel, _targetVessel, ut);
                    double rs = (_targetVessel.Velocity - _vessel.Velocity).Length;
                    bool ready = pd <= PortDockDist && rs < SoftDockSpeed;
                    string txt = ready ? "[P] to dock"
                               : pd > PortDockDist ? $"ports to {PortDockDist:0.0} m ({UiDraw.Dist(pd)})"
                               : $"slow below {SoftDockSpeed:0.0} m/s";
                    Row("Dock", txt, ready ? green : blue);
                }
            }
            if (_caValid)
            {
                Color qcol = RendezvousQuality(_caSep, out string verdict);
                Row("Close App.", $"{UiDraw.Dist(_caSep)}  {verdict}", qcol);
                Row("  in", UiDraw.Time(_caUT - ut));
                Row("  rel. vel.", UiDraw.Speed(_caRelSpeed));
            }
            // when the route drops into the targeted body's SOI, show the flyby periapsis + danger here too
            if (alive && RouteFlyby(ut, out var fbEl, out var fbBody) && fbBody == _targetBody)
            {
                var outc = TrajectoryPredictor.ClassifyFlyby(fbEl, fbBody, out double peAlt);
                Color fc = outc == FlybyOutcome.Impact ? DangerRed : outc == FlybyOutcome.AtmoEntry ? DangerAmber : SafeGreen;
                string fword = outc == FlybyOutcome.Impact ? " IMPACT" : outc == FlybyOutcome.AtmoEntry ? " ENTRY" : "";
                Row("Flyby Pe", UiDraw.Dist(peAlt) + fword, fc);
            }
            if (_prox.Count > 0)
            {
                bool meet = false; double nearest = double.MaxValue;
                foreach (var p in _prox) { if (p.Intersect) meet = true; if (p.Sep < nearest) nearest = p.Sep; }
                // geometric, timing-blind: name it so it can't be read as a rendezvous
                if (meet) Row("Paths", "cross", new Color(210, 160, 80));
                else Row("Path gap", UiDraw.Dist(nearest), new Color(140, 230, 160));
            }

            // Plan transfer: drops a Hohmann-style intercept node the player then fine-tunes. Enabled only
            // when the ship and target share a primary (and the ship is coasting); else greyed with a hint.
            CelestialBody planPrimary = null; OrbitalElements planTgt = default;
            bool canPlan = false; string planHint = null;
            if (alive && !_vessel.Landed)
            {
                if (_targetBody != null && _targetBody.Parent != null && _vessel.Body == _targetBody.Parent)
                { planPrimary = _targetBody.Parent; planTgt = _targetBody.Orbit; canPlan = true; }
                else if (_targetVessel != null && _vessel.Body != null && _targetVessel.Body == _vessel.Body)
                { planPrimary = _vessel.Body; planTgt = _targetVessel.CurrentElements(ut); canPlan = true; }
                else
                {
                    string need = _targetBody != null ? (_targetBody.Parent?.Name ?? "?")
                                                      : (_targetVessel?.Body?.Name ?? "?");
                    planHint = $"orbit {need} to plan";
                }
            }
            var btnRect = new Rectangle(r.X + 10, r.Bottom - 30, r.Width - 20, 24);
            if (planHint != null)
                sb.DrawString(f, planHint, new Vector2(btnRect.X, btnRect.Y - 16), UiDraw.TextDim);
            if (UiDraw.Button(pb, sb, f, btnRect, "Plan transfer", Ctx.Input, canPlan) && canPlan)
            {
                var shipEl = _vessel.CurrentElements(ut);
                if (TransferPlanner.PlanIntercept(shipEl, planPrimary, ut, planTgt,
                        out double utB, out double pro, out double rad))
                {
                    _nodes.RemoveAll(n => !n.Reached);
                    _nodes.Add(new Maneuver
                    {
                        UT = utB, Prograde = pro, Radial = rad,
                        Source = shipEl, Body = planPrimary, HasSource = true
                    });
                    _burnSpent = 0; _projSig = double.NaN;
                }
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

            // B1: engine exhaust trail. Gas ejects opposite the vessel's up (thrust) axis from the tail;
            // tinted by the active engine's data-driven exhaust color, thicker/longer-lived in atmosphere.
            if (_vessel != null && !_vessel.Destroyed && _vessel.CurrentThrust > 0)
            {
                var up = new Vec2d(Math.Cos(_vessel.Heading), Math.Sin(_vessel.Heading));
                Vec2d tail = _vessel.AbsolutePosition(ut) - up * (_vessel.TotalHeight * 0.45);
                Color exCol = new Color(255, 140, 40);
                foreach (var p in _vessel.Parts)
                    if (p.Ignited && (p.Def.Kind == PartKind.Engine || p.Def.Kind == PartKind.SolidBooster))
                    { exCol = p.Def.ExhaustColor; break; }
                double density = Math.Clamp((_vessel.Body?.Atmo?.DensityAt(_vessel.Altitude) ?? 0) / 1.225, 0, 1);
                double thr = Math.Max(_vessel.Throttle, 0.5); // solids report throttle 0 yet still fire
                _exhaust.Draw(pb, _cam, ut, tail, up * -1.0, _vessel.Velocity, thr, exCol, density);
            }

            // B4: expanding/fading smoke ring at the last decoupler jettison (~0.6s).
            double puffAge = ut - _stagePuffUt;
            if (puffAge >= 0 && puffAge < 0.6)
            {
                float life = (float)(puffAge / 0.6);
                var ps = _cam.WorldToScreen(_stagePuffPos);
                float rPx = (float)((4 + 14 * life) / _cam.MetersPerPixel * 0.35); // modest world-scaled puff
                var pc = new Color(220, 215, 205) * (1f - life);
                pb.FillCircle(ps, Math.Max(3f, rPx), pc, pc * 0.1f);
            }

            foreach (var d in _debris)
                VesselRenderer.Draw(pb, _cam, d, ut, _anim, tex: Ctx.Textures);

            foreach (var ts in _others)
                VesselRenderer.Draw(pb, _cam, ts.V, ut, _anim, tex: Ctx.Textures);

            if (_vessel != null && !_vessel.Destroyed)
            {
                _partHits.Clear();
                VesselRenderer.Draw(pb, _cam, _vessel, ut, _anim, tex: Ctx.Textures, pickHits: _partHits);
            }

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
                // C1: tint the trajectory by its current SOI's body color (blended toward cyan so it stays
                // legible as "your path"), making patched-conic legs readable at a glance.
                var cyan = new Color(110, 220, 255);
                var trajColor = Color.Lerp(_pred.Body.BodyColor, cyan, 0.55f);

                if (_pred.Type == TransitionType.None)
                {
                    OrbitRenderer.DrawConicGlow(pb, _cam, el, primaryAbs, trajColor, _pred.Body.SoiRadius);
                    OrbitRenderer.DrawDirectionArrows(pb, _cam, el, primaryAbs, trajColor);
                    DrawApPeMarkers(pb, sb, el, primaryAbs, _pred.Body.Radius, PeColor, ApColor);
                }
                else
                {
                    OrbitRenderer.DrawTrajectory(pb, _cam, el, primaryAbs, ut, _pred.TransitionUT, trajColor, 1.6f);
                    // transition marker (C3): colored by the body it hands off to, labeled with a verb.
                    Vec2d mPos = primaryAbs + Kepler.StateAtTime(el, _pred.TransitionUT).pos;
                    var ms = _cam.WorldToScreen(mPos);
                    Color mkCol = _pred.NextBody != null ? _pred.NextBody.BodyColor : new Color(255, 200, 110);
                    pb.CircleOutline(ms, 6, 1.5f, mkCol);
                    string mkLabel = _pred.Type switch
                    {
                        TransitionType.Encounter => _pred.NextBody != null ? "enc " + _pred.NextBody.Name : "encounter",
                        TransitionType.Escape => _pred.NextBody != null ? "exit to " + _pred.NextBody.Name : "escape",
                        TransitionType.AtmoEntry => "entry",
                        _ => null,
                    };
                    if (mkLabel != null)
                        sb.DrawString(Ctx.Font, mkLabel, new Vector2(ms.X + 9, ms.Y - 7), mkCol);
                    if (!el.Hyperbolic && _pred.TransitionUT - ut > el.Period * 0.4)
                        DrawApPeMarkers(pb, sb, el, primaryAbs, _pred.Body.Radius, PeColor, ApColor);

                    // post-transition conic (ghost) around the next body at transition time
                    if (_pred.NextBody != null)
                    {
                        Vec2d nbAbs = _pred.NextBody.AbsolutePositionAt(_pred.TransitionUT);
                        // C1: ghost of the post-encounter conic tinted by the body it falls into.
                        var c2 = Color.Lerp(_pred.NextBody.BodyColor, new Color(255, 200, 120), 0.5f);
                        OrbitRenderer.DrawConic(pb, _cam, _pred.NextOrbit, nbAbs, c2, 1.4f, _pred.NextBody.SoiRadius);
                        if (_pred.Type == TransitionType.Encounter)
                        {
                            // ghost of the body at encounter time
                            var gs = _cam.WorldToScreenD(nbAbs);
                            if (_cam.OnScreen(gs, 100))
                                pb.CircleOutline(new Vector2((float)gs.X, (float)gs.Y),
                                    (float)Math.Max(4, _pred.NextBody.Radius / _cam.MetersPerPixel), 1.2f, c2 * 0.8f);
                            // flyby periapsis altitude + danger (impact / atmosphere skim / clear), so the
                            // player sees how close this encounter passes BEFORE warping to it.
                            var (dCol, dWord) = FlybyDanger(_pred.NextOrbit, _pred.NextBody);
                            DrawApPeMarkers(pb, sb, _pred.NextOrbit, nbAbs, _pred.NextBody.Radius, dCol, ApColor);
                            if (dWord != null)
                                sb.DrawString(Ctx.Font, dWord, new Vector2(ms.X + 9, ms.Y + 7), dCol);
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

            // Closest-approach pair -- the hero of the rendezvous view. A matched you/target pair at the
            // SAME instant: your marker (diamond) and where the target will actually be then (ring),
            // joined by a tie coloured green/amber/red by how good the rendezvous is. This is the timing-
            // aware approach, unlike the geometric orbit crossings below.
            if (_caValid && _caYouPrimary != null && _caTpos != null && !double.IsNaN(_caYouEl.A))
            {
                Vec2d youCa = _caYouPrimary.AbsolutePositionAt(_caUT) + Kepler.StateAtTime(_caYouEl, _caUT).pos;
                Vec2d tgtCa = _caTpos(_caUT);   // where the target/approach body will be at closest approach
                var ys = _cam.WorldToScreen(youCa);
                var tsScreen = _cam.WorldToScreen(tgtCa);
                Color qcol = RendezvousQuality(_caSep, out string verdict);

                pb.Line(ys, tsScreen, 2f, qcol * 0.85f);                 // the tie reads the verdict colour
                // your marker: a filled diamond so it never reads as the target's ring
                pb.Quad(ys + new Vector2(0, -5), ys + new Vector2(5, 0), ys + new Vector2(0, 5), ys + new Vector2(-5, 0), YouCaColor);
                // the target's projected position at the approach: a labelled ring, findable even when it
                // sits away from the body's current (possibly focused) position.
                pb.CircleOutline(tsScreen, 6, 2f, TargetColor);
                pb.FillCircle(tsScreen, 2f, TargetColor);
                sb.DrawString(Ctx.Font, _targetName ?? "target", tsScreen + new Vector2(9, 5), TargetColor);
                // label on your side: separation + verdict, then ETA + closing speed
                sb.DrawString(Ctx.Font, $"{UiDraw.Dist(_caSep)}  {verdict}", ys + new Vector2(8, -16), qcol);
                sb.DrawString(Ctx.Font, $"in {UiDraw.Time(_caUT - ut)}  @ {UiDraw.Speed(_caRelSpeed)}",
                              ys + new Vector2(8, -2), new Color(200, 210, 230));
            }

            // Geometric orbit-curve crossings -- timing-BLIND, so deliberately secondary to the pair above.
            // At a crossing we draw where the target actually is when the ship arrives, labelled "miss",
            // to teach that the paths crossing in space is not a rendezvous (the target is elsewhere then).
            var meet = new Color(210, 160, 80);     // dimmed amber: a teaching cue, not "you'll meet here"
            var close = new Color(120, 190, 140);
            bool havePlan = _caValid && _caYouPrimary != null && _caTpos != null && !double.IsNaN(_caYouEl.A);
            foreach (var p in _prox)
            {
                var ys = _cam.WorldToScreenD(p.YouPos);
                // generous margin: when focused on the target body the crossing point can sit off the
                // edge of the view while its target-arrival marker is still worth drawing.
                if (!_cam.OnScreen(ys, 400)) continue;
                var yp = new Vector2((float)ys.X, (float)ys.Y);
                if (p.Intersect)
                {
                    if (havePlan)
                    {
                        double tArr   = Kepler.TimeAtTrueAnomaly(_caYouEl, p.NuYou, _caStart);
                        Vec2d shipArr = _caYouPrimary.AbsolutePositionAt(tArr) + Kepler.StateAtTime(_caYouEl, tArr).pos;
                        Vec2d tgtArr  = _caTpos(tArr);
                        var sp = _cam.WorldToScreen(shipArr);
                        var tp = _cam.WorldToScreen(tgtArr);
                        pb.Line(sp, tp, 1f, meet * 0.55f);
                        pb.CircleOutline(tp, 4, 1.2f, TargetColor * 0.8f);
                        yp = sp;   // anchor the crossing mark/label on the recomputed arrival point
                        sb.DrawString(Ctx.Font, "miss " + UiDraw.Dist((shipArr - tgtArr).Length),
                                      yp + new Vector2(8, -8), meet);
                    }
                    else sb.DrawString(Ctx.Font, "cross", yp + new Vector2(8, -8), meet);
                    pb.CircleOutline(yp, 5, 1.2f, meet);
                    pb.Line(yp + new Vector2(-3, -3), yp + new Vector2(3, 3), 1f, meet);
                    pb.Line(yp + new Vector2(-3, 3), yp + new Vector2(3, -3), 1f, meet);
                }
                else
                {
                    var ts2 = _cam.WorldToScreen(p.TgtPos);
                    pb.Line(yp, ts2, 1f, close * 0.45f);
                    pb.CircleOutline(yp, 4, 1.2f, close);
                    pb.FillCircle(ts2, 2.5f, close * 0.8f);
                    sb.DrawString(Ctx.Font, "gap " + UiDraw.Dist(p.Sep), yp + new Vector2(8, -8), close);
                }
            }
        }

        private void DrawApPeMarkers(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
                                     in OrbitalElements el, Vec2d primaryAbs, double bodyRadius, Color peCol, Color apCol)
        {
            var peW = OrbitRenderer.PeriapsisPoint(el, primaryAbs);
            var apW = OrbitRenderer.ApoapsisPoint(el, primaryAbs);
            var priS = _cam.WorldToScreen(primaryAbs);

            // C4: apse line (major axis) through both apsides, faint so it reads as a reference.
            if (!el.Hyperbolic)
                pb.Line(_cam.WorldToScreen(peW), _cam.WorldToScreen(apW), 1f, apCol * 0.30f);

            var peS = _cam.WorldToScreenD(peW);
            if (_cam.OnScreen(peS, 50))
                DrawApsisMarker(pb, sb, new Vector2((float)peS.X, (float)peS.Y), priS,
                                $"Pe {UiDraw.Dist(el.Periapsis - bodyRadius)}", peCol);
            if (!el.Hyperbolic)
            {
                var apS = _cam.WorldToScreenD(apW);
                if (_cam.OnScreen(apS, 50))
                    DrawApsisMarker(pb, sb, new Vector2((float)apS.X, (float)apS.Y), priS,
                                    $"Ap {UiDraw.Dist(el.Apoapsis - bodyRadius)}", apCol);
            }
        }

        /// <summary>C2: an apsis marker drawn as a chevron pointing radially outward (away from the primary)
        /// with the label offset along that direction by a short leader, so it clears the orbit line.</summary>
        private void DrawApsisMarker(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
                                     Vector2 p, Vector2 primaryScreen, string label, Color col)
        {
            Vector2 ro = p - primaryScreen;
            ro = ro.LengthSquared() > 1e-3f ? Vector2.Normalize(ro) : new Vector2(0, -1);
            Vector2 perp = new Vector2(-ro.Y, ro.X);
            Vector2 tip = p + ro * 8;
            pb.Line(p - perp * 5, tip, 1.8f, col);
            pb.Line(p + perp * 5, tip, 1.8f, col);
            pb.FillCircle(p, 2.5f, col);
            sb.DrawString(Ctx.Font, label, p + ro * 12 + new Vector2(3, -6), col);
        }

        private static readonly Color PeColor = new Color(120, 220, 255);
        private static readonly Color ApColor = new Color(170, 150, 255);

        // Traffic-light tints for a flyby/encounter periapsis: red impact, amber atmosphere skim, green clear.
        private static readonly Color DangerRed = new Color(255, 90, 80);
        private static readonly Color DangerAmber = new Color(255, 170, 90);
        private static readonly Color SafeGreen = new Color(140, 230, 160);

        /// <summary>Danger tint + short ASCII tag ("IMPACT"/"ENTRY"/null) for a conic falling toward a body,
        /// from <see cref="TrajectoryPredictor.ClassifyFlyby"/>. Drives the flyby periapsis colouring/labels.</summary>
        private static (Color col, string word) FlybyDanger(in OrbitalElements el, CelestialBody body)
        {
            switch (TrajectoryPredictor.ClassifyFlyby(el, body, out _))
            {
                case FlybyOutcome.Impact: return (DangerRed, "IMPACT");
                case FlybyOutcome.AtmoEntry: return (DangerAmber, "ENTRY");
                default: return (SafeGreen, null);
            }
        }

        // A ship/white tint for "you @ closest approach" so the pair reads as you-vs-target.
        private static readonly Color YouCaColor = new Color(120, 230, 255);

        /// <summary>Traffic-light quality of a closest-approach separation against the current target,
        /// with a one-word verdict. A body is scored by its SOI (entering it = an encounter); a vessel
        /// by a fixed metre scale. Drives the colour of the closest-approach pair and the panel readout.</summary>
        private Color RendezvousQuality(double sep, out string verdict)
        {
            if (_targetBody != null)
            {
                double soi = _targetBody.SoiRadius;
                if (sep <= soi) { verdict = "encounter"; return SafeGreen; }
                if (sep <= 3 * soi) { verdict = "close"; return DangerAmber; }
                verdict = "far"; return DangerRed;
            }
            if (sep < 2000) { verdict = "close"; return SafeGreen; }
            if (sep < 50000) { verdict = "near"; return DangerAmber; }
            verdict = "far"; return DangerRed;
        }

        /// <summary>Draws every planned node: chained orbit previews, Ap/Pe + encounter for the
        /// final patch, plus each node's marker, delta-v handles and X delete button. Nodes are
        /// consumed the moment their burn time passes, so only pending future nodes are ever drawn.</summary>
        private void DrawManeuver(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            var orange = new Color(255, 170, 90);

            // Planned conics across every SOI patch (the orange route). Pre-burn segments belong to the
            // live/predicted path (drawn cyan elsewhere), so only Planned segments are drawn here.
            var segs = Projection(ut, refreshNodes: false);
            int lastPlanned = -1;
            for (int i = 0; i < segs.Count; i++) if (segs[i].Planned) lastPlanned = i;
            for (int i = 0; i < segs.Count; i++)
            {
                var sg = segs[i];
                if (!sg.Planned) continue;
                // C1: tint each planned leg by its SOI body, blended toward orange so the route reads.
                var legCol = Color.Lerp(sg.Body.BodyColor, orange, 0.6f);
                if (i == lastPlanned)
                {
                    OrbitRenderer.DrawConicGlow(pb, _cam, sg.El, sg.PrimaryAbs, legCol, sg.Body.SoiRadius);
                    // if this final leg is a flyby/capture inside an encountered body, colour its Pe by danger
                    Color peCol = legCol;
                    if (i > 0 && segs[i - 1].Body == sg.Body.Parent) peCol = FlybyDanger(sg.El, sg.Body).col;
                    DrawApPeMarkers(pb, sb, sg.El, sg.PrimaryAbs, sg.Body.Radius, peCol, new Color(255, 210, 140));
                }
                else
                {
                    OrbitRenderer.DrawConic(pb, _cam, sg.El, sg.PrimaryAbs, legCol * 0.8f, 1.3f, sg.Body.SoiRadius);
                }
                // transition marker where this patch hands off to a different SOI
                if (i + 1 < segs.Count && segs[i + 1].Body != sg.Body && !double.IsInfinity(sg.EndUT))
                {
                    var next = segs[i + 1];
                    Vec2d mPos = sg.PrimaryAbs + Kepler.StateAtTime(sg.El, sg.EndUT).pos;
                    var ms = _cam.WorldToScreen(mPos);
                    pb.CircleOutline(ms, 6, 1.5f, orange);

                    // Encounter (descent into a child SOI): draw the encountered body + its SOI ring at the
                    // encounter time, and a closest-approach (Pe) marker on the flyby leg, so the "passage
                    // close to <body>" is visible. Click the marker to frame this flyby (see ClickedEncounterMarker)
                    // and plan a capture node on the otherwise-tiny inside-SOI leg.
                    if (next.Body.Parent == sg.Body)
                    {
                        var encCol = Color.Lerp(next.Body.BodyColor, orange, 0.5f);
                        var bs = _cam.WorldToScreen(next.PrimaryAbs);
                        float soiPx = (float)(next.Body.SoiRadius / _cam.MetersPerPixel);
                        if (soiPx >= 3) pb.CircleOutline(bs, soiPx, 1.2f, encCol * 0.7f);
                        pb.CircleOutline(bs, Math.Max(3f, (float)(next.Body.Radius / _cam.MetersPerPixel)), 1.4f, encCol);
                        sb.DrawString(Ctx.Font, next.Body.Name, ms + new Vector2(9, -7), encCol);
                        // flyby periapsis altitude + danger tag: drawn here for every encounter leg but the
                        // last (that one's Pe is drawn, danger-coloured, by the lastPlanned branch above), so a
                        // too-close / impacting capture is visible before the burn is executed.
                        var (dCol, dWord) = FlybyDanger(next.El, next.Body);
                        if (i + 1 != lastPlanned)
                            DrawApPeMarkers(pb, sb, next.El, next.PrimaryAbs, next.Body.Radius, dCol, new Color(255, 210, 140));
                        if (dWord != null)
                            sb.DrawString(Ctx.Font, dWord, ms + new Vector2(9, 7), dCol);
                    }
                }
            }

            // Reached nodes: frozen reference plots of the burns the player set, kept (rather than
            // auto-cancelled) until deleted. Drawn dimmer than the live plan, around the frozen primary
            // derived from the stored absolute position; only an X to clear, no editable handles.
            var dimOrange = new Color(200, 140, 80);
            foreach (var node in _nodes)
            {
                if (!node.Reached || !node.HasSource || node.Body == null || double.IsNaN(node.Source.A)) continue;
                Vec2d primAbs = ReachedPrimaryAbs(node);
                var res = node.ResultOrbit(node.Source, node.Body.Mu);
                if (!double.IsNaN(res.A))
                {
                    OrbitRenderer.DrawConic(pb, _cam, res, primAbs, dimOrange * 0.7f, 1.2f, node.Body.SoiRadius);
                    DrawApPeMarkers(pb, sb, res, primAbs, node.Body.Radius, dimOrange, dimOrange * 0.8f);
                }
                var rns = ReachedNodeScreen(node);
                pb.CircleOutline(rns, 7, 1.6f, dimOrange);
                pb.FillCircle(rns, 2.5f, dimOrange);
                var rxp = XButtonPos(rns);
                Color rxc = _hoverX == _nodes.IndexOf(node) ? new Color(255, 120, 110) : new Color(200, 180, 180);
                pb.FillCircle(rxp, 7, new Color(40, 20, 24, 220));
                pb.CircleOutline(rxp, 7, 1.5f, rxc);
                pb.Line(rxp + new Vector2(-3, -3), rxp + new Vector2(3, 3), 1.6f, rxc);
                pb.Line(rxp + new Vector2(-3, 3), rxp + new Vector2(3, -3), 1.6f, rxc);
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
                    bool active = (_dragNode == ni && _dragHandle == k) || (_hoverNode == ni && _hoverHandle == k);
                    pb.FillCircle(hp[k], active ? 7f : 5f, hc[k]);
                    // D4: dV tooltip on the hovered/dragged handle (k: 0/1 = prograde +/-, 2/3 = radial out/in).
                    if (active)
                    {
                        double comp = k < 2 ? node.Prograde : node.Radial;
                        string axis = k < 2 ? "pro" : "rad";
                        sb.DrawString(Ctx.Font, $"{axis} {comp:+0.###;-0.###} m/s", hp[k] + new Vector2(9, -6), hc[k]);
                    }
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

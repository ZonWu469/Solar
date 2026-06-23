using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Solar.Core;
using Solar.Parts;
using Solar.Physics;
using Solar.Vessels;

namespace Solar.UI
{
    /// <summary>Flight HUD: readouts, throttle, heading dial, stage list, warp/time display.</summary>
    /// <summary>Actions the player triggered through the HUD this frame.</summary>
    public struct HudResult
    {
        public double? WarpToUT;   // set when the "Warp to maneuver" button was clicked
        public bool FireStage;     // set when the player clicked the stage list to fire the next stage
        public int? RequestedSas;  // set to a SasMode index when an SAS icon was clicked
        public int RightColumnBottom; // screen-Y below the last right-column panel (for overlay panels)
    }

    /// <summary>One SAS-mode icon's render state, computed by the flight scene.</summary>
    public struct SasIconInfo
    {
        public int Icon;        // SasMode index (also selects the glyph)
        public bool Available;  // can be engaged this frame (else greyed, not clickable)
        public bool Active;     // currently the engaged mode
    }

    /// <summary>Target-relative navball cues, computed by the flight scene where target data lives.
    /// Angles are world angles (math convention); NaN means "not shown".</summary>
    public struct NavMarkers
    {
        public bool Active;       // a target is selected
        public string Name;       // target display name (for the left-panel target row)
        public double Target;     // world angle from vessel to target (anti-target is +pi)
        public double RelPro;     // world angle of relative velocity (rel-retro is +pi)
        public string Readout;    // distance + closing speed line under the navball
        public int SasMode;       // 0 = off; >0 = active hold (for the navball label)
        public string SasLabel;   // short hold-mode name, ASCII
        public bool SasEnabled;   // SAS is available (capable part + power); else the icon column is greyed
        public SasIconInfo[] SasIcons; // per-mode icon states for the icon column (null = none)
    }

    public static class Hud
    {
        public static HudResult Draw(GameContext ctx, Vessel v, Prediction pred, bool mapMode, string focusName,
                                Maneuver node = null, double burnDirAngle = double.NaN, double burnSpent = 0, int nodeCount = 0,
                                NavMarkers nav = default, OrbitalElements? flybyEl = null, CelestialBody flybyBody = null)
        {
            var result = new HudResult();
            var pb = ctx.Pb; var sb = ctx.Sb; var f = ctx.Font;
            int w = ctx.W, h = ctx.H;

            // The stage list and the "dV REM" readouts all derive from ComputeStages, which deep-clones
            // the whole part tree. It used to be called ~4x per HUD frame with identical input; compute
            // it once here and reuse. Null when there's nothing to stage (matches the old empty-list path).
            List<StageStat> stages = (v != null && !v.Destroyed && v.Parts.Count > 0)
                ? Staging.ComputeStages(v.Parts) : null;
            double totalDV = 0;
            if (stages != null) for (int i = 0; i < stages.Count; i++) totalDV += stages[i].DeltaV;

            var progTex = ctx.Textures?.Ui("gameplay_progressbar");

            // ---- left vessel panel (flight/nav readouts only; resources live in the Systems panel) ----
            var rowsLeft = new List<(string label, string value, Color c)>();
            void LRow(string label, string value, Color? c = null) => rowsLeft.Add((label, value, c ?? Color.White));

            string vesselName = ctx.Design?.Name;
            if (string.IsNullOrEmpty(vesselName)) vesselName = "Vessel";
            if (v != null && !v.Destroyed)
            {
                double ut0 = ctx.Clock.UT;
                var el = v.CurrentElements(ut0);
                double peAlt = el.Periapsis - v.Body.Radius;
                string apText = el.Hyperbolic ? "-" : UiDraw.Dist(el.Apoapsis - v.Body.Radius);
                LRow("Body", v.Body.Name);
                LRow("Status", Situation(v, el), UiDraw.Accent);
                LRow("Altitude", UiDraw.Dist(v.Altitude));
                LRow("Velocity", UiDraw.Speed(v.Velocity.Length));
                double vspd = v.Velocity.Dot(v.Position.Normalized());
                LRow("Vert. speed", UiDraw.Speed(vspd), Math.Abs(vspd) < 1 ? Color.White : vspd >= 0 ? new Color(150, 220, 150) : new Color(230, 160, 110));
                LRow("Apoapsis", apText + TimeToText(el, Math.PI, ut0, el.Hyperbolic));
                LRow("Periapsis", UiDraw.Dist(peAlt) + TimeToText(el, 0, ut0, false),
                    peAlt < (v.Body.Atmo?.Top ?? 0) ? new Color(255, 170, 90) : Color.White);
                if (!el.Hyperbolic) LRow("Period", UiDraw.Time(el.Period));
                LRow("Mass", $"{v.TotalMass / 1000:0.0} t");
                if (nav.Active && !string.IsNullOrEmpty(nav.Name)) LRow("Target", nav.Name, new Color(235, 130, 235));
            }
            else
            {
                LRow("Status", v == null ? "-" : "DESTROYED", new Color(255, 100, 90));
            }
            {
                const int headerH = 32;   // vessel name + gap before the first row
                var panel = new Rectangle(10, 10, 256, headerH + rowsLeft.Count * 19 + 10);
                UiDraw.TexPanel(pb, ctx, "gameplay_vessel_panel", panel);
                sb.DrawString(f, vesselName, new Vector2(20, panel.Y + 8), UiDraw.Accent);
                float ty = panel.Y + headerH;
                foreach (var (label, value, c) in rowsLeft)
                {
                    sb.DrawString(f, label, new Vector2(20, ty), UiDraw.TextDim);
                    sb.DrawString(f, value, new Vector2(110, ty), c);
                    ty += 19;
                }
            }

            // ---- right column: a top-down stack (time/warp, maneuver, systems, modules, science).
            // Each right-side panel below draws at rColX/rColY and advances rColY. ----
            const int rColW = 230;
            int rColX = w - rColW - 10;
            float rColY = 10;

            // ---- time + warp panel ----
            {
                double warp = ctx.Clock.Warp;
                bool limited = ctx.Clock.WarpIndex > ctx.Clock.EffectiveIndex;
                var twp = new Rectangle(rColX, (int)rColY, rColW, 64);
                UiDraw.TexPanel(pb, ctx, "gameplay_warp_panel", twp);
                string timeText = "T+ " + UiDraw.Time(ctx.Clock.UT);
                sb.DrawString(f, timeText, new Vector2(twp.X + twp.Width / 2 - f.MeasureString(timeText).X / 2, twp.Y + 10), Color.White);
                string warpText = $"WARP {warp:N0}x" + (limited ? " (limited)" : "");
                sb.DrawString(f, warpText, new Vector2(twp.X + twp.Width / 2 - f.MeasureString(warpText).X / 2, twp.Y + 34), warp > 1 ? UiDraw.Accent : UiDraw.TextDim);
                rColY = twp.Bottom + 8;
            }
            if (mapMode)
            {
                string focus = $"MAP - focus: {focusName}  [F] cycle";
                sb.DrawString(f, focus, new Vector2(rColX, rColY), UiDraw.TextDim);
                rColY += 22;
            }

            // ---- encounter banner ----
            if (pred != null && pred.Type != TransitionType.None && v != null && !v.Destroyed)
            {
                string what = pred.Type switch
                {
                    TransitionType.Encounter => $"{pred.NextBody.Name} encounter",
                    TransitionType.Escape => $"Escaping {pred.Body.Name}",
                    _ => pred.Body.Atmo != null ? $"{pred.Body.Name} atmosphere entry" : $"{pred.Body.Name} impact",
                };
                string msg = $"{what} in {UiDraw.Time(pred.TransitionUT - ctx.Clock.UT)}";
                var msz = f.MeasureString(msg);
                sb.DrawString(f, msg, new Vector2(w / 2 - msz.X / 2, 12), new Color(255, 200, 110));
            }

            if (v == null) return result;

            // ---- descent guidance (top center): shows only on a low approach so the player can judge a
            // touchdown. The SAFE/DANGER chip uses the very verdict the landing logic applies, so what the
            // box promises is what happens on contact. ----
            if (!v.Destroyed && !v.Landed)
            {
                var up = v.Position.Normalized();
                double vspeed = v.Velocity.Dot(up);        // + up / - down
                bool descending = vspeed < -0.05;
                if (v.Altitude < 2000 && descending)
                {
                    double speed = v.Velocity.Length;
                    double horiz = Math.Sqrt(Math.Max(0, speed * speed - vspeed * vspeed));
                    double slope = v.Body.Terrain?.SlopeAt(v.Position.Angle()) ?? 0;
                    bool flat = slope <= Solar.Physics.Terrain.LandableSlope;
                    bool safe = v.SurvivesTouchdown(speed) && flat;   // both speed and slope must be OK
                    var dp = new Rectangle(w / 2 - 96, 64, 192, 104);
                    UiDraw.Panel(pb, dp);
                    sb.DrawString(f, "DESCENT", new Vector2(dp.X + 10, dp.Y + 6), UiDraw.Accent);
                    var chip = safe ? new Color(150, 220, 150) : new Color(255, 100, 90);
                    string chipTxt = safe ? "SAFE" : "DANGER";
                    var csz = f.MeasureString(chipTxt);
                    sb.DrawString(f, chipTxt, new Vector2(dp.Right - csz.X - 10, dp.Y + 6), chip);
                    void DRow(int i, string label, string value, Color c)
                    {
                        float ry = dp.Y + 26 + i * 19;
                        sb.DrawString(f, label, new Vector2(dp.X + 10, ry), UiDraw.TextDim);
                        sb.DrawString(f, value, new Vector2(dp.X + 78, ry), c);
                    }
                    DRow(0, "Altitude", UiDraw.Dist(v.Altitude), Color.White);   // above ground (terrain)
                    DRow(1, "Descent", UiDraw.Speed(-vspeed), chip);
                    DRow(2, "Lateral", UiDraw.Speed(horiz), horiz < 2 ? Color.White : new Color(255, 190, 90));
                    // surface verdict for the spot directly below + the rated touchdown speed (margin)
                    if (v.Body.Terrain != null)
                    {
                        var sc = flat ? new Color(150, 220, 150) : new Color(255, 100, 90);
                        sb.DrawString(f, flat ? "LANDABLE" : "TOO STEEP", new Vector2(dp.X + 10, dp.Bottom - 20), sc);
                    }
                    string rated = $"rated {v.SafeLandingSpeed:0} m/s";
                    var rsz = f.MeasureString(rated);
                    sb.DrawString(f, rated, new Vector2(dp.Right - rsz.X - 10, dp.Bottom - 20), UiDraw.TextDim);
                }
            }

            // space-weather state for this vessel, used by both the hazard panel and the navball sun marker
            double hudUt = ctx.Clock.UT;
            StormState storm = v.Destroyed ? default
                : SpaceWeather.ForVessel(ctx.State.WeatherSeed, hudUt, v.AbsolutePosition(hudUt), ctx.Universe);
            bool stormShow = !v.Destroyed && storm.Phase != StormPhase.None;

            // ---- crew hazard readout (top left): solar storm + radiation belt exposure + worst crew dose/illness.
            // Shown only while there is something to warn about, so it stays out of the way otherwise. ----
            if (!v.Destroyed && v.HasCrew)
            {
                double ut = hudUt;
                double beltDose = v.Body?.RadiationAt(v.Altitude) ?? 0;
                double worstDose = 0, worstIll = 0;
                foreach (var c in v.AllCrew())
                { if (c.RadDose > worstDose) worstDose = c.RadDose; if (c.Illness > worstIll) worstIll = c.Illness; }

                if (beltDose > 0 || worstDose > 0 || worstIll > 0 || stormShow)
                {
                    int rows = 2;
                    var hp = new Rectangle(10, 64, 196, 30 + 24 * rows + (stormShow ? 20 : 0));
                    UiDraw.Panel(pb, hp);
                    sb.DrawString(f, "HAZARDS", new Vector2(hp.X + 10, hp.Y + 6), UiDraw.Accent);
                    if (beltDose > 0)
                    {
                        var bsz = f.MeasureString("RADIATION BELT");
                        sb.DrawString(f, "RADIATION BELT", new Vector2(hp.Right - bsz.X - 10, hp.Y + 6), new Color(255, 120, 90));
                    }

                    int rowTop = hp.Y + 26;
                    if (stormShow)
                    {
                        bool active = storm.Phase == StormPhase.Active;
                        bool sheltered = Threats.StormExposure(v, ctx.Universe) < 0.1;
                        double effShield = Threats.BestStormShield(v, storm.SunDir);
                        // pulse the active-storm warning so it reads as urgent
                        bool pulse = !active || ((int)(ut * 2) & 1) == 0;
                        string head = active ? "SOLAR STORM" : "STORM  T-" + UiDraw.Time(storm.TimeToArrival(ut));
                        Color hc = active ? (pulse ? new Color(255, 80, 70) : new Color(180, 60, 55)) : new Color(255, 200, 110);
                        sb.DrawString(f, head, new Vector2(hp.X + 10, rowTop), hc);
                        string sub = sheltered ? "SHELTERED"
                                   : active ? (effShield > 0.5 ? "SHIELDED" : "ALIGN SHIELD -> SUN")
                                   : "ALIGN SHIELD -> SUN";
                        var ssz = f.MeasureString(sub);
                        Color sc = sheltered || (active && effShield > 0.5) ? new Color(150, 220, 150) : new Color(255, 200, 110);
                        sb.DrawString(f, sub, new Vector2(hp.Right - ssz.X - 10, rowTop), sc);
                        rowTop += 22;
                    }

                    void HRow(int i, string label, double frac, Color hot)
                    {
                        float ry = rowTop + i * 24;
                        sb.DrawString(f, label, new Vector2(hp.X + 10, ry), UiDraw.TextDim);
                        var bar = new Rectangle(hp.X + 78, (int)ry + 2, 100, 10);
                        pb.FillRect(bar, new Color(30, 38, 52));
                        frac = Math.Clamp(frac, 0, 1);
                        var col = frac > 0.66 ? new Color(255, 90, 70) : frac > 0.33 ? new Color(255, 190, 90) : hot;
                        if (frac > 0) pb.FillRect(new Rectangle(bar.X, bar.Y, (int)(bar.Width * frac), bar.Height), col);
                    }
                    HRow(0, "Radiation", worstDose / Threats.RadDeathDose, new Color(150, 220, 150));
                    HRow(1, "Illness", worstIll, new Color(150, 220, 150));
                }
            }

            // ---- heading dial + throttle (bottom center); compass enlarged 1.5x ----
            var dial = new Vector2(w / 2f, h - 110);
            float dr = 72;
            pb.FillCircle(dial, dr + 4, new Color(10, 16, 28, 200));
            pb.CircleOutline(dial, dr, 2, UiDraw.PanelBorder);
            // tick marks
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                var d = new Vector2((float)Math.Cos(a), -(float)Math.Sin(a));
                pb.Line(dial + d * (dr - 5), dial + d * dr, 1.5f, UiDraw.TextDim);
            }
            // navball directional cues rendered as icon art (texture centered on the marker position)
            const int markSz = 26;
            void Mark(Vector2 pos, string id) => UiDraw.Icon(pb, ctx.Textures?.Ui(id), pos, markSz, Color.White);
            if (v.Velocity.Length > 1)
            {
                double va = v.Velocity.Angle();
                var pd = new Vector2((float)Math.Cos(va), -(float)Math.Sin(va));
                Mark(dial + pd * (dr - 10), "icon_prograde");        // prograde
                Mark(dial - pd * (dr - 10), "icon_retrograde");      // retrograde
            }
            // solar-storm cue: mark the Sun's direction and shade the shielded cone around the Up (heading) axis,
            // so the player can read at a glance whether the shielded face is turned toward an incoming front.
            if (stormShow)
            {
                bool active = storm.Phase == StormPhase.Active;
                double sunA = storm.SunDir.Angle();
                var sdir = new Vector2((float)Math.Cos(sunA), -(float)Math.Sin(sunA));
                // shielded cone (~55 deg half-angle) centred on the ship's Up axis = Heading
                double align = Math.Max(0, Math.Cos(v.Heading - sunA));
                bool aligned = align > 0.5;
                Color coneCol = aligned ? new Color(120, 220, 140, 70) : new Color(255, 120, 90, 60);
                // dial draws world angle a as screen point (cos a, -sin a), so screen angle = -a; centre on Heading
                pb.RingArc(dial, 0, dr - 6, (float)(-v.Heading - 0.96), (float)(-v.Heading + 0.96), coneCol, coneCol, 0);
                // Sun marker on the rim, pulsing red when the storm is live
                Color sunCol = active ? (((int)(hudUt * 2) & 1) == 0 ? new Color(255, 90, 70) : new Color(255, 170, 90))
                                      : new Color(255, 200, 110);
                var sp = dial + sdir * (dr - 10);
                pb.FillCircle(sp, 6, sunCol);
                for (int k = 0; k < 8; k++)
                {
                    double a = k * Math.PI / 4;
                    var d = new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
                    pb.Line(sp + d * 7, sp + d * 11, 1.5f, sunCol);
                }
            }
            var hd = new Vector2((float)Math.Cos(v.Heading), -(float)Math.Sin(v.Heading));
            pb.Line(dial, dial + hd * (dr - 4), 3f, Color.White);
            // maneuver burn marker: point the heading at this to execute the planned node
            if (node != null && !double.IsNaN(burnDirAngle))
            {
                var md = new Vector2((float)Math.Cos(burnDirAngle), -(float)Math.Sin(burnDirAngle));
                Mark(dial + md * (dr - 10), "icon_maneuver");
            }
            // target navball cues: where the target is (and its opposite) + relative-velocity markers
            if (nav.Active)
            {
                if (!double.IsNaN(nav.Target))
                {
                    var td = new Vector2((float)Math.Cos(nav.Target), -(float)Math.Sin(nav.Target));
                    Mark(dial + td * (dr - 10), "icon_target");          // target
                    Mark(dial - td * (dr - 10), "icon_antitarget");      // anti-target
                }
                if (!double.IsNaN(nav.RelPro))
                {
                    var rd = new Vector2((float)Math.Cos(nav.RelPro), -(float)Math.Sin(nav.RelPro));
                    var rc = new Color(255, 170, 235);                           // rel-velocity, distinct from orbital
                    pb.FillCircle(dial + rd * (dr - 22), 3.5f, rc);              // target-relative prograde (no icon)
                    Mark(dial - rd * (dr - 22), "icon_relretro");               // target-relative retrograde
                }
            }
            // target distance + closing speed on top of the compass
            if (nav.Active && !string.IsNullOrEmpty(nav.Readout))
            {
                var rsz = f.MeasureString(nav.Readout);
                sb.DrawString(f, nav.Readout, new Vector2(dial.X - rsz.X / 2, dial.Y - dr - 22), new Color(235, 130, 235));
            }
            if (nav.SasMode > 0 && !string.IsNullOrEmpty(nav.SasLabel))
            {
                string s = "SAS: " + nav.SasLabel;
                var ssz = f.MeasureString(s);
                sb.DrawString(f, s, new Vector2(dial.X - ssz.X / 2, dial.Y + dr + 4), UiDraw.Accent);
            }

            // vertical throttle bar (textured progress bar), left of the dial
            var thrRect = new Rectangle((int)(dial.X - dr - 42), (int)(dial.Y - dr), 18, (int)(dr * 2));
            UiDraw.TexBarV(pb, progTex, thrRect, (float)v.Throttle, new Color(255, 170, 60));
            sb.DrawString(f, $"THR {v.Throttle * 100:0}%", new Vector2(thrRect.X - 6, thrRect.Bottom + 4), UiDraw.TextDim);

            // ---- SAS mode buttons in 2 columns, right of the compass (clear of the accel panel) ----
            if (nav.SasIcons != null && nav.SasIcons.Length > 0)
            {
                const int isz = 24, gap = 6, colGap = 6;
                // rows: (left icon, right icon); -1 means "single, centered". Off (0) has no icon and is omitted.
                var rows = new (int left, int right)[] {
                    (2, 3),   // Prograde / Retrograde
                    (4, 5),   // Radial In / Radial Out
                    (6, 7),   // Target / Anti-Target
                    (8, 9),   // Kill Relative / Maneuver
                    (1, 10),  // Stability / Shield -> Sun
                };
                int nRows = rows.Length;
                int gridH = nRows * isz + (nRows - 1) * gap;
                // sits just right of the compass, bottom-aligned with the dial
                int gridLeft = (int)(dial.X + dr) + 30;
                int gridTop = (int)(dial.Y + dr) - gridH;

                SasIconInfo ByIdx(int idx)
                {
                    foreach (var icon in nav.SasIcons)
                        if (icon.Icon == idx) return icon;
                    return default;
                }

                int hoverIcon = -1; Rectangle hoverRect = default;   // tooltip drawn after the grid (on top)

                void DrawBtn(SasIconInfo icon, Rectangle r)
                {
                    bool clickable = nav.SasEnabled && icon.Available;
                    // tooltip on hover regardless of clickability, so a mode shows why it's unavailable too
                    bool over = r.Contains((int)ctx.Input.MousePos.X, (int)ctx.Input.MousePos.Y);
                    bool hover = clickable && over;
                    UiDraw.SasIconTex(pb, ctx, r, icon.Icon, icon.Active, clickable, hover);
                    if (hover && ctx.Input.LeftClick) result.RequestedSas = icon.Icon;
                    if (over) { hoverIcon = icon.Icon; hoverRect = r; }
                }

                for (int row = 0; row < nRows; row++)
                {
                    var spec = rows[row];
                    int iy = gridTop + row * (isz + gap);
                    if (spec.right < 0)
                    {
                        // single button, centered across the two-column width
                        int cx = gridLeft + (isz * 2 + colGap) / 2;
                        DrawBtn(ByIdx(spec.left), new Rectangle(cx - isz / 2, iy, isz, isz));
                    }
                    else
                    {
                        DrawBtn(ByIdx(spec.left), new Rectangle(gridLeft, iy, isz, isz));
                        DrawBtn(ByIdx(spec.right), new Rectangle(gridLeft + isz + colGap, iy, isz, isz));
                    }
                }

                if (hoverIcon >= 0 && hoverIcon < UiDraw.SasModeNames.Length)
                {
                    string name = UiDraw.SasModeNames[hoverIcon];
                    string desc = hoverIcon < UiDraw.SasModeDescriptions.Length ? UiDraw.SasModeDescriptions[hoverIcon] : "";
                    if (!ByIdx(hoverIcon).Available && nav.SasEnabled) desc += "  (unavailable now)";
                    UiDraw.Tooltip(pb, sb, f, name, desc, new Vector2(hoverRect.Right, hoverRect.Top), w, h);
                }
            }

            // ---- thrust (upper-left) and accel (upper-right) panels flanking the compass ----
            if (!v.Destroyed)
            {
                double g = v.Body.Mu / Math.Max(1, v.Position.LengthSquared);
                double thrust = v.CurrentThrust;
                double mass = v.TotalMass;
                double twr = g > 0 ? thrust / (mass * g) : 0;
                double accel = thrust / mass;

                const int fpW = 130, fpH = 56, fpGap = 16;
                int fpY = (int)(dial.Y - dr - fpH + 4);   // tops of the flank panels just above the dial top
                var tp = new Rectangle((int)(dial.X - dr - fpGap - fpW), fpY, fpW, fpH);
                var ap = new Rectangle((int)(dial.X + dr + fpGap), fpY, fpW, fpH);
                UiDraw.TexPanel(pb, ctx, "gameplay_thrust_panel", tp, 18);
                UiDraw.TexPanel(pb, ctx, "gameplay_accel_panel", ap, 18);

                void CenterText(string s, int cx, int y, Color c) =>
                    sb.DrawString(f, s, new Vector2(cx - f.MeasureString(s).X / 2, y), c);
                void CenterBold(string s, int cx, int y, Color c) =>
                    UiDraw.BoldText(sb, f, s, new Vector2(cx - f.MeasureString(s).X / 2, y), c);

                CenterText("THRUST", tp.Center.X, tp.Y + 8, UiDraw.TextDim);
                CenterBold(UiDraw.Force(thrust), tp.Center.X, tp.Y + 26, thrust > 0 ? Color.White : UiDraw.TextDim);
                string twrTxt = twr > 0 ? $"TWR {twr:0.00}" : "TWR -";
                sb.DrawString(f, twrTxt, new Vector2(tp.Right - f.MeasureString(twrTxt).X - 12, tp.Y + 8),
                    twr >= 1 ? new Color(150, 220, 150) : twr > 0 ? new Color(255, 170, 90) : UiDraw.TextDim);

                CenterText("ACCEL", ap.Center.X, ap.Y + 8, UiDraw.TextDim);
                CenterBold(thrust > 0 ? UiDraw.Accel(accel) : "-", ap.Center.X, ap.Y + 26, Color.White);
            }

            // ---- stage list (bottom left) ----
            // Show every stage still on the craft: the burning one keeps draining its fuel bar until its
            // parts are dropped, and decoupled stages fall off the list on their own (their parts leave
            // v.Parts). The row Staging.FireNext will fire next -- NextEventStage, kept in lock-step with
            // Vessel.CurrentStage -- is the highlighted, clickable one.
            int nextEvent = (v != null && !v.Destroyed && v.Parts.Count > 0) ? Staging.NextEventStage(v) : -1;
            if (stages != null && !v.Destroyed && v.Parts.Count > 0 && stages.Count > 0)
            {
                int rows = Math.Min(stages.Count, 6);
                var sp = new Rectangle(10, h - 30 - rows * 38 - 28 - 30, 300, rows * 38 + 36 + 30);
                UiDraw.TexPanel(pb, ctx, "gameplay_vessel_panel", sp);
                sb.DrawString(f, $"STAGES  dV {totalDV:0} m/s  [Space]/click fire", new Vector2(sp.X + 8, sp.Y + 6), UiDraw.TextDim);
                float sy = sp.Y + 58;
                for (int i = 0; i < rows; i++)
                {
                    var st = stages[i];
                    // the next-to-fire row is clickable: clicking it fires the next stage, like [Space]
                    var rowRect = new Rectangle(sp.X + 4, (int)sy - 4, sp.Width - 8, 34);
                    bool active = st.Number == nextEvent;
                    bool hover = active && rowRect.Contains((int)ctx.Input.MousePos.X, (int)ctx.Input.MousePos.Y);
                    if (hover) pb.FillRect(rowRect, new Color(60, 95, 140, 120));
                    if (hover && ctx.Input.LeftClick) result.FireStage = true;
                    Color rc = active ? Color.White : UiDraw.TextDim;
                    // icon + "S{n} {Action}" on the top line, right-aligned dV; truncate the label so it can't reach the dV
                    UiDraw.StageIcon(pb, new Rectangle(sp.X + 8, (int)sy + 2, 12, 12), st);
                    string dvText = st.DeltaV > 0 ? $"dV {st.DeltaV:0} m/s" : "";
                    float dvX = sp.Right - 8 - f.MeasureString(dvText).X;
                    if (dvText.Length > 0) sb.DrawString(f, dvText, new Vector2(dvX, sy), active ? UiDraw.Accent : UiDraw.TextDim);
                    string full = $"S{st.Number} {st.Action}";
                    string label = full;
                    float labelX = sp.X + 24;
                    float maxLabelW = dvX - labelX - 8;
                    while (label.Length > 3 && f.MeasureString(label).X > maxLabelW) label = label.Substring(0, label.Length - 1);
                    if (label != full) label = label.Substring(0, Math.Max(3, label.Length - 2)) + "..";
                    sb.DrawString(f, label, new Vector2(labelX, sy), rc);
                    // second line: what the stage ignites/drops, scaled down and clipped to the panel
                    string detail = UiDraw.StageDetail(st);
                    if (detail.Length > 0)
                    {
                        float maxW = sp.Width - 32;
                        while (detail.Length > 3 && f.MeasureString(detail).X * 0.78f > maxW) detail = detail.Substring(0, detail.Length - 1);
                        UiDraw.SmallText(sb, f, detail, new Vector2(sp.X + 24, sy + 15), UiDraw.TextDim, 0.78f);
                    }
                    UiDraw.TexBar(pb, progTex, new Rectangle(sp.X + 8, (int)sy + 28, sp.Width - 16, 8), (float)StageFuelFrac(st), new Color(120, 200, 120));
                    sy += 38;
                }
            }

            // ---- maneuver node panel (top right) ----
            if (node != null && !v.Destroyed)
            {
                double now = ctx.Clock.UT;
                double avail = totalDV;
                bool done = node.Reached;   // burn time has passed: show the executed plan, no live cues
                bool enough = node.DeltaV <= avail + 1e-6;
                double bt = Staging.BurnTime(v, node.DeltaV);
                bool burning = burnSpent > 0;
                var mp = new Rectangle(rColX, (int)rColY, rColW, 248);
                UiDraw.TexPanel(pb, ctx, "gameplay_modules_panel", mp);
                float my = mp.Y + 8;
                void MRow(string label, string value, Color c)
                {
                    sb.DrawString(f, label, new Vector2(mp.X + 10, my), UiDraw.TextDim);
                    sb.DrawString(f, value, new Vector2(mp.X + 104, my), c);
                    my += 18;
                }
                string mTitle = done ? "MANEUVER (done)  X to clear"
                    : nodeCount > 1 ? $"MANEUVER (next of {nodeCount})  [Del]" : "MANEUVER  [Del] / X to clear";
                sb.DrawString(f, mTitle, new Vector2(mp.X + 10, my), done ? new Color(200, 140, 80) : UiDraw.Accent);
                my += 22;
                MRow("dV req", $"{node.DeltaV:0} m/s", enough ? Color.White : new Color(255, 110, 100));
                MRow("pro/rad", $"{node.Prograde:+0.###;-0.###} / {node.Radial:+0.###;-0.###}", UiDraw.TextDim);
                MRow("dV avail", $"{avail:0} m/s", enough ? new Color(150, 220, 150) : new Color(255, 110, 100));
                MRow("burn time", enough && bt > 0 ? UiDraw.Time(bt) : "-", Color.White);
                if (done)
                {
                    // executed node: T+ since the burn time, no upcoming-burn/warp cues
                    MRow("executed", $"T+ {UiDraw.Time(now - node.UT)}", new Color(200, 140, 80));
                }
                else
                {
                    double toNode = node.UT - now;
                    MRow("node in", UiDraw.Time(Math.Max(0, toNode)), toNode < 0 ? new Color(255, 170, 90) : Color.White);
                    // ignition is when the (node-centred) burn should start: node - half the burn
                    double burnIn = node.UT - (enough && bt > 0 ? bt / 2 : 0) - now;
                    MRow("burn in", enough && bt > 0 ? UiDraw.Time(Math.Max(0, burnIn)) : "-",
                         burnIn < 0 ? new Color(255, 110, 100) : new Color(255, 210, 140));
                }
                // resulting orbit of this burn (apsides above the body surface), so the consequence is
                // visible numerically while tuning the node -- not only as map chevrons.
                if (node.HasSource && node.Body != null)
                {
                    var res = node.ResultOrbit(node.Source, node.Body.Mu);
                    if (!double.IsNaN(res.A))
                    {
                        if (!res.Hyperbolic)
                            MRow("result Ap", UiDraw.Dist(res.Apoapsis - node.Body.Radius), new Color(255, 210, 140));
                        MRow("result Pe", UiDraw.Dist(res.Periapsis - node.Body.Radius), new Color(255, 210, 140));
                    }
                }
                // flyby periapsis + danger if the planned route then drops into a body's SOI: the headline
                // "will this bring me too close?" answered before the burn is even executed.
                if (flybyEl.HasValue && flybyBody != null)
                {
                    var (fc, _) = FlybyRow(flybyEl.Value, flybyBody, out string fval);
                    MRow($"Pe @ {flybyBody.Name}", fval, fc);
                }
                if (burning)
                    MRow("remaining", $"{Math.Max(0, node.DeltaV - burnSpent):0} m/s", UiDraw.Accent);
                else if (!done)
                {
                    // the burn is centred on the node, so ignition is half a burn before it; stop the warp a
                    // comfortable lead earlier (room to rotate to the burn vector and settle), not right on it
                    double burnStart = node.UT - (enough && bt > 0 ? bt / 2 : 0);
                    const double WarpLead = 300;   // s of coast left before ignition
                    double warpTarget = burnStart - WarpLead;
                    bool canWarp = warpTarget > now + 1;
                    var br = new Rectangle(mp.X + 10, mp.Bottom - 30, mp.Width - 20, 22);
                    if (UiDraw.Button(pb, sb, f, br, "Warp to maneuver", ctx.Input, canWarp))
                        result.WarpToUT = warpTarget;
                }
                rColY = mp.Bottom + 8;
            }

            // ---- systems panel (right): resource readouts (EC, life support, RCS, ore) with bars,
            // moved out of the left vessel panel. Each entry is a text row, optionally with a thin bar. ----
            if (!v.Destroyed)
            {
                // entries: label, value, text colour; bar = whether to draw a fill bar; frac/barCol for it
                var sysRows = new List<(string label, string value, Color c, bool bar, float frac, Color barCol)>();
                void SRow(string label, string value, Color c) => sysRows.Add((label, value, c, false, 0f, default));
                void SBar(string label, string value, Color c, double frac, Color barCol) =>
                    sysRows.Add((label, value, c, true, (float)frac, barCol));
                var green = new Color(150, 220, 150); var amber = new Color(255, 190, 90);
                var warn = new Color(255, 140, 90); var red = new Color(255, 100, 90);
                Color LsCol(double a, double cap) => a <= 0 ? red : a < cap * 0.15 ? amber : Color.White;
                int crew = v.CrewCount;
                bool hasLs = v.CrewCount > 0 || v.LsCapacity > 0;

                // order: Crew, EC rate, Power, Oxygen, Water, Food, then Supply / RCS / Ore / pressure
                if (hasLs)
                    SRow("Crew", $"{crew}/{v.SeatCount}", !v.LifeSupportOk ? red : Color.White);
                if (v.EcCapacity > 0)
                {
                    v.EcRates(ctx.Clock.UT, ctx.Universe, out double ecProd, out double ecDraw);
                    double ecNet = ecProd - ecDraw;
                    SRow("EC rate", ecNet >= 0 ? $"+{ecNet:0.#}/s" : $"{ecNet:0.#}/s", ecNet < 0 ? warn : green);
                    Color barCol = v.ElectricCharge <= 0 ? warn : green;
                    SBar("Power", $"{v.ElectricCharge:0}/{v.EcCapacity:0}", v.ElectricCharge <= 0 ? warn : Color.White,
                         v.ElectricCharge / v.EcCapacity, barCol);
                }
                if (hasLs)
                {
                    void Ls(string label, double a, double cap, double rate)
                    {
                        if (cap <= 0) { SRow(label, "none", red); return; }
                        string val = rate > 0 ? UiDraw.Time(a / rate) : $"{a / cap * 100:0}%";
                        SBar(label, val, LsCol(a, cap), a / cap, a <= 0 ? red : a < cap * 0.15 ? amber : green);
                    }
                    Ls("Oxygen", v.Oxygen, v.OxygenCapacity, crew * Vessel.OxygenPerCrew);
                    Ls("Water", v.Water, v.WaterCapacity, crew * Vessel.WaterPerCrew);
                    Ls("Food", v.Food, v.FoodCapacity, crew * Vessel.FoodPerCrew);
                    if (crew > 0)
                    {
                        double end = v.LifeSupportEndurance();
                        SRow("Supply", v.SelfSustaining ? "self-sustaining" : UiDraw.Time(end),
                             v.SelfSustaining ? green : end < 6 * 3600 ? amber : Color.White);
                    }
                }
                if (v.RcsBlocks > 0 || v.MonopropCapacity > 0)
                {
                    string status; Color rcsCol;
                    if (!v.RcsEnabled)                { status = "off (press R)";    rcsCol = UiDraw.TextDim; }
                    else if (v.RcsBlocks == 0)        { status = "no thrusters";     rcsCol = warn; }
                    else if (v.MonopropCapacity <= 0) { status = "no monoprop tank"; rcsCol = warn; }
                    else if (v.Monoprop <= 0)         { status = "dry";              rcsCol = warn; }
                    else if (v.ElectricCharge <= 0)   { status = "no power";         rcsCol = warn; }
                    else                              { status = "on";               rcsCol = green; }
                    if (v.MonopropCapacity > 0)
                        SBar("RCS", $"{v.Monoprop:0}/{v.MonopropCapacity:0} {status}", rcsCol, v.Monoprop / v.MonopropCapacity, green);
                    else
                        SRow("RCS", status, rcsCol);
                }
                if (v.OreCapacity > 0 || v.ScannerOperational)
                {
                    if (v.OreCapacity > 0)
                        SBar("Ore", $"{v.Ore:0}/{v.OreCapacity:0}", v.Ore > 0 ? Color.White : UiDraw.TextDim,
                             v.Ore / v.OreCapacity, new Color(190, 160, 110));
                    bool surveyed = ctx.State != null && ctx.State.SurveyedBodies.Contains(v.Body.Name);
                    string richTxt = v.Body.Parent == null ? "no surface" : surveyed ? $"{v.Body.OreRichness * 100:0}% ore" : "unsurveyed";
                    Color richCol = v.Body.Parent == null ? warn : !surveyed ? UiDraw.TextDim
                                  : v.Body.OreRichness > 0 ? green : warn;
                    SRow("Surface ore", richTxt, richCol);
                }
                double atmoTop = v.Body.Atmo?.Top ?? 0;
                if (v.Body.Atmo != null && v.Altitude < atmoTop)
                {
                    double q = 0.5 * v.Body.Atmo.DensityAt(v.Altitude) * v.Velocity.LengthSquared;
                    SRow("Dyn. pressure", UiDraw.Pressure(q), q > 30_000 ? warn : Color.White);
                }

                if (sysRows.Count > 0)
                {
                    int hh = 24;
                    foreach (var e in sysRows) hh += e.bar ? 28 : 19;
                    hh += 30;
                    var rp = new Rectangle(rColX, (int)rColY, rColW, hh);
                    UiDraw.TexPanel(pb, ctx, "gameplay_modules_panel", rp);
                    sb.DrawString(f, "SYSTEMS", new Vector2(rp.X + 12, rp.Y + 6), UiDraw.Accent);
                    float yy = rp.Y + 26;
                    foreach (var (label, value, c, bar, frac, barCol) in sysRows)
                    {
                        sb.DrawString(f, label, new Vector2(rp.X + 12, yy), UiDraw.TextDim);
                        sb.DrawString(f, value, new Vector2(rp.X + 96, yy), c);
                        yy += 18;
                        if (bar)
                        {
                            UiDraw.TexBar(pb, progTex, new Rectangle(rp.X + 12, (int)yy, rColW - 24, 8), frac, barCol);
                            yy += 10;
                        }
                    }
                    rColY = rp.Bottom + 8;
                }
            }

            // ---- module + science panels (right): icon grids. Modules holds the toggleable + passive
            // system modules; Science holds the instruments. Each tile lit when functioning, dimmed when
            // not, green/red status border, amber-red X when broken. ----
            if (!v.Destroyed)
            {
                var modules = new List<ModuleInstance>();   // everything except science instruments
                var science = new List<ModuleInstance>();
                foreach (var p in v.AllParts())
                    foreach (var m in p.Modules)
                        (m.Def.Kind == ModuleKind.Science ? science : modules).Add(m);

                const int tile = 30, gap = 6, cols = 6, pad = 10, labelH = 18;
                int Rows(int n) => n == 0 ? 0 : (n + cols - 1) / cols;
                ModuleInstance tip = null;   // hovered module, tooltip painted after both grids

                // Draws a titled icon grid at rColX/rColY and advances rColY. clickable = toggles on click.
                void Panel(string title, List<ModuleInstance> mods)
                {
                    if (mods.Count == 0) return;
                    int bodyH = 6 + labelH + Rows(mods.Count) * (tile + gap) + 30;
                    var rp = new Rectangle(rColX, (int)rColY, rColW, bodyH);
                    UiDraw.TexPanel(pb, ctx, "gameplay_modules_panel", rp);
                    sb.DrawString(f, title, new Vector2(rp.X + pad, rp.Y + 6), UiDraw.Accent);
                    int gy = rp.Y + 6 + labelH;
                    for (int i = 0; i < mods.Count; i++)
                    {
                        var m = mods[i];
                        int col = i % cols, row = i / cols;
                        var tr = new Rectangle(rp.X + pad + col * (tile + gap), gy + row * (tile + gap), tile, tile);
                        bool func = v.ModuleFunctioning(m, ctx.Clock.UT, ctx.Universe);
                        pb.FillRect(tr, new Color(18, 26, 40, 230));
                        UiDraw.Icon(pb, ctx.Textures?.Module(m.Def.Id), new Rectangle(tr.X + 2, tr.Y + 2, tile - 4, tile - 4), m.Def.Tint, !func);
                        var border = m.Broken ? new Color(255, 90, 60) : func ? UiDraw.StatusOn : UiDraw.StatusOff;
                        pb.RectOutline(tr, 2, border);
                        if (m.Broken) pb.Line(new Vector2(tr.X + 4, tr.Y + 4), new Vector2(tr.Right - 4, tr.Bottom - 4), 2, border);
                        bool hover = tr.Contains((int)ctx.Input.MousePos.X, (int)ctx.Input.MousePos.Y);
                        if (hover) tip = m;
                        if (hover && ctx.Input.LeftClick)
                        {
                            // RCS modules are Activatable:false; their on/off is the vessel flag (also [R])
                            if (m.Def.Kind == ModuleKind.RCS) v.RcsEnabled = !v.RcsEnabled;
                            else if (m.Def.Activatable) m.Active = !m.Active;
                        }
                    }
                    rColY = rp.Bottom + 8;
                }

                Panel("MODULES  [G] solar", modules);
                Panel("SCIENCE", science);
                if (tip != null) UiDraw.ModuleTooltip(pb, sb, f, tip.Def, ctx.Input.MousePos, w, h);
            }

            // ---- controls hint (bottom right) ----
            string hint = mapMode
                ? "[click] orbit=node  [drag] handles  [wheel] tune (Shift/Alt finer)  X=del  [arrows] pan  [Tab/T] target  [F] focus  [M] flight"
                : "[Shift/Ctrl] throttle  [A/D] rotate  [H] SAS  [L] gear  [Space] stage  [Tab/T] target  [K] dock  [U] undock  [C] crew  [B] base  [M] map  [,/.] warp";
            var hsz = f.MeasureString(hint);
            sb.DrawString(f, hint, new Vector2(w - hsz.X - 12, h - 24), new Color(120, 132, 150));

            result.RightColumnBottom = (int)rColY;
            return result;
        }

        /// <summary>Colour + formatted value ("&lt;alt&gt; IMPACT/ENTRY" or just the altitude) for a flyby
        /// periapsis readout, from <see cref="TrajectoryPredictor.ClassifyFlyby"/>. ASCII only.</summary>
        private static (Color col, string tag) FlybyRow(in OrbitalElements el, CelestialBody body, out string value)
        {
            var outcome = TrajectoryPredictor.ClassifyFlyby(el, body, out double peAlt);
            (Color col, string tag) r = outcome switch
            {
                FlybyOutcome.Impact => (new Color(255, 90, 80), "IMPACT"),
                FlybyOutcome.AtmoEntry => (new Color(255, 170, 90), "ENTRY"),
                _ => (new Color(140, 230, 160), ""),
            };
            value = r.tag.Length > 0 ? $"{UiDraw.Dist(peAlt)} {r.tag}" : UiDraw.Dist(peAlt);
            return r;
        }

        /// <summary>" (in MM:SS)" until the orbit next reaches true anomaly nu, or "" if not applicable.</summary>
        private static string TimeToText(in OrbitalElements el, double nu, double ut, bool skip)
        {
            if (skip || double.IsNaN(el.A)) return "";
            double t = Kepler.TimeAtTrueAnomaly(el, nu, ut) - ut;
            if (t <= 0 || double.IsNaN(t) || double.IsInfinity(t)) return "";
            return $"  ({UiDraw.Time(t)})";
        }

        private static double StageFuelFrac(StageStat st)
            // ComputeStages already aggregates the stage's live fuel and capacity (axial parts plus the
            // radials that ride/drop with it), so use those directly — re-deriving from axial segments
            // missed radial fuel and mis-mapped stage numbers once separate radial-booster stages exist.
            => st.FuelCap > 0 ? st.Fuel / st.FuelCap : 0;

        private static string Situation(Vessel v, in OrbitalElements el)
        {
            if (v.Landed) return "Landed";
            double atmoTop = v.Body.Atmo?.Top ?? 0;
            if (atmoTop > 0 && v.Altitude < atmoTop) return "In atmosphere";
            if (el.Hyperbolic) return "Escape trajectory";
            if (el.Periapsis - v.Body.Radius > atmoTop) return "Orbiting";
            return "Sub-orbital";
        }
    }
}

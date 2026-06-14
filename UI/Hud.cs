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
                                NavMarkers nav = default)
        {
            var result = new HudResult();
            var pb = ctx.Pb; var sb = ctx.Sb; var f = ctx.Font;
            int w = ctx.W, h = ctx.H;

            // ---- left readout panel ----
            var panel = new Rectangle(10, 10, 256, 366);
            UiDraw.Panel(pb, panel);
            float ty = 18;
            void Row(string label, string value, Color? c = null)
            {
                sb.DrawString(f, label, new Vector2(20, ty), UiDraw.TextDim);
                sb.DrawString(f, value, new Vector2(110, ty), c ?? Color.White);
                ty += 19;
            }
            // One life-support resource: time-to-empty while crewed, else a stocked percentage; storage of
            // zero shows "none" (that resource is the death driver). Amber under 15%, red when empty.
            void LsRow(string label, double amount, double cap, double ratePerSec)
            {
                if (cap <= 0) { Row(label, "none", new Color(255, 100, 90)); return; }
                string value = ratePerSec > 0 ? UiDraw.Time(amount / ratePerSec) : $"{amount / cap * 100:0}%";
                Color c = amount <= 0 ? new Color(255, 100, 90)
                        : amount < cap * 0.15 ? new Color(255, 190, 90) : Color.White;
                Row(label, value, c);
            }

            if (v != null && !v.Destroyed)
            {
                double ut = ctx.Clock.UT;
                var el = v.CurrentElements(ut);
                double peAlt = el.Periapsis - v.Body.Radius;
                string apText = el.Hyperbolic ? "-" : UiDraw.Dist(el.Apoapsis - v.Body.Radius);
                Row("Body", v.Body.Name);
                Row("Status", Situation(v, el), UiDraw.Accent);
                Row("Altitude", UiDraw.Dist(v.Altitude));
                Row("Velocity", UiDraw.Speed(v.Velocity.Length));
                double vspeed = v.Velocity.Dot(v.Position.Normalized());
                Row("Vert. speed", UiDraw.Speed(vspeed), Math.Abs(vspeed) < 1 ? Color.White : vspeed >= 0 ? new Color(150, 220, 150) : new Color(230, 160, 110));
                Row("Apoapsis", apText + TimeToText(el, Math.PI, ut, el.Hyperbolic));
                Row("Periapsis", UiDraw.Dist(peAlt) + TimeToText(el, 0, ut, false),
                    peAlt < (v.Body.Atmo?.Top ?? 0) ? new Color(255, 170, 90) : Color.White);
                if (!el.Hyperbolic) Row("Period", UiDraw.Time(el.Period));
                Row("Mass", $"{v.TotalMass / 1000:0.0} t");
                double atmoTop = v.Body.Atmo?.Top ?? 0;
                if (v.Body.Atmo != null && v.Altitude < atmoTop)
                {
                    double rho = v.Body.Atmo.DensityAt(v.Altitude);
                    double q = 0.5 * rho * v.Velocity.LengthSquared;
                    Row("Dyn. pressure", UiDraw.Pressure(q), q > 30_000 ? new Color(255, 140, 90) : Color.White);
                }
                if (v.EcCapacity > 0)
                {
                    Row("Charge", $"{v.ElectricCharge:0}/{v.EcCapacity:0}",
                        v.ElectricCharge <= 0 ? new Color(255, 140, 90) : Color.White);
                    v.EcRates(ctx.Clock.UT, ctx.Universe, out double ecProd, out double ecDraw);
                    double ecNet = ecProd - ecDraw;
                    string ecRateTxt = ecNet >= 0 ? $"+{ecNet:0.#}/s" : $"{ecNet:0.#}/s";
                    Row("EC rate", ecRateTxt, ecNet < 0 ? new Color(255, 140, 90) : new Color(150, 220, 150));
                }
                if (v.MonopropCapacity > 0)
                {
                    string rcs = v.RcsEnabled ? (v.Monoprop > 0 ? "RCS on" : "RCS dry") : "RCS off";
                    Color rcsCol = !v.RcsEnabled ? UiDraw.TextDim
                                 : v.Monoprop <= 0 ? new Color(255, 140, 90) : new Color(150, 220, 150);
                    Row("Monoprop", $"{v.Monoprop:0}/{v.MonopropCapacity:0}  {rcs}", rcsCol);
                }
                if (v.CrewCount > 0 || v.LsCapacity > 0)
                {
                    int crew = v.CrewCount;
                    Color lsCol = !v.LifeSupportOk ? new Color(255, 100, 90) : Color.White;
                    Row("Crew", $"{crew}/{v.SeatCount}", lsCol);
                    LsRow("Oxygen", v.Oxygen, v.OxygenCapacity, crew * Vessel.OxygenPerCrew);
                    LsRow("Water", v.Water, v.WaterCapacity, crew * Vessel.WaterPerCrew);
                    LsRow("Food", v.Food, v.FoodCapacity, crew * Vessel.FoodPerCrew);
                }
            }
            else
            {
                Row("Status", v == null ? "-" : "DESTROYED", new Color(255, 100, 90));
            }

            // ---- top right: time + warp ----
            string utText = "T+ " + UiDraw.Time(ctx.Clock.UT);
            double warp = ctx.Clock.Warp;
            bool limited = ctx.Clock.WarpIndex > ctx.Clock.EffectiveIndex;
            string warpText = $"WARP {warp:N0}x" + (limited ? " (limited)" : "");
            var utSz = f.MeasureString(utText);
            sb.DrawString(f, utText, new Vector2(w - utSz.X - 16, 12), Color.White);
            var wpSz = f.MeasureString(warpText);
            sb.DrawString(f, warpText, new Vector2(w - wpSz.X - 16, 32), warp > 1 ? UiDraw.Accent : UiDraw.TextDim);
            if (mapMode)
            {
                string focus = $"MAP - focus: {focusName}  [F] cycle";
                var fsz = f.MeasureString(focus);
                sb.DrawString(f, focus, new Vector2(w - fsz.X - 16, 52), UiDraw.TextDim);
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

            // ---- heading dial + throttle (bottom center) ----
            var dial = new Vector2(w / 2f, h - 78);
            float dr = 48;
            pb.FillCircle(dial, dr + 4, new Color(10, 16, 28, 200));
            pb.CircleOutline(dial, dr, 2, UiDraw.PanelBorder);
            // tick marks
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                var d = new Vector2((float)Math.Cos(a), -(float)Math.Sin(a));
                pb.Line(dial + d * (dr - 5), dial + d * dr, 1.5f, UiDraw.TextDim);
            }
            if (v.Velocity.Length > 1)
            {
                double va = v.Velocity.Angle();
                var pd = new Vector2((float)Math.Cos(va), -(float)Math.Sin(va));
                pb.FillCircle(dial + pd * (dr - 10), 4, new Color(120, 255, 120));        // prograde
                pb.FillCircle(dial - pd * (dr - 10), 4, new Color(255, 110, 100));        // retrograde
            }
            var hd = new Vector2((float)Math.Cos(v.Heading), -(float)Math.Sin(v.Heading));
            pb.Line(dial, dial + hd * (dr - 4), 3f, Color.White);
            // maneuver burn marker: point the heading at this to execute the planned node
            if (node != null && !double.IsNaN(burnDirAngle))
            {
                var md = new Vector2((float)Math.Cos(burnDirAngle), -(float)Math.Sin(burnDirAngle));
                var mp = dial + md * (dr - 10);
                pb.FillCircle(mp, 5, new Color(120, 210, 255));
                pb.CircleOutline(mp, 7, 1.5f, Color.White);
            }
            // target navball cues: where the target is (and its opposite) + relative-velocity markers
            if (nav.Active)
            {
                var tgt = new Color(235, 130, 235);
                if (!double.IsNaN(nav.Target))
                {
                    var td = new Vector2((float)Math.Cos(nav.Target), -(float)Math.Sin(nav.Target));
                    pb.FillCircle(dial + td * (dr - 10), 5, tgt);                 // target
                    pb.CircleOutline(dial + td * (dr - 10), 7, 1.5f, Color.White);
                    pb.CircleOutline(dial - td * (dr - 10), 5, 1.5f, tgt);        // anti-target (hollow)
                }
                if (!double.IsNaN(nav.RelPro))
                {
                    var rd = new Vector2((float)Math.Cos(nav.RelPro), -(float)Math.Sin(nav.RelPro));
                    var rc = new Color(255, 170, 235);                           // rel-velocity, distinct from orbital
                    pb.FillCircle(dial + rd * (dr - 22), 3.5f, rc);              // target-relative prograde
                    pb.CircleOutline(dial - rd * (dr - 22), 4, 1.2f, rc);        // target-relative retrograde
                }
                if (!string.IsNullOrEmpty(nav.Readout))
                {
                    var rsz = f.MeasureString(nav.Readout);
                    sb.DrawString(f, nav.Readout, new Vector2(dial.X - rsz.X / 2, dial.Y + dr + 2), tgt);
                }
            }
            if (nav.SasMode > 0 && !string.IsNullOrEmpty(nav.SasLabel))
            {
                string s = "SAS: " + nav.SasLabel;
                var ssz = f.MeasureString(s);
                sb.DrawString(f, s, new Vector2(dial.X - ssz.X / 2, dial.Y - dr - 18), UiDraw.Accent);
            }

            var thrRect = new Rectangle((int)(dial.X - dr - 34), (int)(dial.Y - dr), 16, (int)(dr * 2));
            // vertical throttle: draw as background + fill from bottom
            pb.FillRect(thrRect, new Color(20, 26, 38, 220));
            float tf = (float)v.Throttle;
            pb.FillRect(thrRect.X + 1, thrRect.Y + 1 + (thrRect.Height - 2) * (1 - tf), thrRect.Width - 2, (thrRect.Height - 2) * tf, new Color(255, 170, 60));
            pb.RectOutline(thrRect, 1, UiDraw.PanelBorder);
            sb.DrawString(f, $"THR {v.Throttle * 100:0}%", new Vector2(thrRect.X - 6, thrRect.Bottom + 4), UiDraw.TextDim);

            // ---- SAS mode icons in 2 columns (left of the throttle bar), paired normal/anti ----
            if (nav.SasIcons != null && nav.SasIcons.Length > 0)
            {
                const int isz = 22, gap = 4, colGap = 6;
                // rows: (left icon, right icon); -1 means "span both columns"
                var rows = new (int left, int right)[] {
                    (2, 3),   // Prograde / Retrograde
                    (4, 5),   // Radial In / Radial Out
                    (6, 7),   // Target / Anti-Target
                    (8, 9),   // Kill Relative / Maneuver
                    (1, -1),  // Stability (span)
                    (0, -1),  // Off (span)
                };
                int nRows = rows.Length;
                int gridH = nRows * isz + (nRows - 1) * gap;
                // two-column grid sits left of the throttle bar, bottom-aligned with it
                int gridLeft = thrRect.X - gap - colGap - isz * 2;
                int gridTop = thrRect.Bottom - gridH;

                SasIconInfo ByIdx(int idx)
                {
                    foreach (var icon in nav.SasIcons)
                        if (icon.Icon == idx) return icon;
                    return default;
                }

                for (int row = 0; row < nRows; row++)
                {
                    var spec = rows[row];
                    int iy = gridTop + row * (isz + gap);

                    void DrawCell(int iconIdx, int col)
                    {
                        if (iconIdx < 0) return;
                        var icon = ByIdx(iconIdx);
                        int ix = gridLeft + col * (isz + colGap);
                        var r = new Rectangle(ix, iy, isz, isz);
                        bool clickable = nav.SasEnabled && icon.Available;
                        bool hover = clickable && r.Contains((int)ctx.Input.MousePos.X, (int)ctx.Input.MousePos.Y);
                        UiDraw.SasIcon(pb, r, icon.Icon, icon.Active, clickable, hover);
                        if (hover && ctx.Input.LeftClick) result.RequestedSas = icon.Icon;
                    }

                    if (spec.right < 0)
                    {
                        // span both columns: draw a single centered icon
                        var icon = ByIdx(spec.left);
                        int spanW = isz * 2 + colGap;
                        int ix = gridLeft;
                        var r = new Rectangle(ix, iy, spanW, isz);
                        bool clickable = nav.SasEnabled && icon.Available;
                        bool hover = clickable && r.Contains((int)ctx.Input.MousePos.X, (int)ctx.Input.MousePos.Y);
                        UiDraw.SasIcon(pb, r, icon.Icon, icon.Active, clickable, hover);
                        if (hover && ctx.Input.LeftClick) result.RequestedSas = icon.Icon;
                    }
                    else
                    {
                        DrawCell(spec.left, 0);
                        DrawCell(spec.right, 1);
                    }
                }
            }

            // ---- propulsion strip (above the navball) ----
            if (!v.Destroyed)
            {
                double g = v.Body.Mu / Math.Max(1, v.Position.LengthSquared);
                double thrust = v.CurrentThrust;
                double mass = v.TotalMass;
                double twr = g > 0 ? thrust / (mass * g) : 0;
                double accel = thrust / mass;
                var ps = new Rectangle((int)(dial.X - 150), (int)(dial.Y - dr - 120), 300, 44);
                UiDraw.Panel(pb, ps);
                void Cell(int col, string label, string value, Color c)
                {
                    float cx = ps.X + 10 + col * 74;
                    sb.DrawString(f, label, new Vector2(cx, ps.Y + 5), UiDraw.TextDim);
                    sb.DrawString(f, value, new Vector2(cx, ps.Y + 22), c);
                }
                Cell(0, "THRUST", UiDraw.Force(thrust), thrust > 0 ? Color.White : UiDraw.TextDim);
                Cell(1, "TWR", twr > 0 ? $"{twr:0.00}" : "-", twr >= 1 ? new Color(150, 220, 150) : twr > 0 ? new Color(255, 170, 90) : UiDraw.TextDim);
                Cell(2, "ACCEL", thrust > 0 ? UiDraw.Accel(accel) : "-", Color.White);
                Cell(3, "dV REM", $"{TotalDeltaV(v):0} m/s", UiDraw.Accent);
            }

            // ---- stage list (bottom left) ----
            if (!v.Destroyed && v.Parts.Count > 0)
            {
                var stages = Staging.ComputeStages(v.Parts);
                int rows = Math.Min(stages.Count, 6);
                var sp = new Rectangle(10, h - 30 - rows * 38 - 28, 300, rows * 38 + 36);
                UiDraw.Panel(pb, sp);
                sb.DrawString(f, $"STAGES  dV {TotalDeltaV(v):0} m/s  [Space]/click fire", new Vector2(sp.X + 8, sp.Y + 6), UiDraw.TextDim);
                float sy = sp.Y + 28;
                for (int i = 0; i < rows; i++)
                {
                    var st = stages[i];
                    // the active (next-to-fire) row is clickable: clicking it fires the next stage, like [Space]
                    var rowRect = new Rectangle(sp.X + 4, (int)sy - 4, sp.Width - 8, 34);
                    bool active = i == 0;
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
                    UiDraw.Bar(pb, new Rectangle(sp.X + 8, (int)sy + 28, sp.Width - 16, 6), (float)StageFuelFrac(st), new Color(120, 200, 120));
                    sy += 38;
                }
            }

            // ---- maneuver node panel (top right) ----
            if (node != null && !v.Destroyed)
            {
                double now = ctx.Clock.UT;
                double avail = TotalDeltaV(v);
                bool enough = node.DeltaV <= avail + 1e-6;
                double bt = Staging.BurnTime(v, node.DeltaV);
                bool burning = burnSpent > 0;
                var mp = new Rectangle(w - 250, 70, 240, 190);
                UiDraw.Panel(pb, mp);
                float my = mp.Y + 8;
                void MRow(string label, string value, Color c)
                {
                    sb.DrawString(f, label, new Vector2(mp.X + 10, my), UiDraw.TextDim);
                    sb.DrawString(f, value, new Vector2(mp.X + 104, my), c);
                    my += 18;
                }
                string mTitle = nodeCount > 1 ? $"MANEUVER (next of {nodeCount})  [Del]" : "MANEUVER  [Del] / X to clear";
                sb.DrawString(f, mTitle, new Vector2(mp.X + 10, my), UiDraw.Accent);
                my += 22;
                MRow("dV req", $"{node.DeltaV:0} m/s", enough ? Color.White : new Color(255, 110, 100));
                MRow("pro/rad", $"{node.Prograde:+0;-0} / {node.Radial:+0;-0}", UiDraw.TextDim);
                MRow("dV avail", $"{avail:0} m/s", enough ? new Color(150, 220, 150) : new Color(255, 110, 100));
                MRow("burn time", enough && bt > 0 ? UiDraw.Time(bt) : "-", Color.White);
                double toNode = node.UT - now;
                MRow("node in", UiDraw.Time(Math.Max(0, toNode)), toNode < 0 ? new Color(255, 170, 90) : Color.White);
                // ignition is when the (node-centred) burn should start: node - half the burn
                double burnIn = node.UT - (enough && bt > 0 ? bt / 2 : 0) - now;
                MRow("burn in", enough && bt > 0 ? UiDraw.Time(Math.Max(0, burnIn)) : "-",
                     burnIn < 0 ? new Color(255, 110, 100) : new Color(255, 210, 140));
                if (burning)
                    MRow("remaining", $"{Math.Max(0, node.DeltaV - burnSpent):0} m/s", UiDraw.Accent);
                else
                {
                    // ignition is half the burn before the node; warp to 2 minutes before that
                    double ignition = node.UT - (enough && bt > 0 ? bt : 0);
                    double warpTarget = ignition - 120;
                    bool canWarp = warpTarget > now + 1;
                    var br = new Rectangle(mp.X + 10, mp.Bottom - 30, mp.Width - 20, 22);
                    if (UiDraw.Button(pb, sb, f, br, "Warp to maneuver", ctx.Input, canWarp))
                        result.WarpToUT = warpTarget;
                }
            }

            // ---- module status panel (right, below the maneuver panel): an icon grid split into
            // toggleable modules (clickable) and always-on systems (status only). Each tile is lit when
            // the module is currently functioning and dimmed when not, with a green/red status border. ----
            if (!v.Destroyed)
            {
                var toggle = new List<ModuleInstance>();
                var systems = new List<ModuleInstance>();
                foreach (var p in v.AllParts())
                    foreach (var m in p.Modules)
                        (m.Def.Activatable ? toggle : systems).Add(m);

                if (toggle.Count > 0 || systems.Count > 0)
                {
                    const int tile = 30, gap = 6, cols = 6, pad = 10, labelH = 18;
                    int innerW = cols * tile + (cols - 1) * gap;
                    int pw2 = innerW + pad * 2;
                    int Rows(int n) => n == 0 ? 0 : (n + cols - 1) / cols;
                    int bodyH = 6;
                    if (toggle.Count > 0) bodyH += labelH + Rows(toggle.Count) * (tile + gap);
                    if (systems.Count > 0) bodyH += labelH + Rows(systems.Count) * (tile + gap);
                    var rp = new Rectangle(w - pw2 - 10, 268, pw2, bodyH + 6);
                    UiDraw.Panel(pb, rp);

                    ModuleInstance tip = null;   // hovered module, tooltip painted after the whole grid
                    int gy = rp.Y + 6;

                    void Group(string title, List<ModuleInstance> mods, bool clickable)
                    {
                        if (mods.Count == 0) return;
                        sb.DrawString(f, title, new Vector2(rp.X + pad, gy), UiDraw.Accent);
                        gy += labelH;
                        for (int i = 0; i < mods.Count; i++)
                        {
                            var m = mods[i];
                            int col = i % cols, row = i / cols;
                            var tr = new Rectangle(rp.X + pad + col * (tile + gap), gy + row * (tile + gap), tile, tile);
                            bool func = v.ModuleFunctioning(m, ctx.Clock.UT, ctx.Universe);
                            pb.FillRect(tr, new Color(18, 26, 40, 230));
                            UiDraw.Icon(pb, ctx.Textures?.Module(m.Def.Id), new Rectangle(tr.X + 2, tr.Y + 2, tile - 4, tile - 4), m.Def.Tint, !func);
                            pb.RectOutline(tr, 2, func ? UiDraw.StatusOn : UiDraw.StatusOff);
                            bool hover = tr.Contains((int)ctx.Input.MousePos.X, (int)ctx.Input.MousePos.Y);
                            if (hover) tip = m;
                            if (hover && clickable && ctx.Input.LeftClick) m.Active = !m.Active;
                        }
                        gy += Rows(mods.Count) * (tile + gap);
                    }

                    Group("MODULES  [G] solar", toggle, true);
                    Group("SYSTEMS", systems, false);

                    if (tip != null) UiDraw.ModuleTooltip(pb, sb, f, tip.Def, ctx.Input.MousePos, w, h);
                }
            }

            // ---- controls hint (bottom right) ----
            string hint = mapMode
                ? "[click] orbit=node  [drag] handles  X=del node  [Tab/T] target  [F] focus  [C] crew  [M] flight"
                : "[Shift/Ctrl] throttle  [A/D] rotate  [H] SAS  [L] gear  [Space] stage  [Tab/T] target  [C] crew  [B] base  [M] map  [,/.] warp";
            var hsz = f.MeasureString(hint);
            sb.DrawString(f, hint, new Vector2(w - hsz.X - 12, h - 24), new Color(120, 132, 150));

            return result;
        }

        /// <summary>" (in MM:SS)" until the orbit next reaches true anomaly nu, or "" if not applicable.</summary>
        private static string TimeToText(in OrbitalElements el, double nu, double ut, bool skip)
        {
            if (skip || double.IsNaN(el.A)) return "";
            double t = Kepler.TimeAtTrueAnomaly(el, nu, ut) - ut;
            if (t <= 0 || double.IsNaN(t) || double.IsInfinity(t)) return "";
            return $"  ({UiDraw.Time(t)})";
        }

        /// <summary>Sum of remaining delta-v across all stages from the current fuel state.</summary>
        private static double TotalDeltaV(Vessel v)
        {
            double sum = 0;
            foreach (var st in Staging.ComputeStages(v.Parts)) sum += st.DeltaV;
            return sum;
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

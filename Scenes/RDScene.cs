using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Solar.Core;
using Solar.Progression;
using Solar.UI;

namespace Solar.Scenes
{
    /// <summary>Interactive Research & Development tech tree. Shows all nodes color-coded by status,
    /// lets the player click to unlock any available node, and previews what parts/modules each unlocks.</summary>
    public sealed class RDScene : Scene
    {
        // layout
        private float _offsetX, _offsetY;
        private float _targetOffX, _targetOffY;
        private string _hoveredId;
        private float _toastTimer;
        private string _toast;

        // node positions (persistent across frames, manually laid out in Enter)
        private readonly Dictionary<string, Vector2> _pos = new();
        private readonly Dictionary<string, Rectangle> _rects = new();

        public RDScene(GameContext ctx) : base(ctx) { }

        public override void Enter()
        {
            _pos.Clear();
            _rects.Clear();
            LayoutTree();
            _offsetX = _targetOffX = Ctx.W / 2f - 550;
            _offsetY = _targetOffY = Ctx.H / 2f - 250;
        }

        /// <summary>The single source of truth for tree layout: node id -> (column = tier, row = branch).
        /// Must cover every <see cref="TechTree.Nodes"/> id; a SanityChecks test asserts this so a new node
        /// can't go invisible (the bug that hid power-systems / near-future).</summary>
        internal static readonly (string Id, int Col, int Row)[] Layout =
        {
            ("start", 0, 3),
            // Tier 1 (col=1)
            ("field-science", 1, 0), ("electrics", 1, 1), ("control", 1, 2),
            ("solids", 1, 3), ("landing", 1, 4), ("miniaturization", 1, 5),
            // Tier 2 (col=2)
            ("science", 2, 0), ("survival", 2, 1), ("fuel-cells", 2, 2), ("radial", 2, 3),
            ("heavy", 2, 4), ("gimbaled", 2, 5), ("reentry", 2, 6), ("probes", 2, 7),
            // Tier 3 (col=3)
            ("advanced-science", 3, 0), ("sustainability", 3, 1), ("advanced-electrics", 3, 2),
            ("radial-advanced", 3, 3), ("megarocketry", 3, 4), ("ultra-heavy", 3, 5),
            ("aerospace", 3, 6), ("vacuum-engines", 3, 7), ("advanced-probes", 3, 8),
            ("monoprop", 3, 9), ("crew-systems", 3, 10), ("heavy-landing", 3, 11), ("advanced", 3, 12),
            ("imperator-heavy", 3, 13),
            ("planetary-science", 4, 0), ("deep-space-science", 4, 1), ("nuclear-power", 4, 2),
            ("docking", 4, 8), ("resource-processing", 4, 4), ("surface-science", 4, 5), ("power-systems", 4, 6),
            // Tier 4 (col=5)
            ("ion-prop", 5, 1), ("space-stations", 5, 7), ("deep-space-net", 5, 3), ("colonization", 5, 9),
            // Tier 5 (col=6)
            ("heavy-crew", 6, 4), ("near-future", 6, 2), ("grand-finale", 6, 6),
            // Tier 6 — interstellar (col=7)
            ("fusion-propulsion", 7, 2), ("interstellar-logistics", 7, 3), ("starflight-systems", 7, 4),
        };

        /// <summary>Build the per-frame node positions/rects from the static <see cref="Layout"/> table.</summary>
        private void LayoutTree()
        {
            foreach (var (id, col, row) in Layout) Put(id, col, row);
        }

        private void Put(string id, int col, int row)
        {
            float x = 60 + col * 210;
            float y = 60 + row * 90;
            _pos[id] = new Vector2(x, y);
            _rects[id] = new Rectangle((int)x, (int)y, 180, 60);
        }

        public override void Update(double dt)
        {
            if (_toastTimer > 0) _toastTimer -= (float)dt;
            var inp = Ctx.Input;

            // scroll wheel = vertical pan
            _targetOffY += inp.WheelDelta * 0.5f;
            // arrow keys = pan
            float panSpeed = 400f * (float)dt;
            if (inp.Down(Keys.Left))  _targetOffX -= panSpeed;
            if (inp.Down(Keys.Right)) _targetOffX += panSpeed;
            if (inp.Down(Keys.Up))    _targetOffY -= panSpeed;
            if (inp.Down(Keys.Down))  _targetOffY += panSpeed;

            // smooth pan
            _offsetX += (_targetOffX - _offsetX) * (float)(1 - Math.Exp(-10 * dt));
            _offsetY += (_targetOffY - _offsetY) * (float)(1 - Math.Exp(-10 * dt));

            // hover detection
            _hoveredId = null;
            float mx = inp.MousePos.X - _offsetX;
            float my = inp.MousePos.Y - _offsetY;
            foreach (var kv in _rects)
            {
                var r = kv.Value;
                if (mx >= r.X && mx <= r.X + r.Width && my >= r.Y && my <= r.Y + r.Height)
                {
                    _hoveredId = kv.Key;
                    break;
                }
            }

            // click to unlock
            if (_hoveredId != null && inp.LeftClick)
            {
                var node = TechTree.Node(_hoveredId);
                if (node != null && TechTree.IsUnlocked(Ctx.State, node.Id))
                {
                    // already unlocked — do nothing (could show detail)
                }
                else if (node != null && TechTree.CanUnlock(Ctx.State, node))
                {
                    TechTree.Unlock(Ctx.State, node);
                    _toast = $"Unlocked: {node.Title}";
                    _toastTimer = 2.0f;
                }
                else if (node != null && !TechTree.IsUnlocked(Ctx.State, node.Id))
                {
                    // show why locked
                    if (!TechTree.PrereqsMet(Ctx.State, node))
                        _toast = "Prerequisites not yet unlocked";
                    else if (Ctx.State.Science < node.Cost)
                        _toast = $"Need {node.Cost - Ctx.State.Science:0} more science";
                    _toastTimer = 1.5f;
                }
            }

            if (inp.Pressed(Keys.Escape))
                Ctx.Scenes.SwitchTo(new HubScene(Ctx));
        }

        public override void Draw()
        {
            var pb = Ctx.Pb; var sb = Ctx.Sb; var f = Ctx.Font;
            int w = Ctx.W, h = Ctx.H;
            var gs = Ctx.State;

            pb.Begin();
            sb.Begin();

            // background
            Ctx.Stars.Draw(pb, w, h, Vec2d.Zero);
            pb.FillRect(0, 0, w, h, new Color(0, 0, 0, 180));

            // title bar
            string title = "RESEARCH  &  DEVELOPMENT";
            var tsz = Ctx.FontBig.MeasureString(title);
            sb.DrawString(Ctx.FontBig, title, new Vector2(w / 2 - tsz.X / 2, 10), Color.White);
            string sci = $"Science: {gs.Science:0}    Nodes unlocked: {gs.UnlockedTech.Count} / {TechTree.Nodes.Count}    [Esc] back";
            var ssz = f.MeasureString(sci);
            sb.DrawString(f, sci, new Vector2(w / 2 - ssz.X / 2, tsz.Y + 14), UiDraw.Accent);

            // draw all connecting lines (prereqs)
            foreach (var node in TechTree.Nodes)
            {
                if (!_pos.TryGetValue(node.Id, out var self)) continue;
                Vector2 center = new(self.X + 90, self.Y + 30);
                foreach (var preq in node.Prereqs)
                {
                    if (!_pos.TryGetValue(preq, out var pre)) continue;
                    Vector2 preCenter = new(pre.X + 90, pre.Y + 60);
                    bool bothUnlocked = TechTree.IsUnlocked(gs, node.Id) || TechTree.IsUnlocked(gs, preq);
                    Color lineCol = bothUnlocked ? new Color(120, 210, 255, 180) : new Color(60, 70, 90, 120);
                    pb.Line(center + new Vector2(_offsetX, _offsetY), preCenter + new Vector2(_offsetX, _offsetY), 2f, lineCol);
                }
            }

            // draw all nodes
            foreach (var node in TechTree.Nodes)
            {
                if (!_pos.TryGetValue(node.Id, out var pos)) continue;
                var r = new Rectangle((int)(pos.X + _offsetX), (int)(pos.Y + _offsetY), 180, 60);

                bool unlocked = TechTree.IsUnlocked(gs, node.Id);
                bool prereqsMet = TechTree.PrereqsMet(gs, node);
                bool canAfford = gs.Science >= node.Cost;
                bool canUnlock = !unlocked && prereqsMet && canAfford;

                Color bg, border;
                string statusText;
                if (unlocked)
                {
                    bg = new Color(30, 60, 30, 230);
                    border = new Color(80, 180, 80);
                    statusText = "UNLOCKED";
                }
                else if (canUnlock)
                {
                    bg = _hoveredId == node.Id ? new Color(40, 90, 130, 240) : new Color(25, 60, 90, 220);
                    border = _hoveredId == node.Id ? new Color(140, 240, 255) : new Color(80, 160, 210);
                    statusText = $"{node.Cost:0} sci";
                }
                else if (prereqsMet && !canAfford)
                {
                    bg = new Color(45, 20, 20, 220);
                    border = new Color(200, 80, 60);
                    statusText = $"{node.Cost:0} sci";
                }
                else
                {
                    bg = new Color(20, 22, 30, 200);
                    border = new Color(45, 50, 60);
                    statusText = "LOCKED";
                }

                pb.FillRect(r, bg);
                pb.RectOutline(r, 1.5f, border);

                // title
                string label = node.Title;
                var lsz = f.MeasureString(label);
                sb.DrawString(f, label, new Vector2(r.X + 6, r.Y + 6), unlocked ? new Color(180, 255, 180) : Color.White);
                // cost / status
                sb.DrawString(f, statusText, new Vector2(r.X + 6, r.Y + 34), canUnlock ? UiDraw.Accent : (prereqsMet && !canAfford ? new Color(255, 120, 100) : UiDraw.TextDim));
            }

            // hover tooltip
            if (_hoveredId != null)
            {
                var node = TechTree.Node(_hoveredId);
                if (node != null)
                {
                    float mx = Ctx.Input.MousePos.X + 20;
                    float my = Ctx.Input.MousePos.Y - 20;
                    // build tooltip text
                    int lines = 2; // title + cost
                    if (node.Prereqs.Length > 0) lines++;
                    if (node.Parts.Length > 0) lines += node.Parts.Length;
                    if (node.Modules.Length > 0) lines += node.Modules.Length;
                    if (!string.IsNullOrEmpty(node.Description)) lines++;
                    float tipH = lines * 18 + 16;
                    float tipW = 340;
            if (my + tipH > h) my = h - tipH - 10f;
            if (mx + tipW > w) mx = w - tipW - 10f;
                    var tip = new Rectangle((int)mx, (int)my, (int)tipW, (int)tipH);
                    pb.FillRect(tip, new Color(10, 18, 32, 250));
                    pb.RectOutline(tip, 1.5f, UiDraw.Accent);

                    float ty = my + 6;
                    sb.DrawString(f, $"{node.Title}  [{node.Id}]", new Vector2(mx + 8, ty), Color.White); ty += 18;
                sb.DrawString(f, $"Cost: {node.Cost:0} science", new Vector2(mx + 8f, ty), node.Cost <= gs.Science ? UiDraw.Accent : new Color(255, 140, 100));
                    ty += 18;
                    if (node.Prereqs.Length > 0)
                    {
                        sb.DrawString(f, "Prereqs: " + string.Join(", ", node.Prereqs), new Vector2(mx + 8, ty), UiDraw.TextDim);
                        ty += 18;
                    }
                    if (!string.IsNullOrEmpty(node.Description))
                    {
                        sb.DrawString(f, node.Description, new Vector2(mx + 8, ty), new Color(200, 210, 225));
                        ty += 18;
                    }
                    if (node.Parts.Length > 0)
                    {
                        sb.DrawString(f, "Parts:", new Vector2(mx + 8, ty), new Color(180, 230, 180));
                        ty += 18;
                        foreach (var p in node.Parts)
                        {
                            sb.DrawString(f, $"  + {Parts.PartCatalog.GetById(p)?.Name ?? p}", new Vector2(mx + 8, ty), new Color(200, 220, 200));
                            ty += 18;
                        }
                    }
                    if (node.Modules.Length > 0)
                    {
                        sb.DrawString(f, "Modules:", new Vector2(mx + 8, ty), new Color(180, 210, 240));
                        ty += 18;
                        foreach (var m in node.Modules)
                        {
                            sb.DrawString(f, $"  + {Parts.ModuleCatalog.GetById(m)?.Name ?? m}", new Vector2(mx + 8, ty), new Color(200, 215, 230));
                            ty += 18;
                        }
                    }
                }
            }

            // toast
            if (_toastTimer > 0 && _toast != null)
            {
                var z = f.MeasureString(_toast);
                sb.DrawString(f, _toast, new Vector2(w / 2 - z.X / 2, h - 60), UiDraw.Accent);
            }

            // legend
            float lx = 12, ly = h - 80;
            void LegendBox(string text, Color bg, Color border)
            {
                var lr = new Rectangle((int)lx, (int)ly, 16, 12);
                pb.FillRect(lr, bg);
                pb.RectOutline(lr, 1, border);
                sb.DrawString(f, text, new Vector2(lx + 22, ly - 2), UiDraw.TextDim);
                lx += f.MeasureString(text).X + 28;
            }
            LegendBox("Unlocked", new Color(30, 60, 30, 230), new Color(80, 180, 80));
            LegendBox("Available", new Color(25, 60, 90, 220), new Color(80, 160, 210));
            LegendBox("Locked", new Color(20, 22, 30, 200), new Color(45, 50, 60));
            sb.DrawString(f, "[Arrows] pan  [Wheel] scroll  [Click] unlock  [Hover] details", new Vector2(lx + 10, ly - 2), UiDraw.TextDim);

            pb.End();
            sb.End();
        }
    }
}
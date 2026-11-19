#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// NDSHook — فقط تشخیص هوک (۱-۲-۳ ← هوک) روی هر تایم‌فریم. پورت مستقیم hooks_v2.py (پایتون).
    /// نود = سویینگ روی کندل‌های خود چارت (Strength کندل هر طرف؛ بدون ATR).
    /// هر هوک یک T دارد و دو سمت: ضربه (impulse) و مقابل (counter) که هرکدام تا ۳ شماره می‌گیرند.
    /// هوک مثبت: T کف؛ قله‌ها صعودی (N) و دره‌ها نزولی (S). هوک منفی: آینه.
    /// قوانین: CLAUDE.md بخش ۱۴. هیچ نگاهی به آینده نیست.
    /// </summary>
    public class NDSHook : Indicator
    {
        #region Types
        private sealed class HNode
        {
            public int Bar;
            public double Price;
            public bool IsHigh;
            public DateTime Time;
        }

        private sealed class HHook
        {
            public int Id;
            public bool Pos;               // true: pos hook (T is a low)
            public HNode T;
            public int Depth;              // 0 macro, 1 micro
            public string Reason;
            public List<HNode> Imp = new List<HNode>();
            public List<HNode> Ctr = new List<HNode>();
            public HNode F;
            public HNode Nd;
            public string State = "forming";   // forming | closed | failed
            public string Kind = "?";          // A | B | ?
            public double Level = double.NaN;
            public double Fraction = double.NaN;
            public bool LevelOk;
            public HNode ClosedAt;
            public HNode FailedBy;

            public int LastBar()
            {
                int m = T.Bar;
                for (int i = 0; i < Imp.Count; i++) if (Imp[i].Bar > m) m = Imp[i].Bar;
                for (int i = 0; i < Ctr.Count; i++) if (Ctr[i].Bar > m) m = Ctr[i].Bar;
                if (F != null && F.Bar > m) m = F.Bar;
                return m;
            }

            public int Span() { return LastBar() - T.Bar; }
        }

        private sealed class HSpawn
        {
            public bool Pos;
            public HNode T;
            public int Depth;
            public string Reason;
        }

        private sealed class HClaim
        {
            public int K1;
            public int K2;
            public int K3;
            public string Label;
            public int HookId;
            public string Role;
            public HNode N;
        }

        private sealed class HLabel
        {
            public HClaim Primary;
            public List<HClaim> Secondary = new List<HClaim>();
        }

        private sealed class HTracker
        {
            public readonly List<HHook> Hooks = new List<HHook>();
            public readonly List<HNode> Nodes = new List<HNode>();
            private int nextId = 1;
            private readonly double tol;
            private readonly double retrace;
            private readonly double closeBand;
            private readonly int minEach;
            private readonly int lookback;

            public HTracker(double tol, double retrace, double closeBand, int minEach, int lookback)
            {
                this.tol = tol;
                this.retrace = retrace;
                this.closeBand = closeBand;
                this.minEach = minEach;
                this.lookback = lookback;
            }

            private static string Fmt(string ev, HHook h, HNode n, string extra)
            {
                return string.Format(CultureInfo.InvariantCulture, "NDSH_EV|{0}|h{1}|{2}|d{3}|{4:yyyy-MM-dd HH:mm}|{5}|{6}|{7}",
                    ev, h.Id, h.Pos ? "pos" : "neg", h.Depth, n.Time, n.IsHigh ? "H" : "L", n.Price, extra);
            }

            public void OnNode(HNode node, Action<string> log)
            {
                List<HSpawn> spawn = new List<HSpawn>();
                for (int i = 0; i < Hooks.Count; i++)
                {
                    HHook h = Hooks[i];
                    if (h.State != "forming")
                        continue;
                    if (Violates(h, node))
                    {
                        h.State = "failed";
                        h.FailedBy = node;
                        if (log != null) log(Fmt("failed", h, node, ""));
                        if (h.Depth == 0)
                            spawn.Add(MakeSpawn(h.Pos, node, 0, "failure"));
                        continue;
                    }
                    Feed(h, node, spawn, log);
                }

                for (int i = 0; i < spawn.Count; i++)
                    Spawn(spawn[i].Pos, spawn[i].T, spawn[i].Depth, spawn[i].Reason, log);

                if (lookback > 0 || Nodes.Count == 0)
                    SpawnByExtreme(node, log);

                Nodes.Add(node);
                Hooks.RemoveAll(delegate (HHook x) { return x.State == "failed"; });
            }

            private static HSpawn MakeSpawn(bool pos, HNode t, int depth, string reason)
            {
                HSpawn s = new HSpawn();
                s.Pos = pos; s.T = t; s.Depth = depth; s.Reason = reason;
                return s;
            }

            private bool Violates(HHook h, HNode node)
            {
                if (h.Pos)
                    return !node.IsHigh && node.Price < h.T.Price - tol;
                return node.IsHigh && node.Price > h.T.Price + tol;
            }

            private bool Beats(HNode node, HNode last, bool ascending)
            {
                if (ascending)
                    return node.Price > last.Price + tol;
                return node.Price < last.Price - tol;
            }

            private void Feed(HHook h, HNode node, List<HSpawn> spawn, Action<string> log)
            {
                bool isImp = (node.IsHigh == h.Pos);          // pos: impulse side = highs
                List<HNode> seq = isImp ? h.Imp : h.Ctr;
                bool asc = isImp ? h.Pos : !h.Pos;            // pos: impulse ascending, counter descending
                if (!isImp && h.Imp.Count == 0)
                    return;                                    // counter node before impulse node 1

                string side = isImp ? "imp" : "ctr";

                if (seq.Count == 0)
                {
                    seq.Add(node);
                    if (log != null) log(Fmt("numbered", h, node, side + "1"));
                    UpdateLevel(h);
                    return;
                }

                if (seq.Count < 3)
                {
                    if (Beats(node, seq[seq.Count - 1], asc))
                    {
                        seq.Add(node);
                        if (log != null) log(Fmt("numbered", h, node, side + seq.Count.ToString(CultureInfo.InvariantCulture)));
                        UpdateLevel(h);
                        if (!isImp && seq.Count == 2 && h.Depth == 0)
                            spawn.Add(MakeSpawn(h.Pos, node, 1, "child"));
                        if (!isImp && seq.Count == 3)
                            Close(h, spawn, log);
                    }
                    return;
                }

                // impulse side already has 3 numbered nodes: F or ND candidate (depth-0 only, decided once)
                if (isImp && h.Depth == 0 && h.F == null && h.Nd == null)
                {
                    if (Beats(node, seq[2], asc))
                    {
                        h.F = node;
                        if (log != null) log(Fmt("F", h, node, ""));
                        spawn.Add(MakeSpawn(!h.Pos, node, 0, "F"));
                    }
                    else
                    {
                        h.Nd = node;
                        if (log != null) log(Fmt("nd_candidate", h, node, ""));
                    }
                }
            }

            private void UpdateLevel(HHook h)
            {
                if (h.Imp.Count < 2)
                    return;
                HNode e = h.Imp[h.Imp.Count - 1];
                if (h.Pos)
                    h.Level = e.Price - retrace * (e.Price - h.T.Price);
                else
                    h.Level = e.Price + retrace * (h.T.Price - e.Price);
            }

            private void Close(HHook h, List<HSpawn> spawn, Action<string> log)
            {
                HNode e = h.Imp[h.Imp.Count - 1];
                HNode c = h.Ctr[2];
                double leg = h.Pos ? (e.Price - h.T.Price) : (h.T.Price - e.Price);
                if (leg <= 0)
                {
                    h.State = "failed";
                    if (log != null) log(Fmt("failed", h, c, "non-positive leg"));
                    return;
                }
                double frac = (h.Pos ? (e.Price - c.Price) : (c.Price - e.Price)) / leg;
                h.Fraction = frac;
                h.LevelOk = Math.Abs(frac - retrace) <= closeBand;
                h.Kind = h.Imp.Count == 3 ? "A" : (h.Imp.Count == 2 ? "B" : "?");
                h.State = "closed";
                h.ClosedAt = c;
                if (log != null)
                    log(Fmt("closed", h, c, h.Kind + "|frac=" + frac.ToString("0.000", CultureInfo.InvariantCulture) + "|ok=" + (h.LevelOk ? "1" : "0")));
                if (h.Depth == 0)
                    spawn.Add(MakeSpawn(h.Pos, c, 0, "closure"));
            }

            private void Spawn(bool pos, HNode t, int depth, string reason, Action<string> log)
            {
                for (int i = 0; i < Hooks.Count; i++)
                    if (Hooks[i].Pos == pos && Hooks[i].T.Bar == t.Bar)
                        return;
                HHook h = new HHook();
                h.Id = nextId++;
                h.Pos = pos;
                h.T = t;
                h.Depth = depth;
                h.Reason = reason;
                Hooks.Add(h);
                if (log != null) log(Fmt("spawn", h, t, reason));
            }

            private void SpawnByExtreme(HNode node, Action<string> log)
            {
                bool beyond = true;
                if (lookback > 0)
                {
                    int seen = 0;
                    for (int i = Nodes.Count - 1; i >= 0 && seen < lookback; i--)
                    {
                        HNode n = Nodes[i];
                        if (n.IsHigh != node.IsHigh)
                            continue;
                        seen++;
                        if (node.IsHigh) { if (!(node.Price > n.Price)) beyond = false; }
                        else { if (!(node.Price < n.Price)) beyond = false; }
                    }
                }
                if (beyond)
                    Spawn(!node.IsHigh, node, 0, Nodes.Count == 0 ? "first" : "extreme", log);
            }

            public List<HHook> Recognized()
            {
                List<HHook> o = new List<HHook>();
                for (int i = 0; i < Hooks.Count; i++)
                {
                    HHook h = Hooks[i];
                    if (h.State == "failed") continue;
                    if (h.Imp.Count < minEach || h.Ctr.Count < minEach) continue;
                    if (h.State == "closed" && !h.LevelOk) continue;   // closed away from the 86.4% level
                    o.Add(h);
                }
                return o;
            }

            private static string Prefix(HNode n) { return n.IsHigh ? "N" : "S"; }

            private static void AddClaim(Dictionary<int, List<HClaim>> claims, HNode n, string label, HHook h, string role)
            {
                HClaim c = new HClaim();
                c.K1 = h.Depth; c.K2 = -h.Span(); c.K3 = h.T.Bar;
                c.Label = label; c.HookId = h.Id; c.Role = role; c.N = n;
                List<HClaim> l;
                if (!claims.TryGetValue(n.Bar, out l))
                {
                    l = new List<HClaim>();
                    claims[n.Bar] = l;
                }
                l.Add(c);
            }

            public Dictionary<int, HLabel> Labels()
            {
                Dictionary<int, List<HClaim>> claims = new Dictionary<int, List<HClaim>>();
                List<HHook> rec = Recognized();
                for (int r = 0; r < rec.Count; r++)
                {
                    HHook h = rec[r];
                    string role = h.Depth == 0 ? "macro" : "micro";
                    for (int i = 0; i < h.Imp.Count; i++)
                        AddClaim(claims, h.Imp[i], Prefix(h.Imp[i]) + (i + 1).ToString(CultureInfo.InvariantCulture), h, role);
                    for (int i = 0; i < h.Ctr.Count; i++)
                        AddClaim(claims, h.Ctr[i], Prefix(h.Ctr[i]) + (i + 1).ToString(CultureInfo.InvariantCulture), h, role);
                    if (h.F != null)
                        AddClaim(claims, h.F, "F", h, role);
                }

                Dictionary<int, HLabel> res = new Dictionary<int, HLabel>();
                foreach (KeyValuePair<int, List<HClaim>> kv in claims)
                {
                    List<HClaim> l = kv.Value;
                    l.Sort(delegate (HClaim a, HClaim b)
                    {
                        int c = a.K1.CompareTo(b.K1);
                        if (c != 0) return c;
                        c = a.K2.CompareTo(b.K2);
                        if (c != 0) return c;
                        return a.K3.CompareTo(b.K3);
                    });
                    HLabel e = new HLabel();
                    e.Primary = l[0];
                    for (int i = 1; i < l.Count; i++)
                        e.Secondary.Add(l[i]);
                    res[kv.Key] = e;
                }
                return res;
            }
        }
        #endregion

        #region Fields
        private const int MaxNodes = 3000;
        private readonly List<HNode> nodes = new List<HNode>();
        private HTracker tracker;
        private int lastAppendedBar = -1000000;
        private readonly Dictionary<int, string> drawnLabelSig = new Dictionary<int, string>();
        private readonly Dictionary<int, string> drawnHookSig = new Dictionary<int, string>();
        private readonly HashSet<int> plainDots = new HashSet<int>();
        private bool dumped;
        private SimpleFont fontMacro;
        private SimpleFont fontMicro;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "NDS Hook - تشخیص هوک از روی نودهای ۱-۲-۳ روی هر تایم‌فریم";
                Name = "NDSHook";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsAutoScale = false;

                Strength = 3;
                MinBarsBetweenNodes = 2;
                TolTicks = 2;
                CloseBand = 0.05;
                MinEachSide = 2;
                SpawnLookback = 3;
                ShowMicro = true;
                ShowAllNodes = false;
                ShowT = true;
                ShowLevels = true;
                ShowStatus = true;
                LabelFontSize = 12;
                LabelPixels = 14;
                MaxDrawLookbackBars = 3000;
                DebugMode = false;
            }
            else if (State == State.DataLoaded)
            {
                fontMacro = new SimpleFont("Consolas", LabelFontSize);
                fontMicro = new SimpleFont("Consolas", Math.Max(6, LabelFontSize - 2));
                nodes.Clear();
                tracker = NewTracker();
                lastAppendedBar = -1000000;
                drawnLabelSig.Clear();
                drawnHookSig.Clear();
                plainDots.Clear();
                dumped = false;
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (CurrentBar >= 2 * Strength)
                DetectSwing();

            if (DebugMode && !dumped && CurrentBar == Bars.Count - 1)
            {
                dumped = true;
                DumpAll();
            }
        }

        #region Node detection (same rules as nodes.py)
        private HTracker NewTracker()
        {
            return new HTracker(TolTicks * TickSize, 0.864, CloseBand, MinEachSide, SpawnLookback);
        }

        private void Log(string s)
        {
            Print(s);
        }

        private void DetectSwing()
        {
            int s = Strength;
            int c = s;                       // candidate = barsAgo s
            double cH = High[c];
            double cL = Low[c];
            bool sh = true;
            bool sl = true;
            for (int i = 0; i < s && (sh || sl); i++)
            {
                double hiR = High[i];            // newer side: a tie disqualifies
                double hiL = High[c + i + 1];    // older side: only a strictly higher bar disqualifies
                double loR = Low[i];
                double loL = Low[c + i + 1];
                if (sh && (hiR >= cH || hiL > cH)) sh = false;
                if (sl && (loR <= cL || loL < cL)) sl = false;
            }
            int bar = CurrentBar - c;
            if (sh) AddRaw(bar, cH, true);
            if (sl) AddRaw(bar, cL, false);
        }

        private void AddRaw(int bar, double price, bool isHigh)
        {
            price = Instrument.MasterInstrument.RoundToTickSize(price);
            int n = nodes.Count;

            if (n > 0 && nodes[n - 1].IsHigh == isHigh)
            {
                HNode prev = nodes[n - 1];
                bool more = isHigh ? price > prev.Price : price < prev.Price;
                if (more)
                {
                    nodes[n - 1] = MakeNode(bar, price, isHigh);
                    Rebuild();
                    RefreshDrawings();
                }
                return;
            }

            if (MinBarsBetweenNodes > 0 && bar - lastAppendedBar < MinBarsBetweenNodes)
                return;

            HNode node = MakeNode(bar, price, isHigh);
            nodes.Add(node);
            lastAppendedBar = bar;

            if (nodes.Count > MaxNodes)
            {
                nodes.RemoveAt(0);
                Rebuild();
            }
            else
            {
                tracker.OnNode(node, DebugMode ? new Action<string>(Log) : null);
            }
            RefreshDrawings();
        }

        private HNode MakeNode(int bar, double price, bool isHigh)
        {
            HNode nd = new HNode();
            nd.Bar = bar;
            nd.Price = price;
            nd.IsHigh = isHigh;
            nd.Time = Time[CurrentBar - bar];
            return nd;
        }

        private void Rebuild()
        {
            tracker = NewTracker();
            for (int i = 0; i < nodes.Count; i++)
                tracker.OnNode(nodes[i], null);
        }
        #endregion

        #region Drawing
        private static string SigOf(HLabel e)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(e.Primary.Label).Append('|').Append(e.Primary.Role).Append('|').Append(e.Primary.HookId.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < e.Secondary.Count; i++)
                sb.Append('/').Append(e.Secondary[i].Label);
            return sb.ToString();
        }

        private void RemoveNodeLabel(int bar)
        {
            RemoveDrawObject("ndsh_dot_" + bar.ToString(CultureInfo.InvariantCulture));
            RemoveDrawObject("ndsh_lbl_" + bar.ToString(CultureInfo.InvariantCulture));
            RemoveDrawObject("ndsh_sec_" + bar.ToString(CultureInfo.InvariantCulture));
        }

        private void DrawNodeLabel(int bar, HLabel e)
        {
            HNode nd = e.Primary.N;
            int barsAgo = CurrentBar - nd.Bar;
            if (barsAgo < 0 || barsAgo > MaxDrawLookbackBars)
                return;
            string key = bar.ToString(CultureInfo.InvariantCulture);
            int dir = nd.IsHigh ? 1 : -1;
            bool macro = e.Primary.Role == "macro";
            Brush b = e.Primary.Label == "F" ? Brushes.Magenta : (macro ? Brushes.White : Brushes.Gray);
            SimpleFont f = macro ? fontMacro : fontMicro;

            Draw.Dot(this, "ndsh_dot_" + key, false, nd.Time, nd.Price, nd.IsHigh ? Brushes.DodgerBlue : Brushes.OrangeRed);
            Draw.Text(this, "ndsh_lbl_" + key, false, e.Primary.Label, nd.Time, nd.Price, dir * LabelPixels, b, f,
                System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);

            if (ShowMicro && e.Secondary.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < e.Secondary.Count; i++)
                {
                    if (i > 0) sb.Append('/');
                    sb.Append(e.Secondary[i].Label);
                }
                Draw.Text(this, "ndsh_sec_" + key, false, sb.ToString(), nd.Time, nd.Price, dir * (LabelPixels * 2 + 2), Brushes.DimGray, fontMicro,
                    System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            else
            {
                RemoveDrawObject("ndsh_sec_" + key);
            }
        }

        private void RemoveHookItems(int id)
        {
            string k = id.ToString(CultureInfo.InvariantCulture);
            RemoveDrawObject("ndsh_T_" + k);
            RemoveDrawObject("ndsh_X_" + k);
            RemoveDrawObject("ndsh_V_" + k);
        }

        private void DrawHookItems(HHook h)
        {
            string k = h.Id.ToString(CultureInfo.InvariantCulture);
            int startAgo = CurrentBar - h.T.Bar;
            if (startAgo < 0 || startAgo > MaxDrawLookbackBars)
                return;

            if (ShowT)
            {
                Draw.Diamond(this, "ndsh_T_" + k, false, h.T.Time, h.T.Price, Brushes.Gold);
                string txt = "h" + k + (h.Pos ? " pos" : " neg") + (h.State == "closed" ? " closed " + h.Kind : " forming");
                Draw.Text(this, "ndsh_X_" + k, false, txt, h.T.Time, h.T.Price, h.T.IsHigh ? 26 : -26, Brushes.Gold, fontMicro,
                    System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            else
            {
                RemoveDrawObject("ndsh_T_" + k);
                RemoveDrawObject("ndsh_X_" + k);
            }

            if (ShowLevels && !double.IsNaN(h.Level))
            {
                Draw.Ray(this, "ndsh_V_" + k, false, startAgo, h.Level, Math.Max(0, startAgo - 1), h.Level,
                    Brushes.MediumSpringGreen, DashStyleHelper.Dash, 1);
            }
            else
            {
                RemoveDrawObject("ndsh_V_" + k);
            }
        }

        private void RefreshDrawings()
        {
            Dictionary<int, HLabel> labs = tracker.Labels();

            HashSet<int> keep = new HashSet<int>();
            foreach (KeyValuePair<int, HLabel> kv in labs)
            {
                HLabel e = kv.Value;
                if (!ShowMicro && e.Primary.Role != "macro")
                    continue;
                keep.Add(kv.Key);
                string sig = SigOf(e);
                string old;
                if (drawnLabelSig.TryGetValue(kv.Key, out old) && old == sig)
                    continue;
                DrawNodeLabel(kv.Key, e);
                drawnLabelSig[kv.Key] = sig;
            }
            List<int> gone = new List<int>();
            foreach (KeyValuePair<int, string> kv in drawnLabelSig)
                if (!keep.Contains(kv.Key))
                    gone.Add(kv.Key);
            for (int i = 0; i < gone.Count; i++)
            {
                RemoveNodeLabel(gone[i]);
                drawnLabelSig.Remove(gone[i]);
            }

            // plain dots for unnumbered nodes (optional)
            HashSet<int> wantPlain = new HashSet<int>();
            if (ShowAllNodes)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    HNode nd = nodes[i];
                    if (keep.Contains(nd.Bar)) continue;
                    if (CurrentBar - nd.Bar > MaxDrawLookbackBars) continue;
                    wantPlain.Add(nd.Bar);
                    if (!plainDots.Contains(nd.Bar))
                    {
                        Draw.Dot(this, "ndsh_pd_" + nd.Bar.ToString(CultureInfo.InvariantCulture), false, nd.Time, nd.Price, Brushes.DimGray);
                        plainDots.Add(nd.Bar);
                    }
                }
            }
            List<int> plainGone = new List<int>();
            foreach (int pb in plainDots)
                if (!wantPlain.Contains(pb))
                    plainGone.Add(pb);
            for (int i = 0; i < plainGone.Count; i++)
            {
                RemoveDrawObject("ndsh_pd_" + plainGone[i].ToString(CultureInfo.InvariantCulture));
                plainDots.Remove(plainGone[i]);
            }

            // hook items (T diamond, text, 86.4% ray)
            List<HHook> rec = tracker.Recognized();
            HashSet<int> live = new HashSet<int>();
            for (int i = 0; i < rec.Count; i++)
            {
                HHook h = rec[i];
                live.Add(h.Id);
                string sig = h.State + "|" + h.Level.ToString("0.00", CultureInfo.InvariantCulture) + "|" + h.Imp.Count + "|" + h.Ctr.Count + "|" + (h.F != null ? "F" : "-") + "|" + h.Kind;
                string old;
                if (drawnHookSig.TryGetValue(h.Id, out old) && old == sig)
                    continue;
                DrawHookItems(h);
                drawnHookSig[h.Id] = sig;
            }
            List<int> hookGone = new List<int>();
            foreach (KeyValuePair<int, string> kv in drawnHookSig)
                if (!live.Contains(kv.Key))
                    hookGone.Add(kv.Key);
            for (int i = 0; i < hookGone.Count; i++)
            {
                RemoveHookItems(hookGone[i]);
                drawnHookSig.Remove(hookGone[i]);
            }

            UpdateStatus(rec);
        }

        private void UpdateStatus(List<HHook> rec)
        {
            if (!ShowStatus)
            {
                RemoveDrawObject("ndsh_status");
                return;
            }
            HHook best = null;
            for (int i = 0; i < rec.Count; i++)
                if (rec[i].Depth == 0 && (best == null || rec[i].T.Bar > best.T.Bar))
                    best = rec[i];

            string txt;
            if (best == null)
            {
                txt = "NDS: no hook yet | nodes " + nodes.Count.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                txt = "NDS HOOK h" + best.Id.ToString(CultureInfo.InvariantCulture)
                    + (best.Pos ? " POS" : " NEG")
                    + " [" + best.State + (best.State == "closed" ? " " + best.Kind : "") + "]"
                    + "  T=" + best.T.Price.ToString("0.##", CultureInfo.InvariantCulture)
                    + "  imp " + best.Imp.Count.ToString(CultureInfo.InvariantCulture)
                    + " ctr " + best.Ctr.Count.ToString(CultureInfo.InvariantCulture)
                    + (best.F != null ? "  F" : "")
                    + (double.IsNaN(best.Level) ? "" : "  lvl " + best.Level.ToString("0.##", CultureInfo.InvariantCulture))
                    + "  | hooks " + rec.Count.ToString(CultureInfo.InvariantCulture);
            }
            Draw.TextFixed(this, "ndsh_status", txt, TextPosition.BottomLeft);
        }
        #endregion

        #region Debug dump (golden test against run_hooks.py)
        private void DumpAll()
        {
            Dictionary<int, HLabel> labs = tracker.Labels();
            for (int i = 0; i < nodes.Count; i++)
            {
                HNode n = nodes[i];
                string txt = "-|-|-|";
                HLabel e;
                if (labs.TryGetValue(n.Bar, out e))
                {
                    StringBuilder sec = new StringBuilder();
                    for (int k = 0; k < e.Secondary.Count; k++)
                    {
                        if (k > 0) sec.Append(';');
                        sec.Append(e.Secondary[k].Label).Append(':').Append('h').Append(e.Secondary[k].HookId.ToString(CultureInfo.InvariantCulture))
                           .Append(':').Append(e.Secondary[k].Role);
                    }
                    txt = e.Primary.Label + "|" + e.Primary.Role + "|h" + e.Primary.HookId.ToString(CultureInfo.InvariantCulture) + "|" + sec.ToString();
                }
                Print(string.Format(CultureInfo.InvariantCulture, "NDSH|{0:yyyy-MM-dd HH:mm}|{1}|{2}|{3}",
                    n.Time, n.IsHigh ? "H" : "L", n.Price, txt));
            }
            List<HHook> rec = tracker.Recognized();
            for (int i = 0; i < tracker.Hooks.Count; i++)
            {
                HHook h = tracker.Hooks[i];
                Print(string.Format(CultureInfo.InvariantCulture,
                    "NDSH_HOOK|h{0}|{1}|d{2}|{3}|{4}|T={5:yyyy-MM-dd HH:mm}|{6}|imp={7}|ctr={8}|F={9}|level={10:0.00}|frac={11:0.000}|ok={12}|rec={13}",
                    h.Id, h.Pos ? "pos" : "neg", h.Depth, h.State, h.Kind, h.T.Time, h.T.Price, h.Imp.Count, h.Ctr.Count,
                    h.F != null ? "1" : "0", h.Level, h.Fraction, h.LevelOk ? "1" : "0", rec.Contains(h) ? "1" : "0"));
            }
        }
        #endregion

        #region Properties
        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "قدرت سویینگ (کندل در هر طرف)", Description = "نود = قله/دره‌ای که از این تعداد کندل در هر طرف بالاتر/پایین‌تر باشد. روی هر تایم‌فریم قابل تنظیم", GroupName = "00) عمومی", Order = 0)]
        public int Strength { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "حداقل فاصله دو نود (کندل)", GroupName = "00) عمومی", Order = 1)]
        public int MinBarsBetweenNodes { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "تلورانس شماره‌گذاری (تیک)", Description = "نود بعدی فقط وقتی شماره می‌گیرد که بیش از این تعداد تیک از نود شماره‌دار قبلی عبور کند", GroupName = "00) عمومی", Order = 2)]
        public int TolTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 0.5)]
        [Display(Name = "نوار بسته‌شدن روی ۸۶.۴٪ (کسری از لگ)", GroupName = "00) عمومی", Order = 3)]
        public double CloseBand { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "حداقل نود شماره‌دار در هر سمت برای نمایش هوک", GroupName = "00) عمومی", Order = 4)]
        public int MinEachSide { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "نگاه به عقب برای شروع هوک جدید (تعداد نود هم‌نوع)", GroupName = "00) عمومی", Order = 5)]
        public int SpawnLookback { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش لایه دوم (micro)", GroupName = "01) نمایش", Order = 0)]
        public bool ShowMicro { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش نقطه برای نودهای بی‌شماره", GroupName = "01) نمایش", Order = 1)]
        public bool ShowAllNodes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش T هوک", GroupName = "01) نمایش", Order = 2)]
        public bool ShowT { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش خط ۸۶.۴٪", GroupName = "01) نمایش", Order = 3)]
        public bool ShowLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش برچسب وضعیت", GroupName = "01) نمایش", Order = 4)]
        public bool ShowStatus { get; set; }

        [NinjaScriptProperty]
        [Range(6, 30)]
        [Display(Name = "اندازه فونت برچسب‌ها", GroupName = "01) نمایش", Order = 5)]
        public int LabelFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "فاصله برچسب از نود (پیکسل)", GroupName = "01) نمایش", Order = 6)]
        public int LabelPixels { get; set; }

        [NinjaScriptProperty]
        [Range(100, 50000)]
        [Display(Name = "بازه رسم (کندل)", GroupName = "01) نمایش", Order = 7)]
        public int MaxDrawLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "حالت اشکال‌زدایی (چاپ در Output)", GroupName = "02) اشکال‌زدایی", Order = 0)]
        public bool DebugMode { get; set; }
        #endregion
    }
}

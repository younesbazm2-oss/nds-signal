#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    // <NDS-CORE-BEGIN>
    internal sealed class NdsCoreNode
    {
        public int Bar;
        public double Price;
        public bool IsHigh;
        public DateTime Time;

        public NdsCoreNode(int bar, double price, bool isHigh, DateTime time)
        {
            Bar = bar;
            Price = price;
            IsHigh = isHigh;
            Time = time;
        }

        public long Key
        {
            get { return ((long)Bar << 1) | (IsHigh ? 1L : 0L); }
        }
    }

    internal sealed class NdsCoreDetector
    {
        public readonly List<NdsCoreNode> Nodes = new List<NdsCoreNode>();
        private readonly List<double> highs = new List<double>();
        private readonly List<double> lows = new List<double>();
        private readonly List<DateTime> times = new List<DateTime>();
        private readonly int strength;
        private readonly int minBarsBetween;
        private readonly double tickSize;
        private int lastConfirmedBar = -1000000;

        public NdsCoreDetector(int strength, int minBarsBetween, double tickSize)
        {
            this.strength = Math.Max(1, strength);
            this.minBarsBetween = Math.Max(0, minBarsBetween);
            this.tickSize = tickSize > 0 ? tickSize : 0.25;
        }

        private double Scale(double v)
        {
            return Math.Round(v / tickSize, MidpointRounding.AwayFromZero) * tickSize;
        }

        public int StepBar(int bar, DateTime time, double high, double low)
        {
            while (highs.Count <= bar)
            {
                highs.Add(0.0);
                lows.Add(0.0);
                times.Add(DateTime.MinValue);
            }
            highs[bar] = high;
            lows[bar] = low;
            times[bar] = time;

            int s = strength;
            if (bar < 2 * s)
                return -1;

            int c = bar - s;
            double hi = highs[c];
            double lo = lows[c];
            bool sh = true;
            bool sl = true;

            for (int i = 1; i <= s; i++)
            {
                double newHi = highs[c + i];
                double oldHi = highs[c - i];
                double newLo = lows[c + i];
                double oldLo = lows[c - i];

                if (sh && (newHi >= hi || oldHi > hi))
                    sh = false;
                if (sl && (newLo <= lo || oldLo < lo))
                    sl = false;
                if (!sh && !sl)
                    break;
            }

            int changedFrom = -1;
            if (sh)
            {
                int ch = FeedCandidate(new NdsCoreNode(c, Scale(hi), true, times[c]));
                if (ch >= 0 && (changedFrom < 0 || ch < changedFrom))
                    changedFrom = ch;
            }
            if (sl)
            {
                int ch = FeedCandidate(new NdsCoreNode(c, Scale(lo), false, times[c]));
                if (ch >= 0 && (changedFrom < 0 || ch < changedFrom))
                    changedFrom = ch;
            }
            return changedFrom;
        }

        private int FeedCandidate(NdsCoreNode p)
        {
            if (minBarsBetween > 0 && p.Bar - lastConfirmedBar < minBarsBetween)
                return -1;

            int n = Nodes.Count;
            if (n > 0 && Nodes[n - 1].IsHigh == p.IsHigh)
            {
                NdsCoreNode prev = Nodes[n - 1];
                bool moreExtreme = p.IsHigh ? (p.Price > prev.Price) : (p.Price < prev.Price);
                if (moreExtreme)
                {
                    NdsCoreNode opp = DeepestOpposite(prev, p);
                    if (opp != null)
                    {
                        Nodes.Add(opp);
                        Nodes.Add(p);
                        lastConfirmedBar = p.Bar;
                        return n;
                    }
                    Nodes[n - 1] = p;
                    lastConfirmedBar = p.Bar;
                    return n - 1;
                }
                return -1;
            }

            Nodes.Add(p);
            lastConfirmedBar = p.Bar;
            return n;
        }

        private NdsCoreNode DeepestOpposite(NdsCoreNode prev, NdsCoreNode p)
        {
            int a = prev.Bar;
            int b = p.Bar;
            if (b - a < 2)
                return null;

            if (p.IsHigh)
            {
                int best = -1;
                double bestLow = 0.0;
                for (int c = a + 1; c < b; c++)
                {
                    double lo = lows[c];
                    if (lo < lows[c - 1] && lo < lows[c + 1])
                    {
                        if (best < 0 || lo < bestLow)
                        {
                            best = c;
                            bestLow = lo;
                        }
                    }
                }
                if (best < 0)
                    return null;
                double bound = Math.Min(prev.Price, p.Price);
                if (bestLow >= bound)
                    return null;
                return new NdsCoreNode(best, Scale(bestLow), false, times[best]);
            }
            else
            {
                int best = -1;
                double bestHigh = 0.0;
                for (int c = a + 1; c < b; c++)
                {
                    double hi = highs[c];
                    if (hi > highs[c - 1] && hi > highs[c + 1])
                    {
                        if (best < 0 || hi > bestHigh)
                        {
                            best = c;
                            bestHigh = hi;
                        }
                    }
                }
                if (best < 0)
                    return null;
                double bound = Math.Max(prev.Price, p.Price);
                if (bestHigh <= bound)
                    return null;
                return new NdsCoreNode(best, Scale(bestHigh), true, times[best]);
            }
        }
    }

    internal sealed class NdsCoreHook
    {
        public int Hid;
        public string Direction;
        public NdsCoreNode T;
        public int Depth;
        public string Reason;
        public readonly List<NdsCoreNode> Imp = new List<NdsCoreNode>();
        public readonly List<NdsCoreNode> Ctr = new List<NdsCoreNode>();
        public NdsCoreNode F;
        public NdsCoreNode Nd;
        public string State = "forming";
        public string Kind = "?";
        public double Level = double.NaN;
        public double Fraction = double.NaN;
        public bool LevelOk;
        public NdsCoreNode ClosedAt;
        public NdsCoreNode FailedBy;

        public bool ImpIsHigh
        {
            get { return Direction == "pos"; }
        }

        public int LastIndex()
        {
            int m = T.Bar;
            for (int i = 0; i < Imp.Count; i++)
                if (Imp[i].Bar > m) m = Imp[i].Bar;
            for (int i = 0; i < Ctr.Count; i++)
                if (Ctr[i].Bar > m) m = Ctr[i].Bar;
            if (F != null && F.Bar > m)
                m = F.Bar;
            return m;
        }

        public int Span()
        {
            return LastIndex() - T.Bar;
        }
    }

    internal sealed class NdsCoreClaim
    {
        public int Depth;
        public int NegSpan;
        public int TBar;
        public int Order;
        public string Label;
        public int HookId;
        public string Role;
        public NdsCoreNode Node;
    }

    internal sealed class NdsCoreLabelEntry
    {
        public NdsCoreClaim Primary;
        public readonly List<NdsCoreClaim> Secondary = new List<NdsCoreClaim>();
    }

    internal sealed class NdsCoreTracker
    {
        public readonly List<NdsCoreHook> Hooks = new List<NdsCoreHook>();
        public readonly List<NdsCoreNode> Nodes = new List<NdsCoreNode>();
        private readonly List<NdsCoreHook> forming = new List<NdsCoreHook>();
        private readonly HashSet<long> spawnedKeys = new HashSet<long>();
        private int nextId = 1;
        private readonly double tol;
        private readonly double retrace;
        private readonly double closeBand;
        private readonly int minEachSide;
        private readonly int spawnLookback;

        private struct SpawnSpec
        {
            public string Direction;
            public NdsCoreNode T;
            public int Depth;
            public string Reason;
        }

        public NdsCoreTracker(double tol, double retrace, double closeBand, int minEachSide, int spawnLookback)
        {
            this.tol = tol;
            this.retrace = retrace;
            this.closeBand = closeBand;
            this.minEachSide = minEachSide;
            this.spawnLookback = spawnLookback;
        }

        public void OnNode(NdsCoreNode node)
        {
            List<SpawnSpec> spawns = new List<SpawnSpec>();
            NdsCoreHook[] snap = forming.ToArray();

            for (int i = 0; i < snap.Length; i++)
            {
                NdsCoreHook h = snap[i];
                if (h.State != "forming")
                    continue;
                if (Violates(h, node))
                {
                    h.State = "failed";
                    h.FailedBy = node;
                    if (h.Depth == 0)
                        spawns.Add(MakeSpawn(h.Direction, node, 0, "failure"));
                    continue;
                }
                Feed(h, node, spawns);
            }

            forming.RemoveAll(delegate(NdsCoreHook x) { return x.State != "forming"; });

            for (int i = 0; i < spawns.Count; i++)
                Spawn(spawns[i].Direction, spawns[i].T, spawns[i].Depth, spawns[i].Reason);

            if (spawnLookback > 0 || Nodes.Count == 0)
                SpawnByExtreme(node);

            Nodes.Add(node);
        }

        private static SpawnSpec MakeSpawn(string direction, NdsCoreNode t, int depth, string reason)
        {
            SpawnSpec s = new SpawnSpec();
            s.Direction = direction;
            s.T = t;
            s.Depth = depth;
            s.Reason = reason;
            return s;
        }

        private bool Violates(NdsCoreHook h, NdsCoreNode node)
        {
            if (h.Direction == "pos")
                return (!node.IsHigh) && node.Price < h.T.Price - tol;
            return node.IsHigh && node.Price > h.T.Price + tol;
        }

        private bool Beats(NdsCoreNode node, NdsCoreNode last, bool ascending)
        {
            if (ascending)
                return node.Price > last.Price + tol;
            return node.Price < last.Price - tol;
        }

        private void Feed(NdsCoreHook h, NdsCoreNode node, List<SpawnSpec> spawns)
        {
            bool isImp = (node.IsHigh == h.ImpIsHigh);
            List<NdsCoreNode> seq = isImp ? h.Imp : h.Ctr;
            bool ascending = isImp ? (h.Direction == "pos") : (h.Direction == "neg");
            if (!isImp && h.Imp.Count == 0)
                return;

            if (seq.Count == 0)
            {
                seq.Add(node);
                UpdateLevel(h);
                return;
            }

            if (seq.Count < 3)
            {
                if (Beats(node, seq[seq.Count - 1], ascending))
                {
                    seq.Add(node);
                    UpdateLevel(h);
                    if (!isImp && seq.Count == 2 && h.Depth == 0)
                        spawns.Add(MakeSpawn(h.Direction, node, 1, "child"));
                    if (!isImp && seq.Count == 3)
                        Close(h, spawns);
                }
                return;
            }

            if (isImp && h.Depth == 0 && h.F == null && h.Nd == null)
            {
                if (Beats(node, seq[seq.Count - 1], ascending))
                {
                    h.F = node;
                    spawns.Add(MakeSpawn(h.Direction == "pos" ? "neg" : "pos", node, 0, "F"));
                }
                else
                {
                    h.Nd = node;
                }
            }
        }

        private void UpdateLevel(NdsCoreHook h)
        {
            if (h.Imp.Count < 2)
                return;
            NdsCoreNode e = h.Imp[h.Imp.Count - 1];
            if (h.Direction == "pos")
                h.Level = e.Price - retrace * (e.Price - h.T.Price);
            else
                h.Level = e.Price + retrace * (h.T.Price - e.Price);
        }

        private void Close(NdsCoreHook h, List<SpawnSpec> spawns)
        {
            NdsCoreNode e = h.Imp[h.Imp.Count - 1];
            NdsCoreNode c = h.Ctr[2];
            double leg = (h.Direction == "pos") ? (e.Price - h.T.Price) : (h.T.Price - e.Price);
            if (leg <= 0)
            {
                h.State = "failed";
                return;
            }
            double frac = ((h.Direction == "pos") ? (e.Price - c.Price) : (c.Price - e.Price)) / leg;
            h.Fraction = frac;
            h.LevelOk = Math.Abs(frac - retrace) <= closeBand;
            h.Kind = h.Imp.Count == 3 ? "A" : (h.Imp.Count == 2 ? "B" : "?");
            h.State = "closed";
            h.ClosedAt = c;
            if (h.Depth == 0)
                spawns.Add(MakeSpawn(h.Direction, c, 0, "closure"));
        }

        private void Spawn(string direction, NdsCoreNode t, int depth, string reason)
        {
            long key = ((long)t.Bar << 2) | (t.IsHigh ? 2L : 0L) | (direction == "pos" ? 1L : 0L);
            if (spawnedKeys.Contains(key))
                return;
            spawnedKeys.Add(key);
            NdsCoreHook h = new NdsCoreHook();
            h.Hid = nextId++;
            h.Direction = direction;
            h.T = t;
            h.Depth = depth;
            h.Reason = reason;
            Hooks.Add(h);
            forming.Add(h);
        }

        private void SpawnByExtreme(NdsCoreNode node)
        {
            int k = spawnLookback;
            bool beyond = true;
            if (k > 0)
            {
                int seen = 0;
                for (int i = Nodes.Count - 1; i >= 0 && seen < k; i--)
                {
                    NdsCoreNode n = Nodes[i];
                    if (n.IsHigh != node.IsHigh)
                        continue;
                    seen++;
                    if (node.IsHigh)
                    {
                        if (!(node.Price > n.Price)) beyond = false;
                    }
                    else
                    {
                        if (!(node.Price < n.Price)) beyond = false;
                    }
                }
            }
            if (!beyond)
                return;

            string direction = node.IsHigh ? "neg" : "pos";
            NdsCoreNode t = HookStart(node, direction);
            Spawn(direction, t, 0, Nodes.Count == 0 ? "first" : "extreme");
        }

        private NdsCoreNode HookStart(NdsCoreNode node, string direction)
        {
            bool oppHigh = (direction == "pos");
            if (Nodes.Count == 0)
                return node;
            NdsCoreNode prev = Nodes[Nodes.Count - 1];
            if (prev.Bar >= node.Bar)
                return node;
            NdsCoreNode best = null;
            for (int i = 0; i < Nodes.Count; i++)
            {
                NdsCoreNode n = Nodes[i];
                if (n.Bar <= prev.Bar || n.Bar >= node.Bar || n.IsHigh != oppHigh)
                    continue;
                if (best == null || (oppHigh ? n.Price > best.Price : n.Price < best.Price))
                    best = n;
            }
            return best != null ? best : node;
        }

        public List<NdsCoreHook> Recognized()
        {
            List<NdsCoreHook> o = new List<NdsCoreHook>();
            for (int i = 0; i < Hooks.Count; i++)
            {
                NdsCoreHook h = Hooks[i];
                if (h.State == "failed")
                    continue;
                if (h.Imp.Count < minEachSide || h.Ctr.Count < minEachSide)
                    continue;
                if (h.State == "closed" && !h.LevelOk)
                    continue;
                o.Add(h);
            }
            return o;
        }

        private static string Prefix(NdsCoreNode n)
        {
            return n.IsHigh ? "N" : "S";
        }

        private static void AddClaim(Dictionary<long, List<NdsCoreClaim>> claims, NdsCoreNode n, string label, NdsCoreHook h, string role, ref int order)
        {
            NdsCoreClaim c = new NdsCoreClaim();
            c.Depth = h.Depth;
            c.NegSpan = -h.Span();
            c.TBar = h.T.Bar;
            c.Order = order++;
            c.Label = label;
            c.HookId = h.Hid;
            c.Role = role;
            c.Node = n;

            List<NdsCoreClaim> list;
            if (!claims.TryGetValue(n.Key, out list))
            {
                list = new List<NdsCoreClaim>();
                claims[n.Key] = list;
            }
            list.Add(c);
        }

        public Dictionary<long, NdsCoreLabelEntry> Labels()
        {
            Dictionary<long, List<NdsCoreClaim>> claims = new Dictionary<long, List<NdsCoreClaim>>();
            List<NdsCoreHook> rec = Recognized();
            int order = 0;
            for (int r = 0; r < rec.Count; r++)
            {
                NdsCoreHook h = rec[r];
                string role = h.Depth == 0 ? "macro" : "micro";
                for (int i = 0; i < h.Imp.Count; i++)
                    AddClaim(claims, h.Imp[i], Prefix(h.Imp[i]) + (i + 1).ToString(CultureInfo.InvariantCulture), h, role, ref order);
                for (int i = 0; i < h.Ctr.Count; i++)
                    AddClaim(claims, h.Ctr[i], Prefix(h.Ctr[i]) + (i + 1).ToString(CultureInfo.InvariantCulture), h, role, ref order);
                if (h.F != null)
                    AddClaim(claims, h.F, "F", h, role, ref order);
            }

            Dictionary<long, NdsCoreLabelEntry> res = new Dictionary<long, NdsCoreLabelEntry>();
            foreach (KeyValuePair<long, List<NdsCoreClaim>> kv in claims)
            {
                List<NdsCoreClaim> l = kv.Value;
                l.Sort(delegate(NdsCoreClaim a, NdsCoreClaim b)
                {
                    int c = a.Depth.CompareTo(b.Depth);
                    if (c != 0) return c;
                    c = a.NegSpan.CompareTo(b.NegSpan);
                    if (c != 0) return c;
                    c = a.TBar.CompareTo(b.TBar);
                    if (c != 0) return c;
                    return a.Order.CompareTo(b.Order);
                });
                NdsCoreLabelEntry e = new NdsCoreLabelEntry();
                e.Primary = l[0];
                for (int i = 1; i < l.Count; i++)
                    e.Secondary.Add(l[i]);
                res[kv.Key] = e;
            }
            return res;
        }
    }

    internal sealed class NdsCoreEngine
    {
        public readonly NdsCoreDetector Detector;
        public NdsCoreTracker Tracker;
        private readonly double tol;
        private readonly double retrace;
        private readonly double closeBand;
        private readonly int minEachSide;
        private readonly int spawnLookback;
        private int fedCount;

        public NdsCoreEngine(int strength, int minBarsBetween, double tickSize, int tolTicks, double retrace, double closeBand, int minEachSide, int spawnLookback)
        {
            double ts = tickSize > 0 ? tickSize : 0.25;
            Detector = new NdsCoreDetector(strength, minBarsBetween, ts);
            tol = Math.Max(0, tolTicks) * ts;
            this.retrace = retrace;
            this.closeBand = closeBand;
            this.minEachSide = minEachSide;
            this.spawnLookback = spawnLookback;
            Tracker = NewTracker();
            fedCount = 0;
        }

        private NdsCoreTracker NewTracker()
        {
            return new NdsCoreTracker(tol, retrace, closeBand, minEachSide, spawnLookback);
        }

        public bool StepBar(int bar, DateTime time, double high, double low)
        {
            int ch = Detector.StepBar(bar, time, high, low);
            if (ch < 0)
                return false;

            if (ch == fedCount)
            {
                while (fedCount < Detector.Nodes.Count)
                {
                    Tracker.OnNode(Detector.Nodes[fedCount]);
                    fedCount++;
                }
            }
            else
            {
                Tracker = NewTracker();
                for (int i = 0; i < Detector.Nodes.Count; i++)
                    Tracker.OnNode(Detector.Nodes[i]);
                fedCount = Detector.Nodes.Count;
            }
            return true;
        }
    }
    // <NDS-CORE-END>

    /// <summary>
    /// NDSStructure — اندیکاتور واحد تشخیص ساختار NDS (نودها و هوک‌های ۱-۲-۳ روی همه‌ی تایم‌فریم‌ها).
    /// پورت دقیق engine/nodes.py و engine/hooks_v2.py بدون نگاه به آینده و بدون ATR.
    /// </summary>
    public class NDSStructure : Indicator
    {
        #region Fields
        private NdsCoreEngine engine;
        private readonly Dictionary<long, string> drawnNodeSig = new Dictionary<long, string>();
        private readonly Dictionary<int, string> drawnHookSig = new Dictionary<int, string>();
        private bool drawingsDirty;
        private bool dumped;
        private SimpleFont fontMacro;
        private SimpleFont fontMicro;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "NDS Structure — تشخیص نودها و هوک‌های ۱-۲-۳ طبق استراتژی NDS روی همه‌ی تایم‌فریم‌ها";
                Name = "NDSStructure";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsAutoScale = false;

                // 00) تنظیمات تشخیص نود و هوک
                SwingStrength = 5;
                MinBarsBetweenNodes = 2;
                TolTicks = 2;
                CycleRetracePct = 86.4;
                CloseBand = 0.05;
                MinEachSide = 2;
                SpawnLookback = 3;
                MinAnalysisMinutes = 10;

                // 01) نمایش نودها
                ShowNodes = true;
                ShowUnnumberedNodes = true;
                ShowProvisionalNode = false;
                NodeOffsetPct = 60;
                NodeHighBrush = Brushes.DodgerBlue;
                NodeLowBrush = Brushes.OrangeRed;
                UnnumberedNodeBrush = Brushes.Gold;

                // 02) نمایش برچسب‌ها و هوک
                ShowLabels = true;
                ShowMicroLayer = true;
                ShowHookT = true;
                ShowLevel86 = true;
                ShowAllHookLevels = false;
                ShowStatusLabel = true;
                StatusPosition = TextPosition.BottomLeft;
                NumberBrush = Brushes.White;
                MicroLabelBrush = Brushes.Silver;
                FlagLabelBrush = Brushes.Magenta;
                HookTBrush = Brushes.Gold;
                Level86Brush = Brushes.MediumSpringGreen;
                NumberFontSize = 12;
                LabelPixelOffset = 16;
                LabelTickOffset = 6;
                MaxDrawLookbackBars = 3000;

                // 03) اشکال‌زدایی
                DebugMode = false;
            }
            else if (State == State.DataLoaded)
            {
                fontMacro = new SimpleFont("Consolas", NumberFontSize);
                fontMicro = new SimpleFont("Consolas", Math.Max(7, NumberFontSize - 2));
                drawnNodeSig.Clear();
                drawnHookSig.Clear();
                drawingsDirty = false;
                dumped = false;
                double ts = TickSize > 0 ? TickSize : 0.25;
                engine = new NdsCoreEngine(
                    SwingStrength,
                    MinBarsBetweenNodes,
                    ts,
                    TolTicks,
                    CycleRetracePct / 100.0,
                    CloseBand,
                    MinEachSide,
                    SpawnLookback);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || engine == null)
                return;

            bool changed = engine.StepBar(CurrentBar, Time[0], High[0], Low[0]);
            if (changed)
            {
                drawingsDirty = true;
                RemoveDrawObject("nds_provH");
                RemoveDrawObject("nds_provL");
                if (DebugMode && engine.Detector.Nodes.Count > 0)
                {
                    NdsCoreNode last = engine.Detector.Nodes[engine.Detector.Nodes.Count - 1];
                    Dictionary<long, NdsCoreLabelEntry> labs = engine.Tracker.Labels();
                    NdsCoreLabelEntry e;
                    string lbl = "-";
                    string role = "-";
                    string hid = "-";
                    if (labs.TryGetValue(last.Key, out e))
                    {
                        lbl = e.Primary.Label;
                        role = e.Primary.Role;
                        hid = "h" + e.Primary.HookId.ToString(CultureInfo.InvariantCulture);
                    }
                    Print(string.Format(CultureInfo.InvariantCulture,
                        "NDS|{0:yyyy-MM-dd HH:mm}|{1}|{2}|{3}|{4}|{5}",
                        last.Time, last.IsHigh ? "H" : "L", last.Price, lbl, role, hid));
                }
            }

            DrawProvisionalNode();

            bool nearEnd = (State == State.Realtime) || (Bars != null && CurrentBar >= Bars.Count - 2);
            if (nearEnd && (drawingsDirty || (Bars != null && CurrentBar == Bars.Count - 1)))
            {
                SyncDrawings();
                drawingsDirty = false;
            }

            if (DebugMode && !dumped && nearEnd && Bars != null && CurrentBar >= Bars.Count - 2)
            {
                dumped = true;
                DumpAll();
            }
        }

        #region Drawing
        private static string TagSuffix(long key)
        {
            int bar = (int)(key >> 1);
            bool isHigh = (key & 1L) != 0;
            return (isHigh ? "H_" : "L_") + bar.ToString(CultureInfo.InvariantCulture);
        }

        private void RemoveNodeDrawings(long key)
        {
            string sfx = TagSuffix(key);
            RemoveDrawObject("nds_" + sfx);
            RemoveDrawObject("nds_num_" + sfx);
            RemoveDrawObject("nds_sec_" + sfx);
        }

        private int LabelStepPixels()
        {
            return Math.Max(16, NumberFontSize + 5);
        }

        private double LabelBasePrice(NdsCoreNode nd)
        {
            double ts = TickSize > 0 ? TickSize : 0.25;
            int dir = nd.IsHigh ? 1 : -1;
            return nd.Price + dir * LabelTickOffset * ts;
        }

        private int VisibleTiersOnNode(long key, Dictionary<long, NdsCoreLabelEntry> labs)
        {
            if (!ShowLabels || labs == null)
                return 0;
            NdsCoreLabelEntry e;
            if (!labs.TryGetValue(key, out e))
                return 0;
            if (ShowMicroLayer || e.Primary.Role == "macro")
            {
                return (ShowMicroLayer && e.Secondary.Count > 0) ? 2 : 1;
            }
            for (int k = 0; k < e.Secondary.Count; k++)
            {
                if (e.Secondary[k].Role == "macro")
                    return 1;
            }
            return 0;
        }

        private void DrawNodeObjects(NdsCoreNode nd, NdsCoreClaim primary, List<NdsCoreClaim> secondary)
        {
            string sfx = TagSuffix(nd.Key);

            if (ShowNodes)
            {
                Brush dotBrush = primary != null
                    ? (nd.IsHigh ? NodeHighBrush : NodeLowBrush)
                    : UnnumberedNodeBrush;
                Draw.Dot(this, "nds_" + sfx, false, nd.Time, nd.Price, dotBrush);
            }
            else
            {
                RemoveDrawObject("nds_" + sfx);
            }

            if (!ShowLabels || primary == null)
            {
                RemoveDrawObject("nds_num_" + sfx);
                RemoveDrawObject("nds_sec_" + sfx);
                return;
            }

            int dir = nd.IsHigh ? 1 : -1;
            int stepPx = LabelStepPixels();
            double basePrice = LabelBasePrice(nd);
            bool isMacro = primary.Role == "macro";
            Brush textBrush = primary.Label == "F"
                ? FlagLabelBrush
                : (isMacro ? NumberBrush : MicroLabelBrush);
            SimpleFont font = isMacro ? fontMacro : fontMicro;

            Draw.Text(this, "nds_num_" + sfx, false, primary.Label, nd.Time, basePrice,
                dir * LabelPixelOffset, textBrush, font,
                System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);

            if (ShowMicroLayer && secondary != null && secondary.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < secondary.Count; i++)
                {
                    if (i > 0) sb.Append('/');
                    sb.Append(secondary[i].Label);
                }
                Draw.Text(this, "nds_sec_" + sfx, false, sb.ToString(), nd.Time, basePrice,
                    dir * (LabelPixelOffset + stepPx), MicroLabelBrush, fontMicro,
                    System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            else
            {
                RemoveDrawObject("nds_sec_" + sfx);
            }
        }

        private void RemoveHookDrawings(int hid)
        {
            string k = hid.ToString(CultureInfo.InvariantCulture);
            RemoveDrawObject("nds_T_" + k);
            RemoveDrawObject("nds_Ttxt_" + k);
            RemoveDrawObject("nds_lvl_" + k);
            RemoveDrawObject("nds_lvltxt_" + k);
        }

        private void DrawHookObjects(NdsCoreHook h, bool drawLevel, int tiersOnT)
        {
            string k = h.Hid.ToString(CultureInfo.InvariantCulture);
            int startAgo = CurrentBar - h.T.Bar;
            if (startAgo < 0 || startAgo > MaxDrawLookbackBars)
            {
                RemoveHookDrawings(h.Hid);
                return;
            }

            if (ShowHookT && (h.Depth == 0 || ShowMicroLayer))
            {
                Draw.Diamond(this, "nds_T_" + k, false, h.T.Time, h.T.Price, HookTBrush);
                string txt = "T h" + k + (h.Direction == "pos" ? "+" : "-")
                    + (h.State == "closed" ? ":" + h.Kind : "");
                int dir = h.T.IsHigh ? 1 : -1;
                int stepPx = LabelStepPixels();
                double basePrice = LabelBasePrice(h.T);
                int yOffset = dir * (LabelPixelOffset + tiersOnT * stepPx);
                Draw.Text(this, "nds_Ttxt_" + k, false, txt, h.T.Time, basePrice,
                    yOffset, HookTBrush, fontMicro,
                    System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            else
            {
                RemoveDrawObject("nds_T_" + k);
                RemoveDrawObject("nds_Ttxt_" + k);
            }

            if (ShowLevel86 && drawLevel && !double.IsNaN(h.Level))
            {
                DateTime endTime = (h.State == "closed" && h.ClosedAt != null && h.ClosedAt.Time > h.T.Time)
                    ? h.ClosedAt.Time
                    : Time[0];
                if (endTime <= h.T.Time)
                    endTime = h.T.Time.AddMinutes(1);
                Draw.Line(this, "nds_lvl_" + k, false, h.T.Time, h.Level, endTime, h.Level,
                    Level86Brush, DashStyleHelper.Dash, 1);
                string ltxt = CycleRetracePct.ToString("0.#", CultureInfo.InvariantCulture)
                    + "% h" + k + " " + h.Level.ToString("0.##", CultureInfo.InvariantCulture);
                Draw.Text(this, "nds_lvltxt_" + k, false, ltxt, endTime, h.Level,
                    8, Level86Brush, fontMicro,
                    System.Windows.TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);
            }
            else
            {
                RemoveDrawObject("nds_lvl_" + k);
                RemoveDrawObject("nds_lvltxt_" + k);
            }
        }

        private void SyncDrawings()
        {
            Dictionary<long, NdsCoreLabelEntry> labs = engine.Tracker.Labels();
            List<NdsCoreNode> nodes = engine.Detector.Nodes;
            HashSet<long> keepNodes = new HashSet<long>();

            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                NdsCoreNode nd = nodes[i];
                if (CurrentBar - nd.Bar > MaxDrawLookbackBars)
                    break;

                NdsCoreLabelEntry e;
                NdsCoreClaim primary = null;
                List<NdsCoreClaim> sec = null;
                if (labs.TryGetValue(nd.Key, out e))
                {
                    if (ShowMicroLayer || e.Primary.Role == "macro")
                    {
                        primary = e.Primary;
                        if (ShowMicroLayer && e.Secondary.Count > 0)
                            sec = e.Secondary;
                    }
                    else
                    {
                        for (int k = 0; k < e.Secondary.Count; k++)
                        {
                            if (e.Secondary[k].Role == "macro")
                            {
                                primary = e.Secondary[k];
                                break;
                            }
                        }
                    }
                }

                if (primary == null && !ShowUnnumberedNodes)
                    continue;

                keepNodes.Add(nd.Key);
                StringBuilder sb = new StringBuilder();
                sb.Append(nd.Price.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                  .Append(ShowNodes ? '1' : '0').Append(ShowLabels ? '1' : '0').Append('|');
                if (primary != null)
                {
                    sb.Append(primary.Label).Append('|').Append(primary.Role).Append('|')
                      .Append(primary.HookId.ToString(CultureInfo.InvariantCulture));
                    if (sec != null)
                    {
                        for (int k = 0; k < sec.Count; k++)
                            sb.Append('/').Append(sec[k].Label);
                    }
                }

                string sig = sb.ToString();
                string oldSig;
                if (drawnNodeSig.TryGetValue(nd.Key, out oldSig) && oldSig == sig)
                    continue;

                DrawNodeObjects(nd, primary, sec);
                drawnNodeSig[nd.Key] = sig;
            }

            List<long> goneNodes = new List<long>();
            foreach (KeyValuePair<long, string> kv in drawnNodeSig)
            {
                if (!keepNodes.Contains(kv.Key))
                    goneNodes.Add(kv.Key);
            }
            for (int i = 0; i < goneNodes.Count; i++)
            {
                RemoveNodeDrawings(goneNodes[i]);
                drawnNodeSig.Remove(goneNodes[i]);
            }

            List<NdsCoreHook> rec = engine.Tracker.Recognized();
            NdsCoreHook activeMacro = null;
            for (int i = 0; i < rec.Count; i++)
            {
                if (rec[i].Depth == 0 && (activeMacro == null || rec[i].T.Bar >= activeMacro.T.Bar))
                    activeMacro = rec[i];
            }

            HashSet<int> keepHooks = new HashSet<int>();
            for (int i = 0; i < rec.Count; i++)
            {
                NdsCoreHook h = rec[i];
                if (CurrentBar - h.T.Bar > MaxDrawLookbackBars)
                    continue;
                keepHooks.Add(h.Hid);
                bool drawLevel = ShowAllHookLevels
                    || (activeMacro != null && h.Hid == activeMacro.Hid)
                    || (h.Depth == 0 && h.State == "forming");
                int tiersOnT = VisibleTiersOnNode(h.T.Key, labs);
                string sig = h.T.Bar.ToString(CultureInfo.InvariantCulture) + "|"
                    + h.State + "|" + h.Kind + "|"
                    + h.Level.ToString("0.00", CultureInfo.InvariantCulture) + "|"
                    + (drawLevel ? "L1" : "L0") + "|"
                    + (ShowHookT ? "T1" : "T0") + "|"
                    + (ShowLevel86 ? "V1" : "V0") + "|"
                    + tiersOnT.ToString(CultureInfo.InvariantCulture) + "|"
                    + (h.State == "forming" && drawLevel ? CurrentBar.ToString(CultureInfo.InvariantCulture) : "F");

                string oldSig;
                if (drawnHookSig.TryGetValue(h.Hid, out oldSig) && oldSig == sig)
                    continue;
                DrawHookObjects(h, drawLevel, tiersOnT);
                drawnHookSig[h.Hid] = sig;
            }

            List<int> goneHooks = new List<int>();
            foreach (KeyValuePair<int, string> kv in drawnHookSig)
            {
                if (!keepHooks.Contains(kv.Key))
                    goneHooks.Add(kv.Key);
            }
            for (int i = 0; i < goneHooks.Count; i++)
            {
                RemoveHookDrawings(goneHooks[i]);
                drawnHookSig.Remove(goneHooks[i]);
            }

            DrawStatus(rec, activeMacro);
        }

        private void DrawProvisionalNode()
        {
            RemoveDrawObject("nds_provH");
            RemoveDrawObject("nds_provL");
            if (!ShowProvisionalNode || SwingStrength < 1 || CurrentBar < SwingStrength)
                return;

            int s = SwingStrength;
            int hiAgo = 0, loAgo = 0;
            for (int i = 1; i < s; i++)
            {
                if (High[i] > High[hiAgo]) hiAgo = i;
                if (Low[i] < Low[loAgo]) loAgo = i;
            }

            Brush bh = NodeHighBrush.Clone();
            bh.Opacity = 0.35;
            bh.Freeze();
            Draw.Dot(this, "nds_provH", false, Time[hiAgo], High[hiAgo], bh);

            Brush bl = NodeLowBrush.Clone();
            bl.Opacity = 0.35;
            bl.Freeze();
            Draw.Dot(this, "nds_provL", false, Time[loAgo], Low[loAgo], bl);
        }

        private bool IsBelowMinAnalysisTimeframe()
        {
            if (MinAnalysisMinutes <= 0 || BarsPeriod == null)
                return false;
            if (BarsPeriod.BarsPeriodType == BarsPeriodType.Minute)
                return BarsPeriod.Value < MinAnalysisMinutes;
            if (BarsPeriod.BarsPeriodType == BarsPeriodType.Second ||
                BarsPeriod.BarsPeriodType == BarsPeriodType.Tick)
                return true;
            return false;
        }

        private void DrawStatus(List<NdsCoreHook> rec, NdsCoreHook active)
        {
            if (!ShowStatusLabel)
            {
                RemoveDrawObject("nds_status");
                return;
            }

            string warn = IsBelowMinAnalysisTimeframe()
                ? " [TF<" + MinAnalysisMinutes.ToString(CultureInfo.InvariantCulture) + "m: high noise]"
                : "";
            string txt;
            if (active == null)
            {
                txt = "NDS (S=" + SwingStrength.ToString(CultureInfo.InvariantCulture) + "): no macro hook | nodes="
                    + engine.Detector.Nodes.Count.ToString(CultureInfo.InvariantCulture) + warn;
            }
            else
            {
                txt = "NDS (S=" + SwingStrength.ToString(CultureInfo.InvariantCulture) + ") h"
                    + active.Hid.ToString(CultureInfo.InvariantCulture)
                    + (active.Direction == "pos" ? " POS" : " NEG")
                    + " [" + active.State + (active.State == "closed" ? " " + active.Kind : "") + "]"
                    + " T=" + active.T.Price.ToString("0.##", CultureInfo.InvariantCulture)
                    + " imp=" + active.Imp.Count.ToString(CultureInfo.InvariantCulture)
                    + " ctr=" + active.Ctr.Count.ToString(CultureInfo.InvariantCulture)
                    + (active.F != null ? " F" : "")
                    + (double.IsNaN(active.Level) ? "" : " lvl=" + active.Level.ToString("0.##", CultureInfo.InvariantCulture))
                    + " | nodes=" + engine.Detector.Nodes.Count.ToString(CultureInfo.InvariantCulture)
                    + " hooks=" + rec.Count.ToString(CultureInfo.InvariantCulture)
                    + warn;
            }
            Draw.TextFixed(this, "nds_status", txt, StatusPosition);
        }

        private void DumpAll()
        {
            Dictionary<long, NdsCoreLabelEntry> labs = engine.Tracker.Labels();
            List<NdsCoreNode> nodes = engine.Detector.Nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                NdsCoreNode n = nodes[i];
                string txt = "-|-|-|";
                NdsCoreLabelEntry e;
                if (labs.TryGetValue(n.Key, out e))
                {
                    StringBuilder sec = new StringBuilder();
                    for (int k = 0; k < e.Secondary.Count; k++)
                    {
                        if (k > 0) sec.Append(';');
                        sec.Append(e.Secondary[k].Label).Append(":h")
                           .Append(e.Secondary[k].HookId.ToString(CultureInfo.InvariantCulture))
                           .Append(':').Append(e.Secondary[k].Role);
                    }
                    txt = e.Primary.Label + "|" + e.Primary.Role + "|h"
                        + e.Primary.HookId.ToString(CultureInfo.InvariantCulture) + "|" + sec.ToString();
                }
                Print(string.Format(CultureInfo.InvariantCulture,
                    "NDS_NODE|{0:yyyy-MM-dd HH:mm}|{1}|{2}|{3}",
                    n.Time, n.IsHigh ? "H" : "L", n.Price, txt));
            }

            List<NdsCoreHook> rec = engine.Tracker.Recognized();
            for (int i = 0; i < engine.Tracker.Hooks.Count; i++)
            {
                NdsCoreHook h = engine.Tracker.Hooks[i];
                Print(string.Format(CultureInfo.InvariantCulture,
                    "NDS_HOOK|h{0}|{1}|d{2}|{3}|{4}|T={5:yyyy-MM-dd HH:mm}|{6}|imp={7}|ctr={8}|F={9}|level={10:0.00}|frac={11:0.000}|ok={12}|rec={13}",
                    h.Hid, h.Direction, h.Depth, h.State, h.Kind, h.T.Time, h.T.Price,
                    h.Imp.Count, h.Ctr.Count, h.F != null ? "1" : "0",
                    h.Level, h.Fraction, h.LevelOk ? "1" : "0", rec.Contains(h) ? "1" : "0"));
            }
        }
        #endregion

        #region Properties
        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "قدرت سویینگ (کندل در هر طرف)", Description = "نود = قله/دره‌ای که از این تعداد کندل در هر طرف بالاتر/پایین‌تر باشد (پیش‌فرض ۵)", GroupName = "00) تشخیص نود و هوک", Order = 0)]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "حداقل فاصله دو نود (کندل)", Description = "گارد فاصله بین دو نود متوالی؛ تقارن قله/دره همیشه اولویت دارد", GroupName = "00) تشخیص نود و هوک", Order = 1)]
        public int MinBarsBetweenNodes { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "تلورانس شماره‌گذاری (تیک)", Description = "نود بعدی فقط وقتی شماره می‌گیرد که بیش از این تعداد تیک از نود شماره‌دار قبلی عبور کند", GroupName = "00) تشخیص نود و هوک", Order = 2)]
        public int TolTicks { get; set; }

        [NinjaScriptProperty]
        [Range(50.0, 99.0)]
        [Display(Name = "درصد ریتریس سایکل (۸۶.۴٪)", Description = "سطح بسته‌شدن هوک طبق کتاب NDS (۸۶.۴ درصد)", GroupName = "00) تشخیص نود و هوک", Order = 3)]
        public double CycleRetracePct { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 0.50)]
        [Display(Name = "باند بسته‌شدن هوک (کسری از لگ)", Description = "حداکثر فاصله از سطح ۸۶.۴٪ برای بسته‌شدن معتبر هوک (پیش‌فرض ۰.۰۵ = ۵٪)", GroupName = "00) تشخیص نود و هوک", Order = 4)]
        public double CloseBand { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "حداقل نود شماره‌دار در هر سمت هوک", GroupName = "00) تشخیص نود و هوک", Order = 5)]
        public int MinEachSide { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "نگاه به عقب برای شروع هوک اکستریم", GroupName = "00) تشخیص نود و هوک", Order = 6)]
        public int SpawnLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0, 240)]
        [Display(Name = "حداقل تایم‌فریم تحلیل (دقیقه)", Description = "زیر این تایم‌فریم هشدار نویز در برچسب وضعیت نمایش داده می‌شود", GroupName = "00) تشخیص نود و هوک", Order = 7)]
        public int MinAnalysisMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش نقاط نودها", GroupName = "01) نمایش نودها", Order = 0)]
        public bool ShowNodes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش نودهای بی‌شماره (زرد)", GroupName = "01) نمایش نودها", Order = 1)]
        public bool ShowUnnumberedNodes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش اکستریم تأییدنشده (کم‌رنگ)", GroupName = "01) نمایش نودها", Order = 2)]
        public bool ShowProvisionalNode { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ نود سقف", GroupName = "01) نمایش نودها", Order = 3)]
        public Brush NodeHighBrush { get; set; }

        [Browsable(false)]
        public string NodeHighBrushSerializable
        {
            get { return Serialize.BrushToString(NodeHighBrush); }
            set { NodeHighBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "رنگ نود کف", GroupName = "01) نمایش نودها", Order = 4)]
        public Brush NodeLowBrush { get; set; }

        [Browsable(false)]
        public string NodeLowBrushSerializable
        {
            get { return Serialize.BrushToString(NodeLowBrush); }
            set { NodeLowBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "رنگ نود بی‌شماره", GroupName = "01) نمایش نودها", Order = 5)]
        public Brush UnnumberedNodeBrush { get; set; }

        [Browsable(false)]
        public string UnnumberedNodeBrushSerializable
        {
            get { return Serialize.BrushToString(UnnumberedNodeBrush); }
            set { UnnumberedNodeBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "نمایش برچسب‌ها (N1..N3 / S1..S3 / F)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 0)]
        public bool ShowLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش لایه دوم (میکرو)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 1)]
        public bool ShowMicroLayer { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش نقطه شروع هوک (T)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 2)]
        public bool ShowHookT { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش خط سطح ۸۶.۴٪", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 3)]
        public bool ShowLevel86 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش خط سطح برای همه هوک‌های گذشته", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 4)]
        public bool ShowAllHookLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش برچسب وضعیت روی چارت", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 5)]
        public bool ShowStatusLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "محل برچسب وضعیت", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 6)]
        public TextPosition StatusPosition { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ برچسب‌های ماکرو", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 7)]
        public Brush NumberBrush { get; set; }

        [Browsable(false)]
        public string NumberBrushSerializable
        {
            get { return Serialize.BrushToString(NumberBrush); }
            set { NumberBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "رنگ برچسب‌های میکرو", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 8)]
        public Brush MicroLabelBrush { get; set; }

        [Browsable(false)]
        public string MicroLabelBrushSerializable
        {
            get { return Serialize.BrushToString(MicroLabelBrush); }
            set { MicroLabelBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "رنگ برچسب F", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 9)]
        public Brush FlagLabelBrush { get; set; }

        [Browsable(false)]
        public string FlagLabelBrushSerializable
        {
            get { return Serialize.BrushToString(FlagLabelBrush); }
            set { FlagLabelBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "رنگ علامت T هوک", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 10)]
        public Brush HookTBrush { get; set; }

        [Browsable(false)]
        public string HookTBrushSerializable
        {
            get { return Serialize.BrushToString(HookTBrush); }
            set { HookTBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "رنگ خط سطح ۸۶.۴٪", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 11)]
        public Brush Level86Brush { get; set; }

        [Browsable(false)]
        public string Level86BrushSerializable
        {
            get { return Serialize.BrushToString(Level86Brush); }
            set { Level86Brush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(7, 30)]
        [Display(Name = "اندازه فونت برچسب‌ها", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 12)]
        public int NumberFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(0, 150)]
        [Display(Name = "فاصله برچسب از نود (پیکسل)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 13)]
        public int LabelPixelOffset { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "فاصله برچسب از کندل (تیک)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 14)]
        public int LabelTickOffset { get; set; }

        [NinjaScriptProperty]
        [Range(100, 50000)]
        [Display(Name = "بازه رسم (کندل)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 15)]
        public int MaxDrawLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "حالت اشکال‌زدایی (چاپ در Output)", GroupName = "03) اشکال‌زدایی", Order = 0)]
        public bool DebugMode { get; set; }
        #endregion
    }
}

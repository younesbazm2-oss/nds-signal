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
        // Heikin-Ashi confirmation
        private readonly List<double> haOpen = new List<double>();
        private readonly List<double> haClose = new List<double>();
        private readonly List<NdsCoreNode> pending = new List<NdsCoreNode>();
        private readonly int haConfirmBars;
        private const int PendingMaxAge = 500;
        // equal double top/bottom tolerance (price units)
        private readonly double tol;
        // HA-only node mode state
        private readonly bool haOnly;
        private int runDir;
        private int runStartBar;
        private int oppCount;

        public NdsCoreDetector(int strength, int minBarsBetween, double tickSize, int haConfirmBars, double tol, bool haOnly = false)
        {
            this.haOnly = haOnly;
            this.strength = Math.Max(1, strength);
            this.minBarsBetween = Math.Max(0, minBarsBetween);
            this.tickSize = tickSize > 0 ? tickSize : 0.25;
            this.haConfirmBars = Math.Max(0, haConfirmBars);
            this.tol = Math.Max(0, tol);
        }

        private bool Equalish(double a, double b)
        {
            return Math.Abs(a - b) <= tol + 1e-9;
        }

        // HA direction of a bar: +1 bullish, -1 bearish, 0 doji
        public int HaDir(int bar)
        {
            if (bar < 0 || bar >= haOpen.Count)
                return 0;
            double d = haClose[bar] - haOpen[bar];
            return d > 0 ? 1 : (d < 0 ? -1 : 0);
        }

        public int PendingCount { get { return pending.Count; } }

        private double Scale(double v)
        {
            return Math.Round(v / tickSize, MidpointRounding.AwayFromZero) * tickSize;
        }

        public int StepBar(int bar, DateTime time, double open, double high, double low, double close)
        {
            while (highs.Count <= bar)
            {
                highs.Add(0.0);
                lows.Add(0.0);
                times.Add(DateTime.MinValue);
                haOpen.Add(0.0);
                haClose.Add(0.0);
            }
            highs[bar] = high;
            lows[bar] = low;
            times[bar] = time;

            double hc = (open + high + low + close) / 4.0;
            double ho = bar > 0 ? (haOpen[bar - 1] + haClose[bar - 1]) / 2.0 : (open + close) / 2.0;
            haOpen[bar] = ho;
            haClose[bar] = hc;

            if (haOnly)
                return StepHaOnly(bar);

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
            if (haConfirmBars <= 0)
            {
                if (sh)
                    changedFrom = MinCh(changedFrom, FeedCandidate(new NdsCoreNode(c, Scale(hi), true, times[c])));
                if (sl)
                    changedFrom = MinCh(changedFrom, FeedCandidate(new NdsCoreNode(c, Scale(lo), false, times[c])));
                return changedFrom;
            }

            if (sh)
                AddPending(new NdsCoreNode(c, Scale(hi), true, times[c]));
            if (sl)
                AddPending(new NdsCoreNode(c, Scale(lo), false, times[c]));

            // Evaluate pending pivots: drop invalidated/stale, feed HA-confirmed ones in bar order.
            int i0 = 0;
            while (i0 < pending.Count)
            {
                NdsCoreNode p = pending[i0];
                bool broken = false;
                for (int k = p.Bar + 1; k <= bar; k++)
                {
                    if (p.IsHigh ? highs[k] > highs[p.Bar] : lows[k] < lows[p.Bar])
                    {
                        broken = true;
                        break;
                    }
                }
                if (broken || bar - p.Bar > PendingMaxAge)
                {
                    pending.RemoveAt(i0);
                    continue;
                }
                if (HaConfirmed(p, bar))
                {
                    // earlier unconfirmed pivots are superseded by this confirmed one
                    pending.RemoveRange(0, i0 + 1);
                    i0 = 0;
                    changedFrom = MinCh(changedFrom, FeedCandidate(p));
                    continue;
                }
                i0++;
            }
            return changedFrom;
        }

        // Step 21: node = Heikin-Ashi colour change ONLY (no fractal test).
        // A run = consecutive HA candles of one colour. When haConfirmBars (min 1) closed candles of the
        // opposite colour follow, the run ends and its extreme wick (highest High / lowest Low) is the node.
        // Doji candles stay inside the run and reset the confirmation count.
        private int StepHaOnly(int bar)
        {
            int d = HaDir(bar);
            if (runDir == 0)
            {
                if (d != 0)
                {
                    runDir = d;
                    runStartBar = bar;
                }
                return -1;
            }
            if (d == runDir || d == 0)
            {
                oppCount = 0;
                return -1;
            }
            oppCount++;
            if (oppCount < Math.Max(1, haConfirmBars))
                return -1;

            // Step 23: SwingStrength = minimum length of the colour run (bars from run start to the
            // first opposite bar). Shorter runs are ignored: the run simply continues in the old colour
            // (no node), so a higher SwingStrength gives fewer, bigger nodes.
            int runLen = (bar - oppCount + 1) - runStartBar;
            if (runLen < strength)
            {
                // too short: swallow the opposite bars into the current run, keep searching
                oppCount = 0;
                return -1;
            }

            bool isHigh = runDir > 0;
            int best = runStartBar;
            for (int k = runStartBar; k <= bar; k++)
            {
                if (isHigh ? highs[k] > highs[best] : lows[k] < lows[best])
                    best = k;
            }
            runDir = -runDir;
            runStartBar = best;
            oppCount = 0;
            return FeedCandidate(new NdsCoreNode(best, Scale(isHigh ? highs[best] : lows[best]), isHigh, times[best]));
        }

        private static int MinCh(int cur, int ch)
        {
            if (ch >= 0 && (cur < 0 || ch < cur))
                return ch;
            return cur;
        }

        private void AddPending(NdsCoreNode p)
        {
            if (p.Bar <= lastConfirmedBar)
                return;
            for (int i = 0; i < pending.Count; i++)
                if (pending[i].Key == p.Key)
                    return;
            int pos = pending.Count;
            while (pos > 0 && pending[pos - 1].Bar > p.Bar)
                pos--;
            pending.Insert(pos, p);
        }

        // N consecutive reversal HA candles after the pivot bar
        // (bearish after a high, bullish after a low).
        private bool HaConfirmed(NdsCoreNode p, int bar)
        {
            int want = p.IsHigh ? -1 : 1;
            int run = 0;
            for (int k = p.Bar + 1; k <= bar; k++)
            {
                if (HaDir(k) == want)
                {
                    run++;
                    if (run >= haConfirmBars)
                        return true;
                }
                else
                    run = 0;
            }
            return false;
        }

        private int FeedCandidate(NdsCoreNode p)
        {
            if (minBarsBetween > 0 && p.Bar - lastConfirmedBar < minBarsBetween)
                return -1;

            int n = Nodes.Count;

            // Equal double top/bottom = ONE node: H1-L-H2 with H2 ~= H1 (within tol)
            // -> drop the middle swing, keep the first node.
            if (n >= 2 && Nodes[n - 1].IsHigh != p.IsHigh && Nodes[n - 2].IsHigh == p.IsHigh
                && Equalish(p.Price, Nodes[n - 2].Price))
            {
                Nodes.RemoveAt(n - 1);
                lastConfirmedBar = p.Bar;
                return n - 1;
            }

            if (n > 0 && Nodes[n - 1].IsHigh == p.IsHigh)
            {
                NdsCoreNode prev = Nodes[n - 1];
                if (Equalish(p.Price, prev.Price))
                    return -1; // same level, keep the first node
                bool moreExtreme = p.IsHigh ? (p.Price > prev.Price) : (p.Price < prev.Price);
                if (moreExtreme)
                {
                    // HA-only mode: never inject an unconfirmed 1-bar swing.
                    NdsCoreNode opp = haOnly ? null : DeepestOpposite(prev, p);
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
        // Chain position (Hook-Hook-Rally): 1 = first hook after reversal, closure spawn = parent.Seq + 1
        public int Seq = 1;
        public double Level = double.NaN;
        public double Fraction = double.NaN;
        public bool LevelOk;
        public NdsCoreNode ClosedAt;
        public NdsCoreNode FailedBy;
        // Step 21: hook whose closure spawned this one (H1 for H2), null otherwise.
        public NdsCoreHook Parent;
        // Number of impulse nodes inherited from prior sequence before T (e.g. 1 or 2)
        public int InheritedImpCount = 0;
        public readonly List<NdsCoreNode> InheritedNodes = new List<NdsCoreNode>();

        public int TotalImpCount
        {
            get { return InheritedImpCount + Imp.Count; }
        }

        public NdsCoreNode GetImpNode(int index)
        {
            if (index < 0 || index >= TotalImpCount)
                return null;
            if (index < InheritedNodes.Count)
                return InheritedNodes[index];
            int local = index - InheritedImpCount;
            if (local >= 0 && local < Imp.Count)
                return Imp[local];
            return null;
        }

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

    // Independent history of already-numbered pairs, not a new hook or an FT/T detector.
    internal sealed class NdsCoreUnfinished
    {
        public string Key;
        public NdsCoreNode First;
        public NdsCoreNode Second;
        public NdsCoreNode Third;
        public bool Ascending;
        public bool Broken;
        public int Depth;
        public readonly List<NdsCoreHook> Sources = new List<NdsCoreHook>();

        public List<NdsCoreNode> SourceSequence(NdsCoreHook source)
        {
            return First.IsHigh == source.ImpIsHigh ? source.Imp : source.Ctr;
        }
    }

    // Step 21: big hook = H1 + H2 when H2's correction goes beyond H1's closing counter node 3
    // without breaking H1's T. No 86.4% condition (user decision).
    internal sealed class NdsCoreBigHook
    {
        public NdsCoreHook First;
        public NdsCoreHook Second;
        public NdsCoreNode BrokeAt;
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
        public string Axis = "Y";
        public NdsCoreNode Node;
        public bool Open;
    }

    internal sealed class NdsCoreXEntry
    {
        public string Label;
        public int StartBar;
        public bool Open;
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
        public readonly List<NdsCoreUnfinished> Unfinished = new List<NdsCoreUnfinished>();
        public readonly List<NdsCoreBigHook> BigHooks = new List<NdsCoreBigHook>();
        // X-axis continuous 1-2-3 sequence numbering across the whole chart (highs & lows independently).
        public readonly Dictionary<long, string> NodeNumbers = new Dictionary<long, string>();
        public readonly Dictionary<long, NdsCoreXEntry> XAxisEntries = new Dictionary<long, NdsCoreXEntry>();
        public readonly Dictionary<long, NdsCoreXEntry> YAxisEntries = new Dictionary<long, NdsCoreXEntry>();

        private sealed class XSeqPair
        {
            public NdsCoreNode First;
            public NdsCoreNode Second;
        }

        private sealed class XSideState
        {
            public readonly List<NdsCoreNode> Singles = new List<NdsCoreNode>();
            public readonly List<XSeqPair> Pairs = new List<XSeqPair>();
        }

        private readonly XSideState highX = new XSideState();
        private readonly XSideState lowX = new XSideState();

        // Continuous X-axis 1-2-3 numbering across all confirmed nodes:
        // - If an open 1-2 sequence is unbroken and this node advances past node 2 -> 3 (closes sequence).
        // - If an unbroken N1/S1 (Single) is higher/lower than any sub-pair and is broken -> 2 (even if Y-axis is 3).
        // - Y-axis sub-nodes inside a hook below a pre-T X-axis anchor do not create competing X-axis 1s.
        // Node-first numbering by market direction (user rule 2026-11-20):
        // - Market rising: highs are Y (yellow, impulse side), lows are X (white, counter side).
        // - Market falling: lows are Y, highs are X.
        // - Direction flips when price breaks the last opposite-side node.
        // - Y: every impulse-side node is numbered, successive 1-2-3 cycles.
        // - X: counter-side advancing sequence 1-2-3; after a flip it continues on
        //   the old counter side until 3 completes (X+Y pairs), then stops there.
        // Nodes are numbered BEFORE any hook exists; hooks are derived from nodes.
        // Node-first numbering by market direction (user rule 2026-11-20):
        // - Market rising: highs are Y (yellow, impulse side), lows are X (white, counter side).
        // - Market falling: lows are Y, highs are X.
        // - Y: every impulse-side node is numbered, successive 1-2-3 cycles.
        // - X (counter side): a node that does NOT break the previous counter node
        //   (higher low in an up market / lower high in a down market) takes 1;
        //   a node that breaks it takes 2; another break takes 3.
        // - Direction flips when the counter side reaches 3.
        // Nodes are numbered BEFORE any hook exists; hooks are derived from nodes.
        // Node-first numbering by market direction (user rules 2026-11-20):
        // - Market rising: highs are Y (yellow, impulse side), lows are X (white, counter side).
        // - Market falling: lows are Y, highs are X.
        // - Y: every impulse-side node is numbered, successive 1-2-3 cycles.
        // - X (counter side): a node that does NOT break the previous counter node
        //   (higher low in an up market / lower high in a down market) takes 1;
        //   a node that breaks it takes 2; another break takes 3.
        // - Direction flip: the first counter node that breaks the X on its left
        //   takes X2 and, being the first node of the new Y side, also takes Y1.
        //   The X sequence still completes to 3 on that side (X+Y pair), then stops.
        // Nodes are numbered BEFORE any hook exists; hooks are derived from nodes.
        // Node-first numbering by market direction (user rules 2026-11-20):
        // - Market rising: highs are Y (yellow, impulse side), lows are X (white, counter side).
        // - Market falling: lows are Y, highs are X.
        // - Y: every impulse-side node is numbered, successive 1-2-3 cycles.
        // - X (counter side): a node that does NOT break the previous counter node
        //   (higher low in an up market / lower high in a down market) takes 1;
        //   a node that breaks it takes 2; another break takes 3.
        // - Direction flips when the counter side reaches 3.
        // Nodes are numbered BEFORE any hook exists; hooks are derived from nodes.
        // Node-first numbering (user rules 2026-11-20):
        // - Each side (highs, lows) is counted continuously 1-2-3: a node that
        //   advances past the previous same-side node (higher high / lower low)
        //   takes the next number; a node that does not advance restarts at 1.
        //   Counting never resets because of a direction flip.
        // - Colour: the Y side (highs when rising, lows when falling) is yellow,
        //   the X side is white. A Y-side node that does not advance (restart
        //   at 1, e.g. a low above S3) is white; the next advancing nodes are
        //   2 and 3 in yellow.
        // - When the X side completes 1-2-3, its three nodes turn yellow and
        //   direction flips. A sequence that stalls at 2 stays white.
        // Nodes are numbered BEFORE any hook exists; hooks are derived from nodes.
        // Node-first numbering (user rules 2026-11-20):
        // - Each side (highs, lows) is counted in 1-2-3 sequences. A node that
        //   advances past the previous same-side node takes the next number; a
        //   node that does not advance starts a new sequence at 1, while the old
        //   sequence stays open: a later node advancing past the old 2 becomes
        //   that sequence's 3 (a cycle ends only when its 3 forms).
        // - Colour: the Y side (highs when rising, lows when falling) is yellow,
        //   the X side is white. A restart node (new 1) is white. When an X-side
        //   sequence completes 1-2-3, node 1 stays white and nodes 2/3 turn
        //   yellow if formed after the last completed opposite sequence;
        //   direction flips.
        // Nodes are numbered BEFORE any hook exists; hooks are derived from nodes.
        private sealed class AxisSeq
        {
            public int Count;
            public double LastPrice;
            public int StartBar;
            public readonly List<long> Keys = new List<long>();
            public readonly List<int> Bars = new List<int>();
            public readonly List<bool> FormYellow = new List<bool>();
            public readonly List<int> OppOpenStart = new List<int>();
        }

        // Node-first numbering (user rules 2026-11-20/21):
        // - Each side (highs, lows) is counted in 1-2-3 sequences. A node that
        //   advances past the previous same-side node takes the next number; a
        //   node that does not advance starts a new sequence at 1, while the old
        //   sequence stays open: a later node advancing past the old 2 becomes
        //   that sequence's 3 (a cycle ends only when its 3 forms).
        // - Colour: a forming sequence stays white (X axis). Nodes turn yellow
        //   (Y axis) only when node 3 forms: the whole 1-2-3 turns yellow
        //   together. No automatic yellowing by market direction.
        // Nodes are numbered BEFORE any hook exists; hooks are derived from nodes.
        private sealed class AxisSeq
        {
            public int Count;
            public double LastPrice;
            public int StartBar;
            public readonly List<long> Keys = new List<long>();
            public readonly List<int> Bars = new List<int>();
        }

        private void ComputeAxes()
        {
            NodeNumbers.Clear();
            XAxisEntries.Clear();
            YAxisEntries.Clear();
            if (Nodes.Count == 0)
                return;

            List<AxisSeq> seqHigh = new List<AxisSeq>();
            List<AxisSeq> seqLow = new List<AxisSeq>();
            NdsCoreNode lastHigh = null, lastLow = null;

            for (int idx = 0; idx < Nodes.Count; idx++)
            {
                NdsCoreNode node = Nodes[idx];
                bool asc = node.IsHigh;
                string pfx = asc ? "N" : "S";
                NdsCoreNode prev = asc ? lastHigh : lastLow;
                bool advancesPrev = prev == null || (asc
                    ? node.Price > prev.Price + tol
                    : node.Price < prev.Price - tol);
                List<AxisSeq> seqs = asc ? seqHigh : seqLow;

                AxisSeq seq = null;
                // 1) Complete an older open sequence waiting at 2.
                for (int i = 0; i < seqs.Count; i++)
                {
                    AxisSeq s = seqs[i];
                    if (s.Count == 2 && (asc
                        ? node.Price > s.LastPrice + tol
                        : node.Price < s.LastPrice - tol))
                    {
                        seq = s;
                        break;
                    }
                }
                // 2) Continue the latest sequence.
                if (seq == null && advancesPrev && seqs.Count > 0)
                {
                    AxisSeq last = seqs[seqs.Count - 1];
                    if (last.Count < 3)
                        seq = last;
                }
                // 3) Start a new sequence at 1.
                if (seq == null)
                {
                    seq = new AxisSeq();
                    seq.Count = 0;
                    seq.StartBar = node.Bar;
                    seqs.Add(seq);
                }

                seq.Count = seq.Count + 1;
                seq.LastPrice = node.Price;
                seq.Keys.Add(node.Key);
                seq.Bars.Add(node.Bar);
                int count = seq.Count;

                string lbl = pfx + count.ToString(CultureInfo.InvariantCulture);
                NodeNumbers[node.Key] = lbl;
                NdsCoreXEntry e = new NdsCoreXEntry();
                e.Label = lbl;
                e.StartBar = seq.StartBar;
                e.Open = count < 3;
                XAxisEntries[node.Key] = e;

                if (count == 3)
                {
                    // Node 3 formed: the whole 1-2-3 turns yellow together.
                    for (int i = 0; i < seq.Keys.Count; i++)
                    {
                        long k = seq.Keys[i];
                        string yl = pfx + (i + 1).ToString(CultureInfo.InvariantCulture);
                        XAxisEntries.Remove(k);
                        NdsCoreXEntry ye = new NdsCoreXEntry();
                        ye.Label = yl;
                        ye.StartBar = seq.StartBar;
                        ye.Open = i + 1 < 3;
                        YAxisEntries[k] = ye;
                        NodeNumbers[k] = yl;
                    }
                }

                if (asc) lastHigh = node; else lastLow = node;
            }
        }

        // Y axis (yellow): numbering starts after T. T is a NODE, not a hook:
        // the node that completes an X 1-2-3 sequence on its side (major extreme).
        // After T, successive opposite-side nodes are numbered 1, 2, 3 in order
        // (every successive node counts, advancing or not - teacher chart Y1).
        // Y axis (yellow): EVERY node is numbered (user rule 2026-11-20: wherever
        // Heikin-Ashi detects a node it must be numbered, fine or coarse).
        // Successive nodes of each side are counted 1, 2, 3 in repeating cycles;
        // the count restarts at 1 after T. T is a NODE, not a hook: the node that
        // completes an X 1-2-3 sequence on its side (major extreme).

        private readonly Dictionary<string, NdsCoreUnfinished> pairs = new Dictionary<string, NdsCoreUnfinished>();
        private readonly List<NdsCoreUnfinished> openPairs = new List<NdsCoreUnfinished>();
        private readonly List<NdsCoreHook> forming = new List<NdsCoreHook>();
        private readonly HashSet<long> spawnedKeys = new HashSet<long>();
        private int nextId = 1;
        private readonly double tol;
        private readonly double numTol;
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
            public int Seq;
            public NdsCoreHook Parent;
        }

        public NdsCoreTracker(double tol, double numTol, double retrace, double closeBand, int minEachSide, int spawnLookback)
        {
            this.tol = tol;
            this.numTol = numTol;
            this.retrace = retrace;
            this.closeBand = closeBand;
            this.minEachSide = minEachSide;
            this.spawnLookback = spawnLookback;
        }

        public void OnNode(NdsCoreNode node)
        {
            // Only detector-confirmed nodes enter here. Evaluate old pairs before
            // Feed can register a new pair on this same node.
            for (int p = openPairs.Count - 1; p >= 0; p--)
            {
                NdsCoreUnfinished pair = openPairs[p];
                if (node.Bar <= pair.Second.Bar)
                    continue;

                // Structure invalidation: any node breaking beyond First breaks the sequence
                bool beyondFirst = pair.Ascending
                    ? node.Price < pair.First.Price - tol
                    : node.Price > pair.First.Price + tol;
                if (beyondFirst)
                {
                    pair.Broken = true;
                    openPairs.RemoveAt(p);
                    continue;
                }

                if (node.IsHigh != pair.Second.IsHigh)
                    continue;

                if (!Advances(node, pair.Second, pair.Ascending))
                    continue;

                pair.Third = node;
                openPairs.RemoveAt(p);
            }

            List<SpawnSpec> spawns = new List<SpawnSpec>();
            NdsCoreHook[] snap = forming.ToArray();

            for (int i = 0; i < snap.Length; i++)
            {
                NdsCoreHook h = snap[i];
                if (h.State != "forming")
                    continue;
                if (Violates(h, node))
                {
                    CheckBigHook(h, node);
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
                Spawn(spawns[i].Direction, spawns[i].T, spawns[i].Depth, spawns[i].Reason, spawns[i].Seq, spawns[i].Parent);

            if (spawnLookback > 0 || Nodes.Count == 0)
                SpawnByExtreme(node);

            Nodes.Add(node);
        }

        private static SpawnSpec MakeSpawn(string direction, NdsCoreNode t, int depth, string reason, int seq = 1)
        {
            SpawnSpec s = new SpawnSpec();
            s.Seq = seq;
            s.Direction = direction;
            s.T = t;
            s.Depth = depth;
            s.Reason = reason;
            return s;
        }

        // h = H2 (closure child of H1) broken at its own T (= H1 closing counter node 3).
        // If node stays within H1's T (not beyond it by > tol) -> H1+H2 = big hook.
        private void CheckBigHook(NdsCoreHook h, NdsCoreNode node)
        {
            NdsCoreHook p = h.Parent;
            if (p == null || h.Depth != 0 || p.Depth != 0 || p.Direction != h.Direction || p.State != "closed")
                return;
            if (h.Imp.Count == 0)
                return; // H2 never moved: not a hook yet
            bool breaksFirstT = h.Direction == "pos"
                ? node.Price < p.T.Price - tol
                : node.Price > p.T.Price + tol;
            if (breaksFirstT)
                return;
            for (int i = 0; i < BigHooks.Count; i++)
                if (BigHooks[i].First == p && BigHooks[i].Second == h)
                    return;
            NdsCoreBigHook b = new NdsCoreBigHook();
            b.First = p;
            b.Second = h;
            b.BrokeAt = node;
            BigHooks.Add(b);
        }

        public NdsCoreBigHook BigHookOf(NdsCoreHook h)
        {
            for (int i = 0; i < BigHooks.Count; i++)
                if (BigHooks[i].First == h || BigHooks[i].Second == h)
                    return BigHooks[i];
            return null;
        }

        // Label shown on H1's T when H1+H2 formed a big hook.
        public string BigHookLabel(NdsCoreHook h)
        {
            for (int i = 0; i < BigHooks.Count; i++)
                if (BigHooks[i].First == h)
                    return "BIG H" + h.Seq.ToString(CultureInfo.InvariantCulture)
                        + "+" + BigHooks[i].Second.Seq.ToString(CultureInfo.InvariantCulture);
            return "";
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

        // Numbering (teacher rule, confirmed 2026-11-20): next node counts only if it
        // breaks the previous numbered node's level (beyond tol). Equal or not-beyond
        // nodes stay unnumbered. Same strict rule as Beats() and Python hooks_v2 beats().
        private bool Advances(NdsCoreNode node, NdsCoreNode last, bool ascending)
        {
            if (ascending)
                return node.Price > last.Price + tol;
            return node.Price < last.Price - tol;
        }

        // Step 25: sequence continuity across T (user rule, screenshots "جزییات هوک در دو محور X و Y").
        // The numbering was already performed on the X axis before T. If the FIRST same-type node
        // after T breaks the last reference node before T, it continues that open 1-2 sequence
        // instead of starting a new local node 1:
        //   - one reference node before T  -> this node is node 2
        //   - two reference nodes before T -> this node is node 3
        // If the first node after T stays below/above the reference (no break), nothing is inherited
        // and the normal local numbering (node 1) applies.
        private bool TryInheritPriorImpulse(NdsCoreHook h, NdsCoreNode node)
        {
            if (h.Depth != 0)
                return false;
            bool ascending = h.Direction == "pos";
            bool wantHigh = h.ImpIsHigh;

            // First check if an unbroken 1-2 sequence existed before T whose node 2 is broken by `node`.
            List<NdsCoreNode> priorSame = new List<NdsCoreNode>();
            for (int i = 0; i < Nodes.Count; i++)
            {
                NdsCoreNode n = Nodes[i];
                if (n.Bar < h.T.Bar && n.IsHigh == wantHigh)
                    priorSame.Add(n);
            }
            if (priorSame.Count == 0)
                return false;

            for (int j = priorSame.Count - 1; j >= 1; j--)
            {
                NdsCoreNode p2 = priorSame[j];
                NdsCoreNode p1 = priorSame[j - 1];
                if (!Advances(p2, p1, ascending))
                    continue;
                bool brokenBetween = false;
                for (int k = j + 1; k < priorSame.Count; k++)
                {
                    NdsCoreNode mid = priorSame[k];
                    if (Advances(mid, p2, ascending) ||
                        (ascending ? mid.Price < p1.Price - tol : mid.Price > p1.Price + tol))
                    {
                        brokenBetween = true;
                        break;
                    }
                }
                if (!brokenBetween && Advances(node, p2, ascending))
                {
                    h.InheritedNodes.Clear();
                    h.InheritedNodes.Add(p1);
                    h.InheritedNodes.Add(p2);
                    h.InheritedImpCount = 2;
                    h.Imp.Add(node);
                    return true;
                }
            }

            NdsCoreNode pLast = priorSame[priorSame.Count - 1];
            bool brokeLast = ascending
                ? node.Price > pLast.Price + tol
                : node.Price < pLast.Price - tol;
            if (!brokeLast)
                return false;

            h.InheritedNodes.Clear();
            h.InheritedNodes.Add(pLast);
            h.InheritedImpCount = 1;
            h.Imp.Add(node);
            return true;
        }

        private void Feed(NdsCoreHook h, NdsCoreNode node, List<SpawnSpec> spawns)
        {
            bool isImp = (node.IsHigh == h.ImpIsHigh);
            bool ascending = isImp ? (h.Direction == "pos") : (h.Direction == "neg");
            if (!isImp && h.Imp.Count == 0)
                return;

            if (isImp)
            {
                if (h.TotalImpCount == 0)
                {
                    // Step 25: first impulse node after T. If it breaks the last reference node
                    // before T, the prior open 1-2 sequence continues (this node becomes 2 or 3).
                    if (TryInheritPriorImpulse(h, node))
                    {
                        if (h.TotalImpCount == 2)
                        {
                            RegisterPair(h, h.InheritedNodes[0], node, ascending);
                        }
                        else if (h.TotalImpCount == 3)
                        {
                            RegisterPair(h, h.InheritedNodes[0], h.InheritedNodes[1], ascending);
                            string key = h.InheritedNodes[0].Key.ToString(CultureInfo.InvariantCulture) + "_"
                                + h.InheritedNodes[1].Key.ToString(CultureInfo.InvariantCulture) + "_"
                                + (ascending ? "up_" : "dn_") + h.Depth.ToString(CultureInfo.InvariantCulture);
                            NdsCoreUnfinished pair;
                            if (pairs.TryGetValue(key, out pair))
                            {
                                pair.Third = node;
                                openPairs.Remove(pair);
                            }
                        }
                        UpdateLevel(h);
                        return;
                    }
                    h.Imp.Add(node);
                    UpdateLevel(h);
                    return;
                }

                if (h.TotalImpCount < 3)
                {
                    NdsCoreNode lastImp = h.Imp.Count > 0
                        ? h.Imp[h.Imp.Count - 1]
                        : h.InheritedNodes[h.InheritedNodes.Count - 1];
                    if (Advances(node, lastImp, ascending))
                    {
                        NdsCoreNode first = h.GetImpNode(0);
                        h.Imp.Add(node);
                        if (h.TotalImpCount == 2)
                            RegisterPair(h, first, node, ascending);
                        UpdateLevel(h);
                    }
                    return;
                }

                if (h.Depth == 0 && h.F == null && h.Nd == null)
                {
                    NdsCoreNode lastImp = h.Imp.Count > 0
                        ? h.Imp[h.Imp.Count - 1]
                        : h.InheritedNodes[h.InheritedNodes.Count - 1];
                    if (Beats(node, lastImp, ascending))
                    {
                        h.F = node;
                        spawns.Add(MakeSpawn(h.Direction == "pos" ? "neg" : "pos", node, 0, "F"));
                    }
                    else
                    {
                        h.Nd = node;
                    }
                }
                return;
            }

            // Counter side
            if (h.Ctr.Count == 0)
            {
                h.Ctr.Add(node);
                UpdateLevel(h);
                return;
            }

            if (h.Ctr.Count < 3)
            {
                if (Advances(node, h.Ctr[h.Ctr.Count - 1], ascending))
                {
                    h.Ctr.Add(node);
                    if (h.Ctr.Count == 2)
                    {
                        RegisterPair(h, h.Ctr[0], h.Ctr[1], ascending);
                        if (h.Depth == 0)
                            spawns.Add(MakeSpawn(h.Direction, node, 1, "child"));
                    }
                    if (h.Ctr.Count == 3)
                        Close(h, spawns);
                    UpdateLevel(h);
                }
            }
        }

        private void RegisterPair(NdsCoreHook h, NdsCoreNode first, NdsCoreNode second, bool ascending)
        {
            if (first == null || second == null || second.Bar <= first.Bar) return;
            string key = first.Key.ToString(CultureInfo.InvariantCulture) + "_"
                + second.Key.ToString(CultureInfo.InvariantCulture) + "_"
                + (ascending ? "up_" : "dn_") + h.Depth.ToString(CultureInfo.InvariantCulture);
            NdsCoreUnfinished pair;
            if (!pairs.TryGetValue(key, out pair))
            {
                pair = new NdsCoreUnfinished();
                pair.Key = key;
                pair.First = first;
                pair.Second = second;
                pair.Ascending = ascending;
                pair.Depth = h.Depth;
                pairs.Add(key, pair);
                Unfinished.Add(pair);
                openPairs.Add(pair);
            }
            if (!pair.Sources.Contains(h)) pair.Sources.Add(h);
        }

        private void RegisterPair(NdsCoreHook h, List<NdsCoreNode> seq, bool ascending)
        {
            if (seq == null || seq.Count < 2) return;
            RegisterPair(h, seq[0], seq[1], ascending);
        }

        private void UpdateLevel(NdsCoreHook h)
        {
            if (h.TotalImpCount < 2 || h.Imp.Count < 1)
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
            // Diagnostic only: whether node 3 landed in the 86.4% zone.
            // 86.4% is a guide/target for node 3, NOT a gate: Close/Recognized
            // depend on node-3 confirmation alone (see Recognized()).
            h.LevelOk = Math.Abs(frac - retrace) <= closeBand;
            // Hook types (slides 25-29): A = impulse & counter sides both reach 1,2,3.
            // B+ (pos hook): peaks 1,2 only, valleys 1,2,3. B- (neg hook): valleys 1,2 only, peaks 1,2,3.
            // Step 25: inherited impulse nodes count too (sequence continued across T).
            int totalImp = h.TotalImpCount;
            if (totalImp >= 3)
                h.Kind = "A";
            else if (totalImp == 2)
                h.Kind = h.Direction == "pos" ? "B+" : "B-";
            else
                h.Kind = "?";
            h.State = "closed";
            h.ClosedAt = c;
            if (h.Depth == 0)
            {
                SpawnSpec sp = MakeSpawn(h.Direction, c, 0, "closure", h.Seq + 1);
                sp.Parent = h;
                spawns.Add(sp);
            }
        }

        private void Spawn(string direction, NdsCoreNode t, int depth, string reason, int seq = 1, NdsCoreHook parent = null)
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
            h.Seq = seq;
            h.Parent = parent;
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
                if (h.TotalImpCount < minEachSide || h.Ctr.Count < minEachSide)
                    continue;
                // Hook validity is confirmed upon node 3 confirmation.
                // 86.4% retrace is a signal/target zone for node 3, not a rejection requirement.
                o.Add(h);
            }
            return o;
        }

        // Step 16 (p27/p29): reversal at impulse node 3 is valid only if no new same-direction 1-2 forms after it.
        // "" = not applicable, "?" = pending (no continuation 1-2 seen yet; NOT a confirmed reversal),
        // "X" = reversal invalid (a later same-direction hook already has impulse nodes 1,2 after node 3).
        public string ReversalStatus(NdsCoreHook h)
        {
            if (h == null || h.Depth != 0 || h.State == "failed" || h.TotalImpCount < 3)
                return "";
            NdsCoreNode n3 = h.GetImpNode(2);
            if (n3 == null)
                return "";
            for (int i = 0; i < Hooks.Count; i++)
            {
                NdsCoreHook o = Hooks[i];
                if (o == h || o.Depth != 0 || o.State == "failed" || o.Direction != h.Direction)
                    continue;
                if (o.T.Bar < n3.Bar || o.TotalImpCount < 2)
                    continue;
                NdsCoreNode o2 = o.GetImpNode(1);
                if (o2 != null && o2.Bar > n3.Bar)
                    return "X";
            }
            return "?";
        }

        // Looser set for numbering: every non-failed hook with at least one node
        // (ignores LevelOk and minEachSide, so nodes a few ticks off still get numbers)
        public List<NdsCoreHook> LabelHooks()
        {
            List<NdsCoreHook> o = new List<NdsCoreHook>();
            for (int i = 0; i < Hooks.Count; i++)
            {
                NdsCoreHook h = Hooks[i];
                if (h.State == "failed")
                    continue;
                if (h.TotalImpCount + h.Ctr.Count == 0)
                    continue;
                o.Add(h);
            }
            return o;
        }

        private static string Prefix(NdsCoreNode n)
        {
            return n.IsHigh ? "N" : "S";
        }

        private static void AddClaim(Dictionary<long, List<NdsCoreClaim>> claims, NdsCoreNode n, string label, NdsCoreHook h, string role, ref int order, string axis = "Y")
        {
            NdsCoreClaim c = new NdsCoreClaim();
            c.Depth = h.Depth;
            c.NegSpan = -h.Span();
            c.TBar = h.T.Bar;
            c.Order = order++;
            c.Label = label;
            c.HookId = h.Hid;
            c.Role = role;
            c.Axis = axis;
            c.Node = n;
            c.Open = h.State == "forming";

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
            List<NdsCoreHook> rec = LabelHooks();
            int order = 0;
            // "3/1": node 3 of a closed hook that is also T (node 1) of a later hook
            HashSet<long> startKeys = new HashSet<long>();
            for (int k = 0; k < Hooks.Count; k++)
                if (Hooks[k].State != "failed" && Hooks[k].T != null)
                    startKeys.Add(Hooks[k].T.Key);
            for (int r = 0; r < rec.Count; r++)
            {
                NdsCoreHook h = rec[r];
                string role = h.Depth == 0 ? "macro" : "micro";
                for (int i = 0; i < h.InheritedNodes.Count; i++)
                {
                    NdsCoreNode inNode = h.InheritedNodes[i];
                    AddClaim(claims, inNode, Prefix(inNode) + (i + 1).ToString(CultureInfo.InvariantCulture), h, role, ref order, "X");
                }
                for (int i = 0; i < h.Imp.Count; i++)
                {
                    int num = h.InheritedImpCount + i + 1;
                    AddClaim(claims, h.Imp[i], Prefix(h.Imp[i]) + num.ToString(CultureInfo.InvariantCulture), h, role, ref order, "Y");
                }
                for (int i = 0; i < h.Ctr.Count; i++)
                {
                    string lbl = Prefix(h.Ctr[i]) + (i + 1).ToString(CultureInfo.InvariantCulture);
                    if (i == 2 && h.State == "closed" && startKeys.Contains(h.Ctr[i].Key))
                        lbl += "/1";
                    AddClaim(claims, h.Ctr[i], lbl, h, role, ref order, "Y");
                }
                if (h.F != null)
                    AddClaim(claims, h.F, "F", h, role, ref order, "Y");
            }

            Dictionary<long, NdsCoreLabelEntry> res = new Dictionary<long, NdsCoreLabelEntry>();
            foreach (KeyValuePair<long, List<NdsCoreClaim>> kv in claims)
            {
                List<NdsCoreClaim> l = kv.Value;
                l.Sort(delegate(NdsCoreClaim a, NdsCoreClaim b)
                {
                    int c = a.Depth.CompareTo(b.Depth);
                    if (c != 0) return c;
                    // Slide style: the hook-local Y number is the main (yellow) label;
                    // the continuous X number is drawn as the white tier above it.
                    // Inherited X claims must not steal the primary slot from a Y claim.
                    c = (a.Axis == "Y" ? 0 : 1).CompareTo(b.Axis == "Y" ? 0 : 1);
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
            ComputeAxes();
            return res;
        }
    }

    internal sealed class NdsCoreEngine
    {
        public readonly NdsCoreDetector Detector;
        public NdsCoreTracker Tracker;
        private readonly double tol;
        private readonly double numTol;
        private readonly double retrace;
        private readonly double closeBand;
        private readonly int minEachSide;
        private readonly int spawnLookback;
        private int fedCount;

        public NdsCoreEngine(int strength, int minBarsBetween, double tickSize, int tolTicks, int numTolTicks, double retrace, double closeBand, int minEachSide, int spawnLookback, int haConfirmBars, bool haOnly = false)
        {
            double ts = tickSize > 0 ? tickSize : 0.25;
            tol = Math.Max(0, tolTicks) * ts;
            numTol = Math.Max(0, numTolTicks) * ts;
            Detector = new NdsCoreDetector(strength, minBarsBetween, ts, haConfirmBars, tol, haOnly);
            this.retrace = retrace;
            this.closeBand = closeBand;
            this.minEachSide = minEachSide;
            this.spawnLookback = spawnLookback;
            Tracker = NewTracker();
            fedCount = 0;
        }

        private NdsCoreTracker NewTracker()
        {
            return new NdsCoreTracker(tol, numTol, retrace, closeBand, minEachSide, spawnLookback);
        }

        public bool StepBar(int bar, DateTime time, double open, double high, double low, double close)
        {
            int ch = Detector.StepBar(bar, time, open, high, low, close);
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
        private readonly HashSet<string> drawnUnfinishedTags = new HashSet<string>();
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
                SwingStrength = 1;
                MinBarsBetweenNodes = 1;
                HaConfirmBars = 1;
                UseHaOnlyNodes = true;
                TolTicks = 1;
                NumberTolTicks = 8;
                CycleRetracePct = 86.4;
                CloseBand = 0.05;
                MinEachSide = 2;
                SpawnLookback = 3;
                MinAnalysisMinutes = 10;

                // 01) نمایش نودها
                ShowNodes = true;
                ShowUnnumberedNodes = true;
                ShowProvisionalNode = false;
                NodeHighBrush = Brushes.DodgerBlue;
                NodeLowBrush = Brushes.OrangeRed;
                UnnumberedNodeBrush = Brushes.White;

                // 02) نمایش برچسب‌ها و هوک
                ShowLabels = true;
                ShowMicroLayer = true;
                ShowHookT = true;
                ShowLevel86 = true;
                ShowAllHookLevels = false;
                ShowStatusLabel = true;
                StatusPosition = TextPosition.BottomLeft;
                NumberBrush = Brushes.Yellow;
                XAxisBrush = Brushes.White;
                MicroLabelBrush = Brushes.Silver;
                FlagLabelBrush = Brushes.Magenta;
                HookTBrush = Brushes.Gold;
                Level86Brush = Brushes.MediumSpringGreen;
                NumberFontSize = 12;
                LabelPixelOffset = 28;
                LabelTickOffset = 6;
                DotTickOffset = 30;
                OffsetAtrFraction = 0.5;
                MaxDrawLookbackBars = 3000;
                ShowTarget161 = true;
                Target161Brush = Brushes.DeepSkyBlue;
                ShowSymmetry = true;
                SymmetryBrush = Brushes.Orchid;
                ShowRallyTarget = true;
                RallyTargetBrush = Brushes.LimeGreen;
                ShowNdLevel = true;
                NdBrush = Brushes.OrangeRed;
                ShowTimeSymmetry = true;
                TimeSymmetryBrush = Brushes.Khaki;
                ShowReversal = true;
                ReversalBrush = Brushes.Aqua;
                ShowUnfinished = false;
                UnfinishedBrush = Brushes.Silver;
                ShowFlag123 = true;
                Flag123Brush = Brushes.DeepPink;

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
                    NumberTolTicks,
                    CycleRetracePct / 100.0,
                    CloseBand,
                    MinEachSide,
                    SpawnLookback,
                    HaConfirmBars,
                    UseHaOnlyNodes);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || engine == null)
                return;

            bool changed = engine.StepBar(CurrentBar, Time[0], Open[0], High[0], Low[0], Close[0]);
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
            RemoveDrawObject("nds_x_" + sfx);
            RemoveDrawObject("nds_sec_" + sfx);
        }

        private int LabelStepPixels()
        {
            return Math.Max(16, NumberFontSize + 5);
        }

        // Scale-independent distance of dots from candles: ticks + fraction of average bar range.
        private double NodeOffset()
        {
            double ts = TickSize > 0 ? TickSize : 0.25;
            double avg = 0;
            int n = Math.Min(20, CurrentBar + 1);
            for (int i = 0; i < n; i++)
                avg += High[i] - Low[i];
            if (n > 0) avg /= n;
            return DotTickOffset * ts + OffsetAtrFraction * avg;
        }

        private double LabelBasePrice(NdsCoreNode nd)
        {
            double ts = TickSize > 0 ? TickSize : 0.25;
            int dir = nd.IsHigh ? 1 : -1;
            return nd.Price + dir * (LabelTickOffset * ts + NodeOffset());
        }

        private bool ShouldDrawXAxisTier(long nodeKey, NdsCoreClaim primary, out NdsCoreXEntry xEntry)
        {
            xEntry = null;
            if (engine == null || !engine.Tracker.XAxisEntries.TryGetValue(nodeKey, out xEntry))
                return false;
            if (primary == null)
                return true;
            // Slide style (جزئیات هوک در دو محور X و Y): always show the white X number
            // next to the yellow Y number, even when both are the same (2/2, 3/3),
            // so the X/Y relationship is visible on every node. The only exception:
            // the primary label already IS this X number.
            return !(primary.Axis == "X" && primary.Label == xEntry.Label);
        }

        private int VisibleTiersOnNode(long key, Dictionary<long, NdsCoreLabelEntry> labs)
        {
            NdsCoreXEntry xe = null, ye = null;
            bool hasX = engine != null && engine.Tracker.XAxisEntries.TryGetValue(key, out xe) && !string.IsNullOrEmpty(xe.Label);
            bool hasY = engine != null && engine.Tracker.YAxisEntries.TryGetValue(key, out ye) && !string.IsNullOrEmpty(ye.Label);
            NdsCoreLabelEntry e = null;
            bool isF = labs != null && labs.TryGetValue(key, out e) && e.Primary != null && e.Primary.Label == "F";
            int tiers = 0;
            if (isF || hasY || hasX)
                tiers = 1;
            if (hasX && (isF || hasY))
                tiers++;
            if (ShowMicroLayer && e != null && e.Secondary.Count > 0)
                tiers++;
            return tiers;
        }

        // Q9: open (forming) sequences are drawn faded; closed ones solid.
        private static Brush Fade(Brush b, bool open)
        {
            if (!open || b == null) return b;
            Brush c = b.Clone();
            c.Opacity = 0.4;
            c.Freeze();
            return c;
        }

        private void DrawNodeObjects(NdsCoreNode nd, NdsCoreClaim primary, List<NdsCoreClaim> secondary)
        {
            string sfx = TagSuffix(nd.Key);
            bool open = primary != null && primary.Open;

            if (ShowNodes)
            {
                Brush dotBrush = nd.IsHigh ? NodeHighBrush : NodeLowBrush;
                double dotY = nd.Price + (nd.IsHigh ? 1 : -1) * NodeOffset();
                Draw.Dot(this, "nds_" + sfx, false, nd.Time, dotY, Fade(dotBrush, open));
            }
            else
            {
                RemoveDrawObject("nds_" + sfx);
            }

            if (!ShowLabels)
            {
                RemoveDrawObject("nds_num_" + sfx);
                RemoveDrawObject("nds_x_" + sfx);
                RemoveDrawObject("nds_sec_" + sfx);
                return;
            }

            int dir = nd.IsHigh ? 1 : -1;
            int stepPx = LabelStepPixels();
            double basePrice = LabelBasePrice(nd);

            // Node-first display: numbers come from the X/Y axis entries computed
            // from nodes alone. Y (yellow) is the main label, X (white) the tier
            // above it; a node with neither gets no number. Hook claims no longer
            // gate node numbers (only the F marker is still taken from them).
            NdsCoreXEntry xEntry = null, yEntry = null;
            bool hasX = engine != null && engine.Tracker.XAxisEntries.TryGetValue(nd.Key, out xEntry) && !string.IsNullOrEmpty(xEntry.Label);
            bool hasY = engine != null && engine.Tracker.YAxisEntries.TryGetValue(nd.Key, out yEntry) && !string.IsNullOrEmpty(yEntry.Label);
            bool isF = primary != null && primary.Label == "F";

            int tier = 0;
            if (isF)
            {
                Draw.Text(this, "nds_num_" + sfx, false, "F", nd.Time, basePrice,
                    dir * LabelPixelOffset, Fade(FlagLabelBrush, open), fontMacro,
                    System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                tier = 1;
            }
            else if (hasY)
            {
                Draw.Text(this, "nds_num_" + sfx, false, yEntry.Label, nd.Time, basePrice,
                    dir * LabelPixelOffset, Fade(NumberBrush, open), fontMacro,
                    System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                tier = 1;
            }
            else if (hasX)
            {
                Draw.Text(this, "nds_num_" + sfx, false, xEntry.Label, nd.Time, basePrice,
                    dir * LabelPixelOffset, XAxisBrush, fontMacro,
                    System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                tier = 1;
                hasX = false; // already drawn as the main label
            }
            else
            {
                RemoveDrawObject("nds_num_" + sfx);
            }

            if (hasX && tier >= 1)
            {
                Draw.Text(this, "nds_x_" + sfx, false, xEntry.Label, nd.Time, basePrice,
                    dir * (LabelPixelOffset + tier * stepPx), XAxisBrush, fontMacro,
                    System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                tier++;
            }
            else
            {
                RemoveDrawObject("nds_x_" + sfx);
            }

            if (ShowMicroLayer && secondary != null && secondary.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < secondary.Count; i++)
                {
                    if (i > 0) sb.Append('/');
                    sb.Append(secondary[i].Label);
                }
                Draw.Text(this, "nds_sec_" + sfx, false, sb.ToString(), nd.Time, basePrice,
                    dir * (LabelPixelOffset + tier * stepPx), MicroLabelBrush, fontMicro,
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
            RemoveDrawObject("nds_t161i_" + k);
            RemoveDrawObject("nds_t161i_" + k + "txt");
            RemoveDrawObject("nds_t161c_" + k);
            RemoveDrawObject("nds_t161c_" + k + "txt");
            RemoveDrawObject("nds_sym_" + k);
            RemoveDrawObject("nds_sym_" + k + "txt");
            RemoveDrawObject("nds_rly_" + k);
            RemoveDrawObject("nds_rly_" + k + "txt");
            RemoveDrawObject("nds_nd_" + k);
            RemoveDrawObject("nds_nd_" + k + "txt");
            RemoveDrawObject("nds_tsi_" + k);
            RemoveDrawObject("nds_tsi_" + k + "txt");
            RemoveDrawObject("nds_tsc_" + k);
            RemoveDrawObject("nds_tsc_" + k + "txt");
            RemoveDrawObject("nds_rev_" + k);
            RemoveDrawObject("nds_f123_" + k);
            RemoveDrawObject("nds_f123_" + k + "txt");
            RemoveDrawObject("nds_f123_" + k + "tp");
            RemoveDrawObject("nds_f123_" + k + "tptxt");
        }

        // Step 4: expansion symmetry. A_i = |Imp[i]-Ctr[i]|, B1 = |Imp[0]-Ctr[1]|,
        // B2 = B1*A2/A1, projected node 3 of Ctr = Imp[1] -/+ B2. Forming hooks with Ctr 1-2 only.
        private void DrawSymmetry(NdsCoreHook h, string tag)
        {
            bool show = ShowSymmetry && h.State == "forming" && h.TotalImpCount >= 2 && h.Ctr.Count == 2
                && (h.Depth == 0 || ShowMicroLayer);
            double a1 = 0, a2 = 0, b1 = 0;
            NdsCoreNode imp0 = h.GetImpNode(0);
            NdsCoreNode imp1 = h.GetImpNode(1);
            if (show && imp0 != null && imp1 != null)
            {
                a1 = Math.Abs(imp0.Price - h.Ctr[0].Price);
                a2 = Math.Abs(imp1.Price - h.Ctr[1].Price);
                b1 = Math.Abs(imp0.Price - h.Ctr[1].Price);
                show = a1 > 0;
            }
            else
            {
                show = false;
            }
            if (!show)
            {
                RemoveDrawObject(tag);
                RemoveDrawObject(tag + "txt");
                return;
            }
            double b2 = b1 * a2 / a1;
            double target = h.Direction == "pos" ? imp1.Price - b2 : imp1.Price + b2;
            DateTime startTime = h.Ctr[1].Time > imp1.Time ? h.Ctr[1].Time : imp1.Time;
            DateTime endTime = Time[0] > startTime ? Time[0] : startTime.AddMinutes(1);
            Draw.Line(this, tag, false, startTime, target, endTime, target,
                Fade(SymmetryBrush, true), DashStyleHelper.DashDot, 1);
            string name = h.Ctr[0].IsHigh ? "N" : "S";
            string txt = "SYM " + name + "3 h" + h.Hid.ToString(CultureInfo.InvariantCulture)
                + " " + target.ToString("0.##", CultureInfo.InvariantCulture);
            Draw.Text(this, tag + "txt", false, txt, endTime, target,
                -8, SymmetryBrush, fontMicro,
                System.Windows.TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);
        }

        // Step 15: time symmetry (p23-24, Symmetry: time 1->2 ~ 2->3). While a 1-2 sequence is open,
        // expected bar of node 3 = bar2 + (bar2 - bar1). Vertical dotted line (may lie in the future).
        private void DrawTimeSymmetry(NdsCoreHook h, List<NdsCoreNode> seq, string tag)
        {
            bool show = ShowTimeSymmetry && h.State == "forming" && seq.Count == 2
                && (h.Depth == 0 || ShowMicroLayer) && seq[1].Bar > seq[0].Bar;
            if (!show)
            {
                RemoveDrawObject(tag);
                RemoveDrawObject(tag + "txt");
                return;
            }
            int projBar = seq[1].Bar + (seq[1].Bar - seq[0].Bar);
            int barsAgo = CurrentBar - projBar;
            Draw.VerticalLine(this, tag, barsAgo, Fade(TimeSymmetryBrush, true), DashStyleHelper.Dot, 1);
            string name = seq[0].IsHigh ? "N" : "S";
            string txt = "T-SYM " + name + "3 h" + h.Hid.ToString(CultureInfo.InvariantCulture);
            double y = seq[1].Price + (seq[1].IsHigh ? 1 : -1) * NodeOffset();
            Draw.Text(this, tag + "txt", false, txt, barsAgo, y,
                0, TimeSymmetryBrush, fontMicro,
                System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
        }

        // Step 14: ND level (slides 51-52). After F breaks impulse node 3 (123Flag), the new hook's node 3
        // is expected at ND = 86.4% retrace of the T->F leg. Line runs from F until price first touches it.
        private void DrawNdLevel(NdsCoreHook h, string tag)
        {
            bool show = ShowNdLevel && h.Depth == 0 && h.F != null && h.F.Bar <= CurrentBar;
            if (!show)
            {
                RemoveDrawObject(tag);
                RemoveDrawObject(tag + "txt");
                return;
            }
            double r = CycleRetracePct / 100.0;
            bool pos = h.Direction == "pos";
            double nd = pos ? h.F.Price - r * (h.F.Price - h.T.Price)
                            : h.F.Price + r * (h.T.Price - h.F.Price);
            int endBar = CurrentBar;
            bool touched = false;
            for (int b = h.F.Bar + 1; b <= CurrentBar; b++)
            {
                if (pos ? Low.GetValueAt(b) <= nd : High.GetValueAt(b) >= nd)
                {
                    endBar = b;
                    touched = true;
                    break;
                }
            }
            DateTime endTime = Time.GetValueAt(endBar);
            if (endTime <= h.F.Time)
                endTime = h.F.Time.AddMinutes(1);
            Draw.Line(this, tag, false, h.F.Time, nd, endTime, nd,
                Fade(NdBrush, !touched), touched ? DashStyleHelper.Solid : DashStyleHelper.Dash, 1);
            string txt = "ND h" + h.Hid.ToString(CultureInfo.InvariantCulture)
                + " " + nd.ToString("0.##", CultureInfo.InvariantCulture);
            Draw.Text(this, tag + "txt", false, txt, endTime, nd,
                8, NdBrush, fontMicro,
                System.Windows.TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);
        }

        // Step 18: 123Flag display (no orders). Depth-0 hook with impulse nodes 1-2-3:
        // flag boundary = last opposite extreme between node 2 and node 3 (buy: highest high between low2 & low3);
        // break = first wick beyond boundary after node 3; TP1 = peak before the flag (between node 1 and node 2).
        private void DrawFlag123(NdsCoreHook h, string tag)
        {
            NdsCoreNode n1 = h.GetImpNode(0);
            NdsCoreNode n2 = h.GetImpNode(1);
            NdsCoreNode n3 = h.GetImpNode(2);
            bool show = ShowFlag123 && h.Depth == 0 && h.TotalImpCount >= 3
                && n1 != null && n2 != null && n3 != null
                && n3.Bar <= CurrentBar && n3.Bar > n2.Bar;
            if (!show)
            {
                RemoveDrawObject(tag);
                RemoveDrawObject(tag + "txt");
                RemoveDrawObject(tag + "tp");
                RemoveDrawObject(tag + "tptxt");
                return;
            }
            bool buy = h.Direction == "neg"; // impulse nodes are lows -> buy flag
            double boundary = buy ? double.MinValue : double.MaxValue;
            int bndBar = n2.Bar;
            for (int b = n2.Bar + 1; b < n3.Bar; b++)
            {
                double p = buy ? High.GetValueAt(b) : Low.GetValueAt(b);
                if (buy ? p >= boundary : p <= boundary) { boundary = p; bndBar = b; }
            }
            if (bndBar == n2.Bar)
            {
                RemoveDrawObject(tag);
                RemoveDrawObject(tag + "txt");
                RemoveDrawObject(tag + "tp");
                RemoveDrawObject(tag + "tptxt");
                return;
            }
            int endBar = CurrentBar;
            bool broken = false;
            for (int b = n3.Bar + 1; b <= CurrentBar; b++)
            {
                if (buy ? High.GetValueAt(b) > boundary : Low.GetValueAt(b) < boundary)
                {
                    endBar = b;
                    broken = true;
                    break;
                }
            }
            DateTime startTime = Time.GetValueAt(bndBar);
            DateTime endTime = Time.GetValueAt(endBar);
            if (endTime <= startTime)
                endTime = startTime.AddMinutes(1);
            Draw.Line(this, tag, false, startTime, boundary, endTime, boundary,
                Fade(Flag123Brush, !broken), broken ? DashStyleHelper.Solid : DashStyleHelper.Dash, 2);
            string txt = "123F h" + h.Hid.ToString(CultureInfo.InvariantCulture)
                + (buy ? " B" : " S") + (broken ? " BRK" : "");
            Draw.Text(this, tag + "txt", false, txt, endTime, boundary,
                8, Flag123Brush, fontMicro,
                System.Windows.TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);

            // TP1 = peak before the flag: opposite extreme between node 1 and node 2.
            double tp = buy ? double.MinValue : double.MaxValue;
            int tpBar = -1;
            for (int b = n1.Bar + 1; b < n2.Bar; b++)
            {
                double p = buy ? High.GetValueAt(b) : Low.GetValueAt(b);
                if (buy ? p >= tp : p <= tp) { tp = p; tpBar = b; }
            }
            if (tpBar < 0)
            {
                RemoveDrawObject(tag + "tp");
                RemoveDrawObject(tag + "tptxt");
                return;
            }
            DateTime tpStart = Time.GetValueAt(tpBar);
            // TP1 line ends at the flag break bar when broken (not at the current bar);
            // while the flag is unbroken it runs to the current bar.
            DateTime tpEnd = broken ? endTime : (Time[0] > tpStart ? Time[0] : tpStart.AddMinutes(1));
            if (tpEnd <= tpStart)
                tpEnd = tpStart.AddMinutes(1);
            Draw.Line(this, tag + "tp", false, tpStart, tp, tpEnd, tp,
                Fade(Flag123Brush, !broken), DashStyleHelper.Dot, 1);
            string tpTxt = "TP1 h" + h.Hid.ToString(CultureInfo.InvariantCulture)
                + " " + tp.ToString("0.##", CultureInfo.InvariantCulture);
            Draw.Text(this, tag + "tptxt", false, tpTxt, tpEnd, tp,
                8, Flag123Brush, fontMicro,
                System.Windows.TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);
        }

        // Step 13: Rally AB=CD target (slides 31-34). Rally hook (depth 0, Seq>=3) forming with Ctr>=2:
        // Rally 2 starts at counter node 2 with length ~ Rally 1 (T -> Imp[0]).
        private void DrawRallyTarget(NdsCoreHook h, string tag)
        {
            bool show = ShowRallyTarget && h.Depth == 0 && h.Seq >= 3 && h.State == "forming"
                && h.Imp.Count >= 1 && h.Ctr.Count >= 2;
            if (!show)
            {
                RemoveDrawObject(tag);
                RemoveDrawObject(tag + "txt");
                return;
            }
            double rally1 = Math.Abs(h.Imp[0].Price - h.T.Price);
            NdsCoreNode c2 = h.Ctr[1];
            double target = h.Direction == "pos" ? c2.Price + rally1 : c2.Price - rally1;
            DateTime endTime = Time[0] > c2.Time ? Time[0] : c2.Time.AddMinutes(1);
            Draw.Line(this, tag, false, c2.Time, target, endTime, target,
                Fade(RallyTargetBrush, true), DashStyleHelper.Dash, 1);
            string txt = "AB=CD R" + (h.Seq - 2).ToString(CultureInfo.InvariantCulture)
                + " h" + h.Hid.ToString(CultureInfo.InvariantCulture)
                + " " + target.ToString("0.##", CultureInfo.InvariantCulture);
            Draw.Text(this, tag + "txt", false, txt, endTime, target,
                8, RallyTargetBrush, fontMicro,
                System.Windows.TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);
        }

        // Step 3: 161.8% target of node 3 = P1 + 1.618*(P2-P1), only while the 1-2 sequence is open.
        private void DrawTarget161(NdsCoreHook h, List<NdsCoreNode> seq, string tag)
        {
            bool show = ShowTarget161 && h.State == "forming" && seq.Count == 2
                && (h.Depth == 0 || ShowMicroLayer);
            if (!show)
            {
                RemoveDrawObject(tag);
                RemoveDrawObject(tag + "txt");
                return;
            }
            string name = seq[0].IsHigh ? "N" : "S";
            DrawPairTarget(seq[0], seq[1], tag, name + "3 h" + h.Hid.ToString(CultureInfo.InvariantCulture));
        }

        private void DrawPairTarget(NdsCoreNode p1, NdsCoreNode p2, string tag, string label)
        {
            double target = p1.Price + 1.618 * (p2.Price - p1.Price);
            DateTime endTime = Time[0] > p2.Time ? Time[0] : p2.Time.AddMinutes(1);
            Draw.Line(this, tag, false, p2.Time, target, endTime, target,
                Fade(Target161Brush, true), DashStyleHelper.Dash, 1);
            if (ShowLabels)
                Draw.Text(this, tag + "txt", false, "161.8% " + label + " "
                    + target.ToString("0.##", CultureInfo.InvariantCulture), endTime, target,
                    8, NumberBrush, fontMicro,
                    System.Windows.TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);
            else
                RemoveDrawObject(tag + "txt");
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
                double tY = h.T.Price + (h.T.IsHigh ? 1 : -1) * NodeOffset();
                Draw.Diamond(this, "nds_T_" + k, false, h.T.Time, tY, HookTBrush);
                string txt = HookName(h) + (h.Direction == "pos" ? "+" : "-")
                    + (h.State == "closed" ? ":" + h.Kind : "");
                string big = engine.Tracker.BigHookLabel(h);
                if (big != "")
                    txt += " " + big;
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
                // Step 22: line spans only the hook itself (T -> closing node 3, or last node while forming),
                // never the whole chart.
                NdsCoreNode endNode = (h.State == "closed" && h.ClosedAt != null) ? h.ClosedAt : null;
                if (endNode == null)
                {
                    int lastBar = h.LastIndex();
                    endNode = h.T;
                    for (int i = 0; i < h.Imp.Count; i++) if (h.Imp[i].Bar == lastBar) endNode = h.Imp[i];
                    for (int i = 0; i < h.Ctr.Count; i++) if (h.Ctr[i].Bar == lastBar) endNode = h.Ctr[i];
                }
                DateTime endTime = endNode.Time;
                if (endTime <= h.T.Time)
                    endTime = h.T.Time.AddMinutes(1);
                bool open = h.State == "forming";
                Draw.Line(this, "nds_lvl_" + k, false, h.T.Time, h.Level, endTime, h.Level,
                    Fade(Level86Brush, open), open ? DashStyleHelper.Dash : DashStyleHelper.Solid, 1);
                string ltxt = CycleRetracePct.ToString("0.#", CultureInfo.InvariantCulture)
                    + "% " + HookName(h) + " " + h.Level.ToString("0.##", CultureInfo.InvariantCulture);
                Draw.Text(this, "nds_lvltxt_" + k, false, ltxt, endTime, h.Level,
                    8, Level86Brush, fontMicro,
                    System.Windows.TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);
            }
            else
            {
                RemoveDrawObject("nds_lvl_" + k);
                RemoveDrawObject("nds_lvltxt_" + k);
            }

            // Step 20 (revised 2026-11-20): 86.4% line + 123Flag/TP1 + rally AB=CD are drawn;
            // other helper lines (161.8, symmetry, ND, time symmetry) stay removed.
            string[] extra = { "nds_t161i_", "nds_t161c_", "nds_sym_", "nds_nd_",
                               "nds_tsi_", "nds_tsc_" };
            foreach (string pfx in extra)
            {
                RemoveDrawObject(pfx + k);
                RemoveDrawObject(pfx + k + "txt");
            }
            DrawFlag123(h, "nds_f123_" + k);
            DrawRallyTarget(h, "nds_rly_" + k);
            DrawReversal(h, "nds_rev_" + k);
        }

        // Step 21: display name. Depth-0 chain: H1, H2, then R1, R2 (Hook-Hook-Rally). Micro hooks: "h" + id.
        private string HookName(NdsCoreHook h)
        {
            if (h.Depth != 0)
                return "h" + h.Hid.ToString(CultureInfo.InvariantCulture);
            if (h.Seq <= 2)
                return "H" + h.Seq.ToString(CultureInfo.InvariantCulture);
            return "R" + (h.Seq - 2).ToString(CultureInfo.InvariantCulture);
        }

        // Step 16: reversal-at-3 status label next to impulse node 3 ("REV?" faded = pending, "REV X" = continuation 1-2 formed).
        private void DrawReversal(NdsCoreHook h, string tag)
        {
            string st = ShowReversal ? engine.Tracker.ReversalStatus(h) : "";
            if (st == "")
            {
                RemoveDrawObject(tag);
                return;
            }
            NdsCoreNode n3 = h.GetImpNode(2);
            if (n3 == null)
            {
                RemoveDrawObject(tag);
                return;
            }
            bool pending = st == "?";
            double y = n3.Price + (n3.IsHigh ? 1 : -1) * 2 * NodeOffset();
            string txt = (pending ? "REV?" : "REV X") + " " + HookName(h);
            Draw.Text(this, tag, false, txt, n3.Time, y, 0,
                Fade(ReversalBrush, pending), fontMicro,
                System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
        }

        private void DrawUnfinishedNode(NdsCoreNode node, int number, string id, string tag,
            bool open, HashSet<string> keep, HashSet<long> normalNodes,
            Dictionary<long, NdsCoreLabelEntry> labs, Dictionary<long, int> tiers)
        {
            if (node.Bar > CurrentBar || CurrentBar - node.Bar > MaxDrawLookbackBars) return;
            int dir = node.IsHigh ? 1 : -1;
            if (ShowNodes && !normalNodes.Contains(node.Key))
            {
                Draw.Dot(this, tag, false, node.Time, node.Price + dir * NodeOffset(), Fade(UnfinishedBrush, open));
                keep.Add(tag);
            }
            if (ShowLabels)
            {
                int tier;
                if (!tiers.TryGetValue(node.Key, out tier))
                    tier = VisibleTiersOnNode(node.Key, labs) + 2;
                tiers[node.Key] = tier + 1;
                Draw.Text(this, tag + "txt", false, "U" + number + " " + id,
                    node.Time, LabelBasePrice(node), dir * (LabelPixelOffset + tier * LabelStepPixels()),
                    Fade(NumberBrush, open), fontMicro, System.Windows.TextAlignment.Center,
                    Brushes.Transparent, Brushes.Transparent, 0);
                keep.Add(tag + "txt");
            }
        }

        // Independent drawings survive removal of their parent hook's drawings.
        private void SyncUnfinished(HashSet<int> normalHooks, HashSet<long> normalNodes,
            Dictionary<long, NdsCoreLabelEntry> labs)
        {
            // U labels removed (user rule 2026-11-20): nodes are numbered first on
            // the X/Y axes; unfinished-pair (U) labels came from the old hook logic
            // and must not appear. Only clean up any previously drawn U objects.
            foreach (string old in drawnUnfinishedTags)
                RemoveDrawObject(old);
            drawnUnfinishedTags.Clear();
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

                NdsCoreXEntry sigX, sigY;
                bool nodeHasX = engine.Tracker.XAxisEntries.TryGetValue(nd.Key, out sigX) && !string.IsNullOrEmpty(sigX.Label);
                bool nodeHasY = engine.Tracker.YAxisEntries.TryGetValue(nd.Key, out sigY) && !string.IsNullOrEmpty(sigY.Label);
                if (primary == null && !nodeHasX && !nodeHasY && !ShowUnnumberedNodes)
                    continue;

                keepNodes.Add(nd.Key);
                StringBuilder sb = new StringBuilder();
                sb.Append(nd.Price.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                  .Append(ShowNodes ? '1' : '0').Append(ShowLabels ? '1' : '0').Append('|');
                if (nodeHasX)
                {
                    sb.Append('X').Append(sigX.Label).Append('@')
                      .Append(sigX.StartBar.ToString(CultureInfo.InvariantCulture)).Append('|');
                }
                if (nodeHasY)
                {
                    sb.Append('Y').Append(sigY.Label).Append('@')
                      .Append(sigY.StartBar.ToString(CultureInfo.InvariantCulture)).Append('|');
                }
                if (primary != null)
                {
                    sb.Append(primary.Label).Append('|').Append(primary.Role).Append('|')
                      .Append(primary.Axis).Append('|')
                      .Append(primary.Open ? '1' : '0').Append('|')
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
                    + h.TotalImpCount.ToString(CultureInfo.InvariantCulture) + "|"
                    + h.Ctr.Count.ToString(CultureInfo.InvariantCulture) + "|"
                    + (ShowTarget161 ? "G1" : "G0") + "|"
                    + (ShowSymmetry ? "Y1" : "Y0") + "|"
                    + (ShowRallyTarget ? "R1" : "R0") + "|"
                    + (ShowNdLevel ? "D1" : "D0") + (h.F != null ? "F1" : "F0") + "|"
                    + (ShowTimeSymmetry ? "X1" : "X0") + "|"
                    + (ShowReversal ? "V" + engine.Tracker.ReversalStatus(h) : "V-") + "|"
                    + engine.Tracker.BigHookLabel(h) + "|"
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

            SyncUnfinished(keepHooks, keepNodes, labs);
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
            Draw.Dot(this, "nds_provH", false, Time[hiAgo], High[hiAgo] + NodeOffset(), bh);

            Brush bl = NodeLowBrush.Clone();
            bl.Opacity = 0.35;
            bl.Freeze();
            Draw.Dot(this, "nds_provL", false, Time[loAgo], Low[loAgo] - NodeOffset(), bl);
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
                txt = "NDS (" + (UseHaOnlyNodes ? "HA" : "S=" + SwingStrength.ToString(CultureInfo.InvariantCulture)) + "): no macro hook | nodes="
                    + engine.Detector.Nodes.Count.ToString(CultureInfo.InvariantCulture) + warn;
            }
            else
            {
                txt = "NDS (" + (UseHaOnlyNodes ? "HA" : "S=" + SwingStrength.ToString(CultureInfo.InvariantCulture)) + ") "
                    + HookName(active)
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
                    h.TotalImpCount, h.Ctr.Count, h.F != null ? "1" : "0",
                    h.Level, h.Fraction, h.LevelOk ? "1" : "0", rec.Contains(h) ? "1" : "0"));
            }
        }
        #endregion

        #region Properties
        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "قدرت سویینگ", Description = "حالت هیکن‌آشی: حداقل تعداد کندل هم‌رنگ یک موج تا نود حساب شود (عدد بزرگ‌تر = نودهای کمتر و بزرگ‌تر). حالت قدیمی: تعداد کندل در هر طرف قله/دره", GroupName = "00) تشخیص نود و هوک", Order = 0)]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "حداقل فاصله دو نود (کندل)", Description = "گارد فاصله بین دو نود متوالی؛ تقارن قله/دره همیشه اولویت دارد", GroupName = "00) تشخیص نود و هوک", Order = 1)]
        public int MinBarsBetweenNodes { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "تأیید نود: تعداد کندل هیکن‌آشی برگشتی", Description = "تعداد کندل‌های متوالی هیکن‌آشی خلاف جهت (همان تایم‌فریم) پس از سقف/کف برای تأیید نود؛ 0 = غیرفعال", GroupName = "00) تشخیص نود و هوک", Order = 1)]
        public int HaConfirmBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نود فقط با تغییر رنگ هیکن‌آشی", Description = "روشن: نود = نوک سایه‌ی افراطی هر موج هم‌رنگ هیکن‌آشی (بدون مقایسه‌ی ۵ کندل). خاموش: روش قدیمی سویینگ", GroupName = "00) تشخیص نود و هوک", Order = 1)]
        public bool UseHaOnlyNodes { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "تلورانس هم‌سطحی (تیک)", Description = "تلورانس ادغام سقف/کف دوقلو، شکست T و عبور F", GroupName = "00) تشخیص نود و هوک", Order = 2)]
        public int TolTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "تلورانس شماره‌گذاری / هوک فشرده (تیک)", Description = "نودی که تا این تعداد تیک هم‌سطح نود شماره‌دار قبلی باشد (یا از آن عبور کند) شماره بعدی را می‌گیرد (هوک فشرده، اسلاید ۸۱)", GroupName = "00) تشخیص نود و هوک", Order = 3)]
        public int NumberTolTicks { get; set; }

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
        [Display(Name = "نمایش نودهای خارج از هوک (محور X)", GroupName = "01) نمایش نودها", Order = 1)]
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
        [Browsable(false)]
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
        [Display(Name = "رنگ اعداد محور Y (هوک - زرد)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 7)]
        public Brush NumberBrush { get; set; }

        [Browsable(false)]
        public string NumberBrushSerializable
        {
            get { return Serialize.BrushToString(NumberBrush); }
            set { NumberBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "رنگ اعداد محور X (پیوسته - سفید)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 7)]
        public Brush XAxisBrush { get; set; }

        [Browsable(false)]
        public string XAxisBrushSerializable
        {
            get { return Serialize.BrushToString(XAxisBrush); }
            set { XAxisBrush = Serialize.StringToBrush(value); }
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
        [Range(0, 200)]
        [Display(Name = "فاصله دایره نود از کندل (تیک)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 14)]
        public int DotTickOffset { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 5.0)]
        [Display(Name = "فاصله دایره (ضریب میانگین رنج کندل)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 15)]
        public double OffsetAtrFraction { get; set; }

        [NinjaScriptProperty]
        [Range(100, 50000)]
        [Display(Name = "بازه رسم (کندل)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 15)]
        public int MaxDrawLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش هدف ۱۶۱.۸٪ نود ۳ (دنباله باز)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 16)]
        public bool ShowTarget161 { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ خط هدف ۱۶۱.۸٪", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 17)]
        public Brush Target161Brush { get; set; }

        [Browsable(false)]
        public string Target161BrushSerializable
        {
            get { return Serialize.BrushToString(Target161Brush); }
            set { Target161Brush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "نمایش پیش‌بینی تقارن انبساطی (اصلاح بعدی)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 18)]
        public bool ShowSymmetry { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ خط تقارن انبساطی", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 19)]
        public Brush SymmetryBrush { get; set; }

        [Browsable(false)]
        public string SymmetryBrushSerializable
        {
            get { return Serialize.BrushToString(SymmetryBrush); }
            set { SymmetryBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "نمایش هدف رالی AB=CD", Description = "در هوک رالی (بعد از هوک ۱ و ۲): هدف رالی ۲ = نود ۲ ± طول رالی ۱", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 20)]
        public bool ShowRallyTarget { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ خط هدف رالی", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 21)]
        public Brush RallyTargetBrush { get; set; }

        [Browsable(false)]
        public string RallyTargetBrushSerializable
        {
            get { return Serialize.BrushToString(RallyTargetBrush); }
            set { RallyTargetBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "نمایش سطح ND بعد از F", Description = "بعد از شکست نود ۳ (F): سطح ۸۶.۴٪ موج T→F، محل مورد انتظار نود ۳ هوک جدید (اسلاید ۵۱-۵۲)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 22)]
        public bool ShowNdLevel { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ خط ND", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 23)]
        public Brush NdBrush { get; set; }

        [Browsable(false)]
        public string NdBrushSerializable
        {
            get { return Serialize.BrushToString(NdBrush); }
            set { NdBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "نمایش تقارن زمانی نود ۳", Description = "در دنباله باز ۱-۲: زمان مورد انتظار نود ۳ = کندل نود ۲ + فاصله زمانی ۱→۲ (اسلاید ۲۳-۲۴)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 24)]
        public bool ShowTimeSymmetry { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ خط تقارن زمانی", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 25)]
        public Brush TimeSymmetryBrush { get; set; }

        [Browsable(false)]
        public string TimeSymmetryBrushSerializable
        {
            get { return Serialize.BrushToString(TimeSymmetryBrush); }
            set { TimeSymmetryBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "نمایش وضعیت برگشت در نود ۳", Description = "REV? = در انتظار (هنوز ۱-۲ جدید هم‌جهت نیامده، برگشت قطعی نیست)؛ REV X = بعد از نود ۳ سری ۱-۲ جدید ساخته شد، برگشت باطل (اسلاید p27/p29)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 26)]
        public bool ShowReversal { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ برچسب برگشت", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 27)]
        public Brush ReversalBrush { get; set; }

        [Browsable(false)]
        public string ReversalBrushSerializable
        {
            get { return Serialize.BrushToString(ReversalBrush); }
            set { ReversalBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "نمایش دنباله‌های ۱–۲ ناتمام", Description = "پیگیری مستقل جفت‌های شماره‌دار پس از توقف هوک؛ U3 فقط با نود تأییدشده", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 28)]
        public bool ShowUnfinished { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ اتصال دنباله ناتمام", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 29)]
        public Brush UnfinishedBrush { get; set; }

        [Browsable(false)]
        public string UnfinishedBrushSerializable
        {
            get { return Serialize.BrushToString(UnfinishedBrush); }
            set { UnfinishedBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "نمایش 123Flag", Description = "مرز فلگ = آخرین نود مخالف بین نود ۲ و ۳؛ شکست با سایه بعد از نود ۳؛ TP1 = قله قبل از فلگ (فقط نمایش، بدون سفارش)", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 30)]
        public bool ShowFlag123 { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ 123Flag", GroupName = "02) نمایش هوک و برچسب‌ها", Order = 31)]
        public Brush Flag123Brush { get; set; }

        [Browsable(false)]
        public string Flag123BrushSerializable
        {
            get { return Serialize.BrushToString(Flag123Brush); }
            set { Flag123Brush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "حالت اشکال‌زدایی (چاپ در Output)", GroupName = "03) اشکال‌زدایی", Order = 0)]
        public bool DebugMode { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private NDSStructure[] cacheNDSStructure;
		public NDSStructure NDSStructure(int swingStrength, int minBarsBetweenNodes, int haConfirmBars, bool useHaOnlyNodes, int tolTicks, int numberTolTicks, double cycleRetracePct, double closeBand, int minEachSide, int spawnLookback, int minAnalysisMinutes, bool showNodes, bool showUnnumberedNodes, bool showProvisionalNode, bool showLabels, bool showMicroLayer, bool showHookT, bool showLevel86, bool showAllHookLevels, bool showStatusLabel, TextPosition statusPosition, int numberFontSize, int labelPixelOffset, int labelTickOffset, int dotTickOffset, double offsetAtrFraction, int maxDrawLookbackBars, bool showTarget161, bool showSymmetry, bool showRallyTarget, bool showNdLevel, bool showTimeSymmetry, bool showReversal, bool showUnfinished, bool showFlag123, bool debugMode)
		{
			return NDSStructure(Input, swingStrength, minBarsBetweenNodes, haConfirmBars, useHaOnlyNodes, tolTicks, numberTolTicks, cycleRetracePct, closeBand, minEachSide, spawnLookback, minAnalysisMinutes, showNodes, showUnnumberedNodes, showProvisionalNode, showLabels, showMicroLayer, showHookT, showLevel86, showAllHookLevels, showStatusLabel, statusPosition, numberFontSize, labelPixelOffset, labelTickOffset, dotTickOffset, offsetAtrFraction, maxDrawLookbackBars, showTarget161, showSymmetry, showRallyTarget, showNdLevel, showTimeSymmetry, showReversal, showUnfinished, showFlag123, debugMode);
		}

		public NDSStructure NDSStructure(ISeries<double> input, int swingStrength, int minBarsBetweenNodes, int haConfirmBars, bool useHaOnlyNodes, int tolTicks, int numberTolTicks, double cycleRetracePct, double closeBand, int minEachSide, int spawnLookback, int minAnalysisMinutes, bool showNodes, bool showUnnumberedNodes, bool showProvisionalNode, bool showLabels, bool showMicroLayer, bool showHookT, bool showLevel86, bool showAllHookLevels, bool showStatusLabel, TextPosition statusPosition, int numberFontSize, int labelPixelOffset, int labelTickOffset, int dotTickOffset, double offsetAtrFraction, int maxDrawLookbackBars, bool showTarget161, bool showSymmetry, bool showRallyTarget, bool showNdLevel, bool showTimeSymmetry, bool showReversal, bool showUnfinished, bool showFlag123, bool debugMode)
		{
			if (cacheNDSStructure != null)
				for (int idx = 0; idx < cacheNDSStructure.Length; idx++)
					if (cacheNDSStructure[idx] != null && cacheNDSStructure[idx].SwingStrength == swingStrength && cacheNDSStructure[idx].MinBarsBetweenNodes == minBarsBetweenNodes && cacheNDSStructure[idx].HaConfirmBars == haConfirmBars && cacheNDSStructure[idx].UseHaOnlyNodes == useHaOnlyNodes && cacheNDSStructure[idx].TolTicks == tolTicks && cacheNDSStructure[idx].NumberTolTicks == numberTolTicks && cacheNDSStructure[idx].CycleRetracePct == cycleRetracePct && cacheNDSStructure[idx].CloseBand == closeBand && cacheNDSStructure[idx].MinEachSide == minEachSide && cacheNDSStructure[idx].SpawnLookback == spawnLookback && cacheNDSStructure[idx].MinAnalysisMinutes == minAnalysisMinutes && cacheNDSStructure[idx].ShowNodes == showNodes && cacheNDSStructure[idx].ShowUnnumberedNodes == showUnnumberedNodes && cacheNDSStructure[idx].ShowProvisionalNode == showProvisionalNode && cacheNDSStructure[idx].ShowLabels == showLabels && cacheNDSStructure[idx].ShowMicroLayer == showMicroLayer && cacheNDSStructure[idx].ShowHookT == showHookT && cacheNDSStructure[idx].ShowLevel86 == showLevel86 && cacheNDSStructure[idx].ShowAllHookLevels == showAllHookLevels && cacheNDSStructure[idx].ShowStatusLabel == showStatusLabel && cacheNDSStructure[idx].StatusPosition == statusPosition && cacheNDSStructure[idx].NumberFontSize == numberFontSize && cacheNDSStructure[idx].LabelPixelOffset == labelPixelOffset && cacheNDSStructure[idx].LabelTickOffset == labelTickOffset && cacheNDSStructure[idx].DotTickOffset == dotTickOffset && cacheNDSStructure[idx].OffsetAtrFraction == offsetAtrFraction && cacheNDSStructure[idx].MaxDrawLookbackBars == maxDrawLookbackBars && cacheNDSStructure[idx].ShowTarget161 == showTarget161 && cacheNDSStructure[idx].ShowSymmetry == showSymmetry && cacheNDSStructure[idx].ShowRallyTarget == showRallyTarget && cacheNDSStructure[idx].ShowNdLevel == showNdLevel && cacheNDSStructure[idx].ShowTimeSymmetry == showTimeSymmetry && cacheNDSStructure[idx].ShowReversal == showReversal && cacheNDSStructure[idx].ShowUnfinished == showUnfinished && cacheNDSStructure[idx].ShowFlag123 == showFlag123 && cacheNDSStructure[idx].DebugMode == debugMode && cacheNDSStructure[idx].EqualsInput(input))
						return cacheNDSStructure[idx];
			return CacheIndicator<NDSStructure>(new NDSStructure(){ SwingStrength = swingStrength, MinBarsBetweenNodes = minBarsBetweenNodes, HaConfirmBars = haConfirmBars, UseHaOnlyNodes = useHaOnlyNodes, TolTicks = tolTicks, NumberTolTicks = numberTolTicks, CycleRetracePct = cycleRetracePct, CloseBand = closeBand, MinEachSide = minEachSide, SpawnLookback = spawnLookback, MinAnalysisMinutes = minAnalysisMinutes, ShowNodes = showNodes, ShowUnnumberedNodes = showUnnumberedNodes, ShowProvisionalNode = showProvisionalNode, ShowLabels = showLabels, ShowMicroLayer = showMicroLayer, ShowHookT = showHookT, ShowLevel86 = showLevel86, ShowAllHookLevels = showAllHookLevels, ShowStatusLabel = showStatusLabel, StatusPosition = statusPosition, NumberFontSize = numberFontSize, LabelPixelOffset = labelPixelOffset, LabelTickOffset = labelTickOffset, DotTickOffset = dotTickOffset, OffsetAtrFraction = offsetAtrFraction, MaxDrawLookbackBars = maxDrawLookbackBars, ShowTarget161 = showTarget161, ShowSymmetry = showSymmetry, ShowRallyTarget = showRallyTarget, ShowNdLevel = showNdLevel, ShowTimeSymmetry = showTimeSymmetry, ShowReversal = showReversal, ShowUnfinished = showUnfinished, ShowFlag123 = showFlag123, DebugMode = debugMode }, input, ref cacheNDSStructure);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.NDSStructure NDSStructure(int swingStrength, int minBarsBetweenNodes, int haConfirmBars, bool useHaOnlyNodes, int tolTicks, int numberTolTicks, double cycleRetracePct, double closeBand, int minEachSide, int spawnLookback, int minAnalysisMinutes, bool showNodes, bool showUnnumberedNodes, bool showProvisionalNode, bool showLabels, bool showMicroLayer, bool showHookT, bool showLevel86, bool showAllHookLevels, bool showStatusLabel, TextPosition statusPosition, int numberFontSize, int labelPixelOffset, int labelTickOffset, int dotTickOffset, double offsetAtrFraction, int maxDrawLookbackBars, bool showTarget161, bool showSymmetry, bool showRallyTarget, bool showNdLevel, bool showTimeSymmetry, bool showReversal, bool showUnfinished, bool showFlag123, bool debugMode)
		{
			return indicator.NDSStructure(Input, swingStrength, minBarsBetweenNodes, haConfirmBars, useHaOnlyNodes, tolTicks, numberTolTicks, cycleRetracePct, closeBand, minEachSide, spawnLookback, minAnalysisMinutes, showNodes, showUnnumberedNodes, showProvisionalNode, showLabels, showMicroLayer, showHookT, showLevel86, showAllHookLevels, showStatusLabel, statusPosition, numberFontSize, labelPixelOffset, labelTickOffset, dotTickOffset, offsetAtrFraction, maxDrawLookbackBars, showTarget161, showSymmetry, showRallyTarget, showNdLevel, showTimeSymmetry, showReversal, showUnfinished, showFlag123, debugMode);
		}

		public Indicators.NDSStructure NDSStructure(ISeries<double> input , int swingStrength, int minBarsBetweenNodes, int haConfirmBars, bool useHaOnlyNodes, int tolTicks, int numberTolTicks, double cycleRetracePct, double closeBand, int minEachSide, int spawnLookback, int minAnalysisMinutes, bool showNodes, bool showUnnumberedNodes, bool showProvisionalNode, bool showLabels, bool showMicroLayer, bool showHookT, bool showLevel86, bool showAllHookLevels, bool showStatusLabel, TextPosition statusPosition, int numberFontSize, int labelPixelOffset, int labelTickOffset, int dotTickOffset, double offsetAtrFraction, int maxDrawLookbackBars, bool showTarget161, bool showSymmetry, bool showRallyTarget, bool showNdLevel, bool showTimeSymmetry, bool showReversal, bool showUnfinished, bool showFlag123, bool debugMode)
		{
			return indicator.NDSStructure(input, swingStrength, minBarsBetweenNodes, haConfirmBars, useHaOnlyNodes, tolTicks, numberTolTicks, cycleRetracePct, closeBand, minEachSide, spawnLookback, minAnalysisMinutes, showNodes, showUnnumberedNodes, showProvisionalNode, showLabels, showMicroLayer, showHookT, showLevel86, showAllHookLevels, showStatusLabel, statusPosition, numberFontSize, labelPixelOffset, labelTickOffset, dotTickOffset, offsetAtrFraction, maxDrawLookbackBars, showTarget161, showSymmetry, showRallyTarget, showNdLevel, showTimeSymmetry, showReversal, showUnfinished, showFlag123, debugMode);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.NDSStructure NDSStructure(int swingStrength, int minBarsBetweenNodes, int haConfirmBars, bool useHaOnlyNodes, int tolTicks, int numberTolTicks, double cycleRetracePct, double closeBand, int minEachSide, int spawnLookback, int minAnalysisMinutes, bool showNodes, bool showUnnumberedNodes, bool showProvisionalNode, bool showLabels, bool showMicroLayer, bool showHookT, bool showLevel86, bool showAllHookLevels, bool showStatusLabel, TextPosition statusPosition, int numberFontSize, int labelPixelOffset, int labelTickOffset, int dotTickOffset, double offsetAtrFraction, int maxDrawLookbackBars, bool showTarget161, bool showSymmetry, bool showRallyTarget, bool showNdLevel, bool showTimeSymmetry, bool showReversal, bool showUnfinished, bool showFlag123, bool debugMode)
		{
			return indicator.NDSStructure(Input, swingStrength, minBarsBetweenNodes, haConfirmBars, useHaOnlyNodes, tolTicks, numberTolTicks, cycleRetracePct, closeBand, minEachSide, spawnLookback, minAnalysisMinutes, showNodes, showUnnumberedNodes, showProvisionalNode, showLabels, showMicroLayer, showHookT, showLevel86, showAllHookLevels, showStatusLabel, statusPosition, numberFontSize, labelPixelOffset, labelTickOffset, dotTickOffset, offsetAtrFraction, maxDrawLookbackBars, showTarget161, showSymmetry, showRallyTarget, showNdLevel, showTimeSymmetry, showReversal, showUnfinished, showFlag123, debugMode);
		}

		public Indicators.NDSStructure NDSStructure(ISeries<double> input , int swingStrength, int minBarsBetweenNodes, int haConfirmBars, bool useHaOnlyNodes, int tolTicks, int numberTolTicks, double cycleRetracePct, double closeBand, int minEachSide, int spawnLookback, int minAnalysisMinutes, bool showNodes, bool showUnnumberedNodes, bool showProvisionalNode, bool showLabels, bool showMicroLayer, bool showHookT, bool showLevel86, bool showAllHookLevels, bool showStatusLabel, TextPosition statusPosition, int numberFontSize, int labelPixelOffset, int labelTickOffset, int dotTickOffset, double offsetAtrFraction, int maxDrawLookbackBars, bool showTarget161, bool showSymmetry, bool showRallyTarget, bool showNdLevel, bool showTimeSymmetry, bool showReversal, bool showUnfinished, bool showFlag123, bool debugMode)
		{
			return indicator.NDSStructure(input, swingStrength, minBarsBetweenNodes, haConfirmBars, useHaOnlyNodes, tolTicks, numberTolTicks, cycleRetracePct, closeBand, minEachSide, spawnLookback, minAnalysisMinutes, showNodes, showUnnumberedNodes, showProvisionalNode, showLabels, showMicroLayer, showHookT, showLevel86, showAllHookLevels, showStatusLabel, statusPosition, numberFontSize, labelPixelOffset, labelTickOffset, dotTickOffset, offsetAtrFraction, maxDrawLookbackBars, showTarget161, showSymmetry, showRallyTarget, showNdLevel, showTimeSymmetry, showReversal, showUnfinished, showFlag123, debugMode);
		}
	}
}

#endregion

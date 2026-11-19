#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.Sockets;
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
    public enum NdsMarkerShape
    {
        Dot,
        TriangleUp,
        TriangleDown,
        Diamond
    }

    public enum NdsNodeColorMode
    {
        HighLow,     // رنگ انتخابی کاربر: سقف = رنگ سقف، کف = رنگ کف
        Numbered     // نود شماره‌دار با رنگ انتخابی، نود بی‌شماره با رنگ زرد
    }

    public enum NdsNodeLabelStyle
    {
        Number,   // 1، 2، 3
        Letter    // N1، N2 برای سقف‌ها و S1، S2 برای کف‌ها (طبق ص14)
    }

    public enum NdsSequenceRule
    {
        AnchoredNoReset,   // رفتار فعلی (پیش‌فرض): نود بی‌شماره (ناقض) سکانس را ریست نمی‌کند؛ لنگر نود شماره‌دار قبلی می‌ماند
        RestartOnFail      // قله/درهٔ ناقض سکانس را از نو شروع می‌کند
    }

    public enum NdsSwingSource
    {
        HighLow,     // ملاک سویینگ: High/Low کندل (پیش‌فرض)
        CloseLine    // ملاک سویینگ: Close کندل
    }

    /// <summary>
    /// NDS.Structure — موتور ساختار NDS روی چارت (بر اساس جزوه‌های NDS Strategy V1.2 و NDS2024)
    ///
    /// پیاده‌سازی شده طبق متن کتاب:
    ///   - نودها (ص14): هر قله‌ای که بالاتر از قله شماره‌دار قبلی تشکیل شود شماره می‌گیرد؛
    ///     قله‌ای که بالاتر از نود شماره‌دار قبلی نباشد، «بدون شماره» می‌ماند (دایره زرد در کتاب — ص68).
    ///   - سکانس (ص67–68): قله1 → دره1 → قله2 → دره2 → قله3 → دره3 و قانون کلیدی قله2>قله1 و قله3>قله2.
    ///   - هوک (ص22–29، ص67): نوع A (هر سه نود تشکیل می‌شوند) و نوع B (نود 3 شکل نمی‌گیرد)؛
    ///     «هوک همواره در شماره 3 بسته می‌شود» و سپس انتظار Reverse از نود 3.
    ///   - سطح 86.4 درصد (ص19، 28، 42، 65): کتاب همه‌جا 86.4٪ نوشته است، نه 86٪.
    ///   - فلگ (ص35–36): موج اصلی → اصلاح تا Z → اسپایک تا F (Low Volume Spike) → ریزش؛ تارگت اول Z حتمی.
    ///   - تقارن (ص53–65): تقارن رالی‌ها R1=R2، تقارن اصلاح‌ها C1≈C2، فرمول B2 = B1×(A2/A1)، و تقارن فاصله نودها.
    ///
    /// توجه: بخش‌های حجمی (Sellers Move In و Low Volume Spike) نیازمند داده حجم/دلتا هستند
    /// و در فاز دلتا/ولیوم‌پروفایل تکمیل می‌شوند.
    /// </summary>
    public class NDSStructure_Fixed : Indicator
    {
        #region Fields
        private const int MaxPivots = 512;
        private const int RedrawNodeCount = 512;   // همهٔ نودها رسم شوند — کپ ۴۰ باعث می‌شد نودهای قدیمی بی‌لیبل بمانند

        private sealed class Pivot    // class نه struct: تا pivots[i].Num مستقیم تغییر کند (CS1612)
        {
            public int Bar;
            public double Price;
            public bool IsHigh;
            public int Num;      // شماره نود (0 = بدون شماره)
            public string Label; // برچسب هوک: N1/S2/N3 … (لایه ۲ — از هوک تطبیقیافته می‌آید، نه شمارنده)
            public int Index;    // جای نود در لیست پیوت‌ها — هنگام اجرای مچر پر می‌شود
            public string DrawnLabel;   // آخرین برچسب رسم‌شده (null = هنوز رسم نشده)
        }

        /// <summary>هوک تطبیقیافتهٔ لایه ۲ — خروجی تابع خالص مچر (پورت hooks.py، معماری v2).</summary>
        private sealed class HookMatch
        {
            public string Direction;      // pos | neg
            public string Kind;           // A | B
            public string Role = "macro"; // macro | micro
            public Pivot T, N1, V1, N2, V2, N3, End;
            public double Level864 = double.NaN;
            public string State = "forming";  // forming | closed
            public int ClosedIndex = -1;
        }

        private struct TagRef { public string Tag; public int Bar; }

        private readonly List<Pivot> pivots = new List<Pivot>(MaxPivots);
        private readonly List<HookMatch> hooks = new List<HookMatch>();   // لایه ۲ — تمام هوک‌های تطبیقیافته
        private readonly Queue<TagRef> trackedTags = new Queue<TagRef>();
        private readonly List<string> annotationTags = new List<string>();

        private NdsStatePublisher publisher;
        private int barsSinceLastSend;
        private SimpleFont numberFont;

        // --- آخرین اسنپ‌شات ساختار ---
        private int numberedPeaksCount;
        private int numberedTroughsCount;
        private int peakSeqPos;
        private int troughSeqPos;
        private string hookDir = "none";     // pos / neg / none
        private string hookType = "none";    // A / B / none
        private bool hookClosed;
        private double level86 = double.NaN;
        private double level86From = double.NaN;
        private double level86To = double.NaN;
        private int level86AnchorBar = -1;   // بار نود پایانی لگ (لنگر رسم خط)
        private bool level86HookBasis;
        private bool level86Broken;
        private double projUpTarget = double.NaN;    // پروجکشن تقارن برای لگ صعودی بعدی
        private double projDownTarget = double.NaN;  // پروجکشن تقارن برای لگ نزولی بعدی
        private bool symUpMatched;
        private bool symDownMatched;
        private bool symCorrMatched;
        private double symR1 = double.NaN;
        private double symR2 = double.NaN;
        private double symC1 = double.NaN;
        private double symC2 = double.NaN;
        private int symBarR1 = -1;
        private int symBarR2 = -1;
        private int symBarC1 = -1;
        private int symBarC2 = -1;
        private double symPriceR1;
        private double symPriceR2;
        private double symPriceC1;
        private double symPriceC2;
        private bool flagActive;
        private double flagZ = double.NaN;
        private double flagF = double.NaN;
        private int flagZBar = -1;
        private int flagFBar = -1;
        private int posHookBar = -1;
        private int negHookBar = -1;
        private string seqText = "-";
        private string lastAnnotationKey = "";
        private int lastNumBar = int.MinValue;
        private double lastNumPrice = double.NaN;
        private int lastNumSide = 1;

        private string tfKeyCached;

        // ── تشخیص نود با سویینگ روی کندل‌های خود چارت (بدون ATR و بدون اندیکاتور نوسان) —
        //    نود تأییدشده هرگز جابه‌جا نمی‌شود؛ اکستریم تأییدنشده فقط نمایشی است.
        private int lastConfirmBar = -1;      // کندل «ثبت» آخرین نود (برای گارد MinBarsBetweenNodes)

        // ── لایه ۲ (معماری v2): مچر هوک خالص روی لیست نودها — شماره‌ها از هوک می‌آیند، نه شمارنده
        private HookMatch activeHook;         // آخرین هوک ماکرو (برای وضعیت/سطح رسم)
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "NDS Structure - نودها، سکانس 1-2-3، هوک A/B، فلگ، سطح 86.4٪ و تقارن";
                Name = "NDSStructure_Fixed";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsAutoScale = false;

                // 00) عمومی — تشخیص نود: سویینگ روی کندل‌های خود چارت (بدون ATR/اندیکاتور نوسان)
                // پیش‌فرض ۲: فاز A روی ES دیلی با S=5 نود T (کف 04-24) را از دست میدهد —
                // سوییچ «تلاش ۲-۳ برای فاز A» مهارت. با S=2 هر ۵ برچسب مرجع بازتولید میشود.
                SwingStrength = 2;
                SwingSource = NdsSwingSource.HighLow;
                NodeBreakToleranceTicks = 2;
                EqualToleranceTicks = 2;
                MinBarsBetweenNodes = 2;
                SequenceRule = NdsSequenceRule.AnchoredNoReset;
                MaxDrawLookbackBars = 1500;
                DebugMode = false;
                ShowStatusLabel = false;

                // 01) نودها
                ShowNodes = true;
                NodeHighBrush = Brushes.DodgerBlue;
                NodeLowBrush = Brushes.OrangeRed;
                UnnumberedNodeBrush = Brushes.Gold;
                NodeColorMode = NdsNodeColorMode.HighLow;
                NodeLabelStyle = NdsNodeLabelStyle.Letter;   // N1/N2/N3 & S1/S2/S3 (gaya huruf) agar maksud N vs S jelas
                NodeLabelTicks = 8;
                ShowUnnumberedLabels = false;
                UnnumberedLabelBrush = Brushes.Gray;
                NodeMarkerShape = NdsMarkerShape.Dot;
                ShowProvisionalNode = false;
                ShowNodeConnections = false;
                NodeLineBrush = Brushes.Gray;
                NodeLineWidth = 1;
                NodeLineDash = DashStyleHelper.Dot;

                // 02) هوک و سکانس
                ShowCountNumbers = true;
                NumberBrush = Brushes.White;
                NumberFontSize = 12;
                ShowHookLabel = false;
                HookLabelBrush = Brushes.Gold;
                HookEqualTolerancePct = 15.0;
                ShowSequenceTrace = false;
                SequenceTraceBrush = Brushes.DeepSkyBlue;
                ShowMicroLayer = false;   // لایهٔ خرد پیش‌فرض خاموش (معماری v2)

                // 03) فلگ
                ShowFlagBox = false;
                FlagBoxOutlineBrush = Brushes.MediumPurple;
                FlagBoxFillBrush = Brushes.MediumPurple;
                FlagZBrush = Brushes.Crimson;
                FlagBoxOpacity = 12;
                FlagMaxBars = 300;

                // 04) سطح 86.4 درصد
                ShowLevel86 = false;
                CycleRetracePct = 86.4;
                Level86IntactBrush = Brushes.SeaGreen;
                Level86BrokenBrush = Brushes.Crimson;
                Level86Width = 1;

                // 05) تقارن
                ShowSymmetry = false;
                SymTolerancePct = 12.0;
                SymLineBrush = Brushes.Cyan;
                SymHitBrush = Brushes.LimeGreen;
                ShowProjections = false;
                ProjectionBrush = Brushes.Violet;

                // 06) جابجایی نمایش
                // جابجایی نودها (در گروه 01)
                HighNodeShiftBars = 0;
                HighNodeYTicks = 0;
                LowNodeShiftBars = 0;
                LowNodeYTicks = 0;
                NumberShiftBars = 0;
                NumberYPixels = 0;
                HookShiftBars = 0;
                HookYPixels = 0;
                LevelShiftBars = 0;
                LevelLabelYPixels = 0;
                FlagShiftBars = 0;
                FlagYTicks = 0;
                ProjShiftBars = 0;
                ProjYTicks = 0;
                ShowSymmetryLabels = false;
                SymLabelShiftBars = 0;
                SymLabelYPixels = 0;
                StatusPosition = TextPosition.BottomLeft;

                // 09) اتصال به موتور تصمیم
                EnableEngineConnection = false;
                EngineHost = "127.0.0.1";
                EnginePort = 46320;
                HeartbeatIntervalBars = 5;
            }
            else if (State == State.DataLoaded)
            {
                numberFont = new SimpleFont("Consolas", NumberFontSize);
                tfKeyCached = null;

                // ریست کامل همهٔ وضعیت‌ها (نودها، کاندیدها، شمارنده‌ها، اسنپ‌شات ساختار)
                pivots.Clear();
                hooks.Clear();
                activeHook = null;
                trackedTags.Clear();
                annotationTags.Clear();
                lastConfirmBar = -1;
                numberedPeaksCount = 0;
                numberedTroughsCount = 0;
                peakSeqPos = 0;
                troughSeqPos = 0;
                hookDir = "none";
                hookType = "none";
                hookClosed = false;
                level86 = double.NaN;       level86From = double.NaN;    level86To = double.NaN;
                level86AnchorBar = -1;      level86HookBasis = false;    level86Broken = false;
                projUpTarget = double.NaN;  projDownTarget = double.NaN;
                symUpMatched = false; symDownMatched = false; symCorrMatched = false;
                symR1 = double.NaN; symR2 = double.NaN; symC1 = double.NaN; symC2 = double.NaN;
                symBarR1 = -1; symBarR2 = -1; symBarC1 = -1; symBarC2 = -1;
                flagActive = false;
                flagZ = double.NaN;  flagF = double.NaN;
                flagZBar = -1;       flagFBar = -1;
                posHookBar = -1;     negHookBar = -1;
                seqText = "-";
                lastAnnotationKey = "";
                lastNumBar = int.MinValue;
                lastNumPrice = double.NaN;
                lastNumSide = 1;
                barsSinceLastSend = 0;

                publisher = new NdsStatePublisher(EngineHost, EnginePort);
            }
            else if (State == State.Terminated)
            {
                if (publisher != null)
                {
                    publisher.Dispose();
                    publisher = null;
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            // تا پنجرهٔ سویینگ کامل بسته نشده (Strength کندل در هر طرف) هیچ نودی بررسی نمی‌شود
            if (CurrentBar < 2 * SwingStrength)
                return;

            CleanupOldObjects();

            // تشخیص نود با سویینگ روی کندل‌های خود چارت (بدون نگاه به آینده)
            StepNodeDetection();

            // ایراد ۱: بسته‌شدن هوک باید «هر کندل» ارزیابی شود (نه فقط وقتی نود جدید ثبت می‌شود) —
            // وگرنه رسیدن قیمت به سطح ۸۶.۴٪ بین دو نود از دست می‌رود.
            if ((hookDir == "pos" || hookDir == "neg") && !double.IsNaN(level86))
            {
                double band = 0.25 * AverageRange(14);
                bool wasClosed = hookClosed;
                hookClosed = hookDir == "pos"
                    ? (Close[0] >= level86 - band)
                    : (Close[0] <= level86 + band);

                // وضعیت بسته‌شدن عوض شد — خط سطح (رنگ سالم/شکسته) و برچسب وضعیت تازه شوند
                if (wasClosed != hookClosed)
                {
                    UpdateEvolvingDrawings();
                    DrawStatusLabel();
                }
            }
        }

        /// <summary>میانگین دامنه کندل‌ها — فقط در فاز هوک (باند بسته‌شدن) استفاده می‌شود؛ فاز نود از آن بی‌بهره است.</summary>
        private double AverageRange(int bars)
        {
            int cnt = Math.Min(bars, CurrentBar);
            if (cnt <= 0)
                return 0;
            double sum = 0;
            for (int i = 0; i < cnt; i++)
                sum += High[i] - Low[i];
            return sum / cnt;
        }

        /// <summary>
        /// گارد فاصلهٔ حداقلی بین «ثبت» دو نود متوالی (فاصلهٔ کندل تأیید نود قبلی تا کندل جاری).
        /// صفر = غیرفعال. برخلاف فاصلهٔ اکستریم‌ها، کندل تأیید همیشه رو به جلو رشد می‌کند و
        /// این گارد هرگز ماشین حالت را قفل نمی‌کند؛ فاصلهٔ اکستریم‌ها نیز همیشه ≥ ۱ و
        /// یکی‌درمیانی قله/دره حفظ می‌شود.
        /// </summary>
        private bool MinBarsSatisfied()
        {
            if (MinBarsBetweenNodes <= 0 || lastConfirmBar < 0)
                return true;
            return CurrentBar - lastConfirmBar >= MinBarsBetweenNodes;
        }

        /// <summary>
        /// گام تشخیص نود با سویینگ روی کندل‌های خود چارت — بدون ATR و بدون هر اندیکاتور نوسان:
        ///
        ///   قله = کندلی که High آن از «Strength» کندل در هر طرف بالاتر باشد
        ///   (دره = Low آن از Strength کندل در هر طرف پایین‌تر باشد).
        ///   با SwingSource.CloseLine ملاک به‌جای High/Low، Close می‌شود.
        ///   کندل کاندید فقط وقتی بررسی می‌شود که Strength کندلِ کامل پس از آن بسته شده باشد
        ///   (بدون نگاه به آینده)؛ نود تأییدشده هرگز جابه‌جا نمی‌شود.
        ///   اگر دو نود هم‌نوع پشت‌سرهم ثبت شوند، اکستریم‌تر در زنجیره می‌ماند (ادغام در AppendNode).
        /// </summary>
        private void StepNodeDetection()
        {
            int s = SwingStrength;
            if (s < 1 || CurrentBar < 2 * s)
                return;

            int centerAgo = s;                        // کندل کاندید: دقیقاً Strength کندل قبل
            bool useHL = SwingSource == NdsSwingSource.HighLow;

            double cH = useHL ? High[centerAgo] : Close[centerAgo];
            double cL = useHL ? Low[centerAgo] : Close[centerAgo];

            bool swingHigh = true;
            bool swingLow = true;

            // کندلِ کاندید در ایندکس centerAgo (= s) است؛
            //   طرف راست (جدیدتر) = ایندکس‌های 0..s-1 ، طرف چپ (قدیمی‌تر) = ایندکس‌های s+1..2s
            // مهم: حلقه نباید به ایندکس s برسد وگرنه High[s]>=High[s] همیشه صادق است
            // و هیچ قله/دره‌ای هرگز تأیید نمیشود (چارت کاملاً خالی میماند).
            // تساوی طبق بخش ۱۰: سمت جدیدتر با >= رد میکند (آخرینِ سقفهای برابر برنده است)، سمت قدیمیتر با > .
            for (int i = 0; i < s && (swingHigh || swingLow); i++)
            {
                double hiR = useHL ? High[i] : Close[i];                                  // طرف راست (جدیدتر)
                double hiL = useHL ? High[centerAgo + i + 1] : Close[centerAgo + i + 1];  // طرف چپ (قدیمیتر)
                double loR = useHL ? Low[i] : Close[i];
                double loL = useHL ? Low[centerAgo + i + 1] : Close[centerAgo + i + 1];

                if (swingHigh && (hiR >= cH || hiL > cH))
                    swingHigh = false;
                if (swingLow && (loR <= cL || loL < cL))
                    swingLow = false;
            }

            if (swingHigh && MinBarsSatisfied())
                AppendNode(CurrentBar - centerAgo, cH, true);
            if (swingLow && MinBarsSatisfied())
                AppendNode(CurrentBar - centerAgo, cL, false);

            DrawProvisionalNode();
        }

        /// <summary>ثبت نود تثبیت‌شده: افزودن به زنجیره + ادغام متناوب + شماره‌گذاری + رسم.</summary>
        private void AppendNode(int bar, double price, bool isHigh)
        {
            int n = pivots.Count;
            lastConfirmBar = CurrentBar;   // کندل ثبت این نود — مبنای گارد MinBarsBetweenNodes

            // ادغام هم‌نوع — دو نود هم‌نوع پشت‌سرهم (با سویینگ ممکن است): اکستریم‌تر می‌ماند
            if (n > 0 && pivots[n - 1].IsHigh == isHigh)
            {
                bool moreExtreme = isHigh ? (price > pivots[n - 1].Price) : (price < pivots[n - 1].Price);
                if (moreExtreme)
                {
                    RemovePivotDrawings(pivots[n - 1]);
                    pivots.RemoveAt(n - 1);
                    n--;
                }
                else
                {
                    return;     // اکستریم‌ترِ قدیمی می‌ماند
                }
            }

            Pivot np = new Pivot();
            np.Bar = bar;
            // قیمت نود دقیقاً High[ago]/Low[ago] است؛ با RoundToTickSize گرد می‌شود (برای داده تیک‌جاافتاده عمل هویتی است)
            np.Price = Instrument.MasterInstrument.RoundToTickSize(price);
            np.IsHigh = isHigh;
            np.Num = 0;
            pivots.Add(np);
            if (pivots.Count > MaxPivots)
                pivots.RemoveAt(0);

            // نود تأیید شد؛ نشانه‌های کم‌رنگ کاندید پاک می‌شوند (نود تأییدشده هرگز جابه‌جا نمی‌شود)
            RemoveDrawObject("nds_provH");
            RemoveDrawObject("nds_provL");

            if (ShowNodeConnections && pivots.Count >= 2)
                DrawSegment(pivots[pivots.Count - 2], np);

            NumberNodesV2();        // لایه ۲ — مچر هوک روی لیست نودها (معماری v2، تابع خالص)
            RedrawNodes();
            DetectStructure();
            UpdateEvolvingDrawings();
            DrawStatusLabel();

            if (DebugMode)
            {
                // یک خط به‌ازای هر نود: NDS|زمان|H یا L|قیمت|شماره|برچسبِ هوک (۰ و - = بی‌شماره/ناقض)
                Pivot last = pivots[pivots.Count - 1];
                Print(string.Format(CultureInfo.InvariantCulture,
                    "NDS|{0:yyyy-MM-dd HH:mm}|{1}|{2}|{3}|{4}",
                    Time[Math.Max(0, CurrentBar - last.Bar)],   // زمانِ کندلِ نود
                    last.IsHigh ? "H" : "L",
                    last.Price.ToString(CultureInfo.InvariantCulture),
                    last.Num,
                    last.Label ?? "-"));
            }

            if (EnableEngineConnection && publisher != null)
                publisher.Send(BuildStateJson("pivot"));
        }

        #region Pivot chain & node numbering
        // (RegisterPivot حذف شد — مسیر ثبت نود فقط AppendNode از ماشین حالت لگ است.)
        // (شمارندهٔ غلتان قدیمی حذف شد — شماره‌گذاری اکنون از مچر هوک لایه ۲ می‌آید:
        //  NumberNodesV2 → MatchHooks → TryPos/TryNeg → NumberedChain. لایه ۲ تابعی خالص است.)

        // ── لایه ۲ (معماری v2 — پورت hooks.py) ─────────────────────────────────────────
        // شماره‌گذاری تابع خالصِ مچر هوک است: شماره‌های ۱ تا ۳ از زنجیرهٔ هوک تطبیقیافته
        // می‌آیند، نه از شمارندهٔ غلتان. قانون شماره (ص14): نخستین قله پس از T = 1؛
        // قلهٔ بالاتر از آن = 2؛ قلهٔ بالاتر از آن = 3؛ قله‌های پایین‌ترِ بین‌راه بدون شماره.
        // دره‌ها آینه‌ای. شروع هوک T: نودی که داخل هیچ هوکِ موجود نیست، یا V2 یک هوک موجود
        // (قانون لایهٔ دوم مالک). پس از نود ۳ هوک نوع A، هوک جدید شروع نمی‌شود (فاز رالی/فلگ).

        private void NumberNodesV2()
        {
            foreach (Pivot p in pivots) { p.Num = 0; p.Label = null; }

            MatchHooks();

            foreach (HookMatch h in hooks)
            {
                bool micro = h.Role == "micro";
                if (micro && !ShowMicroLayer)
                    continue;   // لایهٔ خرد پیش‌فرض خاموش — نودهایش بی‌شماره می‌مانند

                bool tIsLow = !h.T.IsHigh;              // هوک مثبت: T کف است
                string pfxHigh = tIsLow ? "N" : "S";    // مثبت: قله‌ها N و دره‌ها S — منفی برعکس
                string pfxLow  = tIsLow ? "S" : "N";
                AssignNodeLabel(h.N1, "1", pfxHigh, pfxLow, micro);
                AssignNodeLabel(h.V1, "1", pfxHigh, pfxLow, micro);
                AssignNodeLabel(h.N2, "2", pfxHigh, pfxLow, micro);
                AssignNodeLabel(h.V2, "2", pfxHigh, pfxLow, micro);
                AssignNodeLabel(h.N3, "3", pfxHigh, pfxLow, micro);
            }

            // ── اسنپ‌شات وضعیت برای برچسب وضعیت و JSON ──
            activeHook = null;
            for (int i = hooks.Count - 1; i >= 0; i--)
                if (hooks[i].Role == "macro") { activeHook = hooks[i]; break; }

            peakSeqPos = 0; troughSeqPos = 0;
            int np = 0, nt = 0;
            for (int i = pivots.Count - 1; i >= 0; i--)
            {
                Pivot p = pivots[i];
                if (p.Num <= 0)
                    continue;
                if (p.IsHigh) { np++; if (peakSeqPos == 0) peakSeqPos = p.Num; }
                else          { nt++; if (troughSeqPos == 0) troughSeqPos = p.Num; }
            }
            numberedPeaksCount = np;
            numberedTroughsCount = nt;
        }

        /// <summary>برچسب را فقط وقتی روی نود می‌نشاند که قبلاً نشانده نشده باشد — هوکِ بزرگ‌تر مالک برچسب است (تقدم ماکرو بر خرد).</summary>
        private void AssignNodeLabel(Pivot node, string num, string pfxHigh, string pfxLow, bool micro)
        {
            if (node == null)
                return;
            if (node.Num > 0)
                return;                     // مالک قبلی (ماکرو یا هوک زودتر) برچسب را گرفته
            string prefix = node.IsHigh ? pfxHigh : pfxLow;
            node.Num = num[0] - '0';
            node.Label = prefix + num;
        }

        /// <summary>
        /// تابع خالص مچر (پورت match_hooks): نودها را از قدیم به جدید اسکن می‌کند؛
        /// T وقتی پذیرفته می‌شود که خارج از همهٔ هوک‌های موجود باشد یا V2 (درهٔ ۲) یک هوک باشد.
        /// پس از نود ۳ یک هوک نوع A، هیچ هوک جدیدی شروع نمی‌شود.
        /// </summary>
        private void MatchHooks()
        {
            hooks.Clear();
            for (int i = 0; i < pivots.Count; i++)
                pivots[i].Index = i;

            int n3Barrier = -1;             // ایندکس نود ۳ آخرین هوک نوع A تکمیل‌شده
            for (int ti = 0; ti < pivots.Count; ti++)
            {
                Pivot t = pivots[ti];
                if (n3Barrier >= 0 && t.Index > n3Barrier)
                    continue;               // فاز پس از نود ۳: شماره‌گذاری متوقف است (فاز رالی/فلگ)

                string role = "macro";
                foreach (HookMatch h in hooks)
                {
                    int spanHi = HookSpanHi(h);
                    if (HookNumberedSet(h).Contains(t.Index))
                    {
                        // عضو شماره‌دار یک هوک موجود: فقط V2 (دره/قلهٔ ۲) آن می‌تواند هوکِ خرد (لایهٔ دوم) شروع کند
                        role = (h.V2 != null && t.Index == h.V2.Index) ? "micro" : null;
                        break;
                    }
                    if (h.T.Index < t.Index && t.Index < spanHi)
                    {
                        // سویینگ داخلی یک هوک بزرگ‌تر → لایهٔ خرد، نه هوک ماکرو
                        role = "micro";
                        break;
                    }
                }
                if (role == null)
                    continue;

                HookMatch m = t.IsHigh ? TryNeg(t, ti) : TryPos(t, ti);
                if (m == null)
                    continue;
                m.Role = role;
                hooks.Add(m);
                if (m.Kind == "A" && m.N3 != null)
                    n3Barrier = m.N3.Index;
            }
        }

        private static string MarkerTag(Pivot p)
        {
            return (p.IsHigh ? "nds_H_" : "nds_L_") + p.Bar.ToString();
        }

        private static string NumTag(int bar)
        {
            return "nds_num_" + bar.ToString();
        }

        private static int HookSpanHi(HookMatch h)
        {
            int hi = -1;
            Pivot[] ps = { h.N1, h.V1, h.N2, h.V2, h.N3 };
            for (int k = 0; k < ps.Length; k++)
                if (ps[k] != null && ps[k].Index > hi)
                    hi = ps[k].Index;
            return hi;
        }

        private static HashSet<int> HookNumberedSet(HookMatch h)
        {
            HashSet<int> s = new HashSet<int>();
            Pivot[] ps = { h.T, h.N1, h.V1, h.N2, h.V2, h.N3 };
            for (int k = 0; k < ps.Length; k++)
                if (ps[k] != null)
                    s.Add(ps[k].Index);
            return s;
        }

        private sealed class ChainT { public Pivot N1, V1, N2, V2, N3; }

        /// <summary>قانون زنجیرهٔ شماره‌شده (ص14): نخستین نود هم‌جهت پس از T = 1؛ نودِ بهتر از آن = 2؛
        /// نودِ بهتر از آن = 3. نودهای بدتر بدون شماره رد می‌شوند. طرف مخالف (شمارنده) آینه‌ای.</summary>
        private ChainT NumberedChain(int fromIdx, bool impulseHigh)
        {
            ChainT c = new ChainT();
            for (int i = fromIdx; i < pivots.Count; i++)
            {
                Pivot p = pivots[i];
                if (p.IsHigh == impulseHigh)
                {
                    if (c.N1 == null)
                        c.N1 = p;
                    else if (c.N2 == null)
                    {
                        if (impulseHigh ? p.Price > c.N1.Price : p.Price < c.N1.Price)
                            c.N2 = p;
                    }
                    else if (c.N3 == null)
                    {
                        if (impulseHigh ? p.Price > c.N2.Price : p.Price < c.N2.Price)
                            c.N3 = p;
                    }
                }
                else if (c.N1 != null)
                {
                    if (c.V1 == null)
                        c.V1 = p;
                    else if (c.V2 == null)
                    {
                        if (impulseHigh ? p.Price < c.V1.Price : p.Price > c.V1.Price)
                            c.V2 = p;
                    }
                }
            }
            return c;
        }

        /// <summary>هوک مثبت: T کف است؛ نودهای هم‌جهت قله‌اند. سطح ۸۶.۴٪ = P3 − pct×(P3−T) (ص22/28).</summary>
        private HookMatch TryPos(Pivot t, int ti)
        {
            ChainT c = NumberedChain(ti + 1, impulseHigh: true);
            bool isA = c.N3 != null;
            if (c.N1 == null || c.N2 == null)
                return null;
            string kind = isA ? "A" : "B";
            Pivot endPeak = isA ? c.N3 : c.N2;
            double span = endPeak.Price - t.Price;
            if (span <= 0)
                return null;
            double level = endPeak.Price - (CycleRetracePct / 100.0) * span;

            HookMatch m = new HookMatch();
            m.Direction = "pos"; m.Kind = kind; m.T = t;
            m.N1 = c.N1; m.V1 = c.V1; m.N2 = c.N2; m.V2 = c.V2; m.N3 = c.N3;
            m.Level864 = level;
            if (isA)
            {
                // نود بستن = نخستین کف پس از P3 (ریزش از نود ۳)
                Pivot v3 = null;
                for (int i = ti + 1; i < pivots.Count; i++)
                    if (!pivots[i].IsHigh && pivots[i].Index > c.N3.Index) { v3 = pivots[i]; break; }
                m.End = v3 != null ? v3 : c.N3;
            }
            ApplyClosing(m);
            return m;
        }

        /// <summary>هوک منفی: T قله است؛ نودهای هم‌جهت دره‌اند (نزولی). سطح = V3 + pct×(T−V3) (آینه).</summary>
        private HookMatch TryNeg(Pivot t, int ti)
        {
            ChainT c = NumberedChain(ti + 1, impulseHigh: false);
            bool isA = c.N3 != null;
            if (c.N1 == null || c.N2 == null)
                return null;
            string kind = isA ? "A" : "B";
            Pivot endValley = isA ? c.N3 : c.N2;
            double span = t.Price - endValley.Price;
            if (span <= 0)
                return null;
            double level = endValley.Price + (CycleRetracePct / 100.0) * span;

            HookMatch m = new HookMatch();
            m.Direction = "neg"; m.Kind = kind; m.T = t;
            m.N1 = c.N1; m.V1 = c.V1; m.N2 = c.N2; m.V2 = c.V2; m.N3 = c.N3;
            m.Level864 = level;
            if (isA)
            {
                // نود بستن = نخستین قلهٔ پس از V3 (بازگشت از نود ۳)
                Pivot p3 = null;
                for (int i = ti + 1; i < pivots.Count; i++)
                    if (pivots[i].IsHigh && pivots[i].Index > c.N3.Index) { p3 = pivots[i]; break; }
                m.End = p3 != null ? p3 : c.N3;
            }
            ApplyClosing(m);
            return m;
        }

        /// <summary>بستن هوک نوع A: نود بستن اگر در باند تلورانس (۲۵٪ دامنهٔ میانگین ۱۴) نزدیک سطح ۸۶.۴٪ باشد.</summary>
        private void ApplyClosing(HookMatch m)
        {
            if (m.Kind != "A" || m.End == null)
                return;
            double band = 0.25 * AverageRangeAt(m.End.Bar, 14);
            if (Math.Abs(m.End.Price - m.Level864) <= band)
            {
                m.State = "closed";
                m.ClosedIndex = m.End.Index;
            }
        }

        /// <summary>میانگین دامنه (High−Low) روی نوارهای پیش از نود داده‌شده — بدون نگاه به آینده.</summary>
        private double AverageRangeAt(int bar, int len)
        {
            int cnt = Math.Min(len, bar + 1);
            if (cnt <= 0)
                return 0;
            double sum = 0;
            int ago = CurrentBar - bar;
            for (int k = 0; k < cnt; k++)
            {
                int a = ago - k;
                if (a < 0 || a > CurrentBar)
                    break;
                sum += High[a] - Low[a];
            }
            return sum / cnt;
        }

        private static string LineTag(int bar)
        {
            return "nds_line_" + bar.ToString();
        }

        private void RemovePivotDrawings(Pivot p)
        {
            RemoveDrawObject(MarkerTag(p));
            RemoveDrawObject(LineTag(p.Bar));
            RemoveDrawObject(NumTag(p.Bar));
        }

        /// <summary>
        /// رسم نودها — فقط نودهای جدید یا تغییرکرده (DrawnNum != Num)، نه همه.
        /// نود شماره‌دار با رنگ اصلی + شماره، نود بی‌شماره با رنگ زرد و بدون شماره.
        /// علامت نود بدون جابه‌جایی روی دقیقاً Time[ago] و قیمت گردشدهٔ High/Low می‌نشیند؛
        /// فقط برچسب متنی با تیک/پیکسل جابه‌جا می‌شود.
        /// </summary>
        private void RedrawNodes()
        {
            int from = Math.Max(0, pivots.Count - RedrawNodeCount);
            for (int i = from; i < pivots.Count; i++)
            {
                Pivot p = pivots[i];
                int barsAgo = CurrentBar - p.Bar;
                if (barsAgo > MaxDrawLookbackBars || barsAgo < 0)
                    continue;

                string drawnKey = p.Num.ToString(CultureInfo.InvariantCulture) + "|" + (p.Label ?? "");
                if (p.DrawnLabel == drawnKey)
                    continue;   // نود جدید نیست و برچسبش هم تغییر نکرده — رسم مجدد نمی‌شود

                string tag = MarkerTag(p);
                RemoveDrawObject(tag);
                // نود بی‌شماره (قله/درهٔ ناقض، ص68) همیشه زرد — مستقل از NodeColorMode
                Brush b;
                if (p.Num <= 0)
                    b = UnnumberedNodeBrush;
                else
                    b = p.IsHigh ? NodeHighBrush : NodeLowBrush;
                if (ShowNodes)
                {
                    // علامت بدون جابه‌جایی: دقیقاً زمان کندل اکستریم و قیمت گردشدهٔ نود
                    DrawMarker(tag, NodeTime(p.Bar, 0), p.Price, b);
                    TrackTag(tag, p.Bar);
                }

                string numTag = NumTag(p.Bar);
                RemoveDrawObject(numTag);
                if (!ShowCountNumbers)
                {
                    p.DrawnLabel = drawnKey;
                    continue;
                }

                string label;
                Brush labelBrush = NumberBrush;
                if (p.Num > 0)
                {
                    // برچسب از مچر هوک لایه ۲ می‌آید (N1/S2/… با جهت هوک)؛ سبک عددی فقط رقم را می‌نویسد
                    label = NodeLabelStyle == NdsNodeLabelStyle.Letter
                        ? (p.Label ?? ((p.IsHigh ? "N" : "S") + p.Num.ToString(CultureInfo.InvariantCulture)))
                        : p.Num.ToString(CultureInfo.InvariantCulture);
                }
                else if (ShowUnnumberedLabels)
                {
                    // نود بیشماره (ص68): برچسب خاکستری فقط با حرف، بدون شماره
                    label = p.IsHigh ? "N" : "S";
                    labelBrush = UnnumberedLabelBrush;
                }
                else
                {
                    // نود بی‌شماره (ص68): فقط دایرهٔ زرد، بدون هیچ نوشته‌ای
                    p.DrawnLabel = drawnKey;
                    continue;
                }

                // ضدبرخورد: اگر برچسب قبلی خیلی نزدیک بود، سمت افقی عوض و ارتفاع اضافه شود
                bool crowded = !double.IsNaN(lastNumPrice) &&
                               Math.Abs(p.Bar - lastNumBar) <= 2 &&
                               Math.Abs(p.Price - lastNumPrice) <= 12 * TickSize;
                if (crowded)
                    lastNumSide = -lastNumSide;
                else
                    lastNumSide = 1;

                int barsShift = NumberShiftBars + (crowded ? lastNumSide * 2 : 0);

                // نوشته سقف همیشه «بالای» نود و نوشته کف همیشه «پایین» نود.
                // فقط برچسب با تیک (فضای قیمت) + پیکسل (فضای صفحه؛ مثبت = بالا طبق سورس NT: y -= YPixelOffset) جابه‌جا می‌شود
                int dir = p.IsHigh ? 1 : -1;
                int labelTicks = NodeLabelTicks + (crowded ? NodeLabelTicks : 0);
                double y = p.Price + dir * labelTicks * TickSize;
                int pixel = dir * 14 + (crowded ? dir * 12 : 0) + NumberYPixels;

                Draw.Text(this, numTag, false, label,
                    NodeTime(p.Bar, barsShift), y, pixel, labelBrush, numberFont,
                    System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                TrackTag(numTag, p.Bar);
                lastNumBar = p.Bar;
                lastNumPrice = p.Price;

                p.DrawnLabel = drawnKey;
            }
        }

        #region Positioning helpers
        /// <summary>زمان یک نود با جابجایی افقی اختیاری (مثبت = به سمت راست/آینده).</summary>
        private DateTime NodeTime(int bar, int shiftBars)
        {
            int barsAgo = CurrentBar - bar;
            if (barsAgo < 0) barsAgo = 0;
            DateTime t = Time[barsAgo];
            if (shiftBars == 0 || CurrentBar < 1)
                return t;
            long span = (Time[0] - Time[1]).Ticks;
            return t.AddTicks(span * shiftBars);
        }

        /// <summary>جابجایی افقی نود بر اساس نوع آن (سقف/کف).</summary>
        private int ShiftBarsFor(bool isHigh)
        {
            return isHigh ? HighNodeShiftBars : LowNodeShiftBars;
        }

        /// <summary>جابجایی عمودی نود بر اساس نوع آن (سقف/کف).</summary>
        private double ShiftTicksFor(bool isHigh)
        {
            return isHigh ? HighNodeYTicks : LowNodeYTicks;
        }

        /// <summary>قیمت یک نود با جابجایی عمودی مخصوص سقف یا کف.</summary>
        private double NodePrice(double price, bool isHigh)
        {
            return price + ShiftTicksFor(isHigh) * TickSize;
        }

        /// <summary>جابجایی عمودی دلخواه بر حسب تیک.</summary>
        private double PriceShift(double price, double ticks)
        {
            return price + ticks * TickSize;
        }
        #endregion

        private void DrawMarker(string tag, DateTime time, double price, Brush brush)
        {
            switch (NodeMarkerShape)
            {
                case NdsMarkerShape.TriangleUp:   Draw.TriangleUp(this, tag, false, time, price, brush); break;
                case NdsMarkerShape.TriangleDown: Draw.TriangleDown(this, tag, false, time, price, brush); break;
                case NdsMarkerShape.Diamond:      Draw.Diamond(this, tag, false, time, price, brush); break;
                default:                          Draw.Dot(this, tag, false, time, price, brush); break;
            }
        }

        /// <summary>
        /// نشانهٔ کم‌رنگ اکستریم تأییدنشده — ShowProvisionalNode.
        /// تنها کندل‌های پنجرهٔ راست سویینگ (Strength کندلِ آخر) هنوز می‌توانند نود شوند؛
        /// اکستریم همین پنجره با شفافیت ۳۰٪ و جدا از نودهای تأییدشده رسم می‌شود.
        /// </summary>
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

            // نشانهٔ کم‌رنگ: API نشانگرهای چارت پارامتر شفافیت ندارد؛ پس از براش نیمه‌شفاف (۳۰٪) استفاده می‌کنیم
            DrawProvisionalMark("nds_provH", CurrentBar - hiAgo, High[hiAgo], NodeHighBrush);
            DrawProvisionalMark("nds_provL", CurrentBar - loAgo, Low[loAgo], NodeLowBrush);
        }

        private void DrawProvisionalMark(string tag, int bar, double price, Brush baseBrush)
        {
            if (CurrentBar - bar > MaxDrawLookbackBars)
                return;
            Brush b = baseBrush.Clone();
            b.Opacity = 0.3;
            b.Freeze();
            DrawMarker(tag, NodeTime(bar, 0), price, b);
        }

        private void DrawSegment(Pivot a, Pivot b)
        {
            if (CurrentBar - a.Bar > MaxDrawLookbackBars)
                return;
            string tag = LineTag(a.Bar);
            // خط اتصال بین نقاط دقیق نودها (بدون جابه‌جایی علامت‌ها)
            Draw.Line(this, tag, false,
                NodeTime(a.Bar, 0), a.Price,
                NodeTime(b.Bar, 0), b.Price,
                NodeLineBrush, NodeLineDash, Math.Max(1, NodeLineWidth));
            TrackTag(tag, a.Bar);
        }

        private void TrackTag(string tag, int bar)
        {
            TagRef r = new TagRef();
            r.Tag = tag;
            r.Bar = bar;
            trackedTags.Enqueue(r);
        }

        private void CleanupOldObjects()
        {
            while (trackedTags.Count > 0 && CurrentBar - trackedTags.Peek().Bar > MaxDrawLookbackBars)
                RemoveDrawObject(trackedTags.Dequeue().Tag);
        }

        private void ClearAnnotations()
        {
            for (int i = 0; i < annotationTags.Count; i++)
                RemoveDrawObject(annotationTags[i]);
            annotationTags.Clear();
        }
        #endregion

        #region Structure detection
        private List<Pivot> NumberedHighs(int max)
        {
            List<Pivot> list = new List<Pivot>();
            for (int i = pivots.Count - 1; i >= 0 && list.Count < max; i--)
                if (pivots[i].IsHigh && pivots[i].Num > 0)
                    list.Add(pivots[i]);
            list.Reverse();
            return list;
        }

        private List<Pivot> NumberedLows(int max)
        {
            List<Pivot> list = new List<Pivot>();
            for (int i = pivots.Count - 1; i >= 0 && list.Count < max; i--)
                if (!pivots[i].IsHigh && pivots[i].Num > 0)
                    list.Add(pivots[i]);
            list.Reverse();
            return list;
        }

        /// <summary>
        /// آخرین نودهای یک نوع، بدون توجه به شماره‌دار بودن — برای وقتی که
        /// شماره‌گذاری سخت‌گیرانه (طبق ص68) تعداد کافی نود شماره‌دار تولید نکرده باشد.
        /// </summary>
        private List<Pivot> LastPivots(bool isHigh, int max)
        {
            List<Pivot> list = new List<Pivot>();
            for (int i = pivots.Count - 1; i >= 0 && list.Count < max; i--)
                if (pivots[i].IsHigh == isHigh)
                    list.Add(pivots[i]);
            list.Reverse();
            return list;
        }

        /// <summary>
        /// تشخیص هوک، سکانس، سطح 86.4٪، فلگ و پروجکشن‌های تقارن — همه طبق متن کتاب.
        /// </summary>
        private void DetectStructure()
        {
            hookDir = "none";
            hookType = "none";
            hookClosed = false;
            level86 = double.NaN;
            level86From = double.NaN;
            level86To = double.NaN;
            level86AnchorBar = -1;
            level86HookBasis = false;
            level86Broken = false;
            projUpTarget = double.NaN;
            projDownTarget = double.NaN;
            symUpMatched = false;
            symDownMatched = false;
            symCorrMatched = false;
            symR1 = double.NaN;
            symR2 = double.NaN;
            symC1 = double.NaN;
            symC2 = double.NaN;
            symBarR1 = -1;
            symBarR2 = -1;
            symBarC1 = -1;
            symBarC2 = -1;
            flagActive = false;
            flagZ = double.NaN;
            flagF = double.NaN;
            flagZBar = -1;
            flagFBar = -1;
            seqText = "-";

            List<Pivot> highs = NumberedHighs(4);
            List<Pivot> lows = NumberedLows(4);
            // اگر نودهای شماره‌دار کافی نبودند، به همه نودها برمی‌گردیم تا ماژول‌ها زنده بمانند
            if (highs.Count < 2) highs = LastPivots(true, 4);
            if (lows.Count < 2) lows = LastPivots(false, 4);
            if (highs.Count < 2 || lows.Count < 2)
                return;

            // --- سکانس (ص67-68): ترتیب نودها و قانون قله2>قله1 و قله3>قله2 ---
            BuildSequenceText(highs, lows);

            // --- هوک از مچر لایه ۲ (معماری v2) — هوک فعال = آخرین هوک ماکرو تطبیقیافته ---
            // جهت/نوع/سطح/بسته‌شدن هوک همه از خروجی تابع خالص مچر می‌آیند (پورت hooks.py)؛
            // تشخیص دستی «آخرین سه نود شماره‌دار» حذف شد (معماری قدیمی — شمارندهٔ غلتان).
            if (activeHook != null)
            {
                hookDir  = activeHook.Direction;
                hookType = activeHook.Kind;

                // سطح 86.4٪ (ص22/28/29): از نقطه T (شروع هوک) تا نود پایانی — نود ۳ در نوع A، نود ۲ در نوع B
                Pivot endNode = activeHook.Kind == "A"
                    ? (activeHook.N3 ?? activeHook.N2)
                    : activeHook.N2;
                double spanHook = Math.Abs(endNode.Price - activeHook.T.Price);
                if (spanHook > 0 && !double.IsNaN(activeHook.Level864))
                {
                    level86 = activeHook.Level864;
                    level86From = activeHook.T.Price;
                    level86To = endNode.Price;
                    level86AnchorBar = endNode.Bar;
                    level86HookBasis = true;
                    level86Broken = hookDir == "pos" ? (Close[0] < level86) : (Close[0] > level86);
                }
                if (activeHook.State == "closed")
                    hookClosed = true;
            }

            // --- بسته‌شدن هوک (ص28): ارزیابی «بعد از» محاسبهٔ سطح — با باند تلورانس (FAQ: سطح دقیق نیست) ---
            // هوک مثبت = شمارش نزولی دره‌ها؛ close که به سطح ۸۶.۴٪ برسد هوک بسته است.
            // هوک منفی = شمارش صعودی قله‌ها؛ close که به سطح ۸۶.۴٪ برسد هوک بسته است.
            // باند = ۲۵٪ میانگین دامنهٔ ۱۴ کندل (سطح انتظار است نه قانون دقیق — بخش ۵).
            if ((hookDir == "pos" || hookDir == "neg") && !double.IsNaN(level86))
            {
                double band = 0.25 * AverageRange(14);
                hookClosed = hookDir == "pos"
                    ? (Close[0] >= level86 - band)
                    : (Close[0] <= level86 + band);
            }

            // --- تقارن: لگ‌های متوالی زنجیره واقعی نودها (ص55، 56، 62) ---
            ComputeSymmetryLegs();

            // --- فلگ (ص35–36): بعد از بسته شدن هوک در نقطه ۳ (=Z)، اسپایک تا F (Low Volume Spike)
            //     و سپس ناتوانی از ادامه؛ تارگت اول Z حتمی است.
            //     تشخیص فقط بر پایه نودها (پایدار، بدون نوسان با هر کندل).

            // ثبت چسبنده نود بسته‌شدن هوک (تا وقتی نود مخالف بعدی بیاید)
            if (hookClosed && pivots.Count > 0)
            {
                if (hookDir == "pos")
                    posHookBar = pivots[pivots.Count - 1].Bar;
                else if (hookDir == "neg")
                    negHookBar = pivots[pivots.Count - 1].Bar;
            }

            // فلگ: Z = نود بسته‌شدن هوک، F = نود مخالف بعد از آن
            if (pivots.Count >= 2)
            {
                Pivot fNode = pivots[pivots.Count - 1];
                Pivot zNode = pivots[pivots.Count - 2];

                if (posHookBar >= 0 && zNode.Bar == posHookBar &&
                    !zNode.IsHigh && fNode.IsHigh && fNode.Price > zNode.Price)
                {
                    flagActive = true;
                    flagZ = zNode.Price; flagZBar = zNode.Bar;
                    flagF = fNode.Price; flagFBar = fNode.Bar;
                }
                else if (negHookBar >= 0 && zNode.Bar == negHookBar &&
                         zNode.IsHigh && !fNode.IsHigh && fNode.Price < zNode.Price)
                {
                    flagActive = true;
                    flagZ = zNode.Price; flagZBar = zNode.Bar;
                    flagF = fNode.Price; flagFBar = fNode.Bar;
                }
            }
        }

        /// <summary>
        /// تقارن از زنجیره واقعی نودها: آخرین ۹ نود را می‌گیرد و لگ‌های متوالی (صعودی/نزولی)
        /// را می‌سازد؛ دو لگ هم‌جهت آخر → R1,R2 و دو لگ خلاف‌جهت آخر → C1,C2.
        /// (قبلاً از «آخرین سه سقف» و «آخرین سه کف» جدا استفاده می‌شد که موقع نودهای بی‌شماره
        /// ترتیب زمانی را به‌هم می‌زد و اعداد منفی تولید می‌کرد.)
        /// </summary>
        private void ComputeSymmetryLegs()
        {
            int n = pivots.Count;
            if (n < 5)
                return;

            double lastUp = double.NaN, prevUp = double.NaN;
            double lastDown = double.NaN, prevDown = double.NaN;
            int lastUpBar = -1, prevUpBar = -1;
            double lastUpPrice = 0, prevUpPrice = 0;
            int lastDownBar = -1, prevDownBar = -1;
            double lastDownPrice = 0, prevDownPrice = 0;

            int from = Math.Max(1, n - 9);
            for (int i = from; i < n; i++)
            {
                Pivot a = pivots[i - 1];
                Pivot b = pivots[i];
                if (a.IsHigh == b.IsHigh)
                    continue;

                double mag = Math.Abs(b.Price - a.Price);
                if (mag <= 0)
                    continue;

                int midBar = (a.Bar + b.Bar) / 2;
                double midPrice = (a.Price + b.Price) / 2;

                if (b.IsHigh)
                {
                    prevUp = lastUp; prevUpBar = lastUpBar; prevUpPrice = lastUpPrice;
                    lastUp = mag; lastUpBar = midBar; lastUpPrice = midPrice;
                }
                else
                {
                    prevDown = lastDown; prevDownBar = lastDownBar; prevDownPrice = lastDownPrice;
                    lastDown = mag; lastDownBar = midBar; lastDownPrice = midPrice;
                }
            }

            symR1 = prevUp; symBarR1 = prevUpBar; symPriceR1 = prevUpPrice;
            symR2 = lastUp; symBarR2 = lastUpBar; symPriceR2 = lastUpPrice;
            symC1 = prevDown; symBarC1 = prevDownBar; symPriceC1 = prevDownPrice;
            symC2 = lastDown; symBarC2 = lastDownBar; symPriceC2 = lastDownPrice;

            if (symR1 > 0 && symR2 > 0)
            {
                bool hitR = Math.Abs(symR2 - symR1) <= (SymTolerancePct / 100.0) * symR1;
                double lastPrice = pivots[n - 1].Price;
                if (hookDir == "neg")
                {
                    symDownMatched = hitR;
                    projDownTarget = lastPrice - symR2;
                }
                else
                {
                    symUpMatched = hitR;
                    projUpTarget = lastPrice + symR2;
                }
            }
            if (symC1 > 0 && symC2 > 0)
                symCorrMatched = Math.Abs(symC2 - symC1) <= (SymTolerancePct / 100.0) * symC1;
        }

        private void BuildSequenceText(List<Pivot> highs, List<Pivot> lows)
        {
            // آخرین شش نود شماره‌دار به ترتیب زمانی (کل زنجیره، نه فقط چند نود آخر)
            List<string> parts = new List<string>();
            for (int i = pivots.Count - 1; i >= 0 && parts.Count < 6; i--)
            {
                Pivot p = pivots[i];
                if (p.Num <= 0)
                    continue;
                parts.Insert(0, (p.IsHigh ? "P" : "T") + p.Num.ToString(CultureInfo.InvariantCulture));
            }
            seqText = parts.Count > 0 ? string.Join(">", parts.ToArray()) : "-";
        }

        /// <summary>سطح 86.4٪ از مچر هوک لایه ۲ می‌آید (فیلد Level864 هوک فعال — پورت hooks.py)؛
        /// توابع قدیمی ComputeLevel86/ComputeLevelFromHook حذف شدند.</summary>
        #endregion

        #region Drawing
        private void DrawStatusLabel()
        {
            if (!ShowStatusLabel)
            {
                RemoveDrawObject("ndsStatus");
                return;
            }

            string status = "NDS: " + hookDir + "-hook " + hookType +
                            "  |  seq: " + seqText +
                            "  |  N:" + peakSeqPos + "/3 · S:" + troughSeqPos + "/3" +
                            "  |  nodes: " + pivots.Count +
                            " (num:" + (numberedPeaksCount + numberedTroughsCount) +
                            " unnum:" + (pivots.Count - numberedPeaksCount - numberedTroughsCount) + ")" +
                            "  |  lvl" + CycleRetracePct.ToString("0.#", CultureInfo.InvariantCulture) + ": " +
                            (double.IsNaN(level86) ? "-" : Fmt(level86)) +
                            (level86Broken ? " (broken)" : "") +
                            (double.IsNaN(level86From) ? "" :
                                (level86HookBasis ? "  [T→3: " : "  [leg: ") + Fmt(level86From) + "→" + Fmt(level86To) + "]") +
                            "  |  sym: R2/R1=" +
                            (double.IsNaN(symR1) || symR1 <= 0
                                ? "-"
                                : (symR2 / symR1).ToString("0.00", CultureInfo.InvariantCulture)) +
                            (symUpMatched || symDownMatched ? " ok" : "") +
                            (double.IsNaN(symC1) || symC1 <= 0
                                ? ""
                                : "  C2/C1=" + (symC2 / symC1).ToString("0.00", CultureInfo.InvariantCulture) +
                                  (symCorrMatched ? " ok" : "")) +
                            (flagActive ? "  |  FLAG Z=" + Fmt(flagZ) + " F=" + Fmt(flagF) : "");
            Draw.TextFixed(this, "ndsStatus", status, StatusPosition);
        }

        /// <summary>
        /// خطوط متغیر: سطح 86.4٪، کادر فلگ + خط تارگت Z، پروجکشن تقارن، و رد سکانس.
        /// </summary>
        private void UpdateEvolvingDrawings()
        {
            // --- سطح 86.4٪ ---
            List<Pivot> highs = NumberedHighs(2);
            List<Pivot> lows = NumberedLows(2);
            if (highs.Count == 0) highs = LastPivots(true, 2);
            if (lows.Count == 0) lows = LastPivots(false, 2);
            if (ShowLevel86 && !double.IsNaN(level86) && highs.Count > 0 && lows.Count > 0)
            {
                int anchorBar = level86AnchorBar >= 0 ? level86AnchorBar
                              : (hookDir == "neg" ? highs[highs.Count - 1].Bar : lows[lows.Count - 1].Bar);
                if (CurrentBar - anchorBar <= MaxDrawLookbackBars)
                {
                    DateTime tStart = NodeTime(anchorBar, LevelShiftBars);
                    DateTime tEnd = NodeTime(CurrentBar, LevelShiftBars);
                    Draw.Line(this, "ndsLvl86", false,
                        tStart, level86,
                        tEnd, level86,
                        level86Broken ? Level86BrokenBrush : Level86IntactBrush,
                        DashStyleHelper.Dash, Math.Max(1, Level86Width));
                    Draw.Text(this, "ndsLvl86Txt", false,
                        "lvl " + CycleRetracePct.ToString("0.#", CultureInfo.InvariantCulture),
                        tStart, level86, 6 + LevelLabelYPixels,
                        level86Broken ? Level86BrokenBrush : Level86IntactBrush,
                        numberFont, System.Windows.TextAlignment.Right,
                        Brushes.Transparent, Brushes.Transparent, 0);
                }
            }
            else
            {
                RemoveDrawObject("ndsLvl86");
                RemoveDrawObject("ndsLvl86Txt");
            }

            // --- کادر فلگ + تارگت اول Z (ص35/36) ---
            if (ShowFlagBox && flagActive && flagZBar >= 0 && CurrentBar - flagZBar <= FlagMaxBars)
            {
                int opacity = (int)Math.Max(0, Math.Min(100, FlagBoxOpacity)) * 255 / 100;
                double dy = FlagYTicks * TickSize;
                double top = Math.Max(flagF, flagZ) + dy;
                double bottom = Math.Min(flagF, flagZ) + dy;
                double zLine = flagZ + dy;
                DateTime tZ = NodeTime(flagZBar, FlagShiftBars);
                DateTime tNow = NodeTime(CurrentBar, FlagShiftBars);
                Draw.Rectangle(this, "ndsFlagBox", false, tZ, top, tNow, bottom,
                    FlagBoxOutlineBrush, FlagBoxFillBrush, opacity);
                Draw.Line(this, "ndsFlagZ", false, tZ, zLine, tNow, zLine,
                    FlagZBrush, DashStyleHelper.Dot, 1);
                Draw.Text(this, "ndsFlagTxt", false, "Flag Z→F",
                    tNow, bottom, -16, FlagBoxOutlineBrush, numberFont,
                    System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            else
            {
                RemoveDrawObject("ndsFlagBox");
                RemoveDrawObject("ndsFlagZ");
                RemoveDrawObject("ndsFlagTxt");
            }

            // --- پروجکشن تقارن (ص55–63) ---
            if (ShowProjections)
            {
                if (!double.IsNaN(projUpTarget) && highs.Count > 0 && CurrentBar - highs[highs.Count - 1].Bar <= MaxDrawLookbackBars)
                {
                    double y = PriceShift(projUpTarget, ProjYTicks);
                    Draw.Line(this, "ndsProjUp", false,
                        NodeTime(highs[highs.Count - 1].Bar, ProjShiftBars), y,
                        NodeTime(CurrentBar, ProjShiftBars), y,
                        symUpMatched ? SymHitBrush : ProjectionBrush, DashStyleHelper.Dot, 1);
                }
                else
                {
                    RemoveDrawObject("ndsProjUp");
                }

                if (!double.IsNaN(projDownTarget) && lows.Count > 0 && CurrentBar - lows[lows.Count - 1].Bar <= MaxDrawLookbackBars)
                {
                    double y = PriceShift(projDownTarget, ProjYTicks);
                    Draw.Line(this, "ndsProjDown", false,
                        NodeTime(lows[lows.Count - 1].Bar, ProjShiftBars), y,
                        NodeTime(CurrentBar, ProjShiftBars), y,
                        symDownMatched ? SymHitBrush : ProjectionBrush, DashStyleHelper.Dot, 1);
                }
                else
                {
                    RemoveDrawObject("ndsProjDown");
                }
            }
            else
            {
                RemoveDrawObject("ndsProjUp");
                RemoveDrawObject("ndsProjDown");
            }

            // --- برچسب‌های تقارن + رد سکانس + هوک ---
            DrawSymmetryLabels();
            DrawSequenceTrace();
            DrawHookMarking();
        }

        private void DrawSequenceTrace()
        {
            string key = "trace|" + seqText + "|" + hookType + "|" + pivots.Count;
            if (!ShowSequenceTrace || pivots.Count < 2)
            {
                ClearAnnotations();
                lastAnnotationKey = "";
                return;
            }
            if (key == lastAnnotationKey)
                return;
            lastAnnotationKey = key;

            ClearAnnotations();

            List<int> idx = new List<int>();
            for (int i = 0; i < pivots.Count; i++)
                if (pivots[i].Num > 0)
                    idx.Add(i);

            int start = Math.Max(0, idx.Count - 7);
            for (int i = start; i < idx.Count - 1; i++)
            {
                Pivot a = pivots[idx[i]];
                Pivot b = pivots[idx[i + 1]];
                if (CurrentBar - a.Bar > MaxDrawLookbackBars)
                    continue;
                string tag = "ndsSeq" + a.Bar.ToString();
                Draw.Line(this, tag, false,
                    NodeTime(a.Bar, ShiftBarsFor(a.IsHigh)), NodePrice(a.Price, a.IsHigh),
                    NodeTime(b.Bar, ShiftBarsFor(b.IsHigh)), NodePrice(b.Price, b.IsHigh),
                    SequenceTraceBrush, DashStyleHelper.Solid, 1);
                annotationTags.Add(tag);
            }
        }

        private void DrawSymmetryLabels()
        {
            if (!ShowSymmetry || !ShowSymmetryLabels)
            {
                RemoveDrawObject("ndsR1");
                RemoveDrawObject("ndsR2");
                RemoveDrawObject("ndsC1");
                RemoveDrawObject("ndsC2");
                return;
            }

            DrawSymLabel("ndsR1", "R1 " + Fmt(symR1), symBarR1, symPriceR1, symUpMatched || symDownMatched, -14, -12);
            DrawSymLabel("ndsR2", "R2 " + Fmt(symR2), symBarR2, symPriceR2, symUpMatched || symDownMatched, -28, -12);
            DrawSymLabel("ndsC1", "C1 " + Fmt(symC1), symBarC1, symPriceC1, symCorrMatched, 14, 12);
            DrawSymLabel("ndsC2", "C2 " + Fmt(symC2), symBarC2, symPriceC2, symCorrMatched, 28, 12);
        }

        private void DrawSymLabel(string tag, string text, int bar, double price, bool matched, int dirTicks, int dirPixels)
        {
            if (bar < 0 || double.IsNaN(price) || CurrentBar - bar > MaxDrawLookbackBars)
            {
                RemoveDrawObject(tag);
                return;
            }

            double y = PriceShift(price, dirTicks);
            Draw.Text(this, tag, false, text,
                NodeTime(bar, SymLabelShiftBars), y, dirPixels + SymLabelYPixels,
                matched ? SymHitBrush : SymLineBrush, numberFont,
                System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
        }

        private void DrawHookMarking()
        {
            if (!ShowHookLabel || hookDir == "none" || hookType == "none")
            {
                RemoveDrawObject("ndsHookBox");
                return;
            }

            List<int> idx = new List<int>();
            for (int i = 0; i < pivots.Count; i++)
                if (pivots[i].Num > 0)
                    idx.Add(i);
            if (idx.Count < 2)
                return;

            int firstIdx = idx[Math.Max(0, idx.Count - 4)];
            int lastIdx = idx[idx.Count - 1];
            Pivot first = pivots[firstIdx];
            Pivot last = pivots[lastIdx];

            double top = Math.Max(first.Price, last.Price);
            double bottom = Math.Min(first.Price, last.Price);
            for (int i = firstIdx; i <= lastIdx; i++)
            {
                top = Math.Max(top, pivots[i].Price);
                bottom = Math.Min(bottom, pivots[i].Price);
            }

            if (CurrentBar - first.Bar > MaxDrawLookbackBars)
                return;

            Draw.Rectangle(this, "ndsHookBox", false,
                NodeTime(first.Bar, ShiftBarsFor(first.IsHigh)), top + HighNodeYTicks * TickSize,
                NodeTime(last.Bar, ShiftBarsFor(last.IsHigh)), bottom + LowNodeYTicks * TickSize,
                HookLabelBrush, Brushes.Transparent, 0);

            // لنگر متن هوک: نود پایانی سکانسِ حیاتی — مثبت: دره ۳ (پایین کادر)؛ منفی: قله ۳ (بالای کادر)
            double hookAnchor = NodePrice(hookDir == "pos" ? bottom : top, hookDir != "pos");
            int hookDirSign = hookDir == "pos" ? -1 : 1;   // نوشته پایین کادر برای هوک مثبت، بالای کادر برای منفی
            Draw.Text(this, "ndsHookTxt", false,
                "Hook " + hookType + " (" + (hookDir == "pos" ? "pos" : "neg") + ")" + (hookClosed ? " closed@3" : ""),
                NodeTime(last.Bar, HookShiftBars),
                hookAnchor + hookDirSign * 12 * TickSize,
                hookDirSign * 14 + HookYPixels,
                HookLabelBrush, numberFont, System.Windows.TextAlignment.Center,
                Brushes.Transparent, Brushes.Transparent, 0);
        }
        #endregion

        #region Engine bridge
        private string GetTfKey()
        {
            if (tfKeyCached != null)
                return tfKeyCached;
            try
            {
                BarsPeriod bp = BarsPeriod;
                tfKeyCached = (bp != null && bp.BarsPeriodType == BarsPeriodType.Minute)
                    ? bp.Value.ToString(CultureInfo.InvariantCulture)
                    : "chart";
            }
            catch
            {
                tfKeyCached = "chart";
            }
            return tfKeyCached;
        }

        private static string Fmt(double d)
        {
            return d.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static string Esc(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private void SendHeartbeat()
        {
            if (!EnableEngineConnection || publisher == null)
                return;
            barsSinceLastSend++;
            if (barsSinceLastSend < Math.Max(1, HeartbeatIntervalBars))
                return;
            barsSinceLastSend = 0;
            publisher.Send(BuildStateJson("state"));
        }

        private string BuildStateJson(string type)
        {
            StringBuilder sb = new StringBuilder(400);
            sb.Append("{\"type\":\"").Append(type).Append("\"");
            sb.Append(",\"instrument\":\"").Append(Esc(Instrument != null ? Instrument.FullName : "")).Append("\"");
            sb.Append(",\"ts\":\"").Append(Time[0].ToString("o")).Append("\"");
            sb.Append(",\"tf\":{\"").Append(Esc(GetTfKey())).Append("\":{");
            sb.Append("\"nds_state\":\"").Append(hookDir == "none" ? "unknown" : (hookDir == "pos" ? "hook_pos" : "hook_neg")).Append("\"");
            sb.Append(",\"hook\":\"").Append(hookType).Append("\"");
            sb.Append(",\"hook_closed\":").Append(hookClosed ? "true" : "false");
            sb.Append(",\"sequence\":\"").Append(Esc(seqText)).Append("\"");
            sb.Append(",\"peaks\":").Append(numberedPeaksCount).Append(",\"troughs\":").Append(numberedTroughsCount);
            if (!double.IsNaN(level86))
            {
                sb.Append(",\"level\":").Append(Fmt(level86));
                sb.Append(",\"level_broken\":").Append(level86Broken ? "true" : "false");
            }
            if (!double.IsNaN(projUpTarget)) sb.Append(",\"proj_up\":").Append(Fmt(projUpTarget));
            if (!double.IsNaN(projDownTarget)) sb.Append(",\"proj_down\":").Append(Fmt(projDownTarget));
            if (flagActive)
            {
                sb.Append(",\"flag\":true,\"flag_z\":").Append(Fmt(flagZ)).Append(",\"flag_f\":").Append(Fmt(flagF));
            }
            sb.Append(",\"close\":").Append(Fmt(Close[0]));
            sb.Append("}}}");
            return sb.ToString();
        }
        #endregion

        #region Properties

        // 00) عمومی
        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "قدرت سویینگ (کندل در هر طرف)", Description = "قله = کندلی که High آن از این تعداد کندل در هر طرف بالاتر باشد (دره با Low)؛ پیش‌فرض ۵", GroupName = "00) عمومی", Order = 0)]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ملاک سویینگ", Description = "HighLow = High/Low کندل (پیش‌فرض) · CloseLine = Close کندل", GroupName = "00) عمومی", Order = 1)]
        public NdsSwingSource SwingSource { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "حداقل فاصله ثبت نودها (کندل)", Description = "حداقل فاصلهٔ کندلی بین ثبت دو نود متوالی (کندل تأیید نود قبلی تا کندل تأیید نود بعدی)؛ صفر = غیرفعال. یکی‌درمیانی قله/دره همیشه حفظ می‌شود", GroupName = "00) عمومی", Order = 2)]
        public int MinBarsBetweenNodes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "قاعده سکانس", Description = "AnchoredNoReset = نود ناقض سکانس را ریست نمی‌کند (پیش‌فرض فعلی) · RestartOnFail = نود ناقض سکانس را از نو شروع می‌کند", GroupName = "00) عمومی", Order = 3)]
        public NdsSequenceRule SequenceRule { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "حداقل تجاوز نود جدید (تیک)", Description = "کف مطلق: نود جدید باید حداقل این تعداد تیک از نود شماره‌دار قبلی عبور کند", GroupName = "00) عمومی", Order = 6)]
        public int NodeBreakToleranceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "تلورانس دوقله/دوکف (تیک)", Description = "دو قله/دو کف با اختلاف کمتر از این تعداد تیک = یک نود و یک شماره (قانون دوقله هم‌اندازه کتاب)", GroupName = "00) عمومی", Order = 8)]
        public int EqualToleranceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(100, 50000)]
        [Display(Name = "بازه رسم (کندل)", GroupName = "00) عمومی", Order = 2)]
        public int MaxDrawLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "حالت اشکال‌زدایی", GroupName = "00) عمومی", Order = 3)]
        public bool DebugMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "برچسب وضعیت روی چارت", GroupName = "00) عمومی", Order = 4)]
        public bool ShowStatusLabel { get; set; }

        // 01) نودها
        [NinjaScriptProperty]
        [Display(Name = "نمایش نودها", GroupName = "01) نودها", Order = 1)]
        public bool ShowNodes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش اکستریم تأییدنشده", Description = "اکستریم کاندید لگ (هنوز تأیید نشده) با نشانهٔ کم‌رنگ (شفافیت ۳۰٪) جدا نشان داده می‌شود؛ نود تأییدشده هرگز جابه‌جا نمی‌شود", GroupName = "01) نودها", Order = 1)]
        public bool ShowProvisionalNode { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ نود سقف", GroupName = "01) نودها", Order = 2)]
        public Brush NodeHighBrush { get; set; }

        [Browsable(false)]
        public string NodeHighBrushSerializable
        {
            get { return Serialize.BrushToString(NodeHighBrush); }
            set { NodeHighBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "رنگ نود کف", GroupName = "01) نودها", Order = 3)]
        public Brush NodeLowBrush { get; set; }

        [Browsable(false)]
        public string NodeLowBrushSerializable
        {
            get { return Serialize.BrushToString(NodeLowBrush); }
            set { NodeLowBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "رنگ نود بی‌شماره", Description = "نودی که شرط شماره‌گیری (تجاوز از نود قبلی) را ندارد", GroupName = "01) نودها", Order = 4)]
        public Brush UnnumberedNodeBrush { get; set; }

        [Browsable(false)]
        public string UnnumberedNodeBrushSerializable
        {
            get { return Serialize.BrushToString(UnnumberedNodeBrush); }
            set { UnnumberedNodeBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "حالت رنگ نودها", Description = "HighLow = رنگ انتخابی خودت (سقف/کف)؛ Numbered = نود بی‌شماره رنگ جدا می‌گیرد", GroupName = "01) نودها", Order = 5)]
        public NdsNodeColorMode NodeColorMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "شکل علامت نود", GroupName = "01) نودها", Order = 5)]
        public NdsMarkerShape NodeMarkerShape { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "خط اتصال نودها", GroupName = "01) نودها", Order = 6)]
        public bool ShowNodeConnections { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ خط اتصال", GroupName = "01) نودها", Order = 7)]
        public Brush NodeLineBrush { get; set; }

        [Browsable(false)]
        public string NodeLineBrushSerializable
        {
            get { return Serialize.BrushToString(NodeLineBrush); }
            set { NodeLineBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 4)]
        [Display(Name = "ضخامت خط اتصال", GroupName = "01) نودها", Order = 8)]
        public int NodeLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "استایل خط اتصال", GroupName = "01) نودها", Order = 9)]
        public DashStyleHelper NodeLineDash { get; set; }

        // 02) هوک و سکانس
        [NinjaScriptProperty]
        [Display(Name = "نمایش شماره نودها", GroupName = "02) هوک و سکانس", Order = 1)]
        public bool ShowCountNumbers { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ شماره‌ها", GroupName = "02) هوک و سکانس", Order = 2)]
        public Brush NumberBrush { get; set; }

        [Browsable(false)]
        public string NumberBrushSerializable
        {
            get { return Serialize.BrushToString(NumberBrush); }
            set { NumberBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(8, 30)]
        [Display(Name = "اندازه فونت شماره‌ها", GroupName = "02) هوک و سکانس", Order = 3)]
        public int NumberFontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش کادر و برچسب هوک", GroupName = "02) هوک و سکانس", Order = 4)]
        public bool ShowHookLabel { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ هوک", GroupName = "02) هوک و سکانس", Order = 5)]
        public Brush HookLabelBrush { get; set; }

        [Browsable(false)]
        public string HookLabelBrushSerializable
        {
            get { return Serialize.BrushToString(HookLabelBrush); }
            set { HookLabelBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(0.0, 50.0)]
        [Display(Name = "تلورانس نود هم‌سطح (٪)", GroupName = "02) هوک و سکانس", Order = 6)]
        public double HookEqualTolerancePct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش رد سکانس", Description = "خطوط اتصال نودهای شماره‌دار (سکانس)", GroupName = "02) هوک و سکانس", Order = 7)]
        public bool ShowSequenceTrace { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش لایهٔ خرد", Description = "هوک‌های خرد (داخل هوک بزرگ‌تر یا از V2 شروع‌شده) با شمارهٔ جدا رسم شوند؛ پیش‌فرض خاموش (معماری v2)", GroupName = "02) هوک و سکانس", Order = 8)]
        public bool ShowMicroLayer { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ رد سکانس", GroupName = "02) هوک و سکانس", Order = 8)]
        public Brush SequenceTraceBrush { get; set; }

        [Browsable(false)]
        public string SequenceTraceBrushSerializable
        {
            get { return Serialize.BrushToString(SequenceTraceBrush); }
            set { SequenceTraceBrush = Serialize.StringToBrush(value); }
        }

        // 03) فلگ
        [NinjaScriptProperty]
        [Display(Name = "نمایش کادر فلگ", GroupName = "03) فلگ", Order = 1)]
        public bool ShowFlagBox { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ خط کادر فلگ", GroupName = "03) فلگ", Order = 2)]
        public Brush FlagBoxOutlineBrush { get; set; }

        [Browsable(false)]
        public string FlagBoxOutlineBrushSerializable
        {
            get { return Serialize.BrushToString(FlagBoxOutlineBrush); }
            set { FlagBoxOutlineBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "رنگ پرکردن کادر فلگ", GroupName = "03) فلگ", Order = 3)]
        public Brush FlagBoxFillBrush { get; set; }

        [Browsable(false)]
        public string FlagBoxFillBrushSerializable
        {
            get { return Serialize.BrushToString(FlagBoxFillBrush); }
            set { FlagBoxFillBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "شفافیت پرکردن فلگ (٪)", GroupName = "03) فلگ", Order = 4)]
        public int FlagBoxOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ خط تارگت Z", Description = "خط تارگت اول فلم که کتاب «حتمی» می‌داند", GroupName = "03) فلگ", Order = 8)]
        public Brush FlagZBrush { get; set; }

        [Browsable(false)]
        public string FlagZBrushSerializable
        {
            get { return Serialize.BrushToString(FlagZBrush); }
            set { FlagZBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(20, 5000)]
        [Display(Name = "حداکثر عمر فلگ (کندل)", GroupName = "03) فلگ", Order = 5)]
        public int FlagMaxBars { get; set; }

        // 04) سطح 86.4 درصد
        [NinjaScriptProperty]
        [Display(Name = "نمایش سطح ریتریس", GroupName = "04) سطح 86.4 درصد", Order = 1)]
        public bool ShowLevel86 { get; set; }

        [NinjaScriptProperty]
        [Range(50.0, 99.0)]
        [Display(Name = "درصد ریتریس سایکل", Description = "کتاب NDS همیشه 86.4٪ را به کار می‌برد", GroupName = "04) سطح 86.4 درصد", Order = 2)]
        public double CycleRetracePct { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ سطح سالم", GroupName = "04) سطح 86.4 درصد", Order = 3)]
        public Brush Level86IntactBrush { get; set; }

        [Browsable(false)]
        public string Level86IntactBrushSerializable
        {
            get { return Serialize.BrushToString(Level86IntactBrush); }
            set { Level86IntactBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "رنگ سطح شکسته", GroupName = "04) سطح 86.4 درصد", Order = 4)]
        public Brush Level86BrokenBrush { get; set; }

        [Browsable(false)]
        public string Level86BrokenBrushSerializable
        {
            get { return Serialize.BrushToString(Level86BrokenBrush); }
            set { Level86BrokenBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 4)]
        [Display(Name = "ضخامت خط سطح", GroupName = "04) سطح 86.4 درصد", Order = 5)]
        public int Level86Width { get; set; }

        // 05) تقارن
        [NinjaScriptProperty]
        [Display(Name = "نمایش تقارن (تشخیص)", GroupName = "05) تقارن", Order = 1)]
        public bool ShowSymmetry { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 50.0)]
        [Display(Name = "تلورانس تقارن (٪)", GroupName = "05) تقارن", Order = 2)]
        public double SymTolerancePct { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ خط تقارن", GroupName = "05) تقارن", Order = 3)]
        public Brush SymLineBrush { get; set; }

        [Browsable(false)]
        public string SymLineBrushSerializable
        {
            get { return Serialize.BrushToString(SymLineBrush); }
            set { SymLineBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "رنگ تقارن تاییدشده", GroupName = "05) تقارن", Order = 4)]
        public Brush SymHitBrush { get; set; }

        [Browsable(false)]
        public string SymHitBrushSerializable
        {
            get { return Serialize.BrushToString(SymHitBrush); }
            set { SymHitBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "نمایش پروجکشن تارگت", Description = "تارگت تقارن لگ بعدی (B2 = B1×A2/A1)", GroupName = "05) تقارن", Order = 5)]
        public bool ShowProjections { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ پروجکشن تارگت", GroupName = "05) تقارن", Order = 6)]
        public Brush ProjectionBrush { get; set; }

        [Browsable(false)]
        public string ProjectionBrushSerializable
        {
            get { return Serialize.BrushToString(ProjectionBrush); }
            set { ProjectionBrush = Serialize.StringToBrush(value); }
        }

        // --- جابجایی نودها (داخل گروه 01) ---
        [NinjaScriptProperty]
        [Range(-100, 100)]
        [Display(Name = "جابجایی افقی نودهای سقف (کندل)", Description = "مثبت = راست، منفی = چپ", GroupName = "01) نودها", Order = 10)]
        public int HighNodeShiftBars { get; set; }

        [NinjaScriptProperty]
        [Range(-100.0, 100.0)]
        [Display(Name = "جابجایی عمودی نودهای سقف (تیک)", Description = "مثبت = بالا، منفی = پایین", GroupName = "01) نودها", Order = 11)]
        public double HighNodeYTicks { get; set; }

        [NinjaScriptProperty]
        [Range(-100, 100)]
        [Display(Name = "جابجایی افقی نودهای کف (کندل)", GroupName = "01) نودها", Order = 12)]
        public int LowNodeShiftBars { get; set; }

        [NinjaScriptProperty]
        [Range(-100.0, 100.0)]
        [Display(Name = "جابجایی عمودی نودهای کف (تیک)", GroupName = "01) نودها", Order = 13)]
        public double LowNodeYTicks { get; set; }

        // --- جابجایی برچسب‌ها (داخل گروه 02) ---
        [NinjaScriptProperty]
        [Display(Name = "نمایش شماره/حروف نودها", Description = "Number = 1,2,3 · Letter = N1,N2 برای سقف و S1,S2 برای کف (طبق ص14)", GroupName = "02) هوک و سکانس", Order = 13)]
        public NdsNodeLabelStyle NodeLabelStyle { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "فاصله نوشته از نود (تیک)", Description = "نوشته سقف بالای نود و نوشته کف پایین نود، به این تعداد تیک — مستقل از زوم", GroupName = "02) هوک و سکانس", Order = 16)]
        public int NodeLabelTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نوشته نودهای بیشماره", Description = "نودهایی که شرط شماره‌گیری ندارند هم برچسب خاکستری بگیرند (فقط حرف، بدون شماره)", GroupName = "02) هوک و سکانس", Order = 14)]
        public bool ShowUnnumberedLabels { get; set; }

        [XmlIgnore]
        [Display(Name = "رنگ نوشته نودهای بیشماره", GroupName = "02) هوک و سکانس", Order = 15)]
        public Brush UnnumberedLabelBrush { get; set; }

        [Browsable(false)]
        public string UnnumberedLabelBrushSerializable
        {
            get { return Serialize.BrushToString(UnnumberedLabelBrush); }
            set { UnnumberedLabelBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(-100, 100)]
        [Display(Name = "جابجایی افقی اعداد (کندل)", GroupName = "02) هوک و سکانس", Order = 9)]
        public int NumberShiftBars { get; set; }

        [NinjaScriptProperty]
        [Range(-300, 300)]
        [Display(Name = "جابجایی عمودی اعداد (پیکسل؛ مثبت = بالا)", GroupName = "02) هوک و سکانس", Order = 10)]
        public int NumberYPixels { get; set; }

        [NinjaScriptProperty]
        [Range(-100, 100)]
        [Display(Name = "جابجایی افقی برچسب هوک (کندل)", GroupName = "02) هوک و سکانس", Order = 11)]
        public int HookShiftBars { get; set; }

        [NinjaScriptProperty]
        [Range(-300, 300)]
        [Display(Name = "جابجایی عمودی برچسب هوک (پیکسل)", GroupName = "02) هوک و سکانس", Order = 12)]
        public int HookYPixels { get; set; }

        // --- جابجایی فلگ (داخل گروه 03) ---
        [NinjaScriptProperty]
        [Range(-100, 100)]
        [Display(Name = "جابجایی افقی فلگ (کندل)", GroupName = "03) فلگ", Order = 6)]
        public int FlagShiftBars { get; set; }

        [NinjaScriptProperty]
        [Range(-100.0, 100.0)]
        [Display(Name = "جابجایی عمودی فلگ (تیک)", GroupName = "03) فلگ", Order = 7)]
        public double FlagYTicks { get; set; }

        // --- جابجایی سطح (داخل گروه 04) ---
        [NinjaScriptProperty]
        [Range(-100, 100)]
        [Display(Name = "جابجایی افقی سطح (کندل)", Description = "نقطه شروع خط سطح 86.4٪", GroupName = "04) سطح 86.4 درصد", Order = 6)]
        public int LevelShiftBars { get; set; }

        [NinjaScriptProperty]
        [Range(-300, 300)]
        [Display(Name = "جابجایی عمودی برچسب سطح (پیکسل)", GroupName = "04) سطح 86.4 درصد", Order = 7)]
        public int LevelLabelYPixels { get; set; }

        // --- جابجایی پروجکشن‌ها (داخل گروه 05) ---
        [NinjaScriptProperty]
        [Range(-100, 100)]
        [Display(Name = "جابجایی افقی پروجکشن‌ها (کندل)", GroupName = "05) تقارن", Order = 7)]
        public int ProjShiftBars { get; set; }

        [NinjaScriptProperty]
        [Range(-100.0, 100.0)]
        [Display(Name = "جابجایی عمودی پروجکشن‌ها (تیک)", GroupName = "05) تقارن", Order = 8)]
        public double ProjYTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "نمایش برچسب‌های تقارن (R1/R2/C1/C2)", Description = "اندازه هر لگ صعودی و هر اصلاح را روی چارت می‌نویسد", GroupName = "05) تقارن", Order = 9)]
        public bool ShowSymmetryLabels { get; set; }

        [NinjaScriptProperty]
        [Range(-100, 100)]
        [Display(Name = "جابجایی افقی برچسب تقارن (کندل)", GroupName = "05) تقارن", Order = 10)]
        public int SymLabelShiftBars { get; set; }

        [NinjaScriptProperty]
        [Range(-300, 300)]
        [Display(Name = "جابجایی عمودی برچسب تقارن (پیکسل)", GroupName = "05) تقارن", Order = 11)]
        public int SymLabelYPixels { get; set; }

        // --- محل برچسب وضعیت (داخل گروه 00) ---
        [NinjaScriptProperty]
        [Display(Name = "محل برچسب وضعیت", Description = "گوشه نمایش متن وضعیت روی چارت", GroupName = "00) عمومی", Order = 5)]
        public TextPosition StatusPosition { get; set; }

        // 09) اتصال به موتور تصمیم
        [NinjaScriptProperty]
        [Display(Name = "اتصال به موتور تصمیم", GroupName = "09) اتصال به موتور", Order = 1)]
        public bool EnableEngineConnection { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "آدرس موتور (Host)", GroupName = "09) اتصال به موتور", Order = 2)]
        public string EngineHost { get; set; }

        [NinjaScriptProperty]
        [Range(1, 65535)]
        [Display(Name = "پورت موتور", GroupName = "09) اتصال به موتور", Order = 3)]
        public int EnginePort { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "فاصله heartbeat (کندل)", GroupName = "09) اتصال به موتور", Order = 4)]
        public int HeartbeatIntervalBars { get; set; }

        #endregion
    }

    /// <summary>
    /// اتصال TCP سبک به موتور — فقط ارسال، هرگز UI را بلاک نمی‌کند.
    /// </summary>
    public class NdsStatePublisher : IDisposable
    {
        private readonly string host;
        private readonly int port;
        private TcpClient client;
        private NetworkStream stream;
        private bool active = true;
        private int failCount;

        public NdsStatePublisher(string host, int port)
        {
            this.host = string.IsNullOrEmpty(host) ? "127.0.0.1" : host;
            this.port = port <= 0 ? 46320 : port;
        }

        public void Send(string json)
        {
            if (!active)
                return;
            try
            {
                if (client == null || !client.Connected)
                {
                    TcpClient c = new TcpClient();
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    if (ar.AsyncWaitHandle.WaitOne(300) && c.Connected)
                    {
                        c.EndConnect(ar);
                        client = c;
                        stream = client.GetStream();
                    }
                    else
                    {
                        try { c.Close(); } catch { }
                        failCount++;
                        if (failCount > 200)
                            active = false;
                        return;
                    }
                }

                byte[] payload = Encoding.UTF8.GetBytes(json + "\n");
                stream.Write(payload, 0, payload.Length);
                failCount = 0;
            }
            catch
            {
                Cleanup();
            }
        }

        private void Cleanup()
        {
            try { if (stream != null) stream.Dispose(); } catch { }
            try { if (client != null) client.Close(); } catch { }
            stream = null;
            client = null;
        }

        public void Dispose()
        {
            active = false;
            Cleanup();
        }
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.Gui;


namespace NinjaTrader.NinjaScript.Strategies
{
    public class ESTrendline_v1 : Strategy
    {
        #region Models
        private struct SwingPoint
        {
            public int BarIndex;
            public double Price;
            public DateTime Time;
            public bool IsHigh;

            public SwingPoint(int barIndex, double price, DateTime time, bool isHigh)
            {
                BarIndex = barIndex;
                Price = price;
                Time = time;
                IsHigh = isHigh;
            }
        }

        private class TrendLineModel
        {
            public bool IsUptrend;
            public SwingPoint A;
            public SwingPoint B;
            public List<int> TouchBars = new List<int>();
            public bool IsValid;
            public bool IsConsumed;

            public TrendLineModel(bool isUptrend, SwingPoint a, SwingPoint b)
            {
                IsUptrend = isUptrend;
                A = a;
                B = b;
                IsValid = true;
                IsConsumed = false;
            }

            public double Slope
            {
                get
                {
                    int dx = B.BarIndex - A.BarIndex;
                    if (dx == 0) return 0;
                    return (B.Price - A.Price) / dx;
                }
            }

            public int FirstTouchBar => TouchBars.Count > 0 ? TouchBars[0] : -1;
            public int LastTouchBar => TouchBars.Count > 0 ? TouchBars[TouchBars.Count - 1] : -1;

            public double ValueAtBar(int absBar)
            {
                return A.Price + Slope * (absBar - A.BarIndex);
            }

            public string Key
            {
                get
                {
                    string side = IsUptrend ? "UP" : "DN";
                    return side + "_" + A.BarIndex + "_" + B.BarIndex;
                }
            }
        }
        #endregion

        #region Constants
        private const string TagUp = "ESTrendline_v1.UpTrend";
        private const string TagDn = "ESTrendline_v1.DownTrend";
        private const string TagSafety = "ESTrendline_v1.Safety";
        #endregion

        #region Indicators
        private ATR atr2m;
        private EMA ema15m;
        #endregion

        #region State
        private readonly List<SwingPoint> pivotHighs = new List<SwingPoint>();
        private readonly List<SwingPoint> pivotLows = new List<SwingPoint>();

        private TrendLineModel uptrendLine;
        private TrendLineModel downtrendLine;

        // Trendline chaining (A->B, B->C, C->D...). We keep all active segments for now.
        private readonly List<TrendLineModel> uptrendSegments = new List<TrendLineModel>();
        private readonly List<TrendLineModel> downtrendSegments = new List<TrendLineModel>();

        private int htfBias; // +1 bull, -1 bear, 0 neutral

        private int pendingBreakDir; // +1 long, -1 short, 0 none
        private int pendingBreakBar;
        private int pendingBreakAnchorBar;
        private double pendingBreakAnchorPrice;
        private double pendingBreakSlope;

        private string activeEntrySignal;
        private bool activeIsBounce;
        private double entryPrice;
        private double initialRiskTicks;
        private bool breakevenMoved;
        private bool partialTaken;
        private double partialTargetTicks;
        private double currentHardStop;

        private int tradesThisSession;
        private int cooldownBarsRemaining;
        private double sessionStartCumProfit;

        private readonly HashSet<string> consumedLineKeys = new HashSet<string>();

        // Persist touch history across trendline object rebuilds.
        // Keyed by TrendLineModel.Key (UP/DN + anchor bar indices).
        private readonly Dictionary<string, List<int>> touchLedger = new Dictionary<string, List<int>>();

        private int lastProcessedClosedTradeCount;
        private bool rthGapCheckDone;
        private bool submittedEntryThisBar;
        #endregion

        #region Parameters
        [NinjaScriptProperty]
        [Range(2, 10)]
        [Display(Name = "SwingStrength", GroupName = "1. Structure", Order = 1)]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(50, 1000)]
        [Display(Name = "MaxSwingLookback", GroupName = "1. Structure", Order = 2)]
        public int MaxSwingLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "MinSwingDiffTicks", GroupName = "1. Structure", Order = 3)]
        public int MinSwingDiffTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "TouchZoneTicks", GroupName = "2. Touches", Order = 1)]
        public int TouchZoneTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "TouchCountToleranceTicks", GroupName = "2. Touches", Order = 2)]
        public int TouchCountToleranceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "MinBarsBetweenTouches", GroupName = "2. Touches", Order = 2)]
        public int MinBarsBetweenTouches { get; set; }

        [NinjaScriptProperty]
        [Range(2, 6)]
        [Display(Name = "MinTouchCount", GroupName = "2. Touches", Order = 3)]
        public int MinTouchCount { get; set; }

        [NinjaScriptProperty]
        [Range(10, 300)]
        [Display(Name = "MinBarsFromFirstTouch", GroupName = "2. Touches", Order = 4)]
        public int MinBarsFromFirstTouch { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseHTFFilter", GroupName = "3. Filters", Order = 1)]
        public bool UseHTFFilter { get; set; }

        [NinjaScriptProperty]
        [Range(5, 100)]
        [Display(Name = "HTFEmaPeriod", GroupName = "3. Filters", Order = 2)]
        public int HTFEmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "HTFSlopeLookback", GroupName = "3. Filters", Order = 3)]
        public int HTFSlopeLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 5.0)]
        [Display(Name = "HTFSlopeThreshold", GroupName = "3. Filters", Order = 4)]
        public double HTFSlopeThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "ATRPeriod", GroupName = "3. Filters", Order = 5)]
        public int ATRPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "MinATRTicks", GroupName = "3. Filters", Order = 6)]
        public int MinATRTicks { get; set; }

        [NinjaScriptProperty]
        [Range(2, 80)]
        [Display(Name = "MaxATRTicks", GroupName = "3. Filters", Order = 7)]
        public int MaxATRTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseNewsBlackout", GroupName = "3. Filters", Order = 8)]
        public bool UseNewsBlackout { get; set; }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "NewsBlackoutMinutes", GroupName = "3. Filters", Order = 9)]
        public int NewsBlackoutMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "MinRiskRewardRatio", GroupName = "4. Risk", Order = 1)]
        public double MinRiskRewardRatio { get; set; }

        [NinjaScriptProperty]
        [Range(50.0, 5000.0)]
        [Display(Name = "MaxRiskDollarsPerTrade", GroupName = "4. Risk", Order = 2)]
        public double MaxRiskDollarsPerTrade { get; set; }

        [NinjaScriptProperty]
        [Range(4, 60)]
        [Display(Name = "MaxSafetyStopTicks", GroupName = "4. Risk", Order = 3)]
        public int MaxSafetyStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "TargetATRMultiplier", GroupName = "4. Risk", Order = 4)]
        public double TargetATRMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "WaitForRetest", GroupName = "5. Entries", Order = 1)]
        public bool WaitForRetest { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "RetestZoneTicks", GroupName = "5. Entries", Order = 2)]
        public int RetestZoneTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "MaxRetestWaitBars", GroupName = "5. Entries", Order = 3)]
        public int MaxRetestWaitBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "MaxTradesPerSession", GroupName = "5. Entries", Order = 4)]
        public int MaxTradesPerSession { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "CooldownBarsAfterLoss", GroupName = "5. Entries", Order = 5)]
        public int CooldownBarsAfterLoss { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "BounceStopBufferTicks", GroupName = "5. Entries", Order = 6)]
        public int BounceStopBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "SessionStart", GroupName = "5. Entries", Order = 7)]
        public int SessionStart { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "SessionEndNoNewEntries", GroupName = "5. Entries", Order = 8)]
        public int SessionEndNoNewEntries { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "HardStopBufferTicks", GroupName = "6. Exits", Order = 1)]
        public int HardStopBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(4, 80)]
        [Display(Name = "HardStopMaxTicks", GroupName = "6. Exits", Order = 2)]
        public int HardStopMaxTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "BreakevenBufferTicks", GroupName = "6. Exits", Order = 3)]
        public int BreakevenBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "PartialExitPct", GroupName = "6. Exits", Order = 4)]
        public int PartialExitPct { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "PartialLockTicks", GroupName = "6. Exits", Order = 5)]
        public int PartialLockTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "SessionClose", GroupName = "6. Exits", Order = 6)]
        public int SessionClose { get; set; }

        [NinjaScriptProperty]
        [Range(50.0, 10000.0)]
        [Display(Name = "MaxDailyLossDollars", GroupName = "6. Exits", Order = 7)]
        public double MaxDailyLossDollars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableLogs", GroupName = "7. Debug", Order = 1)]
        public bool EnableLogs { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ShowLinesOnChart", GroupName = "7. Debug", Order = 2)]
        public bool ShowLinesOnChart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RequireSafetyLineForBounce", GroupName = "4. Risk", Order = 99)]
        public bool RequireSafetyLineForBounce { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PreferDominantTrendline", GroupName = "1. Structure", Order = 99)]
        public bool PreferDominantTrendline { get; set; }
        #endregion

        #region NinjaScript lifecycle
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ESTrendline_v1";
                Description = "Risk-first ES 2m trendline strategy inspired by Tori Trades (Action/Safety lines, break+bounce, retest, close-based exits).";
                Calculate = Calculate.OnBarClose;
                IsExitOnSessionCloseStrategy = false;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                BarsRequiredToTrade = 60;
                DefaultQuantity = 2;

                SwingStrength = 3;
                MaxSwingLookback = 200;
                MinSwingDiffTicks = 4;

                TouchZoneTicks = 4;
                TouchCountToleranceTicks = 4;
                MinBarsBetweenTouches = 6;
                MinTouchCount = 2;
                MinBarsFromFirstTouch = 10;

                UseHTFFilter = true;
                HTFEmaPeriod = 20;
                HTFSlopeLookback = 3;
                HTFSlopeThreshold = 0.5;
                ATRPeriod = 14;
                MinATRTicks = 2;
                MaxATRTicks = 40;
                UseNewsBlackout = true;
                NewsBlackoutMinutes = 5;

                MinRiskRewardRatio = 1.5;
                MaxRiskDollarsPerTrade = 200.0;
                MaxSafetyStopTicks = 30;
                TargetATRMultiplier = 2.0;

                WaitForRetest = false;
                RetestZoneTicks = 4;
                MaxRetestWaitBars = 15;
                MaxTradesPerSession = 2;
                CooldownBarsAfterLoss = 5;
                BounceStopBufferTicks = 2;
                SessionStart = 83000;
                SessionEndNoNewEntries = 145500;

                HardStopBufferTicks = 4;
                HardStopMaxTicks = 30;
                BreakevenBufferTicks = 2;
                PartialExitPct = 50;
                PartialLockTicks = 4;
                SessionClose = 150000;
                MaxDailyLossDollars = 500.0;

                EnableLogs = true;
                ShowLinesOnChart = true;

                RequireSafetyLineForBounce = true;
                PreferDominantTrendline = true;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 15);
            }
            else if (State == State.DataLoaded)
            {
                atr2m = ATR(ATRPeriod);
                ema15m = EMA(Closes[1], HTFEmaPeriod);
                ResetSessionState();
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
            {
                UpdateHtfBias();
                return;
            }

            if (BarsInProgress != 0)
                return;

            if (CurrentBar < BarsRequiredToTrade || CurrentBar < SwingStrength * 2 + 5)
                return;

            if (Bars.IsFirstBarOfSession)
                OnNewSession();

            submittedEntryThisBar = false;

            if (cooldownBarsRemaining > 0)
                cooldownBarsRemaining--;

            DetectConfirmedSwing();
            RebuildTrendlines();
            ValidateTrendlines();
            UpdateTouches(uptrendLine);
            UpdateTouches(downtrendLine);

            DrawLines();

            ProcessClosedTradesForCooldown();
            HandleRthOpeningGapInvalidation();
            ManageOpenPosition();

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (!CanOpenNewTrade())
                return;

            // break detection & entry states
            if (pendingBreakDir == 0)
                DetectBreakSignal();

            if (pendingBreakDir != 0)
                TryEnterFromPendingBreak();

            // optional bounce entries
            TryBounceEntry();
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            // capture entry details
            if (execution.Order.OrderState == OrderState.Filled &&
                (execution.Order.Name == "BreakLong" || execution.Order.Name == "BreakShort" ||
                 execution.Order.Name == "BounceLong" || execution.Order.Name == "BounceShort"))
            {
                entryPrice = execution.Order.AverageFillPrice;
                activeEntrySignal = execution.Order.Name;
                activeIsBounce = execution.Order.Name.StartsWith("Bounce", StringComparison.Ordinal);

                breakevenMoved = false;
                partialTaken = false;

                tradesThisSession++;

                // if risk wasn't set (edge case), compute fallback from max stop
                if (initialRiskTicks <= 0)
                    initialRiskTicks = HardStopMaxTicks;

                if (EnableLogs)
                    Log2($"[ENTRY] {activeEntrySignal} qty={Position.Quantity} @ {entryPrice:0.00}, riskTicks={initialRiskTicks:0.0}, targetTicks={partialTargetTicks:0.0}");
            }

            // detect session realized loss updates via cum profit delta on flat
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                double dailyPnl = GetDailyPnl();
                if (EnableLogs)
                    Log2($"[FLAT] DailyPnL={dailyPnl:0.00}, trades={tradesThisSession}");
            }
        }
        #endregion

        #region Core logic
        private void OnNewSession()
        {
            ResetSessionState();

            // Opening gap invalidation check happens naturally because we rebuild lines from current structure.
            // Explicitly clear pending signals at session boundary.
            pendingBreakDir = 0;
            pendingBreakBar = -1;
            pendingBreakAnchorBar = -1;
            pendingBreakAnchorPrice = 0;
            pendingBreakSlope = 0;

            if (EnableLogs)
                Log2($"[SESSION] New session at {Time[0]} | CumProfitBase={sessionStartCumProfit:0.00}");
        }

        private void ResetSessionState()
        {
            tradesThisSession = 0;
            cooldownBarsRemaining = 0;
            sessionStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            lastProcessedClosedTradeCount = SystemPerformance.AllTrades.Count;
            rthGapCheckDone = false;

            activeEntrySignal = null;
            activeIsBounce = false;
            entryPrice = 0;
            initialRiskTicks = 0;
            partialTargetTicks = 0;
            breakevenMoved = false;
            partialTaken = false;
            currentHardStop = 0;

            uptrendLine = null;
            downtrendLine = null;
            pendingBreakDir = 0;
            pendingBreakBar = -1;
            pendingBreakAnchorBar = -1;
            pendingBreakAnchorPrice = 0;
            pendingBreakSlope = 0;
            consumedLineKeys.Clear();
            touchLedger.Clear();

            pivotHighs.Clear();
            pivotLows.Clear();
            uptrendSegments.Clear();
            downtrendSegments.Clear();
        }

        private void DetectConfirmedSwing()
        {
            int i = CurrentBar - SwingStrength;
            if (i < SwingStrength)
                return;

            double candidateHigh = AbsHigh(i);
            double candidateLow = AbsLow(i);
            bool isPivotHigh = true;
            bool isPivotLow = true;

            for (int j = i - SwingStrength; j <= i + SwingStrength; j++)
            {
                if (j < 0 || j > CurrentBar || j == i)
                    continue;

                if (AbsHigh(j) >= candidateHigh)
                    isPivotHigh = false;
                if (AbsLow(j) <= candidateLow)
                    isPivotLow = false;

                if (!isPivotHigh && !isPivotLow)
                    break;
            }

            if (isPivotHigh)
            {
                var sp = new SwingPoint(i, candidateHigh, AbsTime(i), true);
                AddSwing(pivotHighs, sp);
                TryChainDowntrend(sp);
            }

            if (isPivotLow)
            {
                var sp = new SwingPoint(i, candidateLow, AbsTime(i), false);
                AddSwing(pivotLows, sp);
                TryChainUptrend(sp);
            }
        }

        private void AddSwing(List<SwingPoint> list, SwingPoint swing)
        {
            if (list.Count > 0)
            {
                SwingPoint last = list[list.Count - 1];
                if (Math.Abs((swing.Price - last.Price) / TickSize) < MinSwingDiffTicks)
                    return;
                if (swing.BarIndex <= last.BarIndex)
                    return;
            }

            list.Add(swing);
            while (list.Count > MaxSwingLookback)
                list.RemoveAt(0);
        }

        private void RebuildTrendlines()
        {
            // Remove invalidated chained segments (body-cross rule).
            PruneInvalidSegments();

            // Primary tradable rays: latest valid chained segment if available, else fall back to scored search.
            TrendLineModel up = uptrendSegments.Count > 0 ? uptrendSegments[uptrendSegments.Count - 1] : BuildUptrendLine();
            TrendLineModel dn = downtrendSegments.Count > 0 ? downtrendSegments[downtrendSegments.Count - 1] : BuildDowntrendLine();

            RestoreTouches(up);
            RestoreTouches(dn);

            uptrendLine = ChooseStableLine(uptrendLine, up);
            downtrendLine = ChooseStableLine(downtrendLine, dn);

            if (uptrendLine != null && consumedLineKeys.Contains(uptrendLine.Key))
                uptrendLine.IsConsumed = true;
            if (downtrendLine != null && consumedLineKeys.Contains(downtrendLine.Key))
                downtrendLine.IsConsumed = true;
        }

        private void RestoreTouches(TrendLineModel line)
        {
            if (line == null)
                return;
            if (touchLedger.TryGetValue(line.Key, out List<int> bars) && bars != null && bars.Count > 0)
                line.TouchBars = new List<int>(bars);
        }

        private TrendLineModel ChooseStableLine(TrendLineModel current, TrendLineModel candidate)
        {
            if (candidate == null)
                return null;

            if (current == null)
                return candidate;

            // If current became invalid/consumed, allow replacement.
            if (!current.IsValid || current.IsConsumed)
                return candidate;

            // If the key is unchanged, keep current (it has live TouchBars that will be updated this bar).
            if (current.Key == candidate.Key)
                return current;

            int curTouches = current.TouchBars != null ? current.TouchBars.Count : 0;
            int candTouches = candidate.TouchBars != null ? candidate.TouchBars.Count : 0;

            // Once a line is "mature" (meets MinTouchCount), do not swap it out unless the candidate is strictly better.
            if (curTouches >= MinTouchCount)
            {
                return candTouches > curTouches ? candidate : current;
            }

            // Before maturity: require a meaningful improvement to switch.
            if (candTouches >= curTouches + 1)
                return candidate;

            return current;
        }

        private TrendLineModel BuildUptrendLine()
        {
            if (pivotLows.Count < 2)
                return null;

            TrendLineModel best = null;
            double bestScore = double.NegativeInfinity;

            for (int b = pivotLows.Count - 1; b >= 1; b--)
            {
                for (int a = b - 1; a >= 0; a--)
                {
                    SwingPoint A = pivotLows[a];
                    SwingPoint B = pivotLows[b];
                    if (B.Price <= A.Price)
                        continue;

                    TrendLineModel line = new TrendLineModel(true, A, B);
                    if (!IsSlopeValid(line))
                        continue;

                    line.IsValid = ValidateZeroIntersection(line, A.BarIndex, CurrentBar);
                    if (!line.IsValid)
                        continue;

                    // incorporate persisted touches if we have them
                    RestoreTouches(line);

                    double score = ScoreTrendlineCandidate(line);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = line;
                    }
                }
            }

            return best;
        }

        private TrendLineModel BuildDowntrendLine()
        {
            if (pivotHighs.Count < 2)
                return null;

            TrendLineModel best = null;
            double bestScore = double.NegativeInfinity;

            for (int b = pivotHighs.Count - 1; b >= 1; b--)
            {
                for (int a = b - 1; a >= 0; a--)
                {
                    SwingPoint A = pivotHighs[a];
                    SwingPoint B = pivotHighs[b];
                    if (B.Price >= A.Price)
                        continue;

                    TrendLineModel line = new TrendLineModel(false, A, B);
                    if (!IsSlopeValid(line))
                        continue;

                    line.IsValid = ValidateZeroIntersection(line, A.BarIndex, CurrentBar);
                    if (!line.IsValid)
                        continue;

                    RestoreTouches(line);

                    double score = ScoreTrendlineCandidate(line);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = line;
                    }
                }
            }

            return best;
        }

        private double ScoreTrendlineCandidate(TrendLineModel line)
        {
            if (line == null)
                return double.NegativeInfinity;

            int touches = line.TouchBars != null ? line.TouchBars.Count : 0;
            int span = Math.Max(0, line.B.BarIndex - line.A.BarIndex);
            int age = Math.Max(0, CurrentBar - line.A.BarIndex);

            if (PreferDominantTrendline)
                return touches * 1000000.0 + span * 1000.0 + age;
            return touches * 1000000.0 + span * 1000.0 - age;
        }

        private void TryChainDowntrend(SwingPoint newHigh)
        {
            // Need at least one prior confirmed pivot high.
            if (pivotHighs.Count < 2)
                return;

            SwingPoint prev = pivotHighs[pivotHighs.Count - 2];
            SwingPoint cur = newHigh;

            // Downtrend continuation requires lower high.
            if (cur.Price >= prev.Price)
                return;

            TrendLineModel seg = new TrendLineModel(false, prev, cur);
            if (!IsSlopeValid(seg))
                return;

            seg.IsValid = ValidateZeroIntersection(seg, prev.BarIndex, CurrentBar);
            if (!seg.IsValid)
                return;

            RestoreTouches(seg);
            downtrendSegments.Add(seg);
            if (EnableLogs)
                Log2($"[CHAIN] DN seg {prev.BarIndex}->{cur.BarIndex} key={seg.Key}");
        }

        private void TryChainUptrend(SwingPoint newLow)
        {
            if (pivotLows.Count < 2)
                return;

            SwingPoint prev = pivotLows[pivotLows.Count - 2];
            SwingPoint cur = newLow;

            // Uptrend continuation requires higher low.
            if (cur.Price <= prev.Price)
                return;

            TrendLineModel seg = new TrendLineModel(true, prev, cur);
            if (!IsSlopeValid(seg))
                return;

            seg.IsValid = ValidateZeroIntersection(seg, prev.BarIndex, CurrentBar);
            if (!seg.IsValid)
                return;

            RestoreTouches(seg);
            uptrendSegments.Add(seg);
            if (EnableLogs)
                Log2($"[CHAIN] UP seg {prev.BarIndex}->{cur.BarIndex} key={seg.Key}");
        }

        private void PruneInvalidSegments()
        {
            // Remove any segment that is invalidated by body-cross on the current bar.
            if (uptrendSegments.Count > 0)
                uptrendSegments.RemoveAll(l => l == null || !ValidateZeroIntersection(l, CurrentBar, CurrentBar));
            if (downtrendSegments.Count > 0)
                downtrendSegments.RemoveAll(l => l == null || !ValidateZeroIntersection(l, CurrentBar, CurrentBar));
        }

        private bool IsSlopeValid(TrendLineModel line)
        {
            double slopeTicksPerBar = line.Slope / TickSize;
            if (line.IsUptrend && slopeTicksPerBar <= 0)
                return false;
            if (!line.IsUptrend && slopeTicksPerBar >= 0)
                return false;
            if (Math.Abs(slopeTicksPerBar) < 0.01)
                return false;
            return true;
        }

        private void ValidateTrendlines()
        {
            if (uptrendLine != null)
                uptrendLine.IsValid = IsSlopeValid(uptrendLine) && ValidateZeroIntersection(uptrendLine, uptrendLine.A.BarIndex, CurrentBar);
            if (downtrendLine != null)
                downtrendLine.IsValid = IsSlopeValid(downtrendLine) && ValidateZeroIntersection(downtrendLine, downtrendLine.A.BarIndex, CurrentBar);
        }

        // Invalidation rule (Tori-style): a line is invalid if it crosses the candle BODY.
        // Applies to any line we draw (up or down). Strict/inclusive: touching body edge counts as invalid.
        private bool ValidateZeroIntersection(TrendLineModel line, int fromBar, int toBar)
        {
            if (line == null)
                return false;

            int start = Math.Max(0, fromBar);
            int end = Math.Min(CurrentBar, toBar);
            for (int b = start; b <= end; b++)
            {
                double lv = line.ValueAtBar(b);
                double o = AbsOpen(b);
                double c = AbsClose(b);
                double bodyLow = Math.Min(o, c);
                double bodyHigh = Math.Max(o, c);

                if (lv >= bodyLow && lv <= bodyHigh)
                    return false;
            }
            return true;
        }

        private void UpdateTouches(TrendLineModel line)
        {
            if (line == null || !line.IsValid || line.IsConsumed)
                return;

            double lv = line.ValueAtBar(CurrentBar);

            // Touch counting tolerance (used to build confidence/MinTouchCount).
            // This is intentionally looser than entry logic. A "touch" is counted if price comes within tolerance ticks of the line.
            double tol = TouchCountToleranceTicks * TickSize;
            bool touch = line.IsUptrend
                ? (Low[0] <= lv + tol)
                : (High[0] >= lv - tol);

            if (!touch)
                return;

            if (line.LastTouchBar >= 0 && CurrentBar - line.LastTouchBar < MinBarsBetweenTouches)
                return;

            int from = line.LastTouchBar >= 0 ? line.LastTouchBar + 1 : line.A.BarIndex;
            if (!ValidateZeroIntersection(line, from, CurrentBar))
                return;

            line.TouchBars.Add(CurrentBar);

            // Persist touch history keyed by line.Key so we don't reset to touch#1 on object rebuild.
            if (!touchLedger.TryGetValue(line.Key, out List<int> ledgerBars) || ledgerBars == null)
            {
                ledgerBars = new List<int>();
                touchLedger[line.Key] = ledgerBars;
            }
            if (ledgerBars.Count == 0 || ledgerBars[ledgerBars.Count - 1] != CurrentBar)
                ledgerBars.Add(CurrentBar);

            if (EnableLogs)
                Log2($"[TOUCH] {(line.IsUptrend ? "UP" : "DN")} line key={line.Key} touch#{line.TouchBars.Count} at bar {CurrentBar}");
        }

        private void DetectBreakSignal()
        {
            bool longBreak = IsValidBreak(downtrendLine, +1);
            bool shortBreak = IsValidBreak(uptrendLine, -1);

            // avoid ambiguous dual-break bars
            if (longBreak && shortBreak)
                return;

            if (longBreak)
            {
                pendingBreakDir = +1;
                pendingBreakBar = CurrentBar;
                pendingBreakAnchorBar = downtrendLine.A.BarIndex;
                pendingBreakAnchorPrice = downtrendLine.A.Price;
                pendingBreakSlope = downtrendLine.Slope;
                consumedLineKeys.Add(downtrendLine.Key);
                downtrendLine.IsConsumed = true;

                if (EnableLogs)
                    Log2($"[BREAK] LONG break at bar {CurrentBar}, line={downtrendLine.Key}");
            }
            else if (shortBreak)
            {
                pendingBreakDir = -1;
                pendingBreakBar = CurrentBar;
                pendingBreakAnchorBar = uptrendLine.A.BarIndex;
                pendingBreakAnchorPrice = uptrendLine.A.Price;
                pendingBreakSlope = uptrendLine.Slope;
                consumedLineKeys.Add(uptrendLine.Key);
                uptrendLine.IsConsumed = true;

                if (EnableLogs)
                    Log2($"[BREAK] SHORT break at bar {CurrentBar}, line={uptrendLine.Key}");
            }
        }

        private bool IsValidBreak(TrendLineModel line, int dir)
        {
            if (line == null || !line.IsValid || line.IsConsumed)
                return false;
            if (line.TouchBars.Count < MinTouchCount)
                return false;
            if (line.FirstTouchBar < 0)
                return false;
            if (CurrentBar - line.FirstTouchBar < MinBarsFromFirstTouch)
                return false;

            double lv = line.ValueAtBar(CurrentBar);
            if (dir > 0)
                return Close[0] > lv;
            return Close[0] < lv;
        }

        private void TryEnterFromPendingBreak()
        {
            if (pendingBreakDir == 0)
                return;

            // expiry
            if (CurrentBar - pendingBreakBar > MaxRetestWaitBars)
            {
                if (EnableLogs)
                    Log2("[PENDING] expired waiting for retest");
                ClearPendingBreak();
                return;
            }

            if (!WaitForRetest)
            {
                // next bar after break
                if (CurrentBar == pendingBreakBar + 1)
                    TryEnterBreakNow("Immediate");
                return;
            }

            // retest mode (never on break bar itself)
            if (CurrentBar <= pendingBreakBar)
                return;

            double lineNow = PendingBreakLineValueAt(CurrentBar);
            double zone = RetestZoneTicks * TickSize;
            bool touched = pendingBreakDir > 0
                ? (Low[0] <= lineNow + zone)
                : (High[0] >= lineNow - zone);

            if (!touched)
                return;

            bool held = pendingBreakDir > 0
                ? (Close[0] > lineNow)
                : (Close[0] < lineNow);

            if (!held)
            {
                if (EnableLogs)
                    Log2("[PENDING] retest failed");
                ClearPendingBreak();
                return;
            }

            TryEnterBreakNow("Retest");
        }

        private void TryEnterBreakNow(string mode)
        {
            TrendLineModel safety = GetSafetyLineForDir(pendingBreakDir);
            if (!PreEntryRiskGate(pendingBreakDir, false, safety, out double estRiskTicks, out double rr, out double stopPx, out double targetTicks, out string failReason))
            {
                if (EnableLogs)
                    Log2($"[SKIP] Break {mode} failed risk gate: {failReason}");
                ClearPendingBreak();
                return;
            }

            initialRiskTicks = estRiskTicks;
            partialTargetTicks = targetTicks;

            if (pendingBreakDir > 0)
                EnterLong(DefaultQuantity, "BreakLong");
            else
                EnterShort(DefaultQuantity, "BreakShort");

            submittedEntryThisBar = true;

            // hard stop (disaster stop) buffered beyond safety
            currentHardStop = stopPx;
            SetStopForSignal(pendingBreakDir > 0 ? "BreakLong" : "BreakShort", currentHardStop);

            if (EnableLogs)
                Log2($"[ENTRY-ARMED] Break {mode} dir={(pendingBreakDir > 0 ? "LONG" : "SHORT")}, riskTicks={estRiskTicks:0.0}, rr={rr:0.00}, stop={stopPx:0.00}");

            ClearPendingBreak();
        }

        private void TryBounceEntry()
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;
            if (pendingBreakDir != 0)
                return;
            if (submittedEntryThisBar)
                return;

            // long bounce on uptrend support
            if (IsValidBounce(uptrendLine, +1))
            {
                TrendLineModel safety = downtrendLine;
                if (PreEntryRiskGate(+1, true, safety, out double estRiskTicks, out double rr, out double stopPx, out double targetTicks, out string failReasonLong))
                {
                    initialRiskTicks = estRiskTicks;
                    partialTargetTicks = targetTicks;
                    EnterLong(DefaultQuantity, "BounceLong");
                    currentHardStop = stopPx;
                    SetStopForSignal("BounceLong", currentHardStop);
                    consumedLineKeys.Add(uptrendLine.Key);
                    uptrendLine.IsConsumed = true;
                    submittedEntryThisBar = true;

                    if (EnableLogs)
                        Log2($"[ENTRY-ARMED] Bounce LONG riskTicks={estRiskTicks:0.0}, rr={rr:0.00}, stop={stopPx:0.00}");

                    return;
                }
                else if (EnableLogs)
                {
                    Log2($"[SKIP] Bounce LONG failed risk gate: {failReasonLong}");
                }
            }

            // short bounce on downtrend resistance
            if (!submittedEntryThisBar && Position.MarketPosition == MarketPosition.Flat && IsValidBounce(downtrendLine, -1))
            {
                TrendLineModel safety = uptrendLine;
                if (PreEntryRiskGate(-1, true, safety, out double estRiskTicks, out double rr, out double stopPx, out double targetTicks, out string failReasonShort))
                {
                    initialRiskTicks = estRiskTicks;
                    partialTargetTicks = targetTicks;
                    EnterShort(DefaultQuantity, "BounceShort");
                    currentHardStop = stopPx;
                    SetStopForSignal("BounceShort", currentHardStop);
                    consumedLineKeys.Add(downtrendLine.Key);
                    downtrendLine.IsConsumed = true;
                    submittedEntryThisBar = true;

                    if (EnableLogs)
                        Log2($"[ENTRY-ARMED] Bounce SHORT riskTicks={estRiskTicks:0.0}, rr={rr:0.00}, stop={stopPx:0.00}");

                    return;
                }
                else if (EnableLogs)
                {
                    Log2($"[SKIP] Bounce SHORT failed risk gate: {failReasonShort}");
                }
            }
        }

        private bool IsValidBounce(TrendLineModel line, int dir)
        {
            if (line == null || !line.IsValid || line.IsConsumed)
                return false;
            if (line.TouchBars.Count < MinTouchCount)
                return false;
            if (line.FirstTouchBar < 0 || CurrentBar - line.FirstTouchBar < MinBarsFromFirstTouch)
                return false;

            double lv = line.ValueAtBar(CurrentBar);
            double zone = TouchZoneTicks * TickSize;

            if (dir > 0)
                return Low[0] <= lv + zone && Close[0] >= lv;
            return High[0] >= lv - zone && Close[0] <= lv;
        }

        private TrendLineModel GetSafetyLineForDir(int dir)
        {
            return dir > 0 ? uptrendLine : downtrendLine;
        }

        private bool PreEntryRiskGate(int dir, bool isBounce, TrendLineModel safetyLine,
            out double estRiskTicks, out double rr, out double hardStopPrice, out double targetTicks, out string failReason)
        {
            estRiskTicks = 0;
            rr = 0;
            hardStopPrice = 0;
            targetTicks = 0;
            failReason = "UNKNOWN";

            if (!CanOpenNewTrade())
            {
                failReason = "CAN_OPEN_NEW_TRADE_FALSE";
                return false;
            }

            if (UseHTFFilter)
            {
                if (dir > 0 && htfBias <= 0)
                {
                    if (EnableLogs) Log2($"[RISK] HTF_BIAS_BLOCK_LONG htfBias={htfBias}");
                    failReason = "HTF_BIAS_BLOCK_LONG";
                    return false;
                }
                if (dir < 0 && htfBias >= 0)
                {
                    if (EnableLogs) Log2($"[RISK] HTF_BIAS_BLOCK_SHORT htfBias={htfBias}");
                    failReason = "HTF_BIAS_BLOCK_SHORT";
                    return false;
                }
            }

            if (!IsVolatilityOk())
            {
                failReason = "VOLATILITY_FILTER";
                return false;
            }

            if (UseNewsBlackout && IsInNewsBlackout())
            {
                failReason = "NEWS_BLACKOUT";
                return false;
            }

            bool safetyOk = !(safetyLine == null || !safetyLine.IsValid || safetyLine.B.BarIndex <= safetyLine.A.BarIndex);

            // Safety line is a hard rule for breaks. For bounces, allow an ATR-only fallback if configured.
            if (!safetyOk)
            {
                bool allowBounceFallback = isBounce && !RequireSafetyLineForBounce;
                if (!allowBounceFallback)
                {
                    if (EnableLogs)
                    {
                        string expected = dir > 0 ? "UP" : "DN";
                        string up = uptrendLine == null ? "null" : $"key={uptrendLine.Key} valid={uptrendLine.IsValid} consumed={uptrendLine.IsConsumed} touches={(uptrendLine.TouchBars != null ? uptrendLine.TouchBars.Count : 0)}";
                        string dn = downtrendLine == null ? "null" : $"key={downtrendLine.Key} valid={downtrendLine.IsValid} consumed={downtrendLine.IsConsumed} touches={(downtrendLine.TouchBars != null ? downtrendLine.TouchBars.Count : 0)}";
                        Log2($"[RISK] SAFETY_LINE_INVALID expected={expected} safety={(safetyLine==null?"null":safetyLine.Key)} | up={up} | dn={dn}");
                    }
                    failReason = "SAFETY_LINE_INVALID";
                    return false;
                }
            }

            // estimate entry at next bar open ~ Close[0]
            double entry = Close[0];
            double safetyValue = safetyOk ? safetyLine.ValueAtBar(CurrentBar) : double.NaN;

            // logical stop anchor
            double logicalStop;
            if (isBounce)
            {
                // bounce stop uses action line + buffer
                TrendLineModel action = dir > 0 ? uptrendLine : downtrendLine;
                if (action == null || !action.IsValid) { failReason = "ACTION_LINE_INVALID"; return false; }
                double actionVal = action.ValueAtBar(CurrentBar);
                logicalStop = dir > 0
                    ? actionVal - BounceStopBufferTicks * TickSize
                    : actionVal + BounceStopBufferTicks * TickSize;
            }
            else
            {
                // break trades anchor stop to safety line (required)
                logicalStop = safetyValue;
            }

            // hard stop = buffered beyond logical stop
            hardStopPrice = dir > 0
                ? logicalStop - HardStopBufferTicks * TickSize
                : logicalStop + HardStopBufferTicks * TickSize;

            double maxStopPrice = dir > 0
                ? entry - HardStopMaxTicks * TickSize
                : entry + HardStopMaxTicks * TickSize;

            // cap hard stop max distance
            if (dir > 0 && hardStopPrice < maxStopPrice)
                hardStopPrice = maxStopPrice;
            if (dir < 0 && hardStopPrice > maxStopPrice)
                hardStopPrice = maxStopPrice;

            estRiskTicks = dir > 0
                ? (entry - hardStopPrice) / TickSize
                : (hardStopPrice - entry) / TickSize;

            if (estRiskTicks <= 0)
            {
                failReason = "NON_POSITIVE_RISK";
                return false;
            }

            if (estRiskTicks > MaxSafetyStopTicks)
            {
                failReason = "STOP_TOO_WIDE";
                return false;
            }

            // dollar risk gate
            double riskDollars = estRiskTicks * Instrument.MasterInstrument.PointValue * TickSize * DefaultQuantity;
            if (riskDollars > MaxRiskDollarsPerTrade)
            {
                failReason = "RISK_DOLLARS_EXCEEDED";
                return false;
            }

            // reward estimate: channel height vs ATR target
            TrendLineModel actionLine = dir > 0 ? downtrendLine : uptrendLine;
            if (actionLine == null)
            {
                // For bounce fallback (no safety line), allow ATR-only target even if the opposite line is missing.
                bool allowAtrOnlyTarget = isBounce && !RequireSafetyLineForBounce;
                if (!allowAtrOnlyTarget)
                {
                    failReason = "NO_ACTION_LINE";
                    return false;
                }
            }

            double atrTicks = atr2m[0] / TickSize;
            double channelHeightTicks = (safetyOk && actionLine != null)
                ? Math.Abs(actionLine.ValueAtBar(CurrentBar) - safetyLine.ValueAtBar(CurrentBar)) / TickSize
                : 0.0;

            // If safety line is missing (bounce fallback), use ATR-only target.
            targetTicks = Math.Max(channelHeightTicks, atrTicks * TargetATRMultiplier);
            if (targetTicks < 1)
            {
                failReason = "TARGET_TOO_SMALL";
                return false;
            }

            rr = targetTicks / estRiskTicks;
            if (rr < MinRiskRewardRatio)
            {
                failReason = "RR_BELOW_MIN";
                return false;
            }

            failReason = "OK";
            return true;
        }

        private void ManageOpenPosition()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            int dir = Position.MarketPosition == MarketPosition.Long ? +1 : -1;
            TrendLineModel safety = GetSafetyLineForDir(dir);
            bool safetyOk = !(safety == null || !safety.IsValid);

            // If configured, allow bounce trades to continue without a safety line (hard stop + BE + partial only).
            if (!safetyOk)
            {
                bool allowBounceNoSafety = activeIsBounce && !RequireSafetyLineForBounce;
                if (!allowBounceNoSafety)
                {
                    if (EnableLogs)
                        Log2("[EXIT] Safety line missing/invalid while in trade -> flatten");
                    if (dir > 0) ExitLong("SafetyMissing", activeEntrySignal);
                    else ExitShort("SafetyMissing", activeEntrySignal);
                    return;
                }
                if (EnableLogs)
                    Log2("[WARN] Safety line missing for bounce trade; managing with hard stop only.");
            }

            double safetyValue = safetyOk ? safety.ValueAtBar(CurrentBar) : double.NaN;
            double close = Close[0];
            double unrealizedTicks = dir > 0 ? (close - entryPrice) / TickSize : (entryPrice - close) / TickSize;

            // 1) Tori primary exit: candle close through safety line
            if (safetyOk && ((dir > 0 && close < safetyValue) || (dir < 0 && close > safetyValue)))
            {
                if (EnableLogs)
                    Log2($"[EXIT] SafetyLineViolation close={close:0.00} safety={safetyValue:0.00}");
                if (dir > 0) ExitLong("SafetyLineViolation", activeEntrySignal);
                else ExitShort("SafetyLineViolation", activeEntrySignal);
                return;
            }

            // 2) Move hard stop to breakeven at 1:1
            if (!breakevenMoved && initialRiskTicks > 0 && unrealizedTicks >= initialRiskTicks)
            {
                double beStop = dir > 0
                    ? entryPrice + BreakevenBufferTicks * TickSize
                    : entryPrice - BreakevenBufferTicks * TickSize;

                if ((dir > 0 && beStop > currentHardStop) || (dir < 0 && beStop < currentHardStop) || currentHardStop == 0)
                {
                    currentHardStop = beStop;
                    SetStopForSignal(activeEntrySignal, currentHardStop);
                }

                breakevenMoved = true;
                if (EnableLogs)
                    Log2($"[RISK] Move to BE stop={currentHardStop:0.00}");
            }

            // 3) Partial at first target
            bool singleContractMode = Position.Quantity < 2;
            bool canPartial = !partialTaken && !singleContractMode && PartialExitPct > 0 && unrealizedTicks >= partialTargetTicks;
            if (canPartial)
            {
                int qtyToExit = Math.Max(1, (int)Math.Round(Position.Quantity * (PartialExitPct / 100.0), MidpointRounding.AwayFromZero));
                qtyToExit = Math.Min(qtyToExit, Position.Quantity - 1);

                if (qtyToExit > 0)
                {
                    if (dir > 0)
                        ExitLong(qtyToExit, "PartialExit", activeEntrySignal);
                    else
                        ExitShort(qtyToExit, "PartialExit", activeEntrySignal);

                    partialTaken = true;

                    // lock profit after partial
                    double lockStop = dir > 0
                        ? entryPrice + PartialLockTicks * TickSize
                        : entryPrice - PartialLockTicks * TickSize;

                    if ((dir > 0 && lockStop > currentHardStop) || (dir < 0 && lockStop < currentHardStop))
                    {
                        currentHardStop = lockStop;
                        SetStopForSignal(activeEntrySignal, currentHardStop);
                    }

                    if (EnableLogs)
                        Log2($"[PARTIAL] qty={qtyToExit}, lockStop={currentHardStop:0.00}");
                }
            }

            if (!partialTaken && singleContractMode && unrealizedTicks >= partialTargetTicks)
            {
                partialTaken = true;
                if (EnableLogs)
                    Log2("[PARTIAL] Single-contract mode: skipping scale-out, continuing with trail-only management.");
            }

            // 4) Trail hard stop along safety with buffer (ratchet only)
            if (safetyOk)
            {
                double trailStop = dir > 0
                    ? safetyValue - HardStopBufferTicks * TickSize
                    : safetyValue + HardStopBufferTicks * TickSize;

                if ((dir > 0 && trailStop > currentHardStop) || (dir < 0 && trailStop < currentHardStop) || currentHardStop == 0)
                {
                    currentHardStop = trailStop;
                    SetStopForSignal(activeEntrySignal, currentHardStop);
                }
            }

            // 5) Session close flatten
            if (ToTime(Time[0]) >= SessionClose)
            {
                if (dir > 0) ExitLong("SessionClose", activeEntrySignal);
                else ExitShort("SessionClose", activeEntrySignal);
            }

            // 6) Daily loss kill switch
            if (GetDailyPnl() <= -Math.Abs(MaxDailyLossDollars))
            {
                if (EnableLogs)
                    Log2("[KILL] Daily loss limit hit, flattening and halting entries.");
                if (dir > 0) ExitLong("DailyLossLimit", activeEntrySignal);
                else ExitShort("DailyLossLimit", activeEntrySignal);
            }
        }
        #endregion

        #region Filters/helpers
        // Log helper. Some NT8 builds do not expose OutputTab2 routing APIs.
        // We keep a single logging path that always compiles: Print() with a strategy prefix.
        private void Log2(string msg)
        {
            // Prefix so Output window filtering is easy even when multiple strategies are running.
            Print($"[ESTrendline_v1] {msg}");
        }

        private void ProcessClosedTradesForCooldown()
        {
            int closedCount = SystemPerformance.AllTrades.Count;
            if (closedCount <= lastProcessedClosedTradeCount)
                return;

            for (int i = lastProcessedClosedTradeCount; i < closedCount; i++)
            {
                Trade tr = SystemPerformance.AllTrades[i];
                if (tr == null)
                    continue;

                DateTime exitTime = tr.Exit != null ? tr.Exit.Time : Time[0];
                if (exitTime.Date != Time[0].Date)
                    continue;

                double pnl = tr.ProfitCurrency;
                if (pnl < 0)
                {
                    cooldownBarsRemaining = Math.Max(cooldownBarsRemaining, CooldownBarsAfterLoss);
                    if (EnableLogs)
                        Log2($"[COOLDOWN] Loss trade detected (PnL={pnl:0.00}, exit={exitTime}). Cooldown set to {cooldownBarsRemaining} bars.");
                }
            }

            lastProcessedClosedTradeCount = closedCount;
        }

        private void HandleRthOpeningGapInvalidation()
        {
            if (rthGapCheckDone)
                return;

            int t = ToTime(Time[0]);
            if (t < SessionStart)
                return;

            // Only run on/near the RTH open. If the strategy is enabled mid-day, do NOT gap-invalidate lines.
            if (!IsInFirstMinutesFromSessionStart(Time[0], 5))
                return;

            // Run once when we first enter RTH window.
            rthGapCheckDone = true;
            double openPx = Open[0];

            if (uptrendLine != null && uptrendLine.IsValid)
            {
                double upVal = uptrendLine.ValueAtBar(CurrentBar);
                if (openPx < upVal)
                {
                    uptrendLine.IsValid = false;
                    uptrendLine.IsConsumed = true;
                    consumedLineKeys.Add(uptrendLine.Key);
                    if (EnableLogs)
                        Log2($"[GAP] RTH open gapped below uptrend line. Invalidating {uptrendLine.Key}");
                }
            }

            if (downtrendLine != null && downtrendLine.IsValid)
            {
                double dnVal = downtrendLine.ValueAtBar(CurrentBar);
                if (openPx > dnVal)
                {
                    downtrendLine.IsValid = false;
                    downtrendLine.IsConsumed = true;
                    consumedLineKeys.Add(downtrendLine.Key);
                    if (EnableLogs)
                        Log2($"[GAP] RTH open gapped above downtrend line. Invalidating {downtrendLine.Key}");
                }
            }
        }

        private bool CanOpenNewTrade()
        {
            int t = ToTime(Time[0]);
            if (t < SessionStart || t > SessionEndNoNewEntries)
                return false;

            if (Position.MarketPosition != MarketPosition.Flat)
                return false;

            if (tradesThisSession >= MaxTradesPerSession)
                return false;

            if (cooldownBarsRemaining > 0)
                return false;

            if (GetDailyPnl() <= -Math.Abs(MaxDailyLossDollars))
                return false;

            // first 5 minutes open warmup
            if (IsInFirstMinutesFromSessionStart(Time[0], 5))
                return false;

            return true;
        }

        private void UpdateHtfBias()
        {
            if (CurrentBars[1] < HTFEmaPeriod + HTFSlopeLookback + 2)
            {
                htfBias = 0;
                return;
            }

            double now = ema15m[0];
            double prev = ema15m[HTFSlopeLookback];
            double slope = now - prev;

            if (slope > HTFSlopeThreshold)
                htfBias = +1;
            else if (slope < -HTFSlopeThreshold)
                htfBias = -1;
            else
                htfBias = 0;
        }

        private bool IsVolatilityOk()
        {
            if (CurrentBar < ATRPeriod + 2)
                return false;

            double atrTicks = atr2m[0] / TickSize;
            if (double.IsNaN(atrTicks) || atrTicks <= 0)
                return false;

            bool ok = atrTicks >= MinATRTicks && atrTicks <= MaxATRTicks;
            if (!ok && EnableLogs)
                Log2($"[FILTER] VOLATILITY_FILTER atrTicks={atrTicks:0.0} min={MinATRTicks} max={MaxATRTicks}");
            return ok;
        }

        private bool IsInNewsBlackout()
        {
            // Time[] assumed in chart/session timezone. These windows are approximations.
            int t = ToTime(Time[0]);

            // 8:30 ET major releases (CPI, NFP, etc.)
            int n = NewsBlackoutMinutes;
            int pre830 = 83000 - n * 100;
            int post830 = 83000 + n * 100;
            if (t >= pre830 && t <= post830)
                return true;

            // 14:00 ET FOMC statement window
            int pre200 = 140000 - n * 100;
            int post200 = 140000 + n * 100;
            if (t >= pre200 && t <= post200)
                return true;

            return false;
        }

        private double GetDailyPnl()
        {
            double cum = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            return cum - sessionStartCumProfit;
        }

        private bool IsInFirstMinutesFromSessionStart(DateTime barTime, int minutes)
        {
            TimeSpan start = SessionIntToTimeSpan(SessionStart);
            TimeSpan tod = barTime.TimeOfDay;
            return tod >= start && tod < start.Add(TimeSpan.FromMinutes(minutes));
        }

        private TimeSpan SessionIntToTimeSpan(int hhmmss)
        {
            int h = hhmmss / 10000;
            int m = (hhmmss % 10000) / 100;
            int s = hhmmss % 100;
            h = Math.Max(0, Math.Min(23, h));
            m = Math.Max(0, Math.Min(59, m));
            s = Math.Max(0, Math.Min(59, s));
            return new TimeSpan(h, m, s);
        }

        private double PendingBreakLineValueAt(int absBar)
        {
            if (pendingBreakAnchorBar < 0)
                return double.NaN;
            return pendingBreakAnchorPrice + pendingBreakSlope * (absBar - pendingBreakAnchorBar);
        }

        private void ClearPendingBreak()
        {
            pendingBreakDir = 0;
            pendingBreakBar = -1;
            pendingBreakAnchorBar = -1;
            pendingBreakAnchorPrice = 0;
            pendingBreakSlope = 0;
        }

        private void SetStopForSignal(string signal, double stopPrice)
        {
            // stop is updated per signal; managed approach
            SetStopLoss(signal, CalculationMode.Price, stopPrice, false);
        }
        #endregion

        #region Absolute-index accessors
        private int ToRel(int absBar)
        {
            return CurrentBar - absBar;
        }

        private double AbsHigh(int absBar)
        {
            int rel = ToRel(absBar);
            if (rel < 0 || rel > CurrentBar) return double.NaN;
            return High[rel];
        }

        private double AbsLow(int absBar)
        {
            int rel = ToRel(absBar);
            if (rel < 0 || rel > CurrentBar) return double.NaN;
            return Low[rel];
        }

        private double AbsOpen(int absBar)
        {
            int rel = ToRel(absBar);
            if (rel < 0 || rel > CurrentBar) return double.NaN;
            return Open[rel];
        }

        private double AbsClose(int absBar)
        {
            int rel = ToRel(absBar);
            if (rel < 0 || rel > CurrentBar) return double.NaN;
            return Close[rel];
        }

        private DateTime AbsTime(int absBar)
        {
            int rel = ToRel(absBar);
            if (rel < 0 || rel > CurrentBar) return Core.Globals.MinDate;
            return Time[rel];
        }
        #endregion

        #region Drawing
        private void DrawLines()
        {
            if (!ShowLinesOnChart)
                return;

            if (uptrendLine != null)
            {
                // Draw as a ray: anchor at A and extend to current bar using projected value.
                double yNow = uptrendLine.ValueAtBar(CurrentBar);
                Draw.Line(this, TagUp, false,
                    CurrentBar - uptrendLine.A.BarIndex, uptrendLine.A.Price,
                    0, yNow,
                    Brushes.LimeGreen, DashStyleHelper.Solid, 2);
            }

            if (downtrendLine != null)
            {
                double yNow = downtrendLine.ValueAtBar(CurrentBar);
                Draw.Line(this, TagDn, false,
                    CurrentBar - downtrendLine.A.BarIndex, downtrendLine.A.Price,
                    0, yNow,
                    Brushes.OrangeRed, DashStyleHelper.Solid, 2);
            }

            // safety line highlight while in trade
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                int dir = Position.MarketPosition == MarketPosition.Long ? +1 : -1;
                TrendLineModel safety = GetSafetyLineForDir(dir);
                if (safety != null)
                {
                    double yNow = safety.ValueAtBar(CurrentBar);
                    Draw.Line(this, TagSafety, false,
                        CurrentBar - safety.A.BarIndex, safety.A.Price,
                        0, yNow,
                        Brushes.Red, DashStyleHelper.Dash, 2);
                }
            }
        }
        #endregion
    }
}
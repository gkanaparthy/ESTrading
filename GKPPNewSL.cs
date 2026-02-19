#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using System.Xml.Serialization;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class GKPPNewSL : Strategy
    {
        // === Core variables for pivot levels and trading ===
        private double prevClose, prevHigh, prevLow;
        private double pLevel, r1Level, s1Level, r2Level, s2Level, r3Level, s3Level;
        private double pr1midLevel, r1r2midLevel, r2r3midLevel, ps1midLevel, s1s2midLevel, s2s3midLevel;
        private double lowband, highband, nextbandL, nextbandS, adjEntry;
        private bool lowbandLongFlag, highbandShortFlag;
        private double vwPrice;
        private double OrderDilowband, OrderDihighband, OrderDinextband;
        private double PrevDayPnL = 0, PrevDayTradeCount = 0;
        private double AccountRealizedPL, AccountUnrealizedPL;
        private bool dayOverVar = false;
        private DateTime orderTime;

        // Pivot tracking
        private List<double> levels;
        private double NowPivot = double.NaN;
        private double prevTouchedPivot = double.NaN;
        private double currRecordedPivot = double.NaN;

        // Track when each pivot was last touched
        private Dictionary<double, int> lastPivotTouchBarIndex = new Dictionary<double, int>();

        // Track band trades to avoid overtrading within the same band (optional, can be extended later)
        private Dictionary<double, int> lastBandTradeBarIndex = new Dictionary<double, int>();

        // VWAP indicators for chart display
        private VWAP1 ofVwapETH;

        // ENTRY-ORDER tracking from original skeleton
        private Order entryOrder = null;
        private Order wtdentryOrder = null;

        // EMA8/EMA21, SMA925, Session VWAP for visual and profit exits
        private EMA ema8;
        private EMA ema21;
        private SMA sma925;
        private VWAP1 sessionVwap;

        // Two-leg family order refs (PT & Trail)
        private Order orderPT_L;    // "L_PT"
        private Order orderTrail_L; // "L_Trail"
        private Order orderPT_S;    // "S_PT"
        private Order orderTrail_S; // "S_Trail"

        // State for long family
        private bool setupArmedLong;
        private bool trailActiveLong;
        private bool trailingEnabledLong;
        private double trailCurrentSL_Long = double.NaN;
        private double trailEntryPriceLong = double.NaN;

        // State for short family
        private bool setupArmedShort;
        private bool trailActiveShort;
        private bool trailingEnabledShort;
        private double trailCurrentSL_Short = double.NaN;
        private double trailEntryPriceShort = double.NaN;

        // Family outcome tracking
        private bool familyOpenLong = false;
        private bool familyOpenShort = false;
        private bool ptLegClosedLong = false;
        private bool trailLegClosedLong = false;
        private bool ptLegClosedShort = false;
        private bool trailLegClosedShort = false;
        private bool ptLegWasFullSL_Long = false;
        private bool trailLegWasFullSL_Long = false;
        private bool ptLegWasFullSL_Short = false;
        private bool trailLegWasFullSL_Short = false;

        private bool sessionTradingHalted = false;
        private int consecutiveFullLossFamilies = 0;
        private DateTime sessionDate = DateTime.MinValue;

        // === Parameters ===

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Trade size (contracts)", GroupName = "Parameters", Order = 0)]
        public int TradeSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use RTH (8:30-15:00)", GroupName = "Parameters", Order = 1)]
        public bool UseRTH { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Stop (ticks)", GroupName = "Risk", Order = 2)]
        public int StopTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Profit target (ticks) for PT leg", GroupName = "Risk", Order = 3)]
        public int ProfitTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Trail profit multiple (x StopTicks) for PT2", GroupName = "Risk", Order = 4)]
        public int TrailProfitMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable debug logs", GroupName = "Diagnostics", Order = 10)]
        public bool DebugLogs { get; set; }

        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Previous day Low", GroupName = "Parameters", Order = 20)]
        public double ManualLowPrice { get; set; }

        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Previous day High", GroupName = "Parameters", Order = 21)]
        public double ManualHighPrice { get; set; }

        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Previous day Settlement", GroupName = "Parameters", Order = 22)]
        public double ManualSettPrice { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
        [Display(Name = "Anchor VWAP from 1st", Order = 23, GroupName = "Parameters")]
        public DateTime AnchorFrom { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
        [Display(Name = "Anchor VWAP from 2nd", Order = 24, GroupName = "Parameters")]
        public DateTime AnchorFrom2 { get; set; }

        [Range(double.MinValue, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Max Loss", GroupName = "Parameters", Order = 25)]
        public double MaxLoss { get; set; }

        // signal names
        private const string SigPT_L = "L_PT";
        private const string SigTrail_L = "L_Trail";
        private const string SigPT_S = "S_PT";
        private const string SigTrail_S = "S_Trail";

        private void D(string msg) { if (DebugLogs) Print("[GKPP] " + msg); }

        protected override void OnMarketData(Data.MarketDataEventArgs marketDataUpdate)
        {
            if (marketDataUpdate.IsReset)
                prevClose = double.MinValue;
            else if (marketDataUpdate.MarketDataType == Data.MarketDataType.Settlement)
                prevClose = marketDataUpdate.Price;
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Pivot Bands Strategy with 2-leg SL/PT management (no EMA trailing).";
                Name = "GKPPNewSL";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 4;
                EntryHandling = EntryHandling.UniqueEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 3600;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                BarsRequiredToTrade = 50;

                TradeSize = 2;
                UseRTH = true;

                ManualSettPrice = 1;
                ManualLowPrice = 1;
                ManualHighPrice = 1;
                MaxLoss = -75;

                StopTicks = 5;
                ProfitTicks = 10;
                TrailProfitMultiple = 8;
                DebugLogs = true;

                // AnchorFrom logic similar to original GKPivot
                if (DateTime.Today.DayOfWeek == DayOfWeek.Monday)
                {
                    AnchorFrom = DateTime.Parse("12:30 AM");
                }
                else
                {
                    DateTime sunday = DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek);
                    AnchorFrom = new DateTime(
                        sunday.Year,
                        sunday.Month,
                        sunday.Day,
                        17,  // 5 PM
                        2,
                        0
                    );
                }
                AnchorFrom2 = DateTime.Parse("12:30 AM");

                // Pivot plots
                AddPlot(Brushes.GhostWhite, "S3Level");
                AddPlot(Brushes.GhostWhite, "S2S3MidLevel");
                AddPlot(Brushes.GhostWhite, "S2Level");
                AddPlot(Brushes.GhostWhite, "S1S2MidLevel");
                AddPlot(Brushes.GhostWhite, "S1Level");
                AddPlot(Brushes.GhostWhite, "PS1MidLevel");
                AddPlot(Brushes.Red, "PLevel");
                AddPlot(Brushes.GhostWhite, "PR1MidLevel");
                AddPlot(Brushes.GhostWhite, "R1Level");
                AddPlot(Brushes.GhostWhite, "R1R2MidLevel");
                AddPlot(Brushes.GhostWhite, "R2Level");
                AddPlot(Brushes.GhostWhite, "R2R3MidLevel");
                AddPlot(Brushes.GhostWhite, "R3Level");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(Data.BarsPeriodType.Minute, 1);
                AddDataSeries(Data.BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                // Main VWAP
                ofVwapETH = VWAP1(BarsArray[0],
                    new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true);
                AddChartIndicator(ofVwapETH);

                // EMA8 / EMA21 (kept for visual/reference)
                ema8 = EMA(8);
                ema21 = EMA(21);
                ema8.Plots[0].Brush = Brushes.DodgerBlue;
                ema8.Plots[0].Width = 2;
                ema21.Plots[0].Brush = Brushes.OrangeRed;
                ema21.Plots[0].Width = 2;
                AddChartIndicator(ema8);
                AddChartIndicator(ema21);

                // SMA925 + session VWAP (for profit exits)
                sma925 = SMA(925);
                sma925.Plots[0].Brush = Brushes.MediumPurple;

                sessionVwap = VWAP1(BarsArray[0],
                    new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true);
                sessionVwap.Plots[0].Brush = Brushes.Gold;
                AddChartIndicator(sessionVwap);

                ResetFamilyTracking();
                sessionTradingHalted = false;
                consecutiveFullLossFamilies = 0;
                sessionDate = DateTime.MinValue;

                trailCurrentSL_Long = double.NaN;
                trailCurrentSL_Short = double.NaN;
                trailEntryPriceLong = double.NaN;
                trailEntryPriceShort = double.NaN;

                setupArmedLong = setupArmedShort = false;
                trailActiveLong = trailActiveShort = false;
                trailingEnabledLong = trailingEnabledShort = false;
            }
        }

        private void ResetFamilyTracking()
        {
            familyOpenLong = familyOpenShort = false;
            ptLegClosedLong = trailLegClosedLong = false;
            ptLegClosedShort = trailLegClosedShort = false;
            ptLegWasFullSL_Long = trailLegWasFullSL_Long = false;
            ptLegWasFullSL_Short = trailLegWasFullSL_Short = false;
        }

        private void EnsureSessionResetIfNeeded()
        {
            DateTime curSessionDate = Time[0].Date;
            if (sessionDate == DateTime.MinValue)
                sessionDate = curSessionDate;

            if (curSessionDate != sessionDate)
            {
                sessionDate = curSessionDate;
                sessionTradingHalted = false;
                consecutiveFullLossFamilies = 0;
                ResetFamilyTracking();
                D("[Session] New session detected. Reset halt flag and counters.");
            }
        }

        private void EvaluateAndMaybeHaltAfterFamilyClose(bool wasBothFullStops)
        {
            if (wasBothFullStops)
            {
                consecutiveFullLossFamilies++;
                D($"[HaltCheck] Family ended with BOTH legs full {StopTicks}-tick stops. Streak={consecutiveFullLossFamilies}.");
            }
            else
            {
                if (consecutiveFullLossFamilies != 0)
                    D($"[HaltCheck] Family ended NOT as double full-stop; streak reset from {consecutiveFullLossFamilies} to 0.");
                consecutiveFullLossFamilies = 0;
            }

            if (consecutiveFullLossFamilies >= 3)
            {
                sessionTradingHalted = true;
                D("[HALT] Reached 3 consecutive full-loss families (6 full-stop losses). Halting trading for the session.");
            }
        }

        private bool TradeIsInProfit()
        {
            if (Position.MarketPosition == MarketPosition.Long)
                return Close[0] > Position.AveragePrice + TickSize;

            if (Position.MarketPosition == MarketPosition.Short)
                return Close[0] < Position.AveragePrice - TickSize;

            return false;
        }

        // Use SMA925 & session VWAP to flatten profitable trades
        private bool HitImportantLevelThisBar()
        {
            if (CurrentBar < 1)
                return false;
            if (sma925 == null || sessionVwap == null)
                return false;
            if (sma925.CurrentBar < 1 || sessionVwap.CurrentBar < 1)
                return false;

            double barLow = Low[0];
            double barHigh = High[0];

            double lvlSma925 = sma925[0];
            double lvlVwap = sessionVwap.Output[0];

            bool hitSma925 = (barLow <= lvlSma925 && barHigh >= lvlSma925);
            bool hitVwap = (barLow <= lvlVwap && barHigh >= lvlVwap);

            bool hit = hitSma925 || hitVwap;
            if (DebugLogs && hit)
            {
                D($"[ProfitExit] Hit important level. SMA925={lvlSma925:F2} hit={hitSma925}, " +
                  $"SessionVWAP={lvlVwap:F2} hit={hitVwap}, BarRange=[{barLow:F2},{barHigh:F2}]");
            }
            return hit;
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < BarsRequiredToTrade) return;

            EnsureSessionResetIfNeeded();

            double toTime = ToTime(Time[0]) / 100.0;
            bool isitEarly = toTime >= 1500 && toTime < 2359;

            // RTH window
            bool inRTH = !UseRTH || (toTime >= 830 && toTime < 1500);

            // Reset at 15:10 or 21:00
            if ((toTime == 1510 || toTime == 2100) && BarsInProgress == 0 && IsFirstTickOfBar)
            {
                PrevDayPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                PrevDayTradeCount = SystemPerformance.AllTrades.Count;
                dayOverVar = false;
                lowbandLongFlag = highbandShortFlag = false;
                prevLow = PriorDayOHLC().PriorLow[0];
                prevHigh = PriorDayOHLC().PriorHigh[0];
                currRecordedPivot = double.NaN;
                NowPivot = double.NaN;
                prevTouchedPivot = double.MinValue;
            }

            // PnL-based exit
         /*   if (SystemPerformance.AllTrades.Count > 0 && !dayOverVar)
            {
                double cumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - PrevDayPnL;

                if (AccountRealizedPL < MaxLoss * TradeSize ||
                    AccountRealizedPL > 200 * TradeSize ||
                    AccountUnrealizedPL < -500)
                {
                    dayOverVar = true;

                    if (Position.MarketPosition == MarketPosition.Long) ExitLong();
                    else if (Position.MarketPosition == MarketPosition.Short) ExitShort();

                    D($"Exiting due to PnL conditions: CumProfit={cumProfit}, AccountRealizedPL={AccountRealizedPL}, UnrealizedPL={AccountUnrealizedPL}");
                }
            } */

            // Profit-protect exit if in profit and SMA/VWAP hit
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if (TradeIsInProfit() && HitImportantLevelThisBar())
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                    {
                        D("[ProfitExit] Long position in profit hit SMA925/SessionVWAP. Flattening.");
                        ExitLong("ProfitExit_SMA_VWAP");
                    }
                    else if (Position.MarketPosition == MarketPosition.Short)
                    {
                        D("[ProfitExit] Short position in profit hit SMA925/SessionVWAP. Flattening.");
                        ExitShort("ProfitExit_SMA_VWAP");
                    }
                }
            }

            // MAIN TRADING LOGIC (ENTRY TRIGGERS & PIVOTS)
            if (!inRTH) return;

            if (BarsInProgress == 0 && IsFirstTickOfBar && !isitEarly && toTime > 835 && !dayOverVar)
            {
                prevLow = PriorDayOHLC().PriorLow[0];
                prevHigh = PriorDayOHLC().PriorHigh[0];
                if (prevClose < 2) prevClose = ManualSettPrice;
                if (prevLow < 2 || prevHigh < 2)
                {
                    prevLow = ManualLowPrice;
                    prevHigh = ManualHighPrice;
                }
                if (prevClose < 2) return;

                // Pivot levels
                pLevel = Instrument.MasterInstrument.RoundToTickSize((prevHigh + prevLow + prevClose) / 3);
                r1Level = Instrument.MasterInstrument.RoundToTickSize(pLevel * 2 - prevLow);
                s1Level = Instrument.MasterInstrument.RoundToTickSize(pLevel * 2 - prevHigh);
                r2Level = Instrument.MasterInstrument.RoundToTickSize(pLevel + (prevHigh - prevLow));
                s2Level = Instrument.MasterInstrument.RoundToTickSize(pLevel - (prevHigh - prevLow));
                r3Level = Instrument.MasterInstrument.RoundToTickSize(r1Level + (prevHigh - prevLow));
                s3Level = Instrument.MasterInstrument.RoundToTickSize(s1Level - (prevHigh - prevLow));
                pr1midLevel = Instrument.MasterInstrument.RoundToTickSize(pLevel + (r1Level - pLevel) / 2);
                r1r2midLevel = Instrument.MasterInstrument.RoundToTickSize(r1Level + (r2Level - r1Level) / 2);
                r2r3midLevel = Instrument.MasterInstrument.RoundToTickSize(r2Level + (r3Level - r2Level) / 2);
                ps1midLevel = Instrument.MasterInstrument.RoundToTickSize(pLevel - (pLevel - s1Level) / 2);
                s1s2midLevel = Instrument.MasterInstrument.RoundToTickSize(s1Level - (s1Level - s2Level) / 2);
                s2s3midLevel = Instrument.MasterInstrument.RoundToTickSize(s2Level - (s2Level - s3Level) / 2);

                levels = new List<double>
                {
                    s3Level, s2s3midLevel, s2Level, s1s2midLevel, s1Level, ps1midLevel,
                    pLevel, pr1midLevel, r1Level, r1r2midLevel, r2Level, r2r3midLevel, r3Level
                };

                // Plot pivots
                Values[0][0] = s3Level;
                Values[1][0] = s2s3midLevel;
                Values[2][0] = s2Level;
                Values[3][0] = s1s2midLevel;
                Values[4][0] = s1Level;
                Values[5][0] = ps1midLevel;
                Values[6][0] = pLevel;
                Values[7][0] = pr1midLevel;
                Values[8][0] = r1Level;
                Values[9][0] = r1r2midLevel;
                Values[10][0] = r2Level;
                Values[11][0] = r2r3midLevel;
                Values[12][0] = r3Level;

                // Detect which pivot band was touched on previous bar
                NowPivot = double.NaN;
                for (int i = 0; i < levels.Count - 1; i++)
                    if (High[1] >= levels[i] && levels[i] >= Low[1])
                        NowPivot = levels[i];

                if (!double.IsNaN(NowPivot))
                {
                    lastPivotTouchBarIndex[NowPivot] = CurrentBar;
                    prevTouchedPivot = currRecordedPivot;
                    currRecordedPivot = NowPivot;
                }

                // VWAP price and ATR
                vwPrice = Instrument.MasterInstrument.RoundToTickSize(ofVwapETH.Output[0]);
                double lATR = Instrument.MasterInstrument.RoundToTickSize(1.5 * ATR(Closes[1], 14)[0]);

                // ENTRY LOGIC
                if (Position.MarketPosition == MarketPosition.Flat && !sessionTradingHalted)
                {
                    SetBands(Close[0]);

                    if (entryOrder != null)
                    {
                        CancelOrder(entryOrder);
                        entryOrder = null;
                    }
                    if (wtdentryOrder != null)
                    {
                        CancelOrder(wtdentryOrder);
                        wtdentryOrder = null;
                    }

                    lowbandLongFlag = entryOrder == null && (Close[0] - lowband) < (highband - Close[0]);
                    highbandShortFlag = entryOrder == null && (Close[0] - lowband) > (highband - Close[0]);

                    bool pivotRevisitedWithSufficientRetracement = false;

                    // New: retracement threshold = half band width
                    double bandWidth = highband - lowband;
                    if (bandWidth > 0 && lastPivotTouchBarIndex.ContainsKey(currRecordedPivot))
                    {
                        int barsSinceTouch = CurrentBar - lastPivotTouchBarIndex[currRecordedPivot];
                        double enthaDeviation = GetMaxDeviationSinceLastTouch(currRecordedPivot, barsSinceTouch);
                        double requiredRetracement =0.5 * bandWidth;

                        pivotRevisitedWithSufficientRetracement = enthaDeviation > requiredRetracement;

                        if (DebugLogs)
                        {
                            D(string.Format(
                                "[RetraceCheck] Pivot={0:F2} band=({1:F2},{2:F2}) width={3:F2} requiredRetrace={4:F2} actualDeviation={5:F2} ok={6}",
                                currRecordedPivot, lowband, highband, bandWidth, requiredRetracement, enthaDeviation, pivotRevisitedWithSufficientRetracement
                            ));
                        }
                    }

                    double currentATR = ATR(Closes[1], 14)[0];

                    // SHORT ENTRY – 2-leg family at highband
                    if (highbandShortFlag &&
                        (prevTouchedPivot == double.MinValue ||
                         currRecordedPivot != prevTouchedPivot ||
                         pivotRevisitedWithSufficientRetracement) &&
                        currentATR < 10)
                    {
                        adjEntry = highband;
                        SubmitTwoShortLimits(adjEntry);
                        orderTime = Time[0];
                        D($"Entering SHORT family at {adjEntry} from pivot band; 2 legs (PT & Trail).");
                    }
                    // LONG ENTRY – 2-leg family at lowband
                    else if (lowbandLongFlag &&
                        (prevTouchedPivot == double.MinValue ||
                         currRecordedPivot != prevTouchedPivot ||
                         pivotRevisitedWithSufficientRetracement) &&
                        currentATR < 10)
                    {
                        adjEntry = lowband;
                        SubmitTwoLongLimits(adjEntry);
                        orderTime = Time[0];
                        D($"Entering LONG family at {adjEntry} from pivot band; 2 legs (PT & Trail).");
                    }
                }
            }
        }

        // 2-leg order submission
        private void SubmitTwoLongLimits(double entryPrice)
        {
            int total = Math.Max(2, TradeSize);
            int qPT = Math.Max(1, total / 2);
            int qTrail = Math.Max(1, total - qPT);

            trailingEnabledLong = false;

            // PT leg (first leg)
            SetStopLoss(SigPT_L, CalculationMode.Ticks, StopTicks, false);
            SetProfitTarget(SigPT_L, CalculationMode.Ticks, ProfitTicks);

            // Trail leg (second leg): SL + PT2
            SetStopLoss(SigTrail_L, CalculationMode.Ticks, StopTicks, false);
            int pt2Ticks = TrailProfitMultiple * StopTicks;
            SetProfitTarget(SigTrail_L, CalculationMode.Ticks, pt2Ticks);

            EnterLongLimit(0, true, qPT, entryPrice, SigPT_L);
            EnterLongLimit(0, true, qTrail, entryPrice, SigTrail_L);

            setupArmedLong = true;
            familyOpenLong = true;
            ptLegClosedLong = trailLegClosedLong = false;
            ptLegWasFullSL_Long = trailLegWasFullSL_Long = false;
        }

        private void SubmitTwoShortLimits(double entryPrice)
        {
            int total = Math.Max(2, TradeSize);
            int qPT = Math.Max(1, total / 2);
            int qTrail = Math.Max(1, total - qPT);

            trailingEnabledShort = false;

            // PT leg (first leg)
            SetStopLoss(SigPT_S, CalculationMode.Ticks, StopTicks, false);
            SetProfitTarget(SigPT_S, CalculationMode.Ticks, ProfitTicks);

            // Trail leg (second leg): SL + PT2
            SetStopLoss(SigTrail_S, CalculationMode.Ticks, StopTicks, false);
            int pt2Ticks = TrailProfitMultiple * StopTicks;
            SetProfitTarget(SigTrail_S, CalculationMode.Ticks, pt2Ticks);

            EnterShortLimit(0, true, qPT, entryPrice, SigPT_S);
            EnterShortLimit(0, true, qTrail, entryPrice, SigTrail_S);

            setupArmedShort = true;
            familyOpenShort = true;
            ptLegClosedShort = trailLegClosedShort = false;
            ptLegWasFullSL_Short = trailLegWasFullSL_Short = false;
        }

        private bool IsOrderActive(Order o)
        {
            return o != null && (o.OrderState == OrderState.Working
                || o.OrderState == OrderState.Accepted
                || o.OrderState == OrderState.PartFilled
                || o.OrderState == OrderState.ChangePending
                || o.OrderState == OrderState.Submitted);
        }

        // Pivot helpers
        private double GetMaxDeviationSinceLastTouch(double pivotLevel, int barsSinceTouch)
        {
            int lookback = Math.Min(barsSinceTouch, CurrentBar);
            double maxDeviation = 0;
            for (int i = 0; i < lookback; i++)
            {
                double upDeviation = High[i] - pivotLevel;
                double downDeviation = pivotLevel - Low[i];
                double barDeviation = Math.Max(upDeviation, downDeviation);
                if (barDeviation > maxDeviation)
                    maxDeviation = barDeviation;
            }
            return maxDeviation;
        }

        private void SetBands(double price)
        {
            if (price >= pLevel && price < pr1midLevel) { lowband = pLevel; highband = pr1midLevel; nextbandL = r1Level; nextbandS = ps1midLevel; }
            else if (price >= pr1midLevel && price < r1Level) { lowband = pr1midLevel; highband = r1Level; nextbandL = r1r2midLevel; nextbandS = pLevel; }
            else if (price >= r1Level && price < r1r2midLevel) { lowband = r1Level; highband = r1r2midLevel; nextbandL = r2Level; nextbandS = pr1midLevel; }
            else if (price >= r1r2midLevel && price < r2Level) { lowband = r1r2midLevel; highband = r2Level; nextbandL = r2r3midLevel; nextbandS = r1Level; }
            else if (price >= r2Level && price < r2r3midLevel) { lowband = r2Level; highband = r2r3midLevel; nextbandL = r3Level; nextbandS = r1r2midLevel; }
            else if (price >= r2r3midLevel && price < r3Level) { lowband = r2r3midLevel; highband = r3Level; nextbandL = r3Level; nextbandS = r2Level; }
            else if (price >= ps1midLevel && price < pLevel) { lowband = ps1midLevel; highband = pLevel; nextbandL = pr1midLevel; nextbandS = s1Level; }
            else if (price >= s1Level && price < ps1midLevel) { lowband = s1Level; highband = ps1midLevel; nextbandL = pLevel; nextbandS = s1s2midLevel; }
            else if (price >= s1s2midLevel && price < s1Level) { lowband = s1s2midLevel; highband = s1Level; nextbandL = ps1midLevel; nextbandS = s2Level; }
            else if (price >= s2Level && price < s1s2midLevel) { lowband = s2Level; highband = s1s2midLevel; nextbandL = s1Level; nextbandS = s2s3midLevel; }
            else if (price >= s2s3midLevel && price < s2Level) { lowband = s2s3midLevel; highband = s2Level; nextbandL = s1s2midLevel; nextbandS = s3Level; }
            else if (price >= s3Level && price < s2s3midLevel) { lowband = s3Level; highband = s2s3midLevel; nextbandL = s2Level; nextbandS = s3Level; }
        }

        // ORDER/EXECUTION HANDLING
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
                                              int filled, double averageFillPrice, OrderState orderState,
                                              DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null) return;

            // Maintain entryOrder / wtdentryOrder from original skeleton for compatibility
            if (entryOrder != null && entryOrder.IsBacktestOrder && State == State.Realtime)
                entryOrder = GetRealtimeOrder(entryOrder);

            if (wtdentryOrder != null && wtdentryOrder.IsBacktestOrder && State == State.Realtime)
                wtdentryOrder = GetRealtimeOrder(wtdentryOrder);

            if (entryOrder == null && (order.Name.StartsWith("Long") || order.Name.StartsWith("Short")))
                entryOrder = order;

            if (wtdentryOrder == null && (order.Name.StartsWith("WTD L") || order.Name.StartsWith("WTD S")))
                wtdentryOrder = order;

            if (entryOrder != null && order.OrderState == OrderState.Cancelled)
                entryOrder = null;
            if (wtdentryOrder != null && order.OrderState == OrderState.Cancelled)
                wtdentryOrder = null;

            // Track PT/Trail orders for both sides
            if (order.Name == SigPT_L)
            {
                orderPT_L = order;
                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected) orderPT_L = null;
            }
            else if (order.Name == SigTrail_L)
            {
                orderTrail_L = order;
                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected) orderTrail_L = null;
            }
            else if (order.Name == SigPT_S)
            {
                orderPT_S = order;
                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected) orderPT_S = null;
            }
            else if (order.Name == SigTrail_S)
            {
                orderTrail_S = order;
                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected) orderTrail_S = null;
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
                                                  int quantity, MarketPosition marketPosition,
                                                  string orderId, DateTime time)
        {
            if (execution?.Order == null) return;

            string on = execution.Order.Name ?? string.Empty;
            string fromSig = execution.Order.FromEntrySignal ?? string.Empty;

            bool isPTExit = on.IndexOf("Profit target", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isStopExit = on.IndexOf("Stop loss", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isExitEvent = isStopExit || isPTExit;

            // Trail leg long filled
            if (execution.Order.Name == SigTrail_L &&
                execution.Order.OrderAction == OrderAction.Buy &&
                execution.Order.OrderState != OrderState.PartFilled)
            {
                trailActiveLong = true;
                double ep = execution.Order.AverageFillPrice == 0 ? price : execution.Order.AverageFillPrice;

                trailEntryPriceLong = Instrument.MasterInstrument.RoundToTickSize(ep);
                trailCurrentSL_Long = Instrument.MasterInstrument.RoundToTickSize(ep - StopTicks * TickSize);
                D($"[Long] L_Trail filled @ {ep}. Initial SL ~ {trailCurrentSL_Long}. Trailing disabled until PT1 hit.");

                setupArmedLong = false;
            }

            // Trail leg short filled
            if (execution.Order.Name == SigTrail_S &&
                execution.Order.OrderAction == OrderAction.SellShort &&
                execution.Order.OrderState != OrderState.PartFilled)
            {
                trailActiveShort = true;
                double ep = execution.Order.AverageFillPrice == 0 ? price : execution.Order.AverageFillPrice;

                trailEntryPriceShort = Instrument.MasterInstrument.RoundToTickSize(ep);
                trailCurrentSL_Short = Instrument.MasterInstrument.RoundToTickSize(ep + StopTicks * TickSize);
                D($"[Short] S_Trail filled @ {ep}. Initial SL ~ {trailCurrentSL_Short}. Trailing disabled until PT1 hit.");

                setupArmedShort = false;
            }

            // PT1 hit for long family
            if (isPTExit && fromSig == SigPT_L)
            {
                ptLegClosedLong = true;

                if (trailActiveLong && !double.IsNaN(trailEntryPriceLong))
                {
                    double be = Instrument.MasterInstrument.RoundToTickSize(trailEntryPriceLong);
                    SetStopLoss(SigTrail_L, CalculationMode.Price, be, false);
                    trailCurrentSL_Long = be;
                    D($"[Long] PT1 hit ({ProfitTicks}t). L_Trail SL moved to BE @ {be}. No EMA trailing.");
                }

                trailingEnabledLong = true;
            }

            // PT1 hit for short family
            if (isPTExit && fromSig == SigPT_S)
            {
                ptLegClosedShort = true;

                if (trailActiveShort && !double.IsNaN(trailEntryPriceShort))
                {
                    double be = Instrument.MasterInstrument.RoundToTickSize(trailEntryPriceShort);
                    SetStopLoss(SigTrail_S, CalculationMode.Price, be, false);
                    trailCurrentSL_Short = be;
                    D($"[Short] PT1 hit ({ProfitTicks}t). S_Trail SL moved to BE @ {be}. No EMA trailing.");
                }

                trailingEnabledShort = true;
            }

            // Trail leg exit classification - long
            if (isExitEvent && fromSig == SigTrail_L)
            {
                trailLegClosedLong = true;
                if (isStopExit)
                {
                    // full loss if this happened before trail was enabled (i.e., before BE)
                    bool full = !trailingEnabledLong;
                    trailLegWasFullSL_Long = full;
                }

                trailActiveLong = false;
                trailingEnabledLong = false;
                trailCurrentSL_Long = double.NaN;
                D("[Long] L_Trail exit detected; trail state cleared.");

                if (ptLegClosedLong && trailLegClosedLong && familyOpenLong)
                {
                    bool bothFull = ptLegWasFullSL_Long && trailLegWasFullSL_Long;
                    EvaluateAndMaybeHaltAfterFamilyClose(bothFull);
                    familyOpenLong = false;
                }
            }

            // Trail leg exit classification - short
            if (isExitEvent && fromSig == SigTrail_S)
            {
                trailLegClosedShort = true;
                if (isStopExit)
                {
                    bool full = !trailingEnabledShort;
                    trailLegWasFullSL_Short = full;
                }

                trailActiveShort = false;
                trailingEnabledShort = false;
                trailCurrentSL_Short = double.NaN;
                D("[Short] S_Trail exit detected; trail state cleared.");

                if (ptLegClosedShort && trailLegClosedShort && familyOpenShort)
                {
                    bool bothFull = ptLegWasFullSL_Short && trailLegWasFullSL_Short;
                    EvaluateAndMaybeHaltAfterFamilyClose(bothFull);
                    familyOpenShort = false;
                }
            }

            // PT leg full-stop classification
            if (isStopExit && fromSig == SigPT_L)
            {
                ptLegClosedLong = true;
                ptLegWasFullSL_Long = true;

                if (ptLegClosedLong && trailLegClosedLong && familyOpenLong)
                {
                    bool bothFull = ptLegWasFullSL_Long && trailLegWasFullSL_Long;
                    EvaluateAndMaybeHaltAfterFamilyClose(bothFull);
                    familyOpenLong = false;
                }
            }
            if (isStopExit && fromSig == SigPT_S)
            {
                ptLegClosedShort = true;
                ptLegWasFullSL_Short = true;

                if (ptLegClosedShort && trailLegClosedShort && familyOpenShort)
                {
                    bool bothFull = ptLegWasFullSL_Short && trailLegWasFullSL_Short;
                    EvaluateAndMaybeHaltAfterFamilyClose(bothFull);
                    familyOpenShort = false;
                }
            }
        }

        // Plot properties
        [XmlIgnore] public Series<double> S3Level => Values[0];
        [XmlIgnore] public Series<double> S2S3MidLevel => Values[1];
        [XmlIgnore] public Series<double> S2Level => Values[2];
        [XmlIgnore] public Series<double> S1S2MidLevel => Values[3];
        [XmlIgnore] public Series<double> S1Level => Values[4];
        [XmlIgnore] public Series<double> PS1MidLevel => Values[5];
        [XmlIgnore] public Series<double> PLevel => Values[6];
        [XmlIgnore] public Series<double> PR1MidLevel => Values[7];
        [XmlIgnore] public Series<double> R1Level => Values[8];
        [XmlIgnore] public Series<double> R1R2MidLevel => Values[9];
        [XmlIgnore] public Series<double> R2Level => Values[10];
        [XmlIgnore] public Series<double> R2R3MidLevel => Values[11];
        [XmlIgnore] public Series<double> R3Level => Values[12];

        protected override void OnAccountItemUpdate(Account account, AccountItem accountItem, double value)
        {
            AccountRealizedPL = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
            AccountUnrealizedPL = account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
        }
    }
}
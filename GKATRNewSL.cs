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
    public class GKATRNewSL : Strategy
    {
        // ==== Core variables (from NewATRQOnly skeleton) ====
        private bool BE_Set = false; // kept but effectively managed by 2‑leg logic
        private double PrevDayPnL = 0, PrevDayTradeCount = 0;
        private double AccountRealizedPL, AccountUnrealizedPL;
        private bool dayOverVar = false;
        private DateTime orderTime;
        private bool Trail_Set = false; // no longer used – replaced by 2‑leg logic
        private double currentTrailStopPrice = 0;

        // ATR Quartile variables (unchanged logic)
        private double sessionOpen;
        private double atrQ1Level, atrQ2Level, atrQ3Level, atrQ4Level;
        private double atrQ1Q2midLevel, atrQ2Q3midLevel, atrQ3Q4midLevel;
        private double atrQ0Q1midLevel, atrQ4Q5midLevel; // below Q1 and above Q4
        private double atrLowband, atrHighband, atrNextbandL, atrNextbandS;
        private bool atrLowbandLongFlag, atrHighbandLongFlag, atrLowbandShortFlag, atrHighbandShortFlag;
        private Order atrEntryOrder = null;
        private List<double> atrLevels;
        private double atrNowLevel = double.NaN;
        private double atrPrevTouchedLevel = double.NaN;
        private double atrCurrRecordedLevel = double.NaN;
        private Dictionary<double, int> lastATRLevelTouchBarIndex = new Dictionary<double, int>();
        private double atrOrderDilowband, atrOrderDihighband, atrOrderDinextband;

        // ==== 2‑leg SL / PT1 / PT2 & order‑family state (ported from GKPPNewSL) ====

        // 2‑leg family order refs (PT & Trail) – we will use per‑signal stops/targets
        private Order orderPT_L;    // "L_PT"
        private Order orderTrail_L; // "L_Trail"
        private Order orderPT_S;    // "S_PT"
        private Order orderTrail_S; // "S_Trail"

        // State for long family
        private bool familyOpenLong = false;
        private bool ptLegClosedLong = false;
        private bool trailLegClosedLong = false;
        private bool ptLegWasFullSL_Long = false;
        private bool trailLegWasFullSL_Long = false;
        private bool trailActiveLong;
        private bool trailingEnabledLong;
        private double trailCurrentSL_Long = double.NaN;
        private double trailEntryPriceLong = double.NaN;

        // State for short family
        private bool familyOpenShort = false;
        private bool ptLegClosedShort = false;
        private bool trailLegClosedShort = false;
        private bool ptLegWasFullSL_Short = false;
        private bool trailLegWasFullSL_Short = false;
        private bool trailActiveShort;
        private bool trailingEnabledShort;
        private double trailCurrentSL_Short = double.NaN;
        private double trailEntryPriceShort = double.NaN;

        private bool sessionTradingHalted = false;
        private int consecutiveFullLossFamilies = 0;
        private DateTime sessionDate = DateTime.MinValue;

        // VWAP + SMA for profit exits (from GKPPNewSL)
        private SMA sma925;
        private VWAP1 sessionVwap;

        // RTH parameter
        [NinjaScriptProperty]
        [Display(Name = "Use RTH (8:30-15:00)", GroupName = "Parameters", Order = 3)]
        public bool UseRTH { get; set; }

        // 2‑leg risk parameters
        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Stop (ticks)", GroupName = "Risk", Order = 10)]
        public int StopTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Profit target (ticks) for PT1", GroupName = "Risk", Order = 11)]
        public int ProfitTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Trail profit multiple (x StopTicks) for PT2", GroupName = "Risk", Order = 12)]
        public int TrailProfitMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable debug logs", GroupName = "Diagnostics", Order = 20)]
        public bool DebugLogs { get; set; }

        // original NewATRQOnly parameters
        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Trade size", GroupName = "Parameters", Order = 0)]
        public double TradeSize { get; set; }

        [Range(double.MinValue, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Max Loss", GroupName = "Parameters", Order = 1)]
        public double MaxLoss { get; set; }

        [Range(0.1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "My ATR", GroupName = "Parameters", Order = 2)]
        public double MyATR { get; set; }

        // signal names for 2‑leg families
        private const string SigPT_L = "L_PT";
        private const string SigTrail_L = "L_Trail";
        private const string SigPT_S = "S_PT";
        private const string SigTrail_S = "S_Trail";

        private void D(string msg)
        {
            if (DebugLogs)
                Print("[GKATR] " + msg);
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"ATR Quartiles Trading Strategy with GKPP-style 2-leg SL/PT management";
                Name = "GKATRNewSL";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 3;
                EntryHandling = EntryHandling.UniqueEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 3600;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                BarsRequiredToTrade = 24;

                TradeSize = 2;          // for 2‑leg families
                MaxLoss = -75;
                MyATR = 100.0;

                UseRTH = true;

                StopTicks = 5;
                ProfitTicks = 10;
                TrailProfitMultiple = 8;
                DebugLogs = true;

                // ATR Quartile plots (unchanged)
                AddPlot(Brushes.Orange, "ATRNegQ4Level");    // -Q4
                AddPlot(Brushes.Orange, "ATRNegQ3Level");    // -Q3
                AddPlot(Brushes.Orange, "ATRNegQ2Level");    // -Q2
                AddPlot(Brushes.Orange, "ATRNegQ1Level");    // -Q1
                AddPlot(Brushes.Cyan, "ATRSessionOpen");     // Session Open
                AddPlot(Brushes.Orange, "ATRQ1Level");       // Q1
                AddPlot(Brushes.Orange, "ATRQ2Level");       // Q2
                AddPlot(Brushes.Orange, "ATRQ3Level");       // Q3
                AddPlot(Brushes.Orange, "ATRQ4Level");       // Q4
            }
            else if (State == State.Configure)
            {
                AddDataSeries(Data.BarsPeriodType.Minute, 1);
                AddDataSeries(Data.BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                // session VWAP + SMA925 for profit exits (like GKPPNewSL)
                sma925 = SMA(925);
                sma925.Plots[0].Brush = Brushes.MediumPurple;

                sessionVwap = VWAP1(BarsArray[0],
                    new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true);
                sessionVwap.Plots[0].Brush = Brushes.Gold;

                AddChartIndicator(sma925);
                AddChartIndicator(sessionVwap);

                ResetFamilyTracking();
                sessionTradingHalted = false;
                consecutiveFullLossFamilies = 0;
                sessionDate = DateTime.MinValue;

                trailCurrentSL_Long = double.NaN;
                trailCurrentSL_Short = double.NaN;
                trailEntryPriceLong = double.NaN;
                trailEntryPriceShort = double.NaN;

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

        // Use SMA925 & session VWAP to flatten profitable trades (from GKPPNewSL)
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
            if (CurrentBars[0] < BarsRequiredToTrade)
                return;

            EnsureSessionResetIfNeeded();

            double toTime = ToTime(Time[0]) / 100.0;
            bool isitEarly = toTime >= 1500 && toTime < 2359;

            // RTH window
            bool inRTH = !UseRTH || (toTime >= 830 && toTime < 1500);
            bool isitRTH = inRTH;

            // Daily reset at 15:10 or 21:00 (keep ATR + family state resets)
            if ((toTime == 1510 || toTime == 2100) && BarsInProgress == 0 && IsFirstTickOfBar)
            {
                PrevDayPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                PrevDayTradeCount = SystemPerformance.AllTrades.Count;
                dayOverVar = false;

                // Reset ATR quartile flags
                atrLowbandLongFlag = atrHighbandLongFlag = atrLowbandShortFlag = atrHighbandShortFlag = false;
                atrCurrRecordedLevel = double.NaN;
                atrNowLevel = double.NaN;
                atrPrevTouchedLevel = double.MinValue;

                // Reset family tracking per session
                ResetFamilyTracking();
                sessionTradingHalted = false;
                consecutiveFullLossFamilies = 0;

                D("[DEBUG] Daily reset - All trading flags reset");
            }

            // PnL-based exit logic (unchanged thresholds from skeleton, but scaled by TradeSize where appropriate)
            if (SystemPerformance.AllTrades.Count > 0 && !dayOverVar)
            {
                double cumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - PrevDayPnL;
                if (AccountRealizedPL < MaxLoss || AccountRealizedPL > 200 * TradeSize || AccountUnrealizedPL < -500)
                {
                    dayOverVar = true;

                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong();
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort();

                    D($"[DEBUG] Day trading stopped - PnL limits hit: CumProfit={cumProfit}, RealizedPL={AccountRealizedPL}, UnrealizedPL={AccountUnrealizedPL}");
                }
            }

            // Store session open at specific time (keep original 1702 logic)
            if (toTime == 1702 && BarsInProgress == 0 && IsFirstTickOfBar)
            {
                sessionOpen = Open[0];
                D("[DEBUG] Session Open " + sessionOpen);
            }

            // Profit-protect exit using VWAP/SMA when in profit (from GKPPNewSL)
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

            // MAIN ATR logic (entries / ATR levels) – keep skeleton behavior
            if (BarsInProgress == 0 && IsFirstTickOfBar && isitRTH && !isitEarly && toTime > 830 && !dayOverVar && !sessionTradingHalted)
            {
                // Calculate and plot ATR quartile levels
                if (sessionOpen > 0) // Only calculate if we have session open
                {
                    atrQ1Level = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.25));
                    atrQ2Level = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.50));
                    atrQ3Level = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.75));
                    atrQ4Level = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + MyATR);

                    List<double> positiveLevels = new List<double>
                    {
                        atrQ1Level, atrQ2Level,
                        atrQ3Level, atrQ4Level
                    };

                    atrLevels = new List<double>();

                    // Add negative levels
                    for (int i = positiveLevels.Count - 1; i >= 0; i--)
                    {
                        double negativeLevel = Instrument.MasterInstrument.RoundToTickSize(sessionOpen - (positiveLevels[i] - sessionOpen));
                        atrLevels.Add(negativeLevel);
                    }

                    // Add session open
                    atrLevels.Add(sessionOpen);

                    // Add positive levels
                    atrLevels.AddRange(positiveLevels);

                    // Plot ATR levels
                    Values[0][0] = sessionOpen - MyATR;          // -Q4
                    Values[1][0] = sessionOpen - (MyATR * 0.75); // -Q3
                    Values[2][0] = sessionOpen - (MyATR * 0.50); // -Q2
                    Values[3][0] = sessionOpen - (MyATR * 0.25); // -Q1
                    Values[4][0] = sessionOpen;                  // Session Open
                    Values[5][0] = sessionOpen + (MyATR * 0.25); // Q1
                    Values[6][0] = sessionOpen + (MyATR * 0.50); // Q2
                    Values[7][0] = sessionOpen + (MyATR * 0.75); // Q3
                    Values[8][0] = sessionOpen + MyATR;          // Q4
                }

                // ATR Level Touch Detection
                atrNowLevel = double.NaN;
                if (atrLevels != null)
                {
                    for (int i = 0; i < atrLevels.Count; i++)
                    {
                        if (High[1] >= atrLevels[i] && atrLevels[i] >= Low[1])
                            atrNowLevel = atrLevels[i];
                    }
                }

                if (!double.IsNaN(atrNowLevel))
                {
                    lastATRLevelTouchBarIndex[atrNowLevel] = CurrentBar;
                    D("[DEBUG] ATR Level " + atrNowLevel + " touched at bar " + CurrentBar);
                }

                if (!double.IsNaN(atrNowLevel))
                {
                    atrPrevTouchedLevel = atrCurrRecordedLevel;
                    atrCurrRecordedLevel = atrNowLevel;
                }

                // === ENTRY LOGIC – REPLACED with 2‑leg families ===
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    if (atrLevels != null)
                    {
                        SetATRBands(Close[0]);

                        // Set ATR entry flags (unchanged)
                        atrLowbandLongFlag = (Close[0] - atrLowband) < (atrHighband - Close[0]);
                        atrHighbandShortFlag = (Close[0] - atrLowband) > (atrHighband - Close[0]);

                        bool atrLevelRevisitedWithSufficientRetracement = false;
                        double currentATR = ATR(Closes[1], 14)[0];

                        if (lastATRLevelTouchBarIndex.ContainsKey(atrCurrRecordedLevel))
                        {
                            int barsSinceTouch = CurrentBar - lastATRLevelTouchBarIndex[atrCurrRecordedLevel];
                            double atrDeviation = GetMaxDeviationSinceLastTouch(atrCurrRecordedLevel, barsSinceTouch);
                            atrLevelRevisitedWithSufficientRetracement = atrDeviation > 3 * currentATR;

                            if (atrLevelRevisitedWithSufficientRetracement)
                                D("[DEBUG] ATR Level " + atrCurrRecordedLevel + " has sufficient retracement: " + atrDeviation + " > " + (3 * currentATR));
                        }

                        int total = Math.Max(2, (int)TradeSize);
                        int qPT = Math.Max(1, total / 2);
                        int qTrail = Math.Max(1, total - qPT);

                        // SHORT FAMILY ENTRY – 2 legs at atrHighband
                        if (atrHighbandShortFlag
                            && (atrPrevTouchedLevel == double.MinValue || atrCurrRecordedLevel != atrPrevTouchedLevel || atrLevelRevisitedWithSufficientRetracement)
                            && currentATR < 10)
                        {
                            double atrAdjEntry = atrHighband;

                            // PT1 leg
                            SetStopLoss(SigPT_S, CalculationMode.Ticks, StopTicks, false);
                            SetProfitTarget(SigPT_S, CalculationMode.Ticks, ProfitTicks);

                            // Trail / PT2 leg
                            SetStopLoss(SigTrail_S, CalculationMode.Ticks, StopTicks, false);
                            int pt2Ticks = TrailProfitMultiple * StopTicks;
                            SetProfitTarget(SigTrail_S, CalculationMode.Ticks, pt2Ticks);

                            EnterShortLimit(0, true, qPT, atrAdjEntry, SigPT_S);
                            EnterShortLimit(0, true, qTrail, atrAdjEntry, SigTrail_S);

                            BE_Set = false;
                            Trail_Set = false;
                            currentTrailStopPrice = 0;

                            familyOpenShort = true;
                            ptLegClosedShort = trailLegClosedShort = false;
                            ptLegWasFullSL_Short = trailLegWasFullSL_Short = false;

                            trailActiveShort = false;
                            trailingEnabledShort = false;

                            orderTime = Time[0];

                            D($"[DEBUG] SHORT FAMILY ENTRY: Price={atrAdjEntry}, StopTicks={StopTicks}, PT1={ProfitTicks}, PT2={pt2Ticks}, CurrentATR={currentATR}");
                        }
                        // LONG FAMILY ENTRY – 2 legs at atrLowband
                        else if (atrLowbandLongFlag
                            && (atrPrevTouchedLevel == double.MinValue || atrCurrRecordedLevel != atrPrevTouchedLevel || atrLevelRevisitedWithSufficientRetracement)
                            && currentATR < 10)
                        {
                            double atrAdjEntry = atrLowband;

                            // PT1 leg
                            SetStopLoss(SigPT_L, CalculationMode.Ticks, StopTicks, false);
                            SetProfitTarget(SigPT_L, CalculationMode.Ticks, ProfitTicks);

                            // Trail / PT2 leg
                            SetStopLoss(SigTrail_L, CalculationMode.Ticks, StopTicks, false);
                            int pt2Ticks = TrailProfitMultiple * StopTicks;
                            SetProfitTarget(SigTrail_L, CalculationMode.Ticks, pt2Ticks);

                            EnterLongLimit(0, true, qPT, atrAdjEntry, SigPT_L);
                            EnterLongLimit(0, true, qTrail, atrAdjEntry, SigTrail_L);

                            BE_Set = false;
                            Trail_Set = false;
                            currentTrailStopPrice = 0;

                            familyOpenLong = true;
                            ptLegClosedLong = trailLegClosedLong = false;
                            ptLegWasFullSL_Long = trailLegWasFullSL_Long = false;

                            trailActiveLong = false;
                            trailingEnabledLong = false;

                            orderTime = Time[0];

                            D($"[DEBUG] LONG FAMILY ENTRY: Price={atrAdjEntry}, StopTicks={StopTicks}, PT1={ProfitTicks}, PT2={pt2Ticks}, CurrentATR={currentATR}");
                        }
                    }
                }
            }

            // === Position management specific to 2‑leg families ===
            // Note: BE / trail for 2nd leg is handled in OnExecutionUpdate, not here.
        }

        // Function to get maximum deviation from a level over the last n bars (unchanged)
        private double GetMaxDeviationSinceLastTouch(double level, int barsSinceTouch)
        {
            int lookback = Math.Min(barsSinceTouch, CurrentBar);
            double maxDeviation = 0;

            for (int i = 0; i < lookback; i++)
            {
                double upDeviation = High[i] - level;
                double downDeviation = level - Low[i];
                double barDeviation = Math.Max(upDeviation, downDeviation);
                if (barDeviation > maxDeviation)
                    maxDeviation = barDeviation;
            }

            return maxDeviation;
        }

        // ATR Band Setting Method (unchanged from skeleton)
        private void SetATRBands(double price)
        {
            if (atrLevels == null || atrLevels.Count == 0) return;

            for (int i = 0; i < atrLevels.Count - 1; i++)
            {
                if (price >= atrLevels[i] && price < atrLevels[i + 1])
                {
                    atrLowband = atrLevels[i];
                    atrHighband = atrLevels[i + 1];
                    atrNextbandL = (i + 2 < atrLevels.Count) ? atrLevels[i + 2] : atrLevels[i + 1];
                    atrNextbandS = (i - 1 >= 0) ? atrLevels[i - 1] : atrLevels[i];

                    D("[DEBUG] ATR Bands set - Current price: " + price + ", Band: [" + atrLowband + " - " + atrHighband + "]");
                    break;
                }
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null) return;

            // Maintain ATR entry order if needed
            if (atrEntryOrder != null && atrEntryOrder.IsBacktestOrder && State == State.Realtime)
                atrEntryOrder = GetRealtimeOrder(atrEntryOrder);

            if (atrEntryOrder == null && (order.Name.StartsWith("ATR Long") || order.Name.StartsWith("ATR Short")))
            {
                atrEntryOrder = order;
                D("[DEBUG] Order placed: " + order.Name + " at " + limitPrice);
            }

            if (atrEntryOrder != null && order.OrderState == OrderState.Cancelled)
            {
                atrEntryOrder = null;
                D("[DEBUG] Order cancelled: " + order.Name);
            }

            // Track PT/Trail orders for both sides (from GKPPNewSL)
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

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution?.Order == null) return;

            string on = execution.Order.Name ?? string.Empty;
            string fromSig = execution.Order.FromEntrySignal ?? string.Empty;

            bool isPTExit = on.IndexOf("Profit target", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isStopExit = on.IndexOf("Stop loss", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isExitEvent = isStopExit || isPTExit;

            // Track ATR entries as before
            if (execution.Order.Name.StartsWith("ATR Long") || execution.Order.Name.StartsWith("ATR Short"))
            {
                orderTime = execution.Order.Time;
                BE_Set = false;
                Trail_Set = false;
                currentTrailStopPrice = 0;
                SetATRBands(Close[0]);
                atrOrderDilowband = atrLowband;
                atrOrderDihighband = atrHighband;
                atrOrderDinextband = execution.Order.Name.StartsWith("ATR Long") ? atrNextbandL : atrNextbandS;
                D("[DEBUG] ORDER FILLED: " + execution.Order.Name + " at " + price + ", Position: " + Position.Quantity + " contracts");
            }

            if (execution.Order.OrderState != OrderState.PartFilled)
            {
                if (execution.Order.Name.StartsWith("ATR"))
                    atrEntryOrder = null;
            }

            // === 2‑leg handling (copied from GKPPNewSL, adapted) ===

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

                // we do not arm anything special here – BE is applied when PT1 fires
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
            }

            // PT1 hit for long family -> move 2nd leg to BE and allow trailing
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

            // PT1 hit for short family -> move 2nd leg to BE and allow trailing
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
                    bool full = !trailingEnabledLong; // full loss if before BE activation
                    trailLegWasFullSL_Long = full;
                }

                trailActiveLong = false;
                trailingEnabledLong = false;
                trailCurrentSL_Long = double.NaN;
                D("[Long] L_Trail exit detected; trail state cleared.");

                if (ptLegClosedLong && trailLegClosedLong && familyOpenLong)
                {
                    bool bothFull = ptLegWasFullSL_Long && trailLegWasFullSL_Long;
                 //   EvaluateAndMaybeHaltAfterFamilyClose(bothFull);
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

        #region ATR Quartile Series (unchanged)
        [XmlIgnore] public Series<double> ATRNegQ4Level => Values[0];
        [XmlIgnore] public Series<double> ATRNegQ3Level => Values[1];
        [XmlIgnore] public Series<double> ATRNegQ2Level => Values[2];
        [XmlIgnore] public Series<double> ATRNegQ1Level => Values[3];
        [XmlIgnore] public Series<double> ATRSessionOpen => Values[4];
        [XmlIgnore] public Series<double> ATRQ1Level => Values[5];
        [XmlIgnore] public Series<double> ATRQ2Level => Values[6];
        [XmlIgnore] public Series<double> ATRQ3Level => Values[7];
        [XmlIgnore] public Series<double> ATRQ4Level => Values[8];
        #endregion

        protected override void OnAccountItemUpdate(Account account, AccountItem accountItem, double value)
        {
            AccountRealizedPL = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
            AccountUnrealizedPL = account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
        }
    }
}

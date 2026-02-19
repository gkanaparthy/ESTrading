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
    public class ATRQAVWAPTrailHalf : Strategy
    {
        // Core variables for trading
        private bool BE_Set = false;
        private double PrevDayPnL = 0, PrevDayTradeCount = 0;
        private double AccountRealizedPL, AccountUnrealizedPL;
        private bool dayOverVar = false;
        private DateTime orderTime;
        private bool Trail_Set = false;
        private double currentTrailStopPrice = 0;

        // ATR & quartile variables
        private double sessionOpen;
        private double atrLowband, atrHighband, atrNextbandL, atrNextbandS;
        private bool atrLowbandLongFlag, atrHighbandLongFlag, atrLowbandShortFlag, atrHighbandShortFlag;
        private Order atrEntryOrderFull = null;   // Full profit‐target leg
        private Order atrEntryOrderHalf = null;   // Half profit‐target leg
        private List<double> atrLevels;
        private double atrNowLevel = double.NaN;
        private double atrPrevTouchedLevel = double.NaN;
        private double atrCurrRecordedLevel = double.NaN;
        private Dictionary<double, int> lastATRLevelTouchBarIndex = new Dictionary<double, int>();
        private double atrOrderDilowband, atrOrderDihighband, atrOrderDinextband;

        // Anchored VWAP variables
        private Series<double> anchoredVWAP;
        private double cumPV = 0;
        private double cumVol = 0;
        private int anchorBar = -1;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"ATR Quartiles Trading Strategy";
                Name = "ATRQAVWAPTrailHalf";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 3;
                EntryHandling = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 3600;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                BarsRequiredToTrade = 24;
                TradeSize = 2;
                MaxLoss = -75;
                MyATR = 60.0;
                RTHOnly = true;
                InverseLogic = false; // Default value for the new parameter

                // ATR Quartile plots (±1ATR only)
                AddPlot(Brushes.Orange, "ATRNegQ4Level");   // -Q4
                AddPlot(Brushes.Orange, "ATRNegQ3Level");   // -Q3
                AddPlot(Brushes.Orange, "ATRNegQ2Level");   // -Q2
                AddPlot(Brushes.Orange, "ATRNegQ1Level");   // -Q1
                AddPlot(Brushes.Cyan, "ATRSessionOpen");  // Session Open
                AddPlot(Brushes.Orange, "ATRQ1Level");    // Q1
                AddPlot(Brushes.Orange, "ATRQ2Level");    // Q2
                AddPlot(Brushes.Orange, "ATRQ3Level");    // Q3
                AddPlot(Brushes.Orange, "ATRQ4Level");    // Q4

                AddPlot(Brushes.Magenta, "AnchoredVWAP");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(Data.BarsPeriodType.Minute, 1);
                AddDataSeries(Data.BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                anchoredVWAP = Values[9];
                cumPV = 0;
                cumVol = 0;
                anchorBar = -1;
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (CurrentBars[0] < BarsRequiredToTrade) return;

            // Always clear VWAP by default
            anchoredVWAP[0] = double.NaN;

            double toTime = ToTime(Time[0]) / 100.0;
            bool isitEarly = toTime >= 1500 && toTime < 2359;
            if (!RTHOnly) isitEarly = false;

            // Reset at specific times (15:10 or 21:00)
            if ((toTime == 1510 || toTime == 2100) && IsFirstTickOfBar)
            {
                PrevDayPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                PrevDayTradeCount = SystemPerformance.AllTrades.Count;
                dayOverVar = false;

                atrLowbandLongFlag = atrHighbandLongFlag =
                atrLowbandShortFlag = atrHighbandShortFlag = false;
                atrCurrRecordedLevel = double.NaN;
                atrNowLevel = double.NaN;
                atrPrevTouchedLevel = double.MinValue;

                Print("[DEBUG] Daily reset at " + toTime + " - All trading flags reset");
            }

            // PnL-based exit logic
            if (SystemPerformance.AllTrades.Count > 0 && !dayOverVar)
            {
                double sessionPnL = AccountRealizedPL + AccountUnrealizedPL;
                double profitTarget = 200 * TradeSize;
                double lossLimit = MaxLoss * TradeSize;
                if (sessionPnL >= profitTarget || sessionPnL <= lossLimit)
                {
                    dayOverVar = true;
                    if (Position.MarketPosition == MarketPosition.Long) ExitLong();
                    else if (Position.MarketPosition == MarketPosition.Short) ExitShort();
                    Print("[DEBUG] Day trading stopped - PnL limits hit: RealizedPL=" +
                    AccountRealizedPL + ", UnrealizedPL=" + AccountUnrealizedPL);
                }
            }

            // Store session open at 8:32 AM (17:02 EDT)
            if (toTime == 1702 && IsFirstTickOfBar)
            {
                sessionOpen = Open[0];
                Print("[DEBUG] Session Open " + sessionOpen);
            }

            // Main trading logic
            if (IsFirstTickOfBar && !isitEarly && !dayOverVar)
            {
                Print("++++ Time ++++ " + Time[0]);
                // 1) Build ATR levels ±2ATR in 0.25ATR steps
                if (sessionOpen > 0)
                {
                    double[] multipliers = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 };
                    var posLevels = multipliers
                    .Select(m => Instrument.MasterInstrument.RoundToTickSize(sessionOpen + m * MyATR))
                    .ToList();
                    var negLevels = multipliers.Reverse()
                    .Select(m => Instrument.MasterInstrument.RoundToTickSize(sessionOpen - m * MyATR))
                    .ToList();

                    atrLevels = new List<double>(negLevels);
                    atrLevels.Add(sessionOpen);
                    atrLevels.AddRange(posLevels);

                    // Plot original ±1ATR levels
                    Values[0][0] = sessionOpen - MyATR;    // -Q4
                    Values[1][0] = sessionOpen - (MyATR * 0.75);  // -Q3
                    Values[2][0] = sessionOpen - (MyATR * 0.50);  // -Q2
                    Values[3][0] = sessionOpen - (MyATR * 0.25);  // -Q1
                    Values[4][0] = sessionOpen;    // Session Open
                    Values[5][0] = sessionOpen + (MyATR * 0.25);  // Q1
                    Values[6][0] = sessionOpen + (MyATR * 0.50);  // Q2
                    Values[7][0] = sessionOpen + (MyATR * 0.75);  // Q3
                    Values[8][0] = sessionOpen + MyATR;    // Q4
                }

                // 2) ATR Level Touch Detection
                atrNowLevel = double.NaN;
                if (atrLevels != null)
                {
                    for (int i = 0; i < atrLevels.Count; i++)
                        if (High[1] >= atrLevels[i] && atrLevels[i] >= Low[1])
                            atrNowLevel = atrLevels[i];

                    if (!double.IsNaN(atrNowLevel))
                    {
                        lastATRLevelTouchBarIndex[atrNowLevel] = CurrentBar;
                        Print("[DEBUG] ATR Level " + atrNowLevel + " touched at bar " +
                        CurrentBar);
                        atrPrevTouchedLevel = atrCurrRecordedLevel;
                        atrCurrRecordedLevel = atrNowLevel;
                    }
                }

                // 3) Entry logic with ±2ATR guard (split into HP & FP legs)
                if (Position.MarketPosition == MarketPosition.Flat && atrLevels != null)
                {
                    double minATR = atrLevels.First();
                    double maxATR = atrLevels.Last();
                    if (Close[0] < minATR || Close[0] > maxATR)
                    {
                        Print($"[DEBUG] Price {Close[0]:F2} outside ±2ATR bounds [{minATR:F2}–{maxATR:F2}]; skipping entry");
                    }
                    else
                    {
                        // Cancel any pending entry legs
                        if (atrEntryOrderFull != null)
                        {
                            CancelOrder(atrEntryOrderFull);
                            atrEntryOrderFull = null;
                            Print("[DEBUG] Canceled FP entry");
                        }
                        if (atrEntryOrderHalf != null)
                        {
                            CancelOrder(atrEntryOrderHalf);
                            atrEntryOrderHalf = null;
                            Print("[DEBUG] Canceled HP entry");
                        }

                        // Set bands & flags
                        SetATRBands(Close[0]);
                        atrLowbandLongFlag = (Close[0] - atrLowband) < (atrHighband - Close[0]);
                        atrHighbandShortFlag = (Close[0] - atrLowband) > (atrHighband - Close[0]);

                        bool atrLevelRevisitedWithSufficientRetracement = false;
                        double currentATR = ATR(Closes[1], 14)[0];
                        if (lastATRLevelTouchBarIndex.TryGetValue(
                        atrCurrRecordedLevel, out int lastTouch))
                        {
                            int barsSinceTouch = CurrentBar - lastTouch;
                            double atrDeviation =
                            GetMaxDeviationSinceLastTouch(atrCurrRecordedLevel,
                            barsSinceTouch);
                            atrLevelRevisitedWithSufficientRetracement =
                            atrDeviation > 3 * currentATR;
                            if (atrLevelRevisitedWithSufficientRetracement)
                                Print(" ATR Level " + atrCurrRecordedLevel +
                                " has sufficient retracement: " + atrDeviation +
                                " > " + (3 * currentATR));
                            else
                                Print(" ATR Level " + atrCurrRecordedLevel +
                                " doesnt have enough retracement: " + atrDeviation +
                                " < " + (3 * currentATR));
                        }

                        // Split size & profit targets
                        int sizeHalf = (int)Math.Floor(TradeSize / 2.0);
                        int sizeFull = (int)TradeSize - sizeHalf;
                        double fullPT = Math.Min(4 * 20, 4 * (atrHighband - atrLowband));
                        double halfPT = fullPT / 2.0;

                        Print("atrLowbandLongFlag: " + atrLowbandLongFlag + " :::: atrHighbandShortFlag: " + atrHighbandShortFlag);
                        
                        // =================================================================
                        // INVERSE LOGIC START
                        // =================================================================
                        if (InverseLogic)
                        {
                            // INVERSED: ATR LONG SIGNAL becomes a SHORT STOP LIMIT order
                            if (atrLowbandLongFlag
                                && (atrPrevTouchedLevel == double.MinValue || atrCurrRecordedLevel != atrPrevTouchedLevel || atrLevelRevisitedWithSufficientRetracement)
                                && ATR(Closes[1], 14)[0] < 6)
                            {
                                Print("Setting up an INVERSED Short here (from long signal) using ShortStopLimit.");
                                double stopPrice = atrLowband;
                                double limitPrice = stopPrice - (1 * TickSize); // 1-tick offset

                                if (sizeHalf > 0)
                                    atrEntryOrderHalf = EnterShortStopLimit(0, false, sizeHalf, limitPrice, stopPrice, "ATR Short HP (Inv)");
                                if (sizeFull > 0)
                                    atrEntryOrderFull = EnterShortStopLimit(0, false, sizeFull, limitPrice, stopPrice, "ATR Short FP (Inv)");

                                SetStopLoss(CalculationMode.Price, atrLowband + 4); // Inversed Stop
                                if (atrEntryOrderHalf != null)
                                    SetProfitTarget("ATR Short HP (Inv)", CalculationMode.Ticks, halfPT);
                                if (atrEntryOrderFull != null)
                                    SetProfitTarget("ATR Short FP (Inv)", CalculationMode.Ticks, fullPT);

                                BE_Set = false;
                                Trail_Set = false;
                                currentTrailStopPrice = 0;
                                Print($"[DEBUG] INVERSED SHORT SPLIT ENTRY @ Stop={stopPrice}, Limit={limitPrice}: HP={sizeHalf}@{halfPT} ticks, FP={sizeFull}@{fullPT} ticks, SL={atrLowband + 4}");
                            }
                            // INVERSED: ATR SHORT SIGNAL becomes a LONG STOP LIMIT order
                            else if (atrHighbandShortFlag
                                && (atrPrevTouchedLevel == double.MinValue || atrCurrRecordedLevel != atrPrevTouchedLevel || atrLevelRevisitedWithSufficientRetracement)
                                && ATR(Closes[1], 14)[0] < 6)
                            {
                                Print("Setting up an INVERSED Long here (from short signal) using LongStopLimit.");
                                double stopPrice = atrHighband;
                                double limitPrice = stopPrice + (1 * TickSize); // 1-tick offset

                                if (sizeHalf > 0)
                                    atrEntryOrderHalf = EnterLongStopLimit(0, false, sizeHalf, limitPrice, stopPrice, "ATR Long HP (Inv)");
                                if (sizeFull > 0)
                                    atrEntryOrderFull = EnterLongStopLimit(0, false, sizeFull, limitPrice, stopPrice, "ATR Long FP (Inv)");

                                SetStopLoss(CalculationMode.Price, atrHighband - 4); // Inversed Stop
                                if (atrEntryOrderHalf != null)
                                    SetProfitTarget("ATR Long HP (Inv)", CalculationMode.Ticks, halfPT);
                                if (atrEntryOrderFull != null)
                                    SetProfitTarget("ATR Long FP (Inv)", CalculationMode.Ticks, fullPT);

                                BE_Set = false;
                                Trail_Set = false;
                                currentTrailStopPrice = 0;
                                Print($"[DEBUG] INVERSED LONG SPLIT ENTRY @ Stop={stopPrice}, Limit={limitPrice}: HP={sizeHalf}@{halfPT} ticks, FP={sizeFull}@{fullPT} ticks, SL={atrHighband - 4}");
                            }
                        }
                        else // Original Logic
                        {
                            // ORIGINAL: ATR SHORT ENTRY
                            if (atrHighbandShortFlag
                                && (atrPrevTouchedLevel == double.MinValue || atrCurrRecordedLevel != atrPrevTouchedLevel || atrLevelRevisitedWithSufficientRetracement)
                                && ATR(Closes[1], 14)[0] < 10)
                            {
                                Print("Setting up a Short here");
                                double entryP = atrHighband;
                                if (sizeHalf > 0)
                                    atrEntryOrderHalf = EnterShortLimit(0, false, sizeHalf, entryP, "ATR Short HP");
                                if (sizeFull > 0)
                                    atrEntryOrderFull = EnterShortLimit(0, false, sizeFull, entryP, "ATR Short FP");

                                SetStopLoss(CalculationMode.Price, atrHighband + 4);
                                if (atrEntryOrderHalf != null)
                                    SetProfitTarget("ATR Short HP", CalculationMode.Ticks, halfPT);
                                if (atrEntryOrderFull != null)
                                    SetProfitTarget("ATR Short FP", CalculationMode.Ticks, fullPT);

                                BE_Set = false;
                                Trail_Set = false;
                                currentTrailStopPrice = 0;
                                Print($"[DEBUG] SHORT SPLIT ENTRY @ {entryP}: HP={sizeHalf}@{halfPT} ticks, FP={sizeFull}@{fullPT} ticks, SL={atrHighband + 4}");
                            }
                            // ORIGINAL: ATR LONG ENTRY
                            else if (atrLowbandLongFlag
                                && (atrPrevTouchedLevel == double.MinValue || atrCurrRecordedLevel != atrPrevTouchedLevel || atrLevelRevisitedWithSufficientRetracement)
                                && ATR(Closes[1], 14)[0] < 10)
                            {
                                Print("Setting up a Long here");
                                double entryP = atrLowband;
                                if (sizeHalf > 0)
                                    atrEntryOrderHalf = EnterLongLimit(0, false, sizeHalf, entryP, "ATR Long HP");
                                if (sizeFull > 0)
                                    atrEntryOrderFull = EnterLongLimit(0, false, sizeFull, entryP, "ATR Long FP");

                                SetStopLoss(CalculationMode.Price, atrLowband - 4);
                                if (atrEntryOrderHalf != null)
                                    SetProfitTarget("ATR Long HP", CalculationMode.Ticks, halfPT);
                                if (atrEntryOrderFull != null)
                                    SetProfitTarget("ATR Long FP", CalculationMode.Ticks, fullPT);

                                BE_Set = false;
                                Trail_Set = false;
                                currentTrailStopPrice = 0;
                                Print($"[DEBUG] LONG SPLIT ENTRY @ {entryP}: HP={sizeHalf}@{halfPT} ticks, FP={sizeFull}@{fullPT} ticks, SL={atrLowband - 4}");
                            }
                        }
                        // =================================================================
                        // INVERSE LOGIC END
                        // =================================================================
                    }
                }

                // 4) Manage open positions (unchanged)
                if (Position.MarketPosition == MarketPosition.Long)
                {
                    if (Close[0] >= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice + 1.5 * ATR(Closes[1], 14)[0])
                    && !BE_Set)
                    {
                        SetStopLoss(CalculationMode.Price, Position.AveragePrice);
                        BE_Set = true;
                        Print("[DEBUG] Long moved to breakeven at " + Position.AveragePrice);
                    }

                    if (CurrentBar > anchorBar && IsFirstTickOfBar)
                    {
                        double tp = (Open[1] + High[1] + Low[1] + Close[1]) / 4.0;
                        cumPV += tp * Volume[1];
                        cumVol += Volume[1];
                    }
                    anchoredVWAP[0] = cumVol > 0 ? cumPV / cumVol : double.NaN;

                    double minutesSinceEntry = (Time[0] - orderTime).TotalMinutes - BarsPeriod.Value;

                    if (minutesSinceEntry >= 10)
                    {
                        if (Close[0] < anchoredVWAP[0])
                        {
                            ExitLong();
                            Print($"[DEBUG] Exiting Long → Close below AVWAP after {minutesSinceEntry:F2} mins: AVWAP = {anchoredVWAP[0]}");
                            anchorBar = -1;
                            cumPV = cumVol = 0;
                            anchoredVWAP[0] = double.NaN;
                        }
                    }
                    else
                    {
                        if (Close[1] < Close[2] - 0.5 &&
                        Close[2] < Close[3] - 0.5 &&
                        ((Time[0] - orderTime).TotalMinutes - BarsPeriod.Value) > 4)
                        {
                            ExitLong();
                            Print($"[DEBUG] Exiting Long → Bearish PA exit at {((Time[0] - orderTime).TotalMinutes - BarsPeriod.Value):F2} mins");
                            anchorBar = -1;
                            cumPV = cumVol = 0;
                            anchoredVWAP[0] = double.NaN;
                        }
                    }
                }
                else if (Position.MarketPosition == MarketPosition.Short)
                {
                    if (Close[0] <= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice - 1.5 * ATR(Closes[1], 14)[0])
                    && !BE_Set)
                    {
                        SetStopLoss(CalculationMode.Price, Position.AveragePrice);
                        BE_Set = true;
                        Print("[DEBUG] Short moved to breakeven at " + Position.AveragePrice);
                    }

                    if (anchorBar >= 0)
                    {
                        if (CurrentBar > anchorBar && IsFirstTickOfBar)
                        {
                            double tp = (Open[1] + High[1] + Low[1] + Close[1]) / 4.0;
                            cumPV += tp * Volume[1];
                            cumVol += Volume[1];
                        }
                        anchoredVWAP[0] = cumVol > 0 ? cumPV / cumVol : double.NaN;

                        double minutesSinceEntry = (Time[0] - orderTime).TotalMinutes - BarsPeriod.Value;

                        if (minutesSinceEntry >= 10)
                        {
                            if (Close[0] > anchoredVWAP[0])
                            {
                                ExitShort();
                                Print($"[DEBUG] Exiting Short → Close above AVWAP after {minutesSinceEntry:F2} mins: AVWAP = {anchoredVWAP[0]}");
                                anchorBar = -1;
                                cumPV = cumVol = 0;
                                anchoredVWAP[0] = double.NaN;
                            }
                        }
                        else
                        {
                            if (Close[1] - 0.5 > Close[2] &&
                            Close[2] - 0.5 > Close[3] &&
                            ((Time[0] - orderTime).TotalMinutes - BarsPeriod.Value) > 4)
                            {
                                ExitShort();
                                Print($"[DEBUG] Exiting Short → Bullish PA exit at {((Time[0] - orderTime).TotalMinutes - BarsPeriod.Value):F2} mins");
                                anchorBar = -1;
                                cumPV = cumVol = 0;
                                anchoredVWAP[0] = double.NaN;
                            }
                        }
                    }
                }
                else
                {
                    // Flat: clear VWAP anchor
                    if (anchorBar >= 0)
                    {
                        anchorBar = -1;
                        cumPV = cumVol = 0;
                        anchoredVWAP[0] = double.NaN;
                    }
                }
            }
        }

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
                    Print("[DEBUG] ATR Bands set - Price: " +
                    price + ", Band: [" + atrLowband + " - " + atrHighband + "]");
                    break;
                }
            }
        }

        protected override void OnOrderUpdate(Order order,
        double limitPrice, double stopPrice, int quantity, int filled,
        double averageFillPrice, OrderState orderState, DateTime time,
        ErrorCode error, string nativeError)
        {
            // Map backtest → realtime
            if (State == State.Realtime)
            {
                if (atrEntryOrderFull != null && atrEntryOrderFull.IsBacktestOrder)
                    atrEntryOrderFull = GetRealtimeOrder(atrEntryOrderFull);
                if (atrEntryOrderHalf != null && atrEntryOrderHalf.IsBacktestOrder)
                    atrEntryOrderHalf = GetRealtimeOrder(atrEntryOrderHalf);
            }

            // MODIFIED: Track FP leg (handles original and inversed names)
            if (order.Name.Contains("FP"))
            {
                if (order.OrderState == OrderState.Cancelled)
                    atrEntryOrderFull = null;
                else if (atrEntryOrderFull == null)
                    atrEntryOrderFull = order;
                Print($"[DEBUG] FP Order Update: {order.Name} → {order.OrderState} @{limitPrice}");
            }
            // MODIFIED: Track HP leg (handles original and inversed names)
            else if (order.Name.Contains("HP"))
            {
                if (order.OrderState == OrderState.Cancelled)
                    atrEntryOrderHalf = null;
                else if (atrEntryOrderHalf == null)
                    atrEntryOrderHalf = order;
                Print($"[DEBUG] HP Order Update: {order.Name} → {order.OrderState} @{limitPrice}");
            }
        }

        protected override void OnExecutionUpdate(Execution execution,
        string executionId, double price, int quantity,
        MarketPosition marketPosition, string orderId, DateTime time)
        {
            // MODIFIED: Only on FP leg’s first fill (handles original and inversed names)
            if (execution.Order.Name.Contains("FP") && !Trail_Set)
            {
                orderTime = execution.Order.Time;
                anchorBar = CurrentBar;
                cumPV = cumVol = 0;
                BE_Set = false;
                Trail_Set = false;
                currentTrailStopPrice = 0;
                SetATRBands(Close[0]);
                atrOrderDilowband = atrLowband;
                atrOrderDihighband = atrHighband;
                // MODIFIED: Check for "Long" in name to determine next band
                atrOrderDinextband = execution.Order.Name.Contains("Long")
                ? atrNextbandL : atrNextbandS;

                Print($"[DEBUG] FP ORDER FILLED: {execution.Order.Name} at {price}, Position={Position.Quantity}");
            }

            if (execution.Order.OrderState != OrderState.PartFilled)
            {
                // MODIFIED: Handle original and inversed names
                if (execution.Order.Name.Contains("FP"))
                    atrEntryOrderFull = null;
                else if (execution.Order.Name.Contains("HP"))
                    atrEntryOrderHalf = null;
            }
        }

        #region Properties
        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource),
        Name = "Trade size", GroupName = "Parameters", Order = 0)]
        public double TradeSize { get; set; }

        [Range(double.MinValue, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource),
        Name = "Max Loss", GroupName = "Parameters", Order = 1)]
        public double MaxLoss { get; set; }

        [Range(0.1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource),
        Name = "My ATR", GroupName = "Parameters", Order = 2)]
        public double MyATR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RTH Only", Order = 3, GroupName = "Parameters")]
        public bool RTHOnly { get; set; }

        // NEW PARAMETER
        [NinjaScriptProperty]
        [Display(Name = "Inverse Logic?", Order = 4, GroupName = "Parameters")]
        public bool InverseLogic { get; set; }
        #endregion

        protected override void OnAccountItemUpdate(Account account,
        AccountItem accountItem, double value)
        {
            if (accountItem == AccountItem.RealizedProfitLoss)
                AccountRealizedPL = value;
            else if (accountItem == AccountItem.UnrealizedProfitLoss)
                AccountUnrealizedPL = value;
        }
    }
}
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;

#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class NewAVWAP2025 : Strategy
    {
        // Core state
        private double vwPrice;                // Daily VWAP (rounded to tick)
        private double lATR;                   // Convenience distance for entry gating (kept from original flow)
        private bool   LongTradeFlag;
        private bool   ShortTradeFlag;
        private bool   Long2Flag;
        private bool   Short2Flag;

        // Orders (single per signal)
        private Order entryOrder = null;
		private DateTime sessionStartTime = DateTime.MinValue;

        // Day control
        private bool dayOverVar = false;
        private int  lastThreeTrades = 0;

        // Indicators (reused; created once)
        private VWAP1  ofVwapETH;  // Daily VWAP
        private AVWAP2 VWAPx1;     // AVWAP from AnchorFrom
        private AVWAP2 VWAPx2;     // AVWAP from AnchorFrom2

        // For 12:30 tracking (kept)
        private double AnchorFromClose;

        // Time window (kept)
        private bool   ConvertLocalToESTTime = true;
        private double WindowStart1 = 700;
        private double WindowEnd1   = 1505;

        // VWAP touch tracking for deviation logic in VWAP branch
        private int    lastVwapTouchBarIndex = -1;
        private double lastVwapTouchedValue  = double.NaN;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "AVWAP/VWAP strategy. Fixed 2-point stop and 4-point target, 3 consecutive losses day-stop, one order per signal. VWAP branch uses max deviation since last touch ≥ 4×ATR(1m).";
                Name         = "NewAVWAP2025";
                Calculate    = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling       = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 3600;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage            = 0;
                StartBehavior       = StartBehavior.WaitUntilFlat;
                TimeInForce         = TimeInForce.Gtc; // keep GTC as original later setting
                TraceOrders         = false;
				 UseRTH = true;               // trade 8:30–15:00 by default
                RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;
                StopTargetHandling    = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade   = 24;
                IsInstantiatedOnEachOptimizationIteration = true;

                TradeSize = 1; // only 1 trade (no split)
                useVwap   = false;

                AnchorFrom  = DateTime.Parse("12:30 AM");
                AnchorFrom2 = DateTime.Parse("12:30 AM");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 1);
                AddDataSeries(BarsPeriodType.Tick,   1);
            }
            else if (State == State.DataLoaded)
            {
                // Create indicators ONCE and reuse (perf)
                ofVwapETH = VWAP1(BarsArray[0],
                    new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true);
                AddChartIndicator(ofVwapETH);

                VWAPx1 = AVWAP2(BarsArray[0], AnchorFrom,
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true);
                AddChartIndicator(VWAPx1);

                VWAPx2 = AVWAP2(BarsArray[0], AnchorFrom2,
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true);
                AddChartIndicator(VWAPx2);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < BarsRequiredToTrade)
                return;
			
			 if (UseRTH)
            {
                double t = ToTime(Time[0]) / 100.0;
                if (t < 830 || t >= 1500)
                    return;
            }

            DateTime targetTime = Time[0];
            if (ConvertLocalToESTTime)
                TimeZoneInfo.ConvertTime(Time[0], TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));

            double toTime = ToTime(targetTime) / 100.0;
            bool isitEarly = toTime - 1500 >= 0 && toTime - 2100 < 0;
            bool isitRTH   = toTime - WindowStart1 > 0 && toTime - WindowEnd1 < 0;
            isitRTH = true; // keep original override
			
			// Track session start robustly (works across restarts because historical bars are processed)
if (BarsInProgress == 0 && IsFirstTickOfBar && toTime == 1702 )
{
    sessionStartTime = Time[0];
    Print("[DEBUG] Session start detected at: " + sessionStartTime);
	 LongTradeFlag  = false;
                ShortTradeFlag = false;
                Long2Flag      = false;
                Short2Flag     = false;

                dayOverVar = false;
                lastThreeTrades = 0;

                lastVwapTouchBarIndex = -1;
                lastVwapTouchedValue  = double.NaN;

                entryOrder = null;
                Print("[DEBUG] Daily reset completed.");
}

            // Daily reset
            if ((toTime - 1510 == 0 )//|| toTime - 2100 == 0)
				&& (BarsInProgress == 0 && IsFirstTickOfBar))
            {
                LongTradeFlag  = false;
                ShortTradeFlag = false;
                Long2Flag      = false;
                Short2Flag     = false;

                dayOverVar = false;
                lastThreeTrades = 0;

                lastVwapTouchBarIndex = -1;
                lastVwapTouchedValue  = double.NaN;

                entryOrder = null;
                Print("[DEBUG] Daily reset completed.");
            }
            else if (toTime - 700 == 0)
            {
                // kept: no-op from original other than time stamp point
                Print("[DEBUG] 7:00 timestamp reached.");
            }

            // Main trading gate
            if (BarsInProgress == 0
                && IsFirstTickOfBar
                && isitRTH //&& !isitEarly
                && !dayOverVar)
            {
                Print($"[DEBUG] Time: {Time[0]} Close: {Close[0]}");

                // Reuse indicators (perf)
                double VWAPValue = ofVwapETH.Output[0];
                vwPrice = Instrument.MasterInstrument.RoundToTickSize(VWAPValue);
                Print("[DEBUG] Daily VWAP: " + vwPrice);

                double avwap1 = Instrument.MasterInstrument.RoundToTickSize(VWAPx1.Output[0]);
                Print("[DEBUG] AVWAP1: " + avwap1);

                double avwap2 = Instrument.MasterInstrument.RoundToTickSize(VWAPx2.Output[0]);
                Print("[DEBUG] AVWAP2: " + avwap2);

                // ATR (1m) used for gating and deviation calc
                double atr1m = ATR(Closes[1], 14)[0];
                lATR = Instrument.MasterInstrument.RoundToTickSize(1.5 * atr1m);
                Print("[DEBUG] ATR(1m): " + atr1m + " | lATR: " + lATR);

                // Track last VWAP touch (for VWAP branch deviation rule)
                bool barTouchedVWAP = (High[0] >= vwPrice && Low[0] <= vwPrice);// ||(High[1] >= vwPrice && Low[1] <= vwPrice);
                if (barTouchedVWAP)
                {
                    lastVwapTouchBarIndex = CurrentBar;
                    lastVwapTouchedValue  = vwPrice;
                    Print("[DEBUG] VWAP touched. Tracking new touch point.");
                }

                // 3 consecutive losses day-over (only rule)
                UpdateLastThreeTrades();
                if (lastThreeTrades >= 3 && !dayOverVar)
                {
                    dayOverVar = true;
                    Print("[DEBUG] Day over triggered: 3 consecutive losses.");
                    if (Position.MarketPosition == MarketPosition.Long)
                    {
                        ExitLong();
                        Print("[DEBUG] Exiting LONG on day-over.");
                    }
                    else if (Position.MarketPosition == MarketPosition.Short)
                    {
                        ExitShort();
                        Print("[DEBUG] Exiting SHORT on day-over.");
                    }
                    return;
                }

                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    // Clean up any previously tracked order
                    if (entryOrder != null)
                    {
                        try
                        {
                            CancelOrder(entryOrder);
                            entryOrder = null;
                            Print("[DEBUG] Canceled previous working entry order (cleanup).");
                        }
                        catch { }
                    }

                    // Momentum flips reset flags (kept)
                    if (Close[1] > Close[2] && Close[2] > Close[3] && Close[3] > Close[4] && Close[4] > Close[5] && Close[5] > Close[6])
                    {
                        ShortTradeFlag = false;
                        Short2Flag     = false;
                        Print("[DEBUG] Reset Short flags due to 6-bar up sequence.");
                    }
                    if (Close[1] < Close[2] && Close[2] < Close[3] && Close[3] < Close[4] && Close[4] < Close[5] && Close[5] < Close[6])
                    {
                        LongTradeFlag = false;
                        Long2Flag     = false;
                        Print("[DEBUG] Reset Long flags due to 6-bar down sequence.");
                    }

                    // AVWAP 1 block (kept with single order + fixed SL/TP)
                   // if (!useVwap) // use AVWAPs
					if (1==1)
                    {
                        if ((toTime - (ToTime(AnchorFrom) / 100.0)) == 0)
                            AnchorFromClose = Close[0];

                        if (AnchorFrom != DateTime.Parse("12:30 AM"))
                        {
                            if (LongTradeFlag)
                            {
                                double entry = avwap1;
                                SetStopLoss("L 1st", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(entry - 2.0), false);
                                SetProfitTarget("L 1st", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(entry + 4.0));
                                EnterLongLimit(0, false, (int)TradeSize, entry, "L 1st");
                                Print($"[DEBUG] EnterLongLimit L 1st @ {entry} | SL=entry-2 | PT=entry+4");
                            }
                            else if (ShortTradeFlag)
                            {
                                double entry = avwap1;
                                SetStopLoss("S 1st", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(entry + 2.0), false);
                                SetProfitTarget("S 1st", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(entry - 4.0));
                                EnterShortLimit(0, false, (int)TradeSize, entry, "S 1st");
                                Print($"[DEBUG] EnterShortLimit S 1st @ {entry} | SL=entry+2 | PT=entry-4");
                            }
                            else
                            {
                                if (Time[0] > AnchorFrom)
                                {
                                    if ((Close[0] - lATR) > avwap1)
                                    {
                                        LongTradeFlag = true;
                                        Print("[DEBUG] LongTradeFlag set TRUE (AVWAP1 distance gate).");
                                    }
                                    else if ((Close[0] + lATR) < avwap1)
                                    {
                                        ShortTradeFlag = true;
                                        Print("[DEBUG] ShortTradeFlag set TRUE (AVWAP1 distance gate).");
                                    }
                                    else
                                    {
                                        LongTradeFlag = false;
                                        ShortTradeFlag = false;
                                        Print("[DEBUG] AVWAP1 gates not met. Flags cleared.");
                                    }
                                }
                            }
                        }
                    }

                    // AVWAP 2 block (kept with single order + fixed SL/TP)
                    if (!useVwap)
                    {
                        if ((toTime - (ToTime(AnchorFrom2) / 100.0)) == 0)
                            AnchorFromClose = Close[0];

                        if (AnchorFrom2 != DateTime.Parse("12:30 AM"))
                        {
                            if (Long2Flag)
                            {
                                double entry = avwap2;
                                SetStopLoss("L 2nd", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(entry - 2.0), false);
                                SetProfitTarget("L 2nd", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(entry + 4.0));
                                EnterLongLimit(0, false, (int)TradeSize, entry, "L 2nd");
                                Print($"[DEBUG] EnterLongLimit L 2nd @ {entry} | SL=entry-2 | PT=entry+4");
                            }
                            else if (Short2Flag)
                            {
                                double entry = avwap2;
                                SetStopLoss("S 2nd", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(entry + 2.0), false);
                                SetProfitTarget("S 2nd", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(entry - 4.0));
                                EnterShortLimit(0, false, (int)TradeSize, entry, "S 2nd");
                                Print($"[DEBUG] EnterShortLimit S 2nd @ {entry} | SL=entry+2 | PT=entry-4");
                            }
                            else
                            {
                                if (Time[0] > AnchorFrom2)
                                {
                                    if ((Close[0] - lATR) > avwap2)
                                    {
                                        Long2Flag = true;
                                        Print("[DEBUG] Long2Flag set TRUE (AVWAP2 distance gate).");
                                    }
                                    else if ((Close[0] + lATR) < avwap2)
                                    {
                                        Short2Flag = true;
                                        Print("[DEBUG] Short2Flag set TRUE (AVWAP2 distance gate).");
                                    }
                                    else
                                    {
                                        Long2Flag  = false;
                                        Short2Flag = false;
                                        Print("[DEBUG] AVWAP2 gates not met. Flags cleared.");
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // VWAP branch (special change): replace Low/High 5-bar checks with deviation since last touch ≥ 4×ATR
                        Print("[DEBUG] VWAP flag is TRUE (using VWAP branch).");

                        double deviation = 0.0;
                        if (lastVwapTouchBarIndex >= 0)
                        {
                            int barsSinceTouch = CurrentBar - lastVwapTouchBarIndex;
                            deviation = GetMaxDeviationSinceLastTouch(lastVwapTouchedValue, barsSinceTouch);
                            Print($"[DEBUG] Deviation since last VWAP touch: {deviation:F2} vs 4*ATR={4.0 * atr1m:F2}");
                        }

                        bool deviationOK = lastVwapTouchBarIndex >= 0 && deviation >= (4.0 * atr1m);
						Print("lastVwapTouchBarIndex: "+lastVwapTouchBarIndex);
						Print("deviation: "+deviation+" four * atr1m"+4*atr1m);

                        if (Long2Flag)
                        {
                            double entry = vwPrice;
                            SetStopLoss("L 2nd", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(entry - 2.0), false);
                            SetProfitTarget("L 2nd", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(entry + 4.0));
                            EnterLongLimit(0, false, (int)TradeSize, entry, "L 2nd");
                            Print($"[DEBUG] EnterLongLimit L 2nd (VWAP) @ {entry} | SL=entry-2 | PT=entry+4");
                        }
                        else if (Short2Flag)
                        {
                            double entry = vwPrice;
                            SetStopLoss("S 2nd", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(entry + 2.0), false);
                            SetProfitTarget("S 2nd", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(entry - 4.0));
                            EnterShortLimit(0, false, (int)TradeSize, entry, "S 2nd");
                            Print($"[DEBUG] EnterShortLimit S 2nd (VWAP) @ {entry} | SL=entry+2 | PT=entry-4");
                        }
                        else
                        {
                            // Replace prior Low/High 5-bar band checks with deviation rule (keep distance gate with lATR)
                            if ((Close[0] - lATR) > vwPrice && deviationOK)
                            {
                                Long2Flag = true;
                                Print("[DEBUG] Long2Flag set TRUE (VWAP distance + deviation OK).");
                            }
                            else if ((Close[0] + lATR) < vwPrice && deviationOK)
                            {
                                Short2Flag = true;
                                Print("[DEBUG] Short2Flag set TRUE (VWAP distance + deviation OK).");
                            }
                            else
                            {
                               // Long2Flag  = false;
                                //Short2Flag = false;
                                Print("[DEBUG] VWAP gates not met (distance/deviation). Flags cleared.");
                            }
                        }
                    }
                } // end Flat
            } // end main gate
        }

        // Max excursion from "level" since the last touch
        private double GetMaxDeviationSinceLastTouch(double level, int barsSinceTouch)
        {
            if (barsSinceTouch <= 0 || double.IsNaN(level))
                return 0;

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

        // Compute last-three-trades score for the day. -1 for loss, +1 for win.
       // Compute consecutive in-session losses (from most recent backward)
// Triggers day-over when >= 3. Robust across restarts because it uses the session start time.
private void UpdateLastThreeTrades()
{
    // Default
    lastThreeTrades = 0;

    // If session start is unknown yet or no trades exist, nothing to do
    if (sessionStartTime == DateTime.MinValue || SystemPerformance.AllTrades.Count == 0)
        return;

    int consecutiveLosses = 0;

    // Walk the trades backward and only consider trades that exited after sessionStartTime
    for (int idx = SystemPerformance.AllTrades.Count - 1; idx >= 0; idx--)
    {
        Trade t = SystemPerformance.AllTrades[idx];
        if (t == null || t.Exit == null)
            continue;

        // Stop once we're out of this session
        if (t.Exit.Time < sessionStartTime)
            break;

        // Count consecutive losses from most recent; break on first non-loss
        if (t.ProfitCurrency < 0)
            consecutiveLosses++;
        else
		{consecutiveLosses=0;
			break;
		}
        if (consecutiveLosses >= 3)
            break;
    }

    lastThreeTrades = consecutiveLosses;
    Print($"[DEBUG] Consecutive in-session losses since {sessionStartTime}: {lastThreeTrades} (>=3 => day-over)");
}

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
                                              double averageFillPrice, OrderState orderState, DateTime time,
                                              ErrorCode error, string nativeError)
        {
            // Convert historical to live reference once
            if (entryOrder != null && entryOrder.IsBacktestOrder && State == State.Realtime)
                entryOrder = GetRealtimeOrder(entryOrder);

            // Track our entry order reference by name prefix
            if (entryOrder == null && (order.Name.StartsWith("L ") || order.Name.StartsWith("S ")))
                entryOrder = order;

            if (entryOrder != null && (order.Name.StartsWith("L ") || order.Name.StartsWith("S ")))
            {
                if (order.OrderState == OrderState.Cancelled)
                    entryOrder = null;
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
                                                  MarketPosition marketPosition, string orderId, DateTime time)
        {
            Print("OEU: order: " + execution.Order.Name);

            // On fills, clear the flags for that family so we don't resubmit
            if (execution.Order.Name.StartsWith("L 1st") || execution.Order.Name.StartsWith("S 1st"))
            {
                LongTradeFlag  = false;
                ShortTradeFlag = false;
            }
            else if (execution.Order.Name.StartsWith("L 2nd") || execution.Order.Name.StartsWith("S 2nd"))
            {
                Long2Flag  = false;
                Short2Flag = false;
            }

            // Clear entry reference when fully filled/cancelled handled by OnOrderUpdate; no trailing/breakeven logic per requirements.
        }

        #region Properties
        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Trade size", GroupName = "Parameters", Order = 0)]
        public double TradeSize { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
        [Display(Name = "Anchor VWAP from 1st", Order = 1, GroupName = "Parameters")]
        public DateTime AnchorFrom { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
        [Display(Name = "Anchor VWAP from 2nd", Order = 2, GroupName = "Parameters")]
        public DateTime AnchorFrom2 { get; set; }

        [Display(Name = "Use VWAP?", Order = 3, GroupName = "Parameters")]
        public bool useVwap { get; set; }
		
		 [Display(Name = "Use RTH window (8:30-15:00)?", GroupName = "Parameters", Order = 1)]
        public bool UseRTH { get; set; }
        #endregion
    }
}
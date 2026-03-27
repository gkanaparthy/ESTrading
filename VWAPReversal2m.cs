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

// Based on RevAVWAPBiDirect030424.cs — stripped of pivot band complexity.
// Pure VWAP/AVWAP reversal: detects touch-and-reject, enters via stop order,
// structural stop from signal bar, configurable R:R with breakeven.

namespace NinjaTrader.NinjaScript.Strategies
{
    public class VWAPReversal2m : Strategy
    {
        // ── Reversal flags (set each bar, reset each bar) ─────────────
        private bool vwapLongFlag;
        private bool vwapShortFlag;
        private bool avwap1LongFlag;
        private bool avwap1ShortFlag;
        private bool avwap2LongFlag;
        private bool avwap2ShortFlag;

        // ── Day state ─────────────────────────────────────────────────
        private bool   dayOverVar      = false;
        private bool   BE_Set          = false;
        private double PrevDayPnL      = 0;
        private double PrevDayTradeCount = 0;
        private int    DayTradeCount   = 0;
        private double AccountRealizedPL   = 0;
        private double AccountUnrealizedPL = 0;

        // ── Signal bar capture ────────────────────────────────────────
        private double signalBarHigh = 0;
        private double signalBarLow  = 0;

        // ── Order ref ─────────────────────────────────────────────────
        private Order entryOrder = null;
        private DateTime orderTime;

        // ── Indicators ───────────────────────────────────────────────
        private VWAP1  ofVwapETH;
        private AVWAP2 VWAPx1;
        private AVWAP2 VWAPx2;

        private double vwPrice;

        // ─────────────────────────────────────────────────────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "VWAP Reversal 2m — Bi-directional VWAP/AVWAP reversal. " +
                              "Signal: bar crosses VWAP then closes back on same side. " +
                              "Entry: stop order beyond signal bar H/L. Stop: structural (capped). Target: R:R param.";
                Name        = "VWAPReversal2m";

                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 3600;
                IsFillLimitOnTouch           = false;
                MaximumBarsLookBack          = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution          = OrderFillResolution.Standard;
                Slippage                     = 0;
                StartBehavior                = StartBehavior.WaitUntilFlat;
                TimeInForce                  = TimeInForce.Day;
                TraceOrders                  = false;
                RealtimeErrorHandling        = RealtimeErrorHandling.IgnoreAllErrors;
                StopTargetHandling           = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade          = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // ── Default parameter values ─────────────────────────
                TradeSize         = 1;
                MaxStopTicks      = 8;       // structural stop capped here; skip trade if bar wider
                RiskRewardRatio   = 2.0;     // 1:2 default — 6-tick stop → 12-tick target
                BreakevenTicks    = 6;       // move SL to entry after this many ticks profit
                MaxDailyLoss      = -600;    // halt trading for the day
                MaxTradesPerDay   = 4;
                TradeWindowStart  = 830;     // HHMM
                TradeWindowEnd    = 1500;
                UseSessionVWAP    = true;
                UseAVWAP1         = false;
                UseAVWAP2         = false;
                AnchorFrom        = DateTime.Parse("12:30 AM");
                AnchorFrom2       = DateTime.Parse("12:30 AM");
                MinBarsBetweenEntries = 3;   // cooldown bars after a trade

                AddPlot(Brushes.Transparent, "Signal");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(Data.BarsPeriodType.Minute, 1); // required by VWAP1
            }
            else if (State == State.DataLoaded)
            {
                // Session VWAP (always loaded, shown only when UseSessionVWAP = true)
                ofVwapETH = VWAP1(BarsArray[0],
                    new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true);
                if (UseSessionVWAP)
                    AddChartIndicator(ofVwapETH);

                // AVWAP 1
                if (UseAVWAP1 && AnchorFrom != DateTime.Parse("12:30 AM"))
                {
                    VWAPx1 = AVWAP2(BarsArray[0], AnchorFrom,
                        new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                        new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                        true, true, true);
                    AddChartIndicator(VWAPx1);
                }

                // AVWAP 2
                if (UseAVWAP2 && AnchorFrom2 != DateTime.Parse("12:30 AM"))
                {
                    VWAPx2 = AVWAP2(BarsArray[0], AnchorFrom2,
                        new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                        new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                        true, true, true);
                    AddChartIndicator(VWAPx2);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < BarsRequiredToTrade)
                return;

            // Only process the primary bar series
            if (BarsInProgress != 0)
                return;

            double toTime = ToTime(Time[0]) / 100.0;

            // ── End-of-day reset ──────────────────────────────────────
            if ((toTime == 1510 || toTime == 2100) && IsFirstTickOfBar)
            {
                Print("[VWAPRev] EOD reset at " + Time[0]);
                ResetDayFlags();
                PrevDayPnL        = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                PrevDayTradeCount = SystemPerformance.AllTrades.Count;
                DayTradeCount     = 0;
                return;
            }

            // ── Trade window ──────────────────────────────────────────
            bool inWindow = toTime > TradeWindowStart && toTime < TradeWindowEnd;
            if (!inWindow || dayOverVar)
                return;

            // ── Daily loss guard ──────────────────────────────────────
            double todayPnL = SystemPerformance.AllTrades.Count > 0
                ? SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - PrevDayPnL
                : 0;

            if (todayPnL <= MaxDailyLoss || (AccountRealizedPL + AccountUnrealizedPL) <= MaxDailyLoss)
            {
                if (!dayOverVar)
                {
                    Print("[VWAPRev] Max daily loss hit. No more trades today.");
                    dayOverVar = true;
                    if (Position.MarketPosition == MarketPosition.Long)  ExitLong();
                    else if (Position.MarketPosition == MarketPosition.Short) ExitShort();
                }
                return;
            }

            // ── Max trades guard ──────────────────────────────────────
            if (DayTradeCount >= MaxTradesPerDay)
                return;

            // ── Fetch VWAP values ─────────────────────────────────────
            vwPrice = Instrument.MasterInstrument.RoundToTickSize(
                VWAP1(BarsArray[0],
                    new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true).Output[0]);

            double avwap1 = 0, avwap2 = 0;

            if (UseAVWAP1 && VWAPx1 != null)
                avwap1 = Instrument.MasterInstrument.RoundToTickSize(
                    AVWAP2(BarsArray[0], AnchorFrom,
                        new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                        new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                        true, true, true).Output[0]);

            if (UseAVWAP2 && VWAPx2 != null)
                avwap2 = Instrument.MasterInstrument.RoundToTickSize(
                    AVWAP2(BarsArray[0], AnchorFrom2,
                        new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                        new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                        true, true, true).Output[0]);

            // ── Detect signals ────────────────────────────────────────
            ResetEntryFlags();

            // SIGNAL PATTERN (bars indexed from current=0):
            //   Bar[2]: crossed the VWAP (High > VWAP > Low) — the touch bar
            //   Bar[1]: signal bar — closed AWAY from VWAP (confirms direction)
            //   Bar[0]: current bar — we act at the open (stop order placed)
            //
            // LONG setup:  Bar[2] crossed VWAP; Bar[1] closed ABOVE VWAP → price bounced up
            // SHORT setup: Bar[2] crossed VWAP; Bar[1] closed BELOW VWAP → price rejected down

            if (UseSessionVWAP)
                DetectSignal(vwPrice, ref vwapLongFlag, ref vwapShortFlag, "SessionVWAP");

            if (UseAVWAP1 && avwap1 > 0)
                DetectSignal(avwap1, ref avwap1LongFlag, ref avwap1ShortFlag, "AVWAP1");

            if (UseAVWAP2 && avwap2 > 0)
                DetectSignal(avwap2, ref avwap2LongFlag, ref avwap2ShortFlag, "AVWAP2");

            // ── Entry ─────────────────────────────────────────────────
            if (Position.MarketPosition == MarketPosition.Flat && entryOrder == null)
            {
                bool goLong  = vwapLongFlag  || avwap1LongFlag  || avwap2LongFlag;
                bool goShort = vwapShortFlag || avwap1ShortFlag || avwap2ShortFlag;

                // Don't take conflicting signals
                if (goLong && goShort) goLong = goShort = false;

                if (goLong)
                {
                    // Entry 1 tick above signal bar high
                    // Stop  1 tick below signal bar low (+ 1 tick buffer = 2 ticks below low)
                    double rawStopSize = (High[1] - Low[1]) / TickSize + 2; // in ticks

                    if (rawStopSize > MaxStopTicks)
                    {
                        Print(string.Format("[VWAPRev] LONG skipped — bar too wide: {0:F0} ticks > max {1}", rawStopSize, MaxStopTicks));
                        return;
                    }

                    int stopTicks   = (int)Math.Round(rawStopSize);
                    int targetTicks = (int)Math.Round(stopTicks * RiskRewardRatio);

                    double entryTrigger = High[1] + TickSize;

                    SetStopLoss("Long VWAP Rev", CalculationMode.Ticks, stopTicks, false);
                    SetProfitTarget("Long VWAP Rev", CalculationMode.Ticks, targetTicks);

                    entryOrder    = EnterLongStopMarket(0, true, Convert.ToInt32(TradeSize), entryTrigger, "Long VWAP Rev");
                    signalBarHigh = High[1];
                    signalBarLow  = Low[1];
                    BE_Set        = false;

                    Print(string.Format("[VWAPRev] LONG placed | Trigger:{0:F2} | Stop:{1}t | Target:{2}t | R:R 1:{3}",
                        entryTrigger, stopTicks, targetTicks, RiskRewardRatio));
                }
                else if (goShort)
                {
                    // Entry 1 tick below signal bar low
                    // Stop  2 ticks above signal bar high
                    double rawStopSize = (High[1] - Low[1]) / TickSize + 2;

                    if (rawStopSize > MaxStopTicks)
                    {
                        Print(string.Format("[VWAPRev] SHORT skipped — bar too wide: {0:F0} ticks > max {1}", rawStopSize, MaxStopTicks));
                        return;
                    }

                    int stopTicks   = (int)Math.Round(rawStopSize);
                    int targetTicks = (int)Math.Round(stopTicks * RiskRewardRatio);

                    double entryTrigger = Low[1] - TickSize;

                    SetStopLoss("Short VWAP Rev", CalculationMode.Ticks, stopTicks, false);
                    SetProfitTarget("Short VWAP Rev", CalculationMode.Ticks, targetTicks);

                    entryOrder    = EnterShortStopMarket(0, true, Convert.ToInt32(TradeSize), entryTrigger, "Short VWAP Rev");
                    signalBarHigh = High[1];
                    signalBarLow  = Low[1];
                    BE_Set        = false;

                    Print(string.Format("[VWAPRev] SHORT placed | Trigger:{0:F2} | Stop:{1}t | Target:{2}t | R:R 1:{3}",
                        entryTrigger, stopTicks, targetTicks, RiskRewardRatio));
                }
            }

            // ── Breakeven management ──────────────────────────────────
            if (!BE_Set)
            {
                if (Position.MarketPosition == MarketPosition.Long &&
                    Close[0] >= Position.AveragePrice + BreakevenTicks * TickSize)
                {
                    SetStopLoss(CalculationMode.Price, Position.AveragePrice);
                    BE_Set = true;
                    Print("[VWAPRev] LONG BE set @ " + Position.AveragePrice);
                }
                else if (Position.MarketPosition == MarketPosition.Short &&
                         Close[0] <= Position.AveragePrice - BreakevenTicks * TickSize)
                {
                    SetStopLoss(CalculationMode.Price, Position.AveragePrice);
                    BE_Set = true;
                    Print("[VWAPRev] SHORT BE set @ " + Position.AveragePrice);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Detect VWAP touch-and-reject pattern
        //
        //   Bar[2]: must have crossed the VWAP line (touched both sides)
        //   Bar[1]: signal bar — close confirms direction
        //
        //   Long : Bar[1] Low touched VWAP zone AND closed above VWAP
        //   Short: Bar[1] High touched VWAP zone AND closed below VWAP
        // ─────────────────────────────────────────────────────────────
        private void DetectSignal(double vwapVal, ref bool longFlag, ref bool shortFlag, string label)
        {
            longFlag  = false;
            shortFlag = false;

            // Bar[2] must be the touch bar (crossed VWAP)
            bool bar2Touched = Low[2] < vwapVal && High[2] > vwapVal;
            if (!bar2Touched) return;

            // Long: Bar[1] closed above VWAP (bounce confirmed)
            //       and Bar[1] came down close enough to touch VWAP
            if (Close[1] > vwapVal && Low[1] <= vwapVal + 2 * TickSize)
            {
                longFlag = true;
                Print(string.Format("[VWAPRev] {0} LONG signal | VWAP:{1:F2} | Bar[1]Low:{2:F2} | Bar[1]Close:{3:F2}",
                    label, vwapVal, Low[1], Close[1]));
                return;
            }

            // Short: Bar[1] closed below VWAP (rejection confirmed)
            //        and Bar[1] came up close enough to touch VWAP
            if (Close[1] < vwapVal && High[1] >= vwapVal - 2 * TickSize)
            {
                shortFlag = true;
                Print(string.Format("[VWAPRev] {0} SHORT signal | VWAP:{1:F2} | Bar[1]High:{2:F2} | Bar[1]Close:{3:F2}",
                    label, vwapVal, High[1], Close[1]));
            }
        }

        private void ResetEntryFlags()
        {
            vwapLongFlag    = vwapShortFlag    = false;
            avwap1LongFlag  = avwap1ShortFlag  = false;
            avwap2LongFlag  = avwap2ShortFlag  = false;
        }

        private void ResetDayFlags()
        {
            ResetEntryFlags();
            BE_Set     = false;
            dayOverVar = false;
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice,
            OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (entryOrder != null && entryOrder.IsBacktestOrder && State == State.Realtime)
                entryOrder = GetRealtimeOrder(entryOrder);

            if (entryOrder == null &&
                (order.Name.StartsWith("Long") || order.Name.StartsWith("Short")))
                entryOrder = order;

            if (entryOrder != null &&
                (order.Name.StartsWith("Long") || order.Name.StartsWith("Short")) &&
                order.OrderState == OrderState.Cancelled)
                entryOrder = null;
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            Print("[VWAPRev] OEU: " + execution.Order.Name + " @ " + price);

            if (execution.Order.OrderState != OrderState.PartFilled)
                entryOrder = null;

            if (execution.Order.Name.StartsWith("Long") ||
                execution.Order.Name.StartsWith("Short"))
            {
                orderTime = execution.Order.Time;
                BE_Set    = false;
                DayTradeCount++;
                Print("[VWAPRev] Trade #" + DayTradeCount + " of " + MaxTradesPerDay + " today.");
            }
        }

        protected override void OnAccountItemUpdate(Cbi.Account account,
            Cbi.AccountItem accountItem, double value)
        {
            AccountRealizedPL   = account.Get(AccountItem.RealizedProfitLoss,   Currency.UsDollar);
            AccountUnrealizedPL = account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
        }

        // ─────────────────────────────────────────────────────────────
        #region Properties

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Trade Size", GroupName = "1 - Trade", Order = 1)]
        public double TradeSize { get; set; }

        [Range(1, 20), NinjaScriptProperty]
        [Display(Name = "Max Stop Ticks (skip if wider)", GroupName = "2 - Risk", Order = 1)]
        public int MaxStopTicks { get; set; }

        [Range(0.5, 5.0), NinjaScriptProperty]
        [Display(Name = "Risk:Reward Ratio (e.g. 2.0 = 1:2)", GroupName = "2 - Risk", Order = 2)]
        public double RiskRewardRatio { get; set; }

        [Range(1, 20), NinjaScriptProperty]
        [Display(Name = "Breakeven Ticks (move SL to entry)", GroupName = "2 - Risk", Order = 3)]
        public int BreakevenTicks { get; set; }

        [Range(double.MinValue, 0), NinjaScriptProperty]
        [Display(Name = "Max Daily Loss ($, negative)", GroupName = "2 - Risk", Order = 4)]
        public double MaxDailyLoss { get; set; }

        [Range(1, 20), NinjaScriptProperty]
        [Display(Name = "Max Trades Per Day", GroupName = "1 - Trade", Order = 2)]
        public int MaxTradesPerDay { get; set; }

        [Range(0, 2400), NinjaScriptProperty]
        [Display(Name = "Trade Window Start (HHMM, e.g. 830)", GroupName = "1 - Trade", Order = 3)]
        public double TradeWindowStart { get; set; }

        [Range(0, 2400), NinjaScriptProperty]
        [Display(Name = "Trade Window End (HHMM, e.g. 1500)", GroupName = "1 - Trade", Order = 4)]
        public double TradeWindowEnd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Session VWAP", GroupName = "3 - VWAP Sources", Order = 1)]
        public bool UseSessionVWAP { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use AVWAP 1", GroupName = "3 - VWAP Sources", Order = 2)]
        public bool UseAVWAP1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use AVWAP 2", GroupName = "3 - VWAP Sources", Order = 3)]
        public bool UseAVWAP2 { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
        [Display(Name = "AVWAP 1 Anchor Time", GroupName = "3 - VWAP Sources", Order = 4)]
        public DateTime AnchorFrom { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
        [Display(Name = "AVWAP 2 Anchor Time", GroupName = "3 - VWAP Sources", Order = 5)]
        public DateTime AnchorFrom2 { get; set; }

        [Range(1, 10), NinjaScriptProperty]
        [Display(Name = "Min Bars Between Entries (cooldown)", GroupName = "1 - Trade", Order = 5)]
        public int MinBarsBetweenEntries { get; set; }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> Signal { get { return Values[0]; } }

        #endregion
    }
}

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
    public class VWAPBounceATRGuard : Strategy
    {
        // Core state
        private double vwapValue;
        private ATR atr1m;

        // Order tracking
        private Order vwapEntryOrder = null;

        // Time / session controls
        private bool dayOverVar = false;
        private DateTime lastOrderCloseTime = DateTime.MinValue;

        // Deviation gate (since last VWAP touch)
        private int lastVwapTouchBarIndex = -1;
        private double lastVwapTouchedValue = double.NaN;

        // Daily counters
        private double PrevDayPnL = 0;
        private double PrevDayTradeCount = 0;
        private int consecutiveLossesToday = 0;

        // New: desired state and replace flow control
        private enum EntrySide { None, Long, Short }
        private EntrySide wantedSide = EntrySide.None;
        private double wantedPrice = double.NaN;
        private bool awaitingCancelReplace = false;

        // Cache recent gating values for use inside OnOrderUpdate
        private double lastAtrValue = 0;
        private bool lastSufficientDeviation = true;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Daily VWAP bounce strategy with limit orders at VWAP, 2x ATR revisit guard, 10-min cooldown, RTH option, and 3-loss daily stop.";
                Name = "VWAPBounceATRGuard";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 3600;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                BarsRequiredToTrade = 24;

                // Parameters
                TradeSize = 1;
                RTHOnly = true;

                ATRPeriod = 14;
                ATRMultipleForRevisit = 2.0;
                CooldownMinutes = 10;

                StopATRM = 1.0;
                TargetATRM = 1.0;

                // Plot VWAP for visibility
                AddPlot(Brushes.Magenta, "DailyVWAP");
            }
            else if (State == State.Configure)
            {
                // Match template’s additional data series
                AddDataSeries(BarsPeriodType.Minute, 1);
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                atr1m = ATR(Closes[1], ATRPeriod);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (CurrentBars[0] < BarsRequiredToTrade)
                return;

           // if (!IsFirstTickOfBar)
             //   return;

            // RTH-like gating copied from template
            double toTime = ToTime(Time[0]) / 100.0;
            bool isitEarly = toTime >= 1500 && toTime < 2359;
            if (!RTHOnly)
                isitEarly = false;

            Print("====Time: " + Time[0]);

            // Daily reset timing (like template)
            if ((toTime == 1510 || toTime == 2100) && IsFirstTickOfBar)
            {
                PrevDayPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                PrevDayTradeCount = SystemPerformance.AllTrades.Count;
                dayOverVar = false;
                consecutiveLossesToday = 0;

                lastVwapTouchBarIndex = -1;
                lastVwapTouchedValue = double.NaN;

                CancelEntryOrderIfAny();
                Print("[DEBUG] Daily reset - counters cleared and resting order canceled");
            }

            // Compute Daily VWAP exactly like your attached file
            double VWAPValueRaw = VWAP1(
                BarsArray[0],
                new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                true, true, true
            ).Output[0];

            vwapValue = Instrument.MasterInstrument.RoundToTickSize(VWAPValueRaw);
            Values[0][0] = vwapValue;

            // Update "touch" memory (no tolerance)
            bool barTouchedVWAP = High[0] >= vwapValue && Low[0] <= vwapValue;
            if (barTouchedVWAP)
            {
                lastVwapTouchBarIndex = CurrentBar;
                lastVwapTouchedValue = vwapValue;
            }

            // Compute revisit guard (≥ 2x ATR since last touch)
            bool sufficientDeviation = true;
            double currentATR = atr1m[0];
            if (lastVwapTouchBarIndex >= 0)
            {
                int barsSinceTouch = CurrentBar - lastVwapTouchBarIndex;
                double maxDeviation = GetMaxDeviationSinceLastTouch(lastVwapTouchedValue, barsSinceTouch);
                sufficientDeviation = maxDeviation >= (ATRMultipleForRevisit * currentATR);

                if (!sufficientDeviation)
                    Print($"[DEBUG] Insufficient deviation since last VWAP touch: {maxDeviation:F2} < {(ATRMultipleForRevisit * currentATR):F2}");
                else
                    Print($"[DEBUG] Sufficient deviation since last VWAP touch: {maxDeviation:F2} >= {(ATRMultipleForRevisit * currentATR):F2}");
            }

            bool cooldownOk = lastOrderCloseTime == DateTime.MinValue
                              || (Time[0] - lastOrderCloseTime).TotalMinutes >= CooldownMinutes;

            // If we are halted or outside window, cancel any pending order
            if (isitEarly || dayOverVar)
            {
                CancelEntryOrderIfAny();
                return;
            }

            // Cache for OnOrderUpdate replace flow
            lastAtrValue = currentATR;
            lastSufficientDeviation = sufficientDeviation;

            // Decide desired side by current price relative to VWAP
            EntrySide sideNow = EntrySide.None;
            if (Close[0] > vwapValue) sideNow = EntrySide.Long;
            else if (Close[0] < vwapValue) sideNow = EntrySide.Short;

            if (sideNow == EntrySide.None)
            {
                // Exactly at VWAP — avoid placing both; let next bar decide side
                wantedSide = EntrySide.None;
                CancelEntryOrderIfAny();
                return;
            }

            // All gating
            bool okToWork = Position.MarketPosition == MarketPosition.Flat && cooldownOk && sufficientDeviation;

            if (!okToWork)
            {
                CancelEntryOrderIfAny();
                return;
            }

            // Ready to manage/submit the resting limit
            double entryPrice = vwapValue;
            Print(" READY for a trade");

            wantedSide = sideNow;
            wantedPrice = entryPrice;

            if (vwapEntryOrder == null)
            {
                // No working order — submit fresh
                SubmitEntry(wantedSide, wantedPrice, lastAtrValue);
            }
            else
            {
                EntrySide workingSide = OrderSide(vwapEntryOrder);

                if (workingSide == wantedSide)
                {
                    // Same side — update price in-place
                    TryChangeWorkingLimitPrice(wantedPrice);
                }
                else
                {
                    // Side flipped — cancel then replace on Cancelled event
                    CancelEntryOrderForReplace();
                }
            }
        }

        private bool IsWorking(Order o)
        {
            if (o == null) return false;
            return o.OrderState == OrderState.Working
                || o.OrderState == OrderState.Accepted
                || o.OrderState == OrderState.PartFilled;
        }

        private EntrySide OrderSide(Order o)
        {
            if (o == null) return EntrySide.None;
            if (o.OrderAction == OrderAction.Buy) return EntrySide.Long;
            if (o.OrderAction == OrderAction.SellShort) return EntrySide.Short;
            return EntrySide.None;
        }

        private bool PricesDiffer(double a, double b)
        {
            double ra = Instrument.MasterInstrument.RoundToTickSize(a);
            double rb = Instrument.MasterInstrument.RoundToTickSize(b);
            return ra != rb;
        }

        private void SubmitEntry(EntrySide side, double price, double atr)
        {
            price = Instrument.MasterInstrument.RoundToTickSize(price);

            if (side == EntrySide.Long)
            {
                // Your current fixed offsets (2/4)
                SetStopLoss("VWAP Long", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(price - 2), false);
                SetProfitTarget("VWAP Long", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(price + 4));
                EnterLongLimit(0, false, (int)TradeSize, price, "VWAP Long");
                Print($"[DEBUG] Placed BUY LIMIT at VWAP={price} (ATR={atr:F2})");
            }
            else if (side == EntrySide.Short)
            {
                SetStopLoss("VWAP Short", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(price + 2), false);
                SetProfitTarget("VWAP Short", CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(price - 4));
                EnterShortLimit(0, false, (int)TradeSize, price, "VWAP Short");
                Print($"[DEBUG] Placed SELL LIMIT at VWAP={price} (ATR={atr:F2})");
            }
        }

        private void TryChangeWorkingLimitPrice(double newPrice)
        {
            if (!IsWorking(vwapEntryOrder))
                return;

            double oldPrice = vwapEntryOrder.LimitPrice;
            newPrice = Instrument.MasterInstrument.RoundToTickSize(newPrice);

            if (!PricesDiffer(oldPrice, newPrice))
                return;

            // For a limit order, stopPrice is not used. Keep quantity same.
            ChangeOrder(vwapEntryOrder, vwapEntryOrder.Quantity, newPrice, 0);
            Print($"[DEBUG] ChangeOrder: {vwapEntryOrder.Name} {oldPrice} -> {newPrice}");
        }

        private void CancelEntryOrderForReplace()
        {
            if (vwapEntryOrder != null && IsWorking(vwapEntryOrder))
            {
                awaitingCancelReplace = true;
                CancelOrder(vwapEntryOrder);
                Print($"[DEBUG] Cancel for replace: {vwapEntryOrder.Name}");
            }
        }

        private void CancelEntryOrderIfAny()
        {
            // This is a normal cancel (not a replace); clear replace flag.
            awaitingCancelReplace = false;

            if (vwapEntryOrder != null)
            {
                try
                {
                    CancelOrder(vwapEntryOrder);
                    Print($"[DEBUG] Canceled resting entry order: {vwapEntryOrder.Name}");
                }
                catch { }
            }
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

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
                                              double averageFillPrice, OrderState orderState, DateTime time,
                                              ErrorCode error, string nativeError)
        {
            // Ensure we track realtime order reference
            if (vwapEntryOrder != null && vwapEntryOrder.IsBacktestOrder && State == State.Realtime)
                vwapEntryOrder = GetRealtimeOrder(vwapEntryOrder);

            // Track our entry orders
            if (order != null && (order.Name.StartsWith("VWAP Long") || order.Name.StartsWith("VWAP Short")))
            {
                if (order.OrderState == OrderState.Working || order.OrderState == OrderState.Accepted || order.OrderState == OrderState.PartFilled)
                    vwapEntryOrder = order;

                if (order.OrderState == OrderState.Cancelled || order.OrderState == OrderState.Rejected || order.OrderState == OrderState.Filled)
                {
                    // If filled, we’ll be in a position; otherwise remove ref
                    if (order.OrderState != OrderState.Filled)
                        vwapEntryOrder = null;
                }

                // If we were canceling specifically to replace (side flip), place new order immediately
                if (awaitingCancelReplace
                    && order.OrderState == OrderState.Cancelled)
                {
                    // Clear current reference
                    vwapEntryOrder = null;
                    awaitingCancelReplace = false;

                    // Re-check minimal gates to avoid placing when we shouldn't
                    bool cooldownOkNow = lastOrderCloseTime == DateTime.MinValue
                                         || (Time[0] - lastOrderCloseTime).TotalMinutes >= CooldownMinutes;
                    bool okToWorkNow = Position.MarketPosition == MarketPosition.Flat
                                       && !dayOverVar
                                       && cooldownOkNow
                                       && lastSufficientDeviation;

                    if (okToWorkNow && (wantedSide == EntrySide.Long || wantedSide == EntrySide.Short))
                    {
                        SubmitEntry(wantedSide, wantedPrice, lastAtrValue);
                        // vwapEntryOrder will be captured when it transitions to Working/Accepted above
                    }
                    else
                    {
                        Print("[DEBUG] Replace skipped (gates failed) after cancel.");
                    }
                }
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
                                                  MarketPosition marketPosition, string orderId, DateTime time)
        {
            // If an entry just fully filled, clear the entry reference
            if (execution.Order != null && (execution.Order.Name.StartsWith("VWAP Long") || execution.Order.Name.StartsWith("VWAP Short")))
            {
                if (execution.Order.OrderState == OrderState.Filled)
                    vwapEntryOrder = null;
            }

            // When flat, update cooldown and loss streak, stop day after 3 losses
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                lastOrderCloseTime = time;

                if (SystemPerformance.AllTrades.Count > 0)
                {
                    var lastTrade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                    double pnl = lastTrade.ProfitCurrency;
                    if (pnl < 0)
                        consecutiveLossesToday++;
                    else if (pnl > 0)
                        consecutiveLossesToday = 0;

                    if (consecutiveLossesToday >= 3)
                    {
                        dayOverVar = true;
                        CancelEntryOrderIfAny();
                        Print("[DEBUG] Day halted — reached 3 consecutive losses.");
                    }
                }
            }
        }

        #region Properties
        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Trade size", GroupName = "Parameters", Order = 0)]
        public double TradeSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RTH Only", GroupName = "Parameters", Order = 1)]
        public bool RTHOnly { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "ATR Period (1-min)", GroupName = "Parameters", Order = 2)]
        public int ATRPeriod { get; set; }

        [Range(0.1, double.MaxValue), NinjaScriptProperty]
        [Display(Name = "ATR multiple for revisit", GroupName = "Parameters", Order = 3)]
        public double ATRMultipleForRevisit { get; set; }

        [Range(0, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Cooldown (minutes)", GroupName = "Parameters", Order = 4)]
        public int CooldownMinutes { get; set; }

        [Range(0.1, double.MaxValue), NinjaScriptProperty]
        [Display(Name = "Stop (ATR multiples)", GroupName = "Risk", Order = 5)]
        public double StopATRM { get; set; }

        [Range(0.1, double.MaxValue), NinjaScriptProperty]
        [Display(Name = "Target (ATR multiples)", GroupName = "Risk", Order = 6)]
        public double TargetATRM { get; set; }
        #endregion
    }
}
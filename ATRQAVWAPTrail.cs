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
    public class VWAPBounceStrategy : Strategy
    {
        // Core trading variables
        private bool BE_Set = false;
        private double PrevDayPnL = 0, PrevDayTradeCount = 0;
        private bool dayOverVar = false;
        private DateTime lastOrderTime = DateTime.MinValue;
        private bool Trail_Set = false;
        private double currentTrailStopPrice = 0;
        
        // VWAP and direction tracking
        private double vwapValue;
        private double lastVWAPTouch = double.NaN;
        private DateTime lastVWAPTouchTime = DateTime.MinValue;
        private bool priceAboveVWAP = false;
        private bool priceBelowVWAP = false;
        private int barsAboveVWAP = 0;
        private int barsBelowVWAP = 0;
        
        // Entry management
        private Order vwapEntryOrder = null;
        private double maxDeviationFromVWAP = 0;
        private bool sufficientDeviationAchieved = false;
        
        // Loss tracking for daily stop
        private List<double> dailyTrades = new List<double>();
        private int consecutiveLosses = 0;
        private bool tradingStoppedForDay = false;
        
        // ATR for deviation calculation
        private ATR atr14;
        
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"VWAP Bounce Strategy - Long from top, Short from bottom";
                Name = "VWAPBounceStrategy";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 3600;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                BarsRequiredToTrade = 20;
                
                // Strategy parameters
                TradeSize = 1;
                RTHOnly = true;
                ATRMultiplier = 1.0;
                MinWaitMinutes = 10;
                MaxConsecutiveLosses = 3;
                
                // Add VWAP plot
                AddPlot(Brushes.Yellow, "DailyVWAP");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(Data.BarsPeriodType.Minute, 1);
            }
            else if (State == State.DataLoaded)
            {
                atr14 = ATR(14);
            }
        }
        
        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;
                
            if (CurrentBars[0] < BarsRequiredToTrade)
                return;
                
            double toTime = ToTime(Time[0]) / 100.0;
            bool isRTH = toTime >= 930 && toTime <= 1600;
            
            // Skip if RTH only and outside RTH
            if (RTHOnly && !isRTH)
                return;
                
            // Daily reset at market open
            if (toTime == 930 && IsFirstTickOfBar)
            {
                ResetDailyVariables();
            }
            
            // Calculate daily VWAP (same method as original file)
            double VWAPValue = VWAP1(BarsArray[0],
                new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                true, true, true).Output[0];
                
            vwapValue = Instrument.MasterInstrument.RoundToTickSize(VWAPValue);
            Values[0][0] = vwapValue; // Plot VWAP
            
            if (double.IsNaN(vwapValue))
                return;
                
            // Track price direction relative to VWAP
            TrackPriceDirection();
            
            // Update maximum deviation from VWAP
            UpdateMaxDeviation();
            
            // Main trading logic
            if (IsFirstTickOfBar && !dayOverVar && !tradingStoppedForDay)
            {
                EvaluateVWAPEntry();
            }
            
            // Manage open positions
            ManageOpenPositions();
        }
        
        private void ResetDailyVariables()
        {
            PrevDayPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            PrevDayTradeCount = SystemPerformance.AllTrades.Count;
            dayOverVar = false;
            tradingStoppedForDay = false;
            consecutiveLosses = 0;
            dailyTrades.Clear();
            
            // Reset VWAP tracking
            lastVWAPTouch = double.NaN;
            lastVWAPTouchTime = DateTime.MinValue;
            maxDeviationFromVWAP = 0;
            sufficientDeviationAchieved = false;
            barsAboveVWAP = 0;
            barsBelowVWAP = 0;
            
            Print("[DEBUG] Daily reset completed");
        }
        
        private void TrackPriceDirection()
        {
            bool currentlyAbove = Close[0] > vwapValue;
            bool currentlyBelow = Close[0] < vwapValue;
            
            // Track consecutive bars above/below VWAP
            if (currentlyAbove)
            {
                barsAboveVWAP++;
                barsBelowVWAP = 0;
                priceAboveVWAP = true;
                priceBelowVWAP = false;
            }
            else if (currentlyBelow)
            {
                barsBelowVWAP++;
                barsAboveVWAP = 0;
                priceBelowVWAP = true;
                priceAboveVWAP = false;
            }
            
            // Check if price is touching VWAP
            bool touchingVWAP = (High[0] >= vwapValue && Low[0] <= vwapValue) ||
                               Math.Abs(Close[0] - vwapValue) <= Instrument.MasterInstrument.TickSize * 2;
                               
            if (touchingVWAP)
            {
                lastVWAPTouch = vwapValue;
                lastVWAPTouchTime = Time[0];
                Print($"[DEBUG] VWAP touch detected at {vwapValue}, Time: {Time[0]}");
            }
        }
        
        private void UpdateMaxDeviation()
        {
            double currentDeviation = Math.Abs(Close[0] - vwapValue);
            if (currentDeviation > maxDeviationFromVWAP)
            {
                maxDeviationFromVWAP = currentDeviation;
            }
            
            double requiredDeviation = ATRMultiplier * atr14[0];
            if (maxDeviationFromVWAP >= requiredDeviation)
            {
                sufficientDeviationAchieved = true;
                Print($"[DEBUG] Sufficient deviation achieved: {maxDeviationFromVWAP:F2} >= {requiredDeviation:F2}");
            }
        }
        
        private void EvaluateVWAPEntry()
        {
            // Check if enough time has passed since last order
            if ((Time[0] - lastOrderTime).TotalMinutes < MinWaitMinutes)
            {
                Print($"[DEBUG] Waiting period active: {(Time[0] - lastOrderTime).TotalMinutes:F1} < {MinWaitMinutes} minutes");
                return;
            }
            
            // Check if we have sufficient deviation requirement met
            if (!sufficientDeviationAchieved)
            {
                Print($"[DEBUG] Insufficient deviation from VWAP: {maxDeviationFromVWAP:F2} < {ATRMultiplier * atr14[0]:F2}");
                return;
            }
            
            // Cancel any existing entry orders
            if (vwapEntryOrder != null)
            {
                CancelOrder(vwapEntryOrder);
                vwapEntryOrder = null;
            }
            
            // Entry logic: Long when coming from above, Short when coming from below
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                bool touchingVWAP = Math.Abs(Close[0] - vwapValue) <= Instrument.MasterInstrument.TickSize * 3;
                
                // LONG ENTRY: Price coming from above VWAP (was above, now at VWAP)
                if (touchingVWAP && barsAboveVWAP >= 3 && priceAboveVWAP)
                {
                    double entryPrice = vwapValue;
                    EnterLongLimit(0, false, (int)TradeSize, entryPrice, "VWAP Long");
                    
                    double stopLoss = entryPrice - (2 * atr14[0]);
                    double profitTarget = entryPrice + (3 * atr14[0]);
                    
                    SetStopLoss(CalculationMode.Price, stopLoss);
                    SetProfitTarget(CalculationMode.Price, profitTarget);
                    
                    Print($"[DEBUG] LONG ENTRY at VWAP: {entryPrice}, SL: {stopLoss}, PT: {profitTarget}");
                    ResetDeviationTracking();
                }
                // SHORT ENTRY: Price coming from below VWAP (was below, now at VWAP)  
                else if (touchingVWAP && barsBelowVWAP >= 3 && priceBelowVWAP)
                {
                    double entryPrice = vwapValue;
                    EnterShortLimit(0, false, (int)TradeSize, entryPrice, "VWAP Short");
                    
                    double stopLoss = entryPrice + (2 * atr14[0]);
                    double profitTarget = entryPrice - (3 * atr14[0]);
                    
                    SetStopLoss(CalculationMode.Price, stopLoss);
                    SetProfitTarget(CalculationMode.Price, profitTarget);
                    
                    Print($"[DEBUG] SHORT ENTRY at VWAP: {entryPrice}, SL: {stopLoss}, PT: {profitTarget}");
                    ResetDeviationTracking();
                }
            }
        }
        
        private void ResetDeviationTracking()
        {
            maxDeviationFromVWAP = 0;
            sufficientDeviationAchieved = false;
            lastOrderTime = Time[0];
        }
        
        private void ManageOpenPositions()
        {
            if (Position.MarketPosition == MarketPosition.Long)
            {
                // Move to breakeven after 1 ATR profit
                if (Close[0] >= Position.AveragePrice + atr14[0] && !BE_Set)
                {
                    SetStopLoss(CalculationMode.Price, Position.AveragePrice);
                    BE_Set = true;
                    Print("[DEBUG] Long moved to breakeven");
                }
                
                // Exit if back below VWAP after 10 minutes
                double minutesSinceEntry = (Time[0] - lastOrderTime).TotalMinutes;
                if (minutesSinceEntry >= 10 && Close[0] < vwapValue)
                {
                    ExitLong();
                    Print("[DEBUG] Long exit - back below VWAP after 10 minutes");
                }
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                // Move to breakeven after 1 ATR profit
                if (Close[0] <= Position.AveragePrice - atr14[0] && !BE_Set)
                {
                    SetStopLoss(CalculationMode.Price, Position.AveragePrice);
                    BE_Set = true;
                    Print("[DEBUG] Short moved to breakeven");
                }
                
                // Exit if back above VWAP after 10 minutes
                double minutesSinceEntry = (Time[0] - lastOrderTime).TotalMinutes;
                if (minutesSinceEntry >= 10 && Close[0] > vwapValue)
                {
                    ExitShort();
                    Print("[DEBUG] Short exit - back above VWAP after 10 minutes");
                }
            }
        }
        
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, 
            int quantity, int filled, double averageFillPrice, OrderState orderState, 
            DateTime time, ErrorCode error, string nativeError)
        {
            if (vwapEntryOrder != null && vwapEntryOrder.IsBacktestOrder && State == State.Realtime)
                vwapEntryOrder = GetRealtimeOrder(vwapEntryOrder);
                
            if (vwapEntryOrder == null && (order.Name.Contains("VWAP Long") || order.Name.Contains("VWAP Short")))
            {
                vwapEntryOrder = order;
            }
            
            if (vwapEntryOrder != null && order.OrderState == OrderState.Cancelled)
            {
                vwapEntryOrder = null;
                Print("[DEBUG] VWAP entry order cancelled");
            }
        }
        
        protected override void OnExecutionUpdate(Execution execution, string executionId, 
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order.Name.Contains("VWAP"))
            {
                lastOrderTime = time;
                BE_Set = false;
                Trail_Set = false;
                Print($"[DEBUG] VWAP order filled: {execution.Order.Name} at {price}");
            }
            
            // Track trade results for consecutive loss counting
            if (Position.MarketPosition == MarketPosition.Flat && execution.Order.Name.Contains("Exit"))
            {
                double tradeResult = execution.Order.Instrument.MasterInstrument.RoundToTickSize(
                    SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - PrevDayPnL);
                    
                dailyTrades.Add(tradeResult);
                
                if (tradeResult < 0)
                {
                    consecutiveLosses++;
                    Print($"[DEBUG] Loss #{consecutiveLosses}: {tradeResult}");
                    
                    if (consecutiveLosses >= MaxConsecutiveLosses)
                    {
                        tradingStoppedForDay = true;
                        Print($"[DEBUG] Trading stopped for day after {MaxConsecutiveLosses} consecutive losses");
                    }
                }
                else
                {
                    consecutiveLosses = 0; // Reset on winning trade
                    Print($"[DEBUG] Win: {tradeResult} - Consecutive losses reset");
                }
            }
            
            if (execution.Order.OrderState != OrderState.PartFilled)
            {
                if (execution.Order.Name.Contains("VWAP"))
                    vwapEntryOrder = null;
            }
        }
        
        #region Properties
        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(Name = "Trade Size", Order = 1, GroupName = "Parameters")]
        public double TradeSize { get; set; }
        
        [NinjaScriptProperty]
        [Display(Name = "RTH Only", Order = 2, GroupName = "Parameters")]
        public bool RTHOnly { get; set; }
        
        [Range(0.5, 3.0), NinjaScriptProperty]
        [Display(Name = "ATR Multiplier for Deviation", Order = 3, GroupName = "Parameters")]
        public double ATRMultiplier { get; set; }
        
        [Range(5, 60), NinjaScriptProperty]
        [Display(Name = "Min Wait Minutes After Order", Order = 4, GroupName = "Parameters")]
        public int MinWaitMinutes { get; set; }
        
        [Range(2, 10), NinjaScriptProperty]
        [Display(Name = "Max Consecutive Losses", Order = 5, GroupName = "Parameters")]
        public int MaxConsecutiveLosses { get; set; }
        #endregion
    }
}
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
    public class NewATRQOnly : Strategy
    {
        // Core variables for trading
        private bool BE_Set = false;
        private double PrevDayPnL = 0, PrevDayTradeCount = 0;
        private double AccountRealizedPL, AccountUnrealizedPL;
        private bool dayOverVar = false;
        private DateTime orderTime;
        private bool Trail_Set = false;
        private double currentTrailStopPrice = 0;

        // ATR Quartile variables
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

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"ATR Quartiles Trading Strategy";
                Name = "NewATRQOnly";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 3;
                EntryHandling = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 3600;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                BarsRequiredToTrade = 24;
                TradeSize = 1;
                MaxLoss = -75;
                MyATR = 100.0; // Updated default to 100

                // ATR Quartile plots
               // AddPlot(Brushes.Yellow, "ATRNegQ4Q5Mid");    // Below -Q4
                AddPlot(Brushes.Orange, "ATRNegQ4Level");    // -Q4
               // AddPlot(Brushes.Orange, "ATRNegQ3Q4Mid");    // -Q3Q4 Mid
                AddPlot(Brushes.Orange, "ATRNegQ3Level");    // -Q3
               // AddPlot(Brushes.Orange, "ATRNegQ2Q3Mid");    // -Q2Q3 Mid
                AddPlot(Brushes.Orange, "ATRNegQ2Level");    // -Q2
               // AddPlot(Brushes.Orange, "ATRNegQ1Q2Mid");    // -Q1Q2 Mid
                AddPlot(Brushes.Orange, "ATRNegQ1Level");    // -Q1
               // AddPlot(Brushes.Yellow, "ATRNegQ0Q1Mid");    // Below -Q1
                AddPlot(Brushes.Cyan, "ATRSessionOpen");     // Session Open
               // AddPlot(Brushes.Yellow, "ATRQ0Q1Mid");       // Above Q1
                AddPlot(Brushes.Orange, "ATRQ1Level");       // Q1
               // AddPlot(Brushes.Orange, "ATRQ1Q2Mid");       // Q1-Q2 Mid
                AddPlot(Brushes.Orange, "ATRQ2Level");       // Q2
               // AddPlot(Brushes.Orange, "ATRQ2Q3Mid");       // Q2-Q3 Mid
                AddPlot(Brushes.Orange, "ATRQ3Level");       // Q3
               // AddPlot(Brushes.Orange, "ATRQ3Q4Mid");       // Q3-Q4 Mid
                AddPlot(Brushes.Orange, "ATRQ4Level");       // Q4
                //AddPlot(Brushes.Yellow, "ATRQ4Q5Mid");       // Above Q4
            }
            else if (State == State.Configure)
            {
                AddDataSeries(Data.BarsPeriodType.Minute, 1);
                AddDataSeries(Data.BarsPeriodType.Tick, 1);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < BarsRequiredToTrade) return;

            double toTime = ToTime(Time[0]) / 100.0;
            bool isitEarly = toTime >= 1500 && toTime < 2359;
            bool isitRTH = true; // Always true in this context

            // Reset at specific times (15:10 or 21:00)
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

                Print("[DEBUG] Daily reset at " + toTime + " - All trading flags reset");
            }

            // PnL-based exit logic
            if (SystemPerformance.AllTrades.Count > 0 && !dayOverVar)
            {
                double cumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - PrevDayPnL;
                if (AccountRealizedPL < MaxLoss || AccountRealizedPL > 200 * TradeSize || AccountUnrealizedPL < -500)
                {
                    dayOverVar = true;

                    if (Position.MarketPosition == MarketPosition.Long) ExitLong();
                    else if (Position.MarketPosition == MarketPosition.Short) ExitShort();
                    Print("[DEBUG] Day trading stopped - PnL limits hit: RealizedPL=" + AccountRealizedPL + ", UnrealizedPL=" + AccountUnrealizedPL);
                }
            }
			
			// Store session open at 8:32 AM (changed from 8:30)
                if (toTime == 1702 && BarsInProgress == 0 && IsFirstTickOfBar)
                {
                    sessionOpen = Open[0];
                    Print("[DEBUG] Session Open " + sessionOpen);
                }

            // Main trading logic
            if (BarsInProgress == 0 && IsFirstTickOfBar && isitRTH && !isitEarly && toTime > 830 && !dayOverVar)
            {
                

                // Calculate and plot ATR quartile levels
                if (sessionOpen > 0) // Only calculate if we have session open
                {
                    // Calculate ATR quartile levels
                    atrQ1Level = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.25));
                    atrQ2Level = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.50));
                    atrQ3Level = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.75));
                    atrQ4Level = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + MyATR);

                    // Calculate mid-levels
					/*
                    atrQ0Q1midLevel = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.125));
                    atrQ1Q2midLevel = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.375));
                    atrQ2Q3midLevel = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.625));
                    atrQ3Q4midLevel = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.875));
                    atrQ4Q5midLevel = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 1.125));
*/
                    // Create corresponding negative levels (below session open)
                    List<double> positiveLevels = new List<double> { 
                         atrQ1Level, atrQ2Level, 
                        atrQ3Level, atrQ4Level 
                    };

                    // Store all ATR levels (both positive and negative from session open)
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
                   // Values[0][0] = sessionOpen - (MyATR * 1.125);    // Below -Q4
                    Values[0][0] = sessionOpen - MyATR;              // -Q4
                   // Values[2][0] = sessionOpen - (MyATR * 0.875);    // -Q3Q4 Mid
                    Values[1][0] = sessionOpen - (MyATR * 0.75);     // -Q3
                   // Values[4][0] = sessionOpen - (MyATR * 0.625);    // -Q2Q3 Mid
                    Values[2][0] = sessionOpen - (MyATR * 0.50);     // -Q2
                   // Values[6][0] = sessionOpen - (MyATR * 0.375);    // -Q1Q2 Mid
                    Values[3][0] = sessionOpen - (MyATR * 0.25);     // -Q1
                   // Values[8][0] = sessionOpen - (MyATR * 0.125);    // Below -Q1
                    Values[4][0] = sessionOpen;                       // Session Open
                  //  Values[10][0] = sessionOpen + (MyATR * 0.125);   // Above Q1
                    Values[5][0] = sessionOpen + (MyATR * 0.25);    // Q1
                   // Values[12][0] = sessionOpen + (MyATR * 0.375);   // Q1Q2 Mid
                    Values[6][0] = sessionOpen + (MyATR * 0.50);    // Q2
                   // Values[14][0] = sessionOpen + (MyATR * 0.625);   // Q2Q3 Mid
                    Values[7][0] = sessionOpen + (MyATR * 0.75);    // Q3
                   // Values[16][0] = sessionOpen + (MyATR * 0.875);   // Q3Q4 Mid
                    Values[8][0] = sessionOpen + MyATR;             // Q4
                   // Values[18][0] = sessionOpen + (MyATR * 1.125);   // Above Q4
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
                    // Update the dictionary with current bar index
                    lastATRLevelTouchBarIndex[atrNowLevel] = CurrentBar;
                    Print("[DEBUG] ATR Level " + atrNowLevel + " touched at bar " + CurrentBar);
                }

                // Process ATR level touch
                if (!double.IsNaN(atrNowLevel))
                {
                    // Store last ATR level info
                    atrPrevTouchedLevel = atrCurrRecordedLevel;
                    atrCurrRecordedLevel = atrNowLevel;
                }

                // Entry logic
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    // ATR Entry Logic
                    if (atrLevels != null)
                    {
                        SetATRBands(Close[0]);
                        
                        if (atrEntryOrder != null)
                        {
                            CancelOrder(atrEntryOrder);
                            atrEntryOrder = null;
                            Print("[DEBUG] Cancelled previous ATR entry order");
                        }

                        // Set ATR entry flags
                        atrLowbandLongFlag = atrEntryOrder == null && (Close[0] - atrLowband) < (atrHighband - Close[0]);
                        atrHighbandShortFlag = atrEntryOrder == null && (Close[0] - atrLowband) > (atrHighband - Close[0]);

                        bool atrLevelRevisitedWithSufficientRetracement = false;
                        double currentATR = ATR(Closes[1], 14)[0];

                        // Check ATR level retracement
                        if (lastATRLevelTouchBarIndex.ContainsKey(atrCurrRecordedLevel))
                        {
                            int barsSinceTouch = CurrentBar - lastATRLevelTouchBarIndex[atrCurrRecordedLevel];
                            double atrDeviation = GetMaxDeviationSinceLastTouch(atrCurrRecordedLevel, barsSinceTouch);
                            atrLevelRevisitedWithSufficientRetracement = atrDeviation > 3 * currentATR;
                            
                            if (atrLevelRevisitedWithSufficientRetracement)
                                Print("[DEBUG] ATR Level " + atrCurrRecordedLevel + " has sufficient retracement: " + atrDeviation + " > " + (3 * currentATR));
                        }

                        // ATR SHORT ENTRY
                        if (atrEntryOrder == null && atrHighbandShortFlag 
                            && (atrPrevTouchedLevel == double.MinValue || atrCurrRecordedLevel != atrPrevTouchedLevel || atrLevelRevisitedWithSufficientRetracement)
                            && ATR(Closes[1], 14)[0] < 10)
                        {
                            double atrAdjEntry = atrHighband;
                            EnterShortLimit(0, false, Convert.ToInt32(TradeSize), atrAdjEntry, "ATR Short");
                            atrOrderDilowband = atrLowband;
                            atrOrderDihighband = atrHighband;
                            atrOrderDinextband = atrNextbandS;
                            SetStopLoss(CalculationMode.Price, atrHighband + 4);
                            SetProfitTarget("ATR Short", CalculationMode.Ticks, Math.Min(4 * 20, 4 * (atrHighband - atrLowband)));
                            BE_Set = false;
                            Trail_Set = false;
                            currentTrailStopPrice = 0;
                            Print("[DEBUG] SHORT ENTRY SIGNAL: Price=" + atrAdjEntry + ", SL=" + (atrHighband + 4) + ", PT=20 points, CurrentATR=" + currentATR);
                        }
                        // ATR LONG ENTRY
                        else if (atrEntryOrder == null && atrLowbandLongFlag
                            && (atrPrevTouchedLevel == double.MinValue || atrCurrRecordedLevel != atrPrevTouchedLevel || atrLevelRevisitedWithSufficientRetracement)
                            && ATR(Closes[1], 14)[0] < 10)
                        {
                            double atrAdjEntry = atrLowband;
                            EnterLongLimit(0, false, Convert.ToInt32(TradeSize), atrAdjEntry, "ATR Long");
                            atrOrderDilowband = atrLowband;
                            atrOrderDihighband = atrHighband;
                            atrOrderDinextband = atrNextbandL;
                            SetStopLoss(CalculationMode.Price, atrLowband - 4);
                            SetProfitTarget("ATR Long", CalculationMode.Ticks, Math.Min(4 * 20, 4 * (atrHighband - atrLowband)));
                            BE_Set = false;
                            Trail_Set = false;
                            currentTrailStopPrice = 0;
                            Print("[DEBUG] LONG ENTRY SIGNAL: Price=" + atrAdjEntry + ", SL=" + (atrLowband - 4) + ", PT=20 points, CurrentATR=" + currentATR);
                        }
                    }
                }

                // Manage open positions
                if (Position.MarketPosition == MarketPosition.Long)
                {
                    if (Close[0] >= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice + ATR(Closes[1], 14)[0]) && !BE_Set)
                    {
                        SetStopLoss(CalculationMode.Price, Position.AveragePrice);
                        BE_Set = true;
                        Print("[DEBUG] Long position moved to breakeven at " + Position.AveragePrice);
                    }
                    
                    if (Close[1] < Close[2] - 0.5 && Close[2] < Close[3] - 0.5 && ((Time[0] - orderTime).TotalMinutes - BarsPeriod.Value) > 4)
                    {
                        ExitLong();
                        Print("[DEBUG] Exiting Long - Bearish price action detected after " + ((Time[0] - orderTime).TotalMinutes - BarsPeriod.Value) + " minutes");
                    }
                    
                    // Trailing stop logic
                    double triggerPriceLong = Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice + 10);
                    if (Close[0] >= triggerPriceLong)
                    {
                        if (!Trail_Set)
                        {
                            Trail_Set = true;
                            currentTrailStopPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] - 10);
                            SetStopLoss(CalculationMode.Price, currentTrailStopPrice);
                            Print("[DEBUG] Trailing stop activated for Long at " + currentTrailStopPrice);
                        }
                        else
                        {
                            double newTrail = Instrument.MasterInstrument.RoundToTickSize(Close[0] - 10);
                            if (newTrail > currentTrailStopPrice)
                            {
                                currentTrailStopPrice = newTrail;
                                SetStopLoss(CalculationMode.Price, currentTrailStopPrice);
                                Print("[DEBUG] Trailing stop updated for Long to " + currentTrailStopPrice);
                            }
                        }
                    }
                }
                else if (Position.MarketPosition == MarketPosition.Short)
                {
                    if (Close[0] <= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice - ATR(Closes[1], 14)[0]) && !BE_Set)
                    {
                        SetStopLoss(CalculationMode.Price, Position.AveragePrice);
                        BE_Set = true;
                        Print("[DEBUG] Short position moved to breakeven at " + Position.AveragePrice);
                    }
                    
                    if (Close[1] - 0.5 > Close[2] && Close[2] - 0.5 > Close[3] && ((Time[0] - orderTime).TotalMinutes - BarsPeriod.Value) > 4)
                    {
                        ExitShort();
                        Print("[DEBUG] Exiting Short - Bullish price action detected after " + ((Time[0] - orderTime).TotalMinutes - BarsPeriod.Value) + " minutes");
                    }
                    
                    // Trailing stop logic
                    double triggerPriceShort = Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice - 10);
                    if (Close[0] <= triggerPriceShort)
                    {
                        if (!Trail_Set)
                        {
                            Trail_Set = true;
                            currentTrailStopPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] + 10);
                            SetStopLoss(CalculationMode.Price, currentTrailStopPrice);
                            Print("[DEBUG] Trailing stop activated for Short at " + currentTrailStopPrice);
                        }
                        else
                        {
                            double newTrail = Instrument.MasterInstrument.RoundToTickSize(Close[0] + 10);
                            if (newTrail < currentTrailStopPrice)
                            {
                                currentTrailStopPrice = newTrail;
                                SetStopLoss(CalculationMode.Price, currentTrailStopPrice);
                                Print("[DEBUG] Trailing stop updated for Short to " + currentTrailStopPrice);
                            }
                        }
                    }
                }
            }
        }

        // Function to get maximum deviation from a level over the last n bars
        private double GetMaxDeviationSinceLastTouch(double level, int barsSinceTouch)
        {
            // Limit the lookback to available bars and the bars since touch
            int lookback = Math.Min(barsSinceTouch, CurrentBar);

            // Initialize max deviation
            double maxDeviation = 0;

            // Loop through the bars
            for (int i = 0; i < lookback; i++)
            {
                // Calculate upward deviation (high above level)
                double upDeviation = High[i] - level;

                // Calculate downward deviation (level above low)
                double downDeviation = level - Low[i];

                // Find maximum deviation for this bar (in either direction)
                double barDeviation = Math.Max(upDeviation, downDeviation);

                // Update max deviation if this bar has larger deviation
                if (barDeviation > maxDeviation)
                {
                    maxDeviation = barDeviation;
                }
            }

            return maxDeviation;
        }

        // ATR Band Setting Method
        private void SetATRBands(double price)
        {
            if (atrLevels == null || atrLevels.Count == 0) return;

            // Find which ATR band the price is in
            for (int i = 0; i < atrLevels.Count - 1; i++)
            {
                if (price >= atrLevels[i] && price < atrLevels[i + 1])
                {
                    atrLowband = atrLevels[i];
                    atrHighband = atrLevels[i + 1];
                    atrNextbandL = (i + 2 < atrLevels.Count) ? atrLevels[i + 2] : atrLevels[i + 1];
                    atrNextbandS = (i - 1 >= 0) ? atrLevels[i - 1] : atrLevels[i];
                    
                    Print("[DEBUG] ATR Bands set - Current price: " + price + ", Band: [" + atrLowband + " - " + atrHighband + "]");
                    break;
                }
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            // Handle ATR orders
            if (atrEntryOrder != null && atrEntryOrder.IsBacktestOrder && State == State.Realtime)
                atrEntryOrder = GetRealtimeOrder(atrEntryOrder);

            // Handle ATR orders
            if (atrEntryOrder == null && (order.Name.StartsWith("ATR Long") || order.Name.StartsWith("ATR Short")))
            {
                atrEntryOrder = order;
                Print("[DEBUG] Order placed: " + order.Name + " at " + limitPrice);
            }

            // Handle ATR order cancellation
            if (atrEntryOrder != null && order.OrderState == OrderState.Cancelled)
            {
                atrEntryOrder = null;
                Print("[DEBUG] Order cancelled: " + order.Name);
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            // Handle ATR order executions
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
                Print("[DEBUG] ORDER FILLED: " + execution.Order.Name + " at " + price + ", Position: " + Position.Quantity + " contracts");
            }

            if (execution.Order.OrderState != OrderState.PartFilled)
            {
                // Handle ATR order completion
                if (execution.Order.Name.StartsWith("ATR"))
                    atrEntryOrder = null;
            }
        }

        #region Properties
        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Trade size", GroupName = "Parameters", Order = 0)]
        public double TradeSize { get; set; }

        [Range(double.MinValue, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Max Loss", GroupName = "Parameters", Order = 1)]
        public double MaxLoss { get; set; }

        [Range(0.1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "My ATR", GroupName = "Parameters", Order = 2)]
        public double MyATR { get; set; }

        // ATR Quartile Series
      //  [XmlIgnore] public Series<double> ATRNegQ4Q5Mid => Values[0];
        [XmlIgnore] public Series<double> ATRNegQ4Level => Values[0];
       // [XmlIgnore] public Series<double> ATRNegQ3Q4Mid => Values[2];
        [XmlIgnore] public Series<double> ATRNegQ3Level => Values[1];
       // [XmlIgnore] public Series<double> ATRNegQ2Q3Mid => Values[4];
        [XmlIgnore] public Series<double> ATRNegQ2Level => Values[2];
      //  [XmlIgnore] public Series<double> ATRNegQ1Q2Mid => Values[6];
        [XmlIgnore] public Series<double> ATRNegQ1Level => Values[3];
      //  [XmlIgnore] public Series<double> ATRNegQ0Q1Mid => Values[8];
        [XmlIgnore] public Series<double> ATRSessionOpen => Values[4];
      //  [XmlIgnore] public Series<double> ATRQ0Q1Mid => Values[10];
        [XmlIgnore] public Series<double> ATRQ1Level => Values[5];
      //  [XmlIgnore] public Series<double> ATRQ1Q2Mid => Values[12];
        [XmlIgnore] public Series<double> ATRQ2Level => Values[6];
       // [XmlIgnore] public Series<double> ATRQ2Q3Mid => Values[14];
        [XmlIgnore] public Series<double> ATRQ3Level => Values[7];
       // [XmlIgnore] public Series<double> ATRQ3Q4Mid => Values[16];
        [XmlIgnore] public Series<double> ATRQ4Level => Values[8];
       // [XmlIgnore] public Series<double> ATRQ4Q5Mid => Values[18];
        #endregion

        protected override void OnAccountItemUpdate(Account account, AccountItem accountItem, double value)
        {
            AccountRealizedPL = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
            AccountUnrealizedPL = account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
        }
    }
}
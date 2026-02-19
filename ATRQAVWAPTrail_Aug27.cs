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
    public class ATRQAVWAPTrail : Strategy
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
        private double sessionOpen, vwapValue;
        private double atrLowband, atrHighband, atrNextbandL, atrNextbandS;
        private bool atrLowbandLongFlag, atrHighbandLongFlag, atrLowbandShortFlag, atrHighbandShortFlag;
        private Order atrEntryOrder = null;
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
        
        private ATR dailyATR;
        
        private ATR10D dailyATR1;
        //private ATR_RMA dailyATR2;

        private DateTime lastOrderCloseTime = DateTime.MinValue;

        // New: SMA(925) filter
        private SMA sma925;
        private const int SMA925Period = 1850/2;//BarsPeriod.Value;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"ATR Quartiles Trading Strategy";
                Name = "ATRQAVWAPTrail";
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
                MaxLoss = -750;
                MyATR = 100.0;
                RTHOnly = true;

                // New: tolerance for “near band” checks (VWAP and SMA)
                NearBandTolerance = 3;

                // ATR Quartile plots (±1ATR only)
                AddPlot(Brushes.Orange, "ATRNegQ4Level"); // -Q4
                AddPlot(Brushes.Orange, "ATRNegQ3Level"); // -Q3
                AddPlot(Brushes.Orange, "ATRNegQ2Level"); // -Q2
                AddPlot(Brushes.Orange, "ATRNegQ1Level"); // -Q1
                AddPlot(Brushes.Cyan,   "ATRSessionOpen"); // Session Open
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
                //AddDataSeries(Data.BarsPeriodType.Day, 1);
            }
            else if (State == State.DataLoaded)
            {
                anchoredVWAP = Values[9];
                cumPV = 0;
                cumVol = 0;
                anchorBar = -1;

                // New: create SMA(925) and add to chart for visual debugging
                sma925 = SMA(SMA925Period);
                AddChartIndicator(sma925);
                //dailyATR = ATR(BarsArray[3], 10);
                
                //dailyATR1 = ATR10D(BarsArray[3], 10);
                
                //dailyATR2 = ATR_RMA(BarsArray[3], 10);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            // Ensure we have enough history for the SMA to be meaningful
            if (CurrentBars[0] < Math.Max(BarsRequiredToTrade, SMA925Period))
                return;

            // Always clear VWAP by default
            anchoredVWAP[0] = double.NaN;

            double toTime = ToTime(Time[0]) / 100.0;
            bool isitEarly = toTime >= 1500 && toTime < 2359;
            if (!RTHOnly)
                isitEarly = false;

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

            // Store session open at 8:32 AM
            if (toTime == 1702 && IsFirstTickOfBar)
            {
                sessionOpen = Open[0];
                Print("[DEBUG] Session Open " + sessionOpen);
            }

            // Main trading logic
            if (IsFirstTickOfBar && !isitEarly && !dayOverVar)
            {
                Print("++++ Time ++++ " + Time[0]);
                
                //double currentDailyATR = dailyATR[0];
                
                //Print("currentDailyATR "+currentDailyATR);
                
                //Print("Day Open: "+Opens[3][0]);

                // Daily VWAP
                double VWAPValue = VWAP1(BarsArray[0],
                    new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true).Output[0];

                Print("Daily VWAP " + VWAPValue.ToString());
                vwapValue = Instrument.MasterInstrument.RoundToTickSize(VWAPValue);

                // SMA(925)
                double sma925ValueRaw = sma925[0];
                double sma925Value = Instrument.MasterInstrument.RoundToTickSize(sma925ValueRaw);
                Print("SMA(925) " + sma925Value.ToString());

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
                    Values[0][0] = sessionOpen - MyATR;                // -Q4
                    Values[1][0] = sessionOpen - (MyATR * 0.75);       // -Q3
                    Values[2][0] = sessionOpen - (MyATR * 0.50);       // -Q2
                    Values[3][0] = sessionOpen - (MyATR * 0.25);       // -Q1
                    Values[4][0] = sessionOpen;                        // Session Open
                    Values[5][0] = sessionOpen + (MyATR * 0.25);       // Q1
                    Values[6][0] = sessionOpen + (MyATR * 0.50);       // Q2
                    Values[7][0] = sessionOpen + (MyATR * 0.75);       // Q3
                    Values[8][0] = sessionOpen + MyATR;                // Q4
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
                        Print("[DEBUG] ATR Level " + atrNowLevel + " touched at bar " + CurrentBar);
                        atrPrevTouchedLevel = atrCurrRecordedLevel;
                        atrCurrRecordedLevel = atrNowLevel;
                    }
                }

                // 3) Entry logic with ±2ATR guard and triple confirmation (ATR + VWAP + SMA925)
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
                        double entryVar=0;
                        if (atrEntryOrder != null)
                        {
                            CancelOrder(atrEntryOrder);
                            atrEntryOrder = null;
                            Print("[DEBUG] Cancelled previous ATR entry order");
                            entryVar=1;
                        }
                        
                        if (entryVar==0)
                        EvaluateAtrEntryLogic(sma925Value);

                        
                    }
                }

                // 4) Manage open positions (unchanged)
                if (Position.MarketPosition == MarketPosition.Long)
                {
                    if (Close[0] >= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice + ATR(Closes[1], 14)[0]) && !BE_Set)
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
                            Print($"[DEBUG] Exiting Long → Close below AVWAP after {minutesSinceEntry:F2} mins: AVWAP = " + anchoredVWAP[0]);
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
                    if (Close[0] <= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice - ATR(Closes[1], 14)[0]) && !BE_Set)
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
                                Print($"[DEBUG] Exiting Short → Close above AVWAP after {minutesSinceEntry:F2} mins: AVWAP = " + anchoredVWAP[0]);
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
        
        

        // Function to get maximum deviation from a level over the last n bars
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
        private void EvaluateAtrEntryLogic(double sma925Value)
        {
            SetATRBands(Close[0]);
                        atrLowbandLongFlag  = atrEntryOrder == null && (Close[0] - atrLowband) < (atrHighband - Close[0]);
                        atrHighbandShortFlag = atrEntryOrder == null && (Close[0] - atrLowband) > (atrHighband - Close[0]);

                        bool atrLevelRevisitedWithSufficientRetracement = false;
                        double currentATR = ATR(Closes[1], 14)[0];
            Print("last touch: "+lastATRLevelTouchBarIndex.TryGetValue(atrCurrRecordedLevel, out int lastTouch));
            Print("atrCurrRecordedLevel "+atrCurrRecordedLevel);
                        if (lastATRLevelTouchBarIndex.TryGetValue(atrCurrRecordedLevel, out  lastTouch))
                        {
                            int barsSinceTouch = CurrentBar - lastTouch;
                            double atrDeviation = GetMaxDeviationSinceLastTouch(atrCurrRecordedLevel, barsSinceTouch);
                            atrLevelRevisitedWithSufficientRetracement = atrDeviation > 2 * currentATR;
                              Print("[DEBUG] ATR Level " + atrCurrRecordedLevel + " DOESNT have sufficient retracement: " + atrDeviation + " < " + (2 * currentATR));
                            if (atrLevelRevisitedWithSufficientRetracement)
                                Print("[DEBUG] ATR Level " + atrCurrRecordedLevel + " has sufficient retracement: " + atrDeviation + " > " + (2 * currentATR));
                        }

                        // New: helper booleans for near-band alignment with direction
                        bool vwapNearLow  = Math.Abs(vwapValue   - atrLowband)  <= NearBandTolerance && vwapValue   < atrLowband;
                        bool vwapNearHigh = Math.Abs(vwapValue   - atrHighband) <= NearBandTolerance && vwapValue   > atrHighband;
                        bool smaNearLow   = !double.IsNaN(sma925Value) && Math.Abs(sma925Value - atrLowband)  <= NearBandTolerance && sma925Value < atrLowband;
                        bool smaNearHigh  = !double.IsNaN(sma925Value) && Math.Abs(sma925Value - atrHighband) <= NearBandTolerance && sma925Value > atrHighband;

                        // ATR SHORT ENTRY (requires VWAP and SMA(925) confirmation near atrHighband)
                       if (atrEntryOrder == null && atrHighbandShortFlag
                            && (atrPrevTouchedLevel == double.MinValue
                                || atrCurrRecordedLevel != atrPrevTouchedLevel
                                || atrLevelRevisitedWithSufficientRetracement)
                            && ATR(Closes[1], 14)[0] < 6
                           // && (Time[0] - lastOrderCloseTime).TotalMinutes >= 5
                           )
                        {
                            double atrAdjEntry = atrHighband;

                            if (vwapNearHigh) atrAdjEntry = Math.Max(atrAdjEntry, vwapValue);
                            if (smaNearHigh)  atrAdjEntry = Math.Max(atrAdjEntry, sma925Value);
                        
                            atrAdjEntry = Instrument.MasterInstrument.RoundToTickSize(atrAdjEntry);

                            EnterShortLimit(0, false, (int)TradeSize, atrAdjEntry, "ATR Short");
                            atrOrderDilowband = atrLowband;
                            atrOrderDihighband = atrHighband;
                            atrOrderDinextband = atrNextbandS;

                            SetStopLoss(CalculationMode.Price, atrHighband + 3);
                            SetProfitTarget("ATR Short", CalculationMode.Ticks,
                                Math.Min(4 * 20, 4 * (atrAdjEntry - atrLowband)) / 2);

                            BE_Set = Trail_Set = false;
                            currentTrailStopPrice = 0;
                            Print("[DEBUG] SHORT ENTRY SIGNAL (ATR+VWAP+SMA): Price=" + atrAdjEntry + ", SL=" + (atrHighband + 4));
                        }
                        // ATR LONG ENTRY (requires VWAP and SMA(925) confirmation near atrLowband)
                        else if (atrEntryOrder == null && atrLowbandLongFlag
                            && (atrPrevTouchedLevel == double.MinValue
                                || atrCurrRecordedLevel != atrPrevTouchedLevel
                                || atrLevelRevisitedWithSufficientRetracement)
                            && ATR(Closes[1], 14)[0] < 6
                           // && (Time[0] - lastOrderCloseTime).TotalMinutes >= 5
                           )
                        {
                            double atrAdjEntry = atrLowband;

                            if (vwapNearLow) atrAdjEntry = Math.Min(atrAdjEntry, vwapValue);
                            if (smaNearLow)  atrAdjEntry = Math.Min(atrAdjEntry, sma925Value);
                        
                            atrAdjEntry = Instrument.MasterInstrument.RoundToTickSize(atrAdjEntry);

                            EnterLongLimit(0, false, (int)TradeSize, atrAdjEntry, "ATR Long");
                            atrOrderDilowband = atrLowband;
                            atrOrderDihighband = atrHighband;
                            atrOrderDinextband = atrNextbandL;

                            SetStopLoss(CalculationMode.Price, atrLowband - 3);
                            SetProfitTarget("ATR Long", CalculationMode.Ticks,
                                Math.Min(4 * 20, 4 * (atrHighband - atrAdjEntry)) / 2);

                            BE_Set = Trail_Set = false;
                            currentTrailStopPrice = 0;
                            Print("[DEBUG] LONG ENTRY SIGNAL (ATR+VWAP+SMA): Price=" + atrAdjEntry + ", SL=" + (atrLowband + 4));
                        }
        }

        // ATR Band Setting Method
        private void SetATRBands(double price)
        {
            if (atrLevels == null || atrLevels.Count == 0) return;
            for (int i = 0; i < atrLevels.Count - 1; i++)
            {
                if (price >= atrLevels[i] && price < atrLevels[i + 1])
                {
                    atrLowband = atrLevels[i];
                    atrHighband = atrLevels[i + 1];
                    atrNextbandL = (i + 2 < atrLevels.Count)
                        ? atrLevels[i + 2] : atrLevels[i + 1];
                    atrNextbandS = (i - 1 >= 0)
                        ? atrLevels[i - 1] : atrLevels[i];
                    Print("[DEBUG] ATR Bands set - Price: " + price + ", Band: [" + atrLowband + " - " + atrHighband + "]");
                    break;
                }
            }
        }

        protected override void OnOrderUpdate(Order order,
            double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time,
            ErrorCode error, string nativeError)
        {
            if (atrEntryOrder != null && atrEntryOrder.IsBacktestOrder && State == State.Realtime)
                atrEntryOrder = GetRealtimeOrder(atrEntryOrder);

            if (atrEntryOrder == null &&
                (order.Name.StartsWith("ATR Long") || order.Name.StartsWith("ATR Short")))
            {
                atrEntryOrder = order;
                Print("[DEBUG] Order placed: " + order.Name + " at " + limitPrice);
            }

            if (atrEntryOrder != null && order.OrderState == OrderState.Cancelled)
            {
                atrEntryOrder = null;
                Print("[DEBUG] Order cancelled: " + order.Name);
                if(State == State.Realtime)
                EvaluateAtrEntryLogic(Instrument.MasterInstrument.RoundToTickSize(sma925[0]));
            }
        }

        protected override void OnExecutionUpdate(Execution execution,
            string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order.Name.StartsWith("ATR Long") ||
                execution.Order.Name.StartsWith("ATR Short"))
            {
                // 1) record entry time + bar
                orderTime = execution.Order.Time;
                anchorBar = CurrentBar;
                // 2) reset VWAP accumulator
                cumPV = 0; cumVol = 0;
                // 3) resets
                BE_Set = false; Trail_Set = false;
                currentTrailStopPrice = 0;
                // 4) re-set ATR bands
                SetATRBands(Close[0]);
                atrOrderDilowband = atrLowband;
                atrOrderDihighband = atrHighband;
                atrOrderDinextband = execution.Order.Name.StartsWith("ATR Long") ? atrNextbandL : atrNextbandS;

                Print($"[DEBUG] ORDER FILLED: {execution.Order.Name} at {price}, Position={Position.Quantity}");
            }

            if (execution.Order.OrderState != OrderState.PartFilled)
            {
                if (execution.Order.Name.StartsWith("ATR"))
                    atrEntryOrder = null;
            }

            // This checks if a position has just been closed
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                lastOrderCloseTime = time;
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

        [Range(0, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Near-band tolerance (price units)", Order = 4, GroupName = "Parameters")]
        public int NearBandTolerance { get; set; }
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
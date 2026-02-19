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
    public class GKPivotATRQtr : Strategy
    {
    // Core variables for pivot levels and trading
    private double prevClose, prevHigh, prevLow;
    private double pLevel, r1Level, s1Level, r2Level, s2Level, r3Level, s3Level;
    private double pr1midLevel, r1r2midLevel, r2r3midLevel, ps1midLevel, s1s2midLevel, s2s3midLevel;
    private double lowband, highband, nextbandL, nextbandS, adjEntry;
    private bool BE_Set = false;
    private bool lowbandLongFlag, highbandLongFlag, lowbandShortFlag, highbandShortFlag;
    private bool vwapLongFlag, vwapShortFlag;
    private double vwPrice;
    private double OrderDilowband, OrderDihighband, OrderDinextband;
    private double PrevDayPnL = 0, PrevDayTradeCount = 0;
    private double AccountRealizedPL, AccountUnrealizedPL;
    private Order entryOrder = null;
    private Order wtdentryOrder = null;
    private bool dayOverVar = false;
    private DateTime orderTime;
    private bool Trail_Set = false;
    private double currentTrailStopPrice = 0;

    // Pivot tracking variables
    private List<double> levels;
    private List<double> atrQuartileLevels; // ATR quartile levels
    private int prevBandIndex = -1;
    private double NowPivot = double.NaN;
    private double prevTouchedPivot = double.NaN;
    private double currRecordedPivot = double.NaN;
    private bool newPivotTouched = false;
    
    // NEW: Session tracking variables
    private double sessionOpenPrice = double.NaN;
    private bool atrQuartilesCalculated = false;
    
    // NEW: Variables to store individual ATR quartile levels for plotting
    private double atrQ1Above = double.NaN, atrQ2Above = double.NaN, atrQ3Above = double.NaN, atrQ4Above = double.NaN;
    private double atrQ1Below = double.NaN, atrQ2Below = double.NaN, atrQ3Below = double.NaN, atrQ4Below = double.NaN;
		
    bool LongTradeFlag;
    bool ShortTradeFlag;

    // VWAP indicators for chart display
    private VWAP1 ofVwapETH;
    //  private AVWAP2 VWAPx1, VWAPx2;
		
    double AnchorFromClose, avwap1, l1Entry, s1Entry, lATR;

    // Add this dictionary to track when each pivot was last touched
    private Dictionary<double, int> lastPivotTouchBarIndex = new Dictionary<double, int>();
		
	
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
            Description = @"Enhanced Pivot Bands Strategy with ATR Quartiles";
            Name = "GKPivotATRQtr";
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
            ManualSettPrice = 1;
            ManualLowPrice = 1;
            ManualHighPrice = 1;
            MaxLoss = -75;
            WildersATRValue = 10.0; // NEW: Direct ATR value input
            
            // ←–––– Updated AnchorFrom default logic ––––→
            if (DateTime.Today.DayOfWeek == DayOfWeek.Monday)
            {
                // Monday: anchor at 12:30 AM today
                AnchorFrom = DateTime.Parse("12:30 AM");
            }
            else
            {
                // Other days: anchor at the most recent Sunday at 5 PM
                DateTime sunday = DateTime.Today.AddDays(- (int)DateTime.Today.DayOfWeek);
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

            // Define plots for all pivot points with distinct colors
            AddPlot(Brushes.GhostWhite, "S3Level");    // Deep blue
            AddPlot(Brushes.GhostWhite, "S2S3MidLevel");    // Light blue
            AddPlot(Brushes.GhostWhite, "S2Level");    // Standard blue
            AddPlot(Brushes.GhostWhite, "S1S2MidLevel");    // Pale blue
            AddPlot(Brushes.GhostWhite, "S1Level");    // Medium blue
            AddPlot(Brushes.GhostWhite, "PS1MidLevel"); // Soft blue
            AddPlot(Brushes.Red, "PLevel");    // Red for pivot
            AddPlot(Brushes.GhostWhite, "PR1MidLevel");    // Bright orange
            AddPlot(Brushes.GhostWhite, "R1Level");    // Darker orange
            AddPlot(Brushes.GhostWhite, "R1R2MidLevel");    // Golden yellow
            AddPlot(Brushes.GhostWhite, "R2Level");    // Bright gold
            AddPlot(Brushes.GhostWhite, "R2R3MidLevel");    // Light yellow
            AddPlot(Brushes.GhostWhite, "R3Level");    // Vibrant green
            
            // NEW: Add plots for ATR quartiles (using light colors for black background)
            AddPlot(Brushes.LightCyan, "ATRQ1Above");    // +25% ATR
            AddPlot(Brushes.LightCyan, "ATRQ2Above");    // +50% ATR  
            AddPlot(Brushes.LightCyan, "ATRQ3Above");    // +75% ATR
            AddPlot(Brushes.LightCyan, "ATRQ4Above");    // +100% ATR
            AddPlot(Brushes.LightCyan, "ATRQ1Below");    // -25% ATR
            AddPlot(Brushes.LightCyan, "ATRQ2Below");    // -50% ATR
            AddPlot(Brushes.LightCyan, "ATRQ3Below");    // -75% ATR
            AddPlot(Brushes.LightCyan, "ATRQ4Below");    // -100% ATR
        }
        else if (State == State.Configure)
        {
            AddDataSeries(Data.BarsPeriodType.Minute, 1);
            AddDataSeries(Data.BarsPeriodType.Tick, 1);
        }
        else if (State == State.DataLoaded)
        {
            ofVwapETH = VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
            AddChartIndicator(ofVwapETH);
        }
    }

    // NEW: Method to calculate ATR quartiles based on session open price
    private List<double> CalculateATRQuartilesFromOpen(double openPrice, double atrValue)
    {
        List<double> quartiles = new List<double>();
        
        // Calculate quartile levels above session open price
        quartiles.Add(Instrument.MasterInstrument.RoundToTickSize(openPrice + atrValue / 4));     // +25% ATR
        quartiles.Add(Instrument.MasterInstrument.RoundToTickSize(openPrice + atrValue / 2));     // +50% ATR
        quartiles.Add(Instrument.MasterInstrument.RoundToTickSize(openPrice + 3 * atrValue / 4)); // +75% ATR
        quartiles.Add(Instrument.MasterInstrument.RoundToTickSize(openPrice + atrValue));         // +100% ATR
        
        // Calculate quartile levels below session open price
        quartiles.Add(Instrument.MasterInstrument.RoundToTickSize(openPrice - atrValue / 4));     // -25% ATR
        quartiles.Add(Instrument.MasterInstrument.RoundToTickSize(openPrice - atrValue / 2));     // -50% ATR
        quartiles.Add(Instrument.MasterInstrument.RoundToTickSize(openPrice - 3 * atrValue / 4)); // -75% ATR
        quartiles.Add(Instrument.MasterInstrument.RoundToTickSize(openPrice - atrValue));         // -100% ATR
        
        return quartiles;
    }

    // Method to filter ATR quartiles that are too close to pivot levels
    private List<double> FilterATRQuartiles(List<double> quartiles, List<double> pivotLevels)
    {
        List<double> filteredQuartiles = new List<double>();
        
        foreach (double quartile in quartiles)
        {
            bool tooCloseToAnyPivot = false;
            
            // Check if this quartile is within 10 points of any pivot level
            foreach (double pivotLevel in pivotLevels)
            {
                if (Math.Abs(quartile - pivotLevel) <= 10)
                {
                    tooCloseToAnyPivot = true;
                    Print("ATR Quartile " + quartile + " is within 10 points of pivot " + pivotLevel + " - IGNORED");
                    break;
                }
            }
            
            // If not too close to any pivot, add it to filtered list
            if (!tooCloseToAnyPivot)
            {
                filteredQuartiles.Add(quartile);
                Print("ATR Quartile " + quartile + " added as additional pivot level");
            }
        }
        
        return filteredQuartiles;
    }

    // NEW: Method to update ATR quartile plot values
    private void UpdateATRQuartilePlots(List<double> allQuartiles, List<double> filteredQuartiles)
    {
        // Reset all ATR quartile plot values
        atrQ1Above = atrQ2Above = atrQ3Above = atrQ4Above = double.NaN;
        atrQ1Below = atrQ2Below = atrQ3Below = atrQ4Below = double.NaN;
        
        if (allQuartiles != null && allQuartiles.Count >= 8)
        {
            // Store all quartiles for plotting (regardless of filtering)
            atrQ1Above = allQuartiles[0]; // +25% ATR
            atrQ2Above = allQuartiles[1]; // +50% ATR
            atrQ3Above = allQuartiles[2]; // +75% ATR
            atrQ4Above = allQuartiles[3]; // +100% ATR
            atrQ1Below = allQuartiles[4]; // -25% ATR
            atrQ2Below = allQuartiles[5]; // -50% ATR
            atrQ3Below = allQuartiles[6]; // -75% ATR
            atrQ4Below = allQuartiles[7]; // -100% ATR
        }
        
        // Update plot values
        Values[13][0] = atrQ1Above;  // +25% ATR
        Values[14][0] = atrQ2Above;  // +50% ATR
        Values[15][0] = atrQ3Above;  // +75% ATR
        Values[16][0] = atrQ4Above;  // +100% ATR
        Values[17][0] = atrQ1Below;  // -25% ATR
        Values[18][0] = atrQ2Below;  // -50% ATR
        Values[19][0] = atrQ3Below;  // -75% ATR
        Values[20][0] = atrQ4Below;  // -100% ATR
    }

    protected override void OnBarUpdate()
    {
        if (CurrentBars[0] < BarsRequiredToTrade) return;

        double toTime = ToTime(Time[0]) / 100.0;
        bool isitEarly = toTime >= 1500 && toTime < 2359;
        bool isitRTH = true; // Always true in this context
        
        TimeSpan startTime = new TimeSpan(8, 30, 0); // 9:30 AM
        TimeSpan endTime = new TimeSpan(3, 0, 0);  // 4:00 PM
        TimeSpan currentTime = Time[0].TimeOfDay;

        // Reset at specific times (15:10 or 21:00)
        if ((toTime == 1510 || toTime == 2100) && BarsInProgress == 0 && IsFirstTickOfBar)
        {
            PrevDayPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            PrevDayTradeCount = SystemPerformance.AllTrades.Count;
            dayOverVar = false;
            lowbandLongFlag = highbandLongFlag = lowbandShortFlag = highbandShortFlag = false;
            vwapLongFlag = vwapShortFlag = false;
            LongTradeFlag= false;
            ShortTradeFlag= false;
                
            prevLow = PriorDayOHLC().PriorLow[0];
            prevHigh = PriorDayOHLC().PriorHigh[0];
            Print("Reset at " + toTime + ": PrevLow = " + prevLow + ", PrevHigh = " + prevHigh);
            currRecordedPivot  = double.NaN;
            NowPivot = double.NaN;
            prevTouchedPivot = double.NaN;

            // Initialize prevTouchedPivot to the lowest possible pivot value
            prevTouchedPivot = double.MinValue;
            
            // NEW: Reset session tracking variables
            sessionOpenPrice = double.NaN;
            atrQuartilesCalculated = false;
        }
		
		

        // NEW: Capture session open price (first bar after reset)
        if (double.IsNaN(sessionOpenPrice) && BarsInProgress == 0 && IsFirstTickOfBar && toTime > 835)
        {
            sessionOpenPrice = CurrentDayOHL().CurrentOpen[0];
            Print("Session Open Price captured: " + sessionOpenPrice);
        }

        // PnL-based exit logic
        if (SystemPerformance.AllTrades.Count > 0 && !dayOverVar)
        {
            double cumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - PrevDayPnL;
            double totalPL = AccountRealizedPL + AccountUnrealizedPL;
            
            if (AccountRealizedPL < MaxLoss || AccountRealizedPL > 200 * TradeSize || AccountUnrealizedPL < -500)
            {
                dayOverVar = true;
                
                if (Position.MarketPosition == MarketPosition.Long) ExitLong();
                else if (Position.MarketPosition == MarketPosition.Short) ExitShort();
                Print("Exiting due to PnL conditions: CumProfit = " + cumProfit + ", AccountRealizedPL = " + AccountRealizedPL + ", UnrealizedPL = " + AccountUnrealizedPL);
            }
        }

        // Main trading logic
        if (BarsInProgress == 0 && IsFirstTickOfBar && isitRTH && !isitEarly && toTime > 835 && !dayOverVar)
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

            // Calculate pivot levels (same as before)
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

            // Store all pivot levels (original pivot points)
            List<double> originalPivotLevels = new List<double> { s3Level, s2s3midLevel, s2Level, s1s2midLevel, s1Level, ps1midLevel, pLevel, pr1midLevel, r1Level, r1r2midLevel, r2Level, r2r3midLevel, r3Level };

            // NEW: Calculate ATR quartiles once per session, based on session open price
            List<double> allATRQuartiles = null;
            if (!atrQuartilesCalculated && !double.IsNaN(sessionOpenPrice))
            {
                Print("Calculating ATR Quartiles based on Session Open: " + sessionOpenPrice + ", ATR Value: " + WildersATRValue);

                // Calculate ATR quartiles from session open price
                allATRQuartiles = CalculateATRQuartilesFromOpen(sessionOpenPrice, WildersATRValue);
                Print("Calculated " + allATRQuartiles.Count + " ATR quartile levels from session open");

                // Filter ATR quartiles that are within 10 points of any pivot level
                atrQuartileLevels = FilterATRQuartiles(allATRQuartiles, originalPivotLevels);
                Print("After filtering, " + atrQuartileLevels.Count + " ATR quartile levels remain");

                atrQuartilesCalculated = true;
            }

            // NEW: Update ATR quartile plots
            UpdateATRQuartilePlots(allATRQuartiles, atrQuartileLevels);

            // Combine original pivot levels with filtered ATR quartile levels
            levels = new List<double>(originalPivotLevels);
            if (atrQuartileLevels != null)
            {
                levels.AddRange(atrQuartileLevels);
            }
            levels.Sort(); // Sort all levels in ascending order
            
            Print("Total levels for trading: " + levels.Count + " (Original pivots: " + originalPivotLevels.Count + ", ATR quartiles: " + (atrQuartileLevels?.Count ?? 0) + ")");

            // Plot all pivot points (original ones only)
            Values[0][0] = s3Level;    // S3
            Values[1][0] = s2s3midLevel;    // S2-S3 Mid
            Values[2][0] = s2Level;    // S2
            Values[3][0] = s1s2midLevel;    // S1-S2 Mid
            Values[4][0] = s1Level;    // S1
            Values[5][0] = ps1midLevel;    // P-S1 Mid
            Values[6][0] = pLevel;    // Pivot
            Values[7][0] = pr1midLevel;    // P-R1 Mid
            Values[8][0] = r1Level;    // R1
            Values[9][0] = r1r2midLevel;    // R1-R2 Mid
            Values[10][0] = r2Level;    // R2
            Values[11][0] = r2r3midLevel;   // R2-R3 Mid
            Values[12][0] = r3Level;    // R3

            Print("====START==== "+Time[0]);
            NowPivot=double.NaN;
            
            // Check for pivot touches (now includes ATR quartile levels)
            for (int i = 0; i < levels.Count; i++)
                if (High[1] >= levels[i] && levels[i] >= Low[1]) 
                    NowPivot = levels[i];
            
            if (!double.IsNaN(NowPivot))
            {
                // Update the dictionary with current bar index
                lastPivotTouchBarIndex[NowPivot] = CurrentBar;
                
                // Print for debugging
                string levelType = (atrQuartileLevels != null && atrQuartileLevels.Contains(NowPivot)) ? "ATR Quartile" : "Original Pivot";
                Print(levelType + " level " + NowPivot + " touched at bar " + CurrentBar);
            }

            // Process pivot point touch
            if (!double.IsNaN(NowPivot))
            {
                // Store last pivot info
                prevTouchedPivot = currRecordedPivot;
                currRecordedPivot = NowPivot;
            }

            // Calculate VWAP
            vwPrice = Instrument.MasterInstrument.RoundToTickSize(VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[0]);
            lATR=Instrument.MasterInstrument.RoundToTickSize(1.5*ATR(Closes[1], 14)[0]);
            
            // Entry logic
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                SetBands(Close[0]);
                Print("Bands set: Lowband=" + lowband + ", Highband=" + highband + ", NextbandL=" + nextbandL + ", NextbandS=" + nextbandS);

                if (entryOrder != null)
                {
                    CancelOrder(entryOrder);
                    entryOrder = null;
                    Print("Cancelled previous entry order");
                }
                if (wtdentryOrder != null)
                {
                    CancelOrder(wtdentryOrder);
                    wtdentryOrder = null;
                    Print("Cancelled wtd previous entry order");
                }

                // Set entry flags
                lowbandLongFlag = entryOrder == null && (Close[0]-lowband)<(highband-Close[0]);
                highbandShortFlag = entryOrder == null && (Close[0]-lowband)>(highband-Close[0]);
                
                bool pivotRevisitedWithSufficientRetracement = false;
                double currentATR = ATR(Closes[1], 14)[0];
                
                // To find bars since last touch for a specific pivot:
                if (lastPivotTouchBarIndex.ContainsKey(currRecordedPivot))
                {
                    int barsSinceTouch = CurrentBar - lastPivotTouchBarIndex[currRecordedPivot];
                    Print("Bars since pivot " + currRecordedPivot + " was last touched: " + barsSinceTouch);
                    
                    double enthaDeviation = GetMaxDeviationSinceLastTouch(currRecordedPivot, barsSinceTouch);
                    Print("Maximum deviation from pivot " + currRecordedPivot + " in the last " + 
                        barsSinceTouch + " bars: " + enthaDeviation +" ATR is "+currentATR);
                    pivotRevisitedWithSufficientRetracement = enthaDeviation>3*currentATR;
                    Print("pivotRevisitedWithSufficientRetracement: "+pivotRevisitedWithSufficientRetracement);
                }

                // UPDATED ENTRY LOGIC with correct pivot direction check
                if (entryOrder == null 
                    && highbandShortFlag   && (prevTouchedPivot == double.MinValue || 
                        currRecordedPivot != prevTouchedPivot ||
                        pivotRevisitedWithSufficientRetracement)
                    && ATR(Closes[1], 14)[0] < 10)
                {
                    Print("currRecordedPivot (" + currRecordedPivot + ") < prevTouchedPivot (" + prevTouchedPivot + ")");
                    
                    adjEntry = highband;
                    EnterShortLimit(0, false, Convert.ToInt32(TradeSize), adjEntry, "Short VWAP");
                    OrderDilowband = lowband;
                    OrderDihighband = highband;
                    OrderDinextband = nextbandS;
                    SetStopLoss(CalculationMode.Price, highband + 4);
                    SetProfitTarget("Short VWAP", CalculationMode.Ticks, Math.Min(4*20, 4*(highband-lowband)));
                    BE_Set = false;
                    Trail_Set = false;
                    currentTrailStopPrice = 0;
                    Print("Entering Short VWAP: StopLoss at " + (highband + 4) + ", ProfitTarget at " + (adjEntry-20));
                }
                else if (entryOrder == null 
                    && (prevTouchedPivot == double.MinValue 
                        || currRecordedPivot != prevTouchedPivot 
                        || pivotRevisitedWithSufficientRetracement)
                    && (lowbandLongFlag ) && ATR(Closes[1], 14)[0] < 10)
                {
                    Print("currRecordedPivot (" + currRecordedPivot + ") > prevTouchedPivot (" + prevTouchedPivot + ")");
                    
                    adjEntry = lowband;
                    EnterLongLimit(0, false, Convert.ToInt32(TradeSize), adjEntry, "Long VWAP");
                    OrderDilowband = lowband;
                    OrderDihighband = highband;
                    OrderDinextband = nextbandL;
                    SetStopLoss(CalculationMode.Price, lowband - 4);
                    SetProfitTarget("Long VWAP", CalculationMode.Ticks,  Math.Min(4*20, 4*(highband-lowband)));
                    BE_Set = false;
                    Trail_Set = false;
                    currentTrailStopPrice = 0;
                    
                    Print("Entering Long VWAP: StopLoss at " + (lowband - 4) + ", ProfitTarget at " + (adjEntry+20));
                }
            }

            // Manage open positions (same as before)
            if (Position.MarketPosition == MarketPosition.Long)
            {
                if (Close[0] >= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice + ATR(Closes[1], 14)[0]) && !BE_Set)
                {
                    SetStopLoss(CalculationMode.Price, Position.AveragePrice);
                    BE_Set = true;
                    Print("Moved StopLoss to breakeven for Long position");
                }
                
                if (Close[1] < Close[2] -0.5 && Close[2] < Close[3]-0.5  && ((Time[0] - orderTime).TotalMinutes-BarsPeriod.Value) > 4)
                {
                    ExitLong();
                    Print("Exiting Long position due to price action: "+((Time[0] - orderTime).TotalMinutes-BarsPeriod.Value));
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
                        Print("Trailing stop loss activated at " + currentTrailStopPrice);
                    }
                    else
                    {
                        double newTrail = Instrument.MasterInstrument.RoundToTickSize(Close[0] - 10);
                        if (newTrail > currentTrailStopPrice)
                        {
                            currentTrailStopPrice = newTrail;
                            SetStopLoss(CalculationMode.Price, currentTrailStopPrice);
                            Print("Trailing stop updated to " + currentTrailStopPrice);
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
                    Print("Moved StopLoss to breakeven for Short position");
                }
                
                if (Close[1] -0.5 > Close[2] && Close[2]-0.5 > Close[3] && ((Time[0] - orderTime).TotalMinutes-BarsPeriod.Value) > 4)
                {
                    ExitShort();
                    Print("Exiting Short position due to price action: "+((Time[0] - orderTime).TotalMinutes-BarsPeriod.Value));
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
                        Print("Trailing stop loss activated at " + currentTrailStopPrice);
                    }
                    else
                    {
                        double newTrail = Instrument.MasterInstrument.RoundToTickSize(Close[0] + 10);
                        if (newTrail < currentTrailStopPrice)
                        {
                            currentTrailStopPrice = newTrail;
                            SetStopLoss(CalculationMode.Price, currentTrailStopPrice);
                            Print("Trailing stop updated to " + currentTrailStopPrice);
                        }
                    }
                }
            }
        }
    }

    // Function to get maximum deviation from a pivot over the last n bars
    private double GetMaxDeviationSinceLastTouch(double pivotLevel, int barsSinceTouch)
    {
        // Limit the lookback to available bars and the bars since touch
        int lookback = Math.Min(barsSinceTouch, CurrentBar);
        
        // Initialize max deviation
        double maxDeviation = 0;
        
        // Loop through the bars
        for (int i = 0; i < lookback; i++)
        {
            // Calculate upward deviation (high above pivot)
            double upDeviation = High[i] - pivotLevel;
            
            // Calculate downward deviation (pivot above low)
            double downDeviation = pivotLevel - Low[i];
            
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

    // ENHANCED SetBands method to handle ATR quartile levels
    private void SetBands(double price)
    {
        // Find the band that contains the current price
        for (int i = 0; i < levels.Count - 1; i++)
        {
            if (price >= levels[i] && price < levels[i + 1])
            {
                lowband = levels[i];
                highband = levels[i + 1];
                
                // Set next bands
                if (i > 0)
                    nextbandS = levels[i - 1];
                else
                    nextbandS = levels[i]; // If at the lowest level
                    
                if (i < levels.Count - 2)
                    nextbandL = levels[i + 2];
                else
                    nextbandL = levels[i + 1]; // If at the highest level
                    
                return;
            }
        }
        
        // If price is outside all levels, use the extreme levels
        if (price < levels[0])
        {
            lowband = levels[0];
            highband = levels[1];
            nextbandS = levels[0];
            nextbandL = levels[2];
        }
        else if (price >= levels[levels.Count - 1])
        {
            lowband = levels[levels.Count - 2];
            highband = levels[levels.Count - 1];
            nextbandS = levels[levels.Count - 3];
            nextbandL = levels[levels.Count - 1];
        }
    }

    private int GetBandIndex(double price)
    {
        if (price < levels[0] || price >= levels[levels.Count - 1]) return -1;
        for (int i = 0; i < levels.Count - 1; i++)
            if (price >= levels[i] && price < levels[i + 1]) return i;
        return -1;
    }

    protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
    {
        if (entryOrder != null && entryOrder.IsBacktestOrder && State == State.Realtime)
            entryOrder = GetRealtimeOrder(entryOrder);
        
        if (wtdentryOrder != null && wtdentryOrder.IsBacktestOrder && State == State.Realtime)
            wtdentryOrder = GetRealtimeOrder(wtdentryOrder);

        if (entryOrder == null && (order.Name.StartsWith("Long") || order.Name.StartsWith("Short")))
        {
            entryOrder = order;
        }
        
        if (wtdentryOrder == null && (order.Name.StartsWith("WTD L") || order.Name.StartsWith("WTD S")))
        {
            wtdentryOrder = order;
        }

        if (entryOrder != null && order.OrderState == OrderState.Cancelled)
            entryOrder = null;
        
        if (wtdentryOrder != null && order.OrderState == OrderState.Cancelled)
            wtdentryOrder = null;
    }

    protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
    {
        if (execution.Order.Name.StartsWith("Long") || execution.Order.Name.StartsWith("Short"))
        {
            orderTime = execution.Order.Time;
            BE_Set = false;
            Trail_Set = false;
            currentTrailStopPrice = 0;
            SetBands(Close[0]);
            OrderDilowband = lowband;
            OrderDihighband = highband;
            OrderDinextband = execution.Order.Name.StartsWith("Long") ? nextbandL : nextbandS;
            Print("ORDER EXECUTED:: " + execution.Order.Name + ", Bands: Lowband=" + OrderDilowband + ", Highband=" + OrderDihighband + ", Nextband=" + OrderDinextband);
        }
        else
        {
            LongTradeFlag= false;
            ShortTradeFlag= false;
        }
        
        if (execution.Order.OrderState != OrderState.PartFilled)
        { 
            entryOrder = null;
            wtdentryOrder = null;
        }
    }

    #region Properties
    [Range(1, double.MaxValue), NinjaScriptProperty]
    [Display(ResourceType = typeof(Custom.Resource), Name = "Trade size", GroupName = "Parameters", Order = 0)]
    public double TradeSize { get; set; }

    [Range(1, double.MaxValue), NinjaScriptProperty]
    [Display(ResourceType = typeof(Custom.Resource), Name = "Previous day Low", GroupName = "Parameters", Order = 2)]
    public double ManualLowPrice { get; set; }

    [Range(1, double.MaxValue), NinjaScriptProperty]
    [Display(ResourceType = typeof(Custom.Resource), Name = "Previous day High", GroupName = "Parameters", Order = 1)]
    public double ManualHighPrice { get; set; }
    
    [Range(1, double.MaxValue), NinjaScriptProperty]
    [Display(ResourceType = typeof(Custom.Resource), Name = "Previous day Settlement", GroupName = "Parameters", Order = 3)]
    public double ManualSettPrice { get; set; }

    [NinjaScriptProperty]
    [PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
    [Display(Name = "Anchor VWAP from 1st", Order = 4, GroupName = "Parameters")]
    public DateTime AnchorFrom { get; set; }

    [PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
    [Display(Name = "Anchor VWAP from 2nd", Order = 5, GroupName = "Parameters")]
    public DateTime AnchorFrom2 { get; set; }

    [Range(double.MinValue, double.MaxValue), NinjaScriptProperty]
    [Display(ResourceType = typeof(Custom.Resource), Name = "Max Loss", GroupName = "Parameters", Order = 6)]
    public double MaxLoss { get; set; }

    // NEW: Wilder's ATR Value parameter (direct input, not period)
    [Range(0.1, double.MaxValue), NinjaScriptProperty]
    [Display(Name = "Wilders ATR Value", Description = "Direct ATR value input for quartile calculations", GroupName = "Parameters", Order = 7)]
    public double WildersATRValue { get; set; }

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
    
    // NEW: ATR Quartile plot properties
    [XmlIgnore] public Series<double> ATRQ1Above => Values[13];
    [XmlIgnore] public Series<double> ATRQ2Above => Values[14];
    [XmlIgnore] public Series<double> ATRQ3Above => Values[15];
    [XmlIgnore] public Series<double> ATRQ4Above => Values[16];
    [XmlIgnore] public Series<double> ATRQ1Below => Values[17];
    [XmlIgnore] public Series<double> ATRQ2Below => Values[18];
    [XmlIgnore] public Series<double> ATRQ3Below => Values[19];
    [XmlIgnore] public Series<double> ATRQ4Below => Values[20];
    #endregion

    protected override void OnAccountItemUpdate(Account account, AccountItem accountItem, double value)
    {
        AccountRealizedPL = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
        AccountUnrealizedPL = account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
    }
    }
}
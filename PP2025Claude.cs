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
    public class PP2025Claude : Strategy
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
    private bool dayOverVar = false;
    private DateTime orderTime;

    // Pivot tracking variables
    private List<double> levels;
    private double lastTouchedPivot = double.NaN;
    private double prevTouchedPivot = double.NaN;
    private bool newPivotTouched = false;
    
    // Variables to track if a pivot was touched within the current bar
    private bool pivotTouchedThisBar = false;
    private double touchedPivotValue = double.NaN;
    private bool pivotDirectionUp = false;
    private int barsSincePivotTouch = 0;

    // VWAP indicators for chart display
    private VWAP1 ofVwapETH;
    private AVWAP2 VWAPx1, VWAPx2;

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
            Description = @"Pivot Bands Strategy with Direction Control";
            Name = "PP2025Claude";
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
            MaxLoss = -500;
            AnchorFrom = DateTime.Parse("12:30 AM");
            AnchorFrom2 = DateTime.Parse("12:30 AM");

            // Define plots for all pivot points with distinct colors
            AddPlot(Brushes.Blue, "S3Level");    // Deep blue
            AddPlot(Brushes.Blue, "S2S3MidLevel");    // Light blue
            AddPlot(Brushes.Blue, "S2Level");    // Standard blue
            AddPlot(Brushes.Blue, "S1S2MidLevel");    // Pale blue
            AddPlot(Brushes.Blue, "S1Level");    // Medium blue
            AddPlot(Brushes.Blue, "PS1MidLevel"); // Soft blue
            AddPlot(Brushes.Red, "PLevel");    // Red for pivot
            AddPlot(Brushes.Blue, "PR1MidLevel");    // Bright orange
            AddPlot(Brushes.Blue, "R1Level");    // Darker orange
            AddPlot(Brushes.Blue, "R1R2MidLevel");    // Golden yellow
            AddPlot(Brushes.Blue, "R2Level");    // Bright gold
            AddPlot(Brushes.Blue, "R2R3MidLevel");    // Light yellow
            AddPlot(Brushes.Blue, "R3Level");    // Vibrant green
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
            VWAPx1 = AVWAP2(BarsArray[0], AnchorFrom, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
            AddChartIndicator(VWAPx1);
            VWAPx2 = AVWAP2(BarsArray[0], AnchorFrom2, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
            AddChartIndicator(VWAPx2);
            
            Print("Strategy initialized: PP2025Claude with improved pivot detection");
        }
    }

    protected override void OnBarUpdate()
    {
        if (CurrentBars[0] < BarsRequiredToTrade) return;

        double toTime = ToTime(Time[0]) / 100.0;
        bool isitEarly = toTime >= 1500 && toTime < 2100;
        bool isitRTH = true; // Always true in this context

        // Reset at specific times (15:10 or 21:00)
        if ((toTime == 1510 || toTime == 2100) && BarsInProgress == 0 && IsFirstTickOfBar)
        {
            PrevDayPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            PrevDayTradeCount = SystemPerformance.AllTrades.Count;
            dayOverVar = false;
            lowbandLongFlag = highbandLongFlag = lowbandShortFlag = highbandShortFlag = false;
            vwapLongFlag = vwapShortFlag = false;
            prevLow = PriorDayOHLC().PriorLow[0];
            prevHigh = PriorDayOHLC().PriorHigh[0];
            Print("RESET: Daily reset at " + toTime + ": PrevLow = " + prevLow + ", PrevHigh = " + prevHigh);
            
            // Reset pivot tracking
            lastTouchedPivot = double.NaN;
            prevTouchedPivot = double.NaN;
            newPivotTouched = false;
            pivotTouchedThisBar = false;
            touchedPivotValue = double.NaN;
            barsSincePivotTouch = 0;
            
            Print("RESET: Pivot tracking variables reset");
        }

        // Enter long trade at 15:05 if no trades today
        if (SystemPerformance.AllTrades.Count - PrevDayTradeCount == 0 && toTime == 1505 && BarsInProgress == 0 && IsFirstTickOfBar)
        {
            SetStopLoss(CalculationMode.Ticks, 1);
            SetProfitTarget("Long VWAP 1st", CalculationMode.Ticks, 2);
            EnterLong(2, 1, "Long VWAP 1st");
            Print("ENTRY: Entering Long VWAP 1st at 15:05 (first trade of day)");
        }

        // PnL-based exit logic
        if (SystemPerformance.AllTrades.Count > 0 && !dayOverVar)
        {
            double cumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - PrevDayPnL;
            double totalPL = AccountRealizedPL + AccountUnrealizedPL;
            if (cumProfit < MaxLoss || totalPL < MaxLoss || totalPL > 100 * TradeSize || AccountUnrealizedPL < -500)
            {
                dayOverVar = true;
                if (Position.MarketPosition == MarketPosition.Long) 
                {
                    ExitLong();
                    Print("EXIT: Exiting Long position due to PnL conditions: CumProfit = " + cumProfit + ", TotalPL = " + totalPL);
                }
                else if (Position.MarketPosition == MarketPosition.Short) 
                {
                    ExitShort();
                    Print("EXIT: Exiting Short position due to PnL conditions: CumProfit = " + cumProfit + ", TotalPL = " + totalPL);
                }
            }
        }

        // Main trading logic
        if (BarsInProgress == 0)
        {
            // Reset pivot touch detection at the start of a new bar
            if (IsFirstTickOfBar)
            {
                if (pivotTouchedThisBar)
                {
                    Print("BAR: Previous bar touched pivot level: " + touchedPivotValue + 
                          ", Direction: " + (pivotDirectionUp ? "Up" : "Down"));
                    barsSincePivotTouch = 0;
                }
                else
                {
                    barsSincePivotTouch++;
                    if (barsSincePivotTouch <= 3) // Only print for a few bars
                        Print("BAR: No pivot touch in previous bar. Bars since last touch: " + barsSincePivotTouch);
                }
                
                pivotTouchedThisBar = false;
                touchedPivotValue = double.NaN;
                
                // Calculate pivot levels at the start of the day or when needed
                if (isitRTH && !isitEarly && toTime > 835 && !dayOverVar)
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

                    // Calculate pivot levels
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

                    // Store all pivot levels
                    levels = new List<double> { s3Level, s2s3midLevel, s2Level, s1s2midLevel, s1Level, ps1midLevel, pLevel, pr1midLevel, r1Level, r1r2midLevel, r2Level, r2r3midLevel, r3Level };

                    // Plot all pivot points
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

                    Print("PIVOTS: Calculated pivot levels - P: " + pLevel + 
                          ", R1: " + r1Level + ", S1: " + s1Level + 
                          ", R2: " + r2Level + ", S2: " + s2Level);
                    Print("PRICE: Current price " + Close[0] + " OHLC: " + Open[0] + "/" + High[0] + "/" + Low[0] + "/" + Close[0]);
                }
            }
            
            // Check for pivot point touches on EVERY tick (not just first tick of bar)
            if (isitRTH && !isitEarly && toTime > 835 && !dayOverVar && levels != null && levels.Count > 0)
            {
                // Check if current price (or High/Low of current bar) touches any pivot level
                CheckPivotTouch();
                
                // Calculate VWAP
                vwPrice = Instrument.MasterInstrument.RoundToTickSize(VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[0]);

                // Entry logic - only execute on first tick of bar
                if (IsFirstTickOfBar && (Position.MarketPosition == MarketPosition.Flat || Position.Quantity == Convert.ToInt32(TradeSize)))
                {
                    SetBands(Close[0]);
                    Print("BANDS: Current price: " + Close[0] + ", Bands set: Lowband=" + lowband + ", Highband=" + highband + ", NextbandL=" + nextbandL + ", NextbandS=" + nextbandS);

                    if (entryOrder != null)
                    {
                        CancelOrder(entryOrder);
                        entryOrder = null;
                        Print("ORDER: Cancelled previous entry order");
                    }

                    // Set entry flags with detailed logging
                    lowbandLongFlag = entryOrder == null && Low[2] < lowband && High[2] > lowband && Low[1] > lowband;
                    lowbandShortFlag = entryOrder == null && Low[2] < lowband && High[2] > lowband && High[1] < lowband;
                    highbandLongFlag = entryOrder == null && Low[2] < highband && High[2] > highband && Low[1] > highband;
                    highbandShortFlag = entryOrder == null && Low[2] < highband && High[2] > highband && High[1] < highband;
                    vwapLongFlag = entryOrder == null && Low[2] < vwPrice && High[2] > vwPrice && Low[1] > vwPrice;
                    vwapShortFlag = entryOrder == null && Low[2] < vwPrice && High[2] > vwPrice && High[1] < vwPrice;
                    
                    Print("FLAGS: Entry flags - LowbandLong: " + lowbandLongFlag + 
                          ", LowbandShort: " + lowbandShortFlag + 
                          ", HighbandLong: " + highbandLongFlag + 
                          ", HighbandShort: " + highbandShortFlag + 
                          ", VwapLong: " + vwapLongFlag + 
                          ", VwapShort: " + vwapShortFlag);
                    
                    Print("PIVOTS: Last touched: " + lastTouchedPivot + 
                          ", Previous touched: " + prevTouchedPivot + 
                          ", New pivot touched: " + newPivotTouched);

                    // UPDATED ENTRY LOGIC with improved pivot direction check
                    if (entryOrder == null && newPivotTouched && 
                        (lowbandShortFlag || highbandShortFlag || vwapShortFlag) && 
                        lastTouchedPivot < prevTouchedPivot && ATR(Closes[1], 14)[0] < 90)
                    {
                        Print("ENTRY SIGNAL: SHORT ENTRY TRIGGERED");
                        Print("PIVOT DIRECTION: lastTouchedPivot (" + lastTouchedPivot + ") < prevTouchedPivot (" + prevTouchedPivot + ")");
                        Print("PRICE ACTION: Previous bars - Bar[-1]: " + Open[1] + "/" + High[1] + "/" + Low[1] + "/" + Close[1] + 
                              ", Bar[-2]: " + Open[2] + "/" + High[2] + "/" + Low[2] + "/" + Close[2]);
                        
                        adjEntry = Open[0];
                        if ((highband + 0.25 - Open[0]) > 5)
                            adjEntry = highband - 5;
                        
                        Print("ORDER DETAILS: Entering Short at " + adjEntry + ", Current price: " + Close[0]);
                        
                        EnterShortLimit(0, false, Convert.ToInt32(TradeSize), adjEntry, "Short VWAP");
                        OrderDilowband = lowband;
                        OrderDihighband = highband;
                        OrderDinextband = nextbandS;
                        SetStopLoss(CalculationMode.Price, highband + 0.25);
                        SetProfitTarget("Short VWAP", CalculationMode.Ticks, Math.Min(8 * (highband + 0.25 - Close[0]), 40));
                        BE_Set = false;
                        Print("RISK MANAGEMENT: StopLoss at " + (highband + 0.25) + ", ProfitTarget at " + Math.Min(8 * (highband + 0.25 - Close[0]), 40) + " ticks");
                    }
                    else if (entryOrder == null && newPivotTouched && 
                        (lowbandLongFlag || highbandLongFlag || vwapLongFlag) && 
                        lastTouchedPivot > prevTouchedPivot && ATR(Closes[1], 14)[0] < 90)
                    {
                        Print("ENTRY SIGNAL: LONG ENTRY TRIGGERED");
                        Print("PIVOT DIRECTION: lastTouchedPivot (" + lastTouchedPivot + ") > prevTouchedPivot (" + prevTouchedPivot + ")");
                        Print("PRICE ACTION: Previous bars - Bar[-1]: " + Open[1] + "/" + High[1] + "/" + Low[1] + "/" + Close[1] + 
                              ", Bar[-2]: " + Open[2] + "/" + High[2] + "/" + Low[2] + "/" + Close[2]);
                        
                        adjEntry = Open[0];
                        if ((Open[0] - (lowband - 0.25)) > 5)
                            adjEntry = lowband + 5;
                            
                        Print("ORDER DETAILS: Entering Long at " + adjEntry + ", Current price: " + Close[0]);
                        
                        EnterLongLimit(0, false, Convert.ToInt32(TradeSize), adjEntry, "Long VWAP");
                        OrderDilowband = lowband;
                        OrderDihighband = highband;
                        OrderDinextband = nextbandL;
                        SetStopLoss(CalculationMode.Price, lowband - 1);
                        SetProfitTarget("Long VWAP", CalculationMode.Ticks, Math.Min(8 * (Close[0] - (lowband - 0.25)), 40));
                        BE_Set = false;
                        Print("RISK MANAGEMENT: StopLoss at " + (lowband - 1) + ", ProfitTarget at " + Math.Min(8 * (Close[0] - (lowband - 0.25)), 40) + " ticks");
                    }
                    else if (newPivotTouched)
                    {
                        Print("ENTRY EVALUATION: New pivot touched but entry conditions not met");
                        Print("ATR: " + ATR(Closes[1], 14)[0] + (ATR(Closes[1], 14)[0] >= 9 ? " (too high)" : " (acceptable)"));
                    }
                }

                // Manage open positions
                if (Position.MarketPosition == MarketPosition.Long)
                {
                    if (Close[0] >= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice + 5) && !BE_Set)
                    {
                        SetStopLoss(CalculationMode.Price, Position.AveragePrice);
                        BE_Set = true;
                        Print("POSITION MANAGEMENT: Moved StopLoss to breakeven for Long position at " + Position.AveragePrice);
                    }
                    if (Close[1] < Close[2] - 0.5 && Close[2] < Close[3] - 0.5 && (Time[0] - orderTime).TotalMinutes > 3)
                    {
                        ExitLong();
                        Print("EXIT: Exiting Long position due to price action - 2 consecutive down bars");
                    }
                }
                else if (Position.MarketPosition == MarketPosition.Short)
                {
                    if (Close[0] <= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice - 5) && !BE_Set)
                    {
                        SetStopLoss(CalculationMode.Price, Position.AveragePrice);
                        BE_Set = true;
                        Print("POSITION MANAGEMENT: Moved StopLoss to breakeven for Short position at " + Position.AveragePrice);
                    }
                    if (Close[1] - 0.5 > Close[2] && Close[2] - 0.5 > Close[3] && (Time[0] - orderTime).TotalMinutes > 3)
                    {
                        ExitShort();
                        Print("EXIT: Exiting Short position due to price action - 2 consecutive up bars");
                    }
                }
            }
        }
    }

    // New method to check if price touches any pivot level
    private void CheckPivotTouch()
    {
        // Check if current price or High/Low of the current bar touches any pivot level
        foreach (double pivotLevel in levels)
        {
            // Check if the current bar's range (High to Low) crosses the pivot level
            bool touchedThisTick = (High[0] >= pivotLevel && Low[0] <= pivotLevel) || 
                                   Math.Abs(Close[0] - pivotLevel) < 0.01;  // Close enough to pivot
                                   
            if (touchedThisTick)
            {
                // We've touched a pivot level
                if (!pivotTouchedThisBar || Math.Abs(touchedPivotValue - pivotLevel) > 0.01)
                {
                    // This is a new pivot touch in this bar
                    pivotTouchedThisBar = true;
                    touchedPivotValue = pivotLevel;
                    
                    // Identify which pivot level was touched
                    string pivotName = "Unknown";
                    if (Math.Abs(pivotLevel - pLevel) < 0.01) pivotName = "Pivot";
                    else if (Math.Abs(pivotLevel - r1Level) < 0.01) pivotName = "R1";
                    else if (Math.Abs(pivotLevel - r2Level) < 0.01) pivotName = "R2";
                    else if (Math.Abs(pivotLevel - r3Level) < 0.01) pivotName = "R3";
                    else if (Math.Abs(pivotLevel - s1Level) < 0.01) pivotName = "S1";
                    else if (Math.Abs(pivotLevel - s2Level) < 0.01) pivotName = "S2";
                    else if (Math.Abs(pivotLevel - s3Level) < 0.01) pivotName = "S3";
                    else if (Math.Abs(pivotLevel - pr1midLevel) < 0.01) pivotName = "P-R1 Mid";
                    else if (Math.Abs(pivotLevel - r1r2midLevel) < 0.01) pivotName = "R1-R2 Mid";
                    else if (Math.Abs(pivotLevel - r2r3midLevel) < 0.01) pivotName = "R2-R3 Mid";
                    else if (Math.Abs(pivotLevel - ps1midLevel) < 0.01) pivotName = "P-S1 Mid";
                    else if (Math.Abs(pivotLevel - s1s2midLevel) < 0.01) pivotName = "S1-S2 Mid";
                    else if (Math.Abs(pivotLevel - s2s3midLevel) < 0.01) pivotName = "S2-S3 Mid";
                    
                    Print("PIVOT TOUCH: Bar is touching " + pivotName + " level at " + pivotLevel + 
                          ", Current price: " + Close[0] + ", Bar range: " + Low[0] + " to " + High[0]);
                    
                    // Check if this is a different pivot than the last one we touched
                    if (double.IsNaN(lastTouchedPivot) || Math.Abs(pivotLevel - lastTouchedPivot) > 0.01)
                    {
                        // We've touched a new, different pivot
                        prevTouchedPivot = lastTouchedPivot;
                        lastTouchedPivot = pivotLevel;
                        
                        Print("PIVOT SWITCH: Changed from " + (double.IsNaN(prevTouchedPivot) ? "None" : prevTouchedPivot.ToString()) + 
                              " to " + lastTouchedPivot);
                        
                        // Only consider it a new pivot if it's different from the previous one
                        if (!double.IsNaN(prevTouchedPivot) && Math.Abs(lastTouchedPivot - prevTouchedPivot) > 0.01)
                        {
                            newPivotTouched = true;
                            pivotDirectionUp = lastTouchedPivot > prevTouchedPivot;
                            Print("PIVOT DIRECTION: New pivot touched - Direction is " + (pivotDirectionUp ? "UP" : "DOWN") + 
                                  ", Previous=" + prevTouchedPivot + ", Current=" + lastTouchedPivot);
                            barsSincePivotTouch = 0;
                        }
                    }
                    
                    // Break after finding the first pivot touch
                    break;
                }
            }
        }
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

    protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
    {
        if (entryOrder != null && entryOrder.IsBacktestOrder && State == State.Realtime)
            entryOrder = GetRealtimeOrder(entryOrder);

        if (entryOrder == null && (order.Name.StartsWith("Long") || order.Name.StartsWith("Short")))
        {
            entryOrder = order;
            Print("ORDER UPDATE: New entry order created - " + order.Name + ", State: " + order.OrderState);
        }

        if (entryOrder != null && order.OrderState == OrderState.Cancelled)
        {
            entryOrder = null;
            Print("ORDER UPDATE: Entry order cancelled");
        }
        
        if (error != ErrorCode.NoError)
        {
            Print("ORDER ERROR: " + error + " - " + nativeError);
        }
    }

    protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
    {
        if (execution.Order.Name.StartsWith("Long") || execution.Order.Name.StartsWith("Short"))
        {
            orderTime = execution.Order.Time;
            BE_Set = false;
            SetBands(Close[0]);
            OrderDilowband = lowband;
            OrderDihighband = highband;
            OrderDinextband = execution.Order.Name.StartsWith("Long") ? nextbandL : nextbandS;
            Print("EXECUTION: Order executed - " + execution.Order.Name + " at price " + price + 
                  ", Quantity: " + quantity + ", Time: " + time);
            Print("BANDS AT EXECUTION: Lowband=" + OrderDilowband + 
                  ", Highband=" + OrderDihighband + 
                  ", Nextband=" + OrderDinextband);
        }
        if (execution.Order.OrderState != OrderState.PartFilled)
            entryOrder = null;
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
    #endregion

    protected override void OnAccountItemUpdate(Account account, AccountItem accountItem, double value)
    {
        AccountRealizedPL = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
        AccountUnrealizedPL = account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
    }
    }
}
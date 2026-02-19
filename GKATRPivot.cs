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
    public class GKATRPivot : Strategy
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
    private int prevBandIndex = -1;
    private double NowPivot = double.NaN;
    private double prevTouchedPivot = double.NaN;
    private double currRecordedPivot = double.NaN;
    private bool newPivotTouched = false;

		  bool LongTradeFlag;
	   bool ShortTradeFlag;

    // VWAP indicators for chart display
    private VWAP1 ofVwapETH;
  //  private AVWAP2 VWAPx1, VWAPx2;

		double AnchorFromClose, avwap1, l1Entry, s1Entry, lATR;

// Add this dictionary to track when each pivot was last touched
private Dictionary<double, int> lastPivotTouchBarIndex = new Dictionary<double, int>();

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
    private bool atrBE_Set = false;
    private bool atrTrail_Set = false;
    private double atrCurrentTrailStopPrice = 0;

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
    Description = @"Pivot Bands Strategy with Direction Control and ATR Quartiles";
    Name = "GKATRPivot";
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
    MyATR = 10.0;
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

    // ATR Quartile plots
    AddPlot(Brushes.Yellow, "ATRNegQ4Q5Mid");     // Below -Q4
    AddPlot(Brushes.Orange, "ATRNegQ4Level");     // -Q4
    AddPlot(Brushes.Orange, "ATRNegQ3Q4Mid");     // -Q3Q4 Mid
    AddPlot(Brushes.Orange, "ATRNegQ3Level");     // -Q3
    AddPlot(Brushes.Orange, "ATRNegQ2Q3Mid");     // -Q2Q3 Mid
    AddPlot(Brushes.Orange, "ATRNegQ2Level");     // -Q2
    AddPlot(Brushes.Orange, "ATRNegQ1Q2Mid");     // -Q1Q2 Mid
    AddPlot(Brushes.Orange, "ATRNegQ1Level");     // -Q1
    AddPlot(Brushes.Yellow, "ATRNegQ0Q1Mid");     // Below -Q1
    AddPlot(Brushes.Cyan, "ATRSessionOpen");      // Session Open
    AddPlot(Brushes.Yellow, "ATRQ0Q1Mid");        // Above Q1
    AddPlot(Brushes.Orange, "ATRQ1Level");        // Q1
    AddPlot(Brushes.Orange, "ATRQ1Q2Mid");        // Q1-Q2 Mid
    AddPlot(Brushes.Orange, "ATRQ2Level");        // Q2
    AddPlot(Brushes.Orange, "ATRQ2Q3Mid");        // Q2-Q3 Mid
    AddPlot(Brushes.Orange, "ATRQ3Level");        // Q3
    AddPlot(Brushes.Orange, "ATRQ3Q4Mid");        // Q3-Q4 Mid
    AddPlot(Brushes.Orange, "ATRQ4Level");        // Q4
    AddPlot(Brushes.Yellow, "ATRQ4Q5Mid");        // Above Q4

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
   // VWAPx1 = AVWAP2(BarsArray[0], AnchorFrom, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
    //VWAPx1 = AnchoredVWAP(AnchorFrom, true);
	//	AddChartIndicator(VWAPx1);
    //VWAPx2 = AVWAP2(BarsArray[0], AnchorFrom2, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
   // AddChartIndicator(VWAPx2);
    }
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

    // Check if current time is within regular trading hours
   // bool isRegularTradingHours = currentTime >= startTime && currentTime <= endTime;

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

    // Reset ATR quartile flags
    atrLowbandLongFlag = atrHighbandLongFlag = atrLowbandShortFlag = atrHighbandShortFlag = false;
    atrCurrRecordedLevel = double.NaN;
    atrNowLevel = double.NaN;
    atrPrevTouchedLevel = double.NaN;
    atrPrevTouchedLevel = double.MinValue;

    prevLow = PriorDayOHLC().PriorLow[0];
    prevHigh = PriorDayOHLC().PriorHigh[0];
    Print("Reset at " + toTime + ": PrevLow = " + prevLow + ", PrevHigh = " + prevHigh);
    currRecordedPivot  = double.NaN;
    NowPivot = double.NaN;
    prevTouchedPivot = double.NaN;


    // Initialize prevTouchedPivot to the lowest possible pivot value
    prevTouchedPivot = double.MinValue;



    }



    // PnL-based exit logic
    if (SystemPerformance.AllTrades.Count > 0 && !dayOverVar)
    {
    double cumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - PrevDayPnL;
    double totalPL = AccountRealizedPL + AccountUnrealizedPL;
    //Print("cumProfit: "+cumProfit +" --- totalPL: " +" ----AccountUnrealizedPL "+AccountUnrealizedPL);
   // if (cumProfit < MaxLoss || totalPL < MaxLoss || totalPL > 200 * TradeSize || AccountUnrealizedPL < -500)
    //  if (cumProfit < MaxLoss || AccountRealizedPL < MaxLoss || AccountRealizedPL > 200 * TradeSize || AccountUnrealizedPL < -500)
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

    // Store session open at 8:30 AM
    if (toTime == 830 && BarsInProgress == 0 && IsFirstTickOfBar)
    {
        sessionOpen = Open[0];
        Print("Session Open stored: " + sessionOpen);
    }

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

    // Calculate and plot ATR quartile levels
    if (sessionOpen > 0) // Only calculate if we have session open
    {
        // Calculate ATR quartile levels
        atrQ1Level = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.25));
        atrQ2Level = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.50));
        atrQ3Level = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.75));
        atrQ4Level = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + MyATR);

        // Calculate mid-levels
        atrQ0Q1midLevel = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.125));
        atrQ1Q2midLevel = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.375));
        atrQ2Q3midLevel = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.625));
        atrQ3Q4midLevel = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 0.875));
        atrQ4Q5midLevel = Instrument.MasterInstrument.RoundToTickSize(sessionOpen + (MyATR * 1.125));

        // Create corresponding negative levels (below session open)
        List<double> positiveLevels = new List<double> { 
            atrQ0Q1midLevel, atrQ1Level, atrQ1Q2midLevel, atrQ2Level, atrQ2Q3midLevel, 
            atrQ3Level, atrQ3Q4midLevel, atrQ4Level, atrQ4Q5midLevel 
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
        int atrPlotStartIndex = 13; // Starting after pivot plots
        Values[atrPlotStartIndex + 0][0] = sessionOpen - (MyATR * 1.125);     // Below -Q4
        Values[atrPlotStartIndex + 1][0] = sessionOpen - MyATR;               // -Q4
        Values[atrPlotStartIndex + 2][0] = sessionOpen - (MyATR * 0.875);     // -Q3Q4 Mid
        Values[atrPlotStartIndex + 3][0] = sessionOpen - (MyATR * 0.75);      // -Q3
        Values[atrPlotStartIndex + 4][0] = sessionOpen - (MyATR * 0.625);     // -Q2Q3 Mid
        Values[atrPlotStartIndex + 5][0] = sessionOpen - (MyATR * 0.50);      // -Q2
        Values[atrPlotStartIndex + 6][0] = sessionOpen - (MyATR * 0.375);     // -Q1Q2 Mid
        Values[atrPlotStartIndex + 7][0] = sessionOpen - (MyATR * 0.25);      // -Q1
        Values[atrPlotStartIndex + 8][0] = sessionOpen - (MyATR * 0.125);     // Below -Q1
        Values[atrPlotStartIndex + 9][0] = sessionOpen;                       // Session Open
        Values[atrPlotStartIndex + 10][0] = sessionOpen + (MyATR * 0.125);    // Above Q1
        Values[atrPlotStartIndex + 11][0] = sessionOpen + (MyATR * 0.25);     // Q1
        Values[atrPlotStartIndex + 12][0] = sessionOpen + (MyATR * 0.375);    // Q1Q2 Mid
        Values[atrPlotStartIndex + 13][0] = sessionOpen + (MyATR * 0.50);     // Q2
        Values[atrPlotStartIndex + 14][0] = sessionOpen + (MyATR * 0.625);    // Q2Q3 Mid
        Values[atrPlotStartIndex + 15][0] = sessionOpen + (MyATR * 0.75);     // Q3
        Values[atrPlotStartIndex + 16][0] = sessionOpen + (MyATR * 0.875);    // Q3Q4 Mid
        Values[atrPlotStartIndex + 17][0] = sessionOpen + MyATR;              // Q4
        Values[atrPlotStartIndex + 18][0] = sessionOpen + (MyATR * 1.125);    // Above Q4
    }

  Print("====START==== "+Time[0]);
   NowPivot=double.NaN;
    for (int i = 0; i < levels.Count - 1; i++)
    if (High[1] >= levels[i]  && levels[i]>=Low[1]) 
    NowPivot=levels[i];

    if (!double.IsNaN(NowPivot))
{
    // Update the dictionary with current bar index
    lastPivotTouchBarIndex[NowPivot] = CurrentBar;

    // Print for debugging
    Print("Pivot " + NowPivot + " touched at bar " + CurrentBar);
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
        Print("ATR Level " + atrNowLevel + " touched at bar " + CurrentBar);
    }

    // Process pivot point touch
    if (!double.IsNaN(NowPivot))
    {
    // If this is a new pivot touch
    // if (NowPivot != currRecordedPivot)
    {
    // Store last pivot info
    prevTouchedPivot = currRecordedPivot;
    currRecordedPivot = NowPivot;

    }
    // If we're at the same pivot as before, nothing to update
    }

    // Process ATR level touch
    if (!double.IsNaN(atrNowLevel))
    {
        // Store last ATR level info
        atrPrevTouchedLevel = atrCurrRecordedLevel;
        atrCurrRecordedLevel = atrNowLevel;
    }

    //    Print("prevTouchedPivot: " + prevTouchedPivot + 
    //    " currRecordedPivot: " + currRecordedPivot + 
    //    " NowPivot: " + NowPivot );

    // Calculate VWAP
    vwPrice = Instrument.MasterInstrument.RoundToTickSize(VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[0]);
 //double avwap1=AVWAP2(BarsArray[0],AnchorFrom , new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[0];
  //double avwap1=AnchoredVWAP(AnchorFrom ,true).VWAP[0];
 //Print("Anchored VWAP1 " + avwap1);
		 lATR=Instrument.MasterInstrument.RoundToTickSize(1.5*ATR(Closes[1], 14)[0]);
    // Entry logic
    if (Position.MarketPosition == MarketPosition.Flat //|| Position.Quantity == Convert.ToInt32(TradeSize)
    )
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
    //lowbandShortFlag = entryOrder == null && Low[2] < lowband && High[2] > lowband && High[1] < lowband;
    // highbandLongFlag = entryOrder == null && Low[2] < highband && High[2] > highband && Low[1] > highband;
    highbandShortFlag = entryOrder == null && (Close[0]-lowband)>(highband-Close[0]);
    //  vwapLongFlag = entryOrder == null && Low[2] < vwPrice && High[2] > vwPrice && Low[1] > vwPrice;
    //  vwapShortFlag = entryOrder == null && Low[2] < vwPrice && High[2] > vwPrice && High[1] < vwPrice;


    bool pivotRevisitedWithSufficientRetracement = false;
    double currentATR = ATR(Closes[1], 14)[0];

    // To find bars since last touch for a specific pivot:
if (lastPivotTouchBarIndex.ContainsKey(currRecordedPivot))
{
    int barsSinceTouch = CurrentBar - lastPivotTouchBarIndex[currRecordedPivot];
    Print("Bars since pivot " + currRecordedPivot + " was last touched: " + barsSinceTouch);


    double enthaDeviation = GetMaxDeviationSinceLastTouch(currRecordedPivot, barsSinceTouch);
   // double simpleDeviation = Math.Abs(Close[0]-currRecordedPivot);
    Print("Maximum deviation from pivot " + currRecordedPivot + " in the last " + 
    barsSinceTouch + " bars: " + enthaDeviation +" ATR is "+currentATR);
    pivotRevisitedWithSufficientRetracement = enthaDeviation>3*currentATR;
    Print("pivotRevisitedWithSufficientRetracement: "+pivotRevisitedWithSufficientRetracement);

}

//wtd anchor -- 1st priority
/*
if ((toTime-(ToTime(AnchorFrom) / 100.0))==0)
	{
		AnchorFromClose=Close[0];
	}
	Print("AnchorFrom "+AnchorFrom);
	Print("curr "+DateTime.Parse("12:30 AM"));
	if (AnchorFrom != DateTime.Parse("12:30 AM"))
	{

	Print("AnchorFromClose "+AnchorFromClose);

	//Print("High[HighestBar(High, 5) "+High[HighestBar(High, 10)]);

	Print("=== avwap1 "+avwap1);
		Print("Low[LowestBar(Low, 5)] "+Low[LowestBar(Low, 10)]);

		//  isAVWAP1DiffBand = !(highband > avwap1 && avwap1 > lowband);

		if ( LongTradeFlag== true && wtdentryOrder == null)	 { 
		l1Entry =avwap1; //(isAVWAP1DiffBand?((avwap1-nextbandS)<=10? nextbandS: avwap1) : ((avwap1-lowband)<=10? lowband: avwap1));
		SetStopLoss(CalculationMode.Ticks, 4*4);
			EnterLongLimit(0, false, Convert.ToInt32(TradeSize), l1Entry , "WTD L 1st");
		//maybe below can be moved to on order execution!!!

       --if (LetProfRun)
		--	SetProfitTarget("L 1st", CalculationMode.Price, (isAVWAP1DiffBand? ((lowband-l1Entry)>=25? lowband: highband): (highband-l1Entry)>25? highband:nextbandL)); // can add math max 10
		-- else
		--	SetProfitTarget("L 1st",CalculationMode.Ticks, 80);
			SetProfitTarget("WTD L 1st", CalculationMode.Price, (l1Entry + 20));


Print(" LOng Order 1 is set : " +l1Entry + " ----SL: "+ (l1Entry - lATR) + " ----PT: "+(l1Entry + lATR*2));

		}//long trade
	else if (ShortTradeFlag== true && wtdentryOrder == null)
	{
		s1Entry =avwap1;// (isAVWAP1DiffBand?((nextbandL -avwap1)<=10? nextbandL: avwap1) : ((highband -avwap1)<=10? highband: avwap1));
		SetStopLoss(CalculationMode.Ticks, 4*4);
		EnterShortLimit(0, false, Convert.ToInt32(TradeSize), s1Entry, "WTD S 1st");
	//	EnterShortLimit(0, false, Convert.ToInt32(TradeSize/2), s1Entry, "S 1st 2/2");
		//maybe below can be moved to on order execution!!!
		//SetStopLoss(CalculationMode.Ticks, 4*(s1Entry + lATR));
       -- if (LetProfRun)
		--	SetProfitTarget("S 1st",  CalculationMode.Price, (isAVWAP1DiffBand? ((s1Entry-highband)>=25? highband: lowband): (s1Entry-lowband)>25? lowband:nextbandS)); // can add math max 10
	--	else
	--		SetProfitTarget("S 1st",CalculationMode.Ticks, 80);
		SetProfitTarget("WTD S 1st",CalculationMode.Price, (s1Entry - 20));
		//SetProfitTarget("S 1st 2/2",CalculationMode.Price, (s1Entry - 10*2));
		Print(" Short Order 1  is set :" +s1Entry+ " ----SL: "+ (s1Entry + 4) + " ----PT: "+(s1Entry - 10*2));
	}
	else{
		//  Print(" in else " +entryOrder );
		//current price should be latr more than avwap
		///
		if (Time[0]>AnchorFrom)
		 	// if ((Close[0] -lATR) >avwap1  && Low[LowestBar(Low, 5)] >=avwap1 &&  Position.Quantity< Convert.ToInt32(TradeSize)+2)
			 if ((Close[0] -avwap1) <currentATR*2  && Low[LowestBar(Low, 5)] >=avwap1 &&  Position.Quantity< Convert.ToInt32(TradeSize)+2)
		  {
		  	LongTradeFlag= true;
			   Print(" LongTradeFlag is set to true: " );			  
		  }
		  else if ((avwap1-Close[0] ) <currentATR*2  && High[HighestBar(High, 5)]<=avwap1 && Position.Quantity< Convert.ToInt32(TradeSize)+2)
		  {
			  ShortTradeFlag= true;
			   Print(" ShortTradeFlag is set to true: " );	
		  }
		  else{
		  	LongTradeFlag= false;
			  ShortTradeFlag= false;
			   Print(" All 1 are false" );	

		  }

	}

	}*/



    // UPDATED ENTRY LOGIC with correct pivot direction check
    if (entryOrder == null //&& newPivotTouched 
    && highbandShortFlag   && (prevTouchedPivot == double.MinValue || 
			currRecordedPivot != prevTouchedPivot ||
			pivotRevisitedWithSufficientRetracement)
    && ATR(Closes[1], 14)[0] < 10)
    {
    Print("currRecordedPivot (" + currRecordedPivot + ") < prevTouchedPivot (" + prevTouchedPivot + ")");
    // Check if VWAP is within 10 points of the highband pivot
   // if (Math.Abs(vwPrice - highband) <= 10)
  //  {
    // Use the higher of the two for short entry
   //    adjEntry = Math.Max(vwPrice, highband);
   //    Print("Using higher of VWAP (" + vwPrice + ") and highband (" + highband + ") for short entry: " + adjEntry);
  //  }
  //  else
  //  {
   //    adjEntry = highband;
  //  }

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
    else if (entryOrder == null //&& newPivotTouched 
    && (prevTouchedPivot == double.MinValue 
			|| currRecordedPivot != prevTouchedPivot 
			|| pivotRevisitedWithSufficientRetracement)
    && (lowbandLongFlag ) && ATR(Closes[1], 14)[0] < 10)
    {
    Print("currRecordedPivot (" + currRecordedPivot + ") > prevTouchedPivot (" + prevTouchedPivot + ")");
    // Check if VWAP is within 10 points of the lowband pivot
   /* if (Math.Abs(vwPrice - lowband) <= 10)
    {
    // Use the lower of the two for long entry
    adjEntry = Math.Min(vwPrice, lowband);
    Print("Using lower of VWAP (" + vwPrice + ") and lowband (" + lowband + ") for long entry: " + adjEntry);
    }
    else
    {
    adjEntry = lowband;
    }*/
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

    // ATR Entry Logic (independent of pivot trades)
    if (atrLevels != null)
    {
        SetATRBands(Close[0]);
        Print("ATR Bands set: atrLowband=" + atrLowband + ", atrHighband=" + atrHighband);

        if (atrEntryOrder != null)
        {
            CancelOrder(atrEntryOrder);
            atrEntryOrder = null;
            Print("Cancelled previous ATR entry order");
        }

        // Set ATR entry flags
        atrLowbandLongFlag = atrEntryOrder == null && (Close[0] - atrLowband) < (atrHighband - Close[0]);
        atrHighbandShortFlag = atrEntryOrder == null && (Close[0] - atrLowband) > (atrHighband - Close[0]);

        bool atrLevelRevisitedWithSufficientRetracement = false;

        // Check ATR level retracement
        if (lastATRLevelTouchBarIndex.ContainsKey(atrCurrRecordedLevel))
        {
            int barsSinceTouch = CurrentBar - lastATRLevelTouchBarIndex[atrCurrRecordedLevel];
            double atrDeviation = GetMaxDeviationSinceLastTouch(atrCurrRecordedLevel, barsSinceTouch);
            atrLevelRevisitedWithSufficientRetracement = atrDeviation > 3 * currentATR;
            Print("ATR Level revisited with sufficient retracement: " + atrLevelRevisitedWithSufficientRetracement);
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
            atrBE_Set = false;
            atrTrail_Set = false;
            atrCurrentTrailStopPrice = 0;
            Print("Entering ATR Short: StopLoss at " + (atrHighband + 4) + ", ProfitTarget at " + (atrAdjEntry - 20));
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
            atrBE_Set = false;
            atrTrail_Set = false;
            atrCurrentTrailStopPrice = 0;
            Print("Entering ATR Long: StopLoss at " + (atrLowband - 4) + ", ProfitTarget at " + (atrAdjEntry + 20));
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
    Print("Moved StopLoss to breakeven for Long position");
    }
    //if (Close[1] < Close[2] - 0.5 && Close[2] < Close[3] - 0.5 && (Time[0] - orderTime).TotalMinutes > 4)
    if (Close[1] < Close[2] -0.5 && Close[2] < Close[3]-0.5  && ((Time[0] - orderTime).TotalMinutes-BarsPeriod.Value) > 4)
    {
    ExitLong();
    Print("Exiting Long position due to price action: "+((Time[0] - orderTime).TotalMinutes-BarsPeriod.Value));
    }
    // New trailing stop logic
    /*  double triggerPriceLong = Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice + 1.5*ATR(Closes[1], 14)[0]);
    if (Close[0] >= triggerPriceLong)
    {
    if (!Trail_Set)
    {
    Trail_Set = true;
    currentTrailStopPrice = Math.Max(Low[1]-0.25,Position.AveragePrice);//Instrument.MasterInstrument.RoundToTickSize(Close[0] - 10);
    SetStopLoss(CalculationMode.Price, currentTrailStopPrice);
    Print("Trailing stop loss activated at " + currentTrailStopPrice);
    }
    else
    {
    double newTrail = Low[1]-0.25;//Instrument.MasterInstrument.RoundToTickSize(Close[0] - 10);
    if (newTrail > currentTrailStopPrice)
    {
    currentTrailStopPrice = newTrail;
    SetStopLoss(CalculationMode.Price, Math.Max(currentTrailStopPrice, Position.AveragePrice));
    Print("Trailing stop updated to " + currentTrailStopPrice);
    }
    }
    }*/
	// New trailing stop logic
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
    //if (Close[1] - 0.5 > Close[2] && Close[2] - 0.5 > Close[3] && (Time[0] - orderTime).TotalMinutes > 4)
    if (Close[1] -0.5 > Close[2] && Close[2]-0.5 > Close[3] && ((Time[0] - orderTime).TotalMinutes-BarsPeriod.Value) > 4)
    {
    ExitShort();
    Print("Exiting Short position due to price action: "+((Time[0] - orderTime).TotalMinutes-BarsPeriod.Value));
    }
    // New trailing stop logic
    /* double triggerPriceShort = Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice - 1.5*ATR(Closes[1], 14)[0]);
    if (Close[0] <= triggerPriceShort)
    {
    if (!Trail_Set)
    {
    Trail_Set = true;
    currentTrailStopPrice = Math.Min(High[1]+0.25, Position.AveragePrice);//Instrument.MasterInstrument.RoundToTickSize(Close[0] + 10);
    SetStopLoss(CalculationMode.Price, currentTrailStopPrice);
    Print("Trailing stop loss activated at " + currentTrailStopPrice);
    }
    else
    {
    double newTrail = High[1]+0.25;//Instrument.MasterInstrument.RoundToTickSize(Close[0] + 10);
    if (newTrail < currentTrailStopPrice)
    {
    currentTrailStopPrice = newTrail;
    SetStopLoss(CalculationMode.Price, Math.Min(currentTrailStopPrice, Position.AveragePrice));
    Print("Trailing stop updated to " + currentTrailStopPrice);
    }
    }
    }*/
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
                break;
            }
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

    // Handle ATR orders
    if (atrEntryOrder != null && atrEntryOrder.IsBacktestOrder && State == State.Realtime)
        atrEntryOrder = GetRealtimeOrder(atrEntryOrder);

    if (entryOrder == null && (order.Name.StartsWith("Long") || order.Name.StartsWith("Short")))
    {
    entryOrder = order;
    //Print("ORDER EXECUTED: "+order.Name);
    }

	 if (wtdentryOrder == null && (order.Name.StartsWith("WTD L") || order.Name.StartsWith("WTD S")))
    {
    wtdentryOrder = order;
    //Print("ORDER EXECUTED: "+order.Name);
    }

    // Handle ATR orders
    if (atrEntryOrder == null && (order.Name.StartsWith("ATR Long") || order.Name.StartsWith("ATR Short")))
    {
        atrEntryOrder = order;
    }

    if (entryOrder != null && order.OrderState == OrderState.Cancelled)
    entryOrder = null;


	 if (wtdentryOrder != null && order.OrderState == OrderState.Cancelled)
    wtdentryOrder = null;

    // Handle ATR order cancellation
    if (atrEntryOrder != null && order.OrderState == OrderState.Cancelled)
        atrEntryOrder = null;
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
    // Handle ATR order executions
    else if (execution.Order.Name.StartsWith("ATR Long") || execution.Order.Name.StartsWith("ATR Short"))
    {
        atrBE_Set = false;
        atrTrail_Set = false;
        atrCurrentTrailStopPrice = 0;
        SetATRBands(Close[0]);
        atrOrderDilowband = atrLowband;
        atrOrderDihighband = atrHighband;
        atrOrderDinextband = execution.Order.Name.StartsWith("ATR Long") ? atrNextbandL : atrNextbandS;
        Print("ATR ORDER EXECUTED:: " + execution.Order.Name + ", ATR Bands: Lowband=" + atrOrderDilowband + ", Highband=" + atrOrderDihighband);
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

        // Handle ATR order completion
        if (execution.Order.Name.StartsWith("ATR"))
            atrEntryOrder = null;
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
    [Display(Name = "Anchor VWAP from 1st", Order = 6, GroupName = "Parameters")]
    public DateTime AnchorFrom { get; set; }

    [PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
    [Display(Name = "Anchor VWAP from 2nd", Order = 7, GroupName = "Parameters")]
    public DateTime AnchorFrom2 { get; set; }

    [Range(double.MinValue, double.MaxValue), NinjaScriptProperty]
    [Display(ResourceType = typeof(Custom.Resource), Name = "Max Loss", GroupName = "Parameters", Order = 5)]
    public double MaxLoss { get; set; }

    [Range(0.1, double.MaxValue), NinjaScriptProperty]
    [Display(ResourceType = typeof(Custom.Resource), Name = "My ATR", GroupName = "Parameters", Order = 4)]
    public double MyATR { get; set; }

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

    // ATR Quartile Series
    [XmlIgnore] public Series<double> ATRNegQ4Q5Mid => Values[13];
    [XmlIgnore] public Series<double> ATRNegQ4Level => Values[14];
    [XmlIgnore] public Series<double> ATRNegQ3Q4Mid => Values[15];
    [XmlIgnore] public Series<double> ATRNegQ3Level => Values[16];
    [XmlIgnore] public Series<double> ATRNegQ2Q3Mid => Values[17];
    [XmlIgnore] public Series<double> ATRNegQ2Level => Values[18];
    [XmlIgnore] public Series<double> ATRNegQ1Q2Mid => Values[19];
    [XmlIgnore] public Series<double> ATRNegQ1Level => Values[20];
    [XmlIgnore] public Series<double> ATRNegQ0Q1Mid => Values[21];
    [XmlIgnore] public Series<double> ATRSessionOpen => Values[22];
    [XmlIgnore] public Series<double> ATRQ0Q1Mid => Values[23];
    [XmlIgnore] public Series<double> ATRQ1Level => Values[24];
    [XmlIgnore] public Series<double> ATRQ1Q2Mid => Values[25];
    [XmlIgnore] public Series<double> ATRQ2Level => Values[26];
    [XmlIgnore] public Series<double> ATRQ2Q3Mid => Values[27];
    [XmlIgnore] public Series<double> ATRQ3Level => Values[28];
    [XmlIgnore] public Series<double> ATRQ3Q4Mid => Values[29];
    [XmlIgnore] public Series<double> ATRQ4Level => Values[30];
    [XmlIgnore] public Series<double> ATRQ4Q5Mid => Values[31];
    #endregion

    protected override void OnAccountItemUpdate(Account account, AccountItem accountItem, double value)
    {
    AccountRealizedPL = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
    AccountUnrealizedPL = account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
    }
    }
}
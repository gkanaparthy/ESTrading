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
    public class PP20251direcB4clean : Strategy
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
    private int prevBandIndex = -1;
    private double lastTouchedPivot = double.NaN;
    private double prevTouchedPivot = double.NaN;
    private bool newPivotTouched = false;

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
    Name = "PP20251direcB4clean";
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
    Print("Reset at " + toTime + ": PrevLow = " + prevLow + ", PrevHigh = " + prevHigh);
    }

    // Enter long trade at 15:05 if no trades today
    if (SystemPerformance.AllTrades.Count - PrevDayTradeCount == 0 && toTime == 1505 && BarsInProgress == 0 && IsFirstTickOfBar)
    {
    SetStopLoss(CalculationMode.Ticks, 1);
    SetProfitTarget("Long VWAP 1st", CalculationMode.Ticks, 2);
    EnterLong(2, 1, "Long VWAP 1st");
    Print("Entering Long VWAP 1st at 15:05");
    }

    // PnL-based exit logic
    if (SystemPerformance.AllTrades.Count > 0 && !dayOverVar)
    {
    double cumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - PrevDayPnL;
    double totalPL = AccountRealizedPL + AccountUnrealizedPL;
    if (cumProfit < MaxLoss || totalPL < MaxLoss || totalPL > 100 * TradeSize || AccountUnrealizedPL < -500)
    {
    dayOverVar = true;
    if (Position.MarketPosition == MarketPosition.Long) ExitLong();
    else if (Position.MarketPosition == MarketPosition.Short) ExitShort();
    Print("Exiting due to PnL conditions: CumProfit = " + cumProfit + ", TotalPL = " + totalPL + ", UnrealizedPL = " + AccountUnrealizedPL);
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

    // Update pivot tracking with improved directional logic
    Print("Current price " + Close[0] + " Pivot: " + pLevel + " Settlement " + prevClose + " Timestamp: " + Time[0]);
    int currentBandIndex = GetBandIndex(Close[0]);
    Print(" prevBandIndex: " + prevBandIndex + " currentBandIndex: " + currentBandIndex);
    
    // Reset the new pivot touched flag
    newPivotTouched = false;
    
    // Check if we've moved to a new band
    if (currentBandIndex != prevBandIndex && prevBandIndex != -1 && currentBandIndex != -1)
    {
        // Determine which pivot level we've touched
        double currentPivot;
        if (currentBandIndex > prevBandIndex) {
            // Moving up - touched the lower boundary of the new band
            currentPivot = levels[currentBandIndex];
        } else {
            // Moving down - touched the upper boundary of the new band
            currentPivot = levels[currentBandIndex + 1];
        }
        
        // Check if this is a different pivot than the last one we touched
        if (double.IsNaN(lastTouchedPivot) || Math.Abs(currentPivot - lastTouchedPivot) > 0.001) {
            // We've touched a new, different pivot
            prevTouchedPivot = lastTouchedPivot;
            lastTouchedPivot = currentPivot;
            
            // Only consider it a new pivot if it's different from the previous one
            if (!double.IsNaN(prevTouchedPivot) && Math.Abs(lastTouchedPivot - prevTouchedPivot) > 0.001) {
                newPivotTouched = true;
                Print("New pivot touched: Previous=" + prevTouchedPivot + ", Current=" + lastTouchedPivot);
            }
        } else {
            // We've revisited the same pivot point - do nothing
            Print("Revisited same pivot: " + lastTouchedPivot);
        }
    }
    prevBandIndex = currentBandIndex;
    
    Print("prevTouchedPivot: " + prevTouchedPivot + "  latest PP: " + lastTouchedPivot);

    // Calculate VWAP
    vwPrice = Instrument.MasterInstrument.RoundToTickSize(VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[0]);

    // Entry logic
    if (Position.MarketPosition == MarketPosition.Flat || Position.Quantity == Convert.ToInt32(TradeSize))
    {
        SetBands(Close[0]);
        Print("Bands set: Lowband=" + lowband + ", Highband=" + highband + ", NextbandL=" + nextbandL + ", NextbandS=" + nextbandS);

        if (entryOrder != null)
        {
            CancelOrder(entryOrder);
            entryOrder = null;
            Print("Cancelled previous entry order");
        }

        // Set entry flags
        lowbandLongFlag = entryOrder == null && Low[2] < lowband && High[2] > lowband && Low[1] > lowband;
        lowbandShortFlag = entryOrder == null && Low[2] < lowband && High[2] > lowband && High[1] < lowband;
        highbandLongFlag = entryOrder == null && Low[2] < highband && High[2] > highband && Low[1] > highband;
        highbandShortFlag = entryOrder == null && Low[2] < highband && High[2] > highband && High[1] < highband;
        vwapLongFlag = entryOrder == null && Low[2] < vwPrice && High[2] > vwPrice && Low[1] > vwPrice;
        vwapShortFlag = entryOrder == null && Low[2] < vwPrice && High[2] > vwPrice && High[1] < vwPrice;

        // UPDATED ENTRY LOGIC with correct pivot direction check
        if (entryOrder == null //&& newPivotTouched 
			&& (lowbandShortFlag || highbandShortFlag || vwapShortFlag) && 
            lastTouchedPivot < prevTouchedPivot && ATR(Closes[1], 14)[0] < 90)
        {
            Print("SHORT ENTRY: lastTouchedPivot (" + lastTouchedPivot + ") < prevTouchedPivot (" + prevTouchedPivot + ")");
			adjEntry=Open[0];
			if ((highband + 0.25 - Open[0])> 5)
				adjEntry=highband-5;
			
            EnterShortLimit(0, false, Convert.ToInt32(TradeSize), adjEntry, "Short VWAP");
            OrderDilowband = lowband;
            OrderDihighband = highband;
            OrderDinextband = nextbandS;
            SetStopLoss(CalculationMode.Price, highband + 0.25);
            SetProfitTarget("Short VWAP", CalculationMode.Ticks, Math.Min( 16 * (highband + 0.25 - Close[0]), 40));
            BE_Set = false;
            Print("Entering Short VWAP: StopLoss at " + (highband + 0.25) + ", ProfitTarget at " + (16* (highband + 0.25 - Close[0])) + " ticks");
			
        }
        else if (entryOrder == null //&& newPivotTouched 
			&& (lowbandLongFlag || highbandLongFlag || vwapLongFlag) && 
                lastTouchedPivot > prevTouchedPivot && ATR(Closes[1], 14)[0] < 90)
        {
            Print("LONG ENTRY: lastTouchedPivot (" + lastTouchedPivot + ") > prevTouchedPivot (" + prevTouchedPivot + ")");
			adjEntry=Open[0];
			if ((Open[0] - (lowband - 0.25))>5)
				adjEntry=lowband+5;
            EnterLongLimit(0, false, Convert.ToInt32(TradeSize), adjEntry, "Long VWAP");
            OrderDilowband = lowband;
            OrderDihighband = highband;
            OrderDinextband = nextbandL;
            SetStopLoss(CalculationMode.Price, lowband - 1);
            SetProfitTarget("Long VWAP", CalculationMode.Ticks,  Math.Min(16* (Close[0] - (lowband - 0.25)), 40));
            BE_Set = false;
            Print("Entering Long VWAP: StopLoss at " + (lowband - 1) + ", ProfitTarget at " + (16* (Close[0] - (lowband - 0.25))) + " ticks");
			
        }
    }

    // Manage open positions
    if (Position.MarketPosition == MarketPosition.Long)
    {
    if (Close[0] >= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice + 5) && !BE_Set)
    {
    SetStopLoss(CalculationMode.Price, Position.AveragePrice);
    BE_Set = true;
    Print("Moved StopLoss to breakeven for Long position");
    }
    if (Close[1] < Close[2] - 0.5 && Close[2] < Close[3] - 0.5 && (Time[0] - orderTime).TotalMinutes > 3)
    {
    ExitLong();
    Print("Exiting Long position due to price action");
    }
    }
    else if (Position.MarketPosition == MarketPosition.Short)
    {
    if (Close[0] <= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice - 5) && !BE_Set)
    {
    SetStopLoss(CalculationMode.Price, Position.AveragePrice);
    BE_Set = true;
    Print("Moved StopLoss to breakeven for Short position");
    }
    if (Close[1] - 0.5 > Close[2] && Close[2] - 0.5 > Close[3] && (Time[0] - orderTime).TotalMinutes > 3)
    {
    ExitShort();
    Print("Exiting Short position due to price action");
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

    if (entryOrder == null && (order.Name.StartsWith("Long") || order.Name.StartsWith("Short")))
    entryOrder = order;

    if (entryOrder != null && order.OrderState == OrderState.Cancelled)
    entryOrder = null;
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
    Print("Order executed: " + execution.Order.Name + ", Bands: Lowband=" + OrderDilowband + ", Highband=" + OrderDihighband + ", Nextband=" + OrderDinextband);
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
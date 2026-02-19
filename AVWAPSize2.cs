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

//This namespace holds Strategies in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Strategies {
  public class AVWAPSize2: Strategy {
    private int entryBar;
   
    double prevClose;
    double prevHigh;
    double prevLow;

   bool BE_Set = false;
	
	  double stopNewCalc;
    double PrevDayPnL =0;
	double PrevDayTradeCount =0;
	double vwPrice;
	double AnchorFromClose;

      bool LongTradeFlag;
	   bool ShortTradeFlag;
	bool isVWAPDiffBand;
	bool isAVWAP1DiffBand;
	bool isAVWAP2DiffBand;
	double AccountRealizedPL;
	double AccountUnrealizedPL;
	  	bool Long2Flag;
	bool Short2Flag;
	
	
    private Order entryOrder = null; // This variable holds an object representing our entry order.
    private Order stopOrder = null; // This variable holds an object representing our stop loss order.
    private Order targetOrder = null; // This variable holds an object representing our profit target order.
	  private Order targetOrder1 = null; // This variable holds an object representing our profit target order.
	  private Order targetOrder2 = null; // This variable holds an object representing our profit target order.
	  private int lastThreeTrades 		= 0;  	// This variable holds our value for how profitable the last three trades were.
	  
	  private bool				dayOverVar=false;
	String lOrderName="Success";
	
		//private SessionIterator		sessionIterator;
	  private EMA ema1;
	  double lATR;
	DateTime  orderTime;
	double	iCumVolume			= 0;
		double	iCumTypicalVolume	= 0;
	double lBarsSinceVWAPTouch=0;
			DateTime	lTouchVWAPTime ;
				
double l1Entry;
	double l2Entry;
	double s1Entry;
	double s2Entry;
	
	private  VWAPDesign.StdDesign Deviation1;//= {false, 1};
	private  VWAPDesign.StdDesign Deviation2;//= {false, 2};
	private  VWAPDesign.StdDesign Deviation3;//= {false, 3};
	
		
		private enum AverageType
		{
			EMA,
			HMA,
			SMA,
			WMA,	
		}
		
		private Series<double> Trend;
		private Series<double> aggregationPeriodTrend;
		private Series<double> price;
		private Series<double> absDiff;
		private Series<double> aggregationPeriodPrice;
		private Series<double> agPerAbsDiff;
		private Series<double> ZLevel;
		private Series<double> aggregationPeriodZLevel;
		private Series<int> myState;
		private Series<int> aggregationPeriodState;
		private Series<double> volumeSum;
		private Series<double> volumeVwapSum;
		private Series<double> volumeVwap2Sum;
		private Series<double> VWAP;
		private Series<double> UpperBand;
		private Series<double> LowerBand;
		private Series<double> UpperBandFirst;
		private Series<double> LowerBandFirst;
		
		private SessionIterator sessionIterator;
		private DateTime tradingDay;
    	private DateTime beginTime;
    	private DateTime endTime;
		
		private Series<double> zupData;
		private Series<double> zdnData;
		
		private bool playBuy = false; 
		private bool playExitLong = false;
		private bool playSell = false;
		private bool playExitShort = false;
   
	  
	  private VWAP1	ofVwapETH;
		private AnchoredVWAP myAVWAP1;
		private AnchoredVWAP orderAVWAP;
		private AVWAP2 VWAPx1;
		private AVWAP2 VWAPx2;
	 // private Series < double > VWAPGK;
	  
	  private bool ConvertLocalToESTTime						= true;
				private double WindowStart1						= 700;
				private double WindowEnd1						= 1505;

    protected override void OnMarketData(Data.MarketDataEventArgs marketDataUpdate) {
      if (marketDataUpdate.IsReset)
        prevClose = double.MinValue;
      else if (marketDataUpdate.MarketDataType == Data.MarketDataType.Settlement)
        prevClose = marketDataUpdate.Price;

      //Print(string.Format("MarketDataType.Settlement = " + prevClose));
      //	Print(string.Format("MarketDataType.DailyHigh = "+ HighValue));
      //	Print(string.Format("MarketDataType.DailyLow = "+ LowValue));

    }

    protected override void OnStateChange() {
      if (State == State.SetDefaults) {
        Description = @"Enter the description for your new custom Strategy here.";
        Name = "AVWAPSize2";
        Calculate = Calculate.OnEachTick; //OnBarClose;
        EntriesPerDirection = 3;
        EntryHandling = EntryHandling.UniqueEntries;
        IsExitOnSessionCloseStrategy = true;
        ExitOnSessionCloseSeconds = 3600;
        IsFillLimitOnTouch = false;
        MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
        OrderFillResolution = OrderFillResolution.Standard;
        Slippage = 0;
        StartBehavior = StartBehavior.WaitUntilFlat;
        TimeInForce = TimeInForce.Day;
        TraceOrders = false;
        RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;
        StopTargetHandling = StopTargetHandling.PerEntryExecution;
        BarsRequiredToTrade = 24;
        // Disable this property for performance gains in Strategy Analyzer optimizations
        // See the Help Guide for additional information
        IsInstantiatedOnEachOptimizationIteration = true;
        TradeSize = 10; //hasto be divisible by 3
        TimeInForce = TimeInForce.Gtc;
          //EMAperiod = 13;
	useVwap = false;
	MaxLoss = -2000;
	//MTF											= 15;
		//		entryMode									= EntryMode.Aggressive;
	//UseConservativeEntries						= false;
	//			UseWiderStops								= true;
	//			SRPercentThreshold							= 50;
	//			NearBand =false;
	AnchorFrom = DateTime.Parse("12:30 AM");
	AnchorFrom2 = DateTime.Parse("12:30 AM");
	//prfMul = 3;
				
		  
		 
		AddPlot(Brushes.Green, "TrendLine");
				AddPlot(Brushes.Green, "HTF_TrendLine");
				Plots[0].Width = 1;	
				Plots[1].Width = 2;	
		  
		  

       // AddPlot(Brushes.Purple, "vwap");
        //	Plots[0].Width = 2;	
      } else if (State == State.Configure) {

        AddDataSeries(Data.BarsPeriodType.Minute, 1);
        //SetStopLoss(CalculationMode.Ticks, 10);
	AddDataSeries(Data.BarsPeriodType.Tick, 1);
	
		//AddDataSeries(Data.BarsPeriodType.Minute, MTF);
			

      } else if (State == State.DataLoaded) {

      
        //VWAPGK = new Series < double > (BarsArray[0]);
		  
		  
		  ofVwapETH	= VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
		 		AddChartIndicator(ofVwapETH);
	
	//myAVWAP1=AnchoredVWAP(AnchorFrom);
		//AddChartIndicator(myAVWAP1);
	
	 VWAPx1	= AVWAP2(BarsArray[0], AnchorFrom,  new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
		 		AddChartIndicator(VWAPx1);
	
	VWAPx2	= AVWAP2(BarsArray[0], AnchorFrom2,  new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
		 		AddChartIndicator(VWAPx2);
	
	//VWAPx1 = VWAPxnew();
		// AddChartIndicator(VWAPx1);
		
      }
      	

    }

    protected override void OnBarUpdate() {

      if (CurrentBars[0] < BarsRequiredToTrade)
        return;
	  
	  DateTime targetTime = Time[0];
	 // Print ("Time is "+targetTime);
				
				if (ConvertLocalToESTTime)
					TimeZoneInfo.ConvertTime(Time[0], TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
				
				double toTime = ToTime(targetTime) / 100.0;
			//	Print ("Time is converted: "+targetTime);
				
					if ((toTime - 1510  == 0 || toTime - 2100  == 0)  && (BarsInProgress == 0 && IsFirstTickOfBar))
			{
				Print("resetting flags");
				 LongTradeFlag= false;
	 			 ShortTradeFlag= false;
				isVWAPDiffBand = false;
				PrevDayPnL=SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
				PrevDayTradeCount=SystemPerformance.AllTrades.Count;
				dayOverVar=false;
				//touchflag=false;
				//freeflag=false;
				//notWithin1Point=false;
				lastThreeTrades=0;
				//orderTime=null;
				lOrderName="Success";
				
				// Print("******PREV CLOSE***** "+PriorDayOHLC().PriorClose[0]);
				
				 prevLow = PriorDayOHLC().PriorLow[0];
        prevHigh = PriorDayOHLC().PriorHigh[0];
		
				        Print("******PREV HIGH***** "+prevHigh);
				        Print("******PREV LOW***** "+prevLow);
						Print("******PREV Settlement***** "+prevClose);
			}
			else if(toTime - 700  == 0)
			{
				isVWAPDiffBand =true;
			}
				
				bool isitEarly = toTime - 1500 >= 0 && toTime - 2100  < 0;
				
				//if(isitEarly)					return;
				
				bool isitRTH = toTime - WindowStart1 > 0 && toTime - WindowEnd1  < 0;
				
				//if (RTHorETH==false)
					isitRTH=true;
				
			//limiting loss in a day
				if (SystemPerformance.AllTrades.Count>0)
				{
					if ((SystemPerformance.AllTrades.Count - PrevDayTradeCount)>=3)
					{
						lastThreeTrades=0;
					for (int idx = 1; idx <= 3; idx++)
						{
							/* The SystemPerformance.AllTrades array stores the most recent trade at the highest index value. If there are a total of 10 trades,
							   this loop will retrieve the 10th trade first (at index position 9), then the 9th trade (at 8), then the 8th trade. */
							Trade trade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - idx];
							//Print ("Last trade exit time: "+trade.Exit.Time);
							if (  (ToTime(trade.Exit.Time) / 100.0 ) > 830 && (ToTime(trade.Exit.Time) / 100.0 )<1500){
							if (trade.ProfitCurrency >= 0 )
							lastThreeTrades++;
							else if (trade.ProfitCurrency < 0 )
							lastThreeTrades--;
							//Print("inside 3 losses");
							}
							//Print("Fact check: Profit of last three PT3s: "+trade.ProfitCurrency);
						}
					}
						else
					{
							lastThreeTrades=0;
						
						//3pm trade
						if (((SystemPerformance.AllTrades.Count - PrevDayTradeCount)==0) && (toTime - 1505  == 0) &&  BarsInProgress == 0 && IsFirstTickOfBar)
								{
									SetStopLoss(CalculationMode.Ticks, 1);
							        SetProfitTarget("Long VWAP 1st", CalculationMode.Ticks, 2);
									EnterLong(2,  1, "Long VWAP 1st");
									
									Print("3PM trade taken-1");
									Print("SystemPerformance.AllTrades.Count "+SystemPerformance.AllTrades.Count);
									Print("PrevDayTradeCount "+PrevDayTradeCount);
								}
					}
						
					if ((
						(SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - PrevDayPnL)< MaxLoss 
						|| (AccountRealizedPL+AccountUnrealizedPL)< MaxLoss
						|| (AccountRealizedPL+AccountUnrealizedPL)> 2025 * 1
						//|| lastThreeTrades==-3
						) 
						&& dayOverVar==false)
						{
							Print("Day over: Max loss reached  "+SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit+ "         "+PrevDayPnL + "  lastThreeTrades "+lastThreeTrades);
							Print("AccountRealizedPL: "+AccountRealizedPL+" AccountUnrealizedPL: "+AccountUnrealizedPL);
							dayOverVar=true;
							
							switch (Position.MarketPosition)
									{
									     case MarketPosition.Flat:
									          break;

									     case MarketPosition.Long:
									               ExitLong();
									Print("Exiting LONG position as max loss is reached");
									          break;

									     case MarketPosition.Short:
									               ExitShort();
									Print("Exiting SHORT position as max loss is reached");
									
									          break;
									}
						}
						
				}
				
			else
				{//total realtime trades is zero
					//3pm trade
						//3pm trade
						if (((SystemPerformance.AllTrades.Count - PrevDayTradeCount)==0) && (toTime - 1505  == 0) &&  BarsInProgress == 0 && IsFirstTickOfBar)
								{
									SetStopLoss(CalculationMode.Ticks, 1);
							        SetProfitTarget("Long VWAP 1st", CalculationMode.Ticks, 2);
									EnterLong(2,  1, "Long VWAP 1st");
									
									Print("3PM trade taken-2");
									Print("SystemPerformance.AllTrades.Count "+SystemPerformance.AllTrades.Count);
									Print("PrevDayTradeCount "+PrevDayTradeCount);
								}
				
				}
				
				
				
			//cannot take settlement from priordayOHLC
				// can take high and low
      if (BarsInProgress == 0 &&
        IsFirstTickOfBar // for first tick
		  && isitRTH && !isitEarly //&& toTime>825
		  && !dayOverVar
      ) {
		  
		
	  Print("Current price " + Close[0] +  " Timestamp: " + Time[0] );
	

        	double VWAPValue = VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[0];
	        Print("Old VWAP " + VWAPValue.ToString());
	         vwPrice = Instrument.MasterInstrument.RoundToTickSize(VWAPValue);

    //    double VWAP1ago = VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[1];
	  //  double VWAP2ago = VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[2];
	    //double VWAP3ago = VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[3];
	    double avwap1=AVWAP2(BarsArray[0],AnchorFrom , new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[0];
 avwap1 = Instrument.MasterInstrument.RoundToTickSize(avwap1);
 Print("Anchored VWAP1 " + avwap1);

 double avwap2=AVWAP2(BarsArray[0],AnchorFrom2 , new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[0];
 avwap2 = Instrument.MasterInstrument.RoundToTickSize(avwap2);
 Print("Anchored VWAP2 " + avwap2);


	     
if (Position.MarketPosition == MarketPosition.Flat// || Position.Quantity==Convert.ToInt32(TradeSize)
	) {
       
		
			Print(" LongTradeFlag : " +LongTradeFlag);	
		  Print(" ShortTradeFlag :" +ShortTradeFlag );		
				

     	  
		  lATR=Instrument.MasterInstrument.RoundToTickSize(1.5*ATR(Closes[1], 14)[0]);
		 // Print("1 min ATR : "+ATR(Closes[1], 14)[0]);
			Print("SL ATR : "+lATR);
		  
		 if (entryOrder != null)
		 {
		 	CancelOrder(entryOrder);
			 entryOrder = null;
			 Print("old Order is cancelled");
		 }
	
		 
		 if (Close[1]>Close[2] && Close[2]>Close[3] && Close[3]>Close[4] && Close[4]>Close[5] && Close[5]>Close[6])
		 {
		 	ShortTradeFlag=false;
			 Short2Flag=false;
			  Print("Short Flags are reset because of 5");
		 }
		 
		  if (Close[1]<Close[2] && Close[2]<Close[3] && Close[3]<Close[4] && Close[4]<Close[5] && Close[5]<Close[6])
		 {
		 	LongTradeFlag=false;
			 Long2Flag=false;
			  Print("Long Flags are reset because of 5");
		 }
	
	
	//AVWAP order
		 
		 if(!lOrderName.EndsWith("1st"))
		 {
	Print("+++1+++");
	
	if ((toTime-(ToTime(AnchorFrom) / 100.0))==0)
	{
		AnchorFromClose=Close[0];
	}
	Print("AnchorFrom "+AnchorFrom);
	Print("curr "+DateTime.Parse("12:30 AM"));
	if (AnchorFrom != DateTime.Parse("12:30 AM"))
	{
		
	Print("AnchorFromClose "+AnchorFromClose);
		
	Print("High[HighestBar(High, 5) "+High[HighestBar(High, 10)]);
		
	Print("=== avwap1 "+avwap1);
		Print("Low[LowestBar(Low, 5)] "+Low[LowestBar(Low, 10)]);
		
		//  isAVWAP1DiffBand = !(highband > avwap1 && avwap1 > lowband);
		
		if ( LongTradeFlag== true )	 { 
		l1Entry =avwap1; //(isAVWAP1DiffBand?((avwap1-nextbandS)<=10? nextbandS: avwap1) : ((avwap1-lowband)<=10? lowband: avwap1));
		SetStopLoss(CalculationMode.Ticks, 4*lATR);
			EnterLongLimit(0, false, Convert.ToInt32(TradeSize/2), l1Entry , "Long VWAP 1st 1/2");
			EnterLongLimit(0, false, Convert.ToInt32(TradeSize/2), l1Entry , "Long VWAP 1st 2/2");
		//maybe below can be moved to on order execution!!!
			
       /*if (LetProfRun)
			SetProfitTarget("Long VWAP 1st", CalculationMode.Price, (isAVWAP1DiffBand? ((lowband-l1Entry)>=25? lowband: highband): (highband-l1Entry)>25? highband:nextbandL)); // can add math max 10
		 else
			SetProfitTarget("Long VWAP 1st",CalculationMode.Ticks, 80);*/
			SetProfitTarget("Long VWAP 1st 1/2", CalculationMode.Price, (l1Entry + lATR));
			SetProfitTarget("Long VWAP 1st 2/2", CalculationMode.Price, (l1Entry + lATR*2));
			
Print(" LOng Order 1 is set : " +l1Entry + " -----SL: "+ (l1Entry - lATR) + " ----PT: "+(l1Entry + lATR*2));
		
		}//long trade
	else if (ShortTradeFlag== true )
	{
		s1Entry =avwap1;// (isAVWAP1DiffBand?((nextbandL -avwap1)<=10? nextbandL: avwap1) : ((highband -avwap1)<=10? highband: avwap1));
		SetStopLoss(CalculationMode.Ticks, 4*lATR);
		EnterShortLimit(0, false, Convert.ToInt32(TradeSize/2), s1Entry, "Short VWAP 1st 1/2");
		EnterShortLimit(0, false, Convert.ToInt32(TradeSize/2), s1Entry, "Short VWAP 1st 2/2");
		//maybe below can be moved to on order execution!!!
		//SetStopLoss(CalculationMode.Ticks, 4*(s1Entry + lATR));
       /* if (LetProfRun)
			SetProfitTarget("Short VWAP 1st",  CalculationMode.Price, (isAVWAP1DiffBand? ((s1Entry-highband)>=25? highband: lowband): (s1Entry-lowband)>25? lowband:nextbandS)); // can add math max 10
		else
			SetProfitTarget("Short VWAP 1st",CalculationMode.Ticks, 80);*/
		SetProfitTarget("Short VWAP 1st 1/2",CalculationMode.Price, (s1Entry - lATR));
		SetProfitTarget("Short VWAP 1st 2/2",CalculationMode.Price, (s1Entry - lATR*2));
		Print(" Short Order 1  is set :" +s1Entry+ " ----SL: "+ (s1Entry + lATR) + " ----PT: "+(s1Entry - lATR*2));
	}
	else{
		//  Print(" in else " +entryOrder );
		//current price should be latr more than avwap
		///
		if (Time[0]>AnchorFrom)
		 	 if ((Close[0] -lATR) >avwap1  && Low[LowestBar(Low, 5)] >=avwap1 &&  Position.Quantity< Convert.ToInt32(TradeSize)+2)
		  {
		  	LongTradeFlag= true;
			   Print(" LongTradeFlag is set to true: " );			  
		  }
		  else if ((Close[0] +lATR) <avwap1  && High[HighestBar(High, 5)]<=avwap1 && Position.Quantity< Convert.ToInt32(TradeSize)+2)
		  {
			  ShortTradeFlag= true;
			   Print(" ShortTradeFlag is set to true: " );	
		  }
		  else{
		  	LongTradeFlag= false;
			  ShortTradeFlag= false;
			   Print(" All 1 are false" );	
			 Print("Needed for long at each close: Is avwap > "+(Close[0] -lATR));
			 Print("Needed for short at each close: Is avwap < "+(Close[0] +lATR));
		  }
		  
	}
		
	}
		
	}
	
Print("+++2+++");
		  if(!lOrderName.EndsWith("2nd"))
		 {
			 
	if (useVwap==false)
	{
	
	if ((toTime-(ToTime(AnchorFrom2) / 100.0))==0)
	{
		AnchorFromClose=Close[0];
	}
	
	if (AnchorFrom2 != DateTime.Parse("12:30 AM"))
	{
		
	Print("AnchorFromClose "+AnchorFromClose);
		
	Print("High[HighestBar(High, 5) "+High[HighestBar(High, 5)]);
		
	Print("=== avwap2 "+avwap2);
		Print("Low[LowestBar(Low, 5)] "+Low[LowestBar(Low, 5)]);
		
		//  isAVWAP2DiffBand = !(highband > avwap2 && avwap2 > lowband);
		
		if ( Long2Flag== true )	 { 
		
			l2Entry =avwap2;// (isAVWAP2DiffBand?((avwap2-nextbandS)<=10? nextbandS: avwap2) : ((avwap2-lowband)<=10? lowband: avwap2));
			SetStopLoss(CalculationMode.Ticks, 4*lATR);
			EnterLongLimit(0, false, Convert.ToInt32(TradeSize/2), l2Entry, "Long VWAP 2nd 1/2");
			EnterLongLimit(0, false, Convert.ToInt32(TradeSize/2), l2Entry, "Long VWAP 2nd 2/2");
		//maybe below can be moved to on order execution!!!\
			//SetStopLoss(CalculationMode.Ticks,4*( l2Entry - lATR));
			
       /*if (LetProfRun)
			SetProfitTarget("Long VWAP 1st", CalculationMode.Price, (isAVWAP1DiffBand? ((lowband-l1Entry)>=25? lowband: highband): (highband-l1Entry)>25? highband:nextbandL)); // can add math max 10
		 else
			SetProfitTarget("Long VWAP 1st",CalculationMode.Ticks, 80);*/
			SetProfitTarget("Long VWAP 2nd 1/2", CalculationMode.Price, (l2Entry + lATR));
			SetProfitTarget("Long VWAP 2nd 2/2", CalculationMode.Price, (l2Entry + lATR*2));
			
Print(" LOng Order 2 is set : " +l2Entry + " -----SL: "+ (l2Entry - lATR) + " ----PT: "+(l2Entry + lATR*2));
		}//long trade
	else if (Short2Flag== true )
	{
		s2Entry = avwap2;//(isAVWAP2DiffBand?((nextbandL -avwap2)<=10? nextbandL: avwap2) : ((highband -avwap2)<=10? highband: avwap2));
		SetStopLoss(CalculationMode.Ticks, 4*lATR);
		EnterShortLimit(0, false, Convert.ToInt32(TradeSize/2), s2Entry, "Short VWAP 2nd 1/2");
		EnterShortLimit(0, false, Convert.ToInt32(TradeSize/2), s2Entry, "Short VWAP 2nd 2/2");
		//maybe below can be moved to on order execution!!!
		//SetStopLoss(CalculationMode.Ticks, 4*(s2Entry + lATR));
       /* if (LetProfRun)
			SetProfitTarget("Short VWAP 1st",  CalculationMode.Price, (isAVWAP1DiffBand? ((s1Entry-highband)>=25? highband: lowband): (s1Entry-lowband)>25? lowband:nextbandS)); // can add math max 10
		else
			SetProfitTarget("Short VWAP 1st",CalculationMode.Ticks, 80);*/
		SetProfitTarget("Short VWAP 2nd 1/2",CalculationMode.Price, (s2Entry - lATR));
		SetProfitTarget("Short VWAP 2nd 2/2",CalculationMode.Price, (s2Entry - lATR*2));
		Print(" Short Order 2  is set :" +s2Entry+ " ----SL: "+ (s2Entry + lATR) + " ----PT: "+(s2Entry - lATR*2));}
	else{
		//  Print(" in else " +entryOrder );
		if (Time[0]>AnchorFrom2)
		 	 if ((Close[0] -lATR) >avwap2  && Low[LowestBar(Low, 5)] >=avwap2 &&  Position.Quantity< Convert.ToInt32(TradeSize)+2)
		  {
		  	Long2Flag= true;
			   Print(" Long2Flag is set to true: " );			  
		  }
		  else if ((Close[0] +lATR) <avwap2  && High[HighestBar(High, 5)]<=avwap2 && Position.Quantity< Convert.ToInt32(TradeSize)+2)
		  {
			  Short2Flag= true;
			   Print(" Short2Flag is set to true: " );	
		  }
		  else{
		  	Long2Flag= false;
			  Short2Flag= false;
			   Print(" All 2 are false" );	
			  Print("Needed for long at each close: Is avwap > "+(Close[0] -lATR));
			 Print("Needed for short at each close: Is avwap < "+(Close[0] +lATR));
		  }
		  
	}
		
	}
		
	}
	else // vwap flag is true
	{
		
	
		
	Print("VWAP flag is true ");
		
	Print("High[HighestBar(High, 10) "+High[HighestBar(High, 10)]);
		
	Print("=== vwPrice "+vwPrice);
		Print("Low[LowestBar(Low, 10)] "+Low[LowestBar(Low, 10)]);
		
		//  isVWAP2DiffBand = !(highband > vwPrice && vwPrice > lowband);
		
		if ( Long2Flag== true )	 { 
		
			l2Entry =vwPrice;// (isVWAPDiffBand?((vwPrice-nextbandS)<=10? nextbandS: vwPrice) : ((vwPrice-lowband)<=10? lowband: vwPrice));
			SetStopLoss(CalculationMode.Ticks, 4*lATR);
			EnterLongLimit(0, false, Convert.ToInt32(TradeSize/2), l2Entry, "Long VWAP 2nd 1/2");
			EnterLongLimit(0, false, Convert.ToInt32(TradeSize/2), l2Entry, "Long VWAP 2nd 2/2");
		//maybe below can be moved to on order execution!!!\
			//SetStopLoss(CalculationMode.Ticks,4*( l2Entry - lATR));
       /*if (LetProfRun)
			SetProfitTarget("Long VWAP 1st", CalculationMode.Price, (isAVWAP1DiffBand? ((lowband-l1Entry)>=25? lowband: highband): (highband-l1Entry)>25? highband:nextbandL)); // can add math max 10
		 else
			SetProfitTarget("Long VWAP 1st",CalculationMode.Ticks, 80);*/
			SetProfitTarget("Long VWAP 2nd 1/2", CalculationMode.Price, (l2Entry + lATR));
			SetProfitTarget("Long VWAP 2nd 2/2", CalculationMode.Price, (l2Entry + lATR*2));
			Print("Long VWAP 2nd  is set :" +l2Entry+ " ----SL: "+ (l2Entry - lATR) + " ----PT: "+(l2Entry + lATR*2));
			Print("lATR "+lATR);
		}//long trade
	else if (Short2Flag== true )
	{
		s2Entry = vwPrice;//(isVWAPDiffBand?((nextbandL -vwPrice)<=10? nextbandL: vwPrice) : ((highband -vwPrice)<=10? highband: vwPrice));
		SetStopLoss(CalculationMode.Ticks, 4*lATR);
		EnterShortLimit(0, false, Convert.ToInt32(TradeSize/2), s2Entry, "Short VWAP 2nd 1/2");
		EnterShortLimit(0, false, Convert.ToInt32(TradeSize/2), s2Entry, "Short VWAP 2nd 2/2");
		//maybe below can be moved to on order execution!!!
		//SetStopLoss(CalculationMode.Ticks,4*( s2Entry + lATR));
       /* if (LetProfRun)
			SetProfitTarget("Short VWAP 1st",  CalculationMode.Price, (isAVWAP1DiffBand? ((s1Entry-highband)>=25? highband: lowband): (s1Entry-lowband)>25? lowband:nextbandS)); // can add math max 10
		else
			SetProfitTarget("Short VWAP 1st",CalculationMode.Ticks, 80);*/
		SetProfitTarget("Short VWAP 2nd 1/2",CalculationMode.Price, (s2Entry - lATR));
		SetProfitTarget("Short VWAP 2nd 2/2",CalculationMode.Price, (s2Entry - lATR*2));
		Print(" Short Order 2  is set :" +s2Entry+ " ----SL: "+ (s2Entry + lATR) + " ----PT: "+(s2Entry - lATR*2));
	}
	else{
		//  Print(" in else " +entryOrder );
		
		 	 if ((Close[0] -lATR) >vwPrice  && Low[LowestBar(Low, 5)] >=vwPrice &&  Position.Quantity< Convert.ToInt32(TradeSize)+2)
		  {
		  	Long2Flag= true;
			   Print(" Long2Flag is set to true: " );			  
		  }
		  else if ((Close[0] +lATR) <vwPrice  && High[HighestBar(High, 5)]<=vwPrice && Position.Quantity< Convert.ToInt32(TradeSize)+2)
		  {
			  Short2Flag= true;
			   Print(" Short2Flag is set to true: " );	
		  }
		  else{
		  	Long2Flag= false;
			  Short2Flag= false;
			   Print(" All 2 are false" );	
			  Print("Needed for long at each close: Is avwap > "+(Close[0] -lATR));
			 Print("Needed for short at each close: Is avwap < "+(Close[0] +lATR));
		  }
		  
	}
		
	
	}
		 }

        } // for end if of flat

 if (Position.MarketPosition == MarketPosition.Long) {
	Print("Exiting Long: "+BE_Set);
	 
	 if (Close[0]>= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice + lATR) && !BE_Set)
	{
		SetStopLoss(CalculationMode.Price,   Position.AveragePrice);
				Print("New Stop Loss : "+ Position.AveragePrice);
		BE_Set=true;
	}
	else if (BE_Set)
	{
		SetStopLoss(CalculationMode.Price,   Position.AveragePrice);
				Print("New Stop Loss re-set: "+ Position.AveragePrice);
	}
		
		else
		Print("be is wrong Stop Loss : "+ Position.AveragePrice);
	
	/*if (Close[1] < (Close[2]-0.5) && Close[2] < (Close[3]-0.5) && (Time[0]-orderTime).TotalMinutes>3)
				{
					ExitLong();
				}*/
				
	
}

 else if (Position.MarketPosition == MarketPosition.Short) {
	 Print("Exiting Short: "+BE_Set);
	  if (Close[0]<= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice - lATR) && !BE_Set)
	{
		SetStopLoss(CalculationMode.Price, Position.AveragePrice);
				Print("New Stop Loss: "+  Position.AveragePrice);
		BE_Set=true;
		
	}
	else
		Print("be is wrong Stop Loss : "+ Position.AveragePrice);
	
	/*if ((Close[1]-0.5) > Close[2] && (Close[2]-0.5) > Close[3] && (Time[0]-orderTime).TotalMinutes>3)
				{
					ExitShort();
				}*/
	 
 }
		
    
      } // end of main if - dayisnotover etc etc znd first tick 

    } // for onbarupdate

	 protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError) {
      // Checks for all updates to entryOrder.
		  // One time only, as we transition from historical
  // Convert any old historical order object references to the live order submitted to the real-time account
  if (entryOrder != null && entryOrder.IsBacktestOrder && State == State.Realtime)
      entryOrder = GetRealtimeOrder(entryOrder);
		
		
      if (entryOrder == null && (order.Name.StartsWith("Long") || order.Name.StartsWith("Short"))) {
        // Assign entryOrder in OnOrderUpdate() to ensure the assignment occurs when expected.
        // This is more reliable than assigning Order objects in OnBarUpdate, as the assignment is not gauranteed to be complete if it is referenced immediately after submitting
        entryOrder = order;
      }

      if (entryOrder != null && (order.Name.StartsWith("Long") || order.Name.StartsWith("Short"))) {
        // Check if entryOrder is cancelled.
        if (order.OrderState == OrderState.Cancelled) {
          // Reset entryOrder back to null
          entryOrder = null;

        }
      }
    }
  
	protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time) {
      /* We advise monitoring OnExecution() to trigger submission of stop/target orders instead of OnOrderUpdate() since OnExecution() is called after OnOrderUpdate()
      which ensures your strategy has received the execution which is used for internal signal tracking.
      
      This first if-statement is in place to deal only with the long limit entry. */
      Print("OEU: order: " + execution.Order.Name);
		
		lOrderName=execution.Order.Name;
	 
	/*	orderTime = DateTime.Parse("11:59 PM");
			iCumVolume			= 0;
			iCumTypicalVolume	= 0;
		 if (execution.Order.OrderState != OrderState.PartFilled) {
            entryOrder = null;
          }
		*/ 
		 if (execution.Order.Name.StartsWith("Long") || execution.Order.Name.StartsWith("Short"))
		 {
			 
			 orderTime = execution.Order.Time;//Time[0];
			 BE_Set=false;
			 
		 	

  
if (execution.Order.Name.StartsWith("Long VWAP 1st"))
{LongTradeFlag= false;
	 ShortTradeFlag= false;
		isVWAPDiffBand = false;
		}
else  if (execution.Order.Name.StartsWith("Short VWAP 1st"))
{LongTradeFlag= false;
	 ShortTradeFlag= false;
		isVWAPDiffBand = false;
		}

else if (execution.Order.Name.StartsWith("Long VWAP 2nd"))
{Long2Flag=false;
		Short2Flag=false;
		}
else  if (execution.Order.Name.StartsWith("Short VWAP 2nd"))
{Long2Flag=false;
		Short2Flag=false;
		}		 
		 //avwap draw from order

		 // orderAVWAP	=  AnchoredVWAP(BarsArray[0], DateTime.Now);
		 	
		 
		 
		 }
		 
		 if (Position.Quantity==(int)TradeSize/2)
		 {
		 if (Position.MarketPosition == MarketPosition.Long) {
	Print("Exiting Long: "+BE_Set);
	 
	 if (Close[0]>= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice + lATR) && !BE_Set)
	{
		SetStopLoss(CalculationMode.Price,   Position.AveragePrice);
				Print("New Stop Loss : "+ Position.AveragePrice);
		BE_Set=true;
	}
	else if (BE_Set)
	{
		SetStopLoss(CalculationMode.Price,   Position.AveragePrice);
				Print("New Stop Loss re-set: "+ Position.AveragePrice);
	}
		
		else
		Print("be is wrong Stop Loss : "+ Position.AveragePrice);
	
	/*if (Close[1] < (Close[2]-0.5) && Close[2] < (Close[3]-0.5) && (Time[0]-orderTime).TotalMinutes>3)
				{
					ExitLong();
				}*/
				
	
}

 else if (Position.MarketPosition == MarketPosition.Short) {
	 Print("Exiting Short: "+BE_Set);
	  if (Close[0]<= Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice - lATR) && !BE_Set)
	{
		SetStopLoss(CalculationMode.Price, Position.AveragePrice);
				Print("New Stop Loss: "+  Position.AveragePrice);
		BE_Set=true;
		
	}
	else
		Print("be is wrong Stop Loss : "+ Position.AveragePrice);
	
	/*if ((Close[1]-0.5) > Close[2] && (Close[2]-0.5) > Close[3] && (Time[0]-orderTime).TotalMinutes>3)
				{
					ExitShort();
				}*/
	 
 }
	}
		
    }
  
    #region Properties

      [Range(1, double.MaxValue), NinjaScriptProperty]
      [Display(ResourceType = typeof (Custom.Resource), Name = "Trade size", GroupName = "Parameters", Order = 0)]
    public double TradeSize {
      get;
      set;
    }
		
	

[NinjaScriptProperty]
		//[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
[PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
		[Display(Name="Anchor VWAP from 1st", Order = 0, GroupName="Parameters")]
		public DateTime AnchorFrom
		{ get; set; }
		
		[PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
		[Display(Name="Anchor VWAP from 2nd", Order = 0, GroupName="Parameters")]
		public DateTime AnchorFrom2
		{ get; set; }
		
		
		[Display(Name="Use VWAP?", Order = 0, GroupName="Parameters")]
		public bool useVwap
		{ get; set; }

[Range(double.MinValue, double.MaxValue), NinjaScriptProperty]
    [Display(ResourceType = typeof (Custom.Resource), Name = "Max Loss", GroupName = "Parameters", Order = 0)]
    public double MaxLoss {
      get;
      set;
    }
	
		
		
    [XmlIgnore]
    public Series < double > VWAP_LINE {
      get {
        return Values[1];
      }
    }

[Browsable(false)]
		[XmlIgnore]
		public Series<double> TrendLine
		{
			get { return Values[0]; }
		}

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> HTF_TrendLine
		{
			get { return Values[1]; }
		}
		
    #endregion

#region Method-OnAccountItemUpdate

        protected override void OnAccountItemUpdate(Cbi.Account account, Cbi.AccountItem accountItem, double value)
        {
            // Updated Account P&L
            AccountRealizedPL = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
            AccountUnrealizedPL = account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
        }
		
	

#endregion
  }
}


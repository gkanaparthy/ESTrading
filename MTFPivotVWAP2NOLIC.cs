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
  public class MTFPivotVWAP2NOLIC: Strategy {
    private int entryBar;
   
    double prevClose;
    double prevHigh;
    double prevLow;

    double pLevel;
    double r1Level;
    double s1Level;
    double r2Level;
    double s2Level;
    double r3Level;
    double s3Level;
    double pr1midLevel;
    double r1r2midLevel;
    double r2r3midLevel;
    double ps1midLevel;
    double s1s2midLevel;
    double s2s3midLevel;
    //double startBuf;
    double lowband;
    double highband;
    double nextbandL;
    double nextbandS;
	double currentSlPrice;
	
	double VWAP_lowband;
	double VWAP_highband;
	double VWAP_nextbandL;
	double VWAP_nextbandS;
    //double stopEntha;
	  double stopNewCalc;
    double PrevDayPnL =0;
	double PrevDayTradeCount =0;
	

    double OrderDilowband;
    double OrderDihighband;
    double OrderDinextband;
    double PT1;
	  bool LongTradeFlag;
	   bool ShortTradeFlag;
	bool isVWAPDiffBand;
	double AccountRealizedPL;
	double AccountUnrealizedPL;
	
	
	bool touchflag ;
	bool freeflag ;
	bool notWithin1Point;

    private Order entryOrder = null; // This variable holds an object representing our entry order.
    private Order stopOrder = null; // This variable holds an object representing our stop loss order.
    private Order targetOrder = null; // This variable holds an object representing our profit target order.
	  private Order targetOrder1 = null; // This variable holds an object representing our profit target order.
	  private Order targetOrder2 = null; // This variable holds an object representing our profit target order.
	  private int lastThreeTrades 		= 0;  	// This variable holds our value for how profitable the last three trades were.
	  
	  private bool				dayOverVar=false;
	
		//private SessionIterator		sessionIterator;
	  private EMA ema1;
	  double lATR;

	  private TR trCurrent;
		private TR trAg;
		private VWAP1 vwap;
	
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
        Name = "MTFPivotVWAP2NOLIC";
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
        TradeSize = 1; //hasto be divisible by 3
        TimeInForce = TimeInForce.Gtc;
        ManualSettPrice = 1;
        ManualLowPrice = 1;
        ManualHighPrice = 1;
		  RTHorETH=true;
		  EMAperiod = 13;
	MaxLoss = -700;
	MTF											= 15;
				entryMode									= EntryMode.Aggressive;
	UseConservativeEntries						= false;
				UseWiderStops								= true;
				SRPercentThreshold							= 50;
				NearBand =true;
				
		  
		 
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
	
		AddDataSeries(Data.BarsPeriodType.Minute, MTF);
			

      } else if (State == State.DataLoaded) {

      
        //VWAPGK = new Series < double > (BarsArray[0]);
		  
		  
		  ofVwapETH	=  VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
		 		AddChartIndicator(ofVwapETH);
		
	trCurrent = TR(BarsArray[0]);
				trAg = TR(BarsArray[1]);
				Trend = new Series<double>(BarsArray[0]);
				aggregationPeriodTrend = new Series<double>(BarsArray[1]);
				price = new Series<double>(BarsArray[0]);
				absDiff = new Series<double>(BarsArray[0]);
				aggregationPeriodPrice = new Series<double>(BarsArray[1]);
				agPerAbsDiff = new Series<double>(BarsArray[1]);
				ZLevel = new Series<double>(BarsArray[0]);
				aggregationPeriodZLevel = new Series<double>(BarsArray[1]);
				myState = new Series<int>(BarsArray[0]);
				aggregationPeriodState = new Series<int>(BarsArray[1]);
				vwap = VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
				volumeSum = new Series<double>(BarsArray[0]);
				volumeVwapSum = new Series<double>(BarsArray[0]);
				volumeVwap2Sum = new Series<double>(BarsArray[0]);
				VWAP =  new Series<double>(BarsArray[0]);
				UpperBand = new Series<double>(BarsArray[0]);
				LowerBand = new Series<double>(BarsArray[0]);
				UpperBandFirst = new Series<double>(BarsArray[0]);
				LowerBandFirst = new Series<double>(BarsArray[0]);
				sessionIterator = new SessionIterator(Bars);
				zupData = new Series<double>(BarsArray[0]);
				zdnData = new Series<double>(BarsArray[0]);
		  
		

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
				
					if ((toTime - 1500  == 0 || toTime - 2100  == 0)  && (BarsInProgress == 0 && IsFirstTickOfBar))
			{
				Print("resetting flags");
				 LongTradeFlag= false;
	 			 ShortTradeFlag= false;
				isVWAPDiffBand = false;
				PrevDayPnL=SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
				PrevDayTradeCount=SystemPerformance.AllTrades.Count;
				dayOverVar=false;
				touchflag=false;
				freeflag=false;
				notWithin1Point=false;
				lastThreeTrades=0;
				
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
				
				if (RTHorETH==false)
					isitRTH=true;
				
			//liniting loss in a day
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
							lastThreeTrades=0;
						
					if ((
						(SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - PrevDayPnL)< MaxLoss 
						|| (AccountRealizedPL+AccountUnrealizedPL)< MaxLoss
						|| lastThreeTrades==-3
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
									Print("Exiting LONG position as 700 loss is reached");
									          break;

									     case MarketPosition.Short:
									               ExitShort();
									Print("Exiting SHORT position as 700 loss is reached");
									
									          break;
									}
						}
						
				}
				
			//	double VWAPValuenew = ofVwapETH.VWAP[0];
				
	
				
				
				
				
			//cannot take settlement from priordayOHLC
				// can take high and low
      if (BarsInProgress == 0 &&
        IsFirstTickOfBar // for first tick
		  && isitRTH && !isitEarly
		  && !dayOverVar
      ) {
		  
		 
					Print("============START====MTFPivotVWAP2NOLIC======"  + Time[0]);
	        	//	Print("The current VWAP new: " + VWAPValuenew);
				
	        
	//start of mtf changes
	
	double aggregationPeriodHigh = Highs[1][0];
				double aggregationPeriodLow = Lows[1][0];
				double aggregationPeriodClose = Closes[1][0];
				double aggregationPeriodOpen = Opens[1][0];
				
				int S_R_TickThreshold = 6; // Not used anymore
				double ShortTermATRFactor = 1;
				int ATRPeriod = 4;
				AverageType MAType = AverageType.HMA;
				int Channel_Length = 10;
				int Average_Length = 21;
				int currentAggregationPeriod = BarsPeriod.Value;

	//			Enforces that currentAggregationPeriod must be less than MTF and likely divisible
				int aggregationPeriodFactor = currentAggregationPeriod / MTF;

	//			Outside windows
				
				 targetTime = Time[0];
				
				if (ConvertLocalToESTTime)
					TimeZoneInfo.ConvertTime(Time[0], TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
				
				 toTime = ToTime(targetTime) / 100.0;
				
//				def timeframeOutWindow1 = (SecondsTillTime(skipWindowStart1) > 0 or SecondsTillTime(skipWindowEnd1) < 0);
//				def timeframeOutWindow2 = (SecondsTillTime(skipWindowStart2) > 0 or SecondsTillTime(skipWindowEnd2) < 0);
//				Returns the number of seconds till the specified time (24-hour clock notation) in the EST timezone.
							
				bool timeframeOutWindow1 = 0 - toTime > 0 || 700 - toTime < 0;
				bool timeframeOutWindow2 = 1455 - toTime > 0 || 2100 - toTime < 0;

				int StateUp = 1;
				int StateDn = 2;
				
				if (IsFirstTickOfBar)
				{
					playBuy = false; 
					playExitLong = false;
					playSell = false;
					playExitShort = false;
				}

	//			Current Period
				double varATR = 0;
			
				switch (MAType)
				{
					case AverageType.EMA:
					{
						varATR = (EMA(trCurrent, ATRPeriod)[0]);
						break;
					}
					
					case AverageType.HMA:
					{
						varATR = (HMA(trCurrent, ATRPeriod)[0]);
						break;
					}
					
					case AverageType.SMA:
					{
						varATR = (SMA(trCurrent, ATRPeriod)[0]);
						break;
					}
					
					case AverageType.WMA:
					{
						varATR = (WMA(trCurrent, ATRPeriod)[0]);
						break;
					}
				}
				
				double HL2 = (High[0] + Low[0]) / 2;
				
				double UP = HL2 + (ShortTermATRFactor * varATR);
				double DN = HL2 + (-ShortTermATRFactor * varATR);
				
				Trend[0] = Close[0] < Trend[1] ? UP : DN;
				double ltTrend = Trend[0];
			
//				Aggregation Period
				
				double aggregationPeriodATR = 0;
			
				switch (MAType)
				{
					case AverageType.EMA:
					{
						aggregationPeriodATR = (EMA(trAg, ATRPeriod)[0]);
						break;
					}
					
					case AverageType.HMA:
					{
						aggregationPeriodATR = (HMA(trAg, ATRPeriod)[0]);
						break;
					}
					
					case AverageType.SMA:
					{
						aggregationPeriodATR = (SMA(trAg, ATRPeriod)[0]);
						break;
					}
					
					case AverageType.WMA:
					{
						aggregationPeriodATR = (WMA(trAg, ATRPeriod)[0]);
						break;
					}
				}
				
				double agHL2 = (Highs[1][0] + Lows[1][0]) / 2;
				double aggregationPeriodUP = agHL2 + (ShortTermATRFactor * varATR);
				double aggregationPeriodDN = agHL2 + (-ShortTermATRFactor * varATR);
				
				aggregationPeriodTrend[0] = aggregationPeriodClose < aggregationPeriodTrend[1] ? aggregationPeriodUP : aggregationPeriodDN;
				
				int ZLength = 50;
				int ZLengthATR = 21;
				double ZDivider = 0.15;

				int bar = CurrentBars[0];  //check if we need this for mtf

//				#BarNumber for aggregated timeframe
				int apBar = bar * aggregationPeriodFactor;

				double ZATR = SMA(trCurrent, ZLengthATR)[0] * ShortTermATRFactor;
				price[0] = Close[0] + Low[0] + High[0];
				absDiff[0] = Math.Abs(price[0] - SMA(price, ZLength)[0]);
				double linDev = SMA(absDiff, ZLength)[0];
				
//				aggregation Period calculations
				double aggregationPeriodZATR = SMA(trAg, ZLengthATR)[0] * ShortTermATRFactor;
				aggregationPeriodPrice[0] = aggregationPeriodClose + aggregationPeriodLow + aggregationPeriodHigh;
				agPerAbsDiff[0] = Math.Abs(aggregationPeriodPrice[0] - SMA(aggregationPeriodPrice, ZLength)[0]);
				double aggregationPeriodLinDev = SMA(agPerAbsDiff, ZLength)[0];

				double agLinDev = aggregationPeriodLinDev;
				double agPrice = aggregationPeriodPrice[0];
				double agZATR = aggregationPeriodZATR;

				double ZAG = linDev == 0 ? 0 : (price[0] - SMA(price, ZLength)[0]) / linDev / ZDivider;

				ZLevel[0] = ZAG > 0 ? Math.Max(ZLevel[1], HL2 - ZATR) : Math.Min(ZLevel[1], HL2 + ZATR);

//				plot LTFZLevel = Zlevel;
//				plot zlPlot  = ZLevel;
//				aggregationPeriod calculations

				double aggregationPeriodZAG = aggregationPeriodLinDev == 0 ? 0 : (aggregationPeriodPrice[0] - SMA(aggregationPeriodPrice, ZLength)[0]) / aggregationPeriodLinDev / ZDivider;

				aggregationPeriodZLevel[0] = aggregationPeriodZAG > 0 ? Math.Max(aggregationPeriodZLevel[1], agHL2 - aggregationPeriodZATR) : Math.Min(aggregationPeriodZLevel[1], agHL2 + aggregationPeriodZATR);
		
//				Normal Period

				myState[0] = Close[0] > Trend[0] && Close[0] > ZLevel[0] ? StateUp : Close[0] < Trend[0] && Close[0] < ZLevel[0] ? StateDn : myState[1];

				double newState = myState[0] != myState[1] ? bar : 0;

//				Aggregation Period

				double agClose = aggregationPeriodClose;
				double agTrend = aggregationPeriodTrend[0];
//				#plot agZlevel = aggregationPeriodZLevel;

				int ZLevelBand = 3;

//				TODO: added (GetAggregationPeriod() / 1000)
				int secondsPassed = (int)targetTime.TimeOfDay.TotalSeconds + MTF * 60;
				int secondsSinceHTFClose = secondsPassed % MTF;
				int barsSinceHTFClose = secondsSinceHTFClose / (MTF * 60);
				bool canChangeHTFState = barsSinceHTFClose == 0;			

				switch (entryMode)
				{
					case EntryMode.Aggressive:
					    aggregationPeriodState[0] = aggregationPeriodClose > aggregationPeriodTrend[0] && aggregationPeriodClose > aggregationPeriodZLevel[0] ? StateUp :
		            	aggregationPeriodClose < aggregationPeriodTrend[0] && aggregationPeriodClose < aggregationPeriodZLevel[0] ? StateDn :
			            aggregationPeriodState[1];
						break;
		    		
					case EntryMode.Conservative:
						aggregationPeriodState[0] = aggregationPeriodClose > aggregationPeriodTrend[0] && aggregationPeriodClose > aggregationPeriodZLevel[0] ? StateUp :
			            aggregationPeriodClose < aggregationPeriodTrend[0] && aggregationPeriodClose < aggregationPeriodZLevel[0] ? StateDn :
						aggregationPeriodState[1];
						break;
		    
					case EntryMode.NonRepaintAggressive:
		    			aggregationPeriodState[0] = aggregationPeriodClose > aggregationPeriodTrend[0] && aggregationPeriodClose > aggregationPeriodZLevel[0] && canChangeHTFState ? StateUp :
		            	aggregationPeriodClose < aggregationPeriodTrend[0] && aggregationPeriodClose < aggregationPeriodZLevel[0] && canChangeHTFState ? StateDn :
		            	aggregationPeriodState[1];
						break;
		    
					case EntryMode.NonRepaintConservative:
		    			aggregationPeriodState[0] = aggregationPeriodClose > aggregationPeriodTrend[0] && aggregationPeriodClose > aggregationPeriodZLevel[0] && canChangeHTFState ? StateUp :
		            	aggregationPeriodClose < aggregationPeriodTrend[0] && aggregationPeriodClose < aggregationPeriodZLevel[0] && canChangeHTFState ? StateDn :
		            	aggregationPeriodState[1];
						break;
				}

				double aggregationPeriodNewState = aggregationPeriodState[0] != aggregationPeriodState[1] ? apBar : 0; // TODO check if bar causes issues with MTF

				int prevAgState = aggregationPeriodState[1];
				int highTFState = aggregationPeriodState[0];
				double highTFNewState = aggregationPeriodNewState;
				
				
					string line0 = myState[0] == 1 ? "LTF: Up\n" : "LTF: Down\n";
					string line1 = aggregationPeriodState[0] == 1 ? "HTF: Up\n" : "HTF: Down\n";
					string line2 = "Mode: " + entryMode;
					Draw.TextFixed(this, "myLabel", line0 + line1 + line2, TextPosition.TopLeft);
				

//				Normal Timeframe
//				plot TrendLine = if bar >= newState then ZLevel else Double.NaN;
				
					TrendLine[0] = ZLevel[0];
					PlotBrushes[0][0] = myState[0] == StateUp ? Brushes.Green : myState[0] == StateDn ? Brushes.Red : Brushes.Transparent;
				
//				TrendLine.AssignValueColor(if bar >= newState
//				                   then if State == StateUp then Color.CYAN
//			                       else if State == StateDn then Color.YELLOW
//		                           else Color.CURRENT
//		                           else Color.CURRENT);				

//				Aggregated Timeframe
//				plot aggregatedTimeframeTrendLine = if apBar >= aggregationPeriodNewState then ZLevel else Double.NaN;

//				plot HTFLevel = aggregationPeriodZLevel;

					HTF_TrendLine[0] = aggregationPeriodZLevel[0];
					PlotBrushes[1][0] = aggregationPeriodState[0] == StateUp ? Brushes.Green : aggregationPeriodState[0] == StateDn ? Brushes.Red : Brushes.Blue;
				
				bool isPeriodRolled = Bars.IsFirstBarOfSession;
				
				double typical = (High[0] + Low[0] + Close[0]) / 3;
				
				if (isPeriodRolled)
				{
				    volumeSum[0] = Volume[0];
				    volumeVwapSum[0] = Volume[0] * typical;
				    volumeVwap2Sum[0] = Volume[0] * Math.Pow(typical, 2);
					volumeSum[1] = 0;
				    volumeVwapSum[1] = 0;
				    volumeVwap2Sum[1] = 0;
				}
				else
				{
		    		volumeSum[0] = CurrentBars[0] > 1 ? volumeSum[1] + Volume[0] : Volume[0];
		    		volumeVwapSum[0] = CurrentBars[0] > 1 ? volumeVwapSum[1] + Volume[0] * typical : Volume[0] * typical;
		    		volumeVwap2Sum[0] = CurrentBars[0] > 1 ? volumeVwap2Sum[1] + Volume[0] * Math.Pow(typical, 2) : Volume[0] * Math.Pow(typical, 2);
				}
		
				double vwPrice = volumeVwapSum[0] / volumeSum[0];
				
				double deviation = Math.Sqrt(Math.Max(volumeVwap2Sum[0] / volumeSum[0] - Math.Pow(vwPrice, 2), 0));				

				VWAP[0] = vwPrice;
				UpperBand[0] = vwPrice + 2.0 * deviation;
				LowerBand[0] = vwPrice  - 2.0 * deviation;

				UpperBandFirst[0] = vwPrice + deviation;
				LowerBandFirst[0] = vwPrice - deviation;
				
//				Relation to ADD
		
				double relatedSecurityUpT = 2000;
				double relatedSecurityDownT = -2000;
				double relatedSecurityPrice = 0;
				
					relatedSecurityPrice = Closes[2][0];
				
				if (Bars.IsFirstBarOfSession)
  				{
    				sessionIterator.GetNextSession(Time[0], true);
					tradingDay = sessionIterator.ActualTradingDayExchange;
    				beginTime = sessionIterator.ActualSessionBegin;
    				endTime = sessionIterator.ActualSessionEnd;
 				}

				bool RTHTimeCondition = beginTime < Time[0] && endTime > Time[0];

//				if outside RTH, ignore this condition
				bool rsBuyCondition = RTHTimeCondition ? relatedSecurityPrice > relatedSecurityDownT : true;
				bool rsShortCondition =  RTHTimeCondition ? relatedSecurityPrice < relatedSecurityUpT : true;
				
					rsBuyCondition = true;
					rsShortCondition = true;
				
//				Buy/Sell Arrows

//				Price Bands to avoid entry
				double band = 0.0003 * Close[0];
				double priceBand = Close[0] * 0.0003;
				double twentyema = EMA(Closes[0], 21)[0];
				bool vwapCloseCondition = Close[0] < Math.Abs(VWAP[0] - priceBand) || Close[0] > Math.Abs(VWAP[0] + priceBand);
				bool priceNotInZLevelBand = Close[0] < Math.Abs(ZLevel[0] - priceBand) || Close[0] > Math.Abs(ZLevel[0] + priceBand);
				bool priceNotIn21EmaBand = Close[0] < Math.Abs(twentyema - priceBand) || Close[0] > Math.Abs(twentyema + priceBand);
				bool priceNotInAPZLevelBand = Close[0] < Math.Abs(aggregationPeriodZLevel[0] - priceBand) || Close[0] > Math.Abs(aggregationPeriodZLevel[0] + priceBand);
				bool priceNotInBand = priceNotInZLevelBand && priceNotInAPZLevelBand && priceNotIn21EmaBand;

				int Length = 20;

				zupData[0] = High[0] * (1 + 4 * (High[0] - Low[0]) / (High[0] + Low[0]));
				zdnData[0] = Low[0] * (1 - 4 * (High[0] - Low[0]) / (High[0] + Low[0]));
				
				double ZUP = SMA(zupData, Length)[0];
				double ZDN = SMA(zdnData, Length)[0];

				bool priceNotInZUP = Close[0] < Math.Abs(ZUP - priceBand) || Close[0] > Math.Abs(ZUP + priceBand);
				bool priceNotInZDN = Close[0] < Math.Abs(ZDN - priceBand) || Close[0] > Math.Abs(ZDN + priceBand);
				bool priceNotAtZoneExtreme = priceNotInZUP && priceNotInZDN;
				bool priceNotInCondition = UseConservativeEntries ? priceNotInBand && priceNotAtZoneExtreme : priceNotAtZoneExtreme;

				bool RTH_Only_Condition = RTHOnly ? RTHTimeCondition : true;

				bool vwapStdDevConditionBuyNew = Close[0] < UpperBand[1] - ((UpperBand[1] - UpperBandFirst[1]) * SRPercentThreshold / 100);
				bool vwapStdDevConditionShorNew = Close[0] > LowerBand[1] + ((LowerBandFirst[1] - LowerBand[1]) * SRPercentThreshold / 100);

				bool vwapStdDevConditionBuy = Close[0] + (S_R_TickThreshold * TickSize) < UpperBand[1];
				bool vwapStdDevConditionShort = Close[0] - (S_R_TickThreshold * TickSize) > LowerBand[1];

				bool vwapConditionBuy = Close[0]  < VWAP[1];
				bool vwapConditionShort = Close[0] > VWAP[1];

				bool vwapClosingThresholdConditionBuyOrShort = true; // (close >  VWAP[1] - (S_R_TickThreshold * TickSize())) OR (close <  VWAP[1] + (S_R_TickThreshold * TickSize()));

//				TODO check performance with this
				bool refBarCondition = true; // AbsValue(high - low) < 0.005 * close; #AbsValue(close - ZLevel) <= 4; # deviation * S_R_PercentThreshold/100; #refBarDistanceThreshold;
				bool refBarConditionCons = Math.Abs(Close[0] - ZLevel[0]) <= 16 * TickSize; // deviation * S_R_PercentThreshold/100;

				double tickToUse = TickSize > 0.8 ? 5 * TickSize :
									TickSize >= 0.2 && TickSize < 0.8 ? TickSize :
									TickSize < 0.2 && TickSize > 0.04 ? 3 * TickSize :
									10 * TickSize;

				bool ZLevelCloseConditionBuy = Open[0] < Close[0] ? Math.Abs(Close[0] - ZLevel[0]) > 4 * tickToUse && Math.Abs(Close[0] - aggregationPeriodZLevel[0]) > 4 * tickToUse : true;
				bool ZLevelCloseConditionShort = Open[0] > Close[0] ? Math.Abs(Close[0] - ZLevel[0]) > 4 * tickToUse && Math.Abs(Close[0] - aggregationPeriodZLevel[0]) > 4 * tickToUse : true;

				double secondsFromMidnight = targetTime.TimeOfDay.TotalSeconds;
				bool thirtyMinCondition = true; // secondsPassed % 1800 == 0;

				bool StateUpCondition = myState[0] == StateUp;
				bool StateDownCondition = myState[0] == StateDn;
				bool agStateUpCondition = aggregationPeriodState[0] == StateUp;
				bool agStateDnCondition = aggregationPeriodState[0] == StateDn;

				bool stateChangeToUpCondition =  (aggregationPeriodState[1] != StateUp) || (myState[1] != StateUp);
				bool stateChangeToDnCondition = (aggregationPeriodState[1] != StateDn) || (myState[1] != StateDn);

				bool buyCondition = false;
				bool sellCondition = false;

				bool ZModelBuyAgg = RTH_Only_Condition && refBarCondition && vwapClosingThresholdConditionBuyOrShort && vwapStdDevConditionBuyNew && timeframeOutWindow1 && timeframeOutWindow2 && StateUpCondition && agStateUpCondition && stateChangeToUpCondition && rsBuyCondition && priceNotInCondition;

				bool ZModelShortAgg = RTH_Only_Condition && refBarCondition && vwapClosingThresholdConditionBuyOrShort && vwapStdDevConditionShorNew && timeframeOutWindow1 && timeframeOutWindow2 & StateDownCondition && agStateDnCondition && stateChangeToDnCondition && rsShortCondition && priceNotInCondition;

				bool PModelBuy = StateUpCondition && agStateUpCondition && Low[0] <= ZLevel[0] && Close[0] > ZLevel[0] && Math.Abs(Close[0] - ZLevel[0]) <= 1 &&  timeframeOutWindow1 && timeframeOutWindow2 && Open[0] > ZLevel[0];				

				bool PModelShort = StateDownCondition && agStateDnCondition && High[0] >= ZLevel[0] && Close[0] < ZLevel[0] && Math.Abs(Close[0] - ZLevel[0]) <= 1 &&  timeframeOutWindow1 && timeframeOutWindow2 && Open[0] < ZLevel[0];

				bool ZModelBuyCons = RTH_Only_Condition && vwapConditionBuy && timeframeOutWindow1 && timeframeOutWindow2 && StateUpCondition && agStateUpCondition && stateChangeToUpCondition && refBarCondition;

				bool ZModelShortCons = RTH_Only_Condition && vwapConditionShort && timeframeOutWindow1 && timeframeOutWindow2 && StateDownCondition && agStateDnCondition && stateChangeToDnCondition && refBarCondition;

				bool ZLevelCrossModelBuy = vwapClosingThresholdConditionBuyOrShort && StateUpCondition && ZLevel[0] < aggregationPeriodZLevel[0] && Open[0] < ZLevel[0] && Close[0] > aggregationPeriodZLevel[0];
				bool ZLevelCrossModelShort = vwapClosingThresholdConditionBuyOrShort && StateDownCondition && ZLevel[0] > aggregationPeriodZLevel[0] && Open[0] > ZLevel[0] && Close[0] < aggregationPeriodZLevel[0];

				switch (entryMode)
				{
					case EntryMode.Aggressive:
				    	buyCondition = vwapCloseCondition && thirtyMinCondition && vwapClosingThresholdConditionBuyOrShort && vwapStdDevConditionBuyNew && timeframeOutWindow1 && timeframeOutWindow2 && StateUpCondition && thirtyMinCondition && agStateUpCondition && stateChangeToUpCondition && priceNotInCondition;
					    sellCondition =  vwapCloseCondition && vwapClosingThresholdConditionBuyOrShort && vwapStdDevConditionShorNew && timeframeOutWindow1 && timeframeOutWindow2 && StateDownCondition && agStateDnCondition && stateChangeToDnCondition && priceNotInCondition;
						break;				
					
					case EntryMode.Conservative:
				    	buyCondition = vwapCloseCondition && vwapConditionBuy && timeframeOutWindow1 && timeframeOutWindow2 &&  StateUpCondition && agStateUpCondition && stateChangeToUpCondition;
				    	sellCondition = vwapCloseCondition && vwapConditionShort && timeframeOutWindow1 && timeframeOutWindow2 && StateDownCondition && agStateDnCondition && stateChangeToDnCondition;
						break;
					
					case EntryMode.NonRepaintAggressive:
				    	buyCondition = ZModelBuyAgg; // or PModelBuy ;
				    	sellCondition = ZModelShortAgg; // or PModelShort;
						break;
					
					case EntryMode.NonRepaintConservative:
				    	buyCondition = ZModelBuyAgg && refBarConditionCons;
				    	sellCondition = ZModelShortAgg && refBarConditionCons;
						break;
						
					default:
						break;
				}
				
				if (buyCondition)
					Draw.ArrowUp(this, "BuySignalArrow " + CurrentBar, false, 0, 0.998 * ZLevel[0], Brushes.Cyan);

				if (sellCondition)
					Draw.ArrowDown(this, "SellSignalArrow " + CurrentBar, false, 0, 1.002 * ZLevel[0], Brushes.Yellow);
		
				BarBrushes[0] = CandleOutlineBrushes[0] = true && myState[0] == StateUp ? true ? Brushes.Green : Brushes.Green :
				                    true && myState[0] == StateDn ? true ? Brushes.Red : Brushes.Red :
									BarBrushes[0];
		
//				def shortExitCondition1 = (State == StateUp and State[1] <> StateUp) or  (aggregationPeriodState == StateUp and aggregationPeriodState[1] <> StateUp);
//				def buyExitCondition1 = (State == StateDn and State[1] <> StateDn) or (aggregationPeriodState == StateDn and aggregationPeriodState[1] <> StateDn);

//				Remove vwapCloseCondition
				double barWidth = Math.Abs(Open[0] - Close[0]);
				bool barWidthCondition = barWidth < 0.0015 * Close[0];  // bar is less than 0.15% of close
				bool widerStopBuyExit = UseWiderStops && barWidthCondition ? Low[0] > ZLevel[0] : true;
				bool widerStopShortExit = UseWiderStops && barWidthCondition ?  High[0] < ZLevel[0] : true;

				bool shortExitCondition1 = myState[0] == StateUp && widerStopBuyExit && vwapCloseCondition;
				bool buyExitCondition1 = myState[0] == StateDn && widerStopShortExit && vwapCloseCondition;

				
//				Long side trade
		/*		if (buyCondition)
				{
					EnterLong(TradeSize);
					
					if (!playBuy)
					{
						playBuy = true;
						Alert("ScalperMTFBuyalert", Priority.High, "Scalper MTF Buy alert", NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.Black, Brushes.Yellow);  
					}
				} */

//		 		Long side trade exit
				if (buyExitCondition1)
				{
					ExitLong();
					
					if (true)
					{
						if (!playExitLong)
						{
							playExitLong = true;
							//Alert("ScalperMTFLongExitalert", Priority.High, "Scalper MTF Long Exit alert", NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.Black, Brushes.Yellow);  
						}
					}
				}

//				Short side trade
			/*	if (sellCondition)
				{
					EnterShort(TradeSize);
					
					if (!playSell)
					{
						playSell = true;
						Alert("ScalperMTFShortalert", Priority.High, "Scalper MTF Short alert", NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.Black, Brushes.Yellow);  
					}
				}*/
					
//		 		Short side trade exit
				if (shortExitCondition1)
				{
					ExitShort();
					
					if (true)
					{
						if (!playExitShort)
						{
							playExitShort = true;
							//Alert("ScalperMTFShortExitalert", Priority.High, "Scalper MTF Short Exit alert", NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.Black, Brushes.Yellow);  
						}
					}
				}
				
	
	// end of mtf changes
	
	

        prevLow = PriorDayOHLC().PriorLow[0];
        prevHigh = PriorDayOHLC().PriorHigh[0];

        if (prevClose < 2) {
          prevClose = ManualSettPrice;
        }

        if (prevLow < 2 || prevHigh < 2) {
          prevLow = ManualLowPrice;
          prevHigh = ManualHighPrice;
        }

		
       

        if (prevClose < 2)
          return;

        pLevel = Instrument.MasterInstrument.RoundToTickSize((prevHigh + prevLow + prevClose) / 3);
        r1Level = Instrument.MasterInstrument.RoundToTickSize(pLevel * 2 - prevLow);
        s1Level = Instrument.MasterInstrument.RoundToTickSize(pLevel * 2 - prevHigh);
        r2Level = Instrument.MasterInstrument.RoundToTickSize(pLevel + (prevHigh - prevLow));
        s2Level = Instrument.MasterInstrument.RoundToTickSize(pLevel - (prevHigh - prevLow));
        r3Level = Instrument.MasterInstrument.RoundToTickSize(r1Level + (prevHigh - prevLow));
        s3Level = Instrument.MasterInstrument.RoundToTickSize(s1Level - (prevHigh - prevLow));
        pr1midLevel = Instrument.MasterInstrument.RoundToTickSize(pLevel + ((r1Level - pLevel) / 2));
		r1r2midLevel = Instrument.MasterInstrument.RoundToTickSize(r1Level + ((r2Level - r1Level) / 2));
        r2r3midLevel = Instrument.MasterInstrument.RoundToTickSize(r2Level + ((r3Level - r2Level) / 2));
        ps1midLevel = Instrument.MasterInstrument.RoundToTickSize(pLevel - ((pLevel - s1Level) / 2));
        s1s2midLevel = Instrument.MasterInstrument.RoundToTickSize(s1Level - ((s1Level - s2Level) / 2));
        s2s3midLevel = Instrument.MasterInstrument.RoundToTickSize(s2Level - ((s2Level - s3Level) / 2));
		
		
		

        Print("Current price " + Close[0] + " Pivot: " + pLevel + " Settlement " + prevClose + " Timestamp: " + Time[0] );
      //  Print("prevHigh price " + prevHigh + " prevLow: " + prevLow);

	        
	
			double VWAPValue = //OrderFlowVWAP(VWAPResolution.Standard, TradingHours.String2TradingHours("CME US Index Futures ETH"), VWAPStandardDeviations.Three, 1, 2, 3).VWAP[0];
//	VWAP1( Deviation1,Deviation2, Deviation3, true, true, true).Output[0];
VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[0];
	        Print("Old VWAP " + VWAPValue.ToString());
	         vwPrice = Instrument.MasterInstrument.RoundToTickSize(VWAPValue);

        double VWAP1ago = VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[1];
	    double VWAP2ago = VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[2];
	    double VWAP3ago = VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true).Output[3];
	    



if (!(High[1] >= VWAP1ago && Low[1] <= VWAP1ago) && !(High[2] >= VWAP2ago && Low[2] <= VWAP2ago) )// can we increase to 3 bars?
	freeflag=true;
else
	freeflag=false;
	     
if (Position.MarketPosition == MarketPosition.Flat) {
          //Print("Pos Flat");
          // check current price against OPEN

        if (Close[0] >= pLevel && Close[0] < pr1midLevel) {
            lowband = pLevel;
            highband = pr1midLevel;
            nextbandL = r1Level;
            nextbandS = ps1midLevel;
          } else if (Close[0] >= pr1midLevel && Close[0] < r1Level) {
            lowband = pr1midLevel;
            highband = r1Level;
            nextbandL = r1r2midLevel;
            nextbandS = pLevel;
          } else if (Close[0] >= r1Level && Close[0] < r1r2midLevel) {
            lowband = r1Level;
            highband = r1r2midLevel;
            nextbandL = r2Level;
            nextbandS = pr1midLevel;
          } else if (Close[0] >= r1r2midLevel && Close[0] < r2Level) {
            lowband = r1r2midLevel;
            highband = r2Level;
            nextbandL = r2r3midLevel;
            nextbandS = r1Level;
          } else if (Close[0] >= r2Level && Close[0] < r2r3midLevel) {
            lowband = r2Level;
            highband = r2r3midLevel;
            nextbandL = r3Level;
            nextbandS = r1r2midLevel;
          } else if (Close[0] >= r2r3midLevel && Close[0] < r3Level) {
            lowband = r2r3midLevel;
            highband = r3Level;
            nextbandL = r3Level;
            nextbandS = r2Level;
          }
          //lower side
          else if (Close[0] >= ps1midLevel && Close[0] < pLevel) {
            lowband = ps1midLevel;
            highband = pLevel;
            nextbandL = pr1midLevel;
            nextbandS = s1Level;
          } else if (Close[0] >= s1Level && Close[0] < ps1midLevel) {
            lowband = s1Level;
            highband = ps1midLevel;
            nextbandL = pLevel;
            nextbandS = s1s2midLevel;
          } else if (Close[0] >= s1s2midLevel && Close[0] < s1Level) {
            lowband = s1s2midLevel;
            highband = s1Level;
            nextbandL = ps1midLevel;
            nextbandS = s2Level;
          } else if (Close[0] >= s2Level && Close[0] < s1s2midLevel) {
            lowband = s2Level;
            highband = s1s2midLevel;
            nextbandL = s1Level;
            nextbandS = s2s3midLevel;
          } else if (Close[0] >= s2s3midLevel && Close[0] < s2Level) {
            lowband = s2s3midLevel;
            highband = s2Level;
            nextbandL = s1s2midLevel;
            nextbandS = s3Level;
          } else if (Close[0] >= s3Level && Close[0] < s2s3midLevel) {
            lowband = s3Level;
            highband = s2s3midLevel;
            nextbandL = s2Level;
            nextbandS = s3Level;
          } else {
            Print("outside of s3 and r3");
            return;

          }

//vwap band

if (VWAPValue >= pLevel && VWAPValue < pr1midLevel) {
            VWAP_lowband = pLevel;
            VWAP_highband = pr1midLevel;
	 		VWAP_nextbandL = r1Level;
            VWAP_nextbandS = ps1midLevel;
            
          } else if (VWAPValue >= pr1midLevel && VWAPValue < r1Level) {
            VWAP_lowband = pr1midLevel;
            VWAP_highband = r1Level;
            VWAP_nextbandL = r1r2midLevel;
            VWAP_nextbandS = pLevel;
	
          } else if (VWAPValue >= r1Level && VWAPValue < r1r2midLevel) {
            VWAP_lowband = r1Level;
            VWAP_highband = r1r2midLevel;
             VWAP_nextbandL = r2Level;
            VWAP_nextbandS = pr1midLevel;
	
          } else if (VWAPValue >= r1r2midLevel && VWAPValue < r2Level) {
            VWAP_lowband = r1r2midLevel;
            VWAP_highband = r2Level;
            VWAP_nextbandL = r2r3midLevel;
            VWAP_nextbandS = r1Level;
	
          } else if (VWAPValue >= r2Level && VWAPValue < r2r3midLevel) {
            VWAP_lowband = r2Level;
            VWAP_highband = r2r3midLevel;
             VWAP_nextbandL = r3Level;
            VWAP_nextbandS = r1r2midLevel;
	
          } else if (VWAPValue >= r2r3midLevel && VWAPValue < r3Level) {
            VWAP_lowband = r2r3midLevel;
            VWAP_highband = r3Level;
             VWAP_nextbandL = r3Level;
            VWAP_nextbandS = r2Level;
	
          }
          //lower side
          else if (VWAPValue >= ps1midLevel && VWAPValue < pLevel) {
            VWAP_lowband = ps1midLevel;
            VWAP_highband = pLevel;
	 		VWAP_nextbandL = pr1midLevel;
            VWAP_nextbandS = s1Level;
           
          } else if (VWAPValue >= s1Level && VWAPValue < ps1midLevel) {
            VWAP_lowband = s1Level;
            VWAP_highband = ps1midLevel;
            VWAP_nextbandL = pLevel;
            VWAP_nextbandS = s1s2midLevel;
	
          } else if (VWAPValue >= s1s2midLevel && VWAPValue < s1Level) {
            VWAP_lowband = s1s2midLevel;
            VWAP_highband = s1Level;
			VWAP_nextbandL = ps1midLevel;
            VWAP_nextbandS = s2Level;
           
          } else if (VWAPValue >= s2Level && VWAPValue < s1s2midLevel) {
            VWAP_lowband = s2Level;
            VWAP_highband = s1s2midLevel;
			VWAP_nextbandL = s1Level;
            VWAP_nextbandS = s2s3midLevel;
           
          } else if (VWAPValue >= s2s3midLevel && VWAPValue < s2Level) {
            VWAP_lowband = s2s3midLevel;
            VWAP_highband = s2Level;
            VWAP_nextbandL = s1s2midLevel;
            VWAP_nextbandS = s3Level;
	
          } else if (VWAPValue >= s3Level && VWAPValue < s2s3midLevel) {
            VWAP_lowband = s3Level;
            VWAP_highband = s2s3midLevel;
            VWAP_nextbandL = s2Level;
            VWAP_nextbandS = s3Level;
	
          } else {
            Print(" vwap outside of s3 and r3");
            return;

          }

        //  Print(" entryOrder***** " + entryOrder);
		  
       
		//	Print(" LongTradeFlag : " +LongTradeFlag);	
		//  Print(" ShortTradeFlag :" +ShortTradeFlag );	
				

         // bool isNeartoVWAP = High[3] >= VWAP3ago && Low[3] <= VWAP3ago;
		 // bool isNeartoVWAP = High[3] >= VWAPValue && Low[3] <= VWAPValue;
		  
		 // bool isVWAPDiffBand = !(highband - vwPrice > 0 && vwPrice - lowband  > 0);
		  isVWAPDiffBand = !(highband > vwPrice && vwPrice > lowband);
		  Print(" isVWAPDiffBand :::::::" +isVWAPDiffBand );	

       //06132022
		  
		  lATR=Instrument.MasterInstrument.RoundToTickSize(2*ATR(Closes[1], 14)[0]);
		 // Print("1 min ATR : "+ATR(Closes[1], 14)[0]);
			Print("SL ATR : "+lATR);
		  
		 if (entryOrder != null)
		 {
		 	CancelOrder(entryOrder);
			 entryOrder = null;
			 Print("old Order is cancelled");
		 }
		  
	if (vwPrice> nextbandL )	 
	{
	 
		
		Print("in big bang: close= "+Close[1]);
		Print("Highband >"+ highband);
		Print("High of prev"+High[1]);
		Print("min close is (should be atleast this)"+MIN(Close, 18)[2]);
		//if (entryOrder == null  && highband>High[1] && Close[1] > s3Level && Close[1] <=MIN(Close, 18)[2])
		if (entryOrder == null  && VWAP_nextbandS>High[1] && Close[1] > s3Level && Close[1] <=MIN(Close, 18)[2])
		  {
		  	//EnterShortStopMarket(2, true, Convert.ToInt32(TradeSize), vwPrice, "Short VWAP 1st");
			 EnterShort(2,  Convert.ToInt32(TradeSize), "Short VWAP 1st");
		
		SetStopLoss(CalculationMode.Price, highband + Instrument.MasterInstrument.TickSize);
        SetProfitTarget("Short VWAP 1st", CalculationMode.Price, nextbandS+Instrument.MasterInstrument.TickSize);
		Print(" Short Order is in :" +Close[0] +" SL: "+(highband + Instrument.MasterInstrument.TickSize) + " Profit target: "+nextbandS);		
			 currentSlPrice=highband + Instrument.MasterInstrument.TickSize;
		  }
		  	
		
	}
	
	else if (vwPrice < nextbandS)
	{
		
		Print("in big bang: close= "+Close[1]);
		Print("lowband <"+ lowband);
		Print("Low of prev"+Low[1]);
		Print("min close is (should be atleast this)"+ MAX(Close, 18)[2]);
		
//		if (entryOrder == null  && lowband<=Low[1]   && Close[1] < r3Level && Close[1] >= MAX(Close, 18)[2] )
		if (entryOrder == null  && VWAP_nextbandL<=Low[1]   && Close[1] < r3Level && Close[1] >= MAX(Close, 18)[2] )
			{
		  	//EnterShortStopMarket(2, true, Convert.ToInt32(TradeSize), vwPrice, "Short VWAP 1st");
			 EnterLong(2,  Convert.ToInt32(TradeSize), "Long VWAP 1st");
		
		SetStopLoss(CalculationMode.Price, lowband - Instrument.MasterInstrument.TickSize);
        SetProfitTarget("Long VWAP 1st", CalculationMode.Price, nextbandL-Instrument.MasterInstrument.TickSize);
		Print(" Long Order is in :" +Close[0] +" SL: "+(lowband - Instrument.MasterInstrument.TickSize) + " Profit target: "+nextbandL);		
			 currentSlPrice=lowband - Instrument.MasterInstrument.TickSize;
		  }
		  	
		
	}
        
	 else{ // its in same band	or in immediate next band 
		 if (!(toTime>700 && toTime <830))
		 {
		 //calc risk and reward
		 //determine direction
		 
		 if (( vwPrice - VWAP_lowband)>1 && (VWAP_highband - vwPrice)>1)
		 notWithin1Point=true;
		 else
		 notWithin1Point=false;
		 
		 Print("touchflag "+touchflag);
		 Print("freeflag "+freeflag);
		 Print("notWithin1Point "+notWithin1Point);
		 Print(VWAP_lowband);
		 Print(VWAP_highband);
		 Print(vwPrice);
		 
		 if (entryOrder != null)
		 {
		 	CancelOrder(entryOrder);
			 entryOrder = null;
		 }
		 
	
		if (toTime - 835  > 0  && toTime - 1500 <0)
		{
			
			if (High[1] >= VWAP1ago && Low[1] <= VWAP1ago)
				touchflag=true;
		}
		
    
		 if (touchflag && freeflag && notWithin1Point)
			 //based on direction take the trade
		 {
			 if (entryOrder != null)
		 {
		 	CancelOrder(entryOrder);
			 entryOrder = null;
			 
		 }
			 
		 	if ((VWAPValue-VWAP_lowband)< (VWAP_highband-VWAPValue))
			{
				if (Close[0]>VWAPValue)
				{
					EnterLongLimit(2, true, Convert.ToInt32(TradeSize), vwPrice, "Long VWAP 1st");
				}
				else
				{
					EnterLongStopMarket(2, true, Convert.ToInt32(TradeSize), vwPrice, "Long VWAP 1st");
				}
			 SetStopLoss(CalculationMode.Price, VWAP_lowband-Instrument.MasterInstrument.TickSize);
        	 //SetProfitTarget("Long VWAP 1st", CalculationMode.Price, VWAP_highband-Instrument.MasterInstrument.TickSize);
			//	SetProfitTarget("Long VWAP 1st", CalculationMode.Price, nextbandL-Instrument.MasterInstrument.TickSize);
				SetProfitTarget("Long VWAP 1st", CalculationMode.Price, NearBand==true? VWAP_highband-Instrument.MasterInstrument.TickSize: nextbandL-Instrument.MasterInstrument.TickSize);
				Print("Long order set for "+vwPrice+". SL ="+VWAP_lowband+". Profit= "+(NearBand==true? VWAP_highband-Instrument.MasterInstrument.TickSize: nextbandL-Instrument.MasterInstrument.TickSize));
				Print((vwPrice-VWAP_lowband)+" risk-reward "+ ((NearBand==true? VWAP_highband-Instrument.MasterInstrument.TickSize: nextbandL-Instrument.MasterInstrument.TickSize)-vwPrice));
			}
			else //vwap is high up near high band
			{
				if (Close[0]>VWAPValue)
				{
					EnterShortStopMarket(2, true, Convert.ToInt32(TradeSize), vwPrice, "Short VWAP 1st");
				}
				else
				{
					EnterShortLimit(2, true, Convert.ToInt32(TradeSize), vwPrice, "Short VWAP 1st");
				}
			 SetStopLoss(CalculationMode.Price, VWAP_highband+Instrument.MasterInstrument.TickSize);
        	 //SetProfitTarget("Short VWAP 1st", CalculationMode.Price, VWAP_lowband+Instrument.MasterInstrument.TickSize);
			//	SetProfitTarget("Short VWAP 1st", CalculationMode.Price, nextbandS+Instrument.MasterInstrument.TickSize);
				SetProfitTarget("Short VWAP 1st", CalculationMode.Price, NearBand==true? VWAP_lowband+Instrument.MasterInstrument.TickSize : nextbandS+Instrument.MasterInstrument.TickSize);
				Print("SHORT order set for "+vwPrice+". SL ="+VWAP_highband+". Profit= "+( NearBand==true? VWAP_lowband+Instrument.MasterInstrument.TickSize : nextbandS+Instrument.MasterInstrument.TickSize));
				Print((VWAP_highband-vwPrice)+" risk-reward "+ (vwPrice-( NearBand==true? VWAP_lowband+Instrument.MasterInstrument.TickSize : nextbandS+Instrument.MasterInstrument.TickSize)));
			}
		 }
		 
		 
		 } //end of if for not between 700 and 8 30
		 
		 else
		 { // for time betwen 7 and 8 30
						 if (entryOrder != null)
					 {
					 	CancelOrder(entryOrder);
						 entryOrder = null;
						 
					 }
					 isVWAPDiffBand =true;
		 	 if ( LongTradeFlag== true)	 { 
		
						//EnterLongLimit(0, false, Convert.ToInt32(TradeSize), vwPrice, "Long VWAP 1st");
						EnterLongStopMarket(2, true, Convert.ToInt32(TradeSize), vwPrice, "Long VWAP 1st");
						//maybe below can be moved to on order execution!!!
						SetStopLoss(CalculationMode.Price, vwPrice - lATR);
				        SetProfitTarget("Long VWAP 1st", CalculationMode.Price, Math.Min(vwPrice + lATR*6,vwPrice+50 ));
						Print(" LOng Order is set :" +vwPrice +" SL: "+lATR + " Profit target: "+Math.Min(vwPrice + lATR*6,vwPrice+20 ));
						
				 Print ("Bars.Instrument.MasterInstrument.Name "+Bars.Instrument.MasterInstrument.Name);
						OrderDilowband = lowband;
				            OrderDihighband = highband;
				            OrderDinextband = nextbandL;
		
					}//long trade
				else if (ShortTradeFlag== true)
				{
						//EnterShortLimit(0, false, Convert.ToInt32(TradeSize), vwPrice, "Short VWAP 1st");
						EnterShortStopMarket(2, true, Convert.ToInt32(TradeSize), vwPrice, "Short VWAP 1st");
						
						//maybe below can be moved to on order execution!!!
						SetStopLoss(CalculationMode.Price, vwPrice + lATR);
				        SetProfitTarget("Short VWAP 1st", CalculationMode.Price, Math.Max(vwPrice - lATR*6, vwPrice-50));
						Print(" Short Order is set :" +vwPrice +" SL: "+lATR + " Profit target: "+Math.Max(vwPrice - lATR*6, vwPrice-20));
						
						OrderDilowband = lowband;
				            OrderDihighband = highband;
				            OrderDinextband = nextbandS;
					}
				else{
					  Print(" in else " +entryOrder );
		
						  if (entryOrder == null && isVWAPDiffBand && Low[1]> lowband && lowband> vwPrice)
						  {
						  	ShortTradeFlag= true;
							   Print(" ShortTradeFlag is set to true: " );				  
						  }
						  else if (entryOrder == null && isVWAPDiffBand && vwPrice> highband && highband>High[1])
						  {
							  LongTradeFlag= true;
							   Print(" LongTradeFlag is set to true: " );
						  }
						  else{
						  	LongTradeFlag= false;
							  ShortTradeFlag= false;
							 isVWAPDiffBand = false;
							   Print(" All are false" );	
						  }
		  
				}
				
	}// end of 7-8 30
	
	
	}// end of continuation band check
	


        } // for end if of flat
		 
    
      } // end of main if - dayisnotover etc etc znd first tick 

    } // for onbarupdate

	 protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError) {
      // Checks for all updates to entryOrder.
		  // One time only, as we transition from historical
  // Convert any old historical order object references to the live order submitted to the real-time account
  if (entryOrder != null && entryOrder.IsBacktestOrder && State == State.Realtime)
      entryOrder = GetRealtimeOrder(entryOrder);
		
		
      if (entryOrder == null && (order.Name == "Long VWAP 1st" || order.Name == "Short VWAP 1st" || order.Name == "Long VWAP 2nd" || order.Name == "Short VWAP 2nd"  || order.Name == "Long VWAP 3rd" || order.Name == "Short VWAP 3rd")) {
        // Assign entryOrder in OnOrderUpdate() to ensure the assignment occurs when expected.
        // This is more reliable than assigning Order objects in OnBarUpdate, as the assignment is not gauranteed to be complete if it is referenced immediately after submitting
        entryOrder = order;
      }

      if (entryOrder != null && (order.Name == "Long VWAP 1st" || order.Name == "Short VWAP 1st" || order.Name == "Long VWAP 2nd" || order.Name == "Short VWAP 2nd"  || order.Name == "Long VWAP 3rd" || order.Name == "Short VWAP 3rd")) {
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
		
		
	 LongTradeFlag= false;
	 ShortTradeFlag= false;
		isVWAPDiffBand = false;
		 if (execution.Order.OrderState != OrderState.PartFilled) {
            entryOrder = null;
          }
		 
		 if (execution.Order.Name =="Long VWAP 1st" || execution.Order.Name=="Short VWAP 1st")
		 {
		 	  if (Close[0] >= pLevel && Close[0] < pr1midLevel) {
            lowband = pLevel;
            highband = pr1midLevel;
            nextbandL = r1Level;
            nextbandS = ps1midLevel;
          } else if (Close[0] >= pr1midLevel && Close[0] < r1Level) {
            lowband = pr1midLevel;
            highband = r1Level;
            nextbandL = r1r2midLevel;
            nextbandS = pLevel;
          } else if (Close[0] >= r1Level && Close[0] < r1r2midLevel) {
            lowband = r1Level;
            highband = r1r2midLevel;
            nextbandL = r2Level;
            nextbandS = pr1midLevel;
          } else if (Close[0] >= r1r2midLevel && Close[0] < r2Level) {
            lowband = r1r2midLevel;
            highband = r2Level;
            nextbandL = r2r3midLevel;
            nextbandS = ps1midLevel;
          } else if (Close[0] >= r2Level && Close[0] < r2r3midLevel) {
            lowband = r2Level;
            highband = r2r3midLevel;
            nextbandL = r3Level;
            nextbandS = ps1midLevel;
          } else if (Close[0] >= r2r3midLevel && Close[0] < r3Level) {
            lowband = r2r3midLevel;
            highband = r3Level;
            nextbandL = r3Level;
            nextbandS = r1r2midLevel;
          }
          //lower side
          else if (Close[0] >= ps1midLevel && Close[0] < pLevel) {
            lowband = ps1midLevel;
            highband = pLevel;
            nextbandL = pr1midLevel;
            nextbandS = s1Level;
          } else if (Close[0] >= s1Level && Close[0] < ps1midLevel) {
            lowband = s1Level;
            highband = ps1midLevel;
            nextbandL = pLevel;
            nextbandS = s1s2midLevel;
          } else if (Close[0] >= s1s2midLevel && Close[0] < s1Level) {
            lowband = s1s2midLevel;
            highband = s1Level;
            nextbandL = ps1midLevel;
            nextbandS = s2Level;
          } else if (Close[0] >= s2Level && Close[0] < s1s2midLevel) {
            lowband = s2Level;
            highband = s1s2midLevel;
            nextbandL = s1Level;
            nextbandS = s2s3midLevel;
          } else if (Close[0] >= s2s3midLevel && Close[0] < s2Level) {
            lowband = s2s3midLevel;
            highband = s2Level;
            nextbandL = s1s2midLevel;
            nextbandS = s3Level;
          } else if (Close[0] >= s3Level && Close[0] < s2s3midLevel) {
            lowband = s3Level;
            highband = s2s3midLevel;
            nextbandL = s2Level;
            nextbandS = s3Level;
          } else {
            Print("outside of s3 and r3 - shouldnt be ");
            return;

          }

 if (execution.Order.Name =="Long VWAP 1st")
{
	OrderDilowband = lowband;
            OrderDihighband = highband;
            OrderDinextband = nextbandL;
}
else  if (execution.Order.Name=="Short VWAP 1st")
{
	
		OrderDilowband = lowband;
            OrderDihighband = highband;
            OrderDinextband = nextbandS;
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
	
[Range(1, int.MaxValue), NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name = "MTF", GroupName = "Parameters", Order = 1)]
		public int MTF
		{ get; set; }
		
		[Display(Name="Entry Mode", GroupName = "Parameters", Description="Choose a Moving Average Type.")]
		public EntryMode entryMode 
		{
			get;
			set;
		}
	
	[NinjaScriptProperty]
		[Display(Name="Only RTH?", Order = 5, GroupName="Parameters")]
		public bool RTHorETH
		{ get; set; }

    [Range(1, double.MaxValue), NinjaScriptProperty]
    [Display(ResourceType = typeof (Custom.Resource), Name = "Previous day Settlement", GroupName = "Parameters", Order = 0)]
    public double ManualSettPrice {
      get;
      set;
    }
    [Range(1, double.MaxValue), NinjaScriptProperty]
    [Display(ResourceType = typeof (Custom.Resource), Name = "Previous day Low", GroupName = "Parameters", Order = 0)]
    public double ManualLowPrice {
      get;
      set;
    }
    [Range(1, double.MaxValue), NinjaScriptProperty]
    [Display(ResourceType = typeof (Custom.Resource), Name = "Previous day High", GroupName = "Parameters", Order = 0)]
    public double ManualHighPrice {
      get;
      set;
    }
[Range(double.MinValue, double.MaxValue), NinjaScriptProperty]
    [Display(ResourceType = typeof (Custom.Resource), Name = "Max Loss", GroupName = "Parameters", Order = 0)]
    public double MaxLoss {
      get;
      set;
    }
	[Range(1, int.MaxValue), NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name = "EMA Period", GroupName = "Parameters", Order = 0)]
		public int EMAperiod
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Use Conservative Entries", Order = 7, GroupName="Parameters")]
		public bool UseConservativeEntries
		{ get; set; }
		[NinjaScriptProperty]
		[Display(Name="Use Wider Stops", Order = 3, GroupName="Parameters")]
		public bool UseWiderStops
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Take profits at Near band", Order = 9, GroupName="Parameters")]
		public bool NearBand
		{ get; set; }
		
			[Range(1, int.MaxValue), NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name = "S R Percent Threshold", GroupName = "Parameters", Order = 4)]
		public int SRPercentThreshold
		{ get; set; }
										
		[NinjaScriptProperty]
		[Display(Name="RTH Only", Order = 5, GroupName="Parameters")]
		public bool RTHOnly
		{ get; set; }
		
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


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
  public class PivotBounceDropTrail: Strategy {
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
	double vwPrice;
	

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
	DateTime  orderTime;
	double	iCumVolume			= 0;
		double	iCumTypicalVolume	= 0;
	double lBarsSinceOrderVWAPTouch=0;
			DateTime	lTouchVWAPTime ;
				

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
		private AnchoredVWAP orderAVWAP;
	 // private Series < double > VWAPGK;
	  
	  private bool ConvertLocalToESTTime						= true;
				private double WindowStart1						= 905;
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
        Name = "PivotBounceDropTrail";
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
	MaxLoss = -500;
	MaxProfit=750;
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
		 		//AddChartIndicator(ofVwapETH);
		

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
				touchflag=false;
				freeflag=false;
				notWithin1Point=false;
				lastThreeTrades=0;
				//orderTime=null;
				
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
						//|| lastThreeTrades==-3
						||(SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - PrevDayPnL)> MaxProfit 
						|| (AccountRealizedPL+AccountUnrealizedPL)> MaxProfit
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
		 // && isitRTH && !isitEarly && toTime>900
		  && !dayOverVar
      ) {
		  
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

		
		  
		 if (entryOrder != null)
		 {
		 	CancelOrder(entryOrder);
			 entryOrder = null;
			 Print("old Order is cancelled");
		 }

	
		 
if ( (!(High[1] >= lowband && Low[1] <= lowband) && (High[2] >= lowband && Low[2] <= lowband))
|| (!(High[1] >= highband && Low[1] <= highband) && (High[2] >= highband && Low[2] <= highband))
	)
	freeflag=true;
else
	freeflag=false;
		
		
		  	bool isNeartoAPivotL = (High[2] > lowband && Low[2] < lowband);
 			bool isNeartoAPivotH = (High[2] > highband && Low[2] < highband);

Print("isNeartoAPivotL: " +isNeartoAPivotL);
Print("isNeartoAPivotH: " +isNeartoAPivotH);
		
		 if (entryOrder == null && Low[1] > lowband && isNeartoAPivotL && Close[0]> lowband  && (Close[0] - (lowband-1))<20) { // && isVWAPDiffBand
			 EnterLong(2, Convert.ToInt32(TradeSize ), "Long VWAP 1st");
			 //&&!( toTime>830 && toTime<930)
			 OrderDilowband = lowband;
				            OrderDihighband = highband;
				            OrderDinextband = nextbandL;
			 
			 SetStopLoss(CalculationMode.Ticks, 4*(Close[0] - (lowband-1)));
        SetProfitTarget("Long VWAP 1st",  CalculationMode.Ticks, (Close[0] - (lowband-1))< 5? 12*(Close[0] - (lowband-1)):((Close[0] - (lowband-1))>10)? 4*(Close[0] - (lowband-1)):8*(Close[0] - (lowband-1))); // can add math max 10   vwPrice + lATR*3
		//Print(" LOng Order is set :" +Close[0] );
		//	 Print(" LONG Order is set :" +Close[0] +" SL: "+(vwPrice - lATR) + " Profit target: "+ Math.Min(Close[0]+10, Math.Max(OrderDihighband-Instrument.MasterInstrument.TickSize, Close[0]+5)));		
		Print("lowband: "+lowband);
			 Print("StopLoss ticks: "+4*(Close[0] - (lowband-1)));
		
		 }
		 else  if (entryOrder == null && High[1] < highband && isNeartoAPivotH && Close[0]< highband    && ((highband+1-Close[0]) <20)) { // && isVWAPDiffBand
			 EnterShort(2, Convert.ToInt32(TradeSize ), "Short VWAP 1st");
			 //&&!( toTime>830 && toTime<930)
			 OrderDilowband = lowband;
				            OrderDihighband = highband;
				            OrderDinextband = nextbandS;
			 SetStopLoss(CalculationMode.Price, 4*(highband+1-Close[0]));
        SetProfitTarget("Short VWAP 1st", CalculationMode.Ticks, (highband+1-Close[0])<5? 12*(highband+1-Close[0]): ((highband+1-Close[0])>10)? 4*(highband+1-Close[0]): 8*(highband+1-Close[0]));//vwPrice - lATR*3
		// Print(" SHORT Order is set :" +Close[0] +" SL: "+(vwPrice + lATR) + " Profit target: "+Math.Max(Close[0]-10,  Math.Min(OrderDilowband+Instrument.MasterInstrument.TickSize, Close[0]-5)));		
		Print("highband: "+highband);
			  Print("StopLoss ticks: "+4*(highband+1-Close[0]));
			 }
		 else 
		 {
		 	Print("NO Long or Short yet");
		 }
		
	
        } // for end if of flat

		 
    
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
		
		
	 LongTradeFlag= false;
	 ShortTradeFlag= false;
		isVWAPDiffBand = false;
		orderTime = DateTime.Parse("11:59 PM");
			iCumVolume			= 0;
			iCumTypicalVolume	= 0;
		 if (execution.Order.OrderState != OrderState.PartFilled) {
            entryOrder = null;
          }
		 
		 if (execution.Order.Name.StartsWith("Long") || execution.Order.Name.StartsWith("Short"))
		 {
			 
			 orderTime = execution.Order.Time;//Time[0];
			 
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

 if (execution.Order.Name.StartsWith("Long"))
{
	OrderDilowband = lowband;
            OrderDihighband = highband;
            OrderDinextband = nextbandL;
}
else  if (execution.Order.Name.StartsWith("Short"))
{
	
		OrderDilowband = lowband;
            OrderDihighband = highband;
            OrderDinextband = nextbandS;
}
		 
		 //avwap draw from order

		 // orderAVWAP	=  AnchoredVWAP(BarsArray[0], DateTime.Now);
		 	
		 
		 
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

[Range(double.MinValue, double.MaxValue), NinjaScriptProperty]
    [Display(ResourceType = typeof (Custom.Resource), Name = "Max Profit", GroupName = "Parameters", Order = 0)]
    public double MaxProfit {
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


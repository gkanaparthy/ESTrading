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
  public class VWAPUltaScalpRR: Strategy {
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
    //double stopEntha;
	  double stopNewCalc;
    //	double pbATR;

    double OrderDilowband;
    double OrderDihighband;
    double OrderDinextband;
    double PT1;
	  bool LongTradeFlag;
	   bool ShortTradeFlag;
	bool isVWAPDiffBand;

    private Order entryOrder = null; // This variable holds an object representing our entry order.
    private Order stopOrder = null; // This variable holds an object representing our stop loss order.
    private Order targetOrder = null; // This variable holds an object representing our profit target order.
	  private Order targetOrder1 = null; // This variable holds an object representing our profit target order.
	  private Order targetOrder2 = null; // This variable holds an object representing our profit target order.
	  private int lastThreeTrades 		= 0;  	// This variable holds our value for how profitable the last three trades were.
	  
	  private bool				exitOnCloseWait;
		//private SessionIterator		sessionIterator;
	  private EMA ema1;
	  double lATR;

	  
   
	  
	  private OrderFlowVWAP	ofVwapETH;
	 // private Series < double > VWAPGK;
	  
	  private bool ConvertLocalToESTTime						= true;
				private double WindowStart1						= 830;
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
        Name = "VWAPUltaScalpRR";
        Calculate = Calculate.OnEachTick; //OnBarClose;
        EntriesPerDirection = 3;
        EntryHandling = EntryHandling.UniqueEntries;
        IsExitOnSessionCloseStrategy = true;
        ExitOnSessionCloseSeconds = 2730;
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
		  RTHorETH=false;
		  EMAperiod = 13;
		  
		 
		  AddPlot(new Stroke(Brushes.Black, DashStyleHelper.Dash, 2, 50), PlotStyle.Line, "Plot1");
		  AddPlot(new Stroke(Brushes.Black, DashStyleHelper.Dash, 2, 50), PlotStyle.Line, "Plot2");
		   AddPlot(new Stroke(Brushes.DeepPink, DashStyleHelper.Dash, 2, 50), PlotStyle.Line, "Plot3");
		   AddPlot(new Stroke(Brushes.DeepPink, DashStyleHelper.Solid, 2, 50), PlotStyle.Line, "Plot4");
		  
		  

        //AddPlot(Brushes.Green, "VWAP");
        //	Plots[0].Width = 2;	
      } else if (State == State.Configure) {

        AddDataSeries(Data.BarsPeriodType.Minute, 1);
        //SetStopLoss(CalculationMode.Ticks, 10);

      } else if (State == State.DataLoaded) {

      
        //VWAPGK = new Series < double > (BarsArray[0]);
		  
		  
		  ofVwapETH	= OrderFlowVWAP(VWAPResolution.Standard, TradingHours.String2TradingHours("CME US Index Futures ETH"), VWAPStandardDeviations.Three, 1, 2, 3);
		 		AddChartIndicator(ofVwapETH);
		//  sessionIterator	= new SessionIterator(Bars);
		  
		//  ema1 = EMA(EMAperiod);
		  //ema1.Plots[0].Brush = Brushes.Goldenrod;
				
			//	AddChartIndicator(ema1);
		  
		

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
				
					if (toTime - 2100  == 0)
			{
				Print("resetting flags");
				 LongTradeFlag= false;
	 			 ShortTradeFlag= false;
				isVWAPDiffBand = false;
			}
				
				bool isitEarly = toTime - 1500 >= 0 && toTime - 2100  < 0;
				
				if(isitEarly)
					return;
				
				bool isitRTH = toTime - WindowStart1 > 0 && toTime - WindowEnd1  < 0;
				
				if (RTHorETH==false)
					isitRTH=true;
				
				//if (State == State.Historical)
				//return;
				//sessionIterator.GetNextSession(Time[0], true);

			// if after the exit on close, prevent new orders until the new session
		//	if (Time[0] >= sessionIterator.ActualSessionEnd.AddSeconds(-ExitOnSessionCloseSeconds) && Time[0] <= sessionIterator.ActualSessionEnd)
			//{
			//	exitOnCloseWait = true;
			//}

			// an exit on close occurred in the previous session, reset for a new entry on the first bar of a new session
		//	if (exitOnCloseWait && Bars.IsFirstBarOfSession)
		//	{
		//		exitOnCloseWait = false;
		//	}
				
				//if (Bars.IsFirstBarOfSession && IsFirstTickOfBar)
			
				
				double VWAPValuenew = ofVwapETH.VWAP[0];
				
				
				
				
				
				
			//cannot take settlement from priordayOHLC
				// can take high and low
      if (BarsInProgress == 0 &&
        IsFirstTickOfBar // for first tick
		  && isitRTH 
		  //&& !exitOnCloseWait
      ) {
		  
		 
					Print("============START====VWAPUltaScalpRR======"  + Time[0]);
	        		Print("The current VWAP new: " + VWAPValuenew);
				
	        

        prevLow = PriorDayOHLC().PriorLow[0];
        prevHigh = PriorDayOHLC().PriorHigh[0];

        if (prevClose < 2) {
          prevClose = ManualSettPrice;
        }

        if (prevLow < 2 || prevHigh < 2) {
          prevLow = ManualLowPrice;
          prevHigh = ManualHighPrice;
        }

		
       // Print("******PREV CLOSE***** "+PriorDayOHLC().PriorClose[0]);
        Print("******PREV HIGH***** "+prevHigh);
        Print("******PREV LOW***** "+prevLow);
		Print("******PREV Settlement***** "+prevClose);

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

	        
	
			double VWAPValue = OrderFlowVWAP(VWAPResolution.Standard, TradingHours.String2TradingHours("CME US Index Futures ETH"), VWAPStandardDeviations.Three, 1, 2, 3).VWAP[0];
	       // Print("Old VWAP " + VWAPValue.ToString());
	        double vwPrice = Instrument.MasterInstrument.RoundToTickSize(VWAPValue);
	        //VWAPGK[0] = vwPrice;
	       // Print("VWAP price " + vwPrice + " at time "+targetTime);
		
		double VWAP1ago = OrderFlowVWAP( VWAPResolution.Standard, TradingHours.String2TradingHours("CME US Index Futures ETH"), VWAPStandardDeviations.Three, 1, 2, 3).VWAP[1];
	    double VWAP2ago = OrderFlowVWAP( VWAPResolution.Standard, TradingHours.String2TradingHours("CME US Index Futures ETH"), VWAPStandardDeviations.Three, 1, 2, 3).VWAP[2];
	    double VWAP3ago = OrderFlowVWAP(VWAPResolution.Standard, TradingHours.String2TradingHours("CME US Index Futures ETH"), VWAPStandardDeviations.Three, 1, 2, 3).VWAP[3];
	          
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
            Print("outside of s3 and r3");
            return;

          }

        //  Print(" entryOrder***** " + entryOrder);
		  
       
			Print(" LongTradeFlag : " +LongTradeFlag);	
		  Print(" ShortTradeFlag :" +ShortTradeFlag );	
				

         // bool isNeartoVWAP = High[3] >= VWAP3ago && Low[3] <= VWAP3ago;
		 // bool isNeartoVWAP = High[3] >= VWAPValue && Low[3] <= VWAPValue;
		  
		 // bool isVWAPDiffBand = !(highband - vwPrice > 0 && vwPrice - lowband  > 0);
		  isVWAPDiffBand = !(highband > vwPrice && vwPrice > lowband);
		  Print(" isVWAPDiffBand :::::::" +isVWAPDiffBand );	

       //06132022
		  
		  lATR=2*Instrument.MasterInstrument.RoundToTickSize(ATR(Closes[1], 14)[0]);
		  Print("1 min ATR : "+ATR(Closes[1], 14)[0]);
			Print("SL ATR : "+lATR);
		  
		 if (entryOrder != null)
		 {
		 	CancelOrder(entryOrder);
			 entryOrder = null;
			 
		 }
		  
	if ( LongTradeFlag== true)	 { 
		
		//EnterLongLimit(0, false, Convert.ToInt32(TradeSize), vwPrice, "Long VWAP 1st");
		EnterLongStopMarket(Convert.ToInt32(TradeSize), vwPrice, "Long VWAP 1st");
		//maybe below can be moved to on order execution!!!
		SetStopLoss(CalculationMode.Price, vwPrice - lATR);
        SetProfitTarget("Long VWAP 1st", CalculationMode.Price, vwPrice + lATR*3);
		Print(" LOng Order is set :" +vwPrice +" SL: "+lATR + " Profit target: "+3*lATR);
		
		}//long trade
	else if (ShortTradeFlag== true)
	{
		//EnterShortLimit(0, false, Convert.ToInt32(TradeSize), vwPrice, "Short VWAP 1st");
		EnterShortStopMarket(Convert.ToInt32(TradeSize), vwPrice, "Short VWAP 1st");
		
		//maybe below can be moved to on order execution!!!
		SetStopLoss(CalculationMode.Price, vwPrice + lATR);
        SetProfitTarget("Short VWAP 1st", CalculationMode.Price, vwPrice - lATR*3);
		Print(" Short Order is set :" +vwPrice +" SL: "+lATR + " Profit target: "+3*lATR);
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

        
     

        } // for end if of flat
		 
      
      }

    } // for onbarupdate

	 protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError) {
      // Checks for all updates to entryOrder.
		
		
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
		
    }
  
    #region Properties

      [Range(1, double.MaxValue), NinjaScriptProperty]
      [Display(ResourceType = typeof (Custom.Resource), Name = "Trade size", GroupName = "Parameters", Order = 0)]
    public double TradeSize {
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
	[Range(1, int.MaxValue), NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name = "EMA Period", GroupName = "Parameters", Order = 0)]
		public int EMAperiod
		{ get; set; }
    [XmlIgnore]
    public Series < double > VWAP_LINE {
      get {
        return Values[1];
      }
    }
    #endregion
  }
}
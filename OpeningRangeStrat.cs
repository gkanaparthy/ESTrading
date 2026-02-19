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
  public class OpeningRangeStrat: Strategy {
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
	bool BE_Set = false;
	
	  double stopNewCalc;
    double PrevDayPnL =0;
	double PrevDayTradeCount =0;
	double vwPrice;
	

    double OrderDilowband;
    double OrderDihighband;
    double OrderDinextband;
    double PT1;
	
	double AccountRealizedPL;
	double AccountUnrealizedPL;
	

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
		double MainBarHigh;
	double MainBarLow;

	  
	  private bool ConvertLocalToESTTime						= true;
				private double WindowStart1						= 700;
				private double WindowEnd1						= 1505;

    protected override void OnMarketData(Data.MarketDataEventArgs marketDataUpdate) {
      if (marketDataUpdate.IsReset)
        prevClose = double.MinValue;
      else if (marketDataUpdate.MarketDataType == Data.MarketDataType.Settlement)
        prevClose = marketDataUpdate.Price;

     

    }

    protected override void OnStateChange() {
      if (State == State.SetDefaults) {
        Description = @"Enter the description for your new custom Strategy here.";
        Name = "OpeningRangeStrat";
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
        
      } else if (State == State.Configure) {

        AddDataSeries(Data.BarsPeriodType.Minute, 1);
        //SetStopLoss(CalculationMode.Ticks, 10);
	AddDataSeries(Data.BarsPeriodType.Tick, 1);
	
		//AddDataSeries(Data.BarsPeriodType.Minute, MTF);
			

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
				
					if ((toTime - 830  == 0 )  && (BarsInProgress == 0 && IsFirstTickOfBar))
			{
				//Print("resetting flags");
				 PrevDayPnL=SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
				PrevDayTradeCount=SystemPerformance.AllTrades.Count;
				
				// Print("******PREV CLOSE***** "+PriorDayOHLC().PriorClose[0]);
				
				 prevLow = PriorDayOHLC().PriorLow[0];
        prevHigh = PriorDayOHLC().PriorHigh[0];
				MainBarHigh=0;
				MainBarLow=0;
		
				      
			}
			
			if ((toTime - 832  == 0 )  && (BarsInProgress == 0 && IsFirstTickOfBar))
			{
				MainBarHigh=High[1];
				MainBarLow=Low[1];
		
				 Print("MainBarHigh: "+MainBarHigh+"      MainBarLow: "+MainBarLow+"     Diff: "+(MainBarHigh-MainBarLow));     
			}
				
			
				
				
			//cannot take settlement from priordayOHLC
				// can take high and low
      if (BarsInProgress == 0 && (toTime - 832  >= 0) && ((SystemPerformance.AllTrades.Count - PrevDayTradeCount)==0)//AccountRealizedPL ==0 
      ) {
		  
		Print("Start: "+toTime+" AccountRealizedPL: "+AccountRealizedPL);
       

	     
if ( Position.MarketPosition == MarketPosition.Flat ) {
        
Print("Ral "+MainBarLow);
         
	
			 if (Close[0]< MainBarLow )
			 {
				 
				 
				 if (Convert.ToInt32(TradeSize)==1)
				 {
					 EnterShort(2,  Convert.ToInt32(TradeSize), "Short VWAP");
					 SetStopLoss(CalculationMode.Price, (MainBarHigh+0.25));
         				SetProfitTarget("Short VWAP",  CalculationMode.Price, MainBarLow-2*(MainBarHigh-MainBarLow)); 
				 }
				 else if (Convert.ToInt32(TradeSize)>1)
				 {
					  EnterShort(2,  Convert.ToInt32(TradeSize/2), "Short VWAP1");
					 EnterShort(2,  Convert.ToInt32(TradeSize/2), "Short VWAP2");
					 SetStopLoss("Short VWAP1", CalculationMode.Price, (MainBarHigh+0.25), false);
					 SetStopLoss("Short VWAP2", CalculationMode.Price, (MainBarHigh+0.25), false);
					 SetProfitTarget("Short VWAP1",  CalculationMode.Price, MainBarLow-1*(MainBarHigh-MainBarLow)); 
					 SetProfitTarget("Short VWAP2",  CalculationMode.Price, MainBarLow-2*(MainBarHigh-MainBarLow)); 
				 }
				 Print("Opening Range: "+(MainBarHigh-MainBarLow));
			 Print("SL: "+(MainBarHigh+0.25));
			 Print("PT: "+(MainBarLow-(MainBarHigh-MainBarLow)));
			 }
			 else if (Close[0]> MainBarHigh)
			 {
				  if (Convert.ToInt32(TradeSize)==1)
				 {
					 EnterLong( 2, Convert.ToInt32(TradeSize ), "Long VWAP");
				 	SetStopLoss(CalculationMode.Price, (MainBarLow-0.25));
         			SetProfitTarget("Long VWAP",  CalculationMode.Price, MainBarHigh+2*(MainBarHigh-MainBarLow)); 
				 }
				 else if (Convert.ToInt32(TradeSize)>1)
				 {
					 EnterLong( 2, Convert.ToInt32(TradeSize/2 ), "Long VWAP1");
					  EnterLong( 2, Convert.ToInt32(TradeSize/2 ), "Long VWAP2");
				 	SetStopLoss("Long VWAP1", CalculationMode.Price, (MainBarLow-0.25), false);
					 SetStopLoss("Long VWAP2", CalculationMode.Price, (MainBarLow-0.25), false);
         			SetProfitTarget("Long VWAP1",  CalculationMode.Price, MainBarHigh+1*(MainBarHigh-MainBarLow)); 
					 SetProfitTarget("Long VWAP2",  CalculationMode.Price, MainBarHigh+2*(MainBarHigh-MainBarLow)); 
				 }
				 
				 Print("Opening Range: "+(MainBarHigh-MainBarLow));
			 Print("SL: "+(MainBarLow-0.25));
			 Print("PT: "+(MainBarHigh+(MainBarHigh-MainBarLow)));
			 }
			 
			 
		
        } // for end if of flat

 if (Position.MarketPosition == MarketPosition.Long && Position.Quantity<Convert.ToInt32(TradeSize) && !BE_Set) {
	//Print("Exiting Long: "+BE_Set);
		SetStopLoss(CalculationMode.Price,   Position.AveragePrice);
				Print("New Stop Loss : "+ Position.AveragePrice);
		BE_Set=true;
 }

 else if (Position.MarketPosition == MarketPosition.Short && Position.Quantity<Convert.ToInt32(TradeSize) && !BE_Set) {
	 Print("Exiting Short: "+BE_Set);
	 
		SetStopLoss(CalculationMode.Price, Position.AveragePrice);
				Print("New Stop Loss: "+  Position.AveragePrice);
		BE_Set=true;
		
	
	 
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
	 
		orderTime = DateTime.Parse("11:59 PM");
			iCumVolume			= 0;
			iCumTypicalVolume	= 0;
		 if (execution.Order.OrderState != OrderState.PartFilled) {
            entryOrder = null;
          }
		 
		 if (execution.Order.Name.StartsWith("Long") || execution.Order.Name.StartsWith("Short"))
		 {
			 
			 orderTime = execution.Order.Time;//Time[0];
			 BE_Set=false;
			 
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
           // Print("outside of s3 and r3 - shouldnt be ");
           // return;

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


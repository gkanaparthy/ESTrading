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
namespace NinjaTrader.NinjaScript.Strategies
{
	public class MTFScalpNTv2 : Strategy
	{
		
		double lATR;
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"";
				Name										= "MTF Scalp NT v2";
				Calculate									= Calculate.OnEachTick;
				EntriesPerDirection							= 1;
				EntryHandling								= EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy				= true;
				ExitOnSessionCloseSeconds					= 2730;
				IsFillLimitOnTouch							= false;
				MaximumBarsLookBack							= MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution							= OrderFillResolution.Standard;
				Slippage									= 0;
				StartBehavior								= StartBehavior.WaitUntilFlat;
				TimeInForce									= TimeInForce.Gtc;
				TraceOrders									= false;
				RealtimeErrorHandling						= RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling							= StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade							= 20;
				// Disable this property for performance gains in Strategy Analyzer optimizations
				// See the Help Guide for additional information
				IsInstantiatedOnEachOptimizationIteration	= true;				
			
				TradeSize									= 1;
				MTF											= 15;
				entryMode									= EntryMode.Aggressive;
				UseWiderStops								= true;
				SRPercentThreshold							= 50;
				RTHOnly										= false;
				TradeExitAlerts								= false;
				UseConservativeEntries						= false;
				ColorBars									= true;
				ShowModeLines								= true;
				ShowLabels									= true;
				UsePresetColors								= true;
				UpColor										= Brushes.Green;
				DownColor									= Brushes.Red;
				NeutralColor								= Brushes.Yellow;
				SkipWindowStart1							= 0;
				SkipWindowEnd1								= 700;
				SkipWindowStart2							= 1455;
				SkipWindowEnd2								= 2359;
				UsingRelatedSecurity						= false;
				RelatedSecurity								= @"^ADD";
				ConvertLocalToESTTime						= false;
				StopLossTicks								= 10; // Default Stop Loss value
				
				AddPlot(Brushes.Green, "TrendLine");
				AddPlot(Brushes.Green, "HTF_TrendLine");
				Plots[0].Width = 1;	
				Plots[1].Width = 2;	
			}
			else if (State == State.Configure)
			{
				AddDataSeries(Data.BarsPeriodType.Minute, MTF);
				
				if (UsingRelatedSecurity)
					AddDataSeries(RelatedSecurity, BarsPeriodType.Day, 1);
				
				//SetStopLoss(CalculationMode.Price, lATR); // Stop Loss
				
				SetStopLoss(CalculationMode.Ticks, StopLossTicks); // Stop Loss
			}
			else if (State == State.DataLoaded)
			{
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
				vwap = VWAP1(BarsArray[0], new VWAPDesign.StdDesign { Enabled = false, Num = 1 }, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, false, true);
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
		
		private TR trCurrent;
		private TR trAg;
		private VWAP1 vwap;
		
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
		
		protected override void OnBarUpdate()
		{
			
			// lATR=Instrument.MasterInstrument.RoundToTickSize(2*ATR(Closes[1], 14)[0]);
			if (UsingRelatedSecurity)
			{
				if (CurrentBars[0] < BarsRequiredToTrade || CurrentBars[1] < BarsRequiredToTrade || CurrentBars[2] < BarsRequiredToTrade)
					return;
			}
			else
			{
				if (CurrentBars[0] < BarsRequiredToTrade || CurrentBars[1] < BarsRequiredToTrade)
					return;
			}
			
			if (BarsInProgress == 0)
			{
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
				
				DateTime targetTime = Time[0];
				
				if (ConvertLocalToESTTime)
					TimeZoneInfo.ConvertTime(Time[0], TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
				
				double toTime = ToTime(targetTime) / 100.0;
				
//				def timeframeOutWindow1 = (SecondsTillTime(skipWindowStart1) > 0 or SecondsTillTime(skipWindowEnd1) < 0);
//				def timeframeOutWindow2 = (SecondsTillTime(skipWindowStart2) > 0 or SecondsTillTime(skipWindowEnd2) < 0);
//				Returns the number of seconds till the specified time (24-hour clock notation) in the EST timezone.
							
				bool timeframeOutWindow1 = SkipWindowStart1 - toTime > 0 || SkipWindowEnd1 - toTime < 0;
				bool timeframeOutWindow2 = SkipWindowStart2 - toTime > 0 || SkipWindowEnd2 - toTime < 0;

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
				double l_ATR = 0;
			
				switch (MAType)
				{
					case AverageType.EMA:
					{
						l_ATR = (EMA(trCurrent, ATRPeriod)[0]);
						break;
					}
					
					case AverageType.HMA:
					{
						l_ATR = (HMA(trCurrent, ATRPeriod)[0]);
						break;
					}
					
					case AverageType.SMA:
					{
						l_ATR = (SMA(trCurrent, ATRPeriod)[0]);
						break;
					}
					
					case AverageType.WMA:
					{
						l_ATR = (WMA(trCurrent, ATRPeriod)[0]);
						break;
					}
				}
				
				double HL2 = (High[0] + Low[0]) / 2;
				
				double UP = HL2 + (ShortTermATRFactor * l_ATR);
				double DN = HL2 + (-ShortTermATRFactor * l_ATR);
				
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
				double aggregationPeriodUP = agHL2 + (ShortTermATRFactor * l_ATR);
				double aggregationPeriodDN = agHL2 + (-ShortTermATRFactor * l_ATR);
				
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
				
				if (ShowLabels)
				{
					string line0 = myState[0] == 1 ? "LTF: Up\n" : "LTF: Down\n";
					string line1 = aggregationPeriodState[0] == 1 ? "HTF: Up\n" : "HTF: Down\n";
					string line2 = "Mode: " + entryMode;
					Draw.TextFixed(this, "myLabel", line0 + line1 + line2, TextPosition.TopLeft);
				}

//				Normal Timeframe
//				plot TrendLine = if bar >= newState then ZLevel else Double.NaN;
				
				if (ShowModeLines)
				{	
					TrendLine[0] = ZLevel[0];
					PlotBrushes[0][0] = myState[0] == StateUp ? Brushes.Green : myState[0] == StateDn ? Brushes.Red : Brushes.Transparent;
				}

//				TrendLine.AssignValueColor(if bar >= newState
//				                   then if State == StateUp then Color.CYAN
//			                       else if State == StateDn then Color.YELLOW
//		                           else Color.CURRENT
//		                           else Color.CURRENT);				

//				Aggregated Timeframe
//				plot aggregatedTimeframeTrendLine = if apBar >= aggregationPeriodNewState then ZLevel else Double.NaN;

//				plot HTFLevel = aggregationPeriodZLevel;

				if (ShowModeLines)
				{
					HTF_TrendLine[0] = aggregationPeriodZLevel[0];
					PlotBrushes[1][0] = aggregationPeriodState[0] == StateUp ? Brushes.Green : aggregationPeriodState[0] == StateDn ? Brushes.Red : Brushes.Blue;
				}
					
//				aggregatedTimeframeTrendLine.AssignValueColor(if apBar >= aggregationPeriodNewState
//				                     then if aggregationPeriodState == StateUp then  if usePresetColors #then Color.GREEN else GetColor(upColor)
//				                          else if aggregationPeriodState == StateDn then  if #usePresetColors then Color.RED else GetColor(downColor)
//				                          else Color.CURRENT
//				                     else Color.CURRENT);

//				AssignBackgroundColor( if aggregationPeriodState == StateUp then Color.DARK_GREEN
//				                            else Color.DARK_RED);

//				VWAP
				
//				if (isPeriodRolled)
//				{
//    				volumeSum = volume;
//    				volumeVwapSum = volume * vwap;
//    				volumeVwap2Sum = volume * Sqr(vwap);
//				}
//				else
//				{
//    				volumeSum = CompoundValue(1, volumeSum[1] + volume, volume);
//    				volumeVwapSum = CompoundValue(1, volumeVwapSum[1] + volume * vwap, volume * vwap);
//    				volumeVwap2Sum = CompoundValue(1, volumeVwap2Sum[1] + volume * Sqr(vwap), volume * Sqr(vwap));
//				}
				
//				bool isPeriodRolled = Bars.IsFirstBarOfSession;
				
//				if (isPeriodRolled)
//				{
//				    volumeSum[0] = Volume[0];
//				    volumeVwapSum[0] = Volume[0] * vwap.Output[0];
//				    volumeVwap2Sum[0] = Volume[0] * Math.Pow(vwap.Output[0], 2);
//					volumeSum[1] = 0;
//				    volumeVwapSum[1] = 0;
//				    volumeVwap2Sum[1] = 0;
//				}
//				else
//				{
//		    		volumeSum[0] = CurrentBars[0] > 1 ? volumeSum[1] + Volume[0] : Volume[0];
//		    		volumeVwapSum[0] = CurrentBars[0] > 1 ? volumeVwapSum[1] + Volume[0] * vwap.Output[0] : Volume[0] * vwap.Output[0];
//		    		volumeVwap2Sum[0] = CurrentBars[0] > 1 ? volumeVwap2Sum[1] + Volume[0] * Math.Pow(vwap.Output[0], 2) : Volume[0] * Math.Pow(vwap.Output[0], 2);
//				}
				
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
				
				if (UsingRelatedSecurity)
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
				
				if (!UsingRelatedSecurity)
				{
					rsBuyCondition = true;
					rsShortCondition = true;
				}

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
		
				BarBrushes[0] = CandleOutlineBrushes[0] = ColorBars && myState[0] == StateUp ? UsePresetColors ? Brushes.Green : UpColor :
				                    ColorBars && myState[0] == StateDn ? UsePresetColors ? Brushes.Red : DownColor :
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


//				def qty = GetQuantity();

//				Long side trade
				if (buyCondition && IsFirstTickOfBar)
				{
					EnterLong(TradeSize);
					
					if (!playBuy)
					{
						playBuy = true;
						//Alert("ScalperMTFBuyalert", Priority.High, "Scalper MTF Buy alert", NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.Black, Brushes.Yellow);  
					}
				}

//		 		Long side trade exit
				if (buyExitCondition1)
				{
					ExitLong();
					
					if (TradeExitAlerts)
					{
						if (!playExitLong)
						{
							playExitLong = true;
							//Alert("ScalperMTFLongExitalert", Priority.High, "Scalper MTF Long Exit alert", NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.Black, Brushes.Yellow);  
						}
					}
				}

//				Short side trade
				if (sellCondition  && IsFirstTickOfBar)
				{
					EnterShort(TradeSize);
					
					if (!playSell)
					{
						playSell = true;
						//Alert("ScalperMTFShortalert", Priority.High, "Scalper MTF Short alert", NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.Black, Brushes.Yellow);  
					}
				}
					
//		 		Short side trade exit
				if (shortExitCondition1)
				{
					ExitShort();
					
					if (TradeExitAlerts)
					{
						if (!playExitShort)
						{
							playExitShort = true;
						//	Alert("ScalperMTFShortExitalert", Priority.High, "Scalper MTF Short Exit alert", NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.Black, Brushes.Yellow);  
						}
					}
				}
				
//				Please look at "Exit on session close" parameter
//				See also - https://ninjatrader.com/support/helpGuides/nt8/?isexitonsessionclosestrategy.htm

//				# close all position EOD
//				def startOffset = 2;
//				def endOffset = 2;
//				def isRollover = GetYYYYMMDD() != GetYYYYMMDD()[1];
//				def beforeStart = GetTime() < RegularTradingStart(GetYYYYMMDD());
//				def afterEnd = GetTime() > RegularTradingEnd(GetYYYYMMDD());
//				def firstBarOfDay = if
//		    	(beforeStart[1+startOffset] == 1 and beforeStart[startOffset] == 0) or
//		    	(isRollover[startOffset] and beforeStart[startOffset] == 0)
//		    	then 1
//		    	else 0;
//				def lastBarOfDay = if
//		    	(afterEnd[-1-endOffset] == 1 and afterEnd[endOffset] == 0) or
//		    	(isRollover[-1-endOffset] and firstBarOfDay[-1-endOffset])
//		    	then 1
//		    	else 0;

//				AddOrder(OrderType.SELL_TO_CLOSE, lastBarOfDay[-1], Open[-1], 1);
//				AddOrder(OrderType.BUY_TO_CLOSE, lastBarOfDay[-1], Open[-1], 1);
			}
		}		
		
		#region Properties
		[Range(1, int.MaxValue), NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name = "Trade Size", GroupName = "Parameters", Order = 0)]
		public int TradeSize
		{ get; set; }
		
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
		[Display(Name="Use Wider Stops", Order = 3, GroupName="Parameters")]
		public bool UseWiderStops
		{ get; set; }
		
		[Range(1, int.MaxValue), NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name = "S R Percent Threshold", GroupName = "Parameters", Order = 4)]
		public int SRPercentThreshold
		{ get; set; }
										
		[NinjaScriptProperty]
		[Display(Name="RTH Only", Order = 5, GroupName="Parameters")]
		public bool RTHOnly
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Trade Exit Alerts", Order = 6, GroupName="Parameters")]
		public bool TradeExitAlerts
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Use Conservative Entries", Order = 7, GroupName="Parameters")]
		public bool UseConservativeEntries
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Color Bars", Order = 8, GroupName="Parameters")]
		public bool ColorBars
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Show Mode Lines", Order = 9, GroupName="Parameters")]
		public bool ShowModeLines
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Show Labels", Order = 10, GroupName="Parameters")]
		public bool ShowLabels
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Use Preset Colors", Order = 11, GroupName="Parameters")]
		public bool UsePresetColors
		{ get; set; }
		
		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name="Up Color", Description="Up Color", Order=12, GroupName="Parameters")]
		public Brush UpColor
		{ get; set; }

		[Browsable(false)]
		public string UpColorSerializable
		{
			get { return Serialize.BrushToString(UpColor); }
			set { UpColor = Serialize.StringToBrush(value); }
		}
		
		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name="Down Color", Description="Down Color", Order=13, GroupName="Parameters")]
		public Brush DownColor
		{ get; set; }

		[Browsable(false)]
		public string DownColorSerializable
		{
			get { return Serialize.BrushToString(DownColor); }
			set { DownColor = Serialize.StringToBrush(value); }
		}			
		
		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name="Neutral Color", Description="Neutral Color", Order=14, GroupName="Parameters")]
		public Brush NeutralColor
		{ get; set; }

		[Browsable(false)]
		public string NeutralColorSerializable
		{
			get { return Serialize.BrushToString(NeutralColor); }
			set { NeutralColor = Serialize.StringToBrush(value); }
		}			
		
		[Range(0, int.MaxValue), NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name = "Skip Window Start1", GroupName = "Parameters", Order = 15)]
		public int SkipWindowStart1
		{ get; set; }
		
		[Range(0, int.MaxValue), NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name = "Skip Window End1", GroupName = "Parameters", Order = 16)]
		public int SkipWindowEnd1
		{ get; set; }
		
		[Range(0, int.MaxValue), NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name = "Skip Window Start 2", GroupName = "Parameters", Order = 17)]
		public int SkipWindowStart2
		{ get; set; }
		
		[Range(0, int.MaxValue), NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name = "Skip Window End 2", GroupName = "Parameters", Order = 18)]
		public int SkipWindowEnd2
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Using Related Security", Order = 19, GroupName="Parameters")]
		public bool UsingRelatedSecurity
		{ get; set; }
		
		[Display(ResourceType = typeof(Custom.Resource), Name = "Related Security", GroupName = "Parameters", Order = 20)]
		public string RelatedSecurity
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Convert Local To EST Time", Order = 21, GroupName="Parameters")]
		public bool ConvertLocalToESTTime
		{ get; set; }
		
		// Stop Loss input parameter
		[Range(0, int.MaxValue), NinjaScriptProperty]
		[Display(ResourceType = typeof(Custom.Resource), Name = "Stop Loss Ticks", GroupName = "Parameters", Order = 22)]
		public int StopLossTicks
		{ get; set; }
		
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
	}
}

public enum EntryMode
{
	Aggressive,
	Conservative,
	NonRepaintAggressive,
	NonRepaintConservative
}
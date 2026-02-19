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
	public class SessionLevelsPullback : Strategy
	{
		// Variables for Levels
		private double prevDayHigh = 0;
		private double prevDayLow = 0;
		private double premarketHigh = double.MinValue;
		private double premarketLow = double.MaxValue;
		
		// Lists to hold active trading levels
		private List<double> allLevels = new List<double>();

		// State Variables
		private bool beSet = false;
		private DateTime currentSessionDate = DateTime.MinValue;
		private int sessionStartBar = 0;
		
		// Cooldown tracking per level (Level Price -> BarIndex of last touch)
		private Dictionary<double, int> levelCooldowns = new Dictionary<double, int>();

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= "Counter-trend strategy trading against Session and Premarket levels.";
				Name										= "SessionLevelsPullback";
				Calculate									= Calculate.OnBarClose;
				EntriesPerDirection							= 1;
				EntryHandling								= EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy				= true; 
				ExitOnSessionCloseSeconds					= 30;
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
				
				// Default Parameter Values
				TradeSize = 1;
				RTHOnly = true; // If true, trades ONLY happen between 8:30 AM and 3:00 PM
				LevelProximityPoints = 4;
				CooldownBars = 5;
				StopLossTicks = 5;
				ProfitTarget1Ticks = 10;
				ProfitTarget2Ticks = 40;
			}
			else if (State == State.Configure)
			{
				// Add Daily Bars for Previous Day High/Low (BarsInProgress index 1)
				AddDataSeries(BarsPeriodType.Day, 1);
			}
		}

		protected override void OnBarUpdate()
		{
			// Ensure we have enough bars on both the 2min (0) and Daily (1) series
			if (CurrentBars[0] < BarsRequiredToTrade || CurrentBars[1] < 1)
				return;

			// -------------------------------------------------------------------------
			// 1. Daily Reset & Level Calculation Logic
			// -------------------------------------------------------------------------
			
			// Detect Start of a new trading session (using the 2min bars date)
			if (Times[0][0].Date != currentSessionDate)
			{
				currentSessionDate = Times[0][0].Date;
				sessionStartBar = CurrentBars[0];
				
				// Reset Session Variables
				premarketHigh = double.MinValue;
				premarketLow = double.MaxValue;
				beSet = false;
				levelCooldowns.Clear();
				
				// Calculate Previous Day High/Low from the Daily Series (BarsInProgress == 1)
				prevDayHigh = Highs[1][1];
				prevDayLow = Lows[1][1];
				
				Print(string.Format("New Day: {0} | PrevDayHigh: {1} | PrevDayLow: {2}", currentSessionDate, prevDayHigh, prevDayLow));
			}

			// Only process logic on the primary 2min chart
			if (BarsInProgress != 0) return;

			// Define Time Constants (CST)
			int currentTime = ToTime(Time[0]);
			int premarketStart = 30000; // 3:00 AM
			int rthStart = 83000;       // 8:30 AM
			int rthEnd = 150000;        // 3:00 PM

			// -------------------------------------------------------------------------
			// 2. Premarket High/Low Calculation (3:00 AM - 8:30 AM)
			// -------------------------------------------------------------------------
			if (currentTime >= premarketStart && currentTime < rthStart)
			{
				if (High[0] > premarketHigh) premarketHigh = High[0];
				if (Low[0] < premarketLow) premarketLow = Low[0];
			}

			// -------------------------------------------------------------------------
			// 3. Draw Levels on Chart
			// -------------------------------------------------------------------------
			// Draw Previous Day Levels (Blue)
			Draw.Line(this, "PDH", false, CurrentBars[0] - sessionStartBar, prevDayHigh, 0, prevDayHigh, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
			Draw.Line(this, "PDL", false, CurrentBars[0] - sessionStartBar, prevDayLow, 0, prevDayLow, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);

			// Draw Premarket Levels (Goldenrod) - Only if they are valid values
			if (premarketHigh > double.MinValue)
				Draw.Line(this, "PMH", false, CurrentBars[0] - sessionStartBar, premarketHigh, 0, premarketHigh, Brushes.Goldenrod, DashStyleHelper.Dash, 2);
			
			if (premarketLow < double.MaxValue)
				Draw.Line(this, "PML", false, CurrentBars[0] - sessionStartBar, premarketLow, 0, premarketLow, Brushes.Goldenrod, DashStyleHelper.Dash, 2);


			// -------------------------------------------------------------------------
			// 4. Level Consolidation Logic
			// -------------------------------------------------------------------------
			// We collect all valid levels first
			allLevels.Clear();
			allLevels.Add(prevDayHigh);
			allLevels.Add(prevDayLow);
			if (premarketHigh > double.MinValue) allLevels.Add(premarketHigh);
			if (premarketLow < double.MaxValue) allLevels.Add(premarketLow);
			
			// Sort levels by distance to Current Price (Close[0]) to prioritize the closest one
			double currentPrice = Close[0];
			allLevels.Sort((a, b) => Math.Abs(a - currentPrice).CompareTo(Math.Abs(b - currentPrice)));

			// -------------------------------------------------------------------------
			// 5. Entry Logic
			// -------------------------------------------------------------------------
			
			// Check RTH Only Constraint
			if (RTHOnly)
			{
				if (currentTime < rthStart || currentTime >= rthEnd)
					return;
			}

			// Only look for entries if we are flat
			if (Position.MarketPosition == MarketPosition.Flat)
			{
				foreach (double rawLevel in allLevels)
				{
					// --- DYNAMIC LEVEL ADJUSTMENT (Most Generous Logic) ---
					
					double tradingLevel = rawLevel;
					bool isShortSetup = false;
					bool isLongSetup = false;

					// Check if Price is approaching from BELOW (Potential Short/Resistance)
					// We use Close[1] (previous bar close) to determine where price was coming from
					if (Close[1] < rawLevel) 
					{
						// Potential SHORT. 
						// Check for nearby levels to merge. For Shorts, we want the HIGHEST of the cluster.
						// Look for any other level within proximity that is higher than current rawLevel
						// Note: We query the original unsorted list or re-query the sorted one, logic holds.
						double nearbyHigher = allLevels.Where(l => l > rawLevel && (l - rawLevel) <= LevelProximityPoints).DefaultIfEmpty(double.MinValue).Max();
						
						if (nearbyHigher > double.MinValue)
						{
							// There is a higher level nearby, so we ignore this lower one for shorting.
							continue; 
						}
						
						// If High touches this level, and we came from below.
						if (High[0] >= tradingLevel && Low[0] < tradingLevel)
						{
							isShortSetup = true;
						}
					}
					// Check if Price is approaching from ABOVE (Potential Long/Support)
					else if (Close[1] > rawLevel)
					{
						// Potential LONG.
						// Check for nearby levels to merge. For Longs, we want the LOWEST of the cluster.
						double nearbyLower = allLevels.Where(l => l < rawLevel && (rawLevel - l) <= LevelProximityPoints).DefaultIfEmpty(double.MaxValue).Min();

						if (nearbyLower < double.MaxValue)
						{
							// There is a lower (better) level nearby, skip this one.
							continue;
						}

						if (Low[0] <= tradingLevel && High[0] > tradingLevel)
						{
							isLongSetup = true;
						}
					}

					// --- EXECUTION (LIMIT ORDERS) ---
					if (isShortSetup)
					{
						if (IsCooldownActive(tradingLevel)) continue;

						// Place Limit Order at the Level Price
						EnterShortLimit(TradeSize * 2, tradingLevel, "EntryShort"); 
						
						SetStopLoss("EntryShort", CalculationMode.Ticks, StopLossTicks, false);
						SetProfitTarget("EntryShort", CalculationMode.Ticks, ProfitTarget1Ticks);
						UpdateCooldown(tradingLevel);
						break; 
					}
					else if (isLongSetup)
					{
						if (IsCooldownActive(tradingLevel)) continue;

						// Place Limit Order at the Level Price
						EnterLongLimit(TradeSize * 2, tradingLevel, "EntryLong");
						
						SetStopLoss("EntryLong", CalculationMode.Ticks, StopLossTicks, false);
						SetProfitTarget("EntryLong", CalculationMode.Ticks, ProfitTarget1Ticks);
						UpdateCooldown(tradingLevel);
						break;
					}
				}
			}
		}

		// -------------------------------------------------------------------------
		// 6. Trade Management (Breakeven & Split Targets)
		// -------------------------------------------------------------------------
		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution.Order != null && execution.Order.OrderState == OrderState.Filled)
			{
				if (execution.Order.Name == "EntryLong" || execution.Order.Name == "EntryShort")
				{
					beSet = false;
					SetProfitTarget("EntryLong", CalculationMode.Ticks, ProfitTarget1Ticks);
					SetProfitTarget("EntryShort", CalculationMode.Ticks, ProfitTarget1Ticks);
				}

				if (Position.MarketPosition != MarketPosition.Flat && !beSet)
				{
					if (Position.Quantity == TradeSize) 
					{
						if (Position.MarketPosition == MarketPosition.Long)
							SetStopLoss("EntryLong", CalculationMode.Price, Position.AveragePrice, false);
						else if (Position.MarketPosition == MarketPosition.Short)
							SetStopLoss("EntryShort", CalculationMode.Price, Position.AveragePrice, false);
						
						if (Position.MarketPosition == MarketPosition.Long)
							SetProfitTarget("EntryLong", CalculationMode.Ticks, ProfitTarget2Ticks);
						else
							SetProfitTarget("EntryShort", CalculationMode.Ticks, ProfitTarget2Ticks);

						beSet = true;
					}
				}
			}
		}

		// -------------------------------------------------------------------------
		// Helper Methods
		// -------------------------------------------------------------------------
		
		private bool IsCooldownActive(double level)
		{
			if (levelCooldowns.ContainsKey(level))
			{
				int lastTouchIndex = levelCooldowns[level];
				if (CurrentBar - lastTouchIndex <= CooldownBars)
					return true;
			}
			return false;
		}

		private void UpdateCooldown(double level)
		{
			// We round the level to 2 decimal places for dictionary keys to avoid floating point issues
			double key = Math.Round(level, 2);
			
			if (levelCooldowns.ContainsKey(key))
				levelCooldowns[key] = CurrentBar;
			else
				levelCooldowns.Add(key, CurrentBar);
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="TradeSize (Per Leg)", Order=1, GroupName="Parameters")]
		public int TradeSize
		{ get; set; }

		[NinjaScriptProperty]
		[Display(Name="RTHOnly (Trade 8:30-15:00)", Order=2, GroupName="Parameters")]
		public bool RTHOnly
		{ get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name="LevelProximityPoints", Order=3, GroupName="Parameters")]
		public double LevelProximityPoints
		{ get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name="CooldownBars", Order=4, GroupName="Parameters")]
		public int CooldownBars
		{ get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="StopLossTicks", Order=5, GroupName="Parameters")]
		public int StopLossTicks
		{ get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="ProfitTarget1Ticks", Order=6, GroupName="Parameters")]
		public int ProfitTarget1Ticks
		{ get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="ProfitTarget2Ticks", Order=7, GroupName="Parameters")]
		public int ProfitTarget2Ticks
		{ get; set; }
		#endregion
	}
}
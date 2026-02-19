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
    public class WaveAVWAPStrategy : Strategy
    {
        // Core variables
        private List<PivotCandidate> allPivotCandidates = new List<PivotCandidate>();
        private List<PivotCandidate> activePivots = new List<PivotCandidate>();
        private Dictionary<DateTime, int> pivotToIndicatorMap = new Dictionary<DateTime, int>();
        private bool inRTH = false;
        private double swingHigh = double.MinValue;
        private double swingLow = double.MaxValue;
        private DateTime swingHighTime = DateTime.MinValue;
        private DateTime swingLowTime = DateTime.MinValue;
        private int consecutiveClosesAbove = 0;
        private int consecutiveClosesBelow = 0;
        private int crossedAVWAPindex = -1;
        private double entryPrice = 0;
        
        // AVWAP Pool - pre-created indicators
        private AVWAP2[] avwapPool;
        private bool[] avwapInUse;
        private DateTime[] avwapPivotTimes;
        
        // Direction tracking
        private enum TrendDirection { Up, Down, Undefined }
        private TrendDirection currentDirection = TrendDirection.Undefined;
        private int barsSinceDirectionChange = 0;
        
        // Pivot evaluation parameters
        private int atrPeriod = 14;
        private double atrMultiplier = 0.7;
        private int maxActivePivots = 6; // Maximum active pivots (3 highs, 3 lows)
        private int minBarsForSwing = 4;
        private int consecutiveBarsRequired = 3;
        
        // Reevaluation timer
        private int barsSinceLastReeval = 0;
        private int reevalInterval = 10; // Reevaluate pivots every 10 bars
        
        // Tracking which bar a pivot was used for trading
        private HashSet<DateTime> pivotsUsedForTrades = new HashSet<DateTime>();

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Strategy that trades based on AVWAP crossovers from important pivot points";
                Name = "WaveAVWAPStrategy";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                BarsRequiredToTrade = 20;
                
                // Parameters
                TradeSize = 1;
                StopLoss = 5;
                MaxProfitTarget = 20;
                ATRPeriod = 14;
                ATRMultiplier = 0.7;
                MaxActivePivots = 6;
                MinBarsForSwing = 4;
                ReassessmentInterval = 10;
                SessionStartTime = DateTime.Parse("9:30 AM");
                SessionEndTime = DateTime.Parse("4:00 PM");
                
                // For visualization
                AddPlot(Brushes.DodgerBlue, "SignificantSwingHighs");
                AddPlot(Brushes.Crimson, "SignificantSwingLows");
            }
            else if (State == State.Configure)
            {
                // Add data series
                AddDataSeries(Data.BarsPeriodType.Minute, 1);
                
                // Update from properties
                atrPeriod = ATRPeriod;
                atrMultiplier = ATRMultiplier;
                maxActivePivots = MaxActivePivots;
                minBarsForSwing = MinBarsForSwing;
                reevalInterval = ReassessmentInterval;
                
                // Initialize pools
                avwapPool = new AVWAP2[maxActivePivots];
                avwapInUse = new bool[maxActivePivots];
                avwapPivotTimes = new DateTime[maxActivePivots];
            }
            else if (State == State.DataLoaded)
            {
                // Pre-create all potential AVWAP indicators for the pool
                for (int i = 0; i < maxActivePivots; i++)
                {
                    // Create AVWAP with default time - we'll update this later
                    avwapPool[i] = AVWAP2(BarsArray[0], Time[0], 
                        new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, 
                        new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, 
                        true, true, true);
                    
                    // Set colors and width
                    avwapPool[i].Plots[0].Width = 2;
                    
                    // Set color alternating between red and green
                    avwapPool[i].Plots[0].Brush = (i % 2 == 0) ? Brushes.Red : Brushes.Green;
                    
                    // Hide by default
                    avwapPool[i].IsVisible = false;
                    
                    // Add to chart
                    AddChartIndicator(avwapPool[i]);
                    
                    // Mark as available
                    avwapInUse[i] = false;
                    avwapPivotTimes[i] = DateTime.MinValue;
                }
                
                ResetPivotVariables();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < BarsRequiredToTrade)
                return;

            // Check if we're in Regular Trading Hours
            DateTime currentTime = Time[0];
            inRTH = currentTime.TimeOfDay >= SessionStartTime.TimeOfDay && 
                    currentTime.TimeOfDay <= SessionEndTime.TimeOfDay;

            // Only process during RTH
            if (!inRTH)
                return;

            // Check for session start - reset pivots for new session
            if (Bars.IsFirstBarOfSession)
            {
                ResetPivotVariables();
                Print("New session started. Pivots reset.");
            }
            
            // Calculate current ATR
            double currentATR = ATR(atrPeriod)[0];
            
            // Identify potential pivot points
            IdentifyPotentialPivots(currentATR);
            
            // Periodically reassess and update the active pivots
            barsSinceLastReeval++;
            if (barsSinceLastReeval >= reevalInterval)
            {
                ReevaluateActivePivots(currentATR);
                barsSinceLastReeval = 0;
            }
            
            // Check for AVWAP crossovers
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                CheckForCrossovers();
            }
            
            // Manage existing positions
            ManagePositions();
        }

        private void ResetPivotVariables()
        {
            // Clear all collections
            allPivotCandidates.Clear();
            activePivots.Clear();
            pivotsUsedForTrades.Clear();
            pivotToIndicatorMap.Clear();
            
            // Reset all AVWAP indicators
            for (int i = 0; i < maxActivePivots; i++)
            {
                if (avwapPool[i] != null)
                {
                    avwapPool[i].IsVisible = false;
                    avwapInUse[i] = false;
                    avwapPivotTimes[i] = DateTime.MinValue;
                }
            }
            
            // Reset tracking variables
            swingHigh = High[0];
            swingLow = Low[0];
            swingHighTime = Time[0];
            swingLowTime = Time[0];
            currentDirection = TrendDirection.Undefined;
            barsSinceDirectionChange = 0;
            consecutiveClosesAbove = 0;
            consecutiveClosesBelow = 0;
            crossedAVWAPindex = -1;
            barsSinceLastReeval = 0;
        }

        private void IdentifyPotentialPivots(double currentATR)
        {
            // Need enough bars for ATR and trend identification
            if (CurrentBar < Math.Max(atrPeriod, minBarsForSwing + consecutiveBarsRequired))
                return;
                
            // Plot initially as NaN - will be updated if active swing points are found
            Values[0][0] = double.NaN; // Swing highs
            Values[1][0] = double.NaN; // Swing lows
            
            // Update swing highs and lows
            if (High[0] > swingHigh)
            {
                swingHigh = High[0];
                swingHighTime = Time[0];
            }
            
            if (Low[0] < swingLow)
            {
                swingLow = Low[0];
                swingLowTime = Time[0];
            }
            
            // Set initial direction if undefined
            if (currentDirection == TrendDirection.Undefined)
            {
                if (Close[0] > Close[1] && Close[1] > Close[2])
                {
                    currentDirection = TrendDirection.Up;
                    barsSinceDirectionChange = 1;
                }
                else if (Close[0] < Close[1] && Close[1] < Close[2])
                {
                    currentDirection = TrendDirection.Down;
                    barsSinceDirectionChange = 1;
                }
                return;
            }
            
            // Track bars since direction change
            barsSinceDirectionChange++;
            
            // Check for direction change (with stronger confirmation pattern)
            bool directionChanged = false;
            TrendDirection newDirection = currentDirection;
            
            // For a potential swing high
            if (currentDirection == TrendDirection.Up)
            {
                // Check for consecutive lower closes indicating trend change
                bool hasConsecutiveLowerCloses = true;
                for (int i = 0; i < consecutiveBarsRequired; i++)
                {
                    if (Close[i] >= Close[i+1])
                    {
                        hasConsecutiveLowerCloses = false;
                        break;
                    }
                }
                
                if (hasConsecutiveLowerCloses && barsSinceDirectionChange >= minBarsForSwing)
                {
                    // Calculate move significance
                    double dropAmount = swingHigh - Close[0];
                    double significanceScore = dropAmount / currentATR;
                    
                    if (significanceScore >= atrMultiplier)
                    {
                        newDirection = TrendDirection.Down;
                        directionChanged = true;
                        
                        // Create a pivot candidate
                        PivotCandidate candidate = new PivotCandidate
                        {
                            Price = swingHigh,
                            Time = swingHighTime,
                            IsHigh = true,
                            SignificanceScore = significanceScore,
                            AdjustedScore = significanceScore, // Initial adjusted score is the same
                            MoveSize = dropAmount,
                            BarsInMove = barsSinceDirectionChange
                        };
                        
                        // Add to all candidates list
                        allPivotCandidates.Add(candidate);
                        Print("Added potential swing HIGH: " + swingHigh + " at " + swingHighTime + 
                              " (Score: " + significanceScore.ToString("F2") + ")");
                        
                        // Reset swing low tracking
                        swingLow = Low[0];
                        swingLowTime = Time[0];
                    }
                }
            }
            // For a potential swing low
            else if (currentDirection == TrendDirection.Down)
            {
                // Check for consecutive higher closes indicating trend change
                bool hasConsecutiveHigherCloses = true;
                for (int i = 0; i < consecutiveBarsRequired; i++)
                {
                    if (Close[i] <= Close[i+1])
                    {
                        hasConsecutiveHigherCloses = false;
                        break;
                    }
                }
                
                if (hasConsecutiveHigherCloses && barsSinceDirectionChange >= minBarsForSwing)
                {
                    // Calculate move significance
                    double riseAmount = Close[0] - swingLow;
                    double significanceScore = riseAmount / currentATR;
                    
                    if (significanceScore >= atrMultiplier)
                    {
                        newDirection = TrendDirection.Up;
                        directionChanged = true;
                        
                        // Create a pivot candidate
                        PivotCandidate candidate = new PivotCandidate
                        {
                            Price = swingLow,
                            Time = swingLowTime,
                            IsHigh = false,
                            SignificanceScore = significanceScore,
                            AdjustedScore = significanceScore, // Initial adjusted score is the same
                            MoveSize = riseAmount,
                            BarsInMove = barsSinceDirectionChange
                        };
                        
                        // Add to all candidates list
                        allPivotCandidates.Add(candidate);
                        Print("Added potential swing LOW: " + swingLow + " at " + swingLowTime + 
                              " (Score: " + significanceScore.ToString("F2") + ")");
                        
                        // Reset swing high tracking
                        swingHigh = High[0];
                        swingHighTime = Time[0];
                    }
                }
            }
            
            // If direction changed, update tracking variables
            if (directionChanged)
            {
                currentDirection = newDirection;
                barsSinceDirectionChange = 1;
            }
            
            // Plot active pivots on chart
            foreach (var pivot in activePivots)
            {
                if (Time[0].Date == pivot.Time.Date) // Only plot same-day pivots
                {
                    int barsAgo = GetBarsSinceTime(pivot.Time);
                    
                    if (barsAgo >= 0)
                    {
                        if (pivot.IsHigh)
                            Values[0][barsAgo] = pivot.Price;
                        else
                            Values[1][barsAgo] = pivot.Price;
                    }
                }
            }
        }
        
        // Helper method to get bars since a specific time
        private int GetBarsSinceTime(DateTime time)
        {
            // Start from the current bar and count backward
            for (int i = 0; i < Math.Min(CurrentBar, 1000); i++) // Limit search to 1000 bars for performance
            {
                if (Time[i] <= time && time <= Time[i].AddSeconds(BarsPeriod.Value))
                    return i;
            }
            return -1; // Not found
        }

        private void ReevaluateActivePivots(double currentATR)
        {
            // Skip if no candidates
            if (allPivotCandidates.Count == 0)
                return;
                
            // Get current session's start time - use today's date with session start time
            DateTime today = Time[0].Date;
            DateTime sessionStartDate = today.Add(SessionStartTime.TimeOfDay);
            
            // If current time is before session start, adjust to previous day
            if (Time[0] < sessionStartDate)
                sessionStartDate = sessionStartDate.AddDays(-1);
            
            // Filter candidates from current session
            var sessionCandidates = allPivotCandidates
                .Where(p => p.Time >= sessionStartDate)
                .ToList();
                
            if (sessionCandidates.Count == 0)
                return;
                
            // Apply recency bonus to significance scores
            foreach (var candidate in sessionCandidates)
            {
                // Calculate time-based decay - more recent pivots get a bonus
                TimeSpan elapsed = Time[0] - candidate.Time;
                double recencyFactor = Math.Max(0, 1 - (elapsed.TotalMinutes / 120)); // 2-hour window
                
                // Pivots used in trades get a bonus
                double tradingBonus = pivotsUsedForTrades.Contains(candidate.Time) ? 0.5 : 0;
                
                // Update adjusted score
                candidate.AdjustedScore = candidate.SignificanceScore * (1 + recencyFactor + tradingBonus);
            }
            
            // Sort candidates by adjusted significance (separately for highs and lows)
            var highCandidates = sessionCandidates
                .Where(p => p.IsHigh)
                .OrderByDescending(p => p.AdjustedScore)
                .ToList();
                
            var lowCandidates = sessionCandidates
                .Where(p => !p.IsHigh)
                .OrderByDescending(p => p.AdjustedScore)
                .ToList();
                
            // Select top candidates
            int maxEachType = maxActivePivots / 2; // Equal split between highs and lows
            var topHighs = highCandidates.Take(maxEachType).ToList();
            var topLows = lowCandidates.Take(maxEachType).ToList();
            
            // Print diagnostic info
            Print("Reevaluating pivots - Selected " + topHighs.Count + " highs and " + topLows.Count + " lows");
            
            // Determine which pivots to add or remove
            var newTopPivots = new List<PivotCandidate>();
            newTopPivots.AddRange(topHighs);
            newTopPivots.AddRange(topLows);
            
            var pivotsToRemove = activePivots
                .Where(p => !newTopPivots.Any(np => np.Time == p.Time))
                .ToList();
                
            var pivotsToAdd = newTopPivots
                .Where(p => !activePivots.Any(ap => ap.Time == p.Time))
                .ToList();
                
            // Remove pivots no longer in top list
            foreach (var pivot in pivotsToRemove)
            {
                activePivots.Remove(pivot);
                
                // Find and release the indicator
                if (pivotToIndicatorMap.ContainsKey(pivot.Time))
                {
                    int index = pivotToIndicatorMap[pivot.Time];
                    if (index >= 0 && index < avwapPool.Length)
                    {
                        // Hide the indicator
                        avwapPool[index].IsVisible = false;
                        avwapInUse[index] = false;
                        avwapPivotTimes[index] = DateTime.MinValue;
                    }
                    
                    pivotToIndicatorMap.Remove(pivot.Time);
                    Print("Removed pivot " + (pivot.IsHigh ? "HIGH" : "LOW") + ": " + pivot.Price + 
                          " at " + pivot.Time + " (Score: " + pivot.AdjustedScore.ToString("F2") + ")");
                }
            }
            
            // Add new top pivots
            foreach (var pivot in pivotsToAdd)
            {
                // Find an available indicator in the pool
                int availableIndex = -1;
                for (int i = 0; i < avwapPool.Length; i++)
                {
                    if (!avwapInUse[i])
                    {
                        availableIndex = i;
                        break;
                    }
                }
                
                // Skip if no indicators available
                if (availableIndex < 0)
                {
                    Print("WARNING: No available AVWAP indicators in pool. Skipping pivot.");
                    continue;
                }
                
                // Update the indicator with new pivot time
                AVWAP2 avwap = avwapPool[availableIndex];
                
                // Set appropriate color
                avwap.Plots[0].Brush = pivot.IsHigh ? Brushes.Red : Brushes.Green;
                
                // Reset the AVWAP with the new anchor time
                avwap.SetState(State.Configure); // Reset the indicator
                avwap = AVWAP2(BarsArray[0], pivot.Time,
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, 
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, 
                    true, true, true);
                
                // Make visible
                avwap.IsVisible = true;
                
                // Mark as in use
                avwapInUse[availableIndex] = true;
                avwapPivotTimes[availableIndex] = pivot.Time;
                
                // Add to map
                pivotToIndicatorMap[pivot.Time] = availableIndex;
                
                // Add to active pivots
                activePivots.Add(pivot);
                
                Print("Added new pivot " + (pivot.IsHigh ? "HIGH" : "LOW") + ": " + pivot.Price + 
                      " at " + pivot.Time + " (Score: " + pivot.AdjustedScore.ToString("F2") + ")");
            }
        }

        private void CheckForCrossovers()
        {
            // Skip if no active pivots
            if (activePivots.Count == 0)
                return;
                
            // Check each active AVWAP for potential crossovers
            foreach (var pivot in activePivots)
            {
                // Skip if pivot not mapped to indicator
                if (!pivotToIndicatorMap.ContainsKey(pivot.Time))
                    continue;
                    
                int index = pivotToIndicatorMap[pivot.Time];
                AVWAP2 avwap = avwapPool[index];
                double avwapValue = avwap.Output[0];
                
                // Check for crossover from below (for long entry)
                if (Close[0] > avwapValue && Close[1] <= avwapValue)
                {
                    consecutiveClosesAbove = 1;
                    crossedAVWAPindex = index;
                    Print("First close above AVWAP (from " + pivot.Time + "): " + avwapValue);
                }
                else if (crossedAVWAPindex == index && Close[0] > avwapValue && consecutiveClosesAbove == 1)
                {
                    consecutiveClosesAbove = 2;
                    Print("Second consecutive close above AVWAP (from " + pivot.Time + "): " + avwapValue);
                    
                    // Find next higher AVWAP for profit target
                    double nextHigherAVWAP = FindNextAVWAP(avwapValue, true);
                    double profitTarget = Math.Min(MaxProfitTarget, nextHigherAVWAP - Close[0]);
                    
                    // Only take trade if profit target is at least 5 points
                    if (profitTarget >= 5)
                    {
                        EnterLong();
                        entryPrice = Close[0];
                        SetStopLoss(CalculationMode.Price, entryPrice - StopLoss);
                        SetProfitTarget(CalculationMode.Price, entryPrice + profitTarget);
                        
                        // Mark this pivot as used for trading
                        pivotsUsedForTrades.Add(pivot.Time);
                        
                        Print("LONG Entry at " + entryPrice + " - Stop: " + (entryPrice - StopLoss) + 
                              " - Target: " + (entryPrice + profitTarget));
                    }
                    else
                    {
                        Print("Skipping LONG trade - profit target too small: " + profitTarget);
                    }
                    
                    // Reset
                    consecutiveClosesAbove = 0;
                    crossedAVWAPindex = -1;
                }
                
                // Check for crossover from above (for short entry)
                if (Close[0] < avwapValue && Close[1] >= avwapValue)
                {
                    consecutiveClosesBelow = 1;
                    crossedAVWAPindex = index;
                    Print("First close below AVWAP (from " + pivot.Time + "): " + avwapValue);
                }
                else if (crossedAVWAPindex == index && Close[0] < avwapValue && consecutiveClosesBelow == 1)
                {
                    consecutiveClosesBelow = 2;
                    Print("Second consecutive close below AVWAP (from " + pivot.Time + "): " + avwapValue);
                    
                    // Find next lower AVWAP for profit target
                    double nextLowerAVWAP = FindNextAVWAP(avwapValue, false);
                    double profitTarget = Math.Min(MaxProfitTarget, Close[0] - nextLowerAVWAP);
                    
                    // Only take trade if profit target is at least 5 points
                    if (profitTarget >= 5)
                    {
                        EnterShort();
                        entryPrice = Close[0];
                        SetStopLoss(CalculationMode.Price, entryPrice + StopLoss);
                        SetProfitTarget(CalculationMode.Price, entryPrice - profitTarget);
                        
                        // Mark this pivot as used for trading
                        pivotsUsedForTrades.Add(pivot.Time);
                        
                        Print("SHORT Entry at " + entryPrice + " - Stop: " + (entryPrice + StopLoss) + 
                              " - Target: " + (entryPrice - profitTarget));
                    }
                    else
                    {
                        Print("Skipping SHORT trade - profit target too small: " + profitTarget);
                    }
                    
                    // Reset
                    consecutiveClosesBelow = 0;
                    crossedAVWAPindex = -1;
                }
            }
        }

        private double FindNextAVWAP(double currentAVWAP, bool findHigher)
        {
            double result = findHigher ? double.MaxValue : double.MinValue;
            bool found = false;
            
            // Check all active AVWAPs
            foreach (var pivotTime in pivotToIndicatorMap.Keys)
            {
                int index = pivotToIndicatorMap[pivotTime];
                if (index >= 0 && index < avwapPool.Length && avwapInUse[index])
                {
                    double avwapValue = avwapPool[index].Output[0];
                    
                    if (findHigher && avwapValue > currentAVWAP && avwapValue < result)
                    {
                        result = avwapValue;
                        found = true;
                    }
                    else if (!findHigher && avwapValue < currentAVWAP && avwapValue > result)
                    {
                        result = avwapValue;
                        found = true;
                    }
                }
            }
            
            // If no next AVWAP found, use default profit target
            if (!found)
            {
                result = findHigher ? currentAVWAP + MaxProfitTarget : currentAVWAP - MaxProfitTarget;
            }
            
            return result;
        }

        private void ManagePositions()
        {
            // Simple position management - using the stop loss and profit target set at entry
            // Additional logic could be added here if needed
        }

        // Class to store pivot candidate information
        private class PivotCandidate
        {
            public double Price { get; set; }
            public DateTime Time { get; set; }
            public bool IsHigh { get; set; }
            public double SignificanceScore { get; set; } // Raw score based on ATR
            public double AdjustedScore { get; set; } // Score with recency/trading bonuses
            public double MoveSize { get; set; }
            public int BarsInMove { get; set; }
        }

        #region Properties
        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Trade Size", GroupName = "Parameters", Order = 0)]
        public int TradeSize { get; set; }

        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Stop Loss (points)", GroupName = "Parameters", Order = 1)]
        public double StopLoss { get; set; }

        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Max Profit Target (points)", GroupName = "Parameters", Order = 2)]
        public double MaxProfitTarget { get; set; }
        
        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "ATR Period", GroupName = "Pivot Filters", Order = 0)]
        public int ATRPeriod { get; set; }
        
        [Range(0.1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "ATR Multiplier", GroupName = "Pivot Filters", Order = 1)]
        public double ATRMultiplier { get; set; }
        
        [Range(2, int.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Max Active Pivots", GroupName = "Pivot Filters", Order = 2)]
        public int MaxActivePivots { get; set; }
        
        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Min Bars For Swing", GroupName = "Pivot Filters", Order = 3)]
        public int MinBarsForSwing { get; set; }
        
        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Reassessment Interval", GroupName = "Pivot Filters", Order = 4)]
        public int ReassessmentInterval { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Session Start Time", Order = 0, GroupName = "Session")]
        public DateTime SessionStartTime { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Session End Time", Order = 1, GroupName = "Session")]
        public DateTime SessionEndTime { get; set; }

        [XmlIgnore]
        [Display(Name = "Significant Swing Highs", Order = 0, GroupName = "Plots")]
        public Series<double> SignificantSwingHighs => Values[0];

        [XmlIgnore]
        [Display(Name = "Significant Swing Lows", Order = 1, GroupName = "Plots")]
        public Series<double> SignificantSwingLows => Values[1];
        #endregion
    }
}
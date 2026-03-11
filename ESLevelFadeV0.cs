using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ESLevelFadeV0 : Strategy
    {
        private const int RthStart = 83000;
        private const int RthEnd = 150000;

        private const string PrevSessionHighLineTag = "ESLevelFadeV0.Level.PrevSessionHigh.Line";
        private const string PrevSessionLowLineTag = "ESLevelFadeV0.Level.PrevSessionLow.Line";
        private const string PreMarketHighLineTag = "ESLevelFadeV0.Level.PreMarketHigh.Line";
        private const string PreMarketLowLineTag = "ESLevelFadeV0.Level.PreMarketLow.Line";
        private const string PrevSessionHighLabelTag = "ESLevelFadeV0.Level.PrevSessionHigh.Label";
        private const string PrevSessionLowLabelTag = "ESLevelFadeV0.Level.PrevSessionLow.Label";
        private const string PreMarketHighLabelTag = "ESLevelFadeV0.Level.PreMarketHigh.Label";
        private const string PreMarketLowLabelTag = "ESLevelFadeV0.Level.PreMarketLow.Label";

        private ATR atr;

        // session levels
        private bool hasPrevSession;
        private double prevSessionHigh;
        private double prevSessionLow;
        private double dayHigh;
        private double dayLow;
        private double preMarketHigh;
        private double preMarketLow;

        // per-level state
        private Dictionary<string, int> tradesPerLevel;
        private Dictionary<string, bool> levelLocked;
        private Dictionary<string, double> lockAnchorPrice;

        // single working entry at a time (simple + deterministic)
        private Order workingEntry;
        private string workingSignal;
        private string workingLevel;
        private bool workingIsLong;
        private double workingLevelPrice;
        private bool cancelRequested;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ESLevelFadeV0";
                Description = "Locked v0: fade pre-market/prev-session levels with ATR arming, excursion unlock, and fixed 6/18 ticks";
                Calculate = Calculate.OnBarClose;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 20;
                DefaultQuantity = 1;

                AtrPeriod = 14;
                ArmDistanceAtr = 1.5;
                ReentryExcursionAtr = 2.0;
                StopTicks = 6;
                TargetTicks = 18;
                MaxTradesPerLevelPerSession = 1;
                ClusterDistanceTicks = 18;
                ShowSessionLevelsOnChart = true;
                EnableLogs = true;
            }
            else if (State == State.Configure)
            {
                atr = ATR(AtrPeriod);
            }
            else if (State == State.DataLoaded)
            {
                tradesPerLevel = new Dictionary<string, int>();
                levelLocked = new Dictionary<string, bool>();
                lockAnchorPrice = new Dictionary<string, double>();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            if (Bars.IsFirstBarOfSession)
                ResetForNewSession();

            // update session highs/lows
            dayHigh = Math.Max(dayHigh, High[0]);
            dayLow = Math.Min(dayLow, Low[0]);

            int t = ToTime(Time[0]);

            // pre-market levels freeze at 08:29:59
            if (t <= 82959)
            {
                preMarketHigh = Math.Max(preMarketHigh, High[0]);
                preMarketLow = Math.Min(preMarketLow, Low[0]);
            }

            UpdateLevelOverlays();

            // only RTH trading
            if (t < RthStart || t > RthEnd)
            {
                CancelWorkingEntry("OUTSIDE_RTH");
                return;
            }

            // must have previous session levels
            if (!hasPrevSession)
                return;

            double atrVal = atr[0];
            if (double.IsNaN(atrVal) || atrVal <= 0)
                return;

            // unlock levels after excursion
            UpdateExcursionUnlocks(atrVal);

            // if in position, just manage via attached stop/target
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            // determine candidates within arming band
            double armDist = ArmDistanceAtr * atrVal;
            var levels = new List<(string Name, double Price)>
            {
                ("PrevSessionHigh", prevSessionHigh),
                ("PrevSessionLow", prevSessionLow),
                ("PreMarketHigh", preMarketHigh),
                ("PreMarketLow", preMarketLow)
            };

            var longCandidates = new List<(string Name, double Price)>();
            var shortCandidates = new List<(string Name, double Price)>();

            foreach (var lv in levels)
            {
                if (Math.Abs(Close[0] - lv.Price) > armDist)
                    continue;

                if (IsLevelBlocked(lv.Name))
                    continue;

                // approach-sensitive side, based on prior-side + actual touch this bar
                bool touched = Low[0] <= lv.Price && High[0] >= lv.Price;
                if (!touched)
                    continue;

                bool approachFromAbove = Close[1] > lv.Price || (Close[1] == lv.Price && Close[2] > lv.Price);
                bool approachFromBelow = Close[1] < lv.Price || (Close[1] == lv.Price && Close[2] < lv.Price);

                if (approachFromAbove)
                    longCandidates.Add(lv);   // support behavior
                else if (approachFromBelow)
                    shortCandidates.Add(lv);  // resistance behavior
            }

            // choose one conservative level based on side + clustering
            string pickLevel = null;
            double pickPrice = 0;
            bool pickLong = false;
            double clusterDist = Math.Max(TickSize, ClusterDistanceTicks * TickSize);

            if (shortCandidates.Count > 0)
            {
                // seed = nearest short candidate to current price
                var seed = shortCandidates[0];
                double bestSeedDist = Math.Abs(Close[0] - seed.Price);
                for (int i = 1; i < shortCandidates.Count; i++)
                {
                    double d = Math.Abs(Close[0] - shortCandidates[i].Price);
                    if (d < bestSeedDist) { bestSeedDist = d; seed = shortCandidates[i]; }
                }

                // cluster around seed, then conservative short = highest level in cluster
                var best = seed;
                for (int i = 0; i < shortCandidates.Count; i++)
                {
                    if (Math.Abs(shortCandidates[i].Price - seed.Price) <= clusterDist && shortCandidates[i].Price > best.Price)
                        best = shortCandidates[i];
                }

                pickLevel = best.Name;
                pickPrice = best.Price;
                pickLong = false;
            }
            else if (longCandidates.Count > 0)
            {
                // seed = nearest long candidate to current price
                var seed = longCandidates[0];
                double bestSeedDist = Math.Abs(Close[0] - seed.Price);
                for (int i = 1; i < longCandidates.Count; i++)
                {
                    double d = Math.Abs(Close[0] - longCandidates[i].Price);
                    if (d < bestSeedDist) { bestSeedDist = d; seed = longCandidates[i]; }
                }

                // cluster around seed, then conservative long = lowest level in cluster
                var best = seed;
                for (int i = 0; i < longCandidates.Count; i++)
                {
                    if (Math.Abs(longCandidates[i].Price - seed.Price) <= clusterDist && longCandidates[i].Price < best.Price)
                        best = longCandidates[i];
                }

                pickLevel = best.Name;
                pickPrice = best.Price;
                pickLong = true;
            }

            // no valid setup in range -> cancel stale working order
            if (pickLevel == null)
            {
                CancelWorkingEntry("MOVED_AWAY");
                return;
            }

            // if working order is for different level/side, replace it
            if (!string.IsNullOrEmpty(workingSignal) && (workingLevel != pickLevel || workingIsLong != pickLong))
                CancelWorkingEntry("REPLACE_BETTER_LEVEL");

            // hard duplicate guard: signal presence means an entry intent/order already exists
            if (string.IsNullOrEmpty(workingSignal) && !cancelRequested)
                PlaceEntry(pickLong, pickLevel, pickPrice);
        }

        private void PlaceEntry(bool isLong, string levelName, double levelPrice)
        {
            int stopTicks = Math.Max(1, StopTicks);
            int targetTicks = Math.Max(stopTicks + 1, TargetTicks);

            string side = isLong ? "L" : "S";
            string signal = $"{side}-{levelName}-{CurrentBar}";

            SetStopLoss(signal, CalculationMode.Ticks, stopTicks, false);
            SetProfitTarget(signal, CalculationMode.Ticks, targetTicks);

            workingEntry = null;
            workingSignal = signal;
            workingLevel = levelName;
            workingIsLong = isLong;
            workingLevelPrice = levelPrice;
            cancelRequested = false;

            if (isLong)
                EnterLongLimit(DefaultQuantity, levelPrice, signal);
            else
                EnterShortLimit(DefaultQuantity, levelPrice, signal);

            if (EnableLogs)
                Print($"[{Time[0]:MM-dd HH:mm}] ARM {side} level={levelName} px={levelPrice:F2} stop={stopTicks} target={targetTicks}");
        }

        private void CancelWorkingEntry(string reason)
        {
            if (string.IsNullOrEmpty(workingSignal))
                return;

            cancelRequested = true;

            if (EnableLogs)
                Print($"[{Time[0]:MM-dd HH:mm}] CANCEL_REQ signal={workingSignal} reason={reason}");

            if (workingEntry != null && (workingEntry.OrderState == OrderState.Working || workingEntry.OrderState == OrderState.Accepted || workingEntry.OrderState == OrderState.Submitted))
                CancelOrder(workingEntry);
        }

        private void ResetForNewSession()
        {
            // carry prior session
            if (CurrentBar > BarsRequiredToTrade)
            {
                hasPrevSession = true;
                prevSessionHigh = dayHigh;
                prevSessionLow = dayLow;
            }

            dayHigh = High[0];
            dayLow = Low[0];
            preMarketHigh = High[0];
            preMarketLow = Low[0];

            tradesPerLevel.Clear();
            levelLocked.Clear();
            lockAnchorPrice.Clear();

            CancelWorkingEntry("NEW_SESSION");

            // hard reset local handles at session boundary
            workingEntry = null;
            workingSignal = null;
            workingLevel = null;
            workingLevelPrice = 0;
            cancelRequested = false;
        }

        private bool IsLevelBlocked(string levelName)
        {
            int used = tradesPerLevel.ContainsKey(levelName) ? tradesPerLevel[levelName] : 0;
            if (used >= MaxTradesPerLevelPerSession)
                return true;

            if (levelLocked.ContainsKey(levelName) && levelLocked[levelName])
                return true;

            return false;
        }

        private void UpdateLevelOverlays()
        {
            if (ChartControl == null || !ShowSessionLevelsOnChart)
            {
                RemoveLevelOverlays();
                return;
            }

            var prevHighBrush = new SolidColorBrush(Color.FromArgb(235, 0, 120, 255));   // electric blue
            var prevLowBrush = new SolidColorBrush(Color.FromArgb(235, 0, 210, 140));    // vivid green
            var preHighBrush = new SolidColorBrush(Color.FromArgb(235, 255, 165, 0));    // bright orange
            var preLowBrush = new SolidColorBrush(Color.FromArgb(235, 190, 90, 255));    // neon purple

            if (hasPrevSession)
            {
                Draw.HorizontalLine(this, PrevSessionHighLineTag, prevSessionHigh, prevHighBrush);
                Draw.HorizontalLine(this, PrevSessionLowLineTag, prevSessionLow, prevLowBrush);
                Draw.Text(this, PrevSessionHighLabelTag, "Prev High", 0, prevSessionHigh + (2 * TickSize), prevHighBrush);
                Draw.Text(this, PrevSessionLowLabelTag, "Prev Low", 0, prevSessionLow - (2 * TickSize), prevLowBrush);
            }
            else
            {
                RemoveDrawObject(PrevSessionHighLineTag);
                RemoveDrawObject(PrevSessionLowLineTag);
                RemoveDrawObject(PrevSessionHighLabelTag);
                RemoveDrawObject(PrevSessionLowLabelTag);
            }

            Draw.HorizontalLine(this, PreMarketHighLineTag, preMarketHigh, preHighBrush);
            Draw.HorizontalLine(this, PreMarketLowLineTag, preMarketLow, preLowBrush);
            Draw.Text(this, PreMarketHighLabelTag, "PM High", 0, preMarketHigh + (2 * TickSize), preHighBrush);
            Draw.Text(this, PreMarketLowLabelTag, "PM Low", 0, preMarketLow - (2 * TickSize), preLowBrush);
        }

        private void RemoveLevelOverlays()
        {
            RemoveDrawObject(PrevSessionHighLineTag);
            RemoveDrawObject(PrevSessionLowLineTag);
            RemoveDrawObject(PreMarketHighLineTag);
            RemoveDrawObject(PreMarketLowLineTag);
            RemoveDrawObject(PrevSessionHighLabelTag);
            RemoveDrawObject(PrevSessionLowLabelTag);
            RemoveDrawObject(PreMarketHighLabelTag);
            RemoveDrawObject(PreMarketLowLabelTag);
        }

        private void UpdateExcursionUnlocks(double atrVal)
        {
            if (atrVal <= 0)
                return;

            double need = ReentryExcursionAtr * atrVal;
            var keys = new List<string>(levelLocked.Keys);
            foreach (var k in keys)
            {
                if (!levelLocked[k])
                    continue;
                if (!lockAnchorPrice.ContainsKey(k))
                    continue;

                double anchor = lockAnchorPrice[k];
                if (Math.Abs(Close[0] - anchor) >= need)
                {
                    levelLocked[k] = false;
                    if (EnableLogs)
                        Print($"[{Time[0]:MM-dd HH:mm}] UNLOCK level={k} excursion={Math.Abs(Close[0]-anchor):F2} need={need:F2}");
                }
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (string.IsNullOrEmpty(workingSignal) || order == null)
                return;

            if (order.Name == workingSignal)
            {
                workingEntry = order;

                if (cancelRequested && (orderState == OrderState.Working || orderState == OrderState.Accepted || orderState == OrderState.Submitted))
                    CancelOrder(order);

                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected || orderState == OrderState.Filled)
                {
                    if (orderState != OrderState.Filled)
                    {
                        workingEntry = null;
                        workingSignal = null;
                        workingLevel = null;
                        workingLevelPrice = 0;
                        cancelRequested = false;
                    }
                }
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            if (string.IsNullOrEmpty(workingSignal))
                return;

            if (execution.Order.Name != workingSignal)
                return;

            // entry fill -> count and lock this level until excursion unlock
            string level = workingLevel;
            if (!string.IsNullOrEmpty(level))
            {
                int used = tradesPerLevel.ContainsKey(level) ? tradesPerLevel[level] : 0;
                tradesPerLevel[level] = used + 1;
                levelLocked[level] = true;
                lockAnchorPrice[level] = workingLevelPrice;

                if (EnableLogs)
                    Print($"[{Time[0]:MM-dd HH:mm}] FILL signal={workingSignal} level={level} fill={price:F2} count={tradesPerLevel[level]}");
            }

            // clear working order refs
            workingEntry = null;
            workingSignal = null;
            workingLevel = null;
            workingLevelPrice = 0;
            cancelRequested = false;
        }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "ATR Period", GroupName = "Parameters", Order = 1)]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "Arm Distance ATR", GroupName = "Parameters", Order = 2)]
        public double ArmDistanceAtr { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 6.0)]
        [Display(Name = "Re-entry Excursion ATR", GroupName = "Parameters", Order = 3)]
        public double ReentryExcursionAtr { get; set; }

        [NinjaScriptProperty]
        [Range(1, 40)]
        [Display(Name = "Stop Ticks", GroupName = "Risk", Order = 4)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(2, 200)]
        [Display(Name = "Target Ticks", GroupName = "Risk", Order = 5)]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max Trades Per Level Per Session", GroupName = "Risk", Order = 6)]
        public int MaxTradesPerLevelPerSession { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Cluster Distance Ticks", GroupName = "Parameters", Order = 7)]
        public int ClusterDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Session Levels On Chart", GroupName = "Visual", Order = 8)]
        public bool ShowSessionLevelsOnChart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Logs", GroupName = "Diagnostics", Order = 99)]
        public bool EnableLogs { get; set; }
    }
}

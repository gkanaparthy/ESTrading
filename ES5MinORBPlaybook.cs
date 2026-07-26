#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ES5MinORBPlaybook : Strategy
    {
        private double orbHigh;
        private double orbLow;
        private double orbSize;
        private bool orbLocked;
        private bool tradePlacedToday;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "5-minute ES ORB strategy based on external playbook rules, with Tuesday longs left enabled.";
                Name = "ES5MinORBPlaybook";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 0;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 20;
                IncludeCommission = true;
                IsInstantiatedOnEachOptimizationIteration = true;

                Contracts = 1;
                ProfitTargetOrbMultiple = 0.5;
                StopLossOrbMultiple = 1.0;
                MaxDollarLoss = 700;
                MaxOrbPercent = 0.55;
                AllowMondayLong = true;
                AllowMondayShort = true;
                AllowTuesdayLong = true;
                AllowTuesdayShort = true;
                AllowWednesdayLong = true;
                AllowWednesdayShort = true;
                AllowThursdayLong = true;
                AllowThursdayShort = true;
                AllowFridayLong = true;
                AllowFridayShort = true;
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || CurrentBar < BarsRequiredToTrade)
                return;

            if (Bars.IsFirstBarOfSession)
            {
                orbHigh = 0;
                orbLow = 0;
                orbSize = 0;
                orbLocked = false;
                tradePlacedToday = false;
            }

            int t = ToTime(Time[0]);

            if (!orbLocked && t >= 93500)
            {
                orbHigh = High[1];
                orbLow = Low[1];
                orbSize = orbHigh - orbLow;
                orbLocked = true;
            }

            if (!orbLocked || tradePlacedToday || Position.MarketPosition != MarketPosition.Flat)
                return;

            if (t < 94000 || t > 160000)
                return;

            if (orbLow <= 0 || orbHigh <= orbLow)
                return;

            double orbPercent = (orbSize / Close[0]) * 100.0;
            if (orbPercent > MaxOrbPercent)
                return;

            double stopPoints = orbSize * StopLossOrbMultiple;
            double targetPoints = orbSize * ProfitTargetOrbMultiple;
            double maxLossPointsPerContract = MaxDollarLoss / (Contracts * 50.0);
            double effectiveStopPoints = Math.Min(stopPoints, maxLossPointsPerContract);

            if (effectiveStopPoints <= 0 || targetPoints <= 0)
                return;

            if (Close[0] > orbHigh && IsLongAllowedForDay(Time[0].DayOfWeek))
            {
                double entryPrice = Close[0];
                double stopPrice = entryPrice - effectiveStopPoints;
                double targetPrice = entryPrice + targetPoints;

                SetStopLoss("ORBLong", CalculationMode.Price, stopPrice, false);
                SetProfitTarget("ORBLong", CalculationMode.Price, targetPrice);
                EnterLong(Contracts, "ORBLong");
                tradePlacedToday = true;
            }
            else if (Close[0] < orbLow && IsShortAllowedForDay(Time[0].DayOfWeek))
            {
                double entryPrice = Close[0];
                double stopPrice = entryPrice + effectiveStopPoints;
                double targetPrice = entryPrice - targetPoints;

                SetStopLoss("ORBShort", CalculationMode.Price, stopPrice, false);
                SetProfitTarget("ORBShort", CalculationMode.Price, targetPrice);
                EnterShort(Contracts, "ORBShort");
                tradePlacedToday = true;
            }
        }

        private bool IsLongAllowedForDay(DayOfWeek day)
        {
            switch (day)
            {
                case DayOfWeek.Monday: return AllowMondayLong;
                case DayOfWeek.Tuesday: return AllowTuesdayLong;
                case DayOfWeek.Wednesday: return AllowWednesdayLong;
                case DayOfWeek.Thursday: return AllowThursdayLong;
                case DayOfWeek.Friday: return AllowFridayLong;
                default: return false;
            }
        }

        private bool IsShortAllowedForDay(DayOfWeek day)
        {
            switch (day)
            {
                case DayOfWeek.Monday: return AllowMondayShort;
                case DayOfWeek.Tuesday: return AllowTuesdayShort;
                case DayOfWeek.Wednesday: return AllowWednesdayShort;
                case DayOfWeek.Thursday: return AllowThursdayShort;
                case DayOfWeek.Friday: return AllowFridayShort;
                default: return false;
            }
        }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Contracts", Order = 1, GroupName = "Parameters")]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "ProfitTargetOrbMultiple", Order = 2, GroupName = "Parameters")]
        public double ProfitTargetOrbMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "StopLossOrbMultiple", Order = 3, GroupName = "Parameters")]
        public double StopLossOrbMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(50, 5000)]
        [Display(Name = "MaxDollarLoss", Order = 4, GroupName = "Parameters")]
        public double MaxDollarLoss { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 5.0)]
        [Display(Name = "MaxOrbPercent", Order = 5, GroupName = "Parameters")]
        public double MaxOrbPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMondayLong", Order = 10, GroupName = "Day Filters")]
        public bool AllowMondayLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowMondayShort", Order = 11, GroupName = "Day Filters")]
        public bool AllowMondayShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowTuesdayLong", Order = 12, GroupName = "Day Filters")]
        public bool AllowTuesdayLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowTuesdayShort", Order = 13, GroupName = "Day Filters")]
        public bool AllowTuesdayShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowWednesdayLong", Order = 14, GroupName = "Day Filters")]
        public bool AllowWednesdayLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowWednesdayShort", Order = 15, GroupName = "Day Filters")]
        public bool AllowWednesdayShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowThursdayLong", Order = 16, GroupName = "Day Filters")]
        public bool AllowThursdayLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowThursdayShort", Order = 17, GroupName = "Day Filters")]
        public bool AllowThursdayShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowFridayLong", Order = 18, GroupName = "Day Filters")]
        public bool AllowFridayLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AllowFridayShort", Order = 19, GroupName = "Day Filters")]
        public bool AllowFridayShort { get; set; }
    }
}

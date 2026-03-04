using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ESVwapLite : Strategy
    {
        // ES RTH (CME CT)
        private const int CmeRthStart = 83000;
        private const int CmeRthEnd = 150000;

        private ATR atr;
        private TimeZoneInfo cmeTimeZone;
        private TimeZoneInfo barTimeZone;

        private int dailyTrades;
        private int cooldownRemaining;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ESVwapLite";
                Description = "Simple ES Session VWAP touch-reject strategy with minimal gating.";

                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 2;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 30;

                AtrPeriod = 14;
                MinAtrForEntry = 0.8;
                MaxAtrForEntry = 12.0;

                UseExtendedHours = false;
                MaxTradesPerDay = 6;
                SignalCooldownBars = 2;

                StopLookbackBars = 4;
                MinStopTicks = 8;
                MaxStopPoints = 5.0;
                RiskReward = 2.0;
                MaxRiskPerTradeDollars = 400.0;

                EnableLogs = true;
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(AtrPeriod);
                InitializeTimeZones();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            if (Bars.IsFirstBarOfSession)
            {
                dailyTrades = 0;
                cooldownRemaining = 0;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (cooldownRemaining > 0)
                cooldownRemaining--;

            if (cooldownRemaining > 0)
                return;

            if (dailyTrades >= MaxTradesPerDay)
                return;

            int nowCme = GetCmeTimeInt(Time[0]);
            if (!IsInTradeWindow(nowCme))
                return;

            if (double.IsNaN(atr[0]) || atr[0] < MinAtrForEntry || atr[0] > MaxAtrForEntry)
                return;

            double sessionVwap = GetSessionVwapValue();
            if (double.IsNaN(sessionVwap))
                return;

            bool touch = High[0] >= sessionVwap && Low[0] <= sessionVwap;
            if (!touch)
                return;

            bool longConfirm = Close[0] > sessionVwap && Close[0] > Open[0];
            bool shortConfirm = Close[0] < sessionVwap && Close[0] < Open[0];

            if (longConfirm && shortConfirm)
                return;

            if (longConfirm)
            {
                TrySubmitDirectionalEntry(true, sessionVwap);
                return;
            }

            if (shortConfirm)
            {
                TrySubmitDirectionalEntry(false, sessionVwap);
                return;
            }
        }

        private bool IsInTradeWindow(int cmeTime)
        {
            if (UseExtendedHours)
                return true;

            return cmeTime >= CmeRthStart && cmeTime <= CmeRthEnd;
        }

        private void TrySubmitDirectionalEntry(bool isLong, double anchorPrice)
        {
            int stopTicks = ComputeStopTicks(isLong);
            int quantity = DefaultQuantity;

            if (stopTicks <= 0 || quantity <= 0)
                return;

            if (!ApplyRiskCap(ref quantity, stopTicks))
            {
                if (EnableLogs)
                {
                    PrintWithContext("ENTRY_SKIP reason=RiskCap stopTicks=" + stopTicks +
                                     " qty=" + quantity +
                                     " maxRisk=" + MaxRiskPerTradeDollars.ToString("F0"));
                }
                return;
            }

            int targetTicks = Math.Max(stopTicks + 1, (int)Math.Round(stopTicks * RiskReward));
            string signal = (isLong ? "L" : "S") + "-" + Time[0].ToString("yyyyMMddHHmmss");

            SetStopLoss(signal, CalculationMode.Ticks, stopTicks, false);
            SetProfitTarget(signal, CalculationMode.Ticks, targetTicks);

            if (isLong)
                EnterLong(quantity, signal);
            else
                EnterShort(quantity, signal);

            dailyTrades++;
            cooldownRemaining = Math.Max(cooldownRemaining, SignalCooldownBars);

            if (EnableLogs)
            {
                PrintWithContext("ENTRY side=" + (isLong ? "LONG" : "SHORT") +
                                 " vwap=" + anchorPrice.ToString("F2") +
                                 " close=" + Close[0].ToString("F2") +
                                 " stopTicks=" + stopTicks +
                                 " targetTicks=" + targetTicks +
                                 " qty=" + quantity +
                                 " dailyTrades=" + dailyTrades);
            }
        }

        private int ComputeStopTicks(bool isLong)
        {
            int lookback = Math.Min(StopLookbackBars, CurrentBar);
            double stopRef = isLong ? Low[0] : High[0];

            for (int i = 1; i <= lookback; i++)
            {
                stopRef = isLong ? Math.Min(stopRef, Low[i]) : Math.Max(stopRef, High[i]);
            }

            double distPoints = isLong ? (Close[0] - stopRef) : (stopRef - Close[0]);
            distPoints = Math.Max(TickSize, Math.Min(MaxStopPoints, distPoints));

            int ticks = (int)Math.Ceiling(distPoints / TickSize);
            return Math.Max(MinStopTicks, ticks);
        }

        private bool ApplyRiskCap(ref int quantity, int stopTicks)
        {
            if (MaxRiskPerTradeDollars <= 0)
                return true;

            double tickRisk = TickSize * Instrument.MasterInstrument.PointValue;
            if (tickRisk <= 0)
                return false;

            double perContractRisk = stopTicks * tickRisk;
            if (perContractRisk <= 0)
                return false;

            int maxQty = (int)Math.Floor(MaxRiskPerTradeDollars / perContractRisk);
            if (maxQty < 1)
                return false;

            quantity = Math.Min(quantity, maxQty);
            return quantity >= 1;
        }

        private double GetSessionVwapValue()
        {
            double v = VWAP1(BarsArray[0],
                new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                true, true, true).Output[0];

            return double.IsNaN(v)
                ? double.NaN
                : Instrument.MasterInstrument.RoundToTickSize(v);
        }

        private void InitializeTimeZones()
        {
            cmeTimeZone = ResolveTimeZone("Central Standard Time", "America/Chicago", TimeZoneInfo.Local);
            barTimeZone = Bars?.TradingHours?.TimeZoneInfo ?? cmeTimeZone;
        }

        private static TimeZoneInfo ResolveTimeZone(string primaryId, string fallbackId, TimeZoneInfo defaultZone)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(primaryId);
            }
            catch
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(fallbackId);
                }
                catch
                {
                    return defaultZone;
                }
            }
        }

        private DateTime GetCmeTime(DateTime barTime)
        {
            if (cmeTimeZone == null)
                return barTime;

            TimeZoneInfo source = barTimeZone ?? TimeZoneInfo.Local;
            DateTime unspecified = DateTime.SpecifyKind(barTime, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTime(unspecified, source, cmeTimeZone);
        }

        private int GetCmeTimeInt(DateTime barTime)
        {
            return ToTime(GetCmeTime(barTime));
        }

        private void PrintWithContext(string message)
        {
            if (CurrentBar < 0 || Bars == null || Time == null || Time.Count == 0)
            {
                Print(message);
                return;
            }

            DateTime barCmeTime = GetCmeTime(Time[0]);
            Print("barTimeCME=" + barCmeTime.ToString("yyyy-MM-dd HH:mm:ss") + " " + message);
        }

        #region Parameters

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "ATR Period", GroupName = "Indicators", Order = 1)]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Min ATR For Entry", GroupName = "Regime", Order = 2)]
        public double MinAtrForEntry { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 40.0)]
        [Display(Name = "Max ATR For Entry", GroupName = "Regime", Order = 3)]
        public double MaxAtrForEntry { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Extended Hours", GroupName = "Session", Order = 4)]
        public bool UseExtendedHours { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max Trades Per Day", GroupName = "Risk", Order = 5)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Signal Cooldown Bars", GroupName = "Entry", Order = 6)]
        public int SignalCooldownBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Stop Lookback Bars", GroupName = "Risk", Order = 7)]
        public int StopLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Min Stop Ticks", GroupName = "Risk", Order = 8)]
        public int MinStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10.0)]
        [Display(Name = "Max Stop Points", GroupName = "Risk", Order = 9)]
        public double MaxStopPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 5.0)]
        [Display(Name = "Risk Reward", GroupName = "Risk", Order = 10)]
        public double RiskReward { get; set; }

        [NinjaScriptProperty]
        [Range(50.0, 5000.0)]
        [Display(Name = "Max Risk Per Trade ($)", GroupName = "Risk", Order = 11)]
        public double MaxRiskPerTradeDollars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Logs", GroupName = "Diagnostics", Order = 12)]
        public bool EnableLogs { get; set; }

        #endregion
    }
}

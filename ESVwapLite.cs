using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ESVwapLite : Strategy
    {
        private enum AnchorKind
        {
            SessionVWAP,
            WeeklyVWAP,
            HOD,
            LOD,
            ManualLong,
            ManualShort
        }

        private struct AnchorPoint
        {
            public AnchorKind Kind;
            public double Price;
            public int BarIndex;
            public DateTime AnchorTime;
        }

        private const int CmeRthStart = 83000;
        private const int CmeRthEnd = 150000;
        private const string RelevantAnchorLabelTag = "ESVwapLite.RelevantAnchor.Label";

        private ATR atr;
        private TimeZoneInfo cmeTimeZone;
        private TimeZoneInfo barTimeZone;
        private AVWAP2 manualLongAvwap2;
        private AVWAP2 manualShortAvwap2;
        private AVWAP2 relevantAnchorAvwap2;
        private int relevantAnchorBarIndex = -1;

        // stored DateTimes for anchors that may be > 254 bars ago
        private DateTime sessionStartTime = Core.Globals.MinDate;
        private DateTime dayHighTime = Core.Globals.MinDate;
        private DateTime dayLowTime = Core.Globals.MinDate;

        // manual anchor hotkeys/click capture
        private bool manualHotkeysHooked;
        private Chart chartWindow;
        private readonly object manualAnchorLock = new object();
        private bool pendingSetManualLong;
        private bool pendingSetManualShort;
        private bool pendingClearManualAnchors;
        private int pendingLongBarIndex = -1;
        private int pendingShortBarIndex = -1;
        private int lastClickedBarIndex = -1;

        // weekly avwap running accumulator
        private int wtdAnchorBarIndex = -1;
        private double wtdAnchorOpenPrice;
        private bool wtdAnchorSet;
        private int wtdAnchorWeekYear = -1;
        private DateTime wtdAnchorTime = DateTime.MinValue;
        private double wtdPV;
        private double wtdVSum;
        private bool wtdSeededThisBar;
        private int wtdDeferredWeekYear = -1;

        // daily state
        private DateTime sessionDate = Core.Globals.MinDate;
        private int sessionStartBarIndex = -1;
        private double dayHigh;
        private double dayLow;
        private int dayHighBarIndex = -1;
        private int dayLowBarIndex = -1;
        private int dailyTrades;
        private Dictionary<AnchorKind, int> anchorCooldowns = new Dictionary<AnchorKind, int>();
        private Dictionary<string, int> anchorUsageCounts = new Dictionary<string, int>();
        private Dictionary<string, double> zoneLockPrice = new Dictionary<string, double>();
        private Dictionary<string, bool> zoneNeedsExcursion = new Dictionary<string, bool>();
        private Dictionary<string, int> zoneTradeCount = new Dictionary<string, int>();

        // setup state
        private bool setupActive;
        private bool setupIsLong;
        private double setupAnchorPrice;
        private int setupBar;
        private AnchorKind setupAnchorKind;
        private string setupZoneId;

        // break-even management
        private string activeSignalName;
        private int activeRiskTicks;
        private bool breakEvenMoved;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ESVwapLite";
                Description = "Minimal-gate ES anchor retest strategy (Session/Weekly VWAP + HOD/LOD + manual anchors).";

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
                BarsRequiredToTrade = 50;

                AtrPeriod = 14;
                UseExtendedHours = false;
                MaxTradesPerDay = 1000;
                SignalCooldownBars = 2;
                MinAtrForEntry = 0.8;
                MaxAtrForEntry = 10.0;

                // risk: recent peak/trough distance, capped/floored
                StopSwingLookbackBars = 8;
                MinStopTicks = 8;
                MaxStopPoints = 5.0;
                RiskRewardMultiple = 3.0;
                MaxRiskPerTradeDollars = 400.0;
                EnableBreakEven = true;
                BreakEvenTriggerR = 1.5;
                BreakEvenPlusTicks = 1;
                EnableRetradeExcursionFilter = true;
                RetradeExcursionAtrMultiple = 1.0;
                MaxTradesPerZoneCycle = 2;
                ConfirmBodyAtrMultiple = 0.2;

                // touch/zone behavior
                TouchToleranceTicks = 2;
                RecentBarLookback = 5;
                SetupMaxBars = 5;
                ExtremeEstablishAtrMultiple = 2.0;

                EnableSessionVwapAnchor = true;
                EnableWeeklyVwapAnchor = true;
                EnableHodAnchor = true;
                EnableLodAnchor = true;
                ShowSessionVwapOnChart = true;
                ShowWeeklyVwapOnChart = true;
                ShowRelevantAnchorsOnChart = true;
                UseManualAnchors = true;
                EnableManualAnchorHotkeys = true;
                EnableLogs = true;

                ManualLongAnchorFrom = Core.Globals.MinDate;
                ManualShortAnchorFrom = Core.Globals.MinDate;
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(AtrPeriod);
                InitializeTimeZones();
                RebuildManualAvwapAnchors();
            }
            else if (State == State.Terminated)
            {
                UnhookManualAnchorHotkeys();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            EnsureManualAnchorHotkeysHooked();
            ProcessPendingManualAnchorActions();
            ResetSessionStateIfNeeded();

            wtdSeededThisBar = false;
            UpdateWtdAnchorIfNeeded();
            UpdateWtdRunningAccumulator();
            UpdateDailyExtremes();

            // Build anchors and update chart overlay — always, regardless of position
            List<AnchorPoint> anchors = BuildAnchors();
            UpdateRelevantAnchorOverlays(anchors);
            DecrementAnchorCooldowns();

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                ManageBreakEven();
                return;
            }

            // reset active-trade state when flat
            activeSignalName = null;
            activeRiskTicks = 0;
            breakEvenMoved = false;

            if (anchors.Count == 0)
                return;

            double tol = TouchToleranceTicks * TickSize;
            double atrVal = atr[0];

            // Build touched cluster and select one stable conservative anchor
            var touched = new List<AnchorPoint>();
            for (int i = 0; i < anchors.Count; i++)
            {
                if (Low[0] <= anchors[i].Price + tol && High[0] >= anchors[i].Price - tol)
                    touched.Add(anchors[i]);
            }

            AnchorPoint selected = anchors[0];
            bool touch = touched.Count > 0;
            bool inferredLong = false;
            string zoneId = "";
            if (touch)
            {
                AnchorPoint seed = touched[0];
                double bestDist = Math.Abs(Close[0] - seed.Price);
                for (int i = 1; i < touched.Count; i++)
                {
                    double d = Math.Abs(Close[0] - touched[i].Price);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        seed = touched[i];
                    }
                }

                double clusterBand = Math.Max(TickSize, atrVal); // overlap band: 1 ATR
                var cluster = new List<AnchorPoint>();
                for (int i = 0; i < anchors.Count; i++)
                {
                    if (Math.Abs(anchors[i].Price - seed.Price) <= clusterBand)
                        cluster.Add(anchors[i]);
                }

                bool fromAbove = Close[1] > seed.Price;
                bool fromBelow = Close[1] < seed.Price;
                inferredLong = fromAbove || (!fromBelow && Close[0] >= seed.Price);

                selected = cluster[0];
                for (int i = 1; i < cluster.Count; i++)
                {
                    if (inferredLong)
                    {
                        // conservative support
                        if (cluster[i].Price < selected.Price)
                            selected = cluster[i];
                    }
                    else
                    {
                        // conservative resistance
                        if (cluster[i].Price > selected.Price)
                            selected = cluster[i];
                    }
                }

                double zoneCenter = 0;
                for (int i = 0; i < cluster.Count; i++)
                    zoneCenter += cluster[i].Price;
                zoneCenter /= Math.Max(1, cluster.Count);

                double bucketSize = Math.Max(4 * TickSize, atrVal * 0.5);
                long bucket = (long)Math.Round(zoneCenter / bucketSize);
                zoneId = "Z" + bucket;
            }

            double avwap = selected.Price;

            UpdateZoneExcursionState(atrVal);

            // confirmation stage: once setup is armed, wait for first directional candle (no anchor-switch cancellation)
            if (setupActive && setupBar < CurrentBar)
            {
                double liveAvwap = setupAnchorPrice;
                for (int i = 0; i < anchors.Count; i++)
                {
                    if (anchors[i].Kind == setupAnchorKind)
                    {
                        liveAvwap = anchors[i].Price;
                        break;
                    }
                }

                if ((CurrentBar - setupBar) > SetupMaxBars)
                {
                    if (EnableLogs)
                        PrintWithContext("SETUP_CANCELLED timeout kind=" + setupAnchorKind + " side=" + (setupIsLong ? "L" : "S"));
                    setupActive = false;
                    setupZoneId = null;
                }
                else if (setupIsLong)
                {
                    // keep waiting while close is below anchor; do not cancel until timeout
                    double body = Math.Abs(Close[0] - Open[0]);
                    bool bodyOk = body >= (ConfirmBodyAtrMultiple * atrVal);
                    if (Close[0] > Open[0] && Close[0] >= liveAvwap && bodyOk)
                    {
                        TrySubmitEntry(true, liveAvwap, setupAnchorKind, setupZoneId);
                        setupActive = false;
                        setupZoneId = null;
                        return;
                    }
                }
                else
                {
                    // keep waiting while close is above anchor; do not cancel until timeout
                    double body = Math.Abs(Close[0] - Open[0]);
                    bool bodyOk = body >= (ConfirmBodyAtrMultiple * atrVal);
                    if (Close[0] < Open[0] && Close[0] <= liveAvwap && bodyOk)
                    {
                        TrySubmitEntry(false, liveAvwap, setupAnchorKind, setupZoneId);
                        setupActive = false;
                        setupZoneId = null;
                        return;
                    }
                }
            }

            // Compute state flags
            bool inCooldown  = anchorCooldowns.ContainsKey(selected.Kind) && anchorCooldowns[selected.Kind] > 0;
            bool tradeCapHit = dailyTrades >= MaxTradesPerDay;
            bool atrOk       = atrVal >= MinAtrForEntry && atrVal <= MaxAtrForEntry;

            if (EnableLogs)
            {
                int cd = anchorCooldowns.ContainsKey(selected.Kind) ? anchorCooldowns[selected.Kind] : 0;
                PrintWithContext(string.Format(
                    "BAR anchor={0} avwap={1} atr={2:F2} touch={3} setup={4} cd={5} trades={6} atrOk={7}",
                    selected.Kind, avwap.ToString("F2"), atrVal,
                    touch ? 1 : 0,
                    setupActive ? (setupIsLong ? "L" : "S") : "-",
                    cd, dailyTrades, atrOk ? 1 : 0));
            }

            if (inCooldown || tradeCapHit || !atrOk)
                return;

            // touch arms one latched setup and keeps it fixed until confirm/timeout
            if (touch && !setupActive)
            {
                bool candidateLong = inferredLong;

                if (EnableRetradeExcursionFilter && !string.IsNullOrEmpty(zoneId) && zoneNeedsExcursion.ContainsKey(zoneId) && zoneNeedsExcursion[zoneId])
                {
                    int zCount = zoneTradeCount.ContainsKey(zoneId) ? zoneTradeCount[zoneId] : 0;
                    if (zCount >= MaxTradesPerZoneCycle)
                    {
                        if (EnableLogs)
                            PrintWithContext("SETUP_BLOCKED reason=ZoneTradeCap zone=" + zoneId + " count=" + zCount + " anchor=" + avwap.ToString("F2"));
                        return;
                    }
                }

                setupIsLong = candidateLong;
                setupActive = true;
                setupAnchorPrice = avwap;
                setupBar = CurrentBar;
                setupAnchorKind = selected.Kind;
                setupZoneId = zoneId;

                if (EnableLogs)
                    PrintWithContext("SETUP_ARMED side=" + (setupIsLong ? "LONG" : "SHORT") + " kind=" + selected.Kind + " zone=" + zoneId + " avwap=" + avwap.ToString("F2"));
            }
        }

        private void TrySubmitEntry(bool isLong, double anchorUsed, AnchorKind anchorKind, string zoneId)
        {
            int stopTicks = ComputeSwingStopTicks(isLong);
            int quantity = DefaultQuantity;
            if (stopTicks <= 0 || quantity <= 0)
                return;

            if (!ApplyRiskCap(ref quantity, stopTicks))
            {
                // permissive fallback for sim: still place a 1-lot to test signal flow
                quantity = 1;
                if (EnableLogs)
                    PrintWithContext("ENTRY_RISKCAP_BYPASS qtyForced=1 stopTicks=" + stopTicks);
            }

            int targetTicks = Math.Max(stopTicks + 1, (int)Math.Round(stopTicks * RiskRewardMultiple));
            string side = isLong ? "L" : "S";
            string usageKey = side + "-" + anchorKind;
            int nextCount = 1;
            if (anchorUsageCounts.ContainsKey(usageKey))
                nextCount = anchorUsageCounts[usageKey] + 1;
            anchorUsageCounts[usageKey] = nextCount;

            string signal = usageKey + "-" + nextCount;

            // zone-level lock: any trade in overlap zone locks both directions until excursion
            if (EnableRetradeExcursionFilter && !string.IsNullOrEmpty(zoneId))
            {
                zoneLockPrice[zoneId] = anchorUsed;
                zoneNeedsExcursion[zoneId] = true;
                int zCount = zoneTradeCount.ContainsKey(zoneId) ? zoneTradeCount[zoneId] : 0;
                zoneTradeCount[zoneId] = zCount + 1;
            }

            SetStopLoss(signal, CalculationMode.Ticks, stopTicks, false);
            SetProfitTarget(signal, CalculationMode.Ticks, targetTicks);

            activeSignalName = signal;
            activeRiskTicks = stopTicks;
            breakEvenMoved = false;

            if (isLong)
                EnterLong(quantity, signal);
            else
                EnterShort(quantity, signal);

            dailyTrades++;
            anchorCooldowns[anchorKind] = SignalCooldownBars;

            if (EnableLogs)
            {
                PrintWithContext("ENTRY side=" + (isLong ? "LONG" : "SHORT") +
                                 " kind=" + anchorKind +
                                 " anchor=" + anchorUsed.ToString("F2") +
                                 " close=" + Close[0].ToString("F2") +
                                 " stopTicks=" + stopTicks +
                                 " targetTicks=" + targetTicks +
                                 " rr=" + RiskRewardMultiple.ToString("F1") +
                                 " qty=" + quantity);
            }
        }

        private void ManageBreakEven()
        {
            if (!EnableBreakEven || breakEvenMoved || activeRiskTicks <= 0 || string.IsNullOrEmpty(activeSignalName))
                return;

            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            double avg = Position.AveragePrice;
            double triggerMove = BreakEvenTriggerR * activeRiskTicks * TickSize;

            if (Position.MarketPosition == MarketPosition.Long)
            {
                if (Close[0] >= avg + triggerMove)
                {
                    double bePrice = Instrument.MasterInstrument.RoundToTickSize(avg + (BreakEvenPlusTicks * TickSize));
                    SetStopLoss(activeSignalName, CalculationMode.Price, bePrice, false);
                    breakEvenMoved = true;
                    if (EnableLogs)
                        PrintWithContext("BREAK_EVEN_MOVED side=LONG signal=" + activeSignalName + " stop=" + bePrice.ToString("F2"));
                }
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                if (Close[0] <= avg - triggerMove)
                {
                    double bePrice = Instrument.MasterInstrument.RoundToTickSize(avg - (BreakEvenPlusTicks * TickSize));
                    SetStopLoss(activeSignalName, CalculationMode.Price, bePrice, false);
                    breakEvenMoved = true;
                    if (EnableLogs)
                        PrintWithContext("BREAK_EVEN_MOVED side=SHORT signal=" + activeSignalName + " stop=" + bePrice.ToString("F2"));
                }
            }
        }

        private void UpdateZoneExcursionState(double atrVal)
        {
            if (!EnableRetradeExcursionFilter || double.IsNaN(atrVal) || atrVal <= 0)
                return;

            double required = RetradeExcursionAtrMultiple * atrVal;
            var zones = new List<string>(zoneNeedsExcursion.Keys);
            for (int i = 0; i < zones.Count; i++)
            {
                string zone = zones[i];
                if (!zoneNeedsExcursion[zone])
                    continue;

                if (!zoneLockPrice.ContainsKey(zone))
                {
                    zoneNeedsExcursion[zone] = false;
                    continue;
                }

                double anchor = zoneLockPrice[zone];
                bool satisfied = Math.Abs(Close[0] - anchor) >= required;

                if (satisfied)
                {
                    zoneNeedsExcursion[zone] = false;
                    zoneTradeCount[zone] = 0;
                    if (EnableLogs)
                        PrintWithContext("ZONE_UNLOCKED zone=" + zone + " anchor=" + anchor.ToString("F2") + " requiredMove=" + required.ToString("F2"));
                }
            }
        }

        private int ComputeSwingStopTicks(bool isLong)
        {
            int lookback = Math.Min(Math.Max(1, StopSwingLookbackBars), CurrentBar);
            double swing = isLong ? Low[0] : High[0];

            for (int i = 1; i <= lookback; i++)
                swing = isLong ? Math.Min(swing, Low[i]) : Math.Max(swing, High[i]);

            double distPoints = isLong ? (Close[0] - swing) : (swing - Close[0]);
            distPoints = Math.Max(TickSize, Math.Min(MaxStopPoints, distPoints));
            return Math.Max(MinStopTicks, (int)Math.Ceiling(distPoints / TickSize));
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

        private bool IsLodEstablished()
        {
            if (double.IsNaN(atr[0]) || atr[0] <= 0)
                return false;

            double requiredMove = ExtremeEstablishAtrMultiple * atr[0];
            return (High[0] - dayLow) >= requiredMove;
        }

        private bool IsHodEstablished()
        {
            if (double.IsNaN(atr[0]) || atr[0] <= 0)
                return false;

            double requiredMove = ExtremeEstablishAtrMultiple * atr[0];
            return (dayHigh - Low[0]) >= requiredMove;
        }

        private List<AnchorPoint> BuildAnchors()
        {
            var list = new List<AnchorPoint>();

            if (EnableSessionVwapAnchor)
            {
                double s = GetSessionVwapValue();
                if (!double.IsNaN(s))
                    list.Add(new AnchorPoint { Kind = AnchorKind.SessionVWAP, Price = s, BarIndex = sessionStartBarIndex, AnchorTime = sessionStartTime });
            }

            if (EnableWeeklyVwapAnchor)
            {
                double w = GetWtdAvwap();
                if (!double.IsNaN(w))
                    list.Add(new AnchorPoint { Kind = AnchorKind.WeeklyVWAP, Price = w, BarIndex = wtdAnchorBarIndex, AnchorTime = wtdAnchorTime });
            }

            if (EnableHodAnchor && dayHighBarIndex >= 0 && IsHodEstablished())
            {
                double h = GetAvwapFromBar(dayHighBarIndex, dayHigh);
                if (!double.IsNaN(h))
                    list.Add(new AnchorPoint { Kind = AnchorKind.HOD, Price = h, BarIndex = dayHighBarIndex, AnchorTime = dayHighTime });
            }

            if (EnableLodAnchor && dayLowBarIndex >= 0 && IsLodEstablished())
            {
                double l = GetAvwapFromBar(dayLowBarIndex, dayLow);
                if (!double.IsNaN(l))
                    list.Add(new AnchorPoint { Kind = AnchorKind.LOD, Price = l, BarIndex = dayLowBarIndex, AnchorTime = dayLowTime });
            }

            if (TryGetManualAnchorValue(true, out double mLong))
                list.Add(new AnchorPoint { Kind = AnchorKind.ManualLong, Price = mLong, BarIndex = TimeToBarIndex(ManualLongAnchorFrom), AnchorTime = ManualLongAnchorFrom });

            if (TryGetManualAnchorValue(false, out double mShort))
                list.Add(new AnchorPoint { Kind = AnchorKind.ManualShort, Price = mShort, BarIndex = TimeToBarIndex(ManualShortAnchorFrom), AnchorTime = ManualShortAnchorFrom });

            return list;
        }

        // choose conservative anchor from anchors clustered within 1 ATR of touched neighborhood

        private void UpdateRelevantAnchorOverlays(List<AnchorPoint> anchors)
        {
            if (ChartControl == null || !ShowRelevantAnchorsOnChart)
                return;

            if (anchors == null || anchors.Count == 0)
            {
                RemoveDrawObject(RelevantAnchorLabelTag);
                relevantAnchorBarIndex = -1;
                return;
            }

            // Single closest anchor
            AnchorPoint closest = anchors[0];
            double bestDist = Math.Abs(Close[0] - closest.Price);
            for (int i = 1; i < anchors.Count; i++)
            {
                double d = Math.Abs(Close[0] - anchors[i].Price);
                if (d < bestDist) { bestDist = d; closest = anchors[i]; }
            }

            Draw.Text(this, RelevantAnchorLabelTag, closest.Kind.ToString(), 0, closest.Price + (4 * TickSize), Brushes.DodgerBlue);

            if (closest.BarIndex != relevantAnchorBarIndex && closest.BarIndex >= 0 && closest.AnchorTime > Core.Globals.MinDate)
            {
                HideRelevantAvwap(ref relevantAnchorAvwap2);
                RebuildRelevantAvwap(ref relevantAnchorAvwap2, closest.AnchorTime);
                relevantAnchorBarIndex = closest.BarIndex;
            }
        }

        private void RebuildRelevantAvwap(ref AVWAP2 avwap, DateTime anchorTime)
        {
            avwap = AVWAP2(BarsArray[0], anchorTime,
                new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
        }

        private void HideRelevantAvwap(ref AVWAP2 avwap)
        {
            if (avwap != null && avwap.Plots != null && avwap.Plots.Length > 0)
                avwap.Plots[0].Brush = Brushes.Transparent;
            avwap = null;
        }

        private DateTime SafeBarTime(int absBarIndex)
        {
            int barsAgo = CurrentBar - absBarIndex;
            if (barsAgo < 0 || barsAgo > 254)
                return DateTime.MinValue;
            return Time[barsAgo];
        }

        private bool IsInTradeWindow(int cmeTime)
        {
            if (UseExtendedHours)
                return true;
            return cmeTime >= CmeRthStart && cmeTime <= CmeRthEnd;
        }

        private void ResetSessionStateIfNeeded()
        {
            if (!Bars.IsFirstBarOfSession && sessionDate != Core.Globals.MinDate)
                return;

            sessionDate = GetCmeTime(Time[0]).Date;
            sessionStartBarIndex = CurrentBar;
            sessionStartTime = Time[0];
            dayHigh = High[0];
            dayLow = Low[0];
            dayHighBarIndex = CurrentBar;
            dayHighTime = Time[0];
            dayLowBarIndex = CurrentBar;
            dayLowTime = Time[0];
            dailyTrades = 0;
            anchorCooldowns.Clear();
            anchorUsageCounts.Clear();
            zoneLockPrice.Clear();
            zoneNeedsExcursion.Clear();
            zoneTradeCount.Clear();
            setupActive = false;
            setupZoneId = null;
            HideRelevantAvwap(ref relevantAnchorAvwap2);
            relevantAnchorBarIndex = -1;
        }

        private void DecrementAnchorCooldowns()
        {
            var keys = new List<AnchorKind>(anchorCooldowns.Keys);
            foreach (AnchorKind k in keys)
            {
                anchorCooldowns[k]--;
                if (anchorCooldowns[k] <= 0)
                    anchorCooldowns.Remove(k);
            }
        }

        private void UpdateDailyExtremes()
        {
            if (High[0] >= dayHigh + TickSize)
            {
                dayHigh = High[0];
                dayHighBarIndex = CurrentBar;
                dayHighTime = Time[0];
            }

            if (Low[0] <= dayLow - TickSize)
            {
                dayLow = Low[0];
                dayLowBarIndex = CurrentBar;
                dayLowTime = Time[0];
            }
        }

        private double GetSessionVwapValue()
        {
            double v = VWAP1(BarsArray[0],
                new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                true, true, true).Output[0];

            return double.IsNaN(v) ? double.NaN : Instrument.MasterInstrument.RoundToTickSize(v);
        }

        private double GetAvwapFromBar(int anchorBarIndex, double fallbackPrice)
        {
            if (anchorBarIndex < 0 || anchorBarIndex > CurrentBar)
                return fallbackPrice;

            int anchorBarsAgo = CurrentBar - anchorBarIndex;
            double pv = 0;
            double vSum = 0;

            for (int i = anchorBarsAgo; i >= 0; i--)
            {
                double vol = Volume[i];
                if (vol <= 0)
                    continue;

                double typical = (High[i] + Low[i] + Close[i]) / 3.0;
                pv += typical * vol;
                vSum += vol;
            }

            return vSum > 0 ? pv / vSum : fallbackPrice;
        }

        private void UpdateWtdAnchorIfNeeded()
        {
            if (!EnableWeeklyVwapAnchor)
                return;

            DateTime cmeNow = GetCmeTime(Time[0]);
            var cal = System.Globalization.CultureInfo.InvariantCulture.Calendar;
            int week = cal.GetWeekOfYear(cmeNow, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
            int weekKey = cmeNow.Year * 100 + week;

            bool isNewWeek = weekKey != wtdAnchorWeekYear;
            bool isSunday1700 = cmeNow.DayOfWeek == DayOfWeek.Sunday && cmeNow.Hour == 17 && cmeNow.Minute == 0;

            if (isSunday1700 && isNewWeek)
            {
                SetWtdAnchorToCurrentBar(weekKey);
                wtdDeferredWeekYear = -1;
                return;
            }

            if (!wtdAnchorSet)
            {
                if (wtdDeferredWeekYear == weekKey)
                    return;
                TryInitWtdAnchorFromHistory(weekKey);
            }
        }

        private void SetWtdAnchorToCurrentBar(int weekKey)
        {
            wtdAnchorBarIndex = CurrentBar;
            wtdAnchorOpenPrice = Open[0];
            wtdAnchorTime = Time[0];
            wtdAnchorSet = true;
            wtdAnchorWeekYear = weekKey;
            wtdSeededThisBar = true;

            double typical = (High[0] + Low[0] + Close[0]) / 3.0;
            double vol = Math.Max(0, Volume[0]);
            wtdPV = typical * vol;
            wtdVSum = vol;
        }

        private void TryInitWtdAnchorFromHistory(int currentWeekKey)
        {
            var cal = System.Globalization.CultureInfo.InvariantCulture.Calendar;
            int maxLookback = CurrentBar;

            for (int i = 0; i <= maxLookback; i++)
            {
                DateTime barCme = GetCmeTime(Time[i]);
                if (barCme.DayOfWeek != DayOfWeek.Sunday || barCme.Hour != 17 || barCme.Minute != 0)
                    continue;

                int anchorAbsBar = CurrentBar - i;
                int foundWeekKey = barCme.Year * 100 + cal.GetWeekOfYear(barCme, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);

                wtdAnchorBarIndex = anchorAbsBar;
                wtdAnchorOpenPrice = Open[i];
                wtdAnchorTime = Time[i];
                wtdAnchorSet = true;
                wtdAnchorWeekYear = foundWeekKey;
                wtdSeededThisBar = true;

                wtdPV = 0;
                wtdVSum = 0;
                for (int j = i; j >= 0; j--)
                {
                    double vol = Math.Max(0, Volume[j]);
                    if (vol <= 0)
                        continue;
                    double typical = (High[j] + Low[j] + Close[j]) / 3.0;
                    wtdPV += typical * vol;
                    wtdVSum += vol;
                }
                return;
            }

            wtdDeferredWeekYear = currentWeekKey;
        }

        private void UpdateWtdRunningAccumulator()
        {
            if (!EnableWeeklyVwapAnchor || !wtdAnchorSet)
                return;

            if (wtdSeededThisBar || CurrentBar == wtdAnchorBarIndex)
                return;

            double typical = (High[0] + Low[0] + Close[0]) / 3.0;
            double vol = Math.Max(0, Volume[0]);
            wtdPV += typical * vol;
            wtdVSum += vol;
        }

        private double GetWtdAvwap()
        {
            if (!EnableWeeklyVwapAnchor || !wtdAnchorSet || wtdAnchorBarIndex < 0)
                return double.NaN;
            return wtdVSum > 0 ? wtdPV / wtdVSum : wtdAnchorOpenPrice;
        }

        private void InitializeTimeZones()
        {
            cmeTimeZone = ResolveTimeZone("Central Standard Time", "America/Chicago", TimeZoneInfo.Local);
            barTimeZone = Bars?.TradingHours?.TimeZoneInfo ?? cmeTimeZone;
        }

        private static TimeZoneInfo ResolveTimeZone(string primaryId, string fallbackId, TimeZoneInfo defaultZone)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(primaryId); }
            catch
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(fallbackId); }
                catch { return defaultZone; }
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

        // ===== Manual anchor controls (same concept: click + Q/A, clear C) =====
        private void EnsureManualAnchorHotkeysHooked()
        {
            if (!UseManualAnchors || !EnableManualAnchorHotkeys)
            {
                UnhookManualAnchorHotkeys();
                return;
            }

            if (manualHotkeysHooked || ChartControl == null)
                return;

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                if (manualHotkeysHooked || ChartControl == null)
                    return;

                chartWindow = Window.GetWindow(ChartControl.Parent) as Chart;
                if (chartWindow == null)
                    return;

                chartWindow.PreviewKeyDown += OnManualAnchorHotkeyPressed;
                chartWindow.PreviewMouseDown += OnManualAnchorMouseDown;
                manualHotkeysHooked = true;
            });
        }

        private void UnhookManualAnchorHotkeys()
        {
            if (!manualHotkeysHooked || chartWindow == null)
                return;

            try
            {
                chartWindow.PreviewKeyDown -= OnManualAnchorHotkeyPressed;
                chartWindow.PreviewMouseDown -= OnManualAnchorMouseDown;
            }
            catch { }

            manualHotkeysHooked = false;
            chartWindow = null;
        }

        private void OnManualAnchorMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (ChartControl == null || ChartBars == null)
                return;
            if (e.ChangedButton != MouseButton.Left)
                return;
            if (!(e.OriginalSource is Visual))
                return;

            try
            {
                Point p = e.GetPosition(ChartControl);
                int barIdx = ChartBars.GetBarIdxByX(ChartControl, (int)p.X);
                if (barIdx >= 0 && barIdx <= CurrentBar)
                    lastClickedBarIndex = barIdx;
            }
            catch { }
        }

        private void OnManualAnchorHotkeyPressed(object sender, KeyEventArgs e)
        {
            if (!UseManualAnchors)
                return;

            if (e.OriginalSource is TextBox || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase)
                return;

            bool handled = false;
            lock (manualAnchorLock)
            {
                if (e.Key == Key.Q && lastClickedBarIndex >= 0)
                {
                    pendingSetManualLong = true;
                    pendingLongBarIndex = lastClickedBarIndex;
                    pendingClearManualAnchors = false;
                    handled = true;
                }
                else if (e.Key == Key.A && lastClickedBarIndex >= 0)
                {
                    pendingSetManualShort = true;
                    pendingShortBarIndex = lastClickedBarIndex;
                    pendingClearManualAnchors = false;
                    handled = true;
                }
                else if (e.Key == Key.C)
                {
                    pendingClearManualAnchors = true;
                    pendingSetManualLong = false;
                    pendingSetManualShort = false;
                    pendingLongBarIndex = -1;
                    pendingShortBarIndex = -1;
                    handled = true;
                }
            }

            if (handled)
                e.Handled = true;
        }

        private void ProcessPendingManualAnchorActions()
        {
            bool setLong;
            bool setShort;
            bool clear;
            int longBarIdx;
            int shortBarIdx;

            lock (manualAnchorLock)
            {
                setLong = pendingSetManualLong;
                setShort = pendingSetManualShort;
                clear = pendingClearManualAnchors;
                longBarIdx = pendingLongBarIndex;
                shortBarIdx = pendingShortBarIndex;

                pendingSetManualLong = false;
                pendingSetManualShort = false;
                pendingClearManualAnchors = false;
                pendingLongBarIndex = -1;
                pendingShortBarIndex = -1;
            }

            if (!(setLong || setShort || clear))
                return;

            if (clear)
            {
                ManualLongAnchorFrom = Core.Globals.MinDate;
                ManualShortAnchorFrom = Core.Globals.MinDate;
            }
            else
            {
                if (setLong)
                {
                    DateTime t = BarIndexToTime(longBarIdx);
                    if (t > Core.Globals.MinDate)
                        ManualLongAnchorFrom = t;
                }

                if (setShort)
                {
                    DateTime t = BarIndexToTime(shortBarIdx);
                    if (t > Core.Globals.MinDate)
                        ManualShortAnchorFrom = t;
                }
            }

            RebuildManualAvwapAnchors();
        }

        private DateTime BarIndexToTime(int barIndex)
        {
            if (barIndex < 0 || barIndex > CurrentBar)
                return Core.Globals.MinDate;

            int barsAgo = CurrentBar - barIndex;
            if (barsAgo < 0 || barsAgo >= Time.Count)
                return Core.Globals.MinDate;

            return Time[barsAgo];
        }

        private int TimeToBarIndex(DateTime anchorTime)
        {
            if (anchorTime <= Core.Globals.MinDate)
                return -1;

            int idx = Bars.GetBar(anchorTime);
            return (idx >= 0 && idx <= CurrentBar) ? idx : -1;
        }

        private void RebuildManualAvwapAnchors()
        {
            if (manualLongAvwap2 != null && manualLongAvwap2.Plots != null && manualLongAvwap2.Plots.Length > 0)
                manualLongAvwap2.Plots[0].Brush = Brushes.Transparent;
            if (manualShortAvwap2 != null && manualShortAvwap2.Plots != null && manualShortAvwap2.Plots.Length > 0)
                manualShortAvwap2.Plots[0].Brush = Brushes.Transparent;

            manualLongAvwap2 = null;
            manualShortAvwap2 = null;

            if (!UseManualAnchors)
                return;

            if (ManualLongAnchorFrom > Core.Globals.MinDate)
                manualLongAvwap2 = AVWAP2(BarsArray[0], ManualLongAnchorFrom,
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);

            if (ManualShortAnchorFrom > Core.Globals.MinDate)
                manualShortAvwap2 = AVWAP2(BarsArray[0], ManualShortAnchorFrom,
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
        }

        private bool TryGetManualAnchorValue(bool isLong, out double value)
        {
            value = double.NaN;
            if (!UseManualAnchors)
                return false;

            AVWAP2 manual = isLong ? manualLongAvwap2 : manualShortAvwap2;
            if (manual == null || manual.Output == null || manual.Output.Count < 1)
                return false;

            value = manual.Output[0];
            return !double.IsNaN(value) && value > 0;
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
        [Range(1, 1000)]
        [Display(Name = "Max Trades Per Day", GroupName = "Risk", Order = 5)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Signal Cooldown Bars", GroupName = "Entry", Order = 6)]
        public int SignalCooldownBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Stop Swing Lookback Bars", GroupName = "Risk", Order = 7)]
        public int StopSwingLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Min Stop Ticks", GroupName = "Risk", Order = 8)]
        public int MinStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10.0)]
        [Display(Name = "Max Stop Points", GroupName = "Risk", Order = 9)]
        public double MaxStopPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 4.0)]
        [Display(Name = "Risk Reward Multiple", GroupName = "Risk", Order = 10)]
        public double RiskRewardMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(50.0, 5000.0)]
        [Display(Name = "Max Risk Per Trade ($)", GroupName = "Risk", Order = 11)]
        public double MaxRiskPerTradeDollars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Break Even", GroupName = "Risk", Order = 12)]
        public bool EnableBreakEven { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 3.0)]
        [Display(Name = "Break Even Trigger (R)", GroupName = "Risk", Order = 13)]
        public double BreakEvenTriggerR { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "Break Even Plus Ticks", GroupName = "Risk", Order = 14)]
        public int BreakEvenPlusTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Re-trade Excursion Filter", GroupName = "Risk", Order = 15)]
        public bool EnableRetradeExcursionFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 3.0)]
        [Display(Name = "Re-trade Excursion ATR Multiple", GroupName = "Risk", Order = 16)]
        public double RetradeExcursionAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max Trades Per Zone Cycle", GroupName = "Risk", Order = 17)]
        public int MaxTradesPerZoneCycle { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 1.0)]
        [Display(Name = "Confirm Body ATR Multiple", GroupName = "Risk", Order = 18)]
        public double ConfirmBodyAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Touch Tolerance Ticks", GroupName = "Anchors", Order = 12)]
        public int TouchToleranceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(2, 20)]
        [Display(Name = "Recent Bar Lookback", GroupName = "Anchors", Order = 13)]
        public int RecentBarLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Setup Max Bars", GroupName = "Anchors", Order = 14)]
        public int SetupMaxBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "Extreme Establish ATR Multiple", GroupName = "Anchors", Order = 15)]
        public double ExtremeEstablishAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Session VWAP Anchor", GroupName = "Anchors", Order = 16)]
        public bool EnableSessionVwapAnchor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Weekly VWAP Anchor", GroupName = "Anchors", Order = 17)]
        public bool EnableWeeklyVwapAnchor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable HOD Anchor", GroupName = "Anchors", Order = 18)]
        public bool EnableHodAnchor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable LOD Anchor", GroupName = "Anchors", Order = 19)]
        public bool EnableLodAnchor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Manual Anchors", GroupName = "Anchors", Order = 20)]
        public bool UseManualAnchors { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Session VWAP On Chart", GroupName = "Anchors", Order = 21)]
        public bool ShowSessionVwapOnChart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly VWAP On Chart", GroupName = "Anchors", Order = 22)]
        public bool ShowWeeklyVwapOnChart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Relevant Anchors On Chart", GroupName = "Anchors", Order = 23)]
        public bool ShowRelevantAnchorsOnChart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Manual Anchor Hotkeys (Q/A/C)", GroupName = "Anchors", Order = 24)]
        public bool EnableManualAnchorHotkeys { get; set; }

        [Browsable(false)]
        public DateTime ManualLongAnchorFrom { get; set; }

        [Browsable(false)]
        public DateTime ManualShortAnchorFrom { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Logs", GroupName = "Diagnostics", Order = 23)]
        public bool EnableLogs { get; set; }

        #endregion
    }
}

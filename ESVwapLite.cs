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
        }

        private const int CmeRthStart = 83000;
        private const int CmeRthEnd = 150000;
        private const string SessionVwapLineTag = "ESVwapLite.SessionVWAP.Line";
        private const string SessionVwapLabelTag = "ESVwapLite.SessionVWAP.Label";
        private const string WeeklyVwapLineTag = "ESVwapLite.WeeklyVWAP.Line";
        private const string WeeklyVwapLabelTag = "ESVwapLite.WeeklyVWAP.Label";
        private const string RelevantSupportLineTag = "ESVwapLite.RelevantSupport.Line";
        private const string RelevantSupportLabelTag = "ESVwapLite.RelevantSupport.Label";
        private const string RelevantResistanceLineTag = "ESVwapLite.RelevantResistance.Line";
        private const string RelevantResistanceLabelTag = "ESVwapLite.RelevantResistance.Label";

        private ATR atr;
        private TimeZoneInfo cmeTimeZone;
        private TimeZoneInfo barTimeZone;
        private AVWAP2 manualLongAvwap2;
        private AVWAP2 manualShortAvwap2;

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
        private int cooldownRemaining;

        // setup state
        private bool supportSetupActive;
        private bool resistanceSetupActive;
        private double setupSupportAnchor;
        private double setupResistanceAnchor;
        private int setupSupportBar;
        private int setupResistanceBar;

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
                MaxTradesPerDay = 6;
                SignalCooldownBars = 2;
                MinAtrForEntry = 0.8;
                MaxAtrForEntry = 14.0;

                // risk: recent peak/trough distance, capped/floored
                StopSwingLookbackBars = 8;
                MinStopTicks = 8;
                MaxStopPoints = 5.0;
                RiskRewardMultiple = 2.0;
                MaxRiskPerTradeDollars = 400.0;

                // touch/zone behavior
                TouchToleranceTicks = 2;
                ClusterAtrMultiple = 1.0;

                EnableSessionVwapAnchor = true;
                EnableWeeklyVwapAnchor = true;
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
            UpdateVwapOverlays();

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            // permissive/sim mode: intentionally bypass most gating (time window, ATR, cooldown, daily max)
            List<AnchorPoint> anchors = BuildAnchors();
            if (anchors.Count == 0)
                return;

            UpdateRelevantAnchorOverlays(anchors);

            double touchTol = TouchToleranceTicks * TickSize;
            bool touchedAny = false;
            foreach (AnchorPoint a in anchors)
            {
                if (Low[0] <= a.Price + touchTol && High[0] >= a.Price - touchTol)
                {
                    touchedAny = true;
                    break;
                }
            }

            if (!touchedAny)
                return;

            if (TrySelectConservativeAnchor(anchors, true, out AnchorPoint supportAnchor))
            {
                supportSetupActive = true;
                setupSupportAnchor = supportAnchor.Price;
                setupSupportBar = CurrentBar;
                if (EnableLogs)
                    PrintWithContext("SETUP_ARMED side=LONG kind=" + supportAnchor.Kind + " anchor=" + supportAnchor.Price.ToString("F2"));
            }

            if (TrySelectConservativeAnchor(anchors, false, out AnchorPoint resistanceAnchor))
            {
                resistanceSetupActive = true;
                setupResistanceAnchor = resistanceAnchor.Price;
                setupResistanceBar = CurrentBar;
                if (EnableLogs)
                    PrintWithContext("SETUP_ARMED side=SHORT kind=" + resistanceAnchor.Kind + " anchor=" + resistanceAnchor.Price.ToString("F2"));
            }

            // if price touched anchor and this bar closed green above support zone -> long
            if (supportSetupActive && Close[0] > Open[0] && Close[0] >= setupSupportAnchor - touchTol)
            {
                TrySubmitEntry(true, setupSupportAnchor);
                supportSetupActive = false;
                resistanceSetupActive = false;
                return;
            }

            // if price touched anchor and this bar closed red below resistance zone -> short
            if (resistanceSetupActive && Close[0] < Open[0] && Close[0] <= setupResistanceAnchor + touchTol)
            {
                TrySubmitEntry(false, setupResistanceAnchor);
                supportSetupActive = false;
                resistanceSetupActive = false;
                return;
            }
        }

        private void TrySubmitEntry(bool isLong, double anchorUsed)
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
                                 " anchor=" + anchorUsed.ToString("F2") +
                                 " close=" + Close[0].ToString("F2") +
                                 " stopTicks=" + stopTicks +
                                 " targetTicks=" + targetTicks +
                                 " rr=" + RiskRewardMultiple.ToString("F1") +
                                 " qty=" + quantity);
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

        private List<AnchorPoint> BuildAnchors()
        {
            var list = new List<AnchorPoint>();

            if (EnableSessionVwapAnchor)
            {
                double s = GetSessionVwapValue();
                if (!double.IsNaN(s))
                    list.Add(new AnchorPoint { Kind = AnchorKind.SessionVWAP, Price = s, BarIndex = sessionStartBarIndex });
            }

            if (EnableWeeklyVwapAnchor)
            {
                double w = GetWtdAvwap();
                if (!double.IsNaN(w))
                    list.Add(new AnchorPoint { Kind = AnchorKind.WeeklyVWAP, Price = w, BarIndex = wtdAnchorBarIndex });
            }

            if (dayHighBarIndex >= 0)
            {
                double h = GetAvwapFromBar(dayHighBarIndex, dayHigh);
                if (!double.IsNaN(h))
                    list.Add(new AnchorPoint { Kind = AnchorKind.HOD, Price = h, BarIndex = dayHighBarIndex });
            }

            if (dayLowBarIndex >= 0)
            {
                double l = GetAvwapFromBar(dayLowBarIndex, dayLow);
                if (!double.IsNaN(l))
                    list.Add(new AnchorPoint { Kind = AnchorKind.LOD, Price = l, BarIndex = dayLowBarIndex });
            }

            if (TryGetManualAnchorValue(true, out double mLong))
                list.Add(new AnchorPoint { Kind = AnchorKind.ManualLong, Price = mLong, BarIndex = TimeToBarIndex(ManualLongAnchorFrom) });

            if (TryGetManualAnchorValue(false, out double mShort))
                list.Add(new AnchorPoint { Kind = AnchorKind.ManualShort, Price = mShort, BarIndex = TimeToBarIndex(ManualShortAnchorFrom) });

            return list;
        }

        // choose conservative anchor from anchors clustered within 1 ATR of touched neighborhood
        private bool TrySelectConservativeAnchor(List<AnchorPoint> anchors, bool forSupport, out AnchorPoint selected)
        {
            selected = default(AnchorPoint);
            if (anchors == null || anchors.Count == 0)
                return false;

            double touchTol = TouchToleranceTicks * TickSize;
            var touched = new List<AnchorPoint>();
            foreach (AnchorPoint a in anchors)
            {
                if (Low[0] <= a.Price + touchTol && High[0] >= a.Price - touchTol)
                    touched.Add(a);
            }

            if (touched.Count == 0)
                return false;

            // Start with nearest touched anchor to current close, then expand cluster by ATR distance.
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

            double clusterBand = Math.Max(TickSize, ClusterAtrMultiple * atr[0]);
            var cluster = new List<AnchorPoint>();
            foreach (AnchorPoint a in anchors)
            {
                if (Math.Abs(a.Price - seed.Price) <= clusterBand)
                    cluster.Add(a);
            }

            if (cluster.Count == 0)
                return false;

            selected = cluster[0];
            for (int i = 1; i < cluster.Count; i++)
            {
                if (forSupport)
                {
                    // conservative support = lowest anchor in cluster
                    if (cluster[i].Price < selected.Price)
                        selected = cluster[i];
                }
                else
                {
                    // conservative resistance = highest anchor in cluster
                    if (cluster[i].Price > selected.Price)
                        selected = cluster[i];
                }
            }

            return true;
        }

        private void UpdateVwapOverlays()
        {
            if (ChartControl == null)
                return;

            if (ShowSessionVwapOnChart)
            {
                double session = GetSessionVwapValue();
                if (!double.IsNaN(session))
                {
                    int startBarsAgo = Math.Min(CurrentBar, 200);
                    Draw.HorizontalLine(this, SessionVwapLineTag, session, Brushes.DeepSkyBlue);
                    Draw.Text(this, SessionVwapLabelTag, "Session VWAP", 0, session + (2 * TickSize), Brushes.DeepSkyBlue);
                }
                else
                {
                    RemoveDrawObject(SessionVwapLineTag);
                    RemoveDrawObject(SessionVwapLabelTag);
                }
            }
            else
            {
                RemoveDrawObject(SessionVwapLineTag);
                RemoveDrawObject(SessionVwapLabelTag);
            }

            if (ShowWeeklyVwapOnChart)
            {
                double weekly = GetWtdAvwap();
                if (!double.IsNaN(weekly))
                {
                    int startBarsAgo = Math.Min(CurrentBar, 200);
                    Draw.HorizontalLine(this, WeeklyVwapLineTag, weekly, Brushes.Gold);
                    Draw.Text(this, WeeklyVwapLabelTag, "Weekly VWAP", 0, weekly - (2 * TickSize), Brushes.Gold);
                }
                else
                {
                    RemoveDrawObject(WeeklyVwapLineTag);
                    RemoveDrawObject(WeeklyVwapLabelTag);
                }
            }
            else
            {
                RemoveDrawObject(WeeklyVwapLineTag);
                RemoveDrawObject(WeeklyVwapLabelTag);
            }
        }

        private bool TrySelectConservativeAnchorNearPrice(List<AnchorPoint> anchors, bool forSupport, out AnchorPoint selected)
        {
            selected = default(AnchorPoint);
            if (anchors == null || anchors.Count == 0)
                return false;

            AnchorPoint seed = anchors[0];
            double bestDist = Math.Abs(Close[0] - seed.Price);
            for (int i = 1; i < anchors.Count; i++)
            {
                double d = Math.Abs(Close[0] - anchors[i].Price);
                if (d < bestDist)
                {
                    bestDist = d;
                    seed = anchors[i];
                }
            }

            double clusterBand = Math.Max(TickSize, ClusterAtrMultiple * atr[0]);
            selected = seed;
            bool found = false;
            for (int i = 0; i < anchors.Count; i++)
            {
                AnchorPoint a = anchors[i];
                if (Math.Abs(a.Price - seed.Price) > clusterBand)
                    continue;

                if (!found)
                {
                    selected = a;
                    found = true;
                    continue;
                }

                if (forSupport)
                {
                    if (a.Price < selected.Price)
                        selected = a;
                }
                else
                {
                    if (a.Price > selected.Price)
                        selected = a;
                }
            }

            return found;
        }

        private void UpdateRelevantAnchorOverlays(List<AnchorPoint> anchors)
        {
            if (ChartControl == null)
                return;

            if (!ShowRelevantAnchorsOnChart)
            {
                RemoveDrawObject(RelevantSupportLineTag);
                RemoveDrawObject(RelevantSupportLabelTag);
                RemoveDrawObject(RelevantResistanceLineTag);
                RemoveDrawObject(RelevantResistanceLabelTag);
                return;
            }

            int startBarsAgo = Math.Min(CurrentBar, 200);

            if (TrySelectConservativeAnchorNearPrice(anchors, true, out AnchorPoint support))
            {
                Draw.HorizontalLine(this, RelevantSupportLineTag, support.Price, Brushes.LimeGreen);
                Draw.Text(this, RelevantSupportLabelTag, "Relevant Support: " + support.Kind, 0, support.Price + (4 * TickSize), Brushes.LimeGreen);
            }
            else
            {
                RemoveDrawObject(RelevantSupportLineTag);
                RemoveDrawObject(RelevantSupportLabelTag);
            }

            if (TrySelectConservativeAnchorNearPrice(anchors, false, out AnchorPoint resistance))
            {
                Draw.HorizontalLine(this, RelevantResistanceLineTag, resistance.Price, Brushes.OrangeRed);
                Draw.Text(this, RelevantResistanceLabelTag, "Relevant Resistance: " + resistance.Kind, 0, resistance.Price - (4 * TickSize), Brushes.OrangeRed);
            }
            else
            {
                RemoveDrawObject(RelevantResistanceLineTag);
                RemoveDrawObject(RelevantResistanceLabelTag);
            }
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
            dayHigh = High[0];
            dayLow = Low[0];
            dayHighBarIndex = CurrentBar;
            dayLowBarIndex = CurrentBar;
            dailyTrades = 0;
            cooldownRemaining = 0;
            supportSetupActive = false;
            resistanceSetupActive = false;
        }

        private void UpdateDailyExtremes()
        {
            if (High[0] >= dayHigh + TickSize)
            {
                dayHigh = High[0];
                dayHighBarIndex = CurrentBar;
            }

            if (Low[0] <= dayLow - TickSize)
            {
                dayLow = Low[0];
                dayLowBarIndex = CurrentBar;
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
        [Range(1, 20)]
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
        [Range(1, 20)]
        [Display(Name = "Touch Tolerance Ticks", GroupName = "Anchors", Order = 12)]
        public int TouchToleranceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 2.0)]
        [Display(Name = "Cluster ATR Multiple", GroupName = "Anchors", Order = 13)]
        public double ClusterAtrMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Session VWAP Anchor", GroupName = "Anchors", Order = 14)]
        public bool EnableSessionVwapAnchor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Weekly VWAP Anchor", GroupName = "Anchors", Order = 15)]
        public bool EnableWeeklyVwapAnchor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Manual Anchors", GroupName = "Anchors", Order = 16)]
        public bool UseManualAnchors { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Session VWAP On Chart", GroupName = "Anchors", Order = 17)]
        public bool ShowSessionVwapOnChart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly VWAP On Chart", GroupName = "Anchors", Order = 18)]
        public bool ShowWeeklyVwapOnChart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Relevant Anchors On Chart", GroupName = "Anchors", Order = 19)]
        public bool ShowRelevantAnchorsOnChart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Manual Anchor Hotkeys (Q/A/C)", GroupName = "Anchors", Order = 20)]
        public bool EnableManualAnchorHotkeys { get; set; }

        [Browsable(false)]
        public DateTime ManualLongAnchorFrom { get; set; }

        [Browsable(false)]
        public DateTime ManualShortAnchorFrom { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Logs", GroupName = "Diagnostics", Order = 21)]
        public bool EnableLogs { get; set; }

        #endregion
    }
}

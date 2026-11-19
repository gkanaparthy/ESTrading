//
// ESnoAonlyDWG.cs
// NinjaTrader 8 Strategy - Daily + Weekly VWAP + Gann, no Anchor (SIM)
//
// New variant Nov 18, 2026 (his call): fork of ESCombinedVWAPSimWideNonA,
// stripped down to learn how the non-Anchor pillars behave on a 2-minute
// chart. Live keeps the Anchor, so live A is the comparison.
// Removed vs the fork: the anchored VWAP altogether (no pocket-pivot
// anchors, no BigBody anchors, no Swing/News anchors, no AVWAP plot),
// the opening-range leg (1-min series gone), and the BigBody-without-
// pocket-pivot standalone anchors. Zones are D, W, G only.
// Max stop is 15 pts for every zone (MaxStopPoints = 15.0); the
// per-pillar MaxStopPointsNonAnchor split is gone with the Anchor.
// Bar-count settings (TriggerWindowBars, CooldownBars, etc.) are
// unchanged from the fork, now measured in 2-min bars.
// Logs are Name-based, so this variant writes trades_ESnoAonlyDWG.csv
// and skipped_trades_ESnoAonlyDWG.csv, separate from live and WideNonA.
// Sim defaults: MaxTradesPerDay 20, DailyLossLimit and DailyProfitLimit
// $100,000 each (no effective daily limits). Run on SIM only.
// Live runs ESCombinedVWAPSim v1.1.0, 50/90 limits, untouched.
// ORB tweaks and high/low-of-day anchors are parked for later.
//
// Platform: NinjaTrader 8, ES/MES, 2-minute, ETH session template,
// chart time zone = US Central.
//
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ESnoAonlyDWG : Strategy
    {
        #region Types
        // Daily or Weekly VWAP level.
        private class VwapLevel
        {
            public string Name;
            public double CumPV;
            public double CumV;
            public double Value = double.NaN;
            public bool Armed;
            public int StopOuts;
            public bool Dead;

            public bool IsLive
            {
                get { return !double.IsNaN(Value) && Value > 0.0; }
            }

            public void ResetAccumulator()
            {
                CumPV = 0.0;
                CumV = 0.0;
                Value = double.NaN;
            }

            public void ResetSession()
            {
                Armed = false;
                StopOuts = 0;
                Dead = false;
            }
        }

        // A cluster of eligible levels treated as one tradable band.
        private class Zone
        {
            public bool HasD;
            public bool HasW;
            public bool HasG;
            public double Min;
            public double Max;

            public string Tag
            {
                get
                {
                    string t = string.Empty;
                    if (HasD) t += (t.Length > 0 ? "+" : "") + "D";
                    if (HasW) t += (t.Length > 0 ? "+" : "") + "W";
                    if (HasG) t += (t.Length > 0 ? "+" : "") + "G";
                    return t;
                }
            }
        }

        // A setup blocked by the max-stop gate, tracked forward to see
        // whether its would-be target or would-be stop would have hit first.
        private class ShadowTrade
        {
            public DateTime SignalTime;
            public string Direction;   // "Long" / "Short"
            public string Pillar;      // zone Tag: D, W, G, D+W, ...
            public string AnchorTag;   // Always empty for D/W/G zones (kept for the log format)
            public double SignalPx;    // Close[0] at the signal bar (would-be entry proxy)
            public double StopPx;
            public double StopPts;
            public double Atr;
            public double TgtPts;
            public double ZoneMin;
            public double ZoneMax;
            public double MFE;
            public double MAE;
            public int BarsHeld;
        }
        #endregion

        #region Fields
        // ---------------------------------------------------------------------
        // Indicators
        // ---------------------------------------------------------------------
        private ATR atr14;

        private const string ActiveContactDrawTag = "ESnoAonlyDWG.ActiveContact";

        // ---------------------------------------------------------------------
        // Daily / Weekly VWAP levels
        // ---------------------------------------------------------------------
        private readonly VwapLevel daily = new VwapLevel { Name = "D" };
        private readonly VwapLevel weekly = new VwapLevel { Name = "W" };
        private bool weeklySeeded;

        // ---------------------------------------------------------------------
        // Gann level (Linear GANN 2.0, Adib Noorani). Static horizontal lines.
        // One level is live at a time: the Gann number nearest the close.
        // ---------------------------------------------------------------------
        private readonly VwapLevel gann = new VwapLevel { Name = "G" };

        // Square-of-Nine diagonal numbers. For n = 1..448: odd n -> n^2,
        // even n -> n^2 + 1. Reproduces the indicator's 448-level list
        // exactly (1, 5, 9, 17, 25, ..., 200705).
        private static readonly double[] GannLevels = BuildGannLevels();

        private static double[] BuildGannLevels()
        {
            double[] levels = new double[448];
            for (int n = 1; n <= 448; n++)
            {
                double sq = (double)n * n;
                levels[n - 1] = (n % 2 == 1) ? sq : sq + 1.0;
            }
            return levels;
        }

        // Nearest Gann number to a price. Exact hits return the level itself.
        // Ties go to the lower level.
        private static double NearestGannLevel(double price)
        {
            int idx = Array.BinarySearch(GannLevels, price);
            if (idx >= 0)
            {
                return GannLevels[idx];
            }
            int above = ~idx;
            if (above <= 0)
            {
                return GannLevels[0];
            }
            if (above >= GannLevels.Length)
            {
                return GannLevels[GannLevels.Length - 1];
            }
            double lower = GannLevels[above - 1];
            double upper = GannLevels[above];
            return (price - lower <= upper - price) ? lower : upper;
        }

        // ---------------------------------------------------------------------
        // Unified zone contact state
        // ---------------------------------------------------------------------
        private Zone activeZone;
        private int contactBar = -1;
        private Zone tradeZone;
        private bool inTrade;

        // ---------------------------------------------------------------------
        // Trade state
        // ---------------------------------------------------------------------
        private double plannedStopPrice;
        private string currentSignalName;
        private double lastEntryPrice;
        private int lastEntryDirection;
        private double initialStopTicks;
        private bool stopHalved;
        private bool fillRejected;
        private double tradeMAE;
        private double tradeMFE;
        private double tradeSLPts;
        private double tradeTgtPts;
        private string tradeTag;
        private string tradeAnchorTag;
        private double tradeZoneMin;
        private double tradeZoneMax;
        private DateTime tradeEntryTime;

        // ---------------------------------------------------------------------
        // Daily and cooldown state
        // ---------------------------------------------------------------------
        private int lastExitBar = -1;
        private bool wasInPosition;
        private DateTime lastResetDate;
        private DateTime lastRthEndDate;
        private int dailyTradeCount;
        private double dailyRealizedPnL;
        private bool dailyLossHit;
        private bool dailyProfitHit;

        // ---------------------------------------------------------------------
        // Diagnostics
        // ---------------------------------------------------------------------
        private StreamWriter tradeLog;
        private StreamWriter skippedLog;
        private List<ShadowTrade> pendingShadows = new List<ShadowTrade>();
        private const double MinimumTriggerDistancePoints = 1.0;
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description =
                    "SIM: Daily + Weekly VWAP + Gann pullback on 2-min bars. No anchored VWAP. "
                    + "Max stop 15 pts for every zone.";
                Name = "ESnoAonlyDWG";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 60;
                IsInstantiatedOnEachOptimizationIteration = false;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;

                // 02 | Indicators
                ATRPeriod = 14;

                // 03 | Arming and contact
                ArmDistanceATR = 2.0;
                TriggerWindowBars = 2;
                MaxStopOutsPerAnchor = 2;

                // 03b | Daily / Weekly VWAP
                EnableDailyVWAP = true;
                EnableWeeklyVWAP = true;
                EnableGannLevels = true;
                ConfluenceTolerancePoints = 2.0;

                // 04 | Trade management
                MaxStopPoints = 15.0;
                RRThresholdPoints = 5.0;
                HighRR = 2.0;
                LowRR = 1.0;
                ContractQty = 1;
                EnableStopHalving = true;
                CooldownBars = 1;

                // 05 | Session
                RTHStartHHMMSS = 083000;
                RTHEndHHMMSS = 150000;
                LastEntryHHMMSS = 143000;
                FlattenAtRTHEnd = true;

                // 06 | Risk controls
                MaxTradesPerDay = 20;
                DailyLossLimit = 100000.0;
                DailyProfitLimit = 100000.0;

                // 07 | Diagnostics
                EnableTradeLog = true;
                DrawAnchors = true;

                AddPlot(new Stroke(Brushes.DodgerBlue, 1), PlotStyle.Line, "DVWAP");
                AddPlot(new Stroke(Brushes.OrangeRed, 1), PlotStyle.Line, "WVWAP");
                AddPlot(new Stroke(Brushes.Purple, 1), PlotStyle.Line, "GANN");
            }
            else if (State == State.DataLoaded)
            {
                atr14 = ATR(ATRPeriod);

                lastExitBar = -1;
                wasInPosition = false;
                fillRejected = false;
                inTrade = false;
                activeZone = null;
                tradeZone = null;
                contactBar = -1;
                lastResetDate = Core.Globals.MinDate;
                lastRthEndDate = Core.Globals.MinDate;

                daily.ResetAccumulator();
                daily.ResetSession();
                weekly.ResetAccumulator();
                weekly.ResetSession();
                weeklySeeded = false;

                RemoveDrawObject(ActiveContactDrawTag);

                if (EnableTradeLog)
                {
                    string path = Path.Combine(
                        NinjaTrader.Core.Globals.UserDataDir,
                        "trades_" + Name + ".csv");
                    bool newFile = !File.Exists(path);
                    tradeLog = new StreamWriter(path, true);
                    if (newFile)
                    {
                        tradeLog.WriteLine(
                            "EntryTime,ExitTime,Dir,Tag,EntryPx,ExitPx,"
                            + "Qty,SLPts,TgtPts,PnL,MAEPts,MFEPts,"
                            + "ExitName,AnchorTag,ZoneMin,ZoneMax");
                    }
                    tradeLog.WriteLine(string.Format(
                        "# RUN START {0:yyyy-MM-dd HH:mm:ss} {1} {2}",
                        DateTime.Now, Instrument.FullName, Name));
                    tradeLog.Flush();

                    string skippedPath = Path.Combine(
                        NinjaTrader.Core.Globals.UserDataDir,
                        "skipped_trades_" + Name + ".csv");
                    bool skippedNewFile = !File.Exists(skippedPath);
                    skippedLog = new StreamWriter(skippedPath, true);
                    if (skippedNewFile)
                    {
                        skippedLog.WriteLine(
                            "Time,Dir,Pillar,AnchorTag,SignalPx,StopPx,"
                            + "StopPts,ATR,StopATR,StopPctATR,TgtPts,SkipStage,"
                            + "ZoneMin,ZoneMax,Outcome,BarsHeld,MFE,MAE");
                    }
                    skippedLog.WriteLine(string.Format(
                        "# RUN START {0:yyyy-MM-dd HH:mm:ss} {1} {2}",
                        DateTime.Now, Instrument.FullName, Name));
                    skippedLog.Flush();
                }
            }
            else if (State == State.Terminated)
            {
                if (tradeLog != null)
                {
                    tradeLog.Flush();
                    tradeLog.Close();
                    tradeLog = null;
                }
                if (skippedLog != null)
                {
                    skippedLog.Flush();
                    skippedLog.Close();
                    skippedLog = null;
                }
            }
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            // Daily / Weekly VWAP accumulate from the very first bar so the
            // weekly has full history by the time trading is allowed.
            UpdateDailyWeeklyVwaps();
            UpdateGannLevels();

            int warmup = ATRPeriod + 4;

            if (CurrentBar < warmup)
            {
                return;
            }

            // Shadow-track any setups the max-stop gate blocked, before this
            // bar's trigger evaluation can add a new one.
            UpdateShadowTrades();

            // -----------------------------------------------------------------
            // Daily reset
            // -----------------------------------------------------------------
            if (Time[0].Date != lastResetDate.Date)
            {
                lastResetDate = Time[0].Date;
                dailyTradeCount = 0;
                dailyRealizedPnL = 0.0;
                dailyLossHit = false;
                dailyProfitHit = false;
            }

            // -----------------------------------------------------------------
            // Detect transition from an open position to flat
            // -----------------------------------------------------------------
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (wasInPosition)
                {
                    wasInPosition = false;
                    // lastExitBar is always a primary (2-min) bar index.
                    if (lastExitBar < CurrentBars[0])
                    {
                        lastExitBar = CurrentBars[0];
                    }
                    inTrade = false;
                    tradeZone = null;
                }
            }
            else
            {
                wasInPosition = true;
                TrackExcursion();
            }

            // -----------------------------------------------------------------
            // RTH-end housekeeping
            // -----------------------------------------------------------------
            if (ToTime(Time[0]) >= RTHEndHHMMSS
                && lastRthEndDate.Date != Time[0].Date)
            {
                lastRthEndDate = Time[0].Date;

                if (FlattenAtRTHEnd
                    && Position.MarketPosition != MarketPosition.Flat)
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                    {
                        ExitLong("RTHClose", currentSignalName);
                    }
                    else if (Position.MarketPosition == MarketPosition.Short)
                    {
                        ExitShort("RTHClose", currentSignalName);
                    }
                    Print(string.Format(
                        "{0:yyyy-MM-dd HH:mm} RTH END | flattening position",
                        Time[0]));
                }

                activeZone = null;
                contactBar = -1;
                RemoveDrawObject(ActiveContactDrawTag);
            }

            // -----------------------------------------------------------------
            // Arm Daily / Weekly / Gann levels
            // -----------------------------------------------------------------
            ArmLevel(daily);
            ArmLevel(weekly);
            ArmLevel(gann);

            // -----------------------------------------------------------------
            // Manage an open trade
            // -----------------------------------------------------------------
            if (EnableStopHalving)
            {
                ManageStopHalving();
            }

            // -----------------------------------------------------------------
            // Unified zone contact / trigger
            // -----------------------------------------------------------------
            if (!inTrade && Position.MarketPosition == MarketPosition.Flat)
            {
                HandleZoneContact();
            }
        }
        #endregion

        #region Daily / Weekly VWAP
        private void UpdateDailyWeeklyVwaps()
        {
            bool firstBarOfSession = Bars.IsFirstBarOfSession;

            if (firstBarOfSession)
            {
                daily.ResetAccumulator();
                daily.ResetSession();
                weekly.ResetSession();

                // Only a true Sunday session open seeds the weekly. A partial
                // week (history starting mid-week) is never traded as W.
                if (Time[0].DayOfWeek == DayOfWeek.Sunday)
                {
                    weekly.ResetAccumulator();
                    weeklySeeded = true;
                }
            }

            double typicalPrice = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;

            daily.CumPV += typicalPrice * Volume[0];
            daily.CumV += Volume[0];
            daily.Value = daily.CumV > 0.0 ? daily.CumPV / daily.CumV : double.NaN;

            if (weeklySeeded)
            {
                weekly.CumPV += typicalPrice * Volume[0];
                weekly.CumV += Volume[0];
                weekly.Value = weekly.CumV > 0.0 ? weekly.CumPV / weekly.CumV : double.NaN;
            }

            Values[0][0] = EnableDailyVWAP && daily.IsLive ? daily.Value : double.NaN;
            Values[1][0] = EnableWeeklyVWAP && weekly.IsLive ? weekly.Value : double.NaN;
        }

        // Gann level for this bar: the Gann number nearest the close.
        // Session state (Armed / StopOuts / Dead) resets each session like D/W.
        // Disabled leaves the level empty so it can never be eligible.
        private void UpdateGannLevels()
        {
            if (Bars.IsFirstBarOfSession)
            {
                gann.ResetSession();
            }

            if (!EnableGannLevels)
            {
                gann.Value = double.NaN;
                Values[2][0] = double.NaN;
                return;
            }

            gann.Value = NearestGannLevel(Close[0]);
            Values[2][0] = gann.IsLive ? gann.Value : double.NaN;
        }

        private void ArmLevel(VwapLevel level)
        {
            if (level.Armed || level.Dead || !level.IsLive)
            {
                return;
            }

            double atrNow = atr14[0];
            if (atrNow <= 0.0)
            {
                return;
            }

            if (Math.Abs(Close[0] - level.Value) >= ArmDistanceATR * atrNow)
            {
                level.Armed = true;
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} ARMED {1} | close {2:F2} is {3:F1} ATR from {1} level {4:F2}",
                    Time[0], level.Name, Close[0],
                    Math.Abs(Close[0] - level.Value) / atrNow, level.Value));
            }
        }

        private void RegisterLevelOutcome(VwapLevel level, bool isStop)
        {
            // Any trade on the level forces a re-arm.
            level.Armed = false;

            if (isStop)
            {
                level.StopOuts++;
                if (level.StopOuts >= MaxStopOutsPerAnchor)
                {
                    level.Dead = true;
                    Print(string.Format(
                        "{0:yyyy-MM-dd HH:mm} LEVEL {1} DEAD | {2} consecutive stop-outs",
                        Time[0], level.Name, level.StopOuts));
                }
            }
            else
            {
                level.StopOuts = 0;
            }
        }
        #endregion

        #region Zone Logic
        private bool DailyEligible()
        {
            return EnableDailyVWAP && daily.IsLive && daily.Armed && !daily.Dead;
        }

        private bool WeeklyEligible()
        {
            return EnableWeeklyVWAP && weekly.IsLive && weekly.Armed && !weekly.Dead;
        }

        private bool GannEligible()
        {
            return EnableGannLevels && gann.IsLive && gann.Armed && !gann.Dead;
        }

        // Cluster eligible levels into zones. Levels sorted by value; a level
        // joins the current cluster when it is within tolerance of the
        // cluster's top edge.
        private List<Zone> BuildZones()
        {
            List<KeyValuePair<char, double>> levels = new List<KeyValuePair<char, double>>();

            if (DailyEligible())
                levels.Add(new KeyValuePair<char, double>('D', daily.Value));
            if (WeeklyEligible())
                levels.Add(new KeyValuePair<char, double>('W', weekly.Value));
            if (GannEligible())
                levels.Add(new KeyValuePair<char, double>('G', gann.Value));

            levels.Sort((x, y) => x.Value.CompareTo(y.Value));

            List<Zone> zones = new List<Zone>();
            Zone current = null;

            foreach (KeyValuePair<char, double> lv in levels)
            {
                if (current == null
                    || lv.Value - current.Max > ConfluenceTolerancePoints)
                {
                    current = new Zone { Min = lv.Value, Max = lv.Value };
                    zones.Add(current);
                }
                else
                {
                    current.Max = Math.Max(current.Max, lv.Value);
                }

                if (lv.Key == 'D') current.HasD = true;
                else if (lv.Key == 'W') current.HasW = true;
                else current.HasG = true;
            }

            return zones;
        }

        private bool ZoneMembersStillEligible(Zone z)
        {
            if (z.HasD && !DailyEligible()) return false;
            if (z.HasW && !WeeklyEligible()) return false;
            if (z.HasG && !GannEligible()) return false;
            return true;
        }

        // Members are frozen at contact; the bounds track live level values.
        private void RefreshZoneBounds(Zone z)
        {
            double mn = double.MaxValue;
            double mx = double.MinValue;

            if (z.HasD) { mn = Math.Min(mn, daily.Value); mx = Math.Max(mx, daily.Value); }
            if (z.HasW) { mn = Math.Min(mn, weekly.Value); mx = Math.Max(mx, weekly.Value); }
            if (z.HasG) { mn = Math.Min(mn, gann.Value); mx = Math.Max(mx, gann.Value); }

            z.Min = mn;
            z.Max = mx;
        }

        private bool IsContactBar(Zone z)
        {
            bool touches = Low[0] <= z.Max && High[0] >= z.Min;
            bool gapsThrough =
                (Open[0] > z.Max && Close[1] < z.Min)
                || (Open[0] < z.Min && Close[1] > z.Max);
            return touches || gapsThrough;
        }

        private void HandleZoneContact()
        {
            // -----------------------------------------------------------------
            // Existing contact: validate, refresh, expire or try to trigger.
            // -----------------------------------------------------------------
            if (activeZone != null)
            {
                if (!ZoneMembersStillEligible(activeZone))
                {
                    Print(string.Format(
                        "{0:yyyy-MM-dd HH:mm} ZONE {1} DROPPED | member no longer eligible",
                        Time[0], activeZone.Tag));
                    activeZone = null;
                    contactBar = -1;
                }
                else
                {
                    RefreshZoneBounds(activeZone);

                    if (CurrentBar - contactBar >= TriggerWindowBars)
                    {
                        Print(string.Format(
                            "{0:yyyy-MM-dd HH:mm} WINDOW EXPIRED {1}",
                            Time[0], activeZone.Tag));
                        activeZone = null;
                        contactBar = -1;
                    }
                    else
                    {
                        TryTrigger();
                        return;
                    }
                }
            }

            // -----------------------------------------------------------------
            // Fresh contact: pick the contacted zone nearest the close.
            // -----------------------------------------------------------------
            List<Zone> zones = BuildZones();
            Zone best = null;
            double bestDist = double.MaxValue;

            foreach (Zone z in zones)
            {
                if (!IsContactBar(z))
                {
                    continue;
                }
                double mid = (z.Min + z.Max) / 2.0;
                double dist = Math.Abs(Close[0] - mid);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = z;
                }
            }

            if (best == null)
            {
                return;
            }

            activeZone = best;
            contactBar = CurrentBar;

            if (DrawAnchors)
            {
                Draw.Dot(this, ActiveContactDrawTag, false, 0,
                    (best.Min + best.Max) / 2.0, Brushes.White);
            }

            Print(string.Format(
                "{0:yyyy-MM-dd HH:mm} CONTACT {1} | zone [{2:F2}, {3:F2}]",
                Time[0], best.Tag, best.Min, best.Max));

            // The contact bar may also be the trigger bar.
            TryTrigger();
        }
        #endregion

        #region Trigger
        private void TryTrigger()
        {
            if (activeZone == null)
            {
                return;
            }

            Zone z = activeZone;

            // Long: green close above the TOP of the cluster + buffer.
            // Short: red close below the BOTTOM of the cluster - buffer.
            bool longTrigger =
                Close[0] >= z.Max + MinimumTriggerDistancePoints
                && Close[0] > Open[0];

            bool shortTrigger =
                Close[0] <= z.Min - MinimumTriggerDistancePoints
                && Close[0] < Open[0];

            if (!longTrigger && !shortTrigger)
            {
                return;
            }

            string direction = longTrigger ? "LONG" : "SHORT";

            int currentTime = ToTime(Time[0]);
            if (currentTime < RTHStartHHMMSS || currentTime > LastEntryHHMMSS)
            {
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} {1} {2} BLOCKED | outside entry hours",
                    Time[0], direction, z.Tag));
                return;
            }

            if (dailyLossHit || dailyProfitHit)
            {
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} {1} {2} BLOCKED | daily limit",
                    Time[0], direction, z.Tag));
                return;
            }

            if (dailyTradeCount >= MaxTradesPerDay)
            {
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} {1} {2} BLOCKED | max trades",
                    Time[0], direction, z.Tag));
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                return;
            }

            if (CooldownBars > 0 && lastExitBar >= 0
                && CurrentBar - lastExitBar < CooldownBars)
            {
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} {1} {2} BLOCKED | cooldown",
                    Time[0], direction, z.Tag));
                return;
            }

            // Stop from contact through trigger extremes.
            int barsSinceContact = CurrentBar - contactBar;
            double highestPrice = double.MinValue;
            double lowestPrice = double.MaxValue;

            for (int i = 0; i <= barsSinceContact; i++)
            {
                highestPrice = Math.Max(highestPrice, High[i]);
                lowestPrice = Math.Min(lowestPrice, Low[i]);
            }

            plannedStopPrice = longTrigger
                ? lowestPrice - TickSize
                : highestPrice + TickSize;

            double stopProxyPoints = Math.Abs(Close[0] - plannedStopPrice);

            // Max stop: 15 pts for every zone (no Anchor in this variant).
            double maxStop = MaxStopPoints;
            if (stopProxyPoints > maxStop)
            {
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} {1} {2} BLOCKED | SL {3:F2} pts > max {4:F2}",
                    Time[0], direction, z.Tag, stopProxyPoints, maxStop));
                // Log the miss and shadow-track it: would the would-be target
                // or would-be stop have hit first? Real-time bars only, so a
                // restart's historical sweep never backfills this file.
                if (State == State.Realtime)
                {
                    double atrNow = atr14[0];
                    double rr = stopProxyPoints < RRThresholdPoints ? HighRR : LowRR;
                    pendingShadows.Add(new ShadowTrade
                    {
                        SignalTime = Time[0],
                        Direction = longTrigger ? "Long" : "Short",
                        Pillar = z.Tag,
                        AnchorTag = string.Empty,
                        SignalPx = Close[0],
                        StopPx = plannedStopPrice,
                        StopPts = stopProxyPoints,
                        Atr = atrNow,
                        TgtPts = stopProxyPoints * rr,
                        ZoneMin = z.Min,
                        ZoneMax = z.Max,
                        MFE = 0.0,
                        MAE = 0.0,
                        BarsHeld = 0
                    });
                }
                plannedStopPrice = 0.0;
                return;
            }

            double rewardRisk = stopProxyPoints < RRThresholdPoints ? HighRR : LowRR;
            double stopTicks = Math.Max(1.0, Math.Round(stopProxyPoints / TickSize));
            double targetTicks = Math.Max(1.0, Math.Round(stopTicks * rewardRisk));

            currentSignalName = longTrigger ? "ZoneLong" : "ZoneShort";

            tradeZone = new Zone
            {
                HasD = z.HasD, HasW = z.HasW, HasG = z.HasG,
                Min = z.Min, Max = z.Max
            };
            tradeTag = tradeZone.Tag;
            tradeAnchorTag = string.Empty;
            tradeZoneMin = z.Min;
            tradeZoneMax = z.Max;

            SetStopLoss(currentSignalName, CalculationMode.Ticks, stopTicks, false);
            SetProfitTarget(currentSignalName, CalculationMode.Ticks, targetTicks);

            if (longTrigger)
            {
                EnterLong(ContractQty, currentSignalName);
            }
            else
            {
                EnterShort(ContractQty, currentSignalName);
            }

            inTrade = true;
            activeZone = null;
            contactBar = -1;

            Print(string.Format(
                "{0:yyyy-MM-dd HH:mm} {1} SIGNAL {2} | zone [{3:F2}, {4:F2}] stop={5:F2} SL≈{6:F2}pts RR={7:F1}",
                Time[0], direction, tradeTag,
                z.Min, z.Max, plannedStopPrice, stopProxyPoints, rewardRisk));
        }
        #endregion

        #region OnOrderUpdate
        protected override void OnOrderUpdate(Order order, double limitPrice,
            double stopPrice, int quantity, int filled, double averageFillPrice,
            OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null || string.IsNullOrEmpty(currentSignalName))
            {
                return;
            }

            if (order.Name != currentSignalName)
            {
                return;
            }

            bool entryRejected = orderState == OrderState.Rejected;
            bool entryCancelledWithoutFill = orderState == OrderState.Cancelled && filled == 0;

            if (!entryRejected && !entryCancelledWithoutFill)
            {
                return;
            }

            Print(string.Format(
                "{0:yyyy-MM-dd HH:mm} ENTRY {1} | {2} — entry rejected/cancelled",
                time, orderState, error));

            inTrade = false;
            tradeZone = null;
            plannedStopPrice = 0.0;
            currentSignalName = null;
            tradeTag = null;
        }
        #endregion

        #region Trade Management
        private void ManageStopHalving()
        {
            if (stopHalved || initialStopTicks <= 0.0 || lastEntryPrice <= 0.0
                || string.IsNullOrEmpty(currentSignalName))
            {
                return;
            }

            MarketPosition marketPosition = Position.MarketPosition;
            if (marketPosition == MarketPosition.Flat)
            {
                return;
            }

            double favorableTicks;
            if (marketPosition == MarketPosition.Long)
            {
                favorableTicks = (High[0] - lastEntryPrice) / TickSize;
            }
            else
            {
                favorableTicks = (lastEntryPrice - Low[0]) / TickSize;
            }

            if (favorableTicks >= initialStopTicks)
            {
                double halvedStopTicks = Math.Max(1.0, Math.Round(initialStopTicks / 2.0));

                double newStopPrice = marketPosition == MarketPosition.Long
                    ? lastEntryPrice - halvedStopTicks * TickSize
                    : lastEntryPrice + halvedStopTicks * TickSize;

                double marketRef = marketPosition == MarketPosition.Long
                    ? GetCurrentBid()
                    : GetCurrentAsk();

                bool stopIsValid = marketPosition == MarketPosition.Long
                    ? newStopPrice < marketRef - TickSize
                    : newStopPrice > marketRef + TickSize;

                if (stopIsValid)
                {
                    SetStopLoss(currentSignalName, CalculationMode.Ticks, halvedStopTicks, false);
                    stopHalved = true;
                    Print(string.Format(
                        "{0:yyyy-MM-dd HH:mm} STOP HALVED | {1:F0}t → {2:F0}t",
                        Time[0], initialStopTicks, halvedStopTicks));
                }
                else
                {
                    Print(string.Format(
                        "{0:yyyy-MM-dd HH:mm} STOP HALVE SKIPPED | new {1} vs market {2} (level invalid)",
                        Time[0], newStopPrice, marketRef));
                }
            }
        }

        private void TrackExcursion()
        {
            if (lastEntryPrice <= 0.0)
            {
                return;
            }

            double favorableMove;
            double adverseMove;

            if (lastEntryDirection > 0)
            {
                favorableMove = High[0] - lastEntryPrice;
                adverseMove = lastEntryPrice - Low[0];
            }
            else
            {
                favorableMove = lastEntryPrice - Low[0];
                adverseMove = High[0] - lastEntryPrice;
            }

            tradeMFE = Math.Max(tradeMFE, favorableMove);
            tradeMAE = Math.Max(tradeMAE, adverseMove);
        }
        #endregion

        #region OnExecutionUpdate
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId,
            DateTime time)
        {
            if (execution == null || execution.Order == null
                || execution.Order.OrderState != OrderState.Filled)
            {
                return;
            }

            OrderAction action = execution.Order.OrderAction;

            // -----------------------------------------------------------------
            // Entry fill
            // -----------------------------------------------------------------
            if (action == OrderAction.Buy || action == OrderAction.SellShort)
            {
                bool isLong = action == OrderAction.Buy;

                lastEntryPrice = price;
                lastEntryDirection = isLong ? 1 : -1;
                dailyTradeCount++;
                wasInPosition = true;
                tradeMAE = 0.0;
                tradeMFE = 0.0;
                tradeEntryTime = time;
                fillRejected = false;

                if (plannedStopPrice <= 0.0)
                {
                    return;
                }

                double stopPoints = isLong
                    ? price - plannedStopPrice
                    : plannedStopPrice - price;

                stopPoints = Math.Max(TickSize, stopPoints);

                double maxStop = MaxStopPoints;

                if (stopPoints > maxStop)
                {
                    fillRejected = true;
                    dailyTradeCount--;
                    tradeSLPts = stopPoints;
                    tradeTgtPts = 0.0;

                    Print(string.Format(
                        "{0:yyyy-MM-dd HH:mm} FILL REJECTED | SL {1:F2} pts > max {2:F2} — flattening",
                        time, stopPoints, maxStop));

                    // No shadow to track: the trade was filled and flattened
                    // at once, so its outcome is known. Log it as missed.
                    if (State == State.Realtime)
                    {
                        WriteSkippedRow(new ShadowTrade
                        {
                            SignalTime = time,
                            Direction = isLong ? "Long" : "Short",
                            Pillar = tradeTag,
                            AnchorTag = tradeAnchorTag,
                            SignalPx = price,
                            StopPx = plannedStopPrice,
                            StopPts = stopPoints,
                            Atr = atr14[0],
                            TgtPts = 0.0,
                            ZoneMin = tradeZoneMin,
                            ZoneMax = tradeZoneMax,
                            MFE = 0.0,
                            MAE = 0.0,
                            BarsHeld = 0
                        }, "FILL_REJECTED_SL", "FLATTENED");
                    }

                    if (isLong)
                    {
                        ExitLong("MaxStopExceeded", currentSignalName);
                    }
                    else
                    {
                        ExitShort("MaxStopExceeded", currentSignalName);
                    }
                    return;
                }

                double rewardRisk = stopPoints < RRThresholdPoints ? HighRR : LowRR;
                double targetPoints = stopPoints * rewardRisk;
                double targetPrice = isLong ? price + targetPoints : price - targetPoints;

                SetStopLoss(currentSignalName, CalculationMode.Price, plannedStopPrice, false);
                SetProfitTarget(currentSignalName, CalculationMode.Price, targetPrice);

                initialStopTicks = Math.Round(stopPoints / TickSize);
                stopHalved = false;
                tradeSLPts = stopPoints;
                tradeTgtPts = targetPoints;

                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} {1} FILL @ {2:F2} | stop={3:F2} ({4:F2}pts) target={5:F2} ({6:F2}pts, {7:F1}:1)",
                    time, isLong ? "LONG" : "SHORT", price, plannedStopPrice,
                    stopPoints, targetPrice, targetPoints, rewardRisk));
                return;
            }

            // -----------------------------------------------------------------
            // Exit fill
            // -----------------------------------------------------------------
            if (action == OrderAction.Sell || action == OrderAction.BuyToCover)
            {
                // Record the primary (2-min) bar index so the
                // cooldown checks compare like with like.
                if (lastExitBar < CurrentBars[0])
                {
                    lastExitBar = CurrentBars[0];
                }

                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    wasInPosition = false;
                }

                if (lastEntryPrice == 0.0)
                {
                    return;
                }

                double pointValue = Instrument.MasterInstrument.PointValue;
                double pnl = lastEntryDirection * (price - lastEntryPrice) * quantity * pointValue;
                dailyRealizedPnL += pnl;

                string exitName = execution.Order.Name;

                if (lastEntryDirection > 0)
                {
                    tradeMFE = Math.Max(tradeMFE, price - lastEntryPrice);
                    tradeMAE = Math.Max(tradeMAE, lastEntryPrice - price);
                }
                else
                {
                    tradeMFE = Math.Max(tradeMFE, lastEntryPrice - price);
                    tradeMAE = Math.Max(tradeMAE, price - lastEntryPrice);
                }

                if (fillRejected)
                {
                    fillRejected = false;
                    Print(string.Format(
                        "{0:yyyy-MM-dd HH:mm} REJECTED-FILL FLATTENED @ {1:F2} | P&L=${2:F2} (not counted as trade/stop-out)",
                        time, price, pnl));
                }
                else
                {
                    bool isStop =
                        exitName == "Stop loss"
                        || (exitName != "Profit target" && pnl < 0.0);

                    Zone tz = tradeZone;

                    // Daily / Weekly / Gann outcomes.
                    if (tz != null && tz.HasD)
                    {
                        RegisterLevelOutcome(daily, isStop);
                    }
                    if (tz != null && tz.HasW)
                    {
                        RegisterLevelOutcome(weekly, isStop);
                    }
                    if (tz != null && tz.HasG)
                    {
                        RegisterLevelOutcome(gann, isStop);
                    }

                    Print(string.Format(
                        "{0:yyyy-MM-dd HH:mm} EXIT {1} @ {2:F2} ({3}) | P&L=${4:F2} Daily=${5:F2} MAE={6:F2} MFE={7:F2}",
                        time, tradeTag, price, exitName, pnl, dailyRealizedPnL, tradeMAE, tradeMFE));
                }

                if (tradeLog != null)
                {
                    tradeLog.WriteLine(string.Join(",",
                        tradeEntryTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        time.ToString("yyyy-MM-dd HH:mm:ss"),
                        lastEntryDirection > 0 ? "Long" : "Short",
                        tradeTag,
                        lastEntryPrice.ToString("F2"),
                        price.ToString("F2"),
                        quantity,
                        tradeSLPts.ToString("F2"),
                        tradeTgtPts.ToString("F2"),
                        pnl.ToString("F2"),
                        tradeMAE.ToString("F2"),
                        tradeMFE.ToString("F2"),
                        exitName,
                        tradeAnchorTag,
                        tradeZoneMin.ToString("F2"),
                        tradeZoneMax.ToString("F2")));
                    tradeLog.Flush();
                }

                if (DailyLossLimit > 0.0 && dailyRealizedPnL <= -Math.Abs(DailyLossLimit))
                {
                    dailyLossHit = true;
                }
                if (DailyProfitLimit > 0.0 && dailyRealizedPnL >= Math.Abs(DailyProfitLimit))
                {
                    dailyProfitHit = true;
                }

                // Clear per-trade state
                lastEntryPrice = 0.0;
                lastEntryDirection = 0;
                currentSignalName = null;
                plannedStopPrice = 0.0;
                initialStopTicks = 0.0;
                stopHalved = false;
                inTrade = false;
                tradeZone = null;
                return;
            }
        }
        #endregion

        #region Skipped-trade log (v1.1.1)
        // ---------------------------------------------------------------------
        // A blocked setup is shadow-tracked bar by bar: did its would-be target
        // or would-be stop print first? Written to skipped_trades_<Name>.csv
        // only when the outcome is known (or RTH end leaves it OPEN).
        // ---------------------------------------------------------------------
        private void UpdateShadowTrades()
        {
            if (pendingShadows.Count == 0)
            {
                return;
            }

            bool rthEnded = ToTime(Time[0]) >= RTHEndHHMMSS;

            for (int i = pendingShadows.Count - 1; i >= 0; i--)
            {
                ShadowTrade s = pendingShadows[i];
                s.BarsHeld++;

                bool isLong = s.Direction == "Long";
                double targetPx = isLong ? s.SignalPx + s.TgtPts : s.SignalPx - s.TgtPts;

                if (isLong)
                {
                    s.MFE = Math.Max(s.MFE, High[0] - s.SignalPx);
                    s.MAE = Math.Max(s.MAE, s.SignalPx - Low[0]);
                }
                else
                {
                    s.MFE = Math.Max(s.MFE, s.SignalPx - Low[0]);
                    s.MAE = Math.Max(s.MAE, High[0] - s.SignalPx);
                }

                bool hitTgt = isLong ? High[0] >= targetPx : Low[0] <= targetPx;
                bool hitStop = isLong ? Low[0] <= s.StopPx : High[0] >= s.StopPx;

                string outcome = null;
                if (hitTgt && hitStop)
                {
                    outcome = "AMBIG";
                }
                else if (hitTgt)
                {
                    outcome = "WIN";
                }
                else if (hitStop)
                {
                    outcome = "LOSS";
                }
                else if (rthEnded || s.SignalTime.Date != Time[0].Date)
                {
                    outcome = "OPEN";
                }

                if (outcome != null)
                {
                    WriteSkippedRow(s, "BLOCKED_SL", outcome);
                    pendingShadows.RemoveAt(i);
                }
            }
        }

        private void WriteSkippedRow(ShadowTrade s, string stage, string outcome)
        {
            if (skippedLog == null)
            {
                return;
            }

            skippedLog.WriteLine(string.Join(",",
                s.SignalTime.ToString("yyyy-MM-dd HH:mm:ss"),
                s.Direction,
                s.Pillar,
                s.AnchorTag,
                s.SignalPx.ToString("F2"),
                s.StopPx.ToString("F2"),
                s.StopPts.ToString("F2"),
                s.Atr.ToString("F2"),
                (s.StopPts / Math.Max(0.01, s.Atr)).ToString("F2"),
                (s.StopPts / Math.Max(0.01, s.Atr) * 100.0).ToString("F0"),
                s.TgtPts.ToString("F2"),
                stage,
                s.ZoneMin.ToString("F2"),
                s.ZoneMax.ToString("F2"),
                outcome,
                s.BarsHeld,
                s.MFE.ToString("F2"),
                s.MAE.ToString("F2")));
            skippedLog.Flush();
        }
        #endregion

        #region Properties
        // 02 | Indicators
        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name = "ATR Period", Order = 1, GroupName = "02 | Indicators")]
        public int ATRPeriod { get; set; }

        // 03 | Arming / Contact
        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "Arm Distance (ATR)", Order = 1, GroupName = "03 | Arming / Contact")]
        public double ArmDistanceATR { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Trigger Window (bars)", Order = 3, GroupName = "03 | Arming / Contact")]
        public int TriggerWindowBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max Consecutive Stop-outs per Level", Order = 4, GroupName = "03 | Arming / Contact")]
        public int MaxStopOutsPerAnchor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Daily VWAP", Order = 5, GroupName = "03 | Arming / Contact")]
        public bool EnableDailyVWAP { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Weekly VWAP", Order = 6, GroupName = "03 | Arming / Contact")]
        public bool EnableWeeklyVWAP { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Gann Levels", Description = "Trade the nearest Gann number as a peer level.", Order = 7, GroupName = "03 | Arming / Contact")]
        public bool EnableGannLevels { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 20.0)]
        [Display(Name = "Confluence Tolerance (pts)",
            Description = "Eligible levels within this distance merge into one zone.",
            Order = 8, GroupName = "03 | Arming / Contact")]
        public double ConfluenceTolerancePoints { get; set; }

        // 04 | Trade Management
        [NinjaScriptProperty]
        [Range(0.25, 50.0)]
        [Display(Name = "Max Stop (pts)", Order = 1, GroupName = "04 | Trade Management")]
        public double MaxStopPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 50.0)]
        [Display(Name = "RR Threshold (pts)", Order = 3, GroupName = "04 | Trade Management")]
        public double RRThresholdPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "High RR", Order = 4, GroupName = "04 | Trade Management")]
        public double HighRR { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Low RR", Order = 5, GroupName = "04 | Trade Management")]
        public double LowRR { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Contract Qty", Order = 6, GroupName = "04 | Trade Management")]
        public int ContractQty { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Stop Halving", Order = 7, GroupName = "04 | Trade Management")]
        public bool EnableStopHalving { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Cooldown (bars)", Order = 8, GroupName = "04 | Trade Management")]
        public int CooldownBars { get; set; }

        // 05 | Session
        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "RTH Start (HHMMSS)", Order = 1, GroupName = "05 | Session")]
        public int RTHStartHHMMSS { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "RTH End (HHMMSS)", Order = 2, GroupName = "05 | Session")]
        public int RTHEndHHMMSS { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Last Entry (HHMMSS)", Order = 3, GroupName = "05 | Session")]
        public int LastEntryHHMMSS { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Flatten At RTH End", Order = 5, GroupName = "05 | Session")]
        public bool FlattenAtRTHEnd { get; set; }

        // 06 | Risk Controls
        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Max Trades Per Day", Order = 1, GroupName = "06 | Risk Controls")]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100000.0)]
        [Display(Name = "Daily Loss Limit ($)", Order = 2, GroupName = "06 | Risk Controls")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100000.0)]
        [Display(Name = "Daily Profit Limit ($)", Order = 3, GroupName = "06 | Risk Controls")]
        public double DailyProfitLimit { get; set; }

        // 07 | Diagnostics
        [NinjaScriptProperty]
        [Display(Name = "Enable Trade Log (CSV)", Order = 1, GroupName = "07 | Diagnostics")]
        public bool EnableTradeLog { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Anchors / Contacts", Order = 2, GroupName = "07 | Diagnostics")]
        public bool DrawAnchors { get; set; }

        #endregion
    }
}

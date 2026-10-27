//
// ESCombinedVWAPSim.cs
// NinjaTrader 8 Strategy — Combined Anchor + Daily + Weekly VWAP (SIM) v1.0.0
//
// Built on ESAVWAPAnchor v1.4.0. The anchor engine (pocket-pivot combos,
// BigBody / Swing qualification, right-side swing confirmation, continuation
// lock, news anchor, AVWAP plot) is carried over unchanged. Trade management
// (max stop, RR tiers, stop-halving guard, cooldown, daily limits, RTH hours,
// fill rejection) is carried over unchanged.
//
// What is new:
// - Three peer levels: Anchor VWAP (A), Daily VWAP (D), Weekly VWAP (W).
//   No priority between them. Any eligible level can produce a trade.
// - Eligible levels whose values are within ConfluenceTolerancePoints of
//   each other collapse into ONE zone [min,max]. Tags: A, D, W, A+D, A+W,
//   D+W, A+D+W.
// - Contact = bar touches the zone (or gaps through it). Trigger within
//   TriggerWindowBars: Long = green close >= zone.max + 1 pt;
//   Short = red close <= zone.min - 1 pt. Price must clear the whole
//   cluster before entry.
// - One position at a time. A stop-out charges every member of the zone
//   that produced the trade. MaxStopOutsPerAnchor consecutive stop-outs
//   kill that level (anchor until RTH end, D/W for the session).
// - D and W arm per level (ArmDistanceATR x ATR14 away from the level) and
//   must re-arm after any trade on them. Weekly VWAP seeds only on a true
//   Sunday session open; until then it is not traded.
//
// Platform: NinjaTrader 8, ES/MES, 5-minute, ETH session template,
// chart time zone = US Central (same assumption as ESAVWAPAnchor).
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
    public class ESCombinedVWAPSim : Strategy
    {
        #region Types
        private enum AnchorState
        {
            Idle,
            Anchored,
            Armed,
            InTrade,
            Dead
        }

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
            public bool HasA;
            public bool HasD;
            public bool HasW;
            public double Min;
            public double Max;

            public string Tag
            {
                get
                {
                    string t = string.Empty;
                    if (HasA) t += "A";
                    if (HasD) t += (t.Length > 0 ? "+" : "") + "D";
                    if (HasW) t += (t.Length > 0 ? "+" : "") + "W";
                    return t;
                }
            }
        }
        #endregion

        #region Fields
        // ---------------------------------------------------------------------
        // Bar classification
        // ---------------------------------------------------------------------
        private Series<bool> isBullPP;
        private Series<bool> isBearPP;
        private Series<bool> isGreenBar;
        private Series<bool> isRedBar;
        private ATR atr14;
        private SMA volSma;

        // ---------------------------------------------------------------------
        // Anchor state (unchanged from ESAVWAPAnchor)
        // ---------------------------------------------------------------------
        private AnchorState state = AnchorState.Idle;
        private int anchorBar = -1;
        private int comboEndBar = -1;
        private double anchorClose;
        private double anchorATR;
        private string anchorTag = string.Empty;

        private bool hasPendingSwing;
        private int pendingSwingBar = -1;
        private int pendingSwingComboEnd = -1;
        private bool pendingSwingIsHigh;
        private double pendingSwingExtreme;
        private double pendingSwingATR;

        private double cumPV;
        private double cumV;
        private double avwap;
        private int plotStartBar = -1;
        private int anchorSeq;
        private int anchorStopOuts;
        private DateTime lastAnchorKillDate;

        private const string ActiveAnchorDrawTag = "ESCombinedVWAPSim.ActiveAnchor";
        private const string ActiveAnchorTextTag = "ESCombinedVWAPSim.ActiveAnchorText";
        private const string ActiveContactDrawTag = "ESCombinedVWAPSim.ActiveContact";

        // ---------------------------------------------------------------------
        // Daily / Weekly VWAP levels
        // ---------------------------------------------------------------------
        private readonly VwapLevel daily = new VwapLevel { Name = "D" };
        private readonly VwapLevel weekly = new VwapLevel { Name = "W" };
        private bool weeklySeeded;

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
        private int dailyTradeCount;
        private double dailyRealizedPnL;
        private bool dailyLossHit;
        private bool dailyProfitHit;

        // ---------------------------------------------------------------------
        // Diagnostics
        // ---------------------------------------------------------------------
        private StreamWriter tradeLog;
        private const double MinimumTriggerDistancePoints = 1.0;
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description =
                    "SIM: Anchored + Daily + Weekly VWAP pullback. Peer levels "
                    + "merge into a zone when within tolerance.";
                Name = "ESCombinedVWAPSim";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 60;
                IsInstantiatedOnEachOptimizationIteration = false;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;

                // 01 | Pocket Pivot
                PocketPivotLookback = 10;
                VolumeAverageLength = 50;

                // 02 | Anchor qualification
                ATRPeriod = 14;
                SwingLookbackBars = 8;
                SwingToleranceATR = 0.5;
                SwingLegATR = 1.0;
                BigBodyATR = 1.5;
                BigBodyRangePct = 0.70;
                EnableContinuationFilter = false;
                PreComboThrustBars = 3;
                PreComboThrustATR = 1.0;
                EnableSwingConfirmation = true;
                SwingConfirmBars = 3;
                EnableContinuationLock = true;
                ContinuationLockMinBodyPoints = 1.0;
                EnableNewsAnchor = false;
                NewsAnchorTimes = "07:30,08:30,13:00";
                NewsAnchorATRMult = 2.0;
                NewsAnchorVolumeMult = 2.5;

                // 03 | Arming and contact
                ArmDistanceATR = 2.0;
                ArmMinBars = 3;
                TriggerWindowBars = 2;
                MaxStopOutsPerAnchor = 2;

                // 03b | Daily / Weekly VWAP
                EnableDailyVWAP = true;
                EnableWeeklyVWAP = true;
                ConfluenceTolerancePoints = 2.0;

                // 04 | Trade management
                MaxStopPoints = 7.0;
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
                NoNewAnchorHHMMSS = 143000;
                FlattenAtRTHEnd = true;

                // 06 | Risk controls
                MaxTradesPerDay = 6;
                DailyLossLimit = 500.0;
                DailyProfitLimit = 900.0;

                // 07 | Diagnostics
                EnableTradeLog = true;
                DrawAnchors = true;

                AddPlot(new Stroke(Brushes.Gold, 2), PlotStyle.Line, "AVWAP");
                AddPlot(new Stroke(Brushes.DodgerBlue, 1), PlotStyle.Line, "DVWAP");
                AddPlot(new Stroke(Brushes.OrangeRed, 1), PlotStyle.Line, "WVWAP");
            }
            else if (State == State.DataLoaded)
            {
                isBullPP = new Series<bool>(this, MaximumBarsLookBack.Infinite);
                isBearPP = new Series<bool>(this, MaximumBarsLookBack.Infinite);
                isGreenBar = new Series<bool>(this, MaximumBarsLookBack.Infinite);
                isRedBar = new Series<bool>(this, MaximumBarsLookBack.Infinite);

                atr14 = ATR(ATRPeriod);
                volSma = SMA(Volume, VolumeAverageLength);

                state = AnchorState.Idle;
                lastExitBar = -1;
                wasInPosition = false;
                fillRejected = false;
                inTrade = false;
                activeZone = null;
                tradeZone = null;
                contactBar = -1;
                lastResetDate = Core.Globals.MinDate;
                lastAnchorKillDate = Core.Globals.MinDate;
                plotStartBar = -1;

                daily.ResetAccumulator();
                daily.ResetSession();
                weekly.ResetAccumulator();
                weekly.ResetSession();
                weeklySeeded = false;

                ClearActiveAnchorDrawings();
                ClearAvwapPlot();

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
            }
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            // Daily / Weekly VWAP accumulate from the very first bar so the
            // weekly has full history by the time trading is allowed.
            UpdateDailyWeeklyVwaps();

            int warmup =
                Math.Max(Math.Max(PocketPivotLookback, VolumeAverageLength), ATRPeriod)
                + SwingLookbackBars
                + PreComboThrustBars
                + 4;

            if (CurrentBar < warmup)
            {
                Values[0][0] = double.NaN;
                return;
            }

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
                ClearPendingSwing();
            }

            // -----------------------------------------------------------------
            // Detect transition from an open position to flat
            // -----------------------------------------------------------------
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (wasInPosition)
                {
                    wasInPosition = false;
                    if (lastExitBar < CurrentBar)
                    {
                        lastExitBar = CurrentBar;
                    }
                    if (state == AnchorState.InTrade)
                    {
                        state = AnchorState.Armed;
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
            // Classify current bar
            // -----------------------------------------------------------------
            ClassifyCurrentBar();

            // -----------------------------------------------------------------
            // RTH-end housekeeping
            // -----------------------------------------------------------------
            if (ToTime(Time[0]) >= RTHEndHHMMSS
                && lastAnchorKillDate.Date != Time[0].Date)
            {
                lastAnchorKillDate = Time[0].Date;

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

                if (state != AnchorState.Idle)
                {
                    Print(string.Format(
                        "{0:yyyy-MM-dd HH:mm} RTH END | anchor #{1} killed",
                        Time[0], anchorSeq));
                    ResetAnchor(AnchorState.Idle);
                    ClearPendingSwing();
                }

                activeZone = null;
                contactBar = -1;
                RemoveDrawObject(ActiveContactDrawTag);
            }

            // -----------------------------------------------------------------
            // Update active AVWAP
            // -----------------------------------------------------------------
            if (IsLiveAnchorState())
            {
                double typicalPrice = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;
                cumPV += typicalPrice * Volume[0];
                cumV += Volume[0];
                avwap = cumV > 0.0 ? cumPV / cumV : Close[0];
                Values[0][0] = avwap;
            }
            else
            {
                Values[0][0] = double.NaN;
            }

            // -----------------------------------------------------------------
            // Arm Daily / Weekly levels
            // -----------------------------------------------------------------
            ArmLevel(daily);
            ArmLevel(weekly);

            // -----------------------------------------------------------------
            // Manage an open trade
            // -----------------------------------------------------------------
            if (EnableStopHalving)
            {
                ManageStopHalving();
            }

            // -----------------------------------------------------------------
            // Detect newer anchor candidates (blocked while any trade is on)
            // -----------------------------------------------------------------
            if (!inTrade && Position.MarketPosition == MarketPosition.Flat)
            {
                TryDetectCandidate();
                TryConfirmPendingSwing();
            }

            // -----------------------------------------------------------------
            // Anchor arming
            // -----------------------------------------------------------------
            if (state == AnchorState.Anchored)
            {
                HandleAnchoredState();
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

            Values[1][0] = EnableDailyVWAP && daily.IsLive ? daily.Value : double.NaN;
            Values[2][0] = EnableWeeklyVWAP && weekly.IsLive ? weekly.Value : double.NaN;
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
                    "{0:yyyy-MM-dd HH:mm} ARMED {1} | close {2:F2} is {3:F1} ATR from {1}VWAP {4:F2}",
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
        private bool AnchorEligible()
        {
            return state == AnchorState.Armed && avwap > 0.0;
        }

        private bool DailyEligible()
        {
            return EnableDailyVWAP && daily.IsLive && daily.Armed && !daily.Dead;
        }

        private bool WeeklyEligible()
        {
            return EnableWeeklyVWAP && weekly.IsLive && weekly.Armed && !weekly.Dead;
        }

        // Cluster eligible levels into zones. Levels sorted by value; a level
        // joins the current cluster when it is within tolerance of the
        // cluster's top edge.
        private List<Zone> BuildZones()
        {
            List<KeyValuePair<char, double>> levels = new List<KeyValuePair<char, double>>();

            if (AnchorEligible())
                levels.Add(new KeyValuePair<char, double>('A', avwap));
            if (DailyEligible())
                levels.Add(new KeyValuePair<char, double>('D', daily.Value));
            if (WeeklyEligible())
                levels.Add(new KeyValuePair<char, double>('W', weekly.Value));

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

                if (lv.Key == 'A') current.HasA = true;
                else if (lv.Key == 'D') current.HasD = true;
                else current.HasW = true;
            }

            return zones;
        }

        private bool ZoneMembersStillEligible(Zone z)
        {
            if (z.HasA && !AnchorEligible()) return false;
            if (z.HasD && !DailyEligible()) return false;
            if (z.HasW && !WeeklyEligible()) return false;
            return true;
        }

        // Members are frozen at contact; the bounds track live level values.
        private void RefreshZoneBounds(Zone z)
        {
            double mn = double.MaxValue;
            double mx = double.MinValue;

            if (z.HasA) { mn = Math.Min(mn, avwap); mx = Math.Max(mx, avwap); }
            if (z.HasD) { mn = Math.Min(mn, daily.Value); mx = Math.Max(mx, daily.Value); }
            if (z.HasW) { mn = Math.Min(mn, weekly.Value); mx = Math.Max(mx, weekly.Value); }

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

        #region Anchor State Machine
        private void HandleAnchoredState()
        {
            if (anchorATR <= 0.0)
            {
                return;
            }

            bool minimumBarsElapsed = CurrentBar - comboEndBar >= ArmMinBars;
            bool distanceReached =
                Math.Abs(Close[0] - anchorClose) >= ArmDistanceATR * anchorATR;

            if (minimumBarsElapsed && distanceReached)
            {
                state = AnchorState.Armed;
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} ARMED A #{1} | close {2:F2} is {3:F1} ATR from anchor {4:F2}",
                    Time[0], anchorSeq, Close[0],
                    Math.Abs(Close[0] - anchorClose) / anchorATR, anchorClose));
            }
        }

        private bool IsLiveAnchorState()
        {
            return state == AnchorState.Anchored
                || state == AnchorState.Armed
                || state == AnchorState.InTrade;
        }
        #endregion

        #region Candidate Detection
        private void TryDetectCandidate()
        {
            int comboLen = 0;

            if (isBullPP[0] && isBullPP[1])
            {
                comboLen = 2;
            }
            else if (isBearPP[0] && isBearPP[1])
            {
                comboLen = 2;
            }
            else if (isBullPP[0] && isGreenBar[1] && isBullPP[2])
            {
                comboLen = 3;
            }
            else if (isBearPP[0] && isRedBar[1] && isBearPP[2])
            {
                comboLen = 3;
            }

            if (comboLen == 0)
            {
                if (EnableNewsAnchor)
                {
                    TryInstallNewsAnchor();
                }
                return;
            }

            int firstOffset = comboLen - 1;
            int candidateBar = CurrentBar - firstOffset;

            if (state != AnchorState.Idle && candidateBar <= comboEndBar)
            {
                return;
            }

            int currentTime = ToTime(Time[0]);
            if (currentTime > NoNewAnchorHHMMSS && currentTime < RTHEndHHMMSS)
            {
                return;
            }

            double atrAtCandidate = atr14[firstOffset];
            if (atrAtCandidate <= 0.0)
            {
                return;
            }

            bool isBigBody = IsBigBody(firstOffset, atrAtCandidate);

            bool isSwingHigh = false;
            double swingExtreme = 0.0;
            bool isSwing = false;

            if (!isBigBody)
            {
                isSwing = IsSwing(firstOffset, comboLen, atrAtCandidate,
                    out isSwingHigh, out swingExtreme);
            }

            if (!isBigBody && !isSwing)
            {
                return;
            }

            if (isSwing && EnableSwingConfirmation)
            {
                PendSwingCandidate(candidateBar, isSwingHigh, swingExtreme, atrAtCandidate);
                return;
            }

            InstallAnchor(candidateBar, atrAtCandidate, isBigBody ? "BigBody" : "Swing");
        }

        private void TryInstallNewsAnchor()
        {
            if (!IsNewsAnchorTime())
            {
                return;
            }

            int currentTime = ToTime(Time[0]);
            if (currentTime > NoNewAnchorHHMMSS && currentTime < RTHEndHHMMSS)
            {
                return;
            }

            double atrNow = atr14[0];
            if (atrNow <= 0.0)
            {
                return;
            }

            double body = Math.Abs(Close[0] - Open[0]);
            double range = High[0] - Low[0];
            double averageVolume = volSma[0];
            double needBody = NewsAnchorATRMult * atrNow;
            double rangePct = range > 0.0 ? body / range : 0.0;
            double needVolume = NewsAnchorVolumeMult * averageVolume;

            bool bigEnough =
                range > 0.0
                && body >= needBody
                && body >= BigBodyRangePct * range;

            bool loudEnough =
                averageVolume > 0.0
                && Volume[0] > needVolume;

            if (!bigEnough || !loudEnough)
            {
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} NEWS SKIP | O={1:F2} H={2:F2} L={3:F2} C={4:F2} "
                    + "body={5:F2} pts (need >={6:F2} = {7:F1}x ATR {8:F2}, range {9:P0}) "
                    + "| vol={10:N0} (need >{11:F1}x avg {12:N0})",
                    BarOpenTime(), Open[0], High[0], Low[0], Close[0], body, needBody,
                    NewsAnchorATRMult, atrNow, rangePct, Volume[0],
                    NewsAnchorVolumeMult, averageVolume));
                return;
            }

            InstallAnchor(CurrentBar, atrNow, "News");
        }

        private bool IsNewsAnchorTime()
        {
            if (string.IsNullOrWhiteSpace(NewsAnchorTimes))
            {
                return false;
            }

            TimeSpan barTime = BarOpenTime().TimeOfDay;
            string[] tokens = NewsAnchorTimes.Split(',');

            foreach (string token in tokens)
            {
                string[] hm = token.Trim().Split(':');
                if (hm.Length != 2)
                {
                    continue;
                }

                int hours;
                int minutes;
                if (!int.TryParse(hm[0], out hours) || !int.TryParse(hm[1], out minutes))
                {
                    continue;
                }

                if (barTime.Hours == hours && barTime.Minutes == minutes)
                {
                    return true;
                }
            }

            return false;
        }

        private DateTime BarOpenTime()
        {
            DateTime closeTime = Time[0];
            if (BarsPeriod.BarsPeriodType == BarsPeriodType.Minute)
            {
                return closeTime.AddMinutes(-BarsPeriod.Value);
            }
            if (BarsPeriod.BarsPeriodType == BarsPeriodType.Second)
            {
                return closeTime.AddSeconds(-BarsPeriod.Value);
            }
            return closeTime;
        }

        private void InstallAnchor(int candidateBar, double atrAtCandidate, string qualificationTag)
        {
            int firstOffset = CurrentBar - candidateBar;
            bool replacing = state != AnchorState.Idle;

            if (replacing && EnableContinuationLock && IsContinuationMove(candidateBar))
            {
                int lockCandidateOffset = CurrentBar - candidateBar;
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} ANCHOR LOCK | kept #{1} ({2}) | candidate bar {3:HH:mm} "
                    + "close={4:F2} is continuation of the impulse",
                    Time[0], anchorSeq, anchorTag,
                    Time[lockCandidateOffset], Close[lockCandidateOffset]));
                return;
            }

            ClearActiveAnchorDrawings();
            ClearAvwapPlot();
            ResetAnchor(AnchorState.Anchored);

            // A zone that included the old anchor is no longer valid.
            if (activeZone != null && activeZone.HasA)
            {
                activeZone = null;
                contactBar = -1;
            }

            anchorSeq++;
            anchorBar = candidateBar;
            comboEndBar = CurrentBar;
            anchorClose = Close[firstOffset];
            anchorATR = atrAtCandidate;
            anchorTag = qualificationTag;

            RebuildAvwapPlot(firstOffset);

            if (DrawAnchors)
            {
                double anchorDrawPrice;
                Brush anchorBrush;

                if (qualificationTag == "BigBody")
                {
                    anchorDrawPrice = Close[firstOffset];
                    anchorBrush = Brushes.Magenta;
                }
                else
                {
                    anchorDrawPrice = High[firstOffset] + 2.0 * TickSize;
                    anchorBrush = Brushes.Cyan;
                }

                Draw.Diamond(this, ActiveAnchorDrawTag, false, firstOffset,
                    anchorDrawPrice, anchorBrush);
                Draw.Text(this, ActiveAnchorTextTag, qualificationTag + " #" + anchorSeq,
                    firstOffset, High[firstOffset] + 6.0 * TickSize, Brushes.White);
            }

            Print(string.Format(
                "{0:yyyy-MM-dd HH:mm} ANCHOR #{1} {2} | bar {3:HH:mm} close={4:F2} ATR={5:F2}{6}",
                Time[0], anchorSeq, qualificationTag, Time[firstOffset],
                anchorClose, anchorATR,
                replacing ? " (replaced previous)" : string.Empty));
        }

        private bool IsContinuationMove(int candidateBar)
        {
            if (anchorBar < 0 || candidateBar <= anchorBar)
            {
                return false;
            }

            int candidateOffset = CurrentBar - candidateBar;
            double candidateClose = Close[candidateOffset];

            bool isFall = candidateClose < anchorClose;
            bool isRise = candidateClose > anchorClose;

            if (!isFall && !isRise)
            {
                return false;
            }

            for (int absBar = anchorBar + 1; absBar < candidateBar; absBar++)
            {
                int barsAgo = CurrentBar - absBar;
                double body = Close[barsAgo] - Open[barsAgo];

                bool breaksLock;
                if (isFall)
                {
                    breaksLock = body > ContinuationLockMinBodyPoints;
                }
                else
                {
                    breaksLock = -body > ContinuationLockMinBodyPoints;
                }

                if (breaksLock)
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsBigBody(int barsAgo, double atr)
        {
            double body = Math.Abs(Close[barsAgo] - Open[barsAgo]);
            double range = High[barsAgo] - Low[barsAgo];

            return body >= BigBodyATR * atr
                && range > 0.0
                && body >= BigBodyRangePct * range;
        }

        private bool IsSwing(int firstOffset, int comboLen, double atr,
            out bool isSwingHigh, out double extreme)
        {
            double comboHigh = double.MinValue;
            double comboLow = double.MaxValue;

            for (int i = firstOffset; i >= 0; i--)
            {
                comboHigh = Math.Max(comboHigh, High[i]);
                comboLow = Math.Min(comboLow, Low[i]);
            }

            double priorHigh = double.MinValue;
            double priorLow = double.MaxValue;
            int lastPriorOffset = firstOffset + SwingLookbackBars;

            for (int i = firstOffset + 1; i <= lastPriorOffset; i++)
            {
                if (i > CurrentBar)
                {
                    break;
                }
                priorHigh = Math.Max(priorHigh, High[i]);
                priorLow = Math.Min(priorLow, Low[i]);
            }

            bool swingHigh =
                comboHigh >= priorHigh - SwingToleranceATR * atr
                && comboHigh - priorLow >= SwingLegATR * atr;

            bool swingLow =
                comboLow <= priorLow + SwingToleranceATR * atr
                && priorHigh - comboLow >= SwingLegATR * atr;

            if (EnableContinuationFilter)
            {
                int firstPreComboOffset = firstOffset + 1;
                int lastPreComboOffset = firstPreComboOffset + PreComboThrustBars;

                if (lastPreComboOffset <= CurrentBar)
                {
                    double thrust = Close[firstPreComboOffset] - Close[lastPreComboOffset];

                    if (swingHigh && thrust >= PreComboThrustATR * atr)
                    {
                        swingHigh = false;
                    }
                    if (swingLow && -thrust >= PreComboThrustATR * atr)
                    {
                        swingLow = false;
                    }
                }
            }

            isSwingHigh = swingHigh;
            extreme = swingHigh ? comboHigh : comboLow;
            return swingHigh || swingLow;
        }

        private void PendSwingCandidate(int candidateBar, bool isSwingHigh,
            double extreme, double atrAtCandidate)
        {
            hasPendingSwing = true;
            pendingSwingBar = candidateBar;
            pendingSwingComboEnd = CurrentBar;
            pendingSwingIsHigh = isSwingHigh;
            pendingSwingExtreme = extreme;
            pendingSwingATR = atrAtCandidate;

            Print(string.Format(
                "{0:yyyy-MM-dd HH:mm} SWING PENDING | bar {1:HH:mm} {2} extreme={3:F2} confirm in {4} bars",
                Time[0], Time[CurrentBar - candidateBar],
                isSwingHigh ? "high" : "low", extreme, SwingConfirmBars));
        }

        private void TryConfirmPendingSwing()
        {
            if (!hasPendingSwing)
            {
                return;
            }

            int barsSinceCombo = CurrentBar - pendingSwingComboEnd;
            if (barsSinceCombo < SwingConfirmBars)
            {
                return;
            }

            bool extremeHolds = true;
            for (int absBar = pendingSwingBar + 1; absBar <= CurrentBar; absBar++)
            {
                int barsAgo = CurrentBar - absBar;
                if (pendingSwingIsHigh)
                {
                    if (High[barsAgo] > pendingSwingExtreme)
                    {
                        extremeHolds = false;
                        break;
                    }
                }
                else
                {
                    if (Low[barsAgo] < pendingSwingExtreme)
                    {
                        extremeHolds = false;
                        break;
                    }
                }
            }

            int confirmedBar = pendingSwingBar;
            double confirmedATR = pendingSwingATR;

            if (extremeHolds)
            {
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} SWING CONFIRMED | bar {1:HH:mm} extreme held {2} bars",
                    Time[0], Time[CurrentBar - confirmedBar], barsSinceCombo));
                ClearPendingSwing();
                InstallAnchor(confirmedBar, confirmedATR, "Swing");
            }
            else
            {
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} SWING DISCARDED | bar {1:HH:mm} extreme broken",
                    Time[0], Time[CurrentBar - confirmedBar]));
                ClearPendingSwing();
            }
        }

        private void ClearPendingSwing()
        {
            hasPendingSwing = false;
            pendingSwingBar = -1;
            pendingSwingComboEnd = -1;
            pendingSwingIsHigh = false;
            pendingSwingExtreme = 0.0;
            pendingSwingATR = 0.0;
        }
        #endregion

        #region AVWAP Display
        private void ClearActiveAnchorDrawings()
        {
            RemoveDrawObject(ActiveAnchorDrawTag);
            RemoveDrawObject(ActiveAnchorTextTag);
            RemoveDrawObject(ActiveContactDrawTag);
        }

        private void ClearAvwapPlot()
        {
            if (CurrentBar < 0 || plotStartBar < 0)
            {
                return;
            }

            int maxBarsAgo = CurrentBar - plotStartBar;
            maxBarsAgo = Math.Min(maxBarsAgo, CurrentBar);

            if (maxBarsAgo < 0)
            {
                plotStartBar = -1;
                return;
            }

            for (int i = 0; i <= maxBarsAgo; i++)
            {
                Values[0][i] = double.NaN;
            }

            plotStartBar = -1;
        }

        private void RebuildAvwapPlot(int firstOffset)
        {
            cumPV = 0.0;
            cumV = 0.0;
            avwap = 0.0;

            for (int i = firstOffset; i >= 0; i--)
            {
                double typicalPrice = (Open[i] + High[i] + Low[i] + Close[i]) / 4.0;
                cumPV += typicalPrice * Volume[i];
                cumV += Volume[i];
                avwap = cumV > 0.0 ? cumPV / cumV : Close[i];
                Values[0][i] = avwap;
            }

            plotStartBar = CurrentBar - firstOffset;
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

            if (stopProxyPoints > MaxStopPoints)
            {
                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} {1} {2} BLOCKED | SL {3:F2} pts > max {4:F2}",
                    Time[0], direction, z.Tag, stopProxyPoints, MaxStopPoints));
                plannedStopPrice = 0.0;
                return;
            }

            double rewardRisk = stopProxyPoints < RRThresholdPoints ? HighRR : LowRR;
            double stopTicks = Math.Max(1.0, Math.Round(stopProxyPoints / TickSize));
            double targetTicks = Math.Max(1.0, Math.Round(stopTicks * rewardRisk));

            currentSignalName = longTrigger ? "ZoneLong" : "ZoneShort";

            tradeZone = new Zone
            {
                HasA = z.HasA, HasD = z.HasD, HasW = z.HasW,
                Min = z.Min, Max = z.Max
            };
            tradeTag = tradeZone.Tag;
            tradeAnchorTag = z.HasA ? anchorTag : string.Empty;
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
            if (z.HasA)
            {
                state = AnchorState.InTrade;
            }

            activeZone = null;
            contactBar = -1;
            ClearPendingSwing();

            Print(string.Format(
                "{0:yyyy-MM-dd HH:mm} {1} SIGNAL {2}{3} | zone [{4:F2}, {5:F2}] stop={6:F2} SL≈{7:F2}pts RR={8:F1}",
                Time[0], direction, tradeTag,
                z.HasA ? " (" + anchorTag + " #" + anchorSeq + ")" : string.Empty,
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
                "{0:yyyy-MM-dd HH:mm} ENTRY {1} | {2} — back to Armed",
                time, orderState, error));

            if (state == AnchorState.InTrade)
            {
                state = AnchorState.Armed;
            }

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

                if (stopPoints > MaxStopPoints)
                {
                    fillRejected = true;
                    dailyTradeCount--;
                    tradeSLPts = stopPoints;
                    tradeTgtPts = 0.0;

                    Print(string.Format(
                        "{0:yyyy-MM-dd HH:mm} FILL REJECTED | SL {1:F2} pts > max {2:F2} — flattening",
                        time, stopPoints, MaxStopPoints));

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
                if (lastExitBar < CurrentBar)
                {
                    lastExitBar = CurrentBar;
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
                    if (state == AnchorState.InTrade)
                    {
                        state = AnchorState.Armed;
                    }
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

                    // Anchor outcome (unchanged logic).
                    if (tz != null && tz.HasA)
                    {
                        if (isStop)
                        {
                            anchorStopOuts++;
                        }
                        else
                        {
                            anchorStopOuts = 0;
                        }

                        if (state == AnchorState.InTrade)
                        {
                            if (anchorStopOuts >= MaxStopOutsPerAnchor)
                            {
                                state = AnchorState.Dead;
                                Print(string.Format(
                                    "{0:yyyy-MM-dd HH:mm} ANCHOR #{1} DEAD | {2} consecutive stop-outs",
                                    time, anchorSeq, anchorStopOuts));
                            }
                            else
                            {
                                state = AnchorState.Armed;
                            }
                        }
                    }

                    // Daily / Weekly outcomes.
                    if (tz != null && tz.HasD)
                    {
                        RegisterLevelOutcome(daily, isStop);
                    }
                    if (tz != null && tz.HasW)
                    {
                        RegisterLevelOutcome(weekly, isStop);
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

        #region Anchor Reset
        private void ResetAnchor(AnchorState newState)
        {
            if (newState == AnchorState.Idle || newState == AnchorState.Dead)
            {
                ClearActiveAnchorDrawings();
                ClearAvwapPlot();
            }

            state = newState;
            anchorBar = -1;
            comboEndBar = -1;
            anchorClose = 0.0;
            anchorATR = 0.0;
            anchorTag = string.Empty;
            cumPV = 0.0;
            cumV = 0.0;
            avwap = 0.0;
            anchorStopOuts = 0;
        }
        #endregion

        #region Bar Classification
        private void ClassifyCurrentBar()
        {
            if (CurrentBar < 2)
            {
                isBullPP[0] = false;
                isBearPP[0] = false;
                isGreenBar[0] = false;
                isRedBar[0] = false;
                return;
            }

            bool isUpBar = Close[0] > Close[1];
            bool isDownBar = Close[0] < Close[1];

            double maxDownVolume = double.NaN;
            double maxUpVolume = double.NaN;

            int scanBars = Math.Min(PocketPivotLookback, CurrentBar - 1);

            for (int i = 1; i <= scanBars; i++)
            {
                bool priorBarWasDown = Close[i] < Close[i + 1];
                bool priorBarWasUp = Close[i] > Close[i + 1];

                if (priorBarWasDown
                    && (double.IsNaN(maxDownVolume) || Volume[i] > maxDownVolume))
                {
                    maxDownVolume = Volume[i];
                }

                if (priorBarWasUp
                    && (double.IsNaN(maxUpVolume) || Volume[i] > maxUpVolume))
                {
                    maxUpVolume = Volume[i];
                }
            }

            isBullPP[0] = isUpBar && !double.IsNaN(maxDownVolume) && Volume[0] > maxDownVolume;
            isBearPP[0] = isDownBar && !double.IsNaN(maxUpVolume) && Volume[0] > maxUpVolume;

            double averageVolume = volSma[0];
            bool volumeAboveAverage = averageVolume > 0.0 && Volume[0] > averageVolume;

            isGreenBar[0] = isUpBar && volumeAboveAverage && !isBullPP[0];
            isRedBar[0] = isDownBar && volumeAboveAverage && !isBearPP[0];
        }
        #endregion

        #region Properties
        // 01 | Pocket Pivot
        [NinjaScriptProperty]
        [Range(2, 500)]
        [Display(Name = "Pocket Pivot Lookback (bars)", Order = 1, GroupName = "01 | Pocket Pivot")]
        public int PocketPivotLookback { get; set; }

        [NinjaScriptProperty]
        [Range(2, 500)]
        [Display(Name = "Volume Average Length (bars)", Order = 2, GroupName = "01 | Pocket Pivot")]
        public int VolumeAverageLength { get; set; }

        // 02 | Anchor Qualification
        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name = "ATR Period", Order = 1, GroupName = "02 | Anchor Qualification")]
        public int ATRPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name = "Swing Lookback P (bars)", Order = 2, GroupName = "02 | Anchor Qualification")]
        public int SwingLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 3.0)]
        [Display(Name = "Swing Tolerance (ATR)", Order = 3, GroupName = "02 | Anchor Qualification")]
        public double SwingToleranceATR { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 5.0)]
        [Display(Name = "Swing Leg (ATR)", Order = 4, GroupName = "02 | Anchor Qualification")]
        public double SwingLegATR { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "Big Body (ATR)", Order = 5, GroupName = "02 | Anchor Qualification")]
        public double BigBodyATR { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 1.0)]
        [Display(Name = "Big Body Range %", Order = 6, GroupName = "02 | Anchor Qualification")]
        public double BigBodyRangePct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Continuation Filter", Order = 7, GroupName = "02 | Anchor Qualification")]
        public bool EnableContinuationFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Pre-Combo Thrust Bars", Order = 8, GroupName = "02 | Anchor Qualification")]
        public int PreComboThrustBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 5.0)]
        [Display(Name = "Pre-Combo Thrust (ATR)", Order = 9, GroupName = "02 | Anchor Qualification")]
        public double PreComboThrustATR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Swing Confirmation", Order = 10, GroupName = "02 | Anchor Qualification")]
        public bool EnableSwingConfirmation { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Swing Confirm Bars", Order = 11, GroupName = "02 | Anchor Qualification")]
        public int SwingConfirmBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Continuation Lock", Order = 12, GroupName = "02 | Anchor Qualification")]
        public bool EnableContinuationLock { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name = "Continuation Lock Min Body (pts)", Order = 13, GroupName = "02 | Anchor Qualification")]
        public double ContinuationLockMinBodyPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable News Anchor", Order = 14, GroupName = "02 | Anchor Qualification")]
        public bool EnableNewsAnchor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "News Anchor Times (HH:MM CT, comma)", Order = 15, GroupName = "02 | Anchor Qualification")]
        public string NewsAnchorTimes { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "News Anchor Body (ATR)", Order = 16, GroupName = "02 | Anchor Qualification")]
        public double NewsAnchorATRMult { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10.0)]
        [Display(Name = "News Anchor Volume (x avg)", Order = 17, GroupName = "02 | Anchor Qualification")]
        public double NewsAnchorVolumeMult { get; set; }

        // 03 | Arming / Contact
        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "Arm Distance (ATR)", Order = 1, GroupName = "03 | Arming / Contact")]
        public double ArmDistanceATR { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Arm Min Bars", Order = 2, GroupName = "03 | Arming / Contact")]
        public int ArmMinBars { get; set; }

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
        [Range(0.0, 20.0)]
        [Display(Name = "Confluence Tolerance (pts)",
            Description = "Eligible levels within this distance merge into one zone.",
            Order = 7, GroupName = "03 | Arming / Contact")]
        public double ConfluenceTolerancePoints { get; set; }

        // 04 | Trade Management
        [NinjaScriptProperty]
        [Range(0.25, 50.0)]
        [Display(Name = "Max Stop (pts)", Order = 1, GroupName = "04 | Trade Management")]
        public double MaxStopPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 50.0)]
        [Display(Name = "RR Threshold (pts)", Order = 2, GroupName = "04 | Trade Management")]
        public double RRThresholdPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "High RR", Order = 3, GroupName = "04 | Trade Management")]
        public double HighRR { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Low RR", Order = 4, GroupName = "04 | Trade Management")]
        public double LowRR { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Contract Qty", Order = 5, GroupName = "04 | Trade Management")]
        public int ContractQty { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Stop Halving", Order = 6, GroupName = "04 | Trade Management")]
        public bool EnableStopHalving { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Cooldown (bars)", Order = 7, GroupName = "04 | Trade Management")]
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
        [Range(0, 235959)]
        [Display(Name = "No New Anchor After (HHMMSS)", Order = 4, GroupName = "05 | Session")]
        public int NoNewAnchorHHMMSS { get; set; }

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

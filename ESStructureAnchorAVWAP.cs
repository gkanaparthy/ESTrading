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
    public class ESStructureAnchorAVWAP : Strategy
    {
        private enum AnchorKind
        {
            LOD,
            HOD,
            StructuralBull,
            StructuralBear,
            RallyOrigin,
            SelloffOrigin,
            WeeklyOpen,
            ManualLongAVWAP2,
            ManualShortAVWAP2
        }

        private enum LodTier
        {
            TierA,
            TierB
        }

        // ES RTH (CME/CT): 08:30 - 15:00
        private const int CmeMorningWindowStart = 83000;
        private const int CmeMorningWindowEnd = 150000;
        private const int CmeAfternoonWindowStart = 120000;
        private const int CmeAfternoonWindowEnd = 150000;
        private const int CmeAnchorCutoffTime = 140000;
        private const string AnchorStatusDrawTag = "ESStructureAnchorAVWAP.AnchorStatus";
        private const string LongAnchorMarkerTag = "ESStructureAnchorAVWAP.LongAnchorMarker";
        private const string LongAnchorLabelTag = "ESStructureAnchorAVWAP.LongAnchorLabel";
        private const string ShortAnchorMarkerTag = "ESStructureAnchorAVWAP.ShortAnchorMarker";
        private const string ShortAnchorLabelTag = "ESStructureAnchorAVWAP.ShortAnchorLabel";
        private const string RallyOriginMarkerTag = "ESStructureAnchorAVWAP.RallyOriginMarker";
        private const string RallyOriginLabelTag = "ESStructureAnchorAVWAP.RallyOriginLabel";
        private const string SelloffOriginMarkerTag = "ESStructureAnchorAVWAP.SelloffOriginMarker";
        private const string SelloffOriginLabelTag = "ESStructureAnchorAVWAP.SelloffOriginLabel";

        private ATR atr;
        private EMA ema;
        private SMA volSma;
        private ADX adx;
        private PriorDayOHLC priorDay;
        private TimeZoneInfo cmeTimeZone;
        private TimeZoneInfo barTimeZone;
        private AVWAP2 manualLongAvwap2;
        private AVWAP2 manualShortAvwap2;
        private bool manualHotkeysHooked;
        private Chart chartWindow;
        private readonly object manualAnchorLock = new object();
        private bool pendingSetManualLong;
        private bool pendingSetManualShort;
        private bool pendingClearManualAnchors;
        private int pendingLongBarIndex = -1;
        private int pendingShortBarIndex = -1;
        private int lastClickedBarIndex = -1;

        private DateTime sessionDate = Core.Globals.MinDate;
        private bool isGapDay;

        private double dayHigh;
        private double dayLow;
        private int dayHighBarIndex;
        private int dayLowBarIndex;

        private bool structuralOverrideUsed;
        private bool structuralOverrideActive;
        private AnchorKind structuralOverrideKind;
        private double structuralOverridePrice;
        private int structuralOverrideBarIndex;
        private int structuralOverrideActivatedBarIndex;
        private int overrideCooldownRemaining;

        private int opportunitiesToday;
        private int consecutiveLosses;
        private double dailyR;
        private int lastProcessedTradeCount;

        private readonly Queue<double> recentTradeR = new Queue<double>();
        private readonly Queue<double> sessionTrueRangeWindow = new Queue<double>();
        private bool expectancyPausedToday;
        private double sessionTrueRangeSum;
        private double sessionAtrForStops;

        private string pendingSignal = string.Empty;
        private double pendingRiskCurrency;
        private double activeRiskCurrency;
        private string activeSignal = string.Empty;
        private int activeStopTicks;
        private int activeQuantity;
        private bool breakevenMoved;
        private bool activeTradeWasTierB;
        private double currentTradeMfeR;
        private double currentTradeMaeR;

        private bool lodInvalidated;
        private bool lodBreakPending;
        private int lodBreakBarIndex;
        private double lodBreakReferenceLow;
        private int defendedLowCandidateBarIndex;
        private double defendedLowCandidatePrice;

        private bool hodInvalidated;
        private bool hodBreakPending;
        private int hodBreakBarIndex;
        private double hodBreakReferenceHigh;
        private int defendedHighCandidateBarIndex;
        private double defendedHighCandidatePrice;

        private bool tierBAttemptUsed;
        private string lastAnchorStateKey = string.Empty;
        private string lastShortAnchorDecisionKey = string.Empty;
        private string lastMissedShortReasonKey = string.Empty;

        private int signalCooldownRemaining;
        private readonly Dictionary<string, int> anchorTradesToday = new Dictionary<string, int>();
        private bool longTouchSeen;
        private bool shortTouchSeen;
        private bool longCloseBackSeen;
        private bool shortCloseBackSeen;
        private bool longBullishSeen;
        private bool shortBearishSeen;
        private int longFirstTouchBar = -1;
        private int shortFirstTouchBar = -1;
        private int longTouchAnchorBar = -1;
        private int shortTouchAnchorBar = -1;
        private bool pendingBreakoutLong;
        private bool pendingBreakoutShort;
        private double pendingBreakoutLongTrigger;
        private double pendingBreakoutShortTrigger;
        private int pendingBreakoutLongSetBar = -1;
        private int pendingBreakoutShortSetBar = -1;
        private int pendingBreakoutLongAnchorBar = -1;
        private int pendingBreakoutShortAnchorBar = -1;
        private double pendingBreakoutLongAnchorPrice;
        private double pendingBreakoutShortAnchorPrice;

        // Persistent impulse-origin anchors (start candle of sharp directional move)
        private int rallyOriginBarIndex = -1;
        private double rallyOriginPrice;
        private double rallyOriginScore;
        private int selloffOriginBarIndex = -1;
        private double selloffOriginPrice;
        private double selloffOriginScore;

        // Week-to-date AVWAP anchor (anchored from Sunday 17:00 CT)
        private int wtdAnchorBarIndex = -1;
        private double wtdAnchorOpenPrice;
        private bool wtdAnchorSet;
        private int wtdAnchorWeekYear = -1; // ISO week key: year*100 + weekOfYear, prevents re-anchoring within same hour
        private double wtdPV;               // running sum of (typical price × volume) since anchor bar
        private double wtdVSum;             // running sum of volume since anchor bar
        private bool wtdSeededThisBar;
        private int wtdDeferredWeekYear = -1;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ESStructureAnchorAVWAP";
                Description = "ES AVWAP strategy with structure anchors, regime gates, and risk-first daily controls.";

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
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 50;

                AtrPeriod = 14;
                AtrStopMultiple = 1.25;
                TargetRMultiple = 2.0;
                MinStopTicks = 8;
                AnchorZoneTicks = 4;
                ReclaimLookbackBars = 5;
                TrendSlopeBars = 5;
                ApproachLookbackBars = 5;
                SignalCooldownBars = 5;
                TouchToleranceTicks = 2;
                MaxStopPoints = 5.0;
                AnchorProximityAtrMultiple = 1.0;
                UseExtendedHours = false;

                MaxOpportunitiesPerDay = 3;
                MaxConsecutiveLosses = 2;
                DailyStopR = -2.0;
                MaxRiskPerTradeDollars = 400.0;
                AllowRiskCapStopCompression = true;
                MaxStopCompressionFraction = 0.25;
                UseSessionAtrForStops = true;

                StructureLookbackBars = 40;
                ImpulseBars = 8;
                StructureDisplacementAtr = 1.5;
                StructureVolumeMultiple = 1.2;
                ChopLookbackBars = 8;
                ChopFlipThreshold = 4;
                StructureScoreMargin = 1.2;
                OverrideCooldownBars = 20;
                MinOverrideActiveBars = 3;
                GapThresholdPoints = 8.0;
                UseTradeTimeWindows = true;

                AdxChopThreshold = 18;
                ExtremeAtrThreshold = 10.0;
                MinAtrForEntry = 1.5;
                DefendedLowImpulseAtr = 1.5;
                DefendedLowMaxBars = 10;
                RollingExpectancyTrades = 10;
                InvalidationFollowThroughBars = 3;
                MinAnchorAgeBars = 1;

                EnableAnchorLogging = true;
                ShowAnchorStatusOnChart = true;
                EnableWtdAnchor = true;
                EnableImpulseOriginAnchors = true;
                UseManualAvwap2Anchors = false;
                EnableManualAnchorHotkeys = true;
                ManualLongAnchorFrom = Core.Globals.MinDate;
                ManualShortAnchorFrom = Core.Globals.MinDate;
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(AtrPeriod);
                ema = EMA(20);
                volSma = SMA(Volume, 20);
                adx = ADX(14);
                priorDay = PriorDayOHLC();
                InitializeTimeZones();

                RebuildManualAvwapAnchors();

                dayHigh = double.MinValue;
                dayLow = double.MaxValue;
                dayHighBarIndex = -1;
                dayLowBarIndex = -1;
                structuralOverrideBarIndex = -1;
                structuralOverrideActivatedBarIndex = -1;
                rallyOriginBarIndex = -1;
                selloffOriginBarIndex = -1;

                Print("CONFIG instrument=" + Instrument?.FullName +
                      " barsType=" + BarsPeriod?.BarsPeriodType +
                      " barsValue=" + BarsPeriod?.Value +
                      " tickSize=" + TickSize.ToString("F2") +
                      " atrPeriod=" + AtrPeriod +
                      " atrStopMultiple=" + AtrStopMultiple.ToString("F2") +
                      " useSessionAtrForStops=" + UseSessionAtrForStops +
                      " minStopTicks=" + MinStopTicks +
                      " maxRisk=" + MaxRiskPerTradeDollars.ToString("F0") +
                      " stopCompression=" + AllowRiskCapStopCompression);
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
            ResetDailyStateIfNeeded();
            wtdSeededThisBar = false;
            UpdateWtdAnchorIfNeeded();         // resets and seeds accumulators on new week / cold start
            UpdateWtdRunningAccumulator();     // adds current bar's contribution (skips anchor bar)
            UpdateSessionAtrForStops();
            UpdateGapDayFlag();
            int nowCme = GetCmeTimeInt(Time[0]);
            bool canRefreshAnchors = nowCme < CmeAnchorCutoffTime;

            double priorLod = dayLow;
            double priorHod = dayHigh;
            ProcessLodInvalidation(priorLod, nowCme);
            ProcessHodInvalidation(priorHod, nowCme);
            UpdateSessionExtremes(canRefreshAnchors);
            RecoverLodFromDefendedLow();
            RecoverHodFromDefendedHigh();
            UpdateImpulseOriginAnchors(canRefreshAnchors, nowCme);

            if (overrideCooldownRemaining > 0)
                overrideCooldownRemaining--;

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                double longAnchorOpen = GetLongAnchor(out AnchorKind longKindOpen, out int longAnchorBarOpen);
                double shortAnchorOpen = GetShortAnchor(out AnchorKind shortKindOpen, out int shortAnchorBarOpen);
                int longAnchorAgeOpen = longAnchorBarOpen >= 0 ? CurrentBar - longAnchorBarOpen : int.MaxValue;
                int shortAnchorAgeOpen = shortAnchorBarOpen >= 0 ? CurrentBar - shortAnchorBarOpen : int.MaxValue;
                bool longAnchorChoppyOpen = !double.IsNaN(longAnchorOpen) && IsAnchorDegraded(longAnchorOpen);
                bool shortAnchorChoppyOpen = !double.IsNaN(shortAnchorOpen) && IsAnchorDegraded(shortAnchorOpen);
                bool longAnchorUsableOpen = !double.IsNaN(longAnchorOpen) && longAnchorAgeOpen >= MinAnchorAgeBars && !longAnchorChoppyOpen;
                bool shortAnchorUsableOpen = !double.IsNaN(shortAnchorOpen) && shortAnchorAgeOpen >= MinAnchorAgeBars && !shortAnchorChoppyOpen;
                PublishAnchorTelemetry(
                    nowCme,
                    longKindOpen,
                    longAnchorOpen,
                    longAnchorBarOpen,
                    longAnchorAgeOpen,
                    longAnchorUsableOpen,
                    longAnchorChoppyOpen,
                    shortKindOpen,
                    shortAnchorOpen,
                    shortAnchorBarOpen,
                    shortAnchorAgeOpen,
                    shortAnchorUsableOpen,
                    shortAnchorChoppyOpen);

                ManageOpenPosition();
                return;
            }

            bool inTradeWindow = IsInTradeWindow(nowCme);
            bool canCreateOrSwitchAnchors = canRefreshAnchors && overrideCooldownRemaining == 0;
            if (canCreateOrSwitchAnchors && !structuralOverrideUsed && inTradeWindow)
                TryPromoteStructuralAnchor(nowCme);

            bool overrideHasLivedLongEnough =
                structuralOverrideActivatedBarIndex < 0 ||
                (CurrentBar - structuralOverrideActivatedBarIndex) >= MinOverrideActiveBars;

            if (structuralOverrideActive &&
                overrideHasLivedLongEnough &&
                IsAnchorDegraded(GetStructuralAvwapOrPrice()))
            {
                if (EnableAnchorLogging)
                {
                    PrintWithContext("ANCHOR_OVERRIDE_DEACTIVATED timeCME=" + FormatCmeTime(nowCme) +
                          " kind=" + structuralOverrideKind +
                          " bar=" + structuralOverrideBarIndex);
                }

                structuralOverrideActive = false;
                structuralOverrideActivatedBarIndex = -1;
            }

            double longAnchor = GetLongAnchor(out AnchorKind longKind, out int longAnchorBar);
            double shortAnchor = GetShortAnchor(out AnchorKind shortKind, out int shortAnchorBar);

            int longAnchorAge = longAnchorBar >= 0 ? CurrentBar - longAnchorBar : int.MaxValue;
            int shortAnchorAge = shortAnchorBar >= 0 ? CurrentBar - shortAnchorBar : int.MaxValue;

            bool longAnchorChoppy = !double.IsNaN(longAnchor) && IsAnchorDegraded(longAnchor);
            bool shortAnchorChoppy = !double.IsNaN(shortAnchor) && IsAnchorDegraded(shortAnchor);

            bool longAnchorUsable = !double.IsNaN(longAnchor) && longAnchorAge >= MinAnchorAgeBars && !longAnchorChoppy;
            bool shortAnchorUsable = !double.IsNaN(shortAnchor) && shortAnchorAge >= MinAnchorAgeBars && !shortAnchorChoppy;

            PublishAnchorTelemetry(
                nowCme,
                longKind,
                longAnchor,
                longAnchorBar,
                longAnchorAge,
                longAnchorUsable,
                longAnchorChoppy,
                shortKind,
                shortAnchor,
                shortAnchorBar,
                shortAnchorAge,
                shortAnchorUsable,
                shortAnchorChoppy);

            if (!string.IsNullOrEmpty(pendingSignal))
                return;

            if (signalCooldownRemaining > 0)
                signalCooldownRemaining--;

            if (!inTradeWindow || !CanSubmitNewTrade())
                return;

            if (double.IsNaN(atr[0]) || atr[0] < MinAtrForEntry || atr[0] > ExtremeAtrThreshold)
                return;

            if (longAnchorUsable && shortAnchorUsable && Math.Abs(longAnchor - shortAnchor) <= (AnchorProximityAtrMultiple * atr[0]))
            {
                double higher = Math.Max(longAnchor, shortAnchor);
                double lower = Math.Min(longAnchor, shortAnchor);
                if (MajorityApproachFromBelow(higher))
                    shortAnchor = higher;
                if (MajorityApproachFromAbove(lower))
                    longAnchor = lower;
            }

            EvaluateAnchorRetestBreakout(nowCme, longKind, longAnchor, longAnchorBar, longAnchorUsable, shortKind, shortAnchor, shortAnchorBar, shortAnchorUsable);
            return;

        }

        protected override void OnExecutionUpdate(
            Execution execution,
            string executionId,
            double price,
            int quantity,
            MarketPosition marketPosition,
            string orderId,
            DateTime time)
        {
            if (execution?.Order == null)
                return;

            if (execution.Order.Name == pendingSignal &&
                execution.Order.Filled > 0)
            {
                activeQuantity = Math.Max(activeQuantity, execution.Order.Filled);

                if (activeStopTicks > 0)
                {
                    activeRiskCurrency = activeQuantity * activeStopTicks * TickSize * Instrument.MasterInstrument.PointValue;
                }
                else if (pendingRiskCurrency > 0 && execution.Order.Quantity > 0)
                {
                    activeRiskCurrency = pendingRiskCurrency * (activeQuantity / (double)execution.Order.Quantity);
                }
            }

            if (Position.MarketPosition == MarketPosition.Flat &&
                SystemPerformance.AllTrades.Count > lastProcessedTradeCount)
            {
                Trade last = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                double risk = activeRiskCurrency;
                if (risk <= 0 && activeStopTicks > 0 && activeQuantity > 0)
                    risk = activeQuantity * activeStopTicks * TickSize * Instrument.MasterInstrument.PointValue;
                if (risk <= 0)
                    risk = 1.0;

                double realizedR = last.ProfitCurrency / risk;
                dailyR += realizedR;

                if (realizedR < 0)
                    consecutiveLosses++;
                else
                    consecutiveLosses = 0;

                recentTradeR.Enqueue(realizedR);
                while (recentTradeR.Count > RollingExpectancyTrades)
                    recentTradeR.Dequeue();

                PrintWithContext("TRADE_METRICS signal=" + activeSignal +
                      " realizedR=" + realizedR.ToString("F2") +
                      " mfeR=" + currentTradeMfeR.ToString("F2") +
                      " maeR=" + currentTradeMaeR.ToString("F2") +
                      " tierB=" + activeTradeWasTierB);

                lastProcessedTradeCount = SystemPerformance.AllTrades.Count;
                activeRiskCurrency = 0;
                pendingSignal = string.Empty;
                pendingRiskCurrency = 0;
                activeSignal = string.Empty;
                activeStopTicks = 0;
                activeQuantity = 0;
                breakevenMoved = false;
                activeTradeWasTierB = false;
                currentTradeMfeR = 0;
                currentTradeMaeR = 0;
            }
        }

        protected override void OnOrderUpdate(
            Order order,
            double limitPrice,
            double stopPrice,
            int quantity,
            int filled,
            double averageFillPrice,
            OrderState orderState,
            DateTime time,
            ErrorCode error,
            string comment)
        {
            if (order == null || string.IsNullOrEmpty(pendingSignal) || order.Name != pendingSignal)
                return;

            if (orderState != OrderState.Cancelled && orderState != OrderState.Rejected)
                return;

            if (filled <= 0 && opportunitiesToday > 0)
                opportunitiesToday--;

            if (filled <= 0 && activeTradeWasTierB)
                tierBAttemptUsed = false;

            pendingSignal = string.Empty;
            pendingRiskCurrency = 0;

            if (filled <= 0 && Position.MarketPosition == MarketPosition.Flat && activeRiskCurrency <= 0)
            {
                activeSignal = string.Empty;
                activeStopTicks = 0;
                activeQuantity = 0;
                breakevenMoved = false;
                activeTradeWasTierB = false;
                currentTradeMfeR = 0;
                currentTradeMaeR = 0;
            }
        }

        private void ResetDailyStateIfNeeded()
        {
            bool shouldReset = Bars.IsFirstBarOfSession || sessionDate == Core.Globals.MinDate;
            if (!shouldReset)
                return;

            sessionDate = GetCmeTime(Time[0]).Date;
            dayHigh = High[0];
            dayLow = Low[0];
            dayHighBarIndex = CurrentBar;
            dayLowBarIndex = CurrentBar;

            structuralOverrideUsed = false;
            structuralOverrideActive = false;
            structuralOverridePrice = 0;
            structuralOverrideKind = AnchorKind.LOD;
            structuralOverrideBarIndex = -1;
            structuralOverrideActivatedBarIndex = -1;
            overrideCooldownRemaining = 0;
            rallyOriginBarIndex = -1;
            rallyOriginPrice = 0;
            rallyOriginScore = 0;
            selloffOriginBarIndex = -1;
            selloffOriginPrice = 0;
            selloffOriginScore = 0;

            opportunitiesToday = 0;
            consecutiveLosses = 0;
            dailyR = 0;
            tierBAttemptUsed = false;
            if (expectancyPausedToday)
                recentTradeR.Clear();
            expectancyPausedToday = false;

            pendingSignal = string.Empty;
            pendingRiskCurrency = 0;
            activeRiskCurrency = 0;
            activeSignal = string.Empty;
            activeStopTicks = 0;
            activeQuantity = 0;
            breakevenMoved = false;
            activeTradeWasTierB = false;
            currentTradeMfeR = 0;
            currentTradeMaeR = 0;
            lastProcessedTradeCount = SystemPerformance.AllTrades.Count;

            lodInvalidated = false;
            lodBreakPending = false;
            lodBreakBarIndex = -1;
            lodBreakReferenceLow = 0;
            defendedLowCandidateBarIndex = CurrentBar;
            defendedLowCandidatePrice = dayLow;

            hodInvalidated = false;
            hodBreakPending = false;
            hodBreakBarIndex = -1;
            hodBreakReferenceHigh = 0;
            defendedHighCandidateBarIndex = CurrentBar;
            defendedHighCandidatePrice = dayHigh;

            isGapDay = false;
            lastAnchorStateKey = string.Empty;
            lastShortAnchorDecisionKey = string.Empty;
            lastMissedShortReasonKey = string.Empty;
            signalCooldownRemaining = 0;
            anchorTradesToday.Clear();
            longTouchSeen = false;
            shortTouchSeen = false;
            longCloseBackSeen = false;
            shortCloseBackSeen = false;
            longBullishSeen = false;
            shortBearishSeen = false;
            longFirstTouchBar = -1;
            shortFirstTouchBar = -1;
            longTouchAnchorBar = -1;
            shortTouchAnchorBar = -1;
            pendingBreakoutLong = false;
            pendingBreakoutShort = false;
            pendingBreakoutLongSetBar = -1;
            pendingBreakoutShortSetBar = -1;
            pendingBreakoutLongAnchorBar = -1;
            pendingBreakoutShortAnchorBar = -1;
            pendingBreakoutLongAnchorPrice = 0;
            pendingBreakoutShortAnchorPrice = 0;
            pendingBreakoutLongTrigger = 0;
            pendingBreakoutShortTrigger = 0;
            sessionTrueRangeWindow.Clear();
            sessionTrueRangeSum = 0;
            sessionAtrForStops = 0;
        }

        private void UpdateGapDayFlag()
        {
            if (!Bars.IsFirstBarOfSession)
                return;

            if (priorDay == null)
            {
                isGapDay = false;
                return;
            }

            double priorSettle = priorDay.PriorClose[0];
            double openPrice = Open[0];
            if (double.IsNaN(priorSettle) || double.IsNaN(openPrice) || priorSettle <= 0)
            {
                isGapDay = false;
                if (EnableAnchorLogging)
                {
                    PrintWithContext("SESSION_OPEN timeCME=" + FormatCmeTime(GetCmeTimeInt(Time[0])) +
                          " priorSettleInvalid=true gapDay=false");
                }

                return;
            }

            isGapDay = Math.Abs(openPrice - priorSettle) >= GapThresholdPoints;
            if (EnableAnchorLogging)
            {
                PrintWithContext("SESSION_OPEN timeCME=" + FormatCmeTime(GetCmeTimeInt(Time[0])) +
                      " open=" + openPrice.ToString("F2") +
                      " priorSettle=" + priorSettle.ToString("F2") +
                      " gapDay=" + isGapDay);
            }
        }

        private void ProcessLodInvalidation(double referenceLod, int nowCme)
        {
            if (ShouldSuppressGapDayInvalidation(nowCme))
                return;

            if (referenceLod == double.MaxValue)
                return;

            if (lodInvalidated)
                return;

            if (!lodBreakPending && Close[0] < referenceLod - (0.5 * TickSize))
            {
                lodBreakPending = true;
                lodBreakBarIndex = CurrentBar;
                lodBreakReferenceLow = Low[0];
                return;
            }

            if (lodBreakPending)
            {
                int barsSinceBreak = CurrentBar - lodBreakBarIndex;
                if (barsSinceBreak >= 1 &&
                    barsSinceBreak <= InvalidationFollowThroughBars &&
                    Low[0] < lodBreakReferenceLow - (0.5 * TickSize))
                {
                    lodInvalidated = true;
                    lodBreakPending = false;
                    return;
                }

                if (barsSinceBreak >= InvalidationFollowThroughBars)
                    lodBreakPending = false;
            }
        }

        private void ProcessHodInvalidation(double referenceHod, int nowCme)
        {
            if (ShouldSuppressGapDayInvalidation(nowCme))
                return;

            if (referenceHod == double.MinValue)
                return;

            if (hodInvalidated)
                return;

            if (!hodBreakPending && Close[0] > referenceHod + (0.5 * TickSize))
            {
                hodBreakPending = true;
                hodBreakBarIndex = CurrentBar;
                hodBreakReferenceHigh = High[0];
                return;
            }

            if (hodBreakPending)
            {
                int barsSinceBreak = CurrentBar - hodBreakBarIndex;
                if (barsSinceBreak >= 1 &&
                    barsSinceBreak <= InvalidationFollowThroughBars &&
                    High[0] > hodBreakReferenceHigh + (0.5 * TickSize))
                {
                    hodInvalidated = true;
                    hodBreakPending = false;
                    return;
                }

                if (barsSinceBreak >= InvalidationFollowThroughBars)
                    hodBreakPending = false;
            }
        }

        private void UpdateSessionExtremes(bool allowAnchorRefresh)
        {
            if (!allowAnchorRefresh)
                return;

            if (High[0] >= dayHigh + TickSize)
            {
                dayHigh = High[0];
                dayHighBarIndex = CurrentBar;
                defendedHighCandidateBarIndex = CurrentBar;
                defendedHighCandidatePrice = High[0];
            }

            if (Low[0] <= dayLow - TickSize)
            {
                dayLow = Low[0];
                dayLowBarIndex = CurrentBar;
                defendedLowCandidateBarIndex = CurrentBar;
                defendedLowCandidatePrice = Low[0];
            }
        }

        private void RecoverLodFromDefendedLow()
        {
            if (!lodInvalidated || defendedLowCandidateBarIndex < 0)
                return;

            int barsSinceCandidate = CurrentBar - defendedLowCandidateBarIndex;
            if (barsSinceCandidate > DefendedLowMaxBars)
            {
                defendedLowCandidateBarIndex = -1;
                defendedLowCandidatePrice = 0;
                return;
            }

            double requiredImpulse = DefendedLowImpulseAtr * atr[0];
            if (High[0] - defendedLowCandidatePrice < requiredImpulse)
                return;

            for (int i = barsSinceCandidate; i >= 0; i--)
            {
                if (Close[i] < defendedLowCandidatePrice - (0.5 * TickSize))
                    return;
            }

            lodInvalidated = false;
            dayLow = defendedLowCandidatePrice;
            dayLowBarIndex = defendedLowCandidateBarIndex;
            lodBreakPending = false;
            lodBreakBarIndex = -1;
            lodBreakReferenceLow = 0;
        }

        private void RecoverHodFromDefendedHigh()
        {
            if (!hodInvalidated || defendedHighCandidateBarIndex < 0)
                return;

            int barsSinceCandidate = CurrentBar - defendedHighCandidateBarIndex;
            if (barsSinceCandidate > DefendedLowMaxBars)
            {
                defendedHighCandidateBarIndex = -1;
                defendedHighCandidatePrice = 0;
                return;
            }

            double requiredImpulse = DefendedLowImpulseAtr * atr[0];
            if (defendedHighCandidatePrice - Low[0] < requiredImpulse)
                return;

            for (int i = barsSinceCandidate; i >= 0; i--)
            {
                if (Close[i] > defendedHighCandidatePrice + (0.5 * TickSize))
                    return;
            }

            hodInvalidated = false;
            dayHigh = defendedHighCandidatePrice;
            dayHighBarIndex = defendedHighCandidateBarIndex;
            hodBreakPending = false;
            hodBreakBarIndex = -1;
            hodBreakReferenceHigh = 0;
        }

        private bool IsInTradeWindow(int time)
        {
            if (!UseTradeTimeWindows)
                return true;

            if (UseExtendedHours)
                return true;

            return time >= CmeMorningWindowStart && time <= CmeMorningWindowEnd;
        }

        // Anchors the WTD AVWAP once per week at the Sunday 17:00 CT bar.
        // Two paths:
        //   1) Normal weekly reset: fires on the exact Sunday 17:00 CT bar for a new calendar week.
        //   2) Cold start: scans backward through available history to find the most recent
        //      Sunday 17:00 bar and pre-computes the accumulator from there.
        private void UpdateWtdAnchorIfNeeded()
        {
            if (!EnableWtdAnchor)
                return;

            DateTime cmeNow = GetCmeTime(Time[0]);

            var cal = System.Globalization.CultureInfo.InvariantCulture.Calendar;
            int weekOfYear = cal.GetWeekOfYear(cmeNow, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
            int weekKey = cmeNow.Year * 100 + weekOfYear;

            bool isNewWeek = weekKey != wtdAnchorWeekYear;
            bool isSundayOpenBar = cmeNow.DayOfWeek == DayOfWeek.Sunday && cmeNow.Hour == 17 && cmeNow.Minute == 0;

            // Path 1: Normal weekly reset on the exact Sunday 17:00 bar
            if (isSundayOpenBar && isNewWeek)
            {
                SetWtdAnchorToCurrentBar(cmeNow, weekKey);
                wtdDeferredWeekYear = -1;
                return;
            }

            // Path 2: Cold start — scan backward for the most recent Sunday 17:00 bar
            if (!wtdAnchorSet)
            {
                if (wtdDeferredWeekYear == weekKey)
                    return;

                TryInitWtdAnchorFromHistory(cmeNow, weekKey);
            }
        }

        // Sets the WTD anchor to the current bar and seeds the running accumulator.
        // Used by the normal Sunday 17:00 weekly reset path.
        private void SetWtdAnchorToCurrentBar(DateTime cmeNow, int weekKey)
        {
            wtdAnchorBarIndex  = CurrentBar;
            wtdAnchorOpenPrice = Open[0];
            wtdAnchorSet       = true;
            wtdAnchorWeekYear  = weekKey;
            wtdSeededThisBar   = true;

            double anchorTypical = (High[0] + Low[0] + Close[0]) / 3.0;
            double anchorVol     = Volume[0] > 0 ? Volume[0] : 0;
            wtdPV   = anchorTypical * anchorVol;
            wtdVSum = anchorVol;

            if (EnableAnchorLogging)
                PrintWithContext("WTD_ANCHOR_RESET timeCME=" + cmeNow.ToString("yyyy-MM-dd HH:mm") +
                      " weekKey=" + weekKey +
                      " bar=" + CurrentBar +
                      " open=" + wtdAnchorOpenPrice.ToString("F2"));
        }

        // Scans backward through all available bar history to find the most
        // recent Sunday 17:00 CT bar. If found, sets the anchor there and pre-computes the
        // full running accumulator from that bar to now. If not found, leaves wtdAnchorSet=false
        // so the anchor activates naturally on the next Sunday 17:00.
        private void TryInitWtdAnchorFromHistory(DateTime cmeNow, int currentWeekKey)
        {
            var cal = System.Globalization.CultureInfo.InvariantCulture.Calendar;
            int maxLookback = CurrentBar;

            // Scan from current bar backward (i=0 is current, i=maxLookback is oldest).
            // Stop on the FIRST Sunday 17:00 hit — that is the most recent weekly open.
            for (int i = 0; i <= maxLookback; i++)
            {
                DateTime barCme = GetCmeTime(Time[i]);
                if (barCme.DayOfWeek != DayOfWeek.Sunday || barCme.Hour != 17 || barCme.Minute != 0)
                    continue;

                // Found the most recent Sunday 17:xx anchor bar
                int anchorAbsBar  = CurrentBar - i;
                int foundWeekKey  = barCme.Year * 100 +
                    cal.GetWeekOfYear(barCme, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);

                wtdAnchorBarIndex  = anchorAbsBar;
                wtdAnchorOpenPrice = Open[i];
                wtdAnchorSet       = true;
                wtdAnchorWeekYear  = foundWeekKey;
                wtdSeededThisBar   = true;

                // Pre-compute the full accumulator from the anchor bar (i) through now (0)
                wtdPV   = 0;
                wtdVSum = 0;
                for (int j = i; j >= 0; j--)
                {
                    double vol = Volume[j] > 0 ? Volume[j] : 0;
                    if (vol <= 0)
                        continue;
                    double typical = (High[j] + Low[j] + Close[j]) / 3.0;
                    wtdPV   += typical * vol;
                    wtdVSum += vol;
                }

                if (EnableAnchorLogging)
                    PrintWithContext("WTD_ANCHOR_COLD_START timeCME=" + cmeNow.ToString("yyyy-MM-dd HH:mm") +
                          " foundSunday1700=" + barCme.ToString("yyyy-MM-dd HH:mm") +
                          " barsBack=" + i +
                          " anchorBar=" + anchorAbsBar +
                          " weekKey=" + foundWeekKey +
                          " open=" + wtdAnchorOpenPrice.ToString("F2") +
                          " wtdAvwap=" + (wtdVSum > 0 ? (wtdPV / wtdVSum).ToString("F2") : "NA"));
                return;
            }

            wtdDeferredWeekYear = currentWeekKey;

            // No Sunday 17:00 bar found in the available history — defer until next week
            if (EnableAnchorLogging)
                PrintWithContext("WTD_ANCHOR_DEFERRED timeCME=" + cmeNow.ToString("yyyy-MM-dd HH:mm") +
                      " reason=NoSundayBarInHistory maxLookback=" + maxLookback);
        }

        // Accumulates one bar's contribution to the WTD AVWAP running totals.
        // Called every bar after UpdateWtdAnchorIfNeeded() so the anchor bar itself
        // is never double-counted (it is seeded inside UpdateWtdAnchorIfNeeded).
        private void UpdateWtdRunningAccumulator()
        {
            if (!EnableWtdAnchor || !wtdAnchorSet)
                return;

            // Skip the bar used to seed/reset the WTD accumulator on this same OnBarUpdate.
            if (wtdSeededThisBar || CurrentBar == wtdAnchorBarIndex)
                return;

            double typical = (High[0] + Low[0] + Close[0]) / 3.0;
            double vol = Volume[0] > 0 ? Volume[0] : 0;
            wtdPV   += typical * vol;
            wtdVSum += vol;
        }

        private bool ShouldSuppressGapDayInvalidation(int nowCme)
        {
            return isGapDay && nowCme < CmeMorningWindowStart;
        }

        private bool CanSubmitNewTrade()
        {
            if (opportunitiesToday >= Math.Min(MaxOpportunitiesPerDay, 3))
                return false;

            if (consecutiveLosses >= MaxConsecutiveLosses)
                return false;

            if (signalCooldownRemaining > 0)
                return false;

            if (dailyR <= DailyStopR)
                return false;

            if (expectancyPausedToday)
                return false;

            if (recentTradeR.Count >= RollingExpectancyTrades && GetRecentExpectancy() <= 0)
            {
                expectancyPausedToday = true;
                return false;
            }

            return true;
        }

        private double GetRecentExpectancy()
        {
            if (recentTradeR.Count == 0)
                return 0;

            double sum = 0;
            foreach (double r in recentTradeR)
                sum += r;

            return sum / recentTradeR.Count;
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
                if (EnableAnchorLogging)
                    PrintWithContext("MANUAL_ANCHORS_CLEARED");
            }
            else
            {
                if (setLong)
                {
                    DateTime longTime = BarIndexToTime(longBarIdx);
                    if (longTime > Core.Globals.MinDate)
                    {
                        ManualLongAnchorFrom = longTime;
                        if (EnableAnchorLogging)
                            PrintWithContext("MANUAL_LONG_ANCHOR_SET timeCME=" + FormatCmeTime(GetCmeTimeInt(longTime)) + " anchorFrom=" + GetCmeTime(longTime).ToString("yyyy-MM-dd HH:mm:ss"));
                    }
                }

                if (setShort)
                {
                    DateTime shortTime = BarIndexToTime(shortBarIdx);
                    if (shortTime > Core.Globals.MinDate)
                    {
                        ManualShortAnchorFrom = shortTime;
                        if (EnableAnchorLogging)
                            PrintWithContext("MANUAL_SHORT_ANCHOR_SET timeCME=" + FormatCmeTime(GetCmeTimeInt(shortTime)) + " anchorFrom=" + GetCmeTime(shortTime).ToString("yyyy-MM-dd HH:mm:ss"));
                    }
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

        private void RebuildManualAvwapAnchors()
        {
            // Hide previously attached manual indicators when re-anchoring.
            // (NT does not expose a straightforward remove for AddChartIndicator.)
            if (manualLongAvwap2 != null && manualLongAvwap2.Plots != null && manualLongAvwap2.Plots.Length > 0)
                manualLongAvwap2.Plots[0].Brush = Brushes.Transparent;
            if (manualShortAvwap2 != null && manualShortAvwap2.Plots != null && manualShortAvwap2.Plots.Length > 0)
                manualShortAvwap2.Plots[0].Brush = Brushes.Transparent;

            manualLongAvwap2 = null;
            manualShortAvwap2 = null;

            if (!UseManualAvwap2Anchors)
                return;

            if (ManualLongAnchorFrom > Core.Globals.MinDate)
            {
                manualLongAvwap2 = AVWAP2(BarsArray[0], ManualLongAnchorFrom, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
                if (manualLongAvwap2.Plots != null && manualLongAvwap2.Plots.Length > 0)
                    manualLongAvwap2.Plots[0].Brush = Brushes.Lime;
                // NOTE: AddChartIndicator can only be called in State.DataLoaded (OnStateChange).
                // RebuildManualAvwapAnchors is also called from OnBarUpdate, so do not attach here.
            }

            if (ManualShortAnchorFrom > Core.Globals.MinDate)
            {
                manualShortAvwap2 = AVWAP2(BarsArray[0], ManualShortAnchorFrom, new VWAPDesign.StdDesign { Enabled = false, Num = 2 }, new VWAPDesign.StdDesign { Enabled = false, Num = 3 }, true, true, true);
                if (manualShortAvwap2.Plots != null && manualShortAvwap2.Plots.Length > 0)
                    manualShortAvwap2.Plots[0].Brush = Brushes.Magenta;
                // NOTE: AddChartIndicator can only be called in State.DataLoaded (OnStateChange).
                // RebuildManualAvwapAnchors is also called from OnBarUpdate, so do not attach here.
            }
        }

        private void EnsureManualAnchorHotkeysHooked()
        {
            if (!UseManualAvwap2Anchors || !EnableManualAnchorHotkeys)
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

                if (EnableAnchorLogging)
                    PrintWithContext("MANUAL_ANCHOR_HOTKEYS_ENABLED mode=historical-click keys=Q(long),A(short),C(clear)");
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
            catch
            {
                // ignore teardown exceptions
            }

            manualHotkeysHooked = false;
            chartWindow = null;
        }

        private void OnManualAnchorMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (ChartControl == null || ChartBars == null)
                return;

            // Only register left-button clicks on the chart panel itself
            if (e.ChangedButton != MouseButton.Left)
                return;
            if (!(e.OriginalSource is System.Windows.Media.Visual))
                return;

            try
            {
                Point p = e.GetPosition(ChartControl);
                int barIdx = ChartBars.GetBarIdxByX(ChartControl, (int)p.X);
                if (barIdx >= 0 && barIdx <= CurrentBar)
                    lastClickedBarIndex = barIdx;
            }
            catch
            {
                // Ignore exceptions from coordinate conversion edge cases
            }
        }

        private void OnManualAnchorHotkeyPressed(object sender, KeyEventArgs e)
        {
            if (!UseManualAvwap2Anchors)
                return;

            // Don't capture keys when user is typing in a text input field
            if (e.OriginalSource is System.Windows.Controls.TextBox ||
                e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase)
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

        private bool TryGetManualAnchorValue(bool isLong, out double value)
        {
            value = double.NaN;
            if (!UseManualAvwap2Anchors)
                return false;

            AVWAP2 manual = isLong ? manualLongAvwap2 : manualShortAvwap2;
            if (manual == null || manual.Output == null || manual.Output.Count < 1)
                return false;

            value = manual.Output[0];
            return !double.IsNaN(value) && value > 0;
        }

        private double GetLongAnchor(out AnchorKind kind, out int anchorBarIndex)
        {
            if (TryGetManualAnchorValue(true, out double manualLong))
            {
                kind = AnchorKind.ManualLongAVWAP2;
                anchorBarIndex = -1;
                return manualLong;
            }

            if (structuralOverrideActive && structuralOverrideKind == AnchorKind.StructuralBull)
            {
                kind = AnchorKind.StructuralBull;
                anchorBarIndex = structuralOverrideBarIndex;
                return GetAvwapFromBar(structuralOverrideBarIndex, structuralOverridePrice);
            }

            // TEMP PRIORITY ORDER (requested): LOD -> RallyOrigin -> WTD

            // 1) LOD first preference
            if (!lodInvalidated)
            {
                kind = AnchorKind.LOD;
                anchorBarIndex = dayLowBarIndex;
                return GetAvwapFromBar(dayLowBarIndex, dayLow);
            }

            // 2) Impulse-origin long fallback
            if (EnableImpulseOriginAnchors && rallyOriginBarIndex >= 0)
            {
                double rallyAvwap = GetAvwapFromBar(rallyOriginBarIndex, rallyOriginPrice);
                if (!double.IsNaN(rallyAvwap) &&
                    Close[0] > rallyAvwap &&
                    !IsAnchorDegraded(rallyAvwap) &&
                    !IsImpulseOriginDecisivelyBroken(true, rallyOriginBarIndex, rallyOriginPrice))
                {
                    kind = AnchorKind.RallyOrigin;
                    anchorBarIndex = rallyOriginBarIndex;
                    return rallyAvwap;
                }
            }

            // 3) WTD final fallback
            if (EnableWtdAnchor && wtdAnchorSet && wtdAnchorBarIndex >= 0)
            {
                double wtd = GetWtdAvwap();
                if (!double.IsNaN(wtd) && Close[0] > wtd && !IsAnchorDegraded(wtd))
                {
                    kind = AnchorKind.WeeklyOpen;
                    anchorBarIndex = wtdAnchorBarIndex;
                    return wtd;
                }
            }

            kind = AnchorKind.LOD;
            anchorBarIndex = -1;
            return double.NaN;
        }

        private double GetShortAnchor(out AnchorKind kind, out int anchorBarIndex)
        {
            if (TryGetManualAnchorValue(false, out double manualShort))
            {
                kind = AnchorKind.ManualShortAVWAP2;
                anchorBarIndex = -1;
                return manualShort;
            }

            if (structuralOverrideActive && structuralOverrideKind == AnchorKind.StructuralBear)
            {
                kind = AnchorKind.StructuralBear;
                anchorBarIndex = structuralOverrideBarIndex;
                return GetAvwapFromBar(structuralOverrideBarIndex, structuralOverridePrice);
            }

            double selloffAvwap = double.NaN;
            bool selloffCandidateValid = false;
            if (EnableImpulseOriginAnchors && selloffOriginBarIndex >= 0)
            {
                selloffAvwap = GetAvwapFromBar(selloffOriginBarIndex, selloffOriginPrice);
                selloffCandidateValid =
                    !double.IsNaN(selloffAvwap) &&
                    Close[0] < selloffAvwap &&
                    !IsAnchorDegraded(selloffAvwap) &&
                    !IsImpulseOriginDecisivelyBroken(false, selloffOriginBarIndex, selloffOriginPrice);
            }

            double hodAvwap = hodInvalidated ? double.NaN : GetAvwapFromBar(dayHighBarIndex, dayHigh);
            bool hodCandidateValid = !hodInvalidated && !double.IsNaN(hodAvwap);

            double wtdAvwap = double.NaN;
            bool wtdCandidateValid = false;
            if (EnableWtdAnchor && wtdAnchorSet && wtdAnchorBarIndex >= 0)
            {
                wtdAvwap = GetWtdAvwap();
                wtdCandidateValid =
                    !double.IsNaN(wtdAvwap) &&
                    Close[0] < wtdAvwap &&
                    !IsAnchorDegraded(wtdAvwap);
            }

            // TEMP PRIORITY ORDER (requested): HOD -> SelloffOrigin -> WTD
            // If a higher-priority anchor is invalid/unusable, fall through to the next.
            kind = AnchorKind.HOD;
            anchorBarIndex = -1;
            double selectedAnchor = double.NaN;

            if (hodCandidateValid)
            {
                kind = AnchorKind.HOD;
                anchorBarIndex = dayHighBarIndex;
                selectedAnchor = hodAvwap;
            }
            else if (selloffCandidateValid)
            {
                kind = AnchorKind.SelloffOrigin;
                anchorBarIndex = selloffOriginBarIndex;
                selectedAnchor = selloffAvwap;
            }
            else if (wtdCandidateValid)
            {
                kind = AnchorKind.WeeklyOpen;
                anchorBarIndex = wtdAnchorBarIndex;
                selectedAnchor = wtdAvwap;
            }

            if (EnableAnchorLogging)
            {
                string selloffText = double.IsNaN(selloffAvwap) ? "NA" : selloffAvwap.ToString("F2");
                string hodText = double.IsNaN(hodAvwap) ? "NA" : hodAvwap.ToString("F2");
                string wtdText = double.IsNaN(wtdAvwap) ? "NA" : wtdAvwap.ToString("F2");
                string selectedText = double.IsNaN(selectedAnchor) ? "NA" : selectedAnchor.ToString("F2");
                string selectedKindText = double.IsNaN(selectedAnchor) ? "None" : kind.ToString();

                string decisionKey =
                    Time[0].Ticks + "|" +
                    selloffCandidateValid + "|" + hodCandidateValid + "|" + wtdCandidateValid + "|" +
                    selectedKindText + "|" + selectedText;

                if (!string.Equals(lastShortAnchorDecisionKey, decisionKey, StringComparison.Ordinal))
                {
                    PrintWithContext("SHORT_ANCHOR_CANDIDATES" +
                          " selloff=" + selloffText + " valid=" + selloffCandidateValid +
                          " hod=" + hodText + " valid=" + hodCandidateValid + " invalidated=" + hodInvalidated +
                          " wtd=" + wtdText + " valid=" + wtdCandidateValid +
                          " selectedKind=" + selectedKindText +
                          " selected=" + selectedText);
                    lastShortAnchorDecisionKey = decisionKey;
                }
            }

            return selectedAnchor;
        }

        private double GetAvwapFromBar(int anchorBarIndex, double fallbackPrice)
        {
            if (anchorBarIndex < 0 || anchorBarIndex > CurrentBar)
                return fallbackPrice;

            int anchorBarsAgo = CurrentBar - anchorBarIndex;

            // All callers (LOD/HOD: intraday, Structural: <=40 bars) are within the 256-bar window.
            // WTD now uses a running accumulator (GetWtdAvwap) and no longer calls this method.

            double pv = 0;
            double vSum = 0;

            for (int i = anchorBarsAgo; i >= 0; i--)
            {
                double volume = Volume[i];
                if (volume <= 0)
                    continue;

                double typical = (High[i] + Low[i] + Close[i]) / 3.0;
                pv += typical * volume;
                vSum += volume;
            }

            return vSum > 0 ? pv / vSum : fallbackPrice;
        }

        private double GetWtdAvwap()
        {
            if (!EnableWtdAnchor || !wtdAnchorSet || wtdAnchorBarIndex < 0)
                return double.NaN;

            // Use the running accumulator — accurate for any number of bars since the
            // Sunday 17:00 CT anchor, with no dependency on NinjaTrader's 256-bar lookback limit.
            return wtdVSum > 0 ? wtdPV / wtdVSum : wtdAnchorOpenPrice;
        }

        private bool HasReclaimAbove(double anchor, int lookbackBars)
        {
            int lookback = Math.Min(lookbackBars, CurrentBar - 2);
            if (lookback < 0)
                return false;

            for (int i = 0; i <= lookback; i++)
            {
                if (Close[i + 1] <= anchor && Close[i] > anchor)
                    return true;
            }

            return false;
        }

        private bool HasRejectBelow(double anchor, int lookbackBars)
        {
            int lookback = Math.Min(lookbackBars, CurrentBar - 2);
            if (lookback < 0)
                return false;

            for (int i = 0; i <= lookback; i++)
            {
                if (Close[i + 1] >= anchor && Close[i] < anchor)
                    return true;
            }

            return false;
        }

        private LodTier DetermineLodTier(int anchorBarIndex)
        {
            if (anchorBarIndex < 0 || anchorBarIndex > CurrentBar)
                return LodTier.TierB;

            int barsSinceAnchor = CurrentBar - anchorBarIndex;
            int evalBars = Math.Min(Math.Max(1, barsSinceAnchor), DefendedLowMaxBars);

            double maxHigh = High[0];
            for (int i = 1; i <= evalBars; i++)
                maxHigh = Math.Max(maxHigh, High[i]);

            bool sharpRejection = (maxHigh - dayLow) >= (DefendedLowImpulseAtr * atr[0]);
            bool retestHolds = true;
            for (int i = evalBars; i >= 0; i--)
            {
                if (Low[i] < dayLow - TickSize)
                {
                    retestHolds = false;
                    break;
                }
            }

            bool orderlyReaction = !IsAnchorDegraded(GetAvwapFromBar(anchorBarIndex, dayLow));
            return (sharpRejection && retestHolds && orderlyReaction) ? LodTier.TierA : LodTier.TierB;
        }

        private LodTier DetermineHodTier(int anchorBarIndex)
        {
            if (anchorBarIndex < 0 || anchorBarIndex > CurrentBar)
                return LodTier.TierB;

            int barsSinceAnchor = CurrentBar - anchorBarIndex;
            int evalBars = Math.Min(Math.Max(1, barsSinceAnchor), DefendedLowMaxBars);

            double minLow = Low[0];
            for (int i = 1; i <= evalBars; i++)
                minLow = Math.Min(minLow, Low[i]);

            bool sharpRejection = (dayHigh - minLow) >= (DefendedLowImpulseAtr * atr[0]);
            bool retestHolds = true;
            for (int i = evalBars; i >= 0; i--)
            {
                if (High[i] > dayHigh + TickSize)
                {
                    retestHolds = false;
                    break;
                }
            }

            bool orderlyReaction = !IsAnchorDegraded(GetAvwapFromBar(anchorBarIndex, dayHigh));
            return (sharpRejection && retestHolds && orderlyReaction) ? LodTier.TierA : LodTier.TierB;
        }

        private void SubmitEntry(bool isLong, int quantity, int stopTicks, int targetTicks, bool isTierB)
        {
            string side = isLong ? "L" : "S";
            string signal = side + "-" + Time[0].ToString("yyyyMMddHHmmss");

            SetStopLoss(signal, CalculationMode.Ticks, stopTicks, false);
            SetProfitTarget(signal, CalculationMode.Ticks, targetTicks);

            pendingSignal = signal;
            pendingRiskCurrency = quantity * stopTicks * TickSize * Instrument.MasterInstrument.PointValue;
            activeRiskCurrency = 0;
            activeSignal = signal;
            activeStopTicks = stopTicks;
            activeQuantity = 0;
            breakevenMoved = false;
            activeTradeWasTierB = isTierB;
            currentTradeMfeR = 0;
            currentTradeMaeR = 0;

            opportunitiesToday++;
            signalCooldownRemaining = Math.Max(signalCooldownRemaining, SignalCooldownBars);
            if (isTierB)
                tierBAttemptUsed = true;

            if (isLong)
                EnterLong(quantity, signal);
            else
                EnterShort(quantity, signal);
        }

        private bool TryApplyRiskCap(ref int quantity, ref int stopTicks, out bool stopCompressed)
        {
            stopCompressed = false;
            int originalStopTicks = stopTicks;

            if (quantity <= 0 || stopTicks <= 0)
                return false;

            double tickRisk = TickSize * Instrument.MasterInstrument.PointValue;
            if (tickRisk <= 0)
                return false;

            double riskPerContract = stopTicks * tickRisk;
            if (riskPerContract <= 0 || MaxRiskPerTradeDollars <= 0)
                return false;

            double totalRisk = quantity * riskPerContract;
            if (totalRisk <= MaxRiskPerTradeDollars)
                return true;

            int maxQty = (int)Math.Floor(MaxRiskPerTradeDollars / riskPerContract);
            if (maxQty < 1)
            {
                if (!AllowRiskCapStopCompression)
                    return false;

                int maxAffordableStopTicks = (int)Math.Floor(MaxRiskPerTradeDollars / (quantity * tickRisk));
                if (maxAffordableStopTicks < MinStopTicks)
                    return false;

                if (maxAffordableStopTicks >= stopTicks)
                    return true;

                int maxCompressionTicks = (int)Math.Floor(originalStopTicks * Math.Max(0.0, MaxStopCompressionFraction));
                int compressedBy = originalStopTicks - maxAffordableStopTicks;
                if (compressedBy > maxCompressionTicks)
                    return false;

                stopTicks = maxAffordableStopTicks;
                stopCompressed = true;
                return true;
            }

            quantity = Math.Min(quantity, maxQty);
            return quantity >= 1;
        }

        private void UpdateSessionAtrForStops()
        {
            if (CurrentBar < 1)
                return;

            double prevClose = Close[1];
            double trueRange = Math.Max(High[0] - Low[0], Math.Max(Math.Abs(High[0] - prevClose), Math.Abs(Low[0] - prevClose)));
            if (trueRange <= 0)
                trueRange = TickSize;

            sessionTrueRangeWindow.Enqueue(trueRange);
            sessionTrueRangeSum += trueRange;

            int window = Math.Max(1, AtrPeriod);
            while (sessionTrueRangeWindow.Count > window)
                sessionTrueRangeSum -= sessionTrueRangeWindow.Dequeue();

            sessionAtrForStops = sessionTrueRangeWindow.Count > 0
                ? sessionTrueRangeSum / sessionTrueRangeWindow.Count
                : 0;
        }

        private double GetStopAtrValue()
        {
            if (UseSessionAtrForStops && sessionAtrForStops > 0)
                return sessionAtrForStops;

            return atr[0];
        }

        private void ManageOpenPosition()
        {
            if (string.IsNullOrEmpty(activeSignal) || activeStopTicks <= 0)
                return;

            double entry = Position.AveragePrice;
            double riskPoints = activeStopTicks * TickSize;
            if (riskPoints <= 0)
                return;

            if (Position.MarketPosition == MarketPosition.Long)
            {
                double favorable = Math.Max(0, High[0] - entry);
                double adverse = Math.Max(0, entry - Low[0]);
                currentTradeMfeR = Math.Max(currentTradeMfeR, favorable / riskPoints);
                currentTradeMaeR = Math.Max(currentTradeMaeR, adverse / riskPoints);

                if (!breakevenMoved && High[0] >= entry + riskPoints)
                {
                    // OnBarClose can see a bar that touched 1R intrabar but closed back through entry.
                    // Only submit BE stop updates when the stop remains valid relative to market.
                    if (Close[0] > entry)
                    {
                        SetStopLoss(activeSignal, CalculationMode.Price, entry, false);
                        breakevenMoved = true;
                    }
                    else if (EnableAnchorLogging)
                    {
                        PrintWithContext("SKIP_BREAKEVEN_INVALID side=LONG entry=" + entry.ToString("F2") +
                              " close=" + Close[0].ToString("F2") +
                              " reason=StopWouldBeAboveOrAtMarket");
                    }
                }
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                double favorable = Math.Max(0, entry - Low[0]);
                double adverse = Math.Max(0, High[0] - entry);
                currentTradeMfeR = Math.Max(currentTradeMfeR, favorable / riskPoints);
                currentTradeMaeR = Math.Max(currentTradeMaeR, adverse / riskPoints);

                if (!breakevenMoved && Low[0] <= entry - riskPoints)
                {
                    // OnBarClose can see a bar that touched 1R intrabar but closed back through entry.
                    // Only submit BE stop updates when the stop remains valid relative to market.
                    if (Close[0] < entry)
                    {
                        SetStopLoss(activeSignal, CalculationMode.Price, entry, false);
                        breakevenMoved = true;
                    }
                    else if (EnableAnchorLogging)
                    {
                        PrintWithContext("SKIP_BREAKEVEN_INVALID side=SHORT entry=" + entry.ToString("F2") +
                              " close=" + Close[0].ToString("F2") +
                              " reason=StopWouldBeBelowOrAtMarket");
                    }
                }
            }
        }

        private void TryPromoteStructuralAnchor(int nowCme)
        {
            if (isGapDay && nowCme < CmeMorningWindowStart)
                return;

            bool bullish = ema[0] > ema[Math.Min(TrendSlopeBars, CurrentBar)];
            bool bearish = ema[0] < ema[Math.Min(TrendSlopeBars, CurrentBar)];
            if (!bullish && !bearish)
                return;

            double baseAnchor = bullish
                ? GetAvwapFromBar(dayLowBarIndex, dayLow)
                : GetAvwapFromBar(dayHighBarIndex, dayHigh);

            if (!IsAnchorDegraded(baseAnchor))
                return;

            if (!TryFindStructuralAnchor(
                    bullish,
                    out double candidatePrice,
                    out int candidateBarIndex,
                    out AnchorKind kind,
                    out double candidateScore))
                return;

            double baseScore = EvaluateAnchorScore(baseAnchor);
            if (candidateScore < baseScore * StructureScoreMargin)
                return;

            structuralOverrideUsed = true;
            structuralOverrideActive = true;
            structuralOverrideKind = kind;
            structuralOverridePrice = candidatePrice;
            structuralOverrideBarIndex = candidateBarIndex;
            structuralOverrideActivatedBarIndex = CurrentBar;
            overrideCooldownRemaining = OverrideCooldownBars;

            if (EnableAnchorLogging)
            {
                PrintWithContext("ANCHOR_OVERRIDE_ACTIVATED timeCME=" + FormatCmeTime(nowCme) +
                      " kind=" + kind +
                      " bar=" + candidateBarIndex +
                      " price=" + candidatePrice.ToString("F2") +
                      " candidateScore=" + candidateScore.ToString("F2") +
                      " baseScore=" + baseScore.ToString("F2"));
            }
        }

        private void UpdateImpulseOriginAnchors(bool allowAnchorRefresh, int nowCme)
        {
            if (!EnableImpulseOriginAnchors || !allowAnchorRefresh)
                return;

            if (TryFindImpulseOriginAnchor(
                    true,
                    out double bullPrice,
                    out int bullBarIndex,
                    out double bullScore) &&
                ShouldReplaceImpulseOriginAnchor(true, rallyOriginBarIndex, rallyOriginPrice, rallyOriginScore, bullBarIndex, bullScore))
            {
                rallyOriginBarIndex = bullBarIndex;
                rallyOriginPrice = bullPrice;
                rallyOriginScore = bullScore;

                if (EnableAnchorLogging)
                {
                    PrintWithContext("ORIGIN_ANCHOR_SET side=LONG timeCME=" + FormatCmeTime(nowCme) +
                          " bar=" + bullBarIndex +
                          " price=" + bullPrice.ToString("F2") +
                          " score=" + bullScore.ToString("F2"));
                }
            }

            if (TryFindImpulseOriginAnchor(
                    false,
                    out double bearPrice,
                    out int bearBarIndex,
                    out double bearScore) &&
                ShouldReplaceImpulseOriginAnchor(false, selloffOriginBarIndex, selloffOriginPrice, selloffOriginScore, bearBarIndex, bearScore))
            {
                selloffOriginBarIndex = bearBarIndex;
                selloffOriginPrice = bearPrice;
                selloffOriginScore = bearScore;

                if (EnableAnchorLogging)
                {
                    PrintWithContext("ORIGIN_ANCHOR_SET side=SHORT timeCME=" + FormatCmeTime(nowCme) +
                          " bar=" + bearBarIndex +
                          " price=" + bearPrice.ToString("F2") +
                          " score=" + bearScore.ToString("F2"));
                }
            }
        }

        private bool ShouldReplaceImpulseOriginAnchor(
            bool bullish,
            int currentBarIndex,
            double currentPrice,
            double currentScore,
            int candidateBarIndex,
            double candidateScore)
        {
            if (candidateBarIndex < 0 || candidateScore <= 0)
                return false;

            if (currentBarIndex < 0)
                return true;

            // If the current origin has been decisively broken, rotate immediately.
            // Example: short selloff origin is valid until price clearly reclaims above it.
            if (IsImpulseOriginDecisivelyBroken(bullish, currentBarIndex, currentPrice))
                return true;

            if (candidateBarIndex > currentBarIndex)
            {
                if (bullish)
                {
                    // For rally origins, avoid rotating to newer sub-legs too aggressively.
                    // Keep older rally origin unless newer candidate is clearly stronger.
                    const double newerRallyUpgrade = 1.05;
                    return candidateScore >= currentScore * newerRallyUpgrade;
                }

                // For selloff origins, prefer fresher impulses once quality is comparable.
                const double newerSelloffRetention = 0.85;
                return candidateScore >= currentScore * newerSelloffRetention;
            }

            if (candidateBarIndex == currentBarIndex)
                return candidateScore >= currentScore;

            // Allow controlled back-correction to an older origin when quality is comparable.
            // This lets the marker move from a later sub-leg to the true start of the selloff/rally.
            const double backCorrectionRetention = 0.90;
            return candidateScore >= currentScore * backCorrectionRetention;
        }

        private bool IsImpulseOriginDecisivelyBroken(bool bullish, int originBarIndex, double originPrice)
        {
            if (originBarIndex < 0 || originBarIndex > CurrentBar)
                return true;

            double avwap = GetAvwapFromBar(originBarIndex, originPrice);
            if (double.IsNaN(avwap))
                return true;

            // "Sufficiently crossed" definition: 2 closes beyond AVWAP by 2 ticks
            // in the last 3 bars.
            const int confirmBarsNeeded = 2;
            const int lookbackBars = 3;
            double buffer = 2 * TickSize;

            int confirmations = 0;
            int lookback = Math.Min(lookbackBars - 1, CurrentBar);
            for (int i = 0; i <= lookback; i++)
            {
                bool broken = bullish
                    ? (Close[i] < avwap - buffer)
                    : (Close[i] > avwap + buffer);

                if (broken)
                    confirmations++;
            }

            return confirmations >= confirmBarsNeeded;
        }

        private bool TryFindImpulseOriginAnchor(
            bool bullish,
            out double anchorPrice,
            out int anchorBarIndex,
            out double score)
        {
            anchorPrice = 0;
            anchorBarIndex = -1;
            score = 0;

            int maxLookback = Math.Min(StructureLookbackBars, CurrentBar - 2);
            if (maxLookback < ImpulseBars + 2)
                return false;

            double bestScore = double.MinValue;
            double bestPrice = 0;
            int bestBarIndex = -1;

            // Pass 1: find highest-quality qualifying impulse window.
            for (int i = maxLookback; i >= ImpulseBars; i--)
            {
                if (!TryGetImpulseCandidate(bullish, i, out double candidate, out int candidateIdx, out double candidateScore))
                    continue;

                if (candidateScore <= bestScore)
                    continue;

                bestScore = candidateScore;
                bestPrice = candidate;
                bestBarIndex = candidateIdx;
            }

            if (bestBarIndex < 0 || bestScore <= 0)
                return false;

            // Pass 2 selection is side-specific:
            // - Bullish (rally): prefer earlier comparable origin to avoid late sub-leg anchors.
            // - Bearish (selloff): prefer most recent comparable origin to stay aligned with active selloff.
            const double originScoreRetention = 0.85;
            double minComparableScore = bestScore * originScoreRetention;

            if (bullish)
            {
                for (int i = maxLookback; i >= ImpulseBars; i--)
                {
                    if (!TryGetImpulseCandidate(bullish, i, out double candidate, out int candidateIdx, out double candidateScore))
                        continue;

                    if (candidateScore + 1e-9 < minComparableScore)
                        continue;

                    anchorPrice = candidate;
                    anchorBarIndex = candidateIdx;
                    score = candidateScore;
                    return true;
                }
            }
            else
            {
                for (int i = ImpulseBars; i <= maxLookback; i++)
                {
                    if (!TryGetImpulseCandidate(bullish, i, out double candidate, out int candidateIdx, out double candidateScore))
                        continue;

                    if (candidateScore + 1e-9 < minComparableScore)
                        continue;

                    anchorPrice = candidate;
                    anchorBarIndex = candidateIdx;
                    score = candidateScore;
                    return true;
                }
            }

            anchorPrice = bestPrice;
            anchorBarIndex = bestBarIndex;
            score = bestScore;
            return true;
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

        private bool TryFindStructuralAnchor(
            bool bullish,
            out double anchorPrice,
            out int anchorBarIndex,
            out AnchorKind kind,
            out double score)
        {
            anchorPrice = 0;
            anchorBarIndex = -1;
            kind = bullish ? AnchorKind.StructuralBull : AnchorKind.StructuralBear;
            score = 0;

            int maxLookback = Math.Min(StructureLookbackBars, CurrentBar - 2);
            if (maxLookback < ImpulseBars + 2)
                return false;

            for (int i = maxLookback; i >= ImpulseBars; i--)
            {
                if (!TryGetImpulseCandidate(bullish, i, out double candidate, out int candidateIdx, out double candidateScore))
                    continue;

                if (candidateScore <= score)
                    continue;

                anchorPrice = candidate;
                anchorBarIndex = candidateIdx;
                score = candidateScore;
            }

            return score > 0 && anchorBarIndex >= 0;
        }

        private bool TryGetImpulseCandidate(
            bool bullish,
            int startBarsAgo,
            out double candidatePrice,
            out int candidateBarIndex,
            out double candidateScore)
        {
            candidatePrice = 0;
            candidateBarIndex = -1;
            candidateScore = 0;

            int end = startBarsAgo - ImpulseBars + 1;
            if (end < 0)
                return false;

            double atrRef = Math.Max(TickSize, atr[startBarsAgo]);

            int directionalBars = 0;
            double volSum = 0;
            for (int j = startBarsAgo; j >= end; j--)
            {
                if (bullish && Close[j] > Open[j])
                    directionalBars++;
                if (!bullish && Close[j] < Open[j])
                    directionalBars++;
                volSum += Volume[j];
            }

            if (directionalBars < (int)Math.Ceiling(0.7 * ImpulseBars))
                return false;

            double avgVol = volSum / ImpulseBars;
            double baselineVol = volSma[startBarsAgo];
            if (double.IsNaN(baselineVol) || baselineVol <= 0)
                return false;

            if (avgVol < StructureVolumeMultiple * baselineVol)
                return false;

            // Find the true origin pivot inside the impulse window instead of always
            // assuming the first bar in the window is the origin.
            int originBarsAgo = startBarsAgo;
            double originPrice = bullish ? Low[startBarsAgo] : High[startBarsAgo];
            for (int j = startBarsAgo; j >= end; j--)
            {
                if (bullish)
                {
                    if (Low[j] < originPrice)
                    {
                        originPrice = Low[j];
                        originBarsAgo = j;
                    }
                }
                else
                {
                    if (High[j] > originPrice)
                    {
                        originPrice = High[j];
                        originBarsAgo = j;
                    }
                }
            }

            // Guardrail: if the detected pivot is too late in the impulse window,
            // it's likely a sub-leg and not the true move origin.
            int originDelayBars = startBarsAgo - originBarsAgo;
            int maxAllowedOriginDelay = Math.Max(1, ImpulseBars / 3);
            if (originDelayBars > maxAllowedOriginDelay)
                return false;

            double netMove = bullish ? (Close[end] - originPrice) : (originPrice - Close[end]);
            if (netMove < StructureDisplacementAtr * atrRef)
                return false;

            candidatePrice = originPrice;
            candidateBarIndex = CurrentBar - originBarsAgo;
            double candidateAvwap = GetAvwapFromBar(candidateBarIndex, candidatePrice);

            // Favor larger/cleaner moves, but lightly penalize "late" origins.
            double timingPenalty = 0.25 * (originDelayBars / (double)Math.Max(1, ImpulseBars));
            candidateScore = (netMove / atrRef) + EvaluateAnchorScore(candidateAvwap) - timingPenalty;
            return true;
        }

        private double GetStructuralAvwapOrPrice()
        {
            if (structuralOverrideBarIndex < 0)
                return structuralOverridePrice;

            return GetAvwapFromBar(structuralOverrideBarIndex, structuralOverridePrice);
        }

        private bool IsAnchorDegraded(double anchor)
        {
            int flips = 0;
            int lookback = Math.Min(ChopLookbackBars, CurrentBar - 2);
            if (lookback < 3)
                return false;

            int priorSide = Math.Sign(Close[lookback] - anchor);
            for (int i = lookback - 1; i >= 0; i--)
            {
                int side = Math.Sign(Close[i] - anchor);
                if (side != 0 && priorSide != 0 && side != priorSide)
                    flips++;
                if (side != 0)
                    priorSide = side;
            }

            return flips >= ChopFlipThreshold;
        }

        private double EvaluateAnchorScore(double anchor)
        {
            int lookback = Math.Min(StructureLookbackBars, CurrentBar - 2);
            if (lookback < 5)
                return 0;

            double zone = AnchorZoneTicks * TickSize;
            int respects = 0;
            int flips = 0;
            int priorSide = Math.Sign(Close[lookback] - anchor);

            for (int i = lookback - 1; i >= 0; i--)
            {
                bool near = (Math.Abs(High[i] - anchor) <= zone) || (Math.Abs(Low[i] - anchor) <= zone);
                if (near)
                    respects++;

                int side = Math.Sign(Close[i] - anchor);
                if (side != 0 && priorSide != 0 && side != priorSide)
                    flips++;
                if (side != 0)
                    priorSide = side;
            }

            return respects - (0.75 * flips);
        }

        private void PublishAnchorTelemetry(
            int nowCme,
            AnchorKind longKind,
            double longAnchor,
            int longAnchorBar,
            int longAnchorAge,
            bool longAnchorUsable,
            bool longAnchorChoppy,
            AnchorKind shortKind,
            double shortAnchor,
            int shortAnchorBar,
            int shortAnchorAge,
            bool shortAnchorUsable,
            bool shortAnchorChoppy)
        {
            string longText = FormatAnchorDescriptor(longKind, longAnchor, longAnchorBar, longAnchorAge, longAnchorUsable, longAnchorChoppy);
            string shortText = FormatAnchorDescriptor(shortKind, shortAnchor, shortAnchorBar, shortAnchorAge, shortAnchorUsable, shortAnchorChoppy);
            string currentRelevantAnchorText = FormatCurrentRelevantAnchor(
                longKind,
                longAnchor,
                longAnchorBar,
                longAnchorUsable,
                shortKind,
                shortAnchor,
                shortAnchorBar,
                shortAnchorUsable);
            string nextLongText = FormatNextAnchorHint(true, longAnchorBar, longAnchor);
            string nextShortText = FormatNextAnchorHint(false, shortAnchorBar, shortAnchor);
            double wtdAvwapNow = GetWtdAvwap();
            string wtdText = (!double.IsNaN(wtdAvwapNow) && wtdAnchorBarIndex >= 0)
                ? "bar=" + wtdAnchorBarIndex + " @" + wtdAvwapNow.ToString("F2") +
                  " choppy=" + IsAnchorDegraded(wtdAvwapNow)
                : "NA";

            string stateKey = longKind + "|" + longAnchorBar + "|" + longAnchorUsable + "|" + longAnchorChoppy +
                              "|" + shortKind + "|" + shortAnchorBar + "|" + shortAnchorUsable + "|" + shortAnchorChoppy +
                              "|" + structuralOverrideActive + "|" + structuralOverrideKind + "|" + structuralOverrideBarIndex +
                              "|" + lodInvalidated + "|" + hodInvalidated +
                              "|" + wtdAnchorBarIndex + "|" + wtdAvwapNow.ToString("F2");

            if (EnableAnchorLogging && !string.Equals(lastAnchorStateKey, stateKey, StringComparison.Ordinal))
            {
                PrintWithContext("ANCHOR_STATE timeCME=" + FormatCmeTime(nowCme) +
                      " firstAnchor=" + longText +
                      " secondAnchor=" + shortText +
                      " currentRelevant=" + currentRelevantAnchorText +
                      " nextFirst=" + nextLongText +
                      " nextSecond=" + nextShortText +
                      " override=" + (structuralOverrideActive
                          ? structuralOverrideKind + " bar=" + structuralOverrideBarIndex
                          : "None") +
                      " wtd=" + wtdText +
                      " lodInvalidated=" + lodInvalidated +
                      " hodInvalidated=" + hodInvalidated);
                lastAnchorStateKey = stateKey;
            }

            if (ChartControl == null)
                return;

            if (!ShowAnchorStatusOnChart)
            {
                RemoveDrawObject(AnchorStatusDrawTag);
                RemoveDrawObject(LongAnchorMarkerTag);
                RemoveDrawObject(LongAnchorLabelTag);
                RemoveDrawObject(ShortAnchorMarkerTag);
                RemoveDrawObject(ShortAnchorLabelTag);
                RemoveDrawObject(RallyOriginMarkerTag);
                RemoveDrawObject(RallyOriginLabelTag);
                RemoveDrawObject(SelloffOriginMarkerTag);
                RemoveDrawObject(SelloffOriginLabelTag);
                return;
            }

            bool firstAnchorValid = longAnchorUsable && !double.IsNaN(longAnchor);
            bool secondAnchorValid = shortAnchorUsable && !double.IsNaN(shortAnchor);
            string firstAnchorTime = GetAnchorTimeLabel(longKind, longAnchorBar);
            string secondAnchorTime = GetAnchorTimeLabel(shortKind, shortAnchorBar);

            string relevantAnchorTime = "NA";
            if (firstAnchorValid && !secondAnchorValid)
                relevantAnchorTime = firstAnchorTime;
            else if (secondAnchorValid && !firstAnchorValid)
                relevantAnchorTime = secondAnchorTime;
            else if (firstAnchorValid && secondAnchorValid)
                relevantAnchorTime = Math.Abs(Close[0] - longAnchor) <= Math.Abs(Close[0] - shortAnchor)
                    ? firstAnchorTime
                    : secondAnchorTime;

            string biasAtAnchor = ComputeBiasAtAnchorText(longAnchor, shortAnchor);
            string chartText =
                "First Anchor Time: " + firstAnchorTime + "\n" +
                "Second Anchor Time: " + secondAnchorTime + "\n" +
                "Relevant Anchor Time: " + relevantAnchorTime + "\n" +
                "Bias at Anchor: " + biasAtAnchor;

            Draw.TextFixed(this, AnchorStatusDrawTag, chartText, TextPosition.BottomLeft);
            DrawAnchorOriginMarkers(longKind, longAnchor, longAnchorBar, shortKind, shortAnchor, shortAnchorBar);
            RemoveDrawObject(RallyOriginMarkerTag);
            RemoveDrawObject(RallyOriginLabelTag);
            RemoveDrawObject(SelloffOriginMarkerTag);
            RemoveDrawObject(SelloffOriginLabelTag);
        }

        private static string FormatCmeTime(int cmeTime)
        {
            int hour = cmeTime / 10000;
            int minute = (cmeTime / 100) % 100;
            return hour.ToString("00") + ":" + minute.ToString("00");
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

        private string FormatAnchorDescriptor(
            AnchorKind kind,
            double anchor,
            int anchorBar,
            int anchorAge,
            bool usable,
            bool choppy)
        {
            string priceText = double.IsNaN(anchor) ? "NA" : anchor.ToString("F2");
            string barText = anchorBar >= 0 ? anchorBar.ToString() : "NA";
            string ageText = anchorBar >= 0 ? anchorAge.ToString() : "NA";
            string anchorOriginCmeText = GetAnchorOriginCmeText(anchorBar);
            string anchorTimeText = GetAnchorTimeText(anchorBar);
            return kind + " @" + priceText +
                   " bar=" + barText +
                   " originCME=" + anchorOriginCmeText +
                   " anchorTime=" + anchorTimeText +
                   " age=" + ageText +
                   " usable=" + usable +
                   " choppy=" + choppy;
        }

        private string GetAnchorOriginCmeText(int anchorBarIndex)
        {
            if (anchorBarIndex < 0 || anchorBarIndex > CurrentBar || Time == null || Time.Count == 0)
                return "NA";

            int anchorBarsAgo = CurrentBar - anchorBarIndex;
            if (anchorBarsAgo < 0 || anchorBarsAgo >= Time.Count)
                return "NA";

            return GetCmeTime(Time[anchorBarsAgo]).ToString("HH:mm");
        }

        private void DrawAnchorOriginMarkers(
            AnchorKind longKind,
            double longAnchor,
            int longAnchorBar,
            AnchorKind shortKind,
            double shortAnchor,
            int shortAnchorBar)
        {
            DrawAnchorOriginMarker(LongAnchorMarkerTag, LongAnchorLabelTag, longKind, longAnchor, longAnchorBar, true);
            DrawAnchorOriginMarker(ShortAnchorMarkerTag, ShortAnchorLabelTag, shortKind, shortAnchor, shortAnchorBar, false);
        }

        private void DrawAnchorOriginMarker(
            string markerTag,
            string labelTag,
            AnchorKind kind,
            double anchorPrice,
            int anchorBarIndex,
            bool isLong)
        {
            if (anchorBarIndex < 0 || anchorBarIndex > CurrentBar || double.IsNaN(anchorPrice))
            {
                RemoveDrawObject(markerTag);
                RemoveDrawObject(labelTag);
                return;
            }

            int barsAgo = CurrentBar - anchorBarIndex;
            if (barsAgo < 0)
                return;

            string originCme = GetAnchorOriginCmeText(anchorBarIndex);
            string label = (isLong ? "L " : "S ") + kind + " origin " + originCme + " CME";

            Draw.Dot(this, markerTag, false, barsAgo, anchorPrice, isLong ? Brushes.LimeGreen : Brushes.OrangeRed);
            Draw.Text(this, labelTag, label, barsAgo, anchorPrice, Brushes.White);
        }

        private void DrawImpulseOriginMarker(
            string markerTag,
            string labelTag,
            int originBarIndex,
            double originPrice,
            string labelPrefix,
            Brush markerBrush)
        {
            if (originBarIndex < 0 || originBarIndex > CurrentBar || originPrice <= 0)
            {
                RemoveDrawObject(markerTag);
                RemoveDrawObject(labelTag);
                return;
            }

            int barsAgo = CurrentBar - originBarIndex;
            if (barsAgo < 0)
                return;

            string originCme = GetAnchorOriginCmeText(originBarIndex);
            string label = labelPrefix + " " + originCme + " CME";

            Draw.Dot(this, markerTag, false, barsAgo, originPrice, markerBrush);
            Draw.Text(this, labelTag, label, barsAgo, originPrice, Brushes.White);
        }

        private string GetAnchorTimeText(int anchorBarIndex)
        {
            if (anchorBarIndex < 0 || anchorBarIndex > CurrentBar || Time == null || Time.Count == 0)
                return "NA";

            int anchorBarsAgo = CurrentBar - anchorBarIndex;
            if (anchorBarsAgo < 0 || anchorBarsAgo >= Time.Count)
                return "NA";

            DateTime anchorCmeTime = GetCmeTime(Time[anchorBarsAgo]);
            return anchorCmeTime.ToString("yyyy-MM-dd HH:mm:ss") + " CME";
        }

        private string FormatNextAnchorHint(bool isLong, int activeAnchorBar, double activeAnchorPrice)
        {
            if (activeAnchorBar >= 0 && !double.IsNaN(activeAnchorPrice))
            {
                return "Active from " + GetAnchorOriginCmeText(activeAnchorBar) +
                       " @" + activeAnchorPrice.ToString("F2");
            }

            int candidateBar = isLong ? defendedLowCandidateBarIndex : defendedHighCandidateBarIndex;
            double candidatePrice = isLong ? defendedLowCandidatePrice : defendedHighCandidatePrice;
            bool invalidated = isLong ? lodInvalidated : hodInvalidated;
            string side = isLong ? "LOD" : "HOD";

            if (candidateBar >= 0 && candidateBar <= CurrentBar && candidatePrice > 0)
            {
                return "Candidate " + side +
                       " from " + GetAnchorOriginCmeText(candidateBar) +
                       " @" + candidatePrice.ToString("F2") +
                       (invalidated ? " (waiting defend/recovery)" : " (waiting activation)");
            }

            return "Waiting new " + side + " structure";
        }

        private string GetAnchorTimeLabel(AnchorKind kind, int anchorBar)
        {
            if (kind == AnchorKind.ManualLongAVWAP2)
                return ManualLongAnchorFrom > Core.Globals.MinDate
                    ? GetCmeTime(ManualLongAnchorFrom).ToString("HH:mm")
                    : "NA";

            if (kind == AnchorKind.ManualShortAVWAP2)
                return ManualShortAnchorFrom > Core.Globals.MinDate
                    ? GetCmeTime(ManualShortAnchorFrom).ToString("HH:mm")
                    : "NA";

            if (anchorBar >= 0 && anchorBar <= CurrentBar)
                return GetCmeTime(BarIndexToTime(anchorBar)).ToString("HH:mm");

            return "NA";
        }

        private string FormatCurrentRelevantAnchor(
            AnchorKind longKind,
            double longAnchor,
            int longAnchorBar,
            bool longAnchorUsable,
            AnchorKind shortKind,
            double shortAnchor,
            int shortAnchorBar,
            bool shortAnchorUsable)
        {
            bool longValid = longAnchorUsable && !double.IsNaN(longAnchor) && longAnchorBar >= 0;
            bool shortValid = shortAnchorUsable && !double.IsNaN(shortAnchor) && shortAnchorBar >= 0;

            if (!longValid && !shortValid)
                return "NA";

            if (longValid && !shortValid)
                return "FIRST " + longKind + " from " + GetAnchorOriginCmeText(longAnchorBar) + " @" + longAnchor.ToString("F2");

            if (shortValid && !longValid)
                return "SECOND " + shortKind + " from " + GetAnchorOriginCmeText(shortAnchorBar) + " @" + shortAnchor.ToString("F2");

            double longDistance = Math.Abs(Close[0] - longAnchor);
            double shortDistance = Math.Abs(Close[0] - shortAnchor);
            bool chooseLong = longDistance <= shortDistance;

            return chooseLong
                ? "FIRST " + longKind + " from " + GetAnchorOriginCmeText(longAnchorBar) + " @" + longAnchor.ToString("F2")
                : "SECOND " + shortKind + " from " + GetAnchorOriginCmeText(shortAnchorBar) + " @" + shortAnchor.ToString("F2");
        }

        private void EvaluateAnchorRetestBreakout(int nowCme, AnchorKind longKind, double longAnchor, int longAnchorBar, bool longAnchorUsable, AnchorKind shortKind, double shortAnchor, int shortAnchorBar, bool shortAnchorUsable)
        {
            if (!longAnchorUsable || longAnchorBar < 0 || double.IsNaN(longAnchor))
            {
                longTouchSeen = false;
                longCloseBackSeen = false;
                longBullishSeen = false;
                longFirstTouchBar = -1;
                longTouchAnchorBar = -1;
            }
            else if (longTouchAnchorBar != longAnchorBar)
            {
                longTouchSeen = false;
                longCloseBackSeen = false;
                longBullishSeen = false;
                longFirstTouchBar = -1;
                longTouchAnchorBar = longAnchorBar;
            }

            if (!shortAnchorUsable || shortAnchorBar < 0 || double.IsNaN(shortAnchor))
            {
                shortTouchSeen = false;
                shortCloseBackSeen = false;
                shortBearishSeen = false;
                shortFirstTouchBar = -1;
                shortTouchAnchorBar = -1;
            }
            else if (shortTouchAnchorBar != shortAnchorBar)
            {
                shortTouchSeen = false;
                shortCloseBackSeen = false;
                shortBearishSeen = false;
                shortFirstTouchBar = -1;
                shortTouchAnchorBar = shortAnchorBar;
            }

            double touchTol = TouchToleranceTicks * TickSize;
            bool longTouch = longAnchorUsable && !double.IsNaN(longAnchor) && Low[0] <= longAnchor + touchTol;
            bool shortTouch = shortAnchorUsable && !double.IsNaN(shortAnchor) && High[0] >= shortAnchor - touchTol;

            if (longTouch)
            {
                if (!longTouchSeen)
                    longFirstTouchBar = CurrentBar;
                longTouchSeen = true;
            }
            if (shortTouch)
            {
                if (!shortTouchSeen)
                    shortFirstTouchBar = CurrentBar;
                shortTouchSeen = true;
            }

            if (longTouchSeen && longAnchorUsable && !double.IsNaN(longAnchor))
            {
                if (Close[0] > longAnchor)
                    longCloseBackSeen = true;
                if (Close[0] > Open[0])
                    longBullishSeen = true;
            }

            if (shortTouchSeen && shortAnchorUsable && !double.IsNaN(shortAnchor))
            {
                if (Close[0] < shortAnchor)
                    shortCloseBackSeen = true;
                if (Close[0] < Open[0])
                    shortBearishSeen = true;
            }

            if (pendingBreakoutLong && CurrentBar > pendingBreakoutLongSetBar)
            {
                if (High[0] > pendingBreakoutLongTrigger && CanEnterForAnchor(pendingBreakoutLongAnchorBar, true, pendingBreakoutLongAnchorPrice, shortAnchor))
                    SubmitDirectionalEntry(true, pendingBreakoutLongAnchorPrice, pendingBreakoutLongAnchorBar);
                pendingBreakoutLong = false;
            }

            if (pendingBreakoutShort && CurrentBar > pendingBreakoutShortSetBar)
            {
                if (Low[0] < pendingBreakoutShortTrigger && CanEnterForAnchor(pendingBreakoutShortAnchorBar, false, pendingBreakoutShortAnchorPrice, longAnchor))
                    SubmitDirectionalEntry(false, pendingBreakoutShortAnchorPrice, pendingBreakoutShortAnchorBar);
                pendingBreakoutShort = false;
            }

            int longTradeCount = GetAnchorTradeCount(longAnchorBar, true);
            int shortTradeCount = GetAnchorTradeCount(shortAnchorBar, false);
            bool longRetestSatisfied = longTradeCount == 0 || (longTouchSeen && longFirstTouchBar >= 0);
            bool shortRetestSatisfied = shortTradeCount == 0 || (shortTouchSeen && shortFirstTouchBar >= 0);

            bool longConfirm = longTouchSeen && longCloseBackSeen && longBullishSeen && longRetestSatisfied && MajorityApproachFromAbove(longAnchor);
            bool shortConfirm = shortTouchSeen && shortCloseBackSeen && shortBearishSeen && shortRetestSatisfied && MajorityApproachFromBelow(shortAnchor);

            if (longConfirm)
            {
                pendingBreakoutLong = true;
                pendingBreakoutLongSetBar = CurrentBar;
                pendingBreakoutLongTrigger = High[0];
                pendingBreakoutLongAnchorBar = longAnchorBar;
                pendingBreakoutLongAnchorPrice = longAnchor;
                longTouchSeen = false;
                longCloseBackSeen = false;
                longBullishSeen = false;
                longFirstTouchBar = -1;
            }

            if (shortConfirm)
            {
                pendingBreakoutShort = true;
                pendingBreakoutShortSetBar = CurrentBar;
                pendingBreakoutShortTrigger = Low[0];
                pendingBreakoutShortAnchorBar = shortAnchorBar;
                pendingBreakoutShortAnchorPrice = shortAnchor;
                shortTouchSeen = false;
                shortCloseBackSeen = false;
                shortBearishSeen = false;
                shortFirstTouchBar = -1;
            }
        }

        private int GetAnchorTradeCount(int anchorBar, bool isLong)
        {
            if (anchorBar < 0)
                return 0;

            string key = (isLong ? "L:" : "S:") + anchorBar;
            return anchorTradesToday.TryGetValue(key, out int count) ? count : 0;
        }

        private bool CanEnterForAnchor(int anchorBar, bool isLong, double ownAnchor, double oppositeAnchor)
        {
            if (anchorBar < 0)
                return false;

            string key = (isLong ? "L:" : "S:") + anchorBar;
            if (anchorTradesToday.TryGetValue(key, out int count) && count >= 2)
                return false;

            if (!double.IsNaN(oppositeAnchor) && Math.Abs(ownAnchor - oppositeAnchor) <= AnchorProximityAtrMultiple * atr[0])
                return false;

            return true;
        }

        private void SubmitDirectionalEntry(bool isLong, double anchorPrice, int anchorBar)
        {
            int quantity = DefaultQuantity;
            int stopTicks = ComputeSwingStopTicks(isLong);
            bool stopCompressed;
            if (!TryApplyRiskCap(ref quantity, ref stopTicks, out stopCompressed))
                return;

            double dynamicTargetR = atr[0] >= 6.0 ? 3.0 : 2.0;
            int targetTicks = Math.Max(stopTicks + 1, (int)Math.Round(stopTicks * dynamicTargetR));
            SubmitEntry(isLong, quantity, stopTicks, targetTicks, false);

            string key = (isLong ? "L:" : "S:") + anchorBar;
            anchorTradesToday[key] = anchorTradesToday.TryGetValue(key, out int c) ? c + 1 : 1;
        }

        private int ComputeSwingStopTicks(bool isLong)
        {
            int lookback = Math.Min(ApproachLookbackBars + 2, CurrentBar);
            double stopPrice = isLong ? Low[0] : High[0];
            for (int i = 1; i <= lookback; i++)
                stopPrice = isLong ? Math.Min(stopPrice, Low[i]) : Math.Max(stopPrice, High[i]);

            double distPoints = isLong ? (Close[0] - stopPrice) : (stopPrice - Close[0]);
            distPoints = Math.Max(TickSize, Math.Min(MaxStopPoints, distPoints));
            return Math.Max(MinStopTicks, (int)Math.Ceiling(distPoints / TickSize));
        }

        private bool MajorityApproachFromAbove(double anchor)
        {
            int n = Math.Min(ApproachLookbackBars, CurrentBar);
            if (n <= 0) return false;
            int above = 0;
            for (int i = 1; i <= n; i++) if (Close[i] > anchor) above++;
            return above >= (n / 2 + 1);
        }

        private bool MajorityApproachFromBelow(double anchor)
        {
            int n = Math.Min(ApproachLookbackBars, CurrentBar);
            if (n <= 0) return false;
            int below = 0;
            for (int i = 1; i <= n; i++) if (Close[i] < anchor) below++;
            return below >= (n / 2 + 1);
        }

        private string ComputeBiasAtAnchorText(double longAnchor, double shortAnchor)
        {
            if (!double.IsNaN(longAnchor) && MajorityApproachFromAbove(longAnchor))
                return "Above→Support(Long bias)";
            if (!double.IsNaN(shortAnchor) && MajorityApproachFromBelow(shortAnchor))
                return "Below→Resistance(Short bias)";
            return "Neutral";
        }


        #region Parameters

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "ATR Period", GroupName = "Indicators", Order = 1)]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "ATR Stop Multiple", GroupName = "Risk", Order = 2)]
        public double AtrStopMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 5.0)]
        [Display(Name = "Target R Multiple", GroupName = "Risk", Order = 3)]
        public double TargetRMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Min Stop Ticks", GroupName = "Risk", Order = 4)]
        public int MinStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Anchor Zone Ticks", GroupName = "Anchors", Order = 5)]
        public int AnchorZoneTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max Opportunities Per Day", GroupName = "Risk", Order = 6)]
        public int MaxOpportunitiesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max Consecutive Losses", GroupName = "Risk", Order = 7)]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Range(-10.0, -0.1)]
        [Display(Name = "Daily Stop R", GroupName = "Risk", Order = 8)]
        public double DailyStopR { get; set; }

        [NinjaScriptProperty]
        [Range(50.0, 5000.0)]
        [Display(Name = "Max Risk Per Trade ($)", GroupName = "Risk", Order = 9)]
        public double MaxRiskPerTradeDollars { get; set; }

        [NinjaScriptProperty]
        [Range(10, 120)]
        [Display(Name = "Structure Lookback Bars", GroupName = "Anchors", Order = 10)]
        public int StructureLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(4, 20)]
        [Display(Name = "Impulse Bars", GroupName = "Anchors", Order = 11)]
        public int ImpulseBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 4.0)]
        [Display(Name = "Structure Displacement ATR", GroupName = "Anchors", Order = 12)]
        public double StructureDisplacementAtr { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 3.0)]
        [Display(Name = "Structure Volume Multiple", GroupName = "Anchors", Order = 13)]
        public double StructureVolumeMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(4, 30)]
        [Display(Name = "Chop Lookback Bars", GroupName = "Anchors", Order = 14)]
        public int ChopLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 15)]
        [Display(Name = "Chop Flip Threshold", GroupName = "Anchors", Order = 15)]
        public int ChopFlipThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 2.0)]
        [Display(Name = "Structure Score Margin", GroupName = "Anchors", Order = 16)]
        public double StructureScoreMargin { get; set; }

        [NinjaScriptProperty]
        [Range(5, 60)]
        [Display(Name = "Override Cooldown Bars", GroupName = "Anchors", Order = 17)]
        public int OverrideCooldownBars { get; set; }

        [NinjaScriptProperty]
        [Range(2.0, 20.0)]
        [Display(Name = "Gap Threshold Points", GroupName = "Session", Order = 18)]
        public double GapThresholdPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Trade Time Windows", GroupName = "Session", Order = 19)]
        public bool UseTradeTimeWindows { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Extended Hours", GroupName = "Session", Order = 19)]
        public bool UseExtendedHours { get; set; }

        [NinjaScriptProperty]
        [Range(5, 40)]
        [Display(Name = "ADX Chop Threshold", GroupName = "Regime", Order = 20)]
        public int AdxChopThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(2.0, 40.0)]
        [Display(Name = "Extreme ATR Threshold", GroupName = "Regime", Order = 21)]
        public double ExtremeAtrThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Min ATR For Entry", GroupName = "Regime", Order = 22)]
        public double MinAtrForEntry { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Trend Slope Bars", GroupName = "Regime", Order = 23)]
        public int TrendSlopeBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Reclaim Lookback Bars", GroupName = "Entry", Order = 24)]
        public int ReclaimLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(2, 20)]
        [Display(Name = "Approach Lookback Bars", GroupName = "Entry", Order = 24)]
        public int ApproachLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Signal Cooldown Bars", GroupName = "Entry", Order = 24)]
        public int SignalCooldownBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 8)]
        [Display(Name = "Touch Tolerance Ticks", GroupName = "Entry", Order = 24)]
        public int TouchToleranceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10.0)]
        [Display(Name = "Max Stop Points", GroupName = "Risk", Order = 24)]
        public double MaxStopPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "Opposite Anchor Proximity ATR", GroupName = "Entry", Order = 24)]
        public double AnchorProximityAtrMultiple { get; set; }


        [NinjaScriptProperty]
        [Range(1.0, 4.0)]
        [Display(Name = "Defended Low Impulse ATR", GroupName = "Anchors", Order = 25)]
        public double DefendedLowImpulseAtr { get; set; }

        [NinjaScriptProperty]
        [Range(3, 20)]
        [Display(Name = "Defended Low Max Bars", GroupName = "Anchors", Order = 26)]
        public int DefendedLowMaxBars { get; set; }

        [NinjaScriptProperty]
        [Range(5, 20)]
        [Display(Name = "Rolling Expectancy Trades", GroupName = "Risk", Order = 27)]
        public int RollingExpectancyTrades { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Invalidation Follow-Through Bars", GroupName = "Anchors", Order = 28)]
        public int InvalidationFollowThroughBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Min Anchor Age Bars", GroupName = "Entry", Order = 29)]
        public int MinAnchorAgeBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Anchor Logging", GroupName = "Diagnostics", Order = 30)]
        public bool EnableAnchorLogging { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Anchor Status On Chart", GroupName = "Diagnostics", Order = 31)]
        public bool ShowAnchorStatusOnChart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Risk Cap Stop Compression", GroupName = "Risk", Order = 32)]
        public bool AllowRiskCapStopCompression { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Session ATR For Stops", GroupName = "Risk", Order = 33)]
        public bool UseSessionAtrForStops { get; set; }

        [NinjaScriptProperty]
        [Range(0.00, 0.90)]
        [Display(Name = "Max Stop Compression Fraction", GroupName = "Risk", Order = 34)]
        public double MaxStopCompressionFraction { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Min Override Active Bars", GroupName = "Anchors", Order = 35)]
        public int MinOverrideActiveBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable WTD Anchor (Sun 17:00 CT)", GroupName = "Anchors", Order = 36)]
        public bool EnableWtdAnchor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Impulse Origin Anchors", GroupName = "Anchors", Order = 37)]
        public bool EnableImpulseOriginAnchors { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Manual AVWAP2 Anchors", GroupName = "Anchors", Order = 38)]
        public bool UseManualAvwap2Anchors { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Manual Anchor Hotkeys (Q/A/C)", GroupName = "Anchors", Order = 39)]
        public bool EnableManualAnchorHotkeys { get; set; }

        [Browsable(false)]
        public DateTime ManualLongAnchorFrom { get; set; }

        [Browsable(false)]
        public DateTime ManualShortAnchorFrom { get; set; }

        #endregion
    }
}

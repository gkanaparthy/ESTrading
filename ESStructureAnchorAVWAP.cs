using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
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
            StructuralBear
        }

        private enum LodTier
        {
            TierA,
            TierB
        }

        private const int CmeMorningWindowStart = 84500;
        private const int CmeMorningWindowEnd = 100000;
        private const int CmeAfternoonWindowStart = 120000;
        private const int CmeAfternoonWindowEnd = 150000;
        private const int CmeAnchorCutoffTime = 140000;
        private const string AnchorStatusDrawTag = "ESStructureAnchorAVWAP.AnchorStatus";
        private const string LongAnchorMarkerTag = "ESStructureAnchorAVWAP.LongAnchorMarker";
        private const string LongAnchorLabelTag = "ESStructureAnchorAVWAP.LongAnchorLabel";
        private const string ShortAnchorMarkerTag = "ESStructureAnchorAVWAP.ShortAnchorMarker";
        private const string ShortAnchorLabelTag = "ESStructureAnchorAVWAP.ShortAnchorLabel";

        private ATR atr;
        private EMA ema;
        private SMA volSma;
        private ADX adx;
        private PriorDayOHLC priorDay;
        private TimeZoneInfo cmeTimeZone;
        private TimeZoneInfo barTimeZone;

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

                MaxOpportunitiesPerDay = 6;
                MaxConsecutiveLosses = 2;
                DailyStopR = -2.0;
                MaxRiskPerTradeDollars = 400.0;
                AllowRiskCapStopCompression = true;
                UseSessionAtrForStops = true;

                StructureLookbackBars = 40;
                ImpulseBars = 8;
                StructureDisplacementAtr = 1.5;
                StructureVolumeMultiple = 1.2;
                ChopLookbackBars = 8;
                ChopFlipThreshold = 4;
                StructureScoreMargin = 1.2;
                OverrideCooldownBars = 20;
                GapThresholdPoints = 8.0;

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
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(AtrPeriod);
                ema = EMA(20);
                volSma = SMA(Volume, 20);
                adx = ADX(14);
                priorDay = PriorDayOHLC();
                InitializeTimeZones();

                dayHigh = double.MinValue;
                dayLow = double.MaxValue;
                dayHighBarIndex = -1;
                dayLowBarIndex = -1;
                structuralOverrideBarIndex = -1;

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
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            ResetDailyStateIfNeeded();
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

            if (structuralOverrideActive && IsAnchorDegraded(GetStructuralAvwapOrPrice()))
            {
                if (EnableAnchorLogging)
                {
                    PrintWithContext("ANCHOR_OVERRIDE_DEACTIVATED timeCME=" + FormatCmeTime(nowCme) +
                          " kind=" + structuralOverrideKind +
                          " bar=" + structuralOverrideBarIndex);
                }

                structuralOverrideActive = false;
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

            if (!inTradeWindow || !CanSubmitNewTrade())
                return;

            if (double.IsNaN(atr[0]) || double.IsNaN(ema[0]) || double.IsNaN(adx[0]) || double.IsNaN(volSma[0]))
                return;

            if (atr[0] < MinAtrForEntry || atr[0] > ExtremeAtrThreshold)
                return;

            bool bullishTrend = ema[0] > ema[Math.Min(TrendSlopeBars, CurrentBar)] && Close[0] > ema[0];
            bool bearishTrend = ema[0] < ema[Math.Min(TrendSlopeBars, CurrentBar)] && Close[0] < ema[0];
            if (!bullishTrend && !bearishTrend)
                return;

            double zone = AnchorZoneTicks * TickSize;
            double stopAtr = GetStopAtrValue();
            double rawStop = Math.Max(AtrStopMultiple * stopAtr, MinStopTicks * TickSize);
            int baseStopTicks = Math.Max(MinStopTicks, (int)Math.Ceiling(rawStop / TickSize));

            if (adx[0] < AdxChopThreshold && (longAnchorChoppy || shortAnchorChoppy))
                return;

            bool entryVolumeConfirmed = Volume[0] >= volSma[0];

            bool longSignal = longAnchorUsable &&
                              HasReclaimAbove(longAnchor, ReclaimLookbackBars) &&
                              Low[0] <= longAnchor + zone &&
                              entryVolumeConfirmed &&
                              Close[0] > Open[0];

            bool shortAnchorSignalEligible =
                shortKind == AnchorKind.HOD || shortKind == AnchorKind.StructuralBear;

            bool shortSignal = shortAnchorSignalEligible &&
                               shortAnchorUsable &&
                               HasRejectBelow(shortAnchor, ReclaimLookbackBars) &&
                               High[0] >= shortAnchor - zone &&
                               entryVolumeConfirmed &&
                               Close[0] < Open[0];

            if (longSignal && shortSignal)
                return;

            if (longSignal && bullishTrend)
            {
                int quantity = DefaultQuantity;
                bool isTierB = false;
                if (longKind == AnchorKind.LOD)
                {
                    LodTier tier = DetermineLodTier(longAnchorBar);
                    if (tier == LodTier.TierB)
                    {
                        isTierB = true;
                        if (tierBAttemptUsed || DefaultQuantity < 2)
                            return;

                        quantity = Math.Max(1, (int)Math.Floor(DefaultQuantity * 0.5));
                    }
                }

                int stopTicks = baseStopTicks;
                int originalStopTicks = stopTicks;
                bool stopCompressed = false;
                if (!TryApplyRiskCap(ref quantity, ref stopTicks, out stopCompressed))
                {
                    PrintWithContext("SKIP_RISK_CAP side=LONG stopTicks=" + stopTicks +
                          " baseStopTicks=" + baseStopTicks +
                          " rawStopPoints=" + rawStop.ToString("F2") +
                          " atr=" + atr[0].ToString("F2") +
                          " stopAtr=" + stopAtr.ToString("F2") +
                          " quantity=" + quantity +
                          " maxRisk=" + MaxRiskPerTradeDollars.ToString("F0"));
                    return;
                }

                if (stopCompressed && EnableAnchorLogging)
                {
                    PrintWithContext("RISK_CAP_STOP_COMPRESSED side=LONG fromTicks=" + originalStopTicks +
                          " toTicks=" + stopTicks +
                          " rawStopPoints=" + rawStop.ToString("F2") +
                          " atr=" + atr[0].ToString("F2") +
                          " stopAtr=" + stopAtr.ToString("F2") +
                          " quantity=" + quantity +
                          " maxRisk=" + MaxRiskPerTradeDollars.ToString("F0"));
                }

                int targetTicks = Math.Max(stopTicks + 1, (int)Math.Round(stopTicks * TargetRMultiple));
                if (EnableAnchorLogging)
                {
                    PrintWithContext("ENTRY_SIGNAL side=LONG anchorKind=" + longKind +
                          " anchor=" + longAnchor.ToString("F2") +
                          " anchorBar=" + longAnchorBar +
                          " atr=" + atr[0].ToString("F2") +
                          " stopAtr=" + stopAtr.ToString("F2") +
                          " rawStopPoints=" + rawStop.ToString("F2") +
                          " stopTicks=" + stopTicks +
                          " targetTicks=" + targetTicks +
                          " quantity=" + quantity +
                          " tierB=" + isTierB);
                }

                SubmitEntry(true, quantity, stopTicks, targetTicks, isTierB);
            }
            else if (shortSignal && bearishTrend)
            {
                int quantity = DefaultQuantity;
                bool isTierB = false;
                if (shortKind == AnchorKind.HOD)
                {
                    LodTier tier = DetermineHodTier(shortAnchorBar);
                    if (tier == LodTier.TierB)
                    {
                        isTierB = true;
                        if (tierBAttemptUsed || DefaultQuantity < 2)
                            return;

                        quantity = Math.Max(1, (int)Math.Floor(DefaultQuantity * 0.5));
                    }
                }

                int stopTicks = baseStopTicks;
                int originalStopTicks = stopTicks;
                bool stopCompressed = false;
                if (!TryApplyRiskCap(ref quantity, ref stopTicks, out stopCompressed))
                {
                    PrintWithContext("SKIP_RISK_CAP side=SHORT stopTicks=" + stopTicks +
                          " baseStopTicks=" + baseStopTicks +
                          " rawStopPoints=" + rawStop.ToString("F2") +
                          " atr=" + atr[0].ToString("F2") +
                          " stopAtr=" + stopAtr.ToString("F2") +
                          " quantity=" + quantity +
                          " maxRisk=" + MaxRiskPerTradeDollars.ToString("F0"));
                    return;
                }

                if (stopCompressed && EnableAnchorLogging)
                {
                    PrintWithContext("RISK_CAP_STOP_COMPRESSED side=SHORT fromTicks=" + originalStopTicks +
                          " toTicks=" + stopTicks +
                          " rawStopPoints=" + rawStop.ToString("F2") +
                          " atr=" + atr[0].ToString("F2") +
                          " stopAtr=" + stopAtr.ToString("F2") +
                          " quantity=" + quantity +
                          " maxRisk=" + MaxRiskPerTradeDollars.ToString("F0"));
                }

                int targetTicks = Math.Max(stopTicks + 1, (int)Math.Round(stopTicks * TargetRMultiple));
                if (EnableAnchorLogging)
                {
                    PrintWithContext("ENTRY_SIGNAL side=SHORT anchorKind=" + shortKind +
                          " anchor=" + shortAnchor.ToString("F2") +
                          " anchorBar=" + shortAnchorBar +
                          " atr=" + atr[0].ToString("F2") +
                          " stopAtr=" + stopAtr.ToString("F2") +
                          " rawStopPoints=" + rawStop.ToString("F2") +
                          " stopTicks=" + stopTicks +
                          " targetTicks=" + targetTicks +
                          " quantity=" + quantity +
                          " tierB=" + isTierB);
                }

                SubmitEntry(false, quantity, stopTicks, targetTicks, isTierB);
            }
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
            overrideCooldownRemaining = 0;

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
            bool morning = time >= CmeMorningWindowStart && time <= CmeMorningWindowEnd;
            bool afternoon = time >= CmeAfternoonWindowStart && time <= CmeAfternoonWindowEnd;
            return morning || afternoon;
        }

        private bool ShouldSuppressGapDayInvalidation(int nowCme)
        {
            return isGapDay && nowCme < CmeMorningWindowStart;
        }

        private bool CanSubmitNewTrade()
        {
            if (opportunitiesToday >= MaxOpportunitiesPerDay)
                return false;

            if (consecutiveLosses >= MaxConsecutiveLosses)
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

        private double GetLongAnchor(out AnchorKind kind, out int anchorBarIndex)
        {
            if (structuralOverrideActive && structuralOverrideKind == AnchorKind.StructuralBull)
            {
                kind = AnchorKind.StructuralBull;
                anchorBarIndex = structuralOverrideBarIndex;
                return GetAvwapFromBar(structuralOverrideBarIndex, structuralOverridePrice);
            }

            if (lodInvalidated)
            {
                kind = AnchorKind.LOD;
                anchorBarIndex = -1;
                return double.NaN;
            }

            kind = AnchorKind.LOD;
            anchorBarIndex = dayLowBarIndex;
            return GetAvwapFromBar(dayLowBarIndex, dayLow);
        }

        private double GetShortAnchor(out AnchorKind kind, out int anchorBarIndex)
        {
            if (structuralOverrideActive && structuralOverrideKind == AnchorKind.StructuralBear)
            {
                kind = AnchorKind.StructuralBear;
                anchorBarIndex = structuralOverrideBarIndex;
                return GetAvwapFromBar(structuralOverrideBarIndex, structuralOverridePrice);
            }

            if (hodInvalidated)
            {
                kind = AnchorKind.HOD;
                anchorBarIndex = -1;
                return double.NaN;
            }

            kind = AnchorKind.HOD;
            anchorBarIndex = dayHighBarIndex;
            return GetAvwapFromBar(dayHighBarIndex, dayHigh);
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
                double volume = Volume[i];
                if (volume <= 0)
                    continue;

                double typical = (High[i] + Low[i] + Close[i]) / 3.0;
                pv += typical * volume;
                vSum += volume;
            }

            return vSum > 0 ? pv / vSum : fallbackPrice;
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
                    SetStopLoss(activeSignal, CalculationMode.Price, entry, false);
                    breakevenMoved = true;
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
                    SetStopLoss(activeSignal, CalculationMode.Price, entry, false);
                    breakevenMoved = true;
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
                int end = i - ImpulseBars + 1;
                if (end < 0)
                    continue;

                double atrRef = Math.Max(TickSize, atr[i]);
                double netMove = bullish ? (Close[end] - Close[i]) : (Close[i] - Close[end]);
                if (netMove < StructureDisplacementAtr * atrRef)
                    continue;

                int directionalBars = 0;
                double volSum = 0;
                for (int j = i; j >= end; j--)
                {
                    if (bullish && Close[j] > Open[j])
                        directionalBars++;
                    if (!bullish && Close[j] < Open[j])
                        directionalBars++;
                    volSum += Volume[j];
                }

                if (directionalBars < (int)Math.Ceiling(0.7 * ImpulseBars))
                    continue;

                double avgVol = volSum / ImpulseBars;
                double baselineVol = volSma[i];
                if (double.IsNaN(baselineVol) || baselineVol <= 0)
                    continue;

                if (avgVol < StructureVolumeMultiple * baselineVol)
                    continue;

                double candidate = bullish ? Low[i] : High[i];
                int candidateIdx = CurrentBar - i;
                double candidateAvwap = GetAvwapFromBar(candidateIdx, candidate);
                double candidateScore = (netMove / atrRef) + EvaluateAnchorScore(candidateAvwap);

                if (candidateScore <= score)
                    continue;

                anchorPrice = candidate;
                anchorBarIndex = candidateIdx;
                score = candidateScore;
            }

            return score > 0 && anchorBarIndex >= 0;
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
            string stateKey = longKind + "|" + longAnchorBar + "|" + longAnchorUsable + "|" + longAnchorChoppy +
                              "|" + shortKind + "|" + shortAnchorBar + "|" + shortAnchorUsable + "|" + shortAnchorChoppy +
                              "|" + structuralOverrideActive + "|" + structuralOverrideKind + "|" + structuralOverrideBarIndex +
                              "|" + lodInvalidated + "|" + hodInvalidated;

            if (EnableAnchorLogging && !string.Equals(lastAnchorStateKey, stateKey, StringComparison.Ordinal))
            {
                PrintWithContext("ANCHOR_STATE timeCME=" + FormatCmeTime(nowCme) +
                      " long=" + longText +
                      " short=" + shortText +
                      " currentRelevant=" + currentRelevantAnchorText +
                      " nextLong=" + nextLongText +
                      " nextShort=" + nextShortText +
                      " override=" + (structuralOverrideActive
                          ? structuralOverrideKind + " bar=" + structuralOverrideBarIndex
                          : "None") +
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
                return;
            }

            string chartText =
                "ESStructureAnchorAVWAP\n" +
                "CME " + FormatCmeTime(nowCme) + "\n" +
                "Long: " + longText + "\n" +
                "Short: " + shortText + "\n" +
                "Current Relevant Anchor: " + currentRelevantAnchorText + "\n" +
                "Next Long Anchor: " + nextLongText + "\n" +
                "Next Short Anchor: " + nextShortText + "\n" +
                "Override: " + (structuralOverrideActive
                    ? structuralOverrideKind + " bar=" + structuralOverrideBarIndex
                    : "None") + "\n" +
                "Invalidation L/H: " + lodInvalidated + "/" + hodInvalidated + "\n" +
                "Active/Pending: " +
                (string.IsNullOrEmpty(activeSignal) ? "None" : activeSignal) + "/" +
                (string.IsNullOrEmpty(pendingSignal) ? "None" : pendingSignal);

            Draw.TextFixed(this, AnchorStatusDrawTag, chartText, TextPosition.BottomLeft);
            DrawAnchorOriginMarkers(longKind, longAnchor, longAnchorBar, shortKind, shortAnchor, shortAnchorBar);
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
                return "LONG " + longKind + " from " + GetAnchorOriginCmeText(longAnchorBar) + " @" + longAnchor.ToString("F2");

            if (shortValid && !longValid)
                return "SHORT " + shortKind + " from " + GetAnchorOriginCmeText(shortAnchorBar) + " @" + shortAnchor.ToString("F2");

            double longDistance = Math.Abs(Close[0] - longAnchor);
            double shortDistance = Math.Abs(Close[0] - shortAnchor);
            bool chooseLong = longDistance <= shortDistance;

            return chooseLong
                ? "LONG " + longKind + " from " + GetAnchorOriginCmeText(longAnchorBar) + " @" + longAnchor.ToString("F2")
                : "SHORT " + shortKind + " from " + GetAnchorOriginCmeText(shortAnchorBar) + " @" + shortAnchor.ToString("F2");
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
        [Range(5, 40)]
        [Display(Name = "ADX Chop Threshold", GroupName = "Regime", Order = 19)]
        public int AdxChopThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(2.0, 40.0)]
        [Display(Name = "Extreme ATR Threshold", GroupName = "Regime", Order = 20)]
        public double ExtremeAtrThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Min ATR For Entry", GroupName = "Regime", Order = 21)]
        public double MinAtrForEntry { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Trend Slope Bars", GroupName = "Regime", Order = 22)]
        public int TrendSlopeBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Reclaim Lookback Bars", GroupName = "Entry", Order = 23)]
        public int ReclaimLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 4.0)]
        [Display(Name = "Defended Low Impulse ATR", GroupName = "Anchors", Order = 24)]
        public double DefendedLowImpulseAtr { get; set; }

        [NinjaScriptProperty]
        [Range(3, 20)]
        [Display(Name = "Defended Low Max Bars", GroupName = "Anchors", Order = 25)]
        public int DefendedLowMaxBars { get; set; }

        [NinjaScriptProperty]
        [Range(5, 20)]
        [Display(Name = "Rolling Expectancy Trades", GroupName = "Risk", Order = 26)]
        public int RollingExpectancyTrades { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Invalidation Follow-Through Bars", GroupName = "Anchors", Order = 27)]
        public int InvalidationFollowThroughBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Min Anchor Age Bars", GroupName = "Entry", Order = 28)]
        public int MinAnchorAgeBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Anchor Logging", GroupName = "Diagnostics", Order = 29)]
        public bool EnableAnchorLogging { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Anchor Status On Chart", GroupName = "Diagnostics", Order = 30)]
        public bool ShowAnchorStatusOnChart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Risk Cap Stop Compression", GroupName = "Risk", Order = 31)]
        public bool AllowRiskCapStopCompression { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Session ATR For Stops", GroupName = "Risk", Order = 32)]
        public bool UseSessionAtrForStops { get; set; }

        #endregion
    }
}

//
// ESAVWAPAnchor.cs
// NinjaTrader 8 Strategy — AVWAP Anchor v1.1.1
//
// Platform:
//   NinjaTrader 8
//
// Intended instrument/timeframe:
//   ES futures
//   5-minute candles
//   ETH session template
//
// Strategy summary:
//   1. Detect qualifying Pocket Pivot combinations.
//   2. Use the first bar of the combination as the anchor candidate.
//   3. Qualify the candidate as either Swing or BigBody.
//   4. Wait for price to move at least ArmDistanceATR from the anchor close.
//   5. Wait for price to contact the anchored VWAP.
//   6. Trigger only when the candle closes beyond AVWAP with the correct color.
//   7. Submit the entry for the next bar.
//   8. Use a maximum stop of MaxStopPoints.
//   9. Use 2:1 reward/risk when the stop is below RRThresholdPoints,
//      otherwise use 1:1.
//  10. Two consecutive stop-outs disqualify the anchor for the session.
//
// Display revision:
//   Only the active anchor is displayed.
//   The AVWAP plot is cleared and rebuilt when a newer anchor replaces the old
//   anchor.
//
// Important behavior:
//   - Candidate anchors may form during ETH.
//   - Actual entries are allowed only during RTH entry hours.
//   - No new anchors are accepted after NoNewAnchorHHMMSS.
//   - Wrong-color bars closing beyond AVWAP are ignored.
//   - A rejected fill caused by the actual fill exceeding the stop cap is
//     flattened immediately and excluded from trade and stop-out statistics.
//   - After an ordinary exit, the anchor returns to Armed.
//
#region Using declarations

using System;
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
    public class ESAVWAPAnchor : Strategy
    {
        #region Types

        private enum AnchorState
        {
            Idle,
            Anchored,
            Armed,
            Contact,
            InTrade,
            Dead
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
        // Anchor state
        // ---------------------------------------------------------------------

        private AnchorState state = AnchorState.Idle;

        private int anchorBar = -1;
        private int comboEndBar = -1;
        private int contactBar = -1;

        private double anchorClose;
        private double anchorATR;

        private string anchorTag = string.Empty;

        // ---------------------------------------------------------------------
        // AVWAP state
        // ---------------------------------------------------------------------

        private double cumPV;
        private double cumV;
        private double avwap;

        // Absolute bar index of the oldest bar currently represented in the
        // AVWAP plot. The plot values are stored using barsAgo indexing.
        private int plotStartBar = -1;

        private int anchorSeq;
        private int anchorStopOuts;

        private DateTime lastAnchorKillDate;

        // ---------------------------------------------------------------------
        // Stable drawing tags
        //
        // These tags are deliberately reused so that only the current active
        // anchor drawing remains visible.
        // ---------------------------------------------------------------------

        private const string ActiveAnchorDrawTag =
            "ESAVWAPAnchor.ActiveAnchor";

        private const string ActiveAnchorTextTag =
            "ESAVWAPAnchor.ActiveAnchorText";

        private const string ActiveContactDrawTag =
            "ESAVWAPAnchor.ActiveContact";

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
                    "Anchored-VWAP pullback strategy using Pocket Pivot "
                    + "anchor qualification.";

                Name = "ESAVWAPAnchor";

                Calculate = Calculate.OnBarClose;

                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;

                BarsRequiredToTrade = 60;

                IsInstantiatedOnEachOptimizationIteration = false;

                // This is required because the strategy clears historical
                // plot values when a new anchor replaces the old anchor.
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;

                // -----------------------------------------------------------------
                // Pocket Pivot
                // -----------------------------------------------------------------

                PocketPivotLookback = 10;
                VolumeAverageLength = 50;

                // -----------------------------------------------------------------
                // Anchor qualification
                // -----------------------------------------------------------------

                ATRPeriod = 14;
                SwingLookbackBars = 8;
                SwingToleranceATR = 0.5;
                SwingLegATR = 1.0;

                BigBodyATR = 1.5;
                BigBodyRangePct = 0.70;

                EnableContinuationFilter = false;
                PreComboThrustBars = 3;
                PreComboThrustATR = 1.0;

                // -----------------------------------------------------------------
                // Arming and contact
                // -----------------------------------------------------------------

                ArmDistanceATR = 2.0;
                ArmMinBars = 3;
                TriggerWindowBars = 2;
                MaxStopOutsPerAnchor = 2;

                // -----------------------------------------------------------------
                // Trade management
                // -----------------------------------------------------------------

                MaxStopPoints = 7.0;
                RRThresholdPoints = 5.0;

                HighRR = 2.0;
                LowRR = 1.0;

                ContractQty = 1;

                EnableStopHalving = false;
                CooldownBars = 1;

                // -----------------------------------------------------------------
                // Session
                // -----------------------------------------------------------------

                RTHStartHHMMSS = 083000;
                RTHEndHHMMSS = 150000;
                LastEntryHHMMSS = 143000;
                NoNewAnchorHHMMSS = 143000;

                FlattenAtRTHEnd = true;

                // -----------------------------------------------------------------
                // Risk controls
                // -----------------------------------------------------------------

                MaxTradesPerDay = 6;
                DailyLossLimit = 500.0;
                DailyProfitLimit = 0.0;

                // -----------------------------------------------------------------
                // Diagnostics
                // -----------------------------------------------------------------

                EnableTradeLog = true;
                DrawAnchors = true;

                AddPlot(
                    new Stroke(Brushes.Gold, 2),
                    PlotStyle.Line,
                    "AVWAP");
            }
            else if (State == State.DataLoaded)
            {
                isBullPP = new Series<bool>(
                    this,
                    MaximumBarsLookBack.Infinite);

                isBearPP = new Series<bool>(
                    this,
                    MaximumBarsLookBack.Infinite);

                isGreenBar = new Series<bool>(
                    this,
                    MaximumBarsLookBack.Infinite);

                isRedBar = new Series<bool>(
                    this,
                    MaximumBarsLookBack.Infinite);

                atr14 = ATR(ATRPeriod);

                volSma = SMA(
                    Volume,
                    VolumeAverageLength);

                state = AnchorState.Idle;

                lastExitBar = -1;
                wasInPosition = false;
                fillRejected = false;

                lastResetDate = Core.Globals.MinDate;
                lastAnchorKillDate = Core.Globals.MinDate;

                plotStartBar = -1;

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
                            + "ExitName,AnchorATR");

                        tradeLog.Flush();
                    }
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
            int warmup =
                Math.Max(
                    Math.Max(
                        PocketPivotLookback,
                        VolumeAverageLength),
                    ATRPeriod)
                + SwingLookbackBars
                + PreComboThrustBars
                + 4;

            if (CurrentBar < warmup)
            {
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
                        ExitLong(
                            "RTHClose",
                            currentSignalName);
                    }
                    else if (Position.MarketPosition == MarketPosition.Short)
                    {
                        ExitShort(
                            "RTHClose",
                            currentSignalName);
                    }

                    Print(
                        string.Format(
                            "{0:yyyy-MM-dd HH:mm} RTH END "
                            + "| flattening position",
                            Time[0]));
                }

                if (state != AnchorState.Idle)
                {
                    Print(
                        string.Format(
                            "{0:yyyy-MM-dd HH:mm} RTH END "
                            + "| anchor #{1} killed",
                            Time[0],
                            anchorSeq));

                    ResetAnchor(AnchorState.Idle);
                }
            }

            // -----------------------------------------------------------------
            // Update active AVWAP
            //
            // A Dead anchor is not updated. Its historical AVWAP line is
            // allowed to end at the bar on which the anchor became dead.
            // -----------------------------------------------------------------

            if (IsLiveAnchorState())
            {
                double typicalPrice =
                    (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;

                cumPV += typicalPrice * Volume[0];
                cumV += Volume[0];

                avwap =
                    cumV > 0.0
                        ? cumPV / cumV
                        : Close[0];

                Values[0][0] = avwap;
            }
            else
            {
                Values[0][0] = double.NaN;
            }

            // -----------------------------------------------------------------
            // Manage an open trade
            // -----------------------------------------------------------------

            if (EnableStopHalving)
            {
                ManageStopHalving();
            }

            // -----------------------------------------------------------------
            // Detect newer candidates.
            //
            // A candidate can replace the active anchor unless a trade is
            // currently active.
            // -----------------------------------------------------------------

            if (state != AnchorState.InTrade)
            {
                TryDetectCandidate();
            }

            // -----------------------------------------------------------------
            // Anchor state machine
            // -----------------------------------------------------------------

            switch (state)
            {
                case AnchorState.Anchored:
                    HandleAnchoredState();
                    break;

                case AnchorState.Armed:
                    HandleArmedState();
                    break;

                case AnchorState.Contact:
                    HandleContactState();
                    break;
            }
        }

        #endregion

        #region OnOrderUpdate

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
            if (order == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(currentSignalName))
            {
                return;
            }

            if (order.Name != currentSignalName)
            {
                return;
            }

            bool entryRejected =
                orderState == OrderState.Rejected;

            bool entryCancelledWithoutFill =
                orderState == OrderState.Cancelled
                && filled == 0;

            if (!entryRejected && !entryCancelledWithoutFill)
            {
                return;
            }

            Print(
                string.Format(
                    "{0:yyyy-MM-dd HH:mm} ENTRY {1} "
                    + "| {2} — anchor back to Armed",
                    time,
                    orderState,
                    error));

            if (state == AnchorState.InTrade)
            {
                state = AnchorState.Armed;
            }

            plannedStopPrice = 0.0;
            currentSignalName = null;
            tradeTag = null;
        }

        #endregion

        #region Anchor State Machine

        private void HandleAnchoredState()
        {
            if (anchorATR <= 0.0)
            {
                return;
            }

            bool minimumBarsElapsed =
                CurrentBar - comboEndBar >= ArmMinBars;

            bool distanceReached =
                Math.Abs(Close[0] - anchorClose)
                >= ArmDistanceATR * anchorATR;

            if (minimumBarsElapsed && distanceReached)
            {
                state = AnchorState.Armed;

                Print(
                    string.Format(
                        "{0:yyyy-MM-dd HH:mm} ARMED #{1} "
                        + "| close {2:F2} is {3:F1} ATR "
                        + "from anchor {4:F2}",
                        Time[0],
                        anchorSeq,
                        Close[0],
                        Math.Abs(Close[0] - anchorClose)
                            / anchorATR,
                        anchorClose));
            }
        }

        private void HandleArmedState()
        {
            if (!IsContactBar())
            {
                return;
            }

            state = AnchorState.Contact;
            contactBar = CurrentBar;

            if (DrawAnchors)
            {
                Draw.Dot(
                    this,
                    ActiveContactDrawTag,
                    false,
                    0,
                    avwap,
                    Brushes.White);
            }

            Print(
                string.Format(
                    "{0:yyyy-MM-dd HH:mm} CONTACT #{1} "
                    + "| AVWAP={2:F2}",
                    Time[0],
                    anchorSeq,
                    avwap));

            // The contact bar may also be the trigger bar.
            TryTrigger();
        }

        private void HandleContactState()
        {
            int barsSinceContact =
                CurrentBar - contactBar;

            if (barsSinceContact >= TriggerWindowBars)
            {
                state = AnchorState.Armed;

                Print(
                    string.Format(
                        "{0:yyyy-MM-dd HH:mm} WINDOW EXPIRED #{1} "
                        + "| back to Armed",
                        Time[0],
                        anchorSeq));

                // Allow a fresh contact to start a fresh trigger window.
                if (IsContactBar())
                {
                    state = AnchorState.Contact;
                    contactBar = CurrentBar;

                    if (DrawAnchors)
                    {
                        Draw.Dot(
                            this,
                            ActiveContactDrawTag,
                            false,
                            0,
                            avwap,
                            Brushes.White);
                    }

                    TryTrigger();
                }
            }
            else
            {
                TryTrigger();
            }
        }

        private bool IsLiveAnchorState()
        {
            return state == AnchorState.Anchored
                || state == AnchorState.Armed
                || state == AnchorState.Contact
                || state == AnchorState.InTrade;
        }

        #endregion

        #region Candidate Detection

        private void TryDetectCandidate()
        {
            int comboLen = 0;

            // Two consecutive bullish Pocket Pivot bars.
            if (isBullPP[0] && isBullPP[1])
            {
                comboLen = 2;
            }
            // Two consecutive bearish Pocket Pivot bars.
            else if (isBearPP[0] && isBearPP[1])
            {
                comboLen = 2;
            }
            // Bullish PP, green volume bar, bullish PP.
            else if (isBullPP[0]
                && isGreenBar[1]
                && isBullPP[2])
            {
                comboLen = 3;
            }
            // Bearish PP, red volume bar, bearish PP.
            else if (isBearPP[0]
                && isRedBar[1]
                && isBearPP[2])
            {
                comboLen = 3;
            }

            if (comboLen == 0)
            {
                return;
            }

            // The first combo bar is the oldest bar in the detected combo.
            int firstOffset = comboLen - 1;

            int candidateBar =
                CurrentBar - firstOffset;

            // Ignore overlapping detections inside the current combo.
            if (state != AnchorState.Idle
                && candidateBar <= comboEndBar)
            {
                return;
            }

            int currentTime = ToTime(Time[0]);

            // No new anchors after the configured cutoff during RTH.
            if (currentTime > NoNewAnchorHHMMSS
                && currentTime < RTHEndHHMMSS)
            {
                return;
            }

            double atrAtCandidate =
                atr14[firstOffset];

            if (atrAtCandidate <= 0.0)
            {
                return;
            }

            string qualificationTag = null;

            if (IsBigBody(
                firstOffset,
                atrAtCandidate))
            {
                qualificationTag = "BigBody";
            }
            else if (IsSwing(
                firstOffset,
                comboLen,
                atrAtCandidate))
            {
                qualificationTag = "Swing";
            }

            if (qualificationTag == null)
            {
                return;
            }

            bool replacing =
                state != AnchorState.Idle;

            // -----------------------------------------------------------------
            // Clear the previous active display before installing the new one.
            // -----------------------------------------------------------------

            ClearActiveAnchorDrawings();
            ClearAvwapPlot();

            ResetAnchor(AnchorState.Anchored);

            anchorSeq++;

            anchorBar = candidateBar;
            comboEndBar = CurrentBar;

            anchorClose = Close[firstOffset];
            anchorATR = atrAtCandidate;
            anchorTag = qualificationTag;

            // Rebuild AVWAP from the new anchor through the current bar.
            RebuildAvwapPlot(firstOffset);

            // -----------------------------------------------------------------
            // Draw only the current active anchor.
            // -----------------------------------------------------------------

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
                    anchorDrawPrice =
                        High[firstOffset] + 2.0 * TickSize;

                    anchorBrush = Brushes.Cyan;
                }

                Draw.Diamond(
                    this,
                    ActiveAnchorDrawTag,
                    false,
                    firstOffset,
                    anchorDrawPrice,
                    anchorBrush);

                Draw.Text(
                    this,
                    ActiveAnchorTextTag,
                    qualificationTag + " #" + anchorSeq,
                    firstOffset,
                    High[firstOffset] + 6.0 * TickSize,
                    Brushes.White);
            }

            Print(
                string.Format(
                    "{0:yyyy-MM-dd HH:mm} ANCHOR #{1} {2} "
                    + "| bar {3:HH:mm} close={4:F2} ATR={5:F2}{6}",
                    Time[0],
                    anchorSeq,
                    qualificationTag,
                    Time[firstOffset],
                    anchorClose,
                    anchorATR,
                    replacing
                        ? " (replaced previous)"
                        : string.Empty));
        }

        private bool IsBigBody(
            int barsAgo,
            double atr)
        {
            double body =
                Math.Abs(
                    Close[barsAgo]
                    - Open[barsAgo]);

            double range =
                High[barsAgo]
                - Low[barsAgo];

            return body >= BigBodyATR * atr
                && range > 0.0
                && body >= BigBodyRangePct * range;
        }

        private bool IsSwing(
            int firstOffset,
            int comboLen,
            double atr)
        {
            double comboHigh = double.MinValue;
            double comboLow = double.MaxValue;

            for (int i = firstOffset; i >= 0; i--)
            {
                comboHigh =
                    Math.Max(
                        comboHigh,
                        High[i]);

                comboLow =
                    Math.Min(
                        comboLow,
                        Low[i]);
            }

            double priorHigh = double.MinValue;
            double priorLow = double.MaxValue;

            int lastPriorOffset =
                firstOffset + SwingLookbackBars;

            for (int i = firstOffset + 1;
                i <= lastPriorOffset;
                i++)
            {
                if (i > CurrentBar)
                {
                    break;
                }

                priorHigh =
                    Math.Max(
                        priorHigh,
                        High[i]);

                priorLow =
                    Math.Min(
                        priorLow,
                        Low[i]);
            }

            bool swingHigh =
                comboHigh >= priorHigh - SwingToleranceATR * atr
                && comboHigh - priorLow >= SwingLegATR * atr;

            bool swingLow =
                comboLow <= priorLow + SwingToleranceATR * atr
                && priorHigh - comboLow >= SwingLegATR * atr;

            // Optional continuation filter. It remains OFF by default.
            if (EnableContinuationFilter)
            {
                int firstPreComboOffset =
                    firstOffset + 1;

                int lastPreComboOffset =
                    firstPreComboOffset + PreComboThrustBars;

                if (lastPreComboOffset <= CurrentBar)
                {
                    double thrust =
                        Close[firstPreComboOffset]
                        - Close[lastPreComboOffset];

                    // Rising movement into a high may indicate continuation.
                    if (swingHigh
                        && thrust >= PreComboThrustATR * atr)
                    {
                        swingHigh = false;
                    }

                    // Falling movement into a low may indicate continuation.
                    if (swingLow
                        && -thrust >= PreComboThrustATR * atr)
                    {
                        swingLow = false;
                    }
                }
            }

            return swingHigh || swingLow;
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
            if (CurrentBar < 0)
            {
                return;
            }

            if (plotStartBar < 0)
            {
                return;
            }

            // plotStartBar is an absolute bar index. Convert it to the
            // maximum barsAgo value that currently contains AVWAP values.
            int maxBarsAgo =
                CurrentBar - plotStartBar;

            maxBarsAgo =
                Math.Min(
                    maxBarsAgo,
                    CurrentBar);

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

            // firstOffset is the number of bars ago containing the first
            // combo bar. Iterating from firstOffset down to zero processes
            // bars chronologically from anchor to current bar.
            for (int i = firstOffset; i >= 0; i--)
            {
                double typicalPrice =
                    (Open[i] + High[i] + Low[i] + Close[i]) / 4.0;

                cumPV += typicalPrice * Volume[i];
                cumV += Volume[i];

                avwap =
                    cumV > 0.0
                        ? cumPV / cumV
                        : Close[i];

                Values[0][i] = avwap;
            }

            // Store the absolute index of the oldest bar represented by the
            // current AVWAP plot.
            plotStartBar =
                CurrentBar - firstOffset;
        }

        #endregion

        #region Contact and Trigger

        private bool IsContactBar()
        {
            bool touchesAvwap =
                Low[0] <= avwap
                && avwap <= High[0];

            bool gapsThroughAvwap =
                (Open[0] - avwap)
                * (Close[1] - avwap) < 0.0;

            return touchesAvwap
                || gapsThroughAvwap;
        }

        private void TryTrigger()
        {
            // Correct-color trigger rules:
            //
            // Long:
            //   close above AVWAP and green bar.
            //
            // Short:
            //   close below AVWAP and red bar.
            //
            // Wrong-color bars closing beyond AVWAP are ignored.
            bool longTrigger =
    Close[0] >= avwap + MinimumTriggerDistancePoints
    && Close[0] > Open[0];

bool shortTrigger =
    Close[0] <= avwap - MinimumTriggerDistancePoints
    && Close[0] < Open[0];

            if (!longTrigger && !shortTrigger)
            {
                return;
            }

            string direction =
                longTrigger
                    ? "LONG"
                    : "SHORT";

            // -----------------------------------------------------------------
            // Entry guards
            //
            // Entry-hours validation intentionally comes first so that the
            // diagnostic log reports the real binding reason when a trigger
            // occurs outside RTH.
            // -----------------------------------------------------------------

            int currentTime = ToTime(Time[0]);

            if (currentTime < RTHStartHHMMSS
                || currentTime > LastEntryHHMMSS)
            {
                Print(
                    string.Format(
                        "{0:yyyy-MM-dd HH:mm} {1} BLOCKED "
                        + "| outside entry hours",
                        Time[0],
                        direction));

                return;
            }

            if (dailyLossHit || dailyProfitHit)
            {
                Print(
                    string.Format(
                        "{0:yyyy-MM-dd HH:mm} {1} BLOCKED "
                        + "| daily limit",
                        Time[0],
                        direction));

                return;
            }

            if (dailyTradeCount >= MaxTradesPerDay)
            {
                Print(
                    string.Format(
                        "{0:yyyy-MM-dd HH:mm} {1} BLOCKED "
                        + "| max trades",
                        Time[0],
                        direction));

                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                return;
            }

            if (CooldownBars > 0
                && lastExitBar >= 0
                && CurrentBar - lastExitBar < CooldownBars)
            {
                Print(
                    string.Format(
                        "{0:yyyy-MM-dd HH:mm} {1} BLOCKED "
                        + "| cooldown",
                        Time[0],
                        direction));

                return;
            }

            // -----------------------------------------------------------------
            // Determine stop using all bars from contact through trigger.
            // -----------------------------------------------------------------

            int barsSinceContact =
                CurrentBar - contactBar;

            double highestPrice = double.MinValue;
            double lowestPrice = double.MaxValue;

            for (int i = 0; i <= barsSinceContact; i++)
            {
                highestPrice =
                    Math.Max(
                        highestPrice,
                        High[i]);

                lowestPrice =
                    Math.Min(
                        lowestPrice,
                        Low[i]);
            }

            plannedStopPrice =
                longTrigger
                    ? lowestPrice - TickSize
                    : highestPrice + TickSize;

            // The next-bar open is unknown while the trigger bar is being
            // processed. Use the trigger close as the conservative proxy.
            double stopProxyPoints =
                Math.Abs(
                    Close[0]
                    - plannedStopPrice);

            if (stopProxyPoints > MaxStopPoints)
            {
                Print(
                    string.Format(
                        "{0:yyyy-MM-dd HH:mm} {1} BLOCKED "
                        + "| SL {2:F2} pts > max {3:F2}",
                        Time[0],
                        direction,
                        stopProxyPoints,
                        MaxStopPoints));

                plannedStopPrice = 0.0;

                return;
            }

            double rewardRisk =
                stopProxyPoints < RRThresholdPoints
                    ? HighRR
                    : LowRR;

            double stopTicks =
                Math.Max(
                    1.0,
                    Math.Round(
                        stopProxyPoints / TickSize));

            double targetTicks =
                Math.Max(
                    1.0,
                    Math.Round(
                        stopTicks * rewardRisk));

            currentSignalName =
                longTrigger
                    ? "AVWAPLong"
                    : "AVWAPShort";

            tradeTag = anchorTag;

            // Initial protective orders are submitted in ticks. They are
            // replaced with exact price orders after the actual fill.
            SetStopLoss(
                currentSignalName,
                CalculationMode.Ticks,
                stopTicks,
                false);

            SetProfitTarget(
                currentSignalName,
                CalculationMode.Ticks,
                targetTicks);

            if (longTrigger)
            {
                EnterLong(
                    ContractQty,
                    currentSignalName);
            }
            else
            {
                EnterShort(
                    ContractQty,
                    currentSignalName);
            }

            // This is provisional. OnOrderUpdate returns the anchor to Armed
            // if the entry is rejected or cancelled without a fill.
            state = AnchorState.InTrade;

            Print(
                string.Format(
                    "{0:yyyy-MM-dd HH:mm} {1} SIGNAL #{2} {3} "
                    + "| stop={4:F2} SL≈{5:F2}pts RR={6:F1}",
                    Time[0],
                    direction,
                    anchorSeq,
                    anchorTag,
                    plannedStopPrice,
                    stopProxyPoints,
                    rewardRisk));
        }

        #endregion

        #region Trade Management

        private void ManageStopHalving()
        {
            if (stopHalved
                || initialStopTicks <= 0.0
                || lastEntryPrice <= 0.0
                || string.IsNullOrEmpty(currentSignalName))
            {
                return;
            }

            MarketPosition marketPosition =
                Position.MarketPosition;

            if (marketPosition == MarketPosition.Flat)
            {
                return;
            }

            double favorableTicks;

            if (marketPosition == MarketPosition.Long)
            {
                favorableTicks =
                    (High[0] - lastEntryPrice)
                    / TickSize;
            }
            else
            {
                favorableTicks =
                    (lastEntryPrice - Low[0])
                    / TickSize;
            }

            if (favorableTicks >= initialStopTicks)
            {
                double halvedStopTicks =
                    Math.Max(
                        1.0,
                        Math.Round(
                            initialStopTicks / 2.0));

                SetStopLoss(
                    currentSignalName,
                    CalculationMode.Ticks,
                    halvedStopTicks,
                    false);

                stopHalved = true;

                Print(
                    string.Format(
                        "{0:yyyy-MM-dd HH:mm} STOP HALVED "
                        + "| {1:F0}t → {2:F0}t",
                        Time[0],
                        initialStopTicks,
                        halvedStopTicks));
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
                favorableMove =
                    High[0] - lastEntryPrice;

                adverseMove =
                    lastEntryPrice - Low[0];
            }
            else
            {
                favorableMove =
                    lastEntryPrice - Low[0];

                adverseMove =
                    High[0] - lastEntryPrice;
            }

            tradeMFE =
                Math.Max(
                    tradeMFE,
                    favorableMove);

            tradeMAE =
                Math.Max(
                    tradeMAE,
                    adverseMove);
        }

        #endregion

        #region OnExecutionUpdate

        protected override void OnExecutionUpdate(
            Execution execution,
            string executionId,
            double price,
            int quantity,
            MarketPosition marketPosition,
            string orderId,
            DateTime time)
        {
            if (execution == null
                || execution.Order == null
                || execution.Order.OrderState != OrderState.Filled)
            {
                return;
            }

            OrderAction action =
                execution.Order.OrderAction;

            // -----------------------------------------------------------------
            // Entry fill
            // -----------------------------------------------------------------

            if (action == OrderAction.Buy
                || action == OrderAction.SellShort)
            {
                bool isLong =
                    action == OrderAction.Buy;

                lastEntryPrice = price;
                lastEntryDirection =
                    isLong
                        ? 1
                        : -1;

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

                double stopPoints;

                if (isLong)
                {
                    stopPoints =
                        price - plannedStopPrice;
                }
                else
                {
                    stopPoints =
                        plannedStopPrice - price;
                }

                stopPoints =
                    Math.Max(
                        TickSize,
                        stopPoints);

                // -----------------------------------------------------------------
                // Actual fill exceeds maximum stop.
                //
                // Flatten immediately. Undo the provisional trade count.
                // Do not count this as a trade or stop-out.
                // -----------------------------------------------------------------

                if (stopPoints > MaxStopPoints)
                {
                    fillRejected = true;

                    dailyTradeCount--;

                    tradeSLPts = stopPoints;
                    tradeTgtPts = 0.0;

                    Print(
                        string.Format(
                            "{0:yyyy-MM-dd HH:mm} FILL REJECTED "
                            + "| SL {1:F2} pts > max {2:F2} "
                            + "— flattening",
                            time,
                            stopPoints,
                            MaxStopPoints));

                    if (isLong)
                    {
                        ExitLong(
                            "MaxStopExceeded",
                            currentSignalName);
                    }
                    else
                    {
                        ExitShort(
                            "MaxStopExceeded",
                            currentSignalName);
                    }

                    return;
                }

                double rewardRisk =
                    stopPoints < RRThresholdPoints
                        ? HighRR
                        : LowRR;

                double targetPoints =
                    stopPoints * rewardRisk;

                double targetPrice;

                if (isLong)
                {
                    targetPrice =
                        price + targetPoints;
                }
                else
                {
                    targetPrice =
                        price - targetPoints;
                }

                SetStopLoss(
                    currentSignalName,
                    CalculationMode.Price,
                    plannedStopPrice,
                    false);

                SetProfitTarget(
                    currentSignalName,
                    CalculationMode.Price,
                    targetPrice);

                initialStopTicks =
                    Math.Round(
                        stopPoints / TickSize);

                stopHalved = false;

                tradeSLPts = stopPoints;
                tradeTgtPts = targetPoints;

                Print(
                    string.Format(
                        "{0:yyyy-MM-dd HH:mm} {1} FILL @ {2:F2} "
                        + "| stop={3:F2} ({4:F2}pts) "
                        + "target={5:F2} ({6:F2}pts, {7:F1}:1)",
                        time,
                        isLong
                            ? "LONG"
                            : "SHORT",
                        price,
                        plannedStopPrice,
                        stopPoints,
                        targetPrice,
                        targetPoints,
                        rewardRisk));

                return;
            }

            // -----------------------------------------------------------------
            // Exit fill
            // -----------------------------------------------------------------

            if (action == OrderAction.Sell
                || action == OrderAction.BuyToCover)
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

                double pointValue =
                    Instrument.MasterInstrument.PointValue;

                double pnl =
                    lastEntryDirection
                    * (price - lastEntryPrice)
                    * quantity
                    * pointValue;

                dailyRealizedPnL += pnl;

                string exitName =
                    execution.Order.Name;

                // -------------------------------------------------------------
                // Rejected-fill handling
                // -------------------------------------------------------------

                if (fillRejected)
                {
                    fillRejected = false;

                    if (state == AnchorState.InTrade)
                    {
                        state = AnchorState.Armed;
                    }

                    Print(
                        string.Format(
                            "{0:yyyy-MM-dd HH:mm} "
                            + "REJECTED-FILL FLATTENED @ {1:F2} "
                            + "| P&L=${2:F2} "
                            + "(not counted as trade/stop-out)",
                            time,
                            price,
                            pnl));
                }
                else
                {
                    bool isStop =
                        exitName == "Stop loss"
                        || (exitName != "Profit target"
                            && pnl < 0.0);

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
                        if (anchorStopOuts
                            >= MaxStopOutsPerAnchor)
                        {
                            state = AnchorState.Dead;

                            Print(
                                string.Format(
                                    "{0:yyyy-MM-dd HH:mm} "
                                    + "ANCHOR #{1} DEAD "
                                    + "| {2} consecutive stop-outs",
                                    time,
                                    anchorSeq,
                                    anchorStopOuts));
                        }
                        else
                        {
                            state = AnchorState.Armed;
                        }
                    }

                    Print(
                        string.Format(
                            "{0:yyyy-MM-dd HH:mm} EXIT @ {1:F2} "
                            + "({2}) | P&L=${3:F2} "
                            + "Daily=${4:F2} "
                            + "MAE={5:F2} MFE={6:F2}",
                            time,
                            price,
                            exitName,
                            pnl,
                            dailyRealizedPnL,
                            tradeMAE,
                            tradeMFE));
                }

                // -------------------------------------------------------------
                // Trade log
                // -------------------------------------------------------------

                if (tradeLog != null)
                {
                    tradeLog.WriteLine(
                        string.Join(
                            ",",
                            tradeEntryTime.ToString(
                                "yyyy-MM-dd HH:mm:ss"),
                            time.ToString(
                                "yyyy-MM-dd HH:mm:ss"),
                            lastEntryDirection > 0
                                ? "Long"
                                : "Short",
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
                            anchorATR.ToString("F2")));

                    tradeLog.Flush();
                }

                // -------------------------------------------------------------
                // Daily limits
                // -------------------------------------------------------------

                if (DailyLossLimit > 0.0
                    && dailyRealizedPnL
                    <= -Math.Abs(DailyLossLimit))
                {
                    dailyLossHit = true;
                }

                if (DailyProfitLimit > 0.0
                    && dailyRealizedPnL
                    >= Math.Abs(DailyProfitLimit))
                {
                    dailyProfitHit = true;
                }

                // -------------------------------------------------------------
                // Clear per-trade state
                // -------------------------------------------------------------

                lastEntryPrice = 0.0;
                lastEntryDirection = 0;

                currentSignalName = null;
                plannedStopPrice = 0.0;

                initialStopTicks = 0.0;
                stopHalved = false;

                return;
            }
        }

        #endregion

        #region Anchor Reset

        private void ResetAnchor(AnchorState newState)
        {
            if (newState == AnchorState.Idle
                || newState == AnchorState.Dead)
            {
                ClearActiveAnchorDrawings();
                ClearAvwapPlot();
            }

            state = newState;

            anchorBar = -1;
            comboEndBar = -1;
            contactBar = -1;

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

            bool isUpBar =
                Close[0] > Close[1];

            bool isDownBar =
                Close[0] < Close[1];

            double maxDownVolume = double.NaN;
            double maxUpVolume = double.NaN;

            int scanBars =
                Math.Min(
                    PocketPivotLookback,
                    CurrentBar - 1);

            for (int i = 1; i <= scanBars; i++)
            {
                bool priorBarWasDown =
                    Close[i] < Close[i + 1];

                bool priorBarWasUp =
                    Close[i] > Close[i + 1];

                if (priorBarWasDown
                    && (double.IsNaN(maxDownVolume)
                        || Volume[i] > maxDownVolume))
                {
                    maxDownVolume = Volume[i];
                }

                if (priorBarWasUp
                    && (double.IsNaN(maxUpVolume)
                        || Volume[i] > maxUpVolume))
                {
                    maxUpVolume = Volume[i];
                }
            }

            isBullPP[0] =
                isUpBar
                && !double.IsNaN(maxDownVolume)
                && Volume[0] > maxDownVolume;

            isBearPP[0] =
                isDownBar
                && !double.IsNaN(maxUpVolume)
                && Volume[0] > maxUpVolume;

            double averageVolume =
                volSma[0];

            bool volumeAboveAverage =
                averageVolume > 0.0
                && Volume[0] > averageVolume;

            isGreenBar[0] =
                isUpBar
                && volumeAboveAverage
                && !isBullPP[0];

            isRedBar[0] =
                isDownBar
                && volumeAboveAverage
                && !isBearPP[0];
        }

        #endregion

        #region Properties

        // ---------------------------------------------------------------------
        // 01 | Pocket Pivot
        // ---------------------------------------------------------------------

        [NinjaScriptProperty]
        [Range(2, 500)]
        [Display(
            Name = "Pocket Pivot Lookback (bars)",
            Order = 1,
            GroupName = "01 | Pocket Pivot")]
        public int PocketPivotLookback
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(2, 500)]
        [Display(
            Name = "Volume Average Length (bars)",
            Order = 2,
            GroupName = "01 | Pocket Pivot")]
        public int VolumeAverageLength
        {
            get;
            set;
        }

        // ---------------------------------------------------------------------
        // 02 | Anchor Qualification
        // ---------------------------------------------------------------------

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(
            Name = "ATR Period",
            Description =
                "ATR frozen at the anchor bar.",
            Order = 1,
            GroupName = "02 | Anchor Qualification")]
        public int ATRPeriod
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(
            Name = "Swing Lookback P (bars)",
            Description =
                "Bars before the combo used for the swing test.",
            Order = 2,
            GroupName = "02 | Anchor Qualification")]
        public int SwingLookbackBars
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(0.0, 3.0)]
        [Display(
            Name = "Swing Tolerance (ATR)",
            Description =
                "How far inside the prior extreme the combo extreme "
                + "may be.",
            Order = 3,
            GroupName = "02 | Anchor Qualification")]
        public double SwingToleranceATR
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(0.0, 5.0)]
        [Display(
            Name = "Swing Leg (ATR)",
            Description =
                "Minimum travel into the pivot over the lookback.",
            Order = 4,
            GroupName = "02 | Anchor Qualification")]
        public double SwingLegATR
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(
            Name = "Big Body (ATR)",
            Description =
                "First combo bar body must be at least this multiple "
                + "of ATR.",
            Order = 5,
            GroupName = "02 | Anchor Qualification")]
        public double BigBodyATR
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(0.1, 1.0)]
        [Display(
            Name = "Big Body Range %",
            Description =
                "Body must be at least this fraction of the bar range.",
            Order = 6,
            GroupName = "02 | Anchor Qualification")]
        public double BigBodyRangePct
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Display(
            Name = "Enable Continuation Filter",
            Description =
                "OFF by default. Rejects swing candidates whose "
                + "preceding bars were already thrusting toward the "
                + "combo extreme.",
            Order = 7,
            GroupName = "02 | Anchor Qualification")]
        public bool EnableContinuationFilter
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(
            Name = "Pre-Combo Thrust Bars",
            Description =
                "Bars before the combo checked for same-direction "
                + "momentum.",
            Order = 8,
            GroupName = "02 | Anchor Qualification")]
        public int PreComboThrustBars
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(0.0, 5.0)]
        [Display(
            Name = "Pre-Combo Thrust (ATR)",
            Description =
                "Prior movement threshold for rejecting continuation.",
            Order = 9,
            GroupName = "02 | Anchor Qualification")]
        public double PreComboThrustATR
        {
            get;
            set;
        }

        // ---------------------------------------------------------------------
        // 03 | Arming and Contact
        // ---------------------------------------------------------------------

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(
            Name = "Arm Distance (ATR)",
            Description =
                "Close must move this many ATR from the anchor close.",
            Order = 1,
            GroupName = "03 | Arming / Contact")]
        public double ArmDistanceATR
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(
            Name = "Arm Min Bars",
            Description =
                "Bars after combo completion before arming is allowed.",
            Order = 2,
            GroupName = "03 | Arming / Contact")]
        public int ArmMinBars
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(
            Name = "Trigger Window (bars)",
            Description =
                "Number of bars, including the contact bar, in which "
                + "a trigger close is accepted.",
            Order = 3,
            GroupName = "03 | Arming / Contact")]
        public int TriggerWindowBars
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(
            Name = "Max Consecutive Stop-outs per Anchor",
            Order = 4,
            GroupName = "03 | Arming / Contact")]
        public int MaxStopOutsPerAnchor
        {
            get;
            set;
        }

        // ---------------------------------------------------------------------
        // 04 | Trade Management
        // ---------------------------------------------------------------------

        [NinjaScriptProperty]
        [Range(0.25, 100.0)]
        [Display(
            Name = "Max Stop (points)",
            Description =
                "Skip the trade if the stop distance exceeds this.",
            Order = 1,
            GroupName = "04 | Trade Management")]
        public double MaxStopPoints
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(0.25, 100.0)]
        [Display(
            Name = "R:R Threshold (points)",
            Description =
                "Stop below this value uses High R:R. Otherwise Low R:R.",
            Order = 2,
            GroupName = "04 | Trade Management")]
        public double RRThresholdPoints
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(
            Name = "High R:R",
            Order = 3,
            GroupName = "04 | Trade Management")]
        public double HighRR
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(
            Name = "Low R:R",
            Order = 4,
            GroupName = "04 | Trade Management")]
        public double LowRR
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(
            Name = "Contracts",
            Order = 5,
            GroupName = "04 | Trade Management")]
        public int ContractQty
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Display(
            Name = "Enable Stop Halving",
            Description =
                "Provisional stop-adjustment method.",
            Order = 6,
            GroupName = "04 | Trade Management")]
        public bool EnableStopHalving
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(
            Name = "Post-Exit Cooldown (bars)",
            Order = 7,
            GroupName = "04 | Trade Management")]
        public int CooldownBars
        {
            get;
            set;
        }

        // ---------------------------------------------------------------------
        // 05 | Session
        // ---------------------------------------------------------------------

        [NinjaScriptProperty]
        [Display(
            Name = "RTH Start (HHMMSS)",
            Order = 1,
            GroupName = "05 | Session")]
        public int RTHStartHHMMSS
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Display(
            Name = "RTH End (HHMMSS)",
            Description =
                "Anchors die and positions are flattened here.",
            Order = 2,
            GroupName = "05 | Session")]
        public int RTHEndHHMMSS
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Display(
            Name = "Last Entry (HHMMSS)",
            Order = 3,
            GroupName = "05 | Session")]
        public int LastEntryHHMMSS
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Display(
            Name = "No New Anchor After (HHMMSS)",
            Order = 4,
            GroupName = "05 | Session")]
        public int NoNewAnchorHHMMSS
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Display(
            Name = "Flatten At RTH End",
            Order = 5,
            GroupName = "05 | Session")]
        public bool FlattenAtRTHEnd
        {
            get;
            set;
        }

        // ---------------------------------------------------------------------
        // 06 | Risk Controls
        // ---------------------------------------------------------------------

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(
            Name = "Max Trades Per Day",
            Order = 1,
            GroupName = "06 | Risk Controls")]
        public int MaxTradesPerDay
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(
            Name = "Daily Loss Limit ($)",
            Description =
                "Zero disables this limit.",
            Order = 2,
            GroupName = "06 | Risk Controls")]
        public double DailyLossLimit
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(
            Name = "Daily Profit Limit ($)",
            Description =
                "Zero disables this limit.",
            Order = 3,
            GroupName = "06 | Risk Controls")]
        public double DailyProfitLimit
        {
            get;
            set;
        }

        // ---------------------------------------------------------------------
        // 07 | Diagnostics
        // ---------------------------------------------------------------------

        [NinjaScriptProperty]
        [Display(
            Name = "Write Trade CSV",
            Description =
                "Writes trades_ESAVWAPAnchor.csv to the NinjaTrader "
                + "user-data directory.",
            Order = 1,
            GroupName = "07 | Diagnostics")]
        public bool EnableTradeLog
        {
            get;
            set;
        }

        [NinjaScriptProperty]
        [Display(
            Name = "Draw Anchors / Contacts",
            Order = 2,
            GroupName = "07 | Diagnostics")]
        public bool DrawAnchors
        {
            get;
            set;
        }

        #endregion
    }
}

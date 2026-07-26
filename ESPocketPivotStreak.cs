//
// ESPocketPivotStreak.cs
// NinjaTrader 8 Strategy
//
// Description
// ───────────
// Translates the "Adv Simple Volume With Pocket Pivots" TradingView indicator
// into a mechanical entry strategy for the ES (5-minute chart).
//
// A "Pocket Pivot" bar is defined as:
//   Bull (Blue) : close > close[1]  AND  volume > max DOWN-bar volume in lookback
//   Bear (Purple): close < close[1]  AND  volume > max UP-bar volume in lookback
//
// Entry rules
//   Long  : StreakLength consecutive Bull PP bars → Enter Long at next bar open
//            Stop  = Low of last streak bar - 1 tick
//            Target= Stop distance × RewardRiskRatio (above fill)
//   Short : StreakLength consecutive Bear PP bars → Enter Short at next bar open
//            Stop  = High of last streak bar + 1 tick
//            Target= Stop distance × RewardRiskRatio (below fill)
//
// NOTE on stop placement
//   After an entry fills, OnExecutionUpdate recalculates the stop/target using
//   the ACTUAL FILL price, ensuring the stop is placed exactly at Low[0]-1 or
//   High[0]+1 (from the signal bar). The initial SetStopLoss in CheckLongEntry/
//   CheckShortEntry serves as a safety net if OnExecutionUpdate doesn't fire.
//
// NOTE on state persistence
//   Daily counters (dailyTradeCount, dailyRealizedPnL, dailyLossHit, dailyProfitHit)
//   reset when the strategy is reloaded or restarted mid-day. This is a general
//   NT8 limitation (private variables don't persist across instances). For most
//   traders this is acceptable; if you need full persistence, write to a file.
//
// Revision history
//   v0.2  2026-07-25  Fixed trade counter timing; added exact stop placement
//   v0.1  2026-07-24  Initial release
//

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
    public class ESPocketPivotStreak : Strategy
    {
        // ═══════════════════════════════════════════════════════════════════════
        // Runtime state
        // ═══════════════════════════════════════════════════════════════════════
        private Series<bool> isBullPP;      // true when bar[i] is a Bull Pocket Pivot
        private Series<bool> isBearPP;      // true when bar[i] is a Bear Pocket Pivot

        private DateTime lastResetDate;     // tracks the last day counters were cleared
        private int      dailyTradeCount;   // entries taken today
        private double   dailyRealizedPnL;  // cumulative closed P&L for the day ($)
        private bool     dailyLossHit;      // flag: daily loss limit reached
        private bool     dailyProfitHit;    // flag: daily profit limit reached

        // Used in OnExecutionUpdate for P&L calculation and stop management
        private double   lastEntryPrice;
        private int      lastEntryDirection; // +1 = long, -1 = short
        private double   plannedStopPrice;   // exact stop price level (Low[0]-1 or High[0]+1)
        private double   plannedTargetTicks; // target distance in ticks (for R:R)

        // ═══════════════════════════════════════════════════════════════════════
        // OnStateChange
        // ═══════════════════════════════════════════════════════════════════════
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                  = "Enters long/short after N consecutive Pocket Pivot bars. "
                                             + "Pocket Pivot = bar whose volume exceeds the max opposite-direction "
                                             + "bar volume in the lookback window (same logic as 'Adv Simple Volume "
                                             + "With Pocket Pivots' on TradingView).";
                Name                         = "ESPocketPivotStreak";
                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;   // close open positions 30 s before session end

                // ── Pocket Pivot ─────────────────────────────────────────────
                PocketPivotLookback  = 10;
                StreakLength         = 2;

                // ── Trade Management ─────────────────────────────────────────
                RewardRiskRatio      = 2.0;
                ContractQty          = 1;

                // ── Session ──────────────────────────────────────────────────
                EnableRTHOnly        = true;
                RTHStartHHMMSS       = 083000;   // 08:30:00 CT
                RTHEndHHMMSS         = 145500;   // 14:55:00 CT  (no new entries after this)

                // ── Risk Controls ─────────────────────────────────────────────
                MaxTradesPerDay      = 3;
                DailyLossLimit       = 500.0;    // $500; set 0 to disable
                DailyProfitLimit     = 1000.0;   // $1,000; set 0 to disable
            }
            else if (State == State.DataLoaded)
            {
                // MaximumBarsLookBack.Infinite keeps the full history in memory
                // so that isBullPP[i] is valid for any i up to CurrentBar.
                isBullPP = new Series<bool>(this, MaximumBarsLookBack.Infinite);
                isBearPP = new Series<bool>(this, MaximumBarsLookBack.Infinite);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // OnBarUpdate — called once per closed bar (Calculate.OnBarClose)
        // ═══════════════════════════════════════════════════════════════════════
        protected override void OnBarUpdate()
        {
            // ── 1. Warmup guard ──────────────────────────────────────────────
            // Need enough bars so the lookback scan and streak check have valid data.
            // +3 gives a small safety margin beyond the bare minimum.
            if (CurrentBar < PocketPivotLookback + StreakLength + 3)
                return;

            // ── 2. Daily counter reset ───────────────────────────────────────
            if (Time[0].Date != lastResetDate)
            {
                lastResetDate      = Time[0].Date;
                dailyTradeCount    = 0;
                dailyRealizedPnL   = 0.0;
                dailyLossHit       = false;
                dailyProfitHit     = false;
            }

            // ── 3. Classify this bar as Bull PP / Bear PP ────────────────────
            //    Must run every bar so isBullPP[i]/isBearPP[i] are populated
            //    for the streak look-back on future bars.
            ClassifyCurrentBar();

            // ── 4. Guards (checked in priority order) ────────────────────────
            if (dailyLossHit || dailyProfitHit)
                return;                                        // daily risk limit hit

            if (!IsInAllowedSession())
                return;                                        // outside RTH window

            if (dailyTradeCount >= MaxTradesPerDay)
                return;                                        // max trades cap

            if (Position.MarketPosition != MarketPosition.Flat)
                return;                                        // already in a position

            // ── 5. Entry signals ─────────────────────────────────────────────
            CheckLongEntry();
            CheckShortEntry();
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ClassifyCurrentBar
        // Sets isBullPP[0] and isBearPP[0] for the bar that just closed.
        // ═══════════════════════════════════════════════════════════════════════
        private void ClassifyCurrentBar()
        {
            // Need at least one prior bar for close comparison
            if (CurrentBar < 2)
            {
                isBullPP[0] = false;
                isBearPP[0] = false;
                return;
            }

            bool isUpBar   = Close[0] > Close[1];
            bool isDownBar = Close[0] < Close[1];

            // Scan the previous N bars (not including bar[0]) for the
            // maximum DOWN-bar volume and maximum UP-bar volume.
            double maxDownVol = double.NaN;
            double maxUpVol   = double.NaN;

            int scanBars = Math.Min(PocketPivotLookback, CurrentBar - 1);
            for (int i = 1; i <= scanBars; i++)
            {
                // bar[i] vs bar[i+1] tells us the direction of that historical bar
                bool wasDown = Close[i] < Close[i + 1];
                bool wasUp   = Close[i] > Close[i + 1];

                if (wasDown)
                {
                    if (double.IsNaN(maxDownVol) || Volume[i] > maxDownVol)
                        maxDownVol = Volume[i];
                }
                if (wasUp)
                {
                    if (double.IsNaN(maxUpVol) || Volume[i] > maxUpVol)
                        maxUpVol = Volume[i];
                }
            }

            // Bull PP: up bar that out-volumes the heaviest down bar in the lookback
            isBullPP[0] = isUpBar   && !double.IsNaN(maxDownVol) && Volume[0] > maxDownVol;

            // Bear PP: down bar that out-volumes the heaviest up bar in the lookback
            isBearPP[0] = isDownBar && !double.IsNaN(maxUpVol)   && Volume[0] > maxUpVol;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // CheckLongEntry
        // ═══════════════════════════════════════════════════════════════════════
        private void CheckLongEntry()
        {
            // Verify StreakLength consecutive Bull PP bars ending at bar[0]
            // bar[0] = most recent closed bar, bar[1] = one before, etc.
            for (int i = 0; i < StreakLength; i++)
            {
                if (!isBullPP[i])
                    return;   // streak broken
            }

            // Store the exact stop price level: low of signal bar minus one tick
            // This will be used in OnExecutionUpdate to set precise stops from the actual fill
            plannedStopPrice   = Low[0] - TickSize;

            // Estimate stop distance using Close[0] as proxy for next bar's open
            // This provides a safety net, but OnExecutionUpdate will set the exact stop
            double stopTicks = Math.Round((Close[0] - plannedStopPrice) / TickSize);
            stopTicks        = Math.Max(1.0, stopTicks);   // floor at 1 tick

            plannedTargetTicks = Math.Round(stopTicks * RewardRiskRatio);
            plannedTargetTicks = Math.Max(1.0, plannedTargetTicks);

            // Submit entry order with approximate stop/target (safety net)
            EnterLong(ContractQty, "BullStreak");
            SetStopLoss   ("BullStreak", CalculationMode.Ticks, stopTicks,           false);
            SetProfitTarget("BullStreak", CalculationMode.Ticks, plannedTargetTicks);

            // Note: dailyTradeCount is incremented in OnExecutionUpdate when the order fills
        }

        // ═══════════════════════════════════════════════════════════════════════
        // CheckShortEntry
        // ═══════════════════════════════════════════════════════════════════════
        private void CheckShortEntry()
        {
            // Verify StreakLength consecutive Bear PP bars ending at bar[0]
            for (int i = 0; i < StreakLength; i++)
            {
                if (!isBearPP[i])
                    return;   // streak broken
            }

            // Store the exact stop price level: high of signal bar plus one tick
            // This will be used in OnExecutionUpdate to set precise stops from the actual fill
            plannedStopPrice   = High[0] + TickSize;

            // Estimate stop distance using Close[0] as proxy for next bar's open
            double stopTicks = Math.Round((plannedStopPrice - Close[0]) / TickSize);
            stopTicks        = Math.Max(1.0, stopTicks);

            plannedTargetTicks = Math.Round(stopTicks * RewardRiskRatio);
            plannedTargetTicks = Math.Max(1.0, plannedTargetTicks);

            // Submit entry order with approximate stop/target (safety net)
            EnterShort(ContractQty, "BearStreak");
            SetStopLoss   ("BearStreak", CalculationMode.Ticks, stopTicks,           false);
            SetProfitTarget("BearStreak", CalculationMode.Ticks, plannedTargetTicks);

            // Note: dailyTradeCount is incremented in OnExecutionUpdate when the order fills
        }

        // ═══════════════════════════════════════════════════════════════════════
        // IsInAllowedSession
        // ═══════════════════════════════════════════════════════════════════════
        private bool IsInAllowedSession()
        {
            if (!EnableRTHOnly)
                return true;

            int t = ToTime(Time[0]);  // returns HHMMSS as int
            return t >= RTHStartHHMMSS && t <= RTHEndHHMMSS;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // OnExecutionUpdate — tracks realized P&L for daily limits and sets exact stops
        // ═══════════════════════════════════════════════════════════════════════
        protected override void OnExecutionUpdate(
            Execution execution,
            string executionId,
            double price,
            int quantity,
            MarketPosition marketPosition,
            string orderId,
            DateTime time)
        {
            if (execution.Order == null) return;
            if (execution.Order.OrderState != OrderState.Filled) return;

            // Record entry fill details so we can compute P&L on exit
            switch (execution.Order.OrderAction)
            {
                case OrderAction.Buy:           // entering long
                    lastEntryPrice     = price;
                    lastEntryDirection = 1;
                    dailyTradeCount++;          // increment ONLY when the entry actually fills

                    // Set exact stop and target from the actual fill price
                    // Stop: plannedStopPrice (Low[0] - 1 tick from the signal bar)
                    // Target: distance from fill to stop × RewardRiskRatio
                    if (plannedStopPrice > 0)
                    {
                        double exactStopTicks = Math.Round((price - plannedStopPrice) / TickSize);
                        exactStopTicks        = Math.Max(1.0, exactStopTicks);

                        double exactTargetTicks = Math.Round(exactStopTicks * RewardRiskRatio);
                        exactTargetTicks        = Math.Max(1.0, exactTargetTicks);

                        SetStopLoss   (CalculationMode.Ticks, exactStopTicks,    false);
                        SetProfitTarget(CalculationMode.Ticks, exactTargetTicks);
                    }
                    break;

                case OrderAction.SellShort:     // entering short
                    lastEntryPrice     = price;
                    lastEntryDirection = -1;
                    dailyTradeCount++;          // increment ONLY when the entry actually fills

                    // Set exact stop and target from the actual fill price
                    // Stop: plannedStopPrice (High[0] + 1 tick from the signal bar)
                    // Target: distance from stop to fill × RewardRiskRatio
                    if (plannedStopPrice > 0)
                    {
                        double exactStopTicks = Math.Round((plannedStopPrice - price) / TickSize);
                        exactStopTicks        = Math.Max(1.0, exactStopTicks);

                        double exactTargetTicks = Math.Round(exactStopTicks * RewardRiskRatio);
                        exactTargetTicks        = Math.Max(1.0, exactTargetTicks);

                        SetStopLoss   (CalculationMode.Ticks, exactStopTicks,    false);
                        SetProfitTarget(CalculationMode.Ticks, exactTargetTicks);
                    }
                    break;

                case OrderAction.Sell:          // exiting a long
                case OrderAction.BuyToCover:    // exiting a short
                    if (lastEntryPrice == 0) break;

                    // P&L = direction × price difference × qty × $ per point
                    double pointValue  = Instrument.MasterInstrument.PointValue;  // 50 for ES
                    double tradePnL    = lastEntryDirection
                                        * (price - lastEntryPrice)
                                        * quantity
                                        * pointValue;

                    dailyRealizedPnL += tradePnL;

                    // Evaluate daily limits after each closed trade
                    if (DailyLossLimit   > 0 && dailyRealizedPnL <= -Math.Abs(DailyLossLimit))
                        dailyLossHit   = true;

                    if (DailyProfitLimit > 0 && dailyRealizedPnL >= Math.Abs(DailyProfitLimit))
                        dailyProfitHit = true;

                    // Reset entry tracking
                    lastEntryPrice     = 0;
                    lastEntryDirection = 0;
                    plannedStopPrice   = 0;     // clear for next trade
                    break;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Properties — exposed in the Strategy Parameters dialog
        // ═══════════════════════════════════════════════════════════════════════
        #region Properties

        // ── 01 | Pocket Pivot ────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Range(2, 500)]
        [Display(
            Name        = "Pocket Pivot Lookback (bars)",
            Description = "Number of prior bars scanned for the maximum up/down volume reference. "
                        + "Mirrors the 'Pocket pivot lookback' input in the TradingView script.",
            Order       = 1,
            GroupName   = "01 | Pocket Pivot")]
        public int PocketPivotLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(
            Name        = "Streak Length (bars)",
            Description = "Number of consecutive Pocket Pivot bars required before an entry is triggered. "
                        + "Default 2 = two consecutive blue/purple bars.",
            Order       = 2,
            GroupName   = "01 | Pocket Pivot")]
        public int StreakLength { get; set; }

        // ── 02 | Trade Management ────────────────────────────────────────────

        [NinjaScriptProperty]
        [Range(0.5, 20.0)]
        [Display(
            Name        = "Reward:Risk Ratio",
            Description = "Profit target as a multiple of the stop-loss distance in ticks. "
                        + "E.g. 2.0 means target = 2 × stop.",
            Order       = 1,
            GroupName   = "02 | Trade Management")]
        public double RewardRiskRatio { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(
            Name        = "Contracts",
            Description = "Number of contracts per trade.",
            Order       = 2,
            GroupName   = "02 | Trade Management")]
        public int ContractQty { get; set; }

        // ── 03 | Session ─────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(
            Name        = "RTH Only",
            Description = "When true, new entries are blocked outside Regular Trading Hours. "
                        + "Open positions are still managed (stops/targets) at all times.",
            Order       = 1,
            GroupName   = "03 | Session")]
        public bool EnableRTHOnly { get; set; }

        [NinjaScriptProperty]
        [Display(
            Name        = "RTH Start (HHMMSS)",
            Description = "Earliest time a new entry may be opened. Format: HHMMSS (e.g. 083000 = 08:30:00 CT).",
            Order       = 2,
            GroupName   = "03 | Session")]
        public int RTHStartHHMMSS { get; set; }

        [NinjaScriptProperty]
        [Display(
            Name        = "RTH End (HHMMSS)",
            Description = "Latest time a new entry may be opened. Format: HHMMSS (e.g. 145500 = 14:55:00 CT). "
                        + "Existing positions continue to be managed after this cutoff.",
            Order       = 3,
            GroupName   = "03 | Session")]
        public int RTHEndHHMMSS { get; set; }

        // ── 04 | Risk Controls ───────────────────────────────────────────────

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(
            Name        = "Max Trades Per Day",
            Description = "Strategy stops opening new positions once this many entries have been taken today.",
            Order       = 1,
            GroupName   = "04 | Risk Controls")]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(
            Name        = "Daily Loss Limit ($)",
            Description = "Halt new entries for the rest of the day once realized losses reach this dollar amount. "
                        + "Set to 0 to disable.",
            Order       = 2,
            GroupName   = "04 | Risk Controls")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(
            Name        = "Daily Profit Limit ($)",
            Description = "Halt new entries for the rest of the day once realized profits reach this dollar amount. "
                        + "Set to 0 to disable.",
            Order       = 3,
            GroupName   = "04 | Risk Controls")]
        public double DailyProfitLimit { get; set; }

        #endregion
    }
}

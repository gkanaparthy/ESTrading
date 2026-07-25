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
//   NT8's SetStopLoss(CalculationMode.Ticks) places the stop N ticks from the
//   ACTUAL FILL price (next bar open), not from Close[0].  The stop tick count
//   is computed from Close[0] as a proxy, so the physical stop level will be
//   within 1-3 ticks of Low[0]-1tick on most 5-min bars.  For exact price-level
//   stops, manage exits manually via OnExecutionUpdate in a future revision.
//
// Revision history
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

        // Used in OnExecutionUpdate for P&L calculation
        private double   lastEntryPrice;
        private int      lastEntryDirection; // +1 = long, -1 = short

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

            // Stop price: low of the most recent streak bar minus one tick
            double stopPrice = Low[0] - TickSize;

            // Compute stop distance in ticks using Close[0] as fill proxy
            // (actual fill = open of next bar, typically within a few ticks of Close[0])
            double stopTicks = Math.Round((Close[0] - stopPrice) / TickSize);
            stopTicks        = Math.Max(1.0, stopTicks);   // floor at 1 tick

            double targetTicks = Math.Round(stopTicks * RewardRiskRatio);
            targetTicks        = Math.Max(1.0, targetTicks);

            EnterLong(ContractQty, "BullStreak");
            SetStopLoss   ("BullStreak", CalculationMode.Ticks, stopTicks,    false);
            SetProfitTarget("BullStreak", CalculationMode.Ticks, targetTicks);

            dailyTradeCount++;
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

            // Stop price: high of the most recent streak bar plus one tick
            double stopPrice = High[0] + TickSize;

            double stopTicks = Math.Round((stopPrice - Close[0]) / TickSize);
            stopTicks        = Math.Max(1.0, stopTicks);

            double targetTicks = Math.Round(stopTicks * RewardRiskRatio);
            targetTicks        = Math.Max(1.0, targetTicks);

            EnterShort(ContractQty, "BearStreak");
            SetStopLoss   ("BearStreak", CalculationMode.Ticks, stopTicks,    false);
            SetProfitTarget("BearStreak", CalculationMode.Ticks, targetTicks);

            dailyTradeCount++;
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
        // OnExecutionUpdate — tracks realized P&L for daily limits
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
                    break;

                case OrderAction.SellShort:     // entering short
                    lastEntryPrice     = price;
                    lastEntryDirection = -1;
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

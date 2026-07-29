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
//   Bull (Blue)  : close > close[1]  AND  volume > max DOWN-bar volume in lookback
//   Bear (Purple): close < close[1]  AND  volume > max UP-bar volume in lookback
//
// Entry rules
//   Long  : (a) StreakLength consecutive Bull PP (blue) bars, OR
//           (b) alternating pattern blue-green-blue (when enabled)
//           → Enter Long at next bar open
//            Stop  = Low of signal bar - 1 tick
//            Target= Stop distance × RewardRiskRatio (above fill)
//   Short : (a) StreakLength consecutive Bear PP (purple) bars, OR
//           (b) alternating pattern purple-red-purple (when enabled)
//           → Enter Short at next bar open
//            Stop  = High of signal bar + 1 tick
//            Target= Stop distance × RewardRiskRatio (below fill)
//
//   green = up bar, volume > volume SMA, not a Bull PP
//   red   = down bar, volume > volume SMA, not a Bear PP
//
// Stop-halving (optional)
//   Once price moves in favor by the initial stop distance, the stop is moved
//   to HALF that distance from entry — one time per trade. E.g. a 20-tick stop
//   becomes a 10-tick stop after the trade goes +20 ticks in your favor.
//
// Post-exit cooldown  (CooldownBars, default 1)
//   The cooldown is anchored to the BAR INDEX on which the exit filled
//   (lastExitBar), NOT to a counter incremented in OnBarUpdate. This matters
//   because a stop/target usually fills INTRABAR: the bar that contains the
//   exit still closes afterwards and calls OnBarUpdate, which would consume a
//   counter-based cooldown and allow an entry on the very next bar.
//     Exit fills somewhere inside bar 81 → lastExitBar = 81
//     Bar 81 closes  : CurrentBar - lastExitBar = 0 → signal BLOCKED
//     Bar 82 closes  : CurrentBar - lastExitBar = 1 → signal allowed
//   Because entries execute at the NEXT bar's open, blocking the signal on
//   bar 81 is what prevents any trade on bar 82.
//
// Max stop filter  (MaxStopLossTicks, default 25)
//   Entries are skipped when the planned stop distance exceeds the limit.
//   Re-checked against the actual fill price; a position whose true stop
//   exceeds the limit is flattened immediately.
//
// NOTE on stop placement
//   After an entry fills, OnExecutionUpdate recalculates the stop/target using
//   the ACTUAL FILL price, ensuring the stop is placed exactly at Low[0]-1 or
//   High[0]+1 (from the signal bar). The initial SetStopLoss in SubmitLongEntry/
//   SubmitShortEntry serves as a safety net if OnExecutionUpdate doesn't fire.
//
// NOTE on state persistence
//   Daily counters (dailyTradeCount, dailyRealizedPnL, dailyLossHit, dailyProfitHit)
//   reset when the strategy is reloaded or restarted mid-day. This is a general
//   NT8 limitation (private variables don't persist across instances). For most
//   traders this is acceptable; if you need full persistence, write to a file.
//
// Revision history
//   v0.7  2026-07-29  Fixed post-exit cooldown: replaced barsSinceExit counter with
//                     lastExitBar bar-index anchor (counter was consumed by the
//                     exit's own bar when the stop/target filled intrabar);
//                     added CooldownBars parameter and a flat-transition backstop
//   v0.6  2026-07-28  Added 1-bar post-exit cooldown (no entry on bar after exit);
//                     added MaxStopLossTicks filter (default 25) to skip wide stops
//   v0.5  2026-07-27  Fixed stop-halving to actually modify stop on chart by using
//                     signal name in SetStopLoss call; added signal name tracking
//   v0.4  2026-07-26  Fixed SetStopLoss compilation (removed isSimulatedStop param);
//                     added debug logging for pocket pivot bars and key events;
//                     added blocked-signal logging to debug entry guards
//   v0.3  2026-07-26  Added blue-green-blue / purple-red-purple alternating pattern
//                     entries and optional stop-halving management
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
        private Series<bool> isBullPP;      // true when bar[i] is a Bull Pocket Pivot (blue)
        private Series<bool> isBearPP;      // true when bar[i] is a Bear Pocket Pivot (purple)
        private Series<bool> isGreenBar;    // true when bar[i] is a high up-volume bar (green)
        private Series<bool> isRedBar;      // true when bar[i] is a high down-volume bar (red)

        private DateTime lastResetDate;     // tracks the last day counters were cleared
        private int      dailyTradeCount;   // entries taken today
        private double   dailyRealizedPnL;  // cumulative closed P&L for the day ($)
        private bool     dailyLossHit;      // flag: daily loss limit reached
        private bool     dailyProfitHit;    // flag: daily profit limit reached

        // Used in OnExecutionUpdate for P&L calculation and stop management
        private double   lastEntryPrice;     // actual fill price of the current entry
        private int      lastEntryDirection; // +1 = long, -1 = short
        private string   currentSignalName;  // "BullStreak" or "BearStreak" for current position
        private double   plannedStopPrice;   // exact stop price level (Low[0]-1 or High[0]+1)
        private double   plannedTargetTicks; // target distance in ticks (for R:R)

        // Stop-halving state
        private double   initialStopTicks;  // exact stop distance in ticks at entry
        private bool     stopHalved;        // true once the stop has been halved for this trade

        // Post-exit cooldown state
        //   lastExitBar   = CurrentBar at the moment the exit filled. -1 = no exit yet.
        //   wasInPosition = position state on the previous OnBarUpdate; used as a
        //                   backstop to catch flat transitions OnExecutionUpdate misses
        //                   (session-close exits, manual flatten, partial-fill edges).
        private int      lastExitBar = -1;
        private bool     wasInPosition;

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
                VolumeAverageLength  = 50;      // SMA length for green/red bar classification

                // ── Entry Patterns ───────────────────────────────────────────
                EnableAlternatingPattern = true; // blue-green-blue / purple-red-purple

                // ── Trade Management ─────────────────────────────────────────
                RewardRiskRatio      = 2.0;
                ContractQty          = 1;
                EnableStopHalving    = true;     // halve stop after favorable move = initial risk
                MaxStopLossTicks     = 25;       // skip entry if stop distance exceeds this
                CooldownBars         = 1;        // no entry on the bar right after an exit

                // ── Session ──────────────────────────────────────────────────
                EnableRTHOnly        = true;
                RTHStartHHMMSS       = 083000;   // 08:30:00 CT
                RTHEndHHMMSS         = 145500;   // 14:55:00 CT  (no new entries after this)

                // ── Risk Controls ─────────────────────────────────────────────
                MaxTradesPerDay      = 30;
                DailyLossLimit       = 5000.0;    // $500; set 0 to disable
                DailyProfitLimit     = 10000.0;   // $1,000; set 0 to disable
            }
            else if (State == State.DataLoaded)
            {
                // MaximumBarsLookBack.Infinite keeps the full history in memory
                // so that isBullPP[i] is valid for any i up to CurrentBar.
                isBullPP   = new Series<bool>(this, MaximumBarsLookBack.Infinite);
                isBearPP   = new Series<bool>(this, MaximumBarsLookBack.Infinite);
                isGreenBar = new Series<bool>(this, MaximumBarsLookBack.Infinite);
                isRedBar   = new Series<bool>(this, MaximumBarsLookBack.Infinite);

                // Reset cooldown/position tracking for this instance
                lastExitBar   = -1;
                wasInPosition = false;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // OnBarUpdate — called once per closed bar (Calculate.OnBarClose)
        // ═══════════════════════════════════════════════════════════════════════
        protected override void OnBarUpdate()
        {
            // ── 1. Warmup guard ──────────────────────────────────────────────
            // Need enough bars so the lookback scan, volume SMA, and streak/pattern
            // checks all have valid data. +3 gives a small safety margin.
            int warmupBars = Math.Max(PocketPivotLookback, VolumeAverageLength) + StreakLength + 3;
            if (CurrentBar < warmupBars)
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

            // ── 2b. Flat-transition backstop for the cooldown anchor ─────────
            //    OnExecutionUpdate is the primary anchor (it fires intrabar with
            //    the correct CurrentBar). This block catches exits that never
            //    produce a tracked Sell/BuyToCover execution — e.g. exit-on-
            //    session-close, manual flatten, or a reset mid-position.
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (wasInPosition)
                {
                    wasInPosition = false;
                    if (lastExitBar < CurrentBar)
                    {
                        lastExitBar = CurrentBar;
                        Print(string.Format("{0:yyyy-MM-dd HH:mm} Cooldown anchored by flat transition | exitBar={1}",
                            Time[0], lastExitBar));
                    }
                }
            }
            else
            {
                wasInPosition = true;
            }

            // ── 3. Classify this bar (blue/purple/green/red) ─────────────────
            //    Must run every bar so the color series are populated for the
            //    streak/pattern look-back on future bars.
            ClassifyCurrentBar();

            // ── 3b. Manage any open position BEFORE entry guards ──────────────
            //    Stop-halving must run even when new entries are blocked
            //    (outside session / daily limits hit).
            if (EnableStopHalving)
                ManageStopHalving();

            // ── 4. Check for signals FIRST (for logging blocked entries) ─────
            bool longSignal  = CheckLongStreak()
                             || (EnableAlternatingPattern && CheckLongPattern());
            bool shortSignal = CheckShortStreak()
                             || (EnableAlternatingPattern && CheckShortPattern());

            // ── 5. Exit an open position when the opposing signal fires ─────
            // Exit management must run before the entry guards so an opposing
            // signal can close a position even outside RTH or after a daily limit.
            if (Position.MarketPosition == MarketPosition.Long && shortSignal)
            {
                ExitLong("OpposingSignalExit", "BullStreak");

                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} EXIT LONG | Opposing short signal fired",
                    Time[0]));

                return; // Exit only; do not reverse into a short on the same signal
            }

            if (Position.MarketPosition == MarketPosition.Short && longSignal)
            {
                ExitShort("OpposingSignalExit", "BearStreak");

                Print(string.Format(
                    "{0:yyyy-MM-dd HH:mm} EXIT SHORT | Opposing long signal fired",
                    Time[0]));

                return; // Exit only; do not reverse into a long on the same signal
            }

            // ── 6. Entry guards (checked in priority order) ──────────────────
            if (dailyLossHit || dailyProfitHit)
            {
                if (longSignal || shortSignal)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm} Signal BLOCKED | Daily limit hit (Loss={1} Profit={2})",
                        Time[0], dailyLossHit, dailyProfitHit));
                return;
            }

            if (!IsInAllowedSession())
            {
                if (longSignal || shortSignal)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm} Signal BLOCKED | Outside RTH ({1:HHmm} not in {2:D6}-{3:D6})",
                        Time[0], Time[0], RTHStartHHMMSS, RTHEndHHMMSS));
                return;
            }

            if (dailyTradeCount >= MaxTradesPerDay)
            {
                if (longSignal || shortSignal)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm} Signal BLOCKED | Max trades hit ({1}/{2})",
                        Time[0], dailyTradeCount, MaxTradesPerDay));
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if (longSignal || shortSignal)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm} Signal BLOCKED | Already in position ({1})",
                        Time[0], Position.MarketPosition));
                return;
            }

            // Post-exit cooldown — anchored to the bar the exit filled on.
            // Blocking the signal on the exit bar itself is what prevents an
            // entry on the following bar (entries fill at the next bar's open).
            if (CooldownBars > 0
                && lastExitBar >= 0
                && (CurrentBar - lastExitBar) < CooldownBars)
            {
                if (longSignal || shortSignal)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm} Signal BLOCKED | Post-exit cooldown (exitBar={1} curBar={2} elapsed={3} need>={4})",
                        Time[0], lastExitBar, CurrentBar, CurrentBar - lastExitBar, CooldownBars));
                return;
            }

            // ── 7. Execute entry signals ─────────────────────────────────────
            if (longSignal)
                SubmitLongEntry();
            else if (shortSignal)
                SubmitShortEntry();
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ClassifyCurrentBar
        // Sets isBullPP[0], isBearPP[0], isGreenBar[0], isRedBar[0] for the bar
        // that just closed. Mirrors the color logic of the TradingView indicator.
        // ═══════════════════════════════════════════════════════════════════════
        private void ClassifyCurrentBar()
        {
            // Need at least one prior bar for close comparison
            if (CurrentBar < 2)
            {
                isBullPP[0]   = false;
                isBearPP[0]   = false;
                isGreenBar[0] = false;
                isRedBar[0]   = false;
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

            // Log important pocket pivot bars (blue/purple only)
            if (isBullPP[0])
                Print(string.Format("{0:yyyy-MM-dd HH:mm} BLUE (Bull PP) | Vol={1:F0} > MaxDown={2:F0}",
                    Time[0], Volume[0], maxDownVol));
            else if (isBearPP[0])
                Print(string.Format("{0:yyyy-MM-dd HH:mm} PURPLE (Bear PP) | Vol={1:F0} > MaxUp={2:F0}",
                    Time[0], Volume[0], maxUpVol));

            // Green / Red classification (needs the volume moving average).
            //   Green = up bar with volume above the average that is NOT a Bull PP.
            //   Red   = down bar with volume above the average that is NOT a Bear PP.
            // Matches the TradingView color priority (dry > blue > purple > red > green),
            // where a bar coloured blue/purple is never also treated as green/red.
            double avgVolume   = SMA(Volume, VolumeAverageLength)[0];
            bool   volAboveAvg = avgVolume > 0 && Volume[0] > avgVolume;

            isGreenBar[0] = isUpBar   && volAboveAvg && !isBullPP[0];
            isRedBar[0]   = isDownBar && volAboveAvg && !isBearPP[0];
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Signal checkers — return true when the pattern is present ending at bar[0]
        // bar[0] = most recent closed bar, bar[1] = one before, etc.
        // ═══════════════════════════════════════════════════════════════════════

        // Long: StreakLength consecutive Bull PP (blue) bars
        private bool CheckLongStreak()
        {
            for (int i = 0; i < StreakLength; i++)
                if (!isBullPP[i])
                    return false;   // streak broken
            return true;
        }

        // Short: StreakLength consecutive Bear PP (purple) bars
        private bool CheckShortStreak()
        {
            for (int i = 0; i < StreakLength; i++)
                if (!isBearPP[i])
                    return false;   // streak broken
            return true;
        }

        // Long alternating pattern: blue-green-blue
        //   bar[2] = Bull PP, bar[1] = green (high up-volume), bar[0] = Bull PP
        private bool CheckLongPattern()
        {
            return isBullPP[2] && isGreenBar[1] && isBullPP[0];
        }

        // Short alternating pattern: purple-red-purple
        //   bar[2] = Bear PP, bar[1] = red (high down-volume), bar[0] = Bear PP
        private bool CheckShortPattern()
        {
            return isBearPP[2] && isRedBar[1] && isBearPP[0];
        }

        // ═══════════════════════════════════════════════════════════════════════
        // SubmitLongEntry — stop = Low[0] - 1 tick, target = stop distance × R:R
        // ═══════════════════════════════════════════════════════════════════════
        private void SubmitLongEntry()
        {
            // Store the exact stop price level: low of signal bar minus one tick.
            // OnExecutionUpdate re-derives exact ticks from the actual fill price.
            plannedStopPrice  = Low[0] - TickSize;
            currentSignalName = "BullStreak";  // track signal name for stop modifications

            // Estimate stop distance using Close[0] as proxy for next bar's open
            // (safety net; OnExecutionUpdate sets the exact stop after the fill).
            double stopTicks = Math.Round((Close[0] - plannedStopPrice) / TickSize);
            stopTicks        = Math.Max(1.0, stopTicks);   // floor at 1 tick

            // Skip if stop is wider than the configured maximum
            if (stopTicks > MaxStopLossTicks)
            {
                Print(string.Format("{0:yyyy-MM-dd HH:mm} LONG BLOCKED | Stop={1:F0}t > MaxStopLossTicks={2}",
                    Time[0], stopTicks, MaxStopLossTicks));
                plannedStopPrice  = 0;
                currentSignalName = null;
                return;
            }

            plannedTargetTicks = Math.Round(stopTicks * RewardRiskRatio);
            plannedTargetTicks = Math.Max(1.0, plannedTargetTicks);

            EnterLong(ContractQty, currentSignalName);
            SetStopLoss    (currentSignalName, CalculationMode.Ticks, stopTicks, false);
            SetProfitTarget(currentSignalName, CalculationMode.Ticks, plannedTargetTicks);

            Print(string.Format("{0:yyyy-MM-dd HH:mm} LONG signal | Stop={1:F2} ({2:F0}t) Target={3:F0}t",
                Time[0], plannedStopPrice, stopTicks, plannedTargetTicks));

            // Note: dailyTradeCount is incremented in OnExecutionUpdate when the order fills
        }

        // ═══════════════════════════════════════════════════════════════════════
        // SubmitShortEntry — stop = High[0] + 1 tick, target = stop distance × R:R
        // ═══════════════════════════════════════════════════════════════════════
        private void SubmitShortEntry()
        {
            // Store the exact stop price level: high of signal bar plus one tick.
            plannedStopPrice  = High[0] + TickSize;
            currentSignalName = "BearStreak";  // track signal name for stop modifications

            double stopTicks = Math.Round((plannedStopPrice - Close[0]) / TickSize);
            stopTicks        = Math.Max(1.0, stopTicks);

            // Skip if stop is wider than the configured maximum
            if (stopTicks > MaxStopLossTicks)
            {
                Print(string.Format("{0:yyyy-MM-dd HH:mm} SHORT BLOCKED | Stop={1:F0}t > MaxStopLossTicks={2}",
                    Time[0], stopTicks, MaxStopLossTicks));
                plannedStopPrice  = 0;
                currentSignalName = null;
                return;
            }

            plannedTargetTicks = Math.Round(stopTicks * RewardRiskRatio);
            plannedTargetTicks = Math.Max(1.0, plannedTargetTicks);

            EnterShort(ContractQty, currentSignalName);
            SetStopLoss    (currentSignalName, CalculationMode.Ticks, stopTicks, false);
            SetProfitTarget(currentSignalName, CalculationMode.Ticks, plannedTargetTicks);

            Print(string.Format("{0:yyyy-MM-dd HH:mm} SHORT signal | Stop={1:F2} ({2:F0}t) Target={3:F0}t",
                Time[0], plannedStopPrice, stopTicks, plannedTargetTicks));

            // Note: dailyTradeCount is incremented in OnExecutionUpdate when the order fills
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ManageStopHalving — once the trade moves in favor by the initial stop
        // distance, move the stop to half that distance from entry (one-time).
        // Example: 20-tick initial stop → after +20 ticks favorable, stop = 10 ticks.
        // ═══════════════════════════════════════════════════════════════════════
        private void ManageStopHalving()
        {
            if (stopHalved) return;                                   // already done for this trade
            if (initialStopTicks <= 0 || lastEntryPrice <= 0) return; // no active trade yet
            if (string.IsNullOrEmpty(currentSignalName)) return;      // no signal name tracked

            MarketPosition mp = Position.MarketPosition;
            if (mp == MarketPosition.Flat) return;

            // Favorable excursion measured in ticks from the entry fill.
            // High[0] for longs / Low[0] for shorts captures the bar's best move.
            double favorableTicks = (mp == MarketPosition.Long)
                                  ? (High[0] - lastEntryPrice) / TickSize
                                  : (lastEntryPrice - Low[0])  / TickSize;

            if (favorableTicks >= initialStopTicks)
            {
                double halvedTicks = Math.Max(1.0, Math.Round(initialStopTicks / 2.0));

                // Re-set the stop using the SAME signal name as the entry.
                // This is critical for NinjaTrader to actually modify the stop on the chart.
                SetStopLoss(currentSignalName, CalculationMode.Ticks, halvedTicks, false);
                stopHalved = true;

                Print(string.Format("{0:yyyy-MM-dd HH:mm} STOP HALVED | {1:F0}t → {2:F0}t (signal={3})",
                    Time[0], initialStopTicks, halvedTicks, currentSignalName));
            }
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
        // OnExecutionUpdate — tracks realized P&L for daily limits, sets exact
        // stops, and anchors the post-exit cooldown to the exit bar index
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
                    wasInPosition      = true;  // keep flat-transition backstop in sync

                    // Set exact stop and target from the actual fill price
                    // Stop: plannedStopPrice (Low[0] - 1 tick from the signal bar)
                    // Target: distance from fill to stop × RewardRiskRatio
                    if (plannedStopPrice > 0)
                    {
                        double exactStopTicks = Math.Round((price - plannedStopPrice) / TickSize);
                        exactStopTicks        = Math.Max(1.0, exactStopTicks);

                        // Safety: if fill-based stop exceeds max, flatten immediately
                        if (exactStopTicks > MaxStopLossTicks)
                        {
                            Print(string.Format("{0:yyyy-MM-dd HH:mm} LONG FILL REJECTED | ExactStop={1:F0}t > MaxStopLossTicks={2} — flattening",
                                time, exactStopTicks, MaxStopLossTicks));
                            ExitLong("MaxStopExceeded", currentSignalName ?? "BullStreak");
                            break;
                        }

                        double exactTargetTicks = Math.Round(exactStopTicks * RewardRiskRatio);
                        exactTargetTicks        = Math.Max(1.0, exactTargetTicks);

                        SetStopLoss    (CalculationMode.Ticks, exactStopTicks);
                        SetProfitTarget(CalculationMode.Ticks, exactTargetTicks);

                        // Store for stop-halving management
                        initialStopTicks = exactStopTicks;
                        stopHalved       = false;

                        Print(string.Format("{0:yyyy-MM-dd HH:mm} LONG FILL @ {1:F2} | Stop={2:F0}t Target={3:F0}t",
                            time, price, exactStopTicks, exactTargetTicks));
                    }
                    break;

                case OrderAction.SellShort:     // entering short
                    lastEntryPrice     = price;
                    lastEntryDirection = -1;
                    dailyTradeCount++;          // increment ONLY when the entry actually fills
                    wasInPosition      = true;  // keep flat-transition backstop in sync

                    // Set exact stop and target from the actual fill price
                    // Stop: plannedStopPrice (High[0] + 1 tick from the signal bar)
                    // Target: distance from stop to fill × RewardRiskRatio
                    if (plannedStopPrice > 0)
                    {
                        double exactStopTicks = Math.Round((plannedStopPrice - price) / TickSize);
                        exactStopTicks        = Math.Max(1.0, exactStopTicks);

                        // Safety: if fill-based stop exceeds max, flatten immediately
                        if (exactStopTicks > MaxStopLossTicks)
                        {
                            Print(string.Format("{0:yyyy-MM-dd HH:mm} SHORT FILL REJECTED | ExactStop={1:F0}t > MaxStopLossTicks={2} — flattening",
                                time, exactStopTicks, MaxStopLossTicks));
                            ExitShort("MaxStopExceeded", currentSignalName ?? "BearStreak");
                            break;
                        }

                        double exactTargetTicks = Math.Round(exactStopTicks * RewardRiskRatio);
                        exactTargetTicks        = Math.Max(1.0, exactTargetTicks);

                        SetStopLoss    (CalculationMode.Ticks, exactStopTicks);
                        SetProfitTarget(CalculationMode.Ticks, exactTargetTicks);

                        // Store for stop-halving management
                        initialStopTicks = exactStopTicks;
                        stopHalved       = false;

                        Print(string.Format("{0:yyyy-MM-dd HH:mm} SHORT FILL @ {1:F2} | Stop={2:F0}t Target={3:F0}t",
                            time, price, exactStopTicks, exactTargetTicks));
                    }
                    break;

                case OrderAction.Sell:          // exiting a long
                case OrderAction.BuyToCover:    // exiting a short

                    // ── Anchor the post-exit cooldown to the CURRENT bar ──────
                    // This runs the moment the stop/target/exit fills, which is
                    // typically INTRABAR. Anchoring here (rather than counting in
                    // OnBarUpdate) is what makes the cooldown actually work: the
                    // exit's own bar will still close and call OnBarUpdate, and
                    // that call must be BLOCKED so no entry occurs on the next bar.
                    if (lastExitBar < CurrentBar)
                        lastExitBar = CurrentBar;

                    if (Position.MarketPosition == MarketPosition.Flat)
                        wasInPosition = false;

                    if (lastEntryPrice == 0)
                    {
                        Print(string.Format("{0:yyyy-MM-dd HH:mm} EXIT @ {1:F2} | cooldown anchored exitBar={2} (no entry price tracked)",
                            time, price, lastExitBar));
                        break;
                    }

                    // P&L = direction × price difference × qty × $ per point
                    double pointValue = Instrument.MasterInstrument.PointValue;  // 50 for ES
                    double tradePnL   = lastEntryDirection
                                      * (price - lastEntryPrice)
                                      * quantity
                                      * pointValue;

                    dailyRealizedPnL += tradePnL;

                    Print(string.Format("{0:yyyy-MM-dd HH:mm} EXIT @ {1:F2} | P&L=${2:F2} DailyP&L=${3:F2} | cooldown exitBar={4}",
                        time, price, tradePnL, dailyRealizedPnL, lastExitBar));

                    // Evaluate daily limits after each closed trade
                    if (DailyLossLimit   > 0 && dailyRealizedPnL <= -Math.Abs(DailyLossLimit))
                        dailyLossHit   = true;

                    if (DailyProfitLimit > 0 && dailyRealizedPnL >= Math.Abs(DailyProfitLimit))
                        dailyProfitHit = true;

                    // Reset entry tracking
                    lastEntryPrice     = 0;
                    lastEntryDirection = 0;
                    currentSignalName  = null;  // clear signal name for next trade
                    plannedStopPrice   = 0;     // clear for next trade
                    initialStopTicks   = 0;     // clear stop-halving state
                    stopHalved         = false;
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

        [NinjaScriptProperty]
        [Range(2, 500)]
        [Display(
            Name        = "Volume Average Length (bars)",
            Description = "SMA length of volume used to classify green (high up-volume) and "
                        + "red (high down-volume) bars for the alternating pattern. "
                        + "Mirrors the 'Volume average length' input in the TradingView script.",
            Order       = 3,
            GroupName   = "01 | Pocket Pivot")]
        public int VolumeAverageLength { get; set; }

        // ── 02 | Entry Patterns ──────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(
            Name        = "Enable Alternating Pattern",
            Description = "When true, also enters on the 3-bar alternating pattern: "
                        + "blue-green-blue → long, purple-red-purple → short. "
                        + "The consecutive streak entry is always active regardless of this setting.",
            Order       = 1,
            GroupName   = "02 | Entry Patterns")]
        public bool EnableAlternatingPattern { get; set; }

        // ── 03 | Trade Management ────────────────────────────────────────────

        [NinjaScriptProperty]
        [Range(0.5, 20.0)]
        [Display(
            Name        = "Reward:Risk Ratio",
            Description = "Profit target as a multiple of the stop-loss distance in ticks. "
                        + "E.g. 2.0 means target = 2 × stop.",
            Order       = 1,
            GroupName   = "03 | Trade Management")]
        public double RewardRiskRatio { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(
            Name        = "Contracts",
            Description = "Number of contracts per trade.",
            Order       = 2,
            GroupName   = "03 | Trade Management")]
        public int ContractQty { get; set; }

        [NinjaScriptProperty]
        [Display(
            Name        = "Enable Stop Halving",
            Description = "When true, once the trade moves in your favor by the initial stop distance, "
                        + "the stop is moved to half that distance from entry (one-time per trade). "
                        + "Example: a 20-tick stop becomes 10 ticks after +20 ticks favorable.",
            Order       = 3,
            GroupName   = "03 | Trade Management")]
        public bool EnableStopHalving { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(
            Name        = "Max Stop Loss (ticks)",
            Description = "Do not take a trade if the planned stop-loss distance exceeds this many ticks. "
                        + "Default 25. Checked at signal time (Close proxy) and again on the actual fill.",
            Order       = 4,
            GroupName   = "03 | Trade Management")]
        public int MaxStopLossTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(
            Name        = "Post-Exit Cooldown (bars)",
            Description = "Bars to wait after a trade closes before a new entry signal is accepted. "
                        + "1 = no entry on the bar immediately following the exit bar. 0 disables.",
            Order       = 5,
            GroupName   = "03 | Trade Management")]
        public int CooldownBars { get; set; }

        // ── 04 | Session ─────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(
            Name        = "RTH Only",
            Description = "When true, new entries are blocked outside Regular Trading Hours. "
                        + "Open positions are still managed (stops/targets) at all times.",
            Order       = 1,
            GroupName   = "04 | Session")]
        public bool EnableRTHOnly { get; set; }

        [NinjaScriptProperty]
        [Display(
            Name        = "RTH Start (HHMMSS)",
            Description = "Earliest time a new entry may be opened. Format: HHMMSS (e.g. 083000 = 08:30:00 CT).",
            Order       = 2,
            GroupName   = "04 | Session")]
        public int RTHStartHHMMSS { get; set; }

        [NinjaScriptProperty]
        [Display(
            Name        = "RTH End (HHMMSS)",
            Description = "Latest time a new entry may be opened. Format: HHMMSS (e.g. 145500 = 14:55:00 CT). "
                        + "Existing positions continue to be managed after this cutoff.",
            Order       = 3,
            GroupName   = "04 | Session")]
        public int RTHEndHHMMSS { get; set; }

        // ── 05 | Risk Controls ───────────────────────────────────────────────

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(
            Name        = "Max Trades Per Day",
            Description = "Strategy stops opening new positions once this many entries have been taken today.",
            Order       = 1,
            GroupName   = "05 | Risk Controls")]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(
            Name        = "Daily Loss Limit ($)",
            Description = "Halt new entries for the rest of the day once realized losses reach this dollar amount. "
                        + "Set to 0 to disable.",
            Order       = 2,
            GroupName   = "05 | Risk Controls")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(
            Name        = "Daily Profit Limit ($)",
            Description = "Halt new entries for the rest of the day once realized profits reach this dollar amount. "
                        + "Set to 0 to disable.",
            Order       = 3,
            GroupName   = "05 | Risk Controls")]
        public double DailyProfitLimit { get; set; }

        #endregion
    }
}

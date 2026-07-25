# ES5mConfluence — Strategy Specification v0.1

**Status:** Draft — pending review before coding
**Instrument:** ES futures (MES for sim/early live)
**Timeframe:** 5-minute primary series
**Platform:** NinjaTrader 8 (NinjaScript Strategy)
**Base templates:** `VWAPReversal2m.cs` (structure), `ESLevelFadeV0.cs` (levels), `ES_2m_SweepReclaim.cs` (sweep logic)

---

## 1. Concept

Bi-directional intraday strategy that only takes trades when **at least 2 of 3 edge families agree**:

1. **VWAP family** — price stretched from session AVWAP and reverting, or reclaiming AVWAP with momentum.
2. **Level family** — proximity to a tracked level (prior-day high/low, overnight high/low, pivot bands, open print).
3. **Sweep-reclaim family** — liquidity sweep of a recent swing/level followed by a reclaim close back through it.

Confluence score gates entries; single-signal setups are ignored.

## 2. Session & Regime Filters

- **RTH only:** entries 08:35–14:30 CT (skip first bar; no new entries after 14:30).
- **Hard flat:** 14:55 CT — exit all positions, cancel orders.
- **Skip days:** optional flag to disable trading on FOMC days (manual bool param `BlockNewsDays`).
- **Volatility gate:** 14-period ATR(5m) must be within `[MinATR, MaxATR]` ticks (defaults: 4–30). Too quiet = no edge; too wild = spreads/slippage risk.
- **Chop gate:** no entries if last N bars (default 6) all overlap the AVWAP band (range-bound noise filter).

## 3. Signal Definitions

### 3.1 VWAP signals
- **VWAP Fade (mean reversion):** price >= `K1` (default 2.0) standard deviation bands from session AVWAP, then a reversal bar closes back inside band 2 -> fade toward AVWAP.
- **VWAP Reclaim (momentum):** price closes across AVWAP after >= `M` bars (default 3) on the other side, with close in top/bottom third of bar range.

### 3.2 Level signals
- Tracked levels: prior-day high/low/close, overnight high/low, RTH open, and pivot band levels (reuse `ESLevelFadeV0` band logic).
- **Level Touch:** bar trades within `LevelProximityTicks` (default 6) of a level.
- **Level Reject:** touch + close >= `RejectTicks` (default 4) away from the level, back in trade direction.

### 3.3 Sweep-reclaim signals
- **Sweep:** bar takes out a swing high/low (lookback `SwingBars`, default 20) or a tracked level by >= 2 ticks.
- **Reclaim:** within `ReclaimWindow` bars (default 2), a bar closes back through the swept level.
- Signal direction: opposite of the sweep (long after low sweep+reclaim, short after high sweep+reclaim).

## 4. Entry Logic

- Compute a **confluence score** each bar close: +1 per family firing in the same direction.
- **Entry when score >= 2**, all regime gates pass, and no position open.
- Direction = direction of the agreeing signals; conflicting signals (long+short in same bar) -> no trade.
- Order: market on next bar open (v0.1 keeps execution simple; limit entries deferred to v0.2).
- **Touchflag/freeflag:** one attempt per level/setup per session — after a level-based entry (win or lose), that level is flagged and not re-traded until reset (reuse existing flag pattern).

## 5. Exits & Risk

- **Initial stop:** `StopATRmult` x ATR (default 1.25), rounded to ticks, capped at `MaxStopTicks` (default 20).
- **Target:** `TargetATRmult` x ATR (default 2.0) — minimum 1.5R after cap.
- **Breakeven:** move stop to entry +/- 1 tick after +1R.
- **Trail (optional, off by default):** ATR trail after +1.5R.
- **Time stop:** exit at market if trade hasn't reached +0.5R within `TimeStopBars` (default 8 bars = 40 min).

### Risk gates (reuse existing infra)
- **Per-trade dollar risk cap:** `MaxRiskPerTrade` (default $150 on MES sizing math; position size = floor(risk / (stopTicks x tickValue)), min 1).
- **Daily loss guard:** stop trading for the day after realized loss >= `DailyLossLimit` (default $300).
- **Max trades/day:** default 4.
- **Max consecutive losers:** halt after 3.

## 6. Parameters Summary

| Param | Default | Notes |
|---|---|---|
| K1 (VWAP band mult) | 2.0 | fade trigger |
| M (bars beyond VWAP) | 3 | reclaim setup |
| LevelProximityTicks | 6 | |
| RejectTicks | 4 | |
| SwingBars | 20 | sweep lookback |
| ReclaimWindow | 2 | bars |
| MinATR / MaxATR | 4 / 30 | ticks |
| StopATRmult / TargetATRmult | 1.25 / 2.0 | |
| MaxStopTicks | 20 | |
| TimeStopBars | 8 | |
| MaxRiskPerTrade | $150 | |
| DailyLossLimit | $300 | |
| MaxTradesPerDay | 4 | |

## 7. Validation Plan

1. **Backtest:** 12+ months of 5m ES RTH data, commission + 1-tick slippage per side.
2. **Metrics required:** profit factor > 1.3, max drawdown < 2x best month, >= 100 trades in sample.
3. **Walk-forward:** 3-month in-sample / 1-month out-of-sample rolling; params must be stable (no cliff behavior on +/-20% param perturbation).
4. **Sim:** minimum 4 weeks on MES live sim before any real capital.
5. **Logging:** print confluence components per entry (which families fired) to enable per-family attribution — this feeds the future ML filter.

## 8. Future (v0.2+)

- Limit-order entries at reclaim level.
- ML filter: train classifier on logged features (score components, ATR, time of day, distance to VWAP) to veto low-probability entries.
- Delta/volume confirmation series.

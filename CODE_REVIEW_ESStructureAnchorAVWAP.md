# Code Review: `ESStructureAnchorAVWAP.cs` vs. Profitability Plan

Date: 2026-02-14

Reviewed against: `PROFITABLE_ES_FUTURES_PLAN.md`

---

## ✅ What the Code Gets Right

| Plan Requirement | Code Status |
|---|---|
| `EntriesPerDirection = 1` | ✅ Line 55 |
| One position at a time (checked via `Position.MarketPosition`) | ✅ Line 127 |
| ATR-based stop (not fixed tiny ticks) | ✅ Line 140, with `MinStopTicks` floor |
| Target as R-multiple (1.8–2.2R range, default 2.0R) | ✅ Line 142 |
| Trade windows (9:45–11:00, 13:00–15:00 ET) | ✅ Lines 239-241 |
| Daily max opportunities (6) | ✅ Line 76, checked at L246 |
| Max consecutive losses (2) | ✅ Line 77, checked at L249 |
| Daily stop at -2R | ✅ Line 78, checked at L252 |
| No anchor creation after 15:00 ET | ✅ Line 117 |
| Gap-day 15-minute delay | ✅ Lines 294-296 |
| Max 1 structural override per session | ✅ Line 315 (`structuralOverrideUsed`) |
| Override cooldown (20 bars) | ✅ Line 319 |
| Anchor degradation/retirement | ✅ Lines 124-125, `IsAnchorDegraded` |
| `IsExitOnSessionCloseStrategy = true` | ✅ Line 57 |

The code is structurally clean and reads well. The parameter system is well-organized. Good consolidation from 52 files down to one.

---

## 🔴 Critical Issues

### 1. No AVWAP Calculation Anywhere

This is the single biggest problem. The plan's entire thesis is **"AVWAP pullback with trend + volatility gate"** — anchored VWAP. But the code **never computes an anchored VWAP**. Instead, `GetLongAnchor()` returns `dayLow` (a raw price) and `GetShortAnchor()` returns `dayHigh` (a raw price).

An anchored VWAP is a **volume-weighted average price** computed from a specific bar forward. It's not the same as using the HOD/LOD price level directly. This means:

- **Entries are triggering on raw LOD/HOD levels**, not AVWAP zones — the strategy is a raw pivot-level bounce strategy, not a VWAP strategy.
- AVWAP provides a dynamic, volume-weighted level that "moves" as the session progresses. A raw LOD is static until a new low prints. These behave very differently in practice.

**Fix:** Implement a running AVWAP calculation anchored at the bar where HOD/LOD formed (or the structural anchor bar). Accumulate `Σ(TypicalPrice * Volume) / Σ(Volume)` from the anchor bar to the current bar.

### 2. No Volatility/Regime Gate

The plan explicitly requires:
> - **ADX(14) < 18** plus frequent anchor cross → no new entries
> - **Extreme volatility** (VIX threshold or extreme ATR regime) → disable or reduce

The code has **zero regime filtering**. No ADX indicator, no VIX reference, no ATR-regime band check. This means the strategy will trade into dead, choppy, range-bound sessions AND into extreme volatility meltdowns — both of which destroy edge.

**Fix:** Add at minimum:

```csharp
private ADX adx;
// In DataLoaded: adx = ADX(14);
// In OnBarUpdate: if (adx[0] < 18 && IsAnchorDegraded(baseAnchor)) return; // chop regime
// Also: if (atr[0] > ExtremeAtrThreshold) return; // extreme vol regime
```

### 3. No Breakeven Management

Plan says:
> Move to breakeven only after >= 1R achieved.

The code sets `SetStopLoss` and `SetProfitTarget` as static bracket orders and **never adjusts the stop** once in a trade. There's no trailing or breakeven logic. The `OnBarUpdate` returns early at line 128 when in a position, so no management logic runs during a trade.

This is a significant profitability leak — many trades that reach 1R will reverse and stop out for a full loss instead of breakeven.

**Fix:** Remove the early return when in a position (or add a management branch), and implement:

```csharp
if (Position.MarketPosition != MarketPosition.Flat)
{
    ManageOpenPosition(); // check if unrealized >= 1R, move stop to entry
    return;
}
```

### 4. LOD Invalidation Rule Missing

Plan says:
> If price closes below LOD and shows follow-through lower, invalidate LOD-long setups for the session unless a new defended low is formed.

The code updates `dayLow = Math.Min(dayLow, Low[0])` (line 110), which means if price breaks below LOD, the LOD simply moves down. But the plan says you should **invalidate long setups at that point** — meaning the broken LOD anchor should be flagged as failed, not silently updated.

Currently, if LOD at 5800 breaks and price goes to 5795, the strategy just says "new LOD is 5795" and immediately starts looking for long reclaims there. This is dangerous because LOD breaks with follow-through are bearish continuation signals.

**Fix:** Add a `lodInvalidated` flag. When `Close[0] < dayLow` AND the next bar confirms follow-through (e.g., `Low[0] < previousLow`), set `lodInvalidated = true`. Only reset when a new defended low forms.

### 5. Conflicting Anchor Signals Not Handled

Plan says:
> If two active anchors give opposite signals at the same time, no trade.

The code doesn't check for this. If `structuralOverrideActive` is true with a `StructuralBull` kind, and the HOD is simultaneously giving a bearish signal, the code will just ignore one based on the `if/else if (bullish/bearish)` branching. There's no explicit conflict detection.

---

## 🟡 Important Edge Cases & Subtle Issues

### 6. Trend Filter is Too Weak (Single-Bar EMA Slope)

```csharp
bool bullish = ema[0] > ema[1];  // Line 133
bool bearish = ema[0] < ema[1];  // Line 134
```

This is a **single-bar EMA comparison** — the tiniest wiggle in the 20 EMA flips the trend. On ES 2-minute bars, this will flip constantly during consolidation. The plan suggests either:
- Slope of EMA(20) — but "slope" typically means a multi-bar measurement (e.g., `ema[0] - ema[5]`), or
- Session VWAP slope

A 1-bar delta is noise, not trend. This will cause whipsaws.

**Fix:** Use a multi-bar slope measurement:

```csharp
bool bullish = ema[0] > ema[5] && Close[0] > ema[0]; // uptrend + above EMA
```

### 7. Slippage Model is Symmetric (Plan Requires Asymmetric)

```csharp
Slippage = 1;  // Line 62
```

The plan requires **asymmetric slippage modeling**:
> - Target slippage: 0.25–0.5 ticks
> - Stop slippage: 1.0–2.0 ticks

NinjaTrader's `Slippage` property applies uniformly. This means backtests will be **too optimistic on stops** (assuming only 1 tick slip) and **too pessimistic on targets** (assuming 1 tick slip when reality might be 0.25). Net effect on a 2:1 RR strategy: the backtest is overstating edge.

**Fix:** For backtesting, set `Slippage = 2` to be conservative on stops. You can't easily make it asymmetric in NinjaTrader's standard fill model, but erring on the side of larger slippage is safer. Alternatively, use a custom fill model.

### 8. `dayHigh`/`dayLow` Initialized in `DataLoaded` to `High[0]`/`Low[0]`

```csharp
else if (State == State.DataLoaded)  // Line 90
{
    ...
    dayHigh = High[0];  // Line 97
    dayLow = Low[0];    // Line 98
}
```

At `State.DataLoaded`, the bars haven't been processed yet and `High[0]`/`Low[0]` may not be meaningful. This is a minor issue since `ResetDailyStateIfNeeded()` will reset them on the first bar of each session, but it's sloppy and could produce incorrect values if `sessionDate` happens to match on the first bar.

**Fix:** Initialize to `double.MinValue` / `double.MaxValue` or handle in `ResetDailyStateIfNeeded` exclusively.

### 9. Entry Logic: "Reclaim" Condition May Be Too Strict

```csharp
bool reclaim = Close[1] <= longAnchor && Close[0] > longAnchor;  // Line 148
bool pullbackNear = Low[0] <= longAnchor + zone;                  // Line 149
bool confirmingBar = Close[0] > Open[0];                           // Line 150
```

This requires the **prior bar's close** to be at or below the anchor AND the **current bar's close** to be above it — requiring a cross-above in a single bar. Combined with `pullbackNear` (low near anchor) and a confirming bar, all three conditions must be true **simultaneously on the same bar**. This is extremely restrictive and will filter out many legitimate setups where:
- Price makes a 2-3 bar base at the anchor before lifting
- Price gaps slightly above the anchor on open but still pulls back to test it intrabar

Consider whether `reclaim` within the last 2-3 bars would be more appropriate.

### 10. `consecutiveLosses` Resets Daily — But What About Cross-Session Streaks?

Line 224: `consecutiveLosses = 0;` resets every day. If the strategy loses the last 2 trades on Monday and then the first trade on Tuesday, it doesn't know it's on a 3-trade losing streak. The plan's "10-trade rolling expectancy" rule (line 184 of plan) isn't implemented either.

### 11. No Chop Filter on Active Entry Path

The plan says:
> Chop filter: if price closes on both sides of an anchor repeatedly over recent bars, stand down.

The `IsAnchorDegraded` method exists and is used for **structural override promotion** (line 305) and **override retirement** (line 124), but it's **never called on the active entry anchor** before taking a trade. A trader could enter a long at LOD while LOD has been chopped 6 times — the code only checks chop when deciding whether to promote/retire a structural override.

**Fix:** Add to the entry logic:

```csharp
double longAnchor = GetLongAnchor();
if (IsAnchorDegraded(longAnchor)) return; // don't trade a choppy anchor
```

### 12. LOD Anchor Tier System Not Implemented

The plan defines Tier A (full risk) and Tier B (50% risk) LOD anchors:
> - Tier A: sharp rejection, retest holds, orderly reaction
> - Tier B: mixed/choppy, allow only one attempt, 50% risk

The code doesn't distinguish tiers. Every LOD anchor is treated with full risk.

---

## 🟢 Profitability Engineering Improvements

### 13. Add Time-of-Day Quality Weighting

The morning window (9:45–11:00) historically has higher-quality AVWAP setups on ES than early afternoon. Consider tracking and biasing toward morning signals, or reducing risk on afternoon trades.

### 14. Add a Minimum ATR Floor for Entry

If ATR is extremely low (dead market), the stop will be tiny and vulnerable to normal noise. Add:

```csharp
if (atr[0] < MinAtrForEntry) return; // e.g., 1.5 ES points minimum
```

### 15. Add MFE/MAE Tracking for Post-Analysis

The plan requires MFE/MAE tracking in Phase 5. Adding `Print()` statements or custom data tracking for each trade's maximum favorable/adverse excursion would help validate the target/stop parameters.

### 16. Consider Partial Profit Taking

Many profitable ES strategies take partial profits at 1R and let the remainder run to 2R with a breakeven stop. This significantly improves win rate and psychological sustainability while only modestly reducing average winner size.

---

## Summary of Action Items (Priority Ordered)

| Priority | Issue | Impact |
|---|---|---|
| 🔴 P0 | **Implement actual AVWAP calculation** — strategy has no VWAP | Entire thesis of strategy is missing |
| 🔴 P0 | **Add regime/volatility filter** (ADX + ATR extremes) | Prevents trading dead or extreme sessions |
| 🔴 P0 | **Add breakeven management at 1R** | Prevents giving back winners |
| 🔴 P1 | **Add chop filter on entry anchor** (not just override) | Prevents entries on degraded levels |
| 🔴 P1 | **Implement LOD invalidation** on breakdown | Prevents buying into bearish continuation |
| 🟡 P2 | **Strengthen trend filter** (multi-bar EMA slope) | Reduces whipsaw entries |
| 🟡 P2 | **Add conflicting-anchor signal check** | Prevents trades into ambiguous structure |
| 🟡 P2 | **Increase backtest slippage** to 2 ticks | More realistic P&L simulation |
| 🟡 P3 | **LOD tier system** (Tier A full / Tier B half risk) | Better risk allocation |
| 🟡 P3 | **Relax reclaim condition** to multi-bar window | More trade opportunities without quality loss |
| 🟢 P4 | **Minimum ATR floor** for entries | Avoids noise-killed trades |
| 🟢 P4 | **Rolling 10-trade expectancy check** | Per plan, pause when ≤ 0 |
| 🟢 P4 | **MFE/MAE tracking** | Required for Phase 5 validation |

---

## Bottom Line

The code is well-structured and handles the risk-management rails (daily caps, max trades, session windows, single override) correctly. But **the core edge is missing** — there's no AVWAP, no regime filter, and no in-trade management. These three gaps mean the strategy as written is essentially a raw HOD/LOD bounce trader with a weak trend filter, which is unlikely to be profitable on ES.

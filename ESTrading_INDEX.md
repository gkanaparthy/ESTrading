# ESTrading Repository — Strategy Index

**Repo:** https://github.com/gkanaparthy/ESTrading  
**Owner:** Gautham (gkanaparthy)  
**Instrument:** ES (S&P 500 E-mini Futures), primarily on NinjaTrader 8  
**Last updated:** 2026-03-27

---

## How to Use This File

This index is the entry point for any AI or developer reading this repository.  
Every `.cs` file is a NinjaTrader 8 `Strategy`. Each section below describes what the strategy does, its current status, and which files are worth reading first.

**Status legend:**
- 🟢 **Active / In Development** — currently being worked on
- 🟡 **Archived / Experimental** — past experiments, kept for reference
- 🔵 **Template / Base** — used as a starting point for new strategies
- ⚫ **Duplicate / Backup** — older or backup copy, not primary

---

## 🟢 Active Strategies (read these first)

### `ESVwapLite.cs`
**Class:** `ESVwapLite`  
**Status:** 🟢 Primary production strategy  
**What it does:**  
The main anchor-VWAP strategy. Draws session VWAP, weekly VWAP, and anchored VWAPs from structural points (HOD, LOD, PrevSessionHigh/Low, PreMarketHigh/Low, manual anchors via hotkey/click). Enters trades when price bounces or breaks from a relevant AVWAP level. Features:
- Multi-anchor system with automatic selection of the "most relevant" anchor
- Manual anchor hotkeys: trader can click a bar to set Long/Short AVWAP anchor
- Pre-market and previous session level overlays
- RTH-only trading (08:30–15:00 CT)
- Risk gates: ATR-based stop width, dollar risk cap per trade
- Configurable: which anchor types are active, stop/target parameters

**Key parameters:** `MaxStopPoints`, `ProfitTarget`, `EnablePrevSessionLevels`, `EnablePreMarketLevels`, anchor enable/disable toggles  
**File size:** ~64K (large, complex)

---

### `ESTrendline_v1.cs`
**Class:** `ESTrendline_v1`  
**Status:** 🟢 Active development — based on Tori Trades trendline methodology  
**What it does:**  
Draws swing-based trendlines automatically and trades bounces/breaks off them. Inspired by the "Tori Trades" system (attack line + safety line). Features:
- Auto-detects swing highs and lows using fractal lookback
- Draws uptrend lines (connecting swing lows) and downtrend lines (connecting swing highs)
- Trades: price approaches trendline touch zone → enters on rejection candle close
- Uses a safety line (second trendline behind the attack line) as fallback entry
- Risk gates: volatility guard (ATR %), stop width limit, dollar risk cap
- Trade windows, max trades/day, breakeven management
- Partial profits at parent trendlines

**Key parameters:** `MaxSwingLookback`, `TouchZoneTicks`, `MaxStopPoints`, `ProfitTarget`, trade window times  
**File size:** ~71K (largest strategy file)  
**Note:** Has a `CODE_REVIEW_ESStructureAnchorAVWAP.md` companion review document

---

### `ES_2m_SweepReclaim.cs` and `new_strategy/ES_2m_SweepReclaim.cs`
**Class:** `ES_2m_SweepReclaim`  
**Status:** 🟢 Active — baseline spec locked (Spec v2, 2026-03-09)  
**What it does:**  
2-minute ES intraday sweep-and-reclaim continuation strategy. Logic:
- Detects a swing high/low getting "swept" (false break, liquidity grab)
- Entry when price "reclaims" back above/below the swept level
- Trend filter: 15-min EMA slope + position vs session VWAP
- Fixed 1:3 RR, dynamic ATR-based stops, trade windows (09:00–10:30, 13:30–14:45 CT)
- Max 2 trades per window, max 4 per day
- News blackout on high-impact events

**File note:** Two copies exist — root `ES_2m_SweepReclaim.cs` (32K) and `new_strategy/ES_2m_SweepReclaim.cs` (38K, newer/more developed)  
**Key parameters:** `EmaPeriod`, `SwingFractalBars`, `StopMinAtrMult`, `RewardRiskRatio`

---

### `ESLevelFadeV0.cs`
**Class:** `ESLevelFadeV0`  
**Status:** 🟢 Locked baseline — do not modify  
**What it does:**  
Fades (counter-trend) pre-market and previous session key levels. Logic:
- Monitors 4 levels: PrevSessionHigh, PrevSessionLow, PreMarketHigh, PreMarketLow
- Arms a level only when price comes within `ArmDistanceAtr * ATR`
- Places limit order at the level (buy limit for support, sell limit for resistance)
- Fixed 6-tick stop, 18-tick target (1:3 RR)
- Levels re-arm after `ReentryExcursionAtr * ATR` excursion (unlock mechanism)
- RTH only, max N trades per level per session

**Companion spec:** `ESLevelFadeV0_SPEC.md` — read this before modifying  
**Key parameters:** `StopTicks=6`, `TargetTicks=18`, `ArmDistanceAtr`, `ReentryExcursionAtr`, `ClusterDistanceTicks`

---

### `ESLevelStopRunner.cs`
**Class:** `ESLevelStopRunner`  
**Status:** 🟢 Active — newer evolution of ESVwapLite  
**What it does:**  
Evolution of ESVwapLite. Same anchor infrastructure (AVWAP, session levels, manual anchors via hotkeys) but with a stop-runner entry model instead of limit orders. Enters on stop orders placed beyond key levels, intending to catch momentum continuation. Shares most structural code with ESVwapLite (same anchor enum, same constant tags, same hotkey system).  
**File size:** ~55K

---

### `VWAPReversal2m.cs`
**Class:** `VWAPReversal2m`  
**Status:** 🟢 New — v1.1 (2026-03-27)  
**What it does:**  
Clean bi-directional VWAP/AVWAP reversal strategy for 2-minute ES chart. Signal pattern:
- Bar[2]: touch bar — crosses the VWAP (both sides)
- Bar[1]: signal bar — closes cleanly back away from VWAP (NOT crossing it)
- Bar[0]: current bar — stop-market order placed beyond signal bar H/L

Entry: structural stop (signal bar range + 2 ticks). Max stop capped at `MaxStopTicks`. Target: `RiskRewardRatio` × stop (default 1:2). Breakeven after `BreakevenTicks` profit.

Supports session VWAP + up to 2 anchored VWAPs (AVWAP1, AVWAP2). Session VWAP takes priority when signals conflict.

**Key parameters:** `MaxStopTicks=8`, `RiskRewardRatio=2.0`, `BreakevenTicks=6`, `MaxTradesPerDay=4`, `MinBarsBetweenEntries=3`, `TradeWindowStart=830`, `TradeWindowEnd=1500`  
**Based on:** `RevAVWAPBiDirect030424.cs` (template, stripped of pivot band complexity)

---

### `ESStructureAnchorAVWAP.cs`
**Class:** `ESStructureAnchorAVWAP`  
**Status:** 🟢 Active research / experimental  
**What it does:**  
Advanced version combining structural swing analysis with anchored VWAPs. Anchors from: LOD/HOD tiers, structural bull/bear pivot points, rally/selloff origin bars, weekly open, session VWAP, and manual anchors. Adds ADX/EMA regime filter and volume SMA filter. More complex than ESVwapLite.  
**Companion:** `CODE_REVIEW_ESStructureAnchorAVWAP.md` — AI code review document  
**File size:** ~127K (largest file in repo)

---

## 🟡 Experimental / Research Strategies

### `EMA821Pullback.cs`
**Class:** `EMA821Pullback`  
**What it does:** Pullback strategy using 8 and 21 EMA crossover. Enters on pullback to 8/21 EMA zone after trend is established. Trailing stop management.

### `SMA1021PullbackTrailES2m.cs`
**Class:** `SMA10_21_PullbackTrail_ES2m`  
**What it does:** Similar to EMA821Pullback but uses SMA 10/21 on a 2-minute ES chart. Pullback entry with trailing stop. Clean modern implementation.

### `SessionLevelsPullback.cs`
**Class:** `SessionLevelsPullback`  
**What it does:** Pulls back to session levels (pre-market high/low, prev session high/low) and enters long/short on confirmation. Simpler than ESLevelFadeV0.

### `OpeningRangeStrat.cs`
**Class:** `OpeningRangeStrat`  
**What it does:** Classic opening range breakout. Captures the first N-minute range after RTH open, enters on break above/below it.

---

## 🔵 AVWAP / VWAP Family (Template Pool)

These files form the VWAP strategy family. Many share similar structure. Use as templates when building new VWAP-based strategies.

| File | Class | Notes |
|------|-------|-------|
| `RevAVWAPBiDirect030424.cs` | `RevAVWAPBiDirect030424` | **Best template** — bi-directional, 2 AVWAP anchors, touchflag/freeflag pattern. Base for VWAPReversal2m |
| `AVWAPBiDirection030124.cs` | `AVWAPBiDirection030124` | Older bi-directional AVWAP, Jan 2024 |
| `AVWAPBounce.cs` | `AVWAPBounce` | Single AVWAP bounce (long-only biased). Has pivot bands |
| `AVWAPBounce200.cs` | `AVWAPBounce` | AVWAPBounce variant with 200 parameter changes |
| `AVWAPOnly.cs` | `AVWAPOnly` | Stripped-down AVWAP entry only, no pivot bands |
| `AVWAPSize2.cs` | `AVWAPSize2` | AVWAP with 2-contract position sizing |
| `AVWAPs012224.cs` / `AVWAPs070424.cs` / `AVWAPs103123.cs` | `AVWAPs*` | Date-versioned AVWAP experiments |
| `AvWaP15R.cs` | — | AVWAP with 15-tick R target |
| `NewAVWAP2025.cs` | `NewAVWAP2025` | Cleaner 2025 rewrite of core AVWAP logic |
| `VWAPBounce.cs` | `VWAPBounce` | Session VWAP bounce (similar to AVWAPBounce but session-anchored) |
| `VWAPDirectionNewSL.cs` | `VWAPDirectionNewSL` | VWAP direction bias with new stop loss logic |
| `VWAPScalp1.cs` | `VWAPScalp1` | Quick scalp off VWAP touch, tight stops |
| `VWAPUltaR3in1.cs` | `VWAPUltaR3in1` | VWAP with 3-in-1 entry (3 contracts, scale out) |
| `VWAPUltaScalpRR.cs` | `VWAPUltaScalpRR` | VWAP scalp with configurable R:R |
| `WaveAVWAPStrategy.cs` | `WaveAVWAPStrategy` | VWAP with wave/oscillation detection |
| `NextBandVWAP.cs` | `NextBandVWAP` | VWAP + next pivot band as target |
| `DailyVwap200GPT5.cs` | `VWAPBounceATRGuard` | VWAP bounce with ATR guard filter (GPT-5 assisted) |
| `MTFPivotVWAP1.cs` / `MTFPivotVWAP2NOLIC.cs` | `MTFPivotVWAP1/2` | Multi-timeframe VWAP + pivot band combo |
| `JeffSunVWAP.cs` / `JeffSunVWAP 2.cs` | — | External strategy reference (Jeff Sun's VWAP approach) |
| `ATRQAVWAPTrail.cs` | `VWAPBounceStrategy` | AVWAP bounce with ATR-based trailing stop |
| `ATRQAVWAPTrailHalf.cs` | `ATRQAVWAPTrailHalf` | Half-position variant of ATRQAVWAPTrail |
| `ATRQAVWAPTrail_FIXED.cs` | `ATRQAVWAPTrail` | Bug-fixed version of ATRQAVWAPTrail |
| `ATRQAVWAPTrail_Aug27.cs` / `ATRQAVWAPTrailbkp.cs` | — | Date-versioned/backup copies |
| `GKAVWAP.cs` | `GKAVWAP` | Gautham's personal VWAP base template |

---

## 🔵 Pivot Band Family (Template Pool)

Strategies using classic pivot points (P, R1/S1, R2/S2, R3/S3) as band boundaries.

| File | Class | Notes |
|------|-------|-------|
| `GKPivotPointBandStrategy.cs` | `GKPivotPointBandStrategy` | Core pivot band implementation |
| `GKATRPivot.cs` | `GKATRPivot` | Pivot points + ATR-based dynamic stops |
| `GKPivotATRQtr.cs` | `GKPivotATRQtr` | Pivot with ATR quarter-band targeting |
| `PivotBandsBiDirect030724.cs` | `PivotBandsBiDirect030724` | Bi-directional pivot band trades, Jul 2024 |
| `RevPivotBandsBiDirect030524.cs` | `RevPivotBandsBiDirect030524` | Reversal at pivot bands, bi-directional, May 2024 |
| `PivotDirection.cs` | `PivotDirection` | Directional bias from pivot position |
| `PivotDirectionTrueBE.cs` | `PivotDirectionTrueBE` | PivotDirection with true breakeven logic |
| `PivotBounceDropTrail.cs` | `PivotBounceDropTrail` | Pivot bounce with trailing stop |
| `PP202051direcB4logs.cs` / `PP20251direc.cs` / `PP20251direcB4clean.cs` / `PP2025Claude.cs` | — | Pivot point 2025 experiments (Claude-assisted) |
| `GKATRNewSL.cs` | `GKATRNewSL` | ATR-based dynamic stop loss system |
| `GKPPNewSL.cs` | `GKPPNewSL` | Pivot points with new stop loss logic |
| `GKPreMarketNewSL.cs` | `GKPreMarketNewSL` | Pre-market levels + new stop loss |
| `NearFarNewSL.cs` | `NearFarNewSL` | Near/far band stop loss approach |
| `MTFScalpNTv2.cs` | `MTFScalpNTv2` | Multi-timeframe scalp v2 |
| `NewATRQOnly.cs` | `NewATRQOnly` | ATR quarter-range targeting only |

---

## 📁 Folders

### `new_strategy/`
Contains `ES_2m_SweepReclaim.cs` — the newer, more developed version of the sweep-reclaim strategy. This is the version being actively refined.

### `tori-strategy/`
Research folder for replicating the "Tori Trades" trendline methodology:
- `TORI_TRADES_NOTES.md` — scraped X/Twitter research from @toritrades. Documents her key concepts: "attack line" (entry trendline), "safety line" (backup trendline), wicks-based touch points, 1 trade/day philosophy, risk rules.
- `IMPLEMENTATION_PLAN.md` — step-by-step plan to implement Tori's system in NinjaTrader. This fed into `ESTrendline_v1.cs`.

---

## 📄 Documentation Files

| File | Purpose |
|------|---------|
| `ESLevelFadeV0_SPEC.md` | Locked spec for ESLevelFadeV0. Defines levels, session rules, entry logic, parameters. Do not deviate from this without updating the spec. |
| `CODE_REVIEW_ESStructureAnchorAVWAP.md` | AI code review of ESStructureAnchorAVWAP.cs. Documents bugs, edge cases, and improvement suggestions. |
| `tori-strategy/TORI_TRADES_NOTES.md` | Research notes on Tori Trades methodology. |
| `tori-strategy/IMPLEMENTATION_PLAN.md` | Implementation plan for ESTrendline_v1.cs. |

---

## 🏗 Architecture Patterns (common across strategies)

**Understanding these patterns helps you read any file in this repo:**

### 1. OnBarClose vs OnEachTick
Most strategies here use `Calculate.OnBarClose` for deterministic backtesting. A few use `OnEachTick` for real-time trailing stop management.

### 2. Anchor System (ESVwapLite / ESStructureAnchorAVWAP / ESLevelStopRunner)
These use an `AnchorKind` enum and `AnchorPoint` struct. The strategy scans for the "most relevant" anchor each bar and draws an AVWAP from it. Trader can override with manual hotkey (L key = long anchor, S key = short anchor).

### 3. touchflag / freeflag pattern (AVWAP family)
`touchflag = true` when price enters the VWAP zone. `freeflag = true` when price moves away from VWAP (frees the system to look for the next touch). Used in the older AVWAP strategies to prevent re-entering on the same touch.

### 4. Pivot band logic (pivot family)
`lowband`/`highband` define which pivot band the price is in. `nextbandL`/`nextbandS` are the targets if price breaks out. These are calculated from P/R1/R2/R3/S1/S2/S3 levels using `PriorDayOHLC`.

### 5. Daily loss / trade count guard
Nearly all strategies implement:
- `PrevDayPnL` reset at EOD
- `dayOverVar = true` when max daily loss hit → no new trades
- `DayTradeCount` capped at `MaxTradesPerDay`

### 6. Order management pattern
```
entryOrder = null → place entry → OnOrderUpdate assigns entryOrder → OnExecutionUpdate clears entryOrder
```
Stop/target orders are managed via `SetStopLoss()` / `SetProfitTarget()` with the entry signal name.

---

## 📌 Quick Reference — Which File to Read for What

| Goal | Start here |
|------|-----------|
| Understand the main production strategy | `ESVwapLite.cs` |
| Build a new trendline strategy | `ESTrendline_v1.cs` + `tori-strategy/TORI_TRADES_NOTES.md` |
| Build a VWAP reversal strategy | `VWAPReversal2m.cs` (clean) or `RevAVWAPBiDirect030424.cs` (template) |
| Build a level fade/bounce strategy | `ESLevelFadeV0.cs` + `ESLevelFadeV0_SPEC.md` |
| Build a sweep-reclaim strategy | `new_strategy/ES_2m_SweepReclaim.cs` |
| Understand anchor VWAP infrastructure | `ESStructureAnchorAVWAP.cs` + `CODE_REVIEW_ESStructureAnchorAVWAP.md` |
| Find a pivot band template | `GKPivotPointBandStrategy.cs` or `RevPivotBandsBiDirect030524.cs` |
| Find a clean modern template | `VWAPReversal2m.cs` (best documented, v1.1, all bugs fixed) |

# Unified ES Trading Plan — Implementation Specification

**Document Version:** 1.0  
**Date:** 2026-04-09  
**Author:** Gautham K (gkanaparthy)  
**Platform:** NinjaTrader 8  
**Instrument:** ES (E-mini S&P 500 Futures)  
**Chart:** 2-minute, OnBarClose  
**Session:** RTH 08:30–15:00 CT  

---

## Table of Contents

1. [Overview](#1-overview)
2. [Architecture](#2-architecture)
3. [Regime Filter (The Brain)](#3-regime-filter-the-brain)
4. [Strategy A — ESLevelFadeV0 (Range Mode)](#4-strategy-a--eslevelfadev0-range-mode)
5. [Strategy B — ES_2m_SweepReclaim (Trend Mode)](#5-strategy-b--es_2m_sweepreclaim-trend-mode)
6. [Strategy C — VWAPReversal2m (Conditional Midday)](#6-strategy-c--vwapreversal2m-conditional-midday)
7. [Global Risk Manager](#7-global-risk-manager)
8. [Trade Windows & Scheduling](#8-trade-windows--scheduling)
9. [Order Management & Position Tracking](#9-order-management--position-tracking)
10. [Shared Utilities & Base Class](#10-shared-utilities--base-class)
11. [Modifications to Existing Strategies](#11-modifications-to-existing-strategies)
12. [NinjaTrader Implementation Details](#12-ninjatader-implementation-details)
13. [Backtesting Plan](#13-backtesting-plan)
14. [Deployment Checklist](#14-deployment-checklist)
15. [File Reference Map](#15-file-reference-map)

---

## 1. Overview

### Philosophy

Trade only when the edge is clear. Two to three trades per day maximum. Every trade must have a structural reason behind it — a key level fade, a liquidity sweep reclaim, or a clean VWAP reversal pattern. The regime filter decides which strategy is active before the first trade fires. The global risk manager enforces hard daily limits across all strategies.

### The Three Strategies

| ID | Strategy | Mode | R:R | Role | Existing File |
|----|----------|------|-----|------|---------------|
| A | ESLevelFadeV0 | Range | 3:1 | Fade session/pre-market levels | `ESLevelFadeV0.cs` |
| B | ES_2m_SweepReclaim | Trend | 3:1 | Sweep-and-reclaim continuation | `new_strategy/ES_2m_SweepReclaim.cs` |
| C | VWAPReversal2m | Any | 2:1 | 3-bar VWAP reversal pattern | `VWAPReversal2m.cs` |

### Daily Trade Budget

| Scenario | Strategy A | Strategy B | Strategy C | Total |
|----------|-----------|-----------|-----------|-------|
| Range day, all windows fire | 2 | 0 | 1 | 3 |
| Trend day, all windows fire | 0 | 2 | 1 | 3 |
| Mixed day | 1 | 1 | 1 | 3 |
| Bad morning (2 losses) | 1–2 | 0 | 0 | 1–2 |
| Conservative day | 1 | 0 | 0 | 1 |

### Hard Constraints

- **Max trades per day:** 3 (across ALL strategies combined)
- **Max daily loss:** -$400 (realized P&L, checked after every fill)
- **No trading before:** 09:00 CT (first 30 min = regime read)
- **No trading after:** 14:45 CT
- **Flatten all positions by:** 14:58 CT (2 minutes before close)
- **Max consecutive losing days before throttle:** 2 (reduce to 1 trade/day)

---

## 2. Architecture

### Option A: Single Orchestrator Strategy (Recommended)

Build one NinjaTrader strategy (`ESUnifiedPlan.cs`) that contains all three sub-strategies as internal modules. The orchestrator owns the regime filter, global risk manager, and trade counter. Sub-strategies are methods/classes within the orchestrator, not separate NinjaTrader strategies.

**Why:** NinjaTrader does not natively support inter-strategy communication. Running three separate strategies means they can't share a trade count or daily P&L limit without an external file or shared static class. A single strategy avoids this entirely.

```
ESUnifiedPlan.cs
├── RegimeFilter          (decides range vs. trend at 09:00)
├── GlobalRiskManager     (daily P&L, trade count, consecutive loss tracking)
├── StrategyA_LevelFade   (ESLevelFadeV0 logic)
├── StrategyB_SweepReclaim (ES_2m_SweepReclaim logic)
├── StrategyC_VWAPReversal (VWAPReversal2m logic)
└── SharedUtils           (pivot calc, ATR helpers, session management)
```

### Option B: Separate Strategies + Static Shared State

If the orchestrator becomes too large, run three separate `.cs` strategies but share state via a static class:

```csharp
// File: ESUnifiedState.cs (AddOn or indicator)
public static class ESUnifiedState
{
    public static int TotalTradesToday { get; set; }
    public static double RealizedPnLToday { get; set; }
    public static RegimeType CurrentRegime { get; set; }
    public static int ConsecutiveLosingDays { get; set; }
    public static DateTime LastResetDate { get; set; }
    
    public static bool CanTrade()
    {
        if (TotalTradesToday >= 3) return false;
        if (RealizedPnLToday <= -400.0) return false;
        return true;
    }
    
    public static void Reset()
    {
        if (DateTime.Now.Date != LastResetDate.Date)
        {
            TotalTradesToday = 0;
            RealizedPnLToday = 0;
            LastResetDate = DateTime.Now.Date;
        }
    }
}

public enum RegimeType
{
    Unknown,    // Before 09:00 CT — no trading
    Range,      // ADX < 20 or chopping VWAP
    Trend,      // ADX > 20 and directional
    Throttled   // After 2 consecutive losing days
}
```

**Recommendation:** Start with Option A. Only split into Option B if the single file exceeds ~80K and becomes hard to maintain.

---

## 3. Regime Filter (The Brain)

### Purpose

Before any trade fires, the regime filter classifies the day as **Range** or **Trend**. This classification determines which strategies are active. This is the single most important component — it keeps you out of the wrong strategy on the wrong day.

### Indicators Required

| Indicator | Period | Timeframe | Source |
|-----------|--------|-----------|--------|
| ADX | 14 | 15-minute | NinjaTrader built-in `ADX(BarsArray[1], 14)` |
| Session VWAP | — | 2-minute | NinjaTrader `VWAP1` or manual computation |

### Adding the 15-Minute Data Series

```csharp
protected override void OnStateChange()
{
    if (State == State.Configure)
    {
        // BarsArray[0] = 2-minute (primary)
        AddDataSeries(BarsPeriodType.Minute, 15);  // BarsArray[1] = 15-minute
    }
    
    if (State == State.DataLoaded)
    {
        adx15m = ADX(BarsArray[1], 14);
        vwap = VWAP1(Close);  // Session VWAP on primary series
    }
}
```

### Regime Decision Logic

**When:** 09:00 CT (exactly 30 minutes into RTH)  
**Frequency:** Once per day. The regime is set and does NOT change for the rest of the session.  
**Exception:** If ADX crosses the 20 threshold decisively during the day (rises above 25 from below 18), allow a one-time regime upgrade from Range → Trend. Never downgrade from Trend → Range mid-session.

```csharp
private RegimeType DetermineRegime()
{
    // --- Prerequisite: must be 09:00 CT or later ---
    if (Times[0][0].TimeOfDay < new TimeSpan(14, 0, 0))  // 09:00 CT = 14:00 UTC
        return RegimeType.Unknown;

    // --- Check consecutive losing days throttle ---
    if (consecutiveLosingDays >= 2)
        return RegimeType.Throttled;

    // --- ADX reading from 15-minute chart ---
    double currentADX = adx15m[0];
    
    // --- VWAP cross count: count how many times Close crossed VWAP in last 15 bars (30 min) ---
    int vwapCrossCount = 0;
    for (int i = 1; i <= 15 && i < CurrentBars[0]; i++)
    {
        bool prevAbove = Closes[0][i] > vwap[i];
        bool currAbove = Closes[0][i - 1] > vwap[i - 1];
        if (prevAbove != currAbove)
            vwapCrossCount++;
    }

    // --- Gap day detection: open vs prior close ---
    bool isGapDay = Math.Abs(Opens[0][0] - PriorDayOHLC().PriorClose[0]) > 8.0;

    // --- Decision ---
    // Chopping VWAP overrides ADX: if price can't decide which side of VWAP it's on, it's range
    if (vwapCrossCount >= 3 && !isGapDay)
        return RegimeType.Range;

    // ADX-based classification
    if (currentADX > 20.0)
        return RegimeType.Trend;
    else
        return RegimeType.Range;
}
```

### Regime Upgrade (Mid-Session)

Only allowed in one direction: Range → Trend. Checked on every 15-minute bar close after 09:00.

```csharp
// In OnBarUpdate(), when BarsInProgress == 1 (15-minute bar):
if (currentRegime == RegimeType.Range && adx15m[0] >= 25.0 && adx15m[1] < 22.0)
{
    // ADX surged from below 22 to above 25 — regime upgrade
    currentRegime = RegimeType.Trend;
    Print("REGIME UPGRADE: Range -> Trend at " + Times[1][0]);
}
```

### Regime State Tracking

```csharp
private RegimeType currentRegime = RegimeType.Unknown;
private bool regimeDecisionMade = false;
private DateTime regimeDecisionTime;

// Set once at 09:00 CT
if (!regimeDecisionMade && /* time check */)
{
    currentRegime = DetermineRegime();
    regimeDecisionMade = true;
    regimeDecisionTime = Times[0][0];
    Print("REGIME SET: " + currentRegime + " at " + regimeDecisionTime);
}
```

### Regime → Strategy Mapping

| Regime | Strategy A (LevelFade) | Strategy B (SweepReclaim) | Strategy C (VWAPReversal) |
|--------|----------------------|--------------------------|--------------------------|
| Unknown (before 09:00) | OFF | OFF | OFF |
| Range | ON (primary) | OFF | ON (conditional) |
| Trend | OFF | ON (primary) | ON (conditional) |
| Throttled | ON (1 trade max) | OFF | OFF |

---

## 4. Strategy A — ESLevelFadeV0 (Range Mode)

### Source Reference

**Existing file:** `ESLevelFadeV0.cs`  
**Spec document:** `ESLevelFadeV0_SPEC.md` (frozen — do not modify the spec)

### When Active

- `currentRegime == RegimeType.Range` OR `currentRegime == RegimeType.Throttled`
- During valid trade windows (see Section 8)

### Levels (Unchanged from Spec)

Only 4 levels, computed once per session:

```csharp
private double prevSessionHigh;   // From PriorDayOHLC or manual session tracking
private double prevSessionLow;
private double preMarketHigh;     // Tracked from 02:00–08:29:59 CT
private double preMarketLow;      // Frozen at 08:29:59 CT
```

**Level computation is identical to existing `ESLevelFadeV0.cs`.** Do not change the level logic.

### Entry Logic (Unchanged from Spec)

1. **Arming:** Level armed when price comes within `ArmDistanceAtr` (1.5) * ATR(14) of any level.
2. **Approach direction:** Determined from `Close[1]` relative to level:
   - Close[1] > level → approaching from above → level is support → LONG
   - Close[1] < level → approaching from below → level is resistance → SHORT
3. **Clustering:** If two levels are within `ClusterDistanceTicks` (18 ticks), pick the most conservative:
   - For longs: lowest level in cluster
   - For shorts: highest level in cluster
4. **Order:** Limit order at the level price.
5. **Shorts preferred** when both long and short candidates exist simultaneously.

### Exit Logic (Unchanged from Spec)

- **Stop:** Fixed 6 ticks ($75.00 per contract)
- **Target:** Fixed 18 ticks ($225.00 per contract) → 3:1 R:R
- **No breakeven** (spec deliberately excludes it)
- **Session close exit:** Flatten at 14:58 CT

### Modifications for Unified Plan

**These are the ONLY changes to ESLevelFadeV0 logic:**

#### Modification A1: Trade Window Enforcement

Replace the existing 08:30–15:00 window with two discrete windows:

```csharp
private bool IsInLevelFadeWindow()
{
    TimeSpan now = Times[0][0].TimeOfDay;  // UTC times — convert accordingly
    
    // Window 1: 09:00–10:30 CT (14:00–15:30 UTC)
    bool w1 = now >= new TimeSpan(14, 0, 0) && now <= new TimeSpan(15, 30, 0);
    
    // Window 2: 13:30–14:45 CT (18:30–19:45 UTC)
    bool w2 = now >= new TimeSpan(18, 30, 0) && now <= new TimeSpan(19, 45, 0);
    
    return w1 || w2;
}
```

#### Modification A2: Global Trade Count Check

Before placing any order, check:

```csharp
if (globalTradeCount >= MAX_DAILY_TRADES)  // MAX_DAILY_TRADES = 3
{
    // Do not place order
    return;
}
```

#### Modification A3: Max 2 Trades for Strategy A

```csharp
private int strategyATradeCount = 0;
private const int MAX_STRATEGY_A_TRADES = 2;

// Before entry:
if (strategyATradeCount >= MAX_STRATEGY_A_TRADES) return;

// After fill:
strategyATradeCount++;
globalTradeCount++;
```

#### Modification A4: Cluster Priority Enhancement

When levels cluster (within 18 ticks), add a log/tag to mark it as a **high-confidence zone**. This is informational only for now, but prepares for future weighting:

```csharp
if (isClusteredLevel)
{
    Print("HIGH-CONF ZONE: " + levelPrice + " (clustered with " + otherLevel + ")");
    // Future: could increase position size by 1 contract for clustered levels
}
```

### Parameters (Carry Forward from ESLevelFadeV0_SPEC.md)

| Parameter | Value | Notes |
|-----------|-------|-------|
| ArmDistanceAtr | 1.5 | ATR multiplier for arming range |
| ClusterDistanceTicks | 18 | Ticks within which levels merge |
| StopTicks | 6 | Fixed stop loss |
| TargetTicks | 18 | Fixed profit target |
| MaxTradesPerLevelPerSession | 1 | Do not retrade same level |
| ReentryExcursionAtr | 2.0 | ATR deviation before re-entry allowed |

---

## 5. Strategy B — ES_2m_SweepReclaim (Trend Mode)

### Source Reference

**Existing file:** `new_strategy/ES_2m_SweepReclaim.cs` (use the enhanced version, NOT the root version)  
**Baseline spec locked:** v2, 2026-03-09

### When Active

- `currentRegime == RegimeType.Trend`
- During valid trade windows (see Section 8)

### Key Indicators

```csharp
// Already present in new_strategy/ES_2m_SweepReclaim.cs:
private ATR atr2m;         // ATR(14) on 2-minute
private EMA ema15m;        // EMA(20) on 15-minute — for trend bias
private ADX adx15m;        // ADX(14) on 15-minute — already in new_strategy version
// Session VWAP: manually computed in new_strategy version (no indicator dependency)
```

### State Machine (Unchanged)

```
Idle → SweptLong → AwaitingLong → [Entry or Expired]
Idle → SweptShort → AwaitingShort → [Entry or Expired]
```

- **Idle:** Scanning for swing highs/lows
- **SweptLong:** Low broke below swing low - 1 tick. Watching for reclaim.
- **AwaitingLong:** Reclaim confirmed. Buy-stop armed at High + 1 tick.
- **Entry:** Buy-stop triggered. Position open.
- **Expired:** Setup not filled within 3 bars. Back to Idle.

### Entry Logic (Unchanged from new_strategy version)

1. **Bias:** 15m EMA(20) slope > `EmaSlopeThreshold` (0.5 pts) over 3 bars + price on correct side of VWAP. Both must agree.
2. **Whipsaw filter:** Max 3 VWAP crosses in 30-minute rolling window. If exceeded, no entries.
3. **ADX gate:** ADX(14) on 15m must be ≥ `AdxMin` (18). Already in new_strategy version.
4. **Sweep detection:**
   - Long: Current bar Low < swing low - 1 tick
   - Short: Current bar High > swing high + 1 tick
5. **Reclaim detection:**
   - Long: Close back above sweep level, body ≥ 0.25 * ATR, close rank ≥ 60%
   - Short: Close back below sweep level, body ≥ 0.25 * ATR, close rank ≥ 60%
6. **Entry arming:**
   - Long: Buy-stop at High + 1 tick
   - Short: Sell-stop at Low - 1 tick
7. **Setup score:** `MinSetupScore` = 5 (multi-factor scoring from new_strategy version)
8. **Expiry:** Setup expires after 3 bars if not filled.

### Exit Logic — MODIFIED

The baseline has no breakeven. **Add breakeven for the unified plan.**

```csharp
// --- Original exits (keep these) ---
// Stop: below sweep extreme (lowest low of last 5 bars) - 1 tick
// Clamped to [0.5, 1.5] * ATR
// Target: 3.0 * stop distance (fixed 3:1 R:R)

// --- NEW: Breakeven at 1.5R ---
private double entryPrice;
private double initialStopDistance;  // in points
private bool breakEvenApplied = false;

// In OnBarUpdate(), when position is open:
if (Position.MarketPosition != MarketPosition.Flat && !breakEvenApplied)
{
    double currentPnLPoints;
    if (Position.MarketPosition == MarketPosition.Long)
        currentPnLPoints = Close[0] - entryPrice;
    else
        currentPnLPoints = entryPrice - Close[0];

    // 1.5R = 1.5 * initial stop distance
    if (currentPnLPoints >= 1.5 * initialStopDistance)
    {
        double breakEvenPrice;
        if (Position.MarketPosition == MarketPosition.Long)
            breakEvenPrice = entryPrice + (1 * TickSize);  // 1 tick above entry
        else
            breakEvenPrice = entryPrice - (1 * TickSize);  // 1 tick below entry
        
        // Move stop to breakeven
        if (Position.MarketPosition == MarketPosition.Long)
            ExitLongStopMarket(0, true, Position.Quantity, breakEvenPrice, "BE_Stop", "SweepEntry");
        else
            ExitShortStopMarket(0, true, Position.Quantity, breakEvenPrice, "BE_Stop", "SweepEntry");
        
        breakEvenApplied = true;
        Print("BREAKEVEN APPLIED at " + breakEvenPrice + " (1.5R reached)");
    }
}
```

### Modifications for Unified Plan

#### Modification B1: Global Trade Count Check

Same as Strategy A — check `globalTradeCount >= 3` before entry.

#### Modification B2: Max 2 Trades for Strategy B

```csharp
private int strategyBTradeCount = 0;
private const int MAX_STRATEGY_B_TRADES = 2;
```

#### Modification B3: Breakeven at 1.5R

As shown above. This is the primary modification to the existing strategy.

#### Modification B4: VWAP Distance Filter

Already present in new_strategy version as `MaxEntryVwapDistanceAtr` (2.0). Keep this. If price is more than 2x ATR away from VWAP at entry, skip the trade — it's overextended.

#### Modification B5: News Blackout

Already present: 10 minutes each side of scheduled news. Keep this.

### Parameters

| Parameter | Value | Notes |
|-----------|-------|-------|
| EmaSlopeThreshold | 0.5 | Points over 3 bars |
| AdxMin | 18 | Minimum ADX for trend confirmation |
| MaxVwapCrosses | 3 | In 30-minute rolling window |
| MinReclaimBodyAtrRatio | 0.25 | Minimum body as fraction of ATR |
| MinCloseRank | 0.60 | 60% close rank for reclaim bar |
| StopLookback | 5 | Bars for swing extreme stop |
| StopAtrClampMin | 0.5 | Minimum stop as ATR multiple |
| StopAtrClampMax | 1.5 | Maximum stop as ATR multiple |
| TargetRR | 3.0 | Fixed risk-reward ratio |
| BreakEvenTriggerR | 1.5 | **NEW** — R-multiple to trigger breakeven |
| BreakEvenBufferTicks | 1 | **NEW** — ticks beyond entry for BE stop |
| MinSetupScore | 5 | Multi-factor scoring gate |
| MaxEntryVwapDistanceAtr | 2.0 | Max distance from VWAP at entry |
| SetupExpiryBars | 3 | Bars before armed setup expires |

---

## 6. Strategy C — VWAPReversal2m (Conditional Midday)

### Source Reference

**Existing file:** `VWAPReversal2m.cs` (v1.1, 2026-03-27)

### When Active

- ANY regime (Range or Trend) — this strategy works in both
- **Conditional:** Only fires if `globalTradeCount <= 1` at time of signal (never after 2+ trades already taken)
- **Window:** 10:30–13:00 CT only (fills the gap between morning and afternoon windows)
- **Never after two consecutive losses on the day**

### The 3-Bar Pattern (Unchanged)

This is the core edge. Do not modify the pattern detection.

```
Bar[2] (Touch Bar):     Bar crosses VWAP — High > VWAP AND Low < VWAP
Bar[1] (Signal Bar):    Does NOT cross VWAP. Clean separation.
                         Long: Close[1] > VWAP AND Low[1] within 2 ticks of VWAP
                         Short: Close[1] < VWAP AND High[1] within 2 ticks of VWAP
Bar[0] (Entry Bar):     Stop-market order placed:
                         Long: Buy-stop at High[1] + 1 tick
                         Short: Sell-stop at Low[1] - 1 tick
```

**Visual example (Long setup):**

```
         │     ← Bar[0]: Buy-stop triggered above signal bar high
    ┌────┤
    │    │     ← Bar[1] (Signal): Closes above VWAP, low near VWAP, doesn't cross
    │    └──
    │
════│══════════  ← VWAP line
    │
  ──┴────┐
         │     ← Bar[2] (Touch): Crosses through VWAP (high above, low below)
    ─────┘
```

### VWAP Sources

The strategy checks three VWAP lines independently:
1. **Session VWAP** (primary — highest priority)
2. **AVWAP1** (user-anchored via hotkey)
3. **AVWAP2** (user-anchored via hotkey)

When Session VWAP and an AVWAP give conflicting signals, Session VWAP wins.

### Entry Logic (Unchanged)

```csharp
// Pseudocode for long detection:
bool touchBar = High[2] > vwapValue && Low[2] < vwapValue;
bool signalBar = !(High[1] > vwapValue && Low[1] < vwapValue)  // doesn't cross
                 && Close[1] > vwapValue                         // closes above
                 && (Low[1] - vwapValue) <= 2 * TickSize;       // low within 2 ticks

if (touchBar && signalBar)
{
    // Place buy-stop at High[1] + 1 tick
    double entryPrice = High[1] + TickSize;
    EnterLongStopMarket(0, true, quantity, entryPrice, "VWAPRev_Long");
}
```

### Exit Logic (Unchanged)

- **Stop:** Signal bar range `(High[1] - Low[1])` in ticks + 2 tick buffer
  - **Skip if:** calculated stop > `MaxStopTicks` (8 ticks). Do not take the trade.
- **Target:** Stop ticks × `RiskRewardRatio` (2.0)
- **Breakeven:** At `BreakevenTicks` (6) in profit, stop moves to entry price

### Modifications for Unified Plan

#### Modification C1: Restricted Trade Window

Change from 08:30–15:00 to 10:30–13:00 CT only:

```csharp
private bool IsInVWAPReversalWindow()
{
    TimeSpan now = Times[0][0].TimeOfDay;
    // 10:30 CT = 15:30 UTC, 13:00 CT = 18:00 UTC
    return now >= new TimeSpan(15, 30, 0) && now <= new TimeSpan(18, 0, 0);
}
```

#### Modification C2: Conditional Activation

```csharp
private bool CanFireStrategyC()
{
    // Only if 0 or 1 trades taken so far today
    if (globalTradeCount > 1) return false;
    
    // Never after 2 consecutive losses today
    if (dailyConsecutiveLosses >= 2) return false;
    
    // Must be in the midday window
    if (!IsInVWAPReversalWindow()) return false;
    
    return true;
}
```

#### Modification C3: Max 1 Trade for Strategy C

```csharp
private int strategyCTradeCount = 0;
private const int MAX_STRATEGY_C_TRADES = 1;
```

#### Modification C4: Reduce Max Daily Trades Parameter

The existing code has `MaxTradesPerDay = 4`. Change to 1 for this strategy within the unified plan. The global cap of 3 is separate and also enforced.

### Parameters

| Parameter | Value | Notes |
|-----------|-------|-------|
| TouchToleranceTicks | 2 | Max distance from VWAP for signal bar |
| MaxStopTicks | 8 | Skip trade if stop exceeds this |
| StopBufferTicks | 2 | Added to signal bar range for stop |
| RiskRewardRatio | 2.0 | Target = stop × this |
| BreakevenTicks | 6 | Profit ticks to trigger BE |
| CooldownBars | 3 | Min bars between entries |
| MaxTradesPerDay | 1 | **Changed from 4 to 1** |
| MaxDailyLoss | -600 | Per-strategy (global $400 cap is tighter) |

---

## 7. Global Risk Manager

### Purpose

Enforces risk limits that span all three strategies. This is the final gate — even if a sub-strategy says "go," the risk manager can say "no."

### Implementation

```csharp
public class GlobalRiskManager
{
    // --- Configuration ---
    private const int MAX_DAILY_TRADES = 3;
    private const double MAX_DAILY_LOSS = -400.0;          // dollars
    private const int MAX_CONSECUTIVE_LOSING_DAYS = 2;
    private const int THROTTLED_MAX_TRADES = 1;
    
    // --- State ---
    private int totalTradesToday = 0;
    private double realizedPnLToday = 0.0;
    private int consecutiveLossesToday = 0;
    private int consecutiveLosingDays = 0;          // persisted across sessions
    private List<double> tradeResults = new List<double>();  // P&L per trade today
    
    // --- Trade Permission ---
    public bool CanOpenNewTrade()
    {
        int maxTrades = (consecutiveLosingDays >= MAX_CONSECUTIVE_LOSING_DAYS) 
                        ? THROTTLED_MAX_TRADES 
                        : MAX_DAILY_TRADES;
        
        if (totalTradesToday >= maxTrades) return false;
        if (realizedPnLToday <= MAX_DAILY_LOSS) return false;
        return true;
    }
    
    // --- Called After Every Trade Closes ---
    public void RecordTradeResult(double pnl, string strategyId)
    {
        totalTradesToday++;
        realizedPnLToday += pnl;
        tradeResults.Add(pnl);
        
        if (pnl < 0)
            consecutiveLossesToday++;
        else
            consecutiveLossesToday = 0;
        
        Print(string.Format("RISK MGR: Trade #{0} ({1}): {2:C2} | Day P&L: {3:C2} | Consec Losses: {4}",
            totalTradesToday, strategyId, pnl, realizedPnLToday, consecutiveLossesToday));
        
        // Check if we've hit the daily loss limit
        if (realizedPnLToday <= MAX_DAILY_LOSS)
        {
            Print("RISK MGR: DAILY LOSS LIMIT HIT. No more trades today.");
        }
    }
    
    // --- Called at Session Open ---
    public void OnSessionOpen(double priorDayPnL)
    {
        // Track consecutive losing days
        if (priorDayPnL < 0)
            consecutiveLosingDays++;
        else
            consecutiveLosingDays = 0;
        
        // Reset daily counters
        totalTradesToday = 0;
        realizedPnLToday = 0;
        consecutiveLossesToday = 0;
        tradeResults.Clear();
        
        Print("RISK MGR: Session open. Consecutive losing days: " + consecutiveLosingDays);
        if (consecutiveLosingDays >= MAX_CONSECUTIVE_LOSING_DAYS)
            Print("RISK MGR: THROTTLED MODE — max 1 trade today.");
    }
    
    // --- Properties ---
    public int TradesToday => totalTradesToday;
    public double PnLToday => realizedPnLToday;
    public int ConsecutiveLossesToday => consecutiveLossesToday;
    public bool IsThrottled => consecutiveLosingDays >= MAX_CONSECUTIVE_LOSING_DAYS;
}
```

### Consecutive Losing Days Persistence

NinjaTrader strategies reset state between sessions. To persist `consecutiveLosingDays`, write it to a file at session close and read it at session open:

```csharp
private string stateFilePath = NinjaTrader.Core.Globals.UserDataDir + @"ESUnifiedState.json";

private void SaveState()
{
    var state = new
    {
        ConsecutiveLosingDays = riskManager.ConsecutiveLosingDays,
        LastSessionPnL = riskManager.PnLToday,
        LastSessionDate = DateTime.Now.ToString("yyyy-MM-dd")
    };
    string json = Newtonsoft.Json.JsonConvert.SerializeObject(state);
    System.IO.File.WriteAllText(stateFilePath, json);
}

private void LoadState()
{
    if (System.IO.File.Exists(stateFilePath))
    {
        string json = System.IO.File.ReadAllText(stateFilePath);
        dynamic state = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
        // Use state.ConsecutiveLosingDays, state.LastSessionPnL, etc.
    }
}
```

### Drawdown Scenarios

| Scenario | Trades | Result | Day P&L | Action |
|----------|--------|--------|---------|--------|
| 3 winners (3:1 RR, 6t stop) | 3 | +$675 | +$675 | Great day, done |
| 2 wins + 1 loss | 3 | +$375 | +$375 | Done for the day |
| 1 win + 2 losses | 3 | +$75 | +$75 | Done for the day |
| 3 losses (6t stop each) | 3 | -$225 | -$225 | Under limit, but done (3 trade cap) |
| 2 losses (larger stops from Strategy B) | 2 | -$375 | -$375 | Under limit, 1 trade left |
| 2 losses + 1 more loss | 3 | -$412 | -$400 cap | Hit limit mid-trade, flatten |
| 1 loss + daily cap proximity | 1 | -$188 | -$188 | 2 trades left but budget is $212 |

---

## 8. Trade Windows & Scheduling

### Daily Timeline

```
Time (CT)     Event
─────────     ─────
08:30         RTH open. Pre-market levels frozen.
08:30–09:00   NO TRADING. Regime observation period.
09:00         Regime decision made (Range / Trend / Throttled).
09:00–10:30   WINDOW 1: Strategy A (Range) or Strategy B (Trend)
10:30–13:00   WINDOW MID: Strategy C (VWAPReversal) — conditional
13:30–14:45   WINDOW 2: Strategy A (Range) or Strategy B (Trend)
14:45         All new entries disabled.
14:58         Flatten any open position.
15:00         RTH close.
15:10         Session state saved to file.
```

### Window Implementation

```csharp
private enum TradeWindow
{
    None,
    Window1,        // 09:00–10:30 CT
    WindowMid,      // 10:30–13:00 CT
    Window2         // 13:30–14:45 CT
}

private TradeWindow GetCurrentWindow()
{
    TimeSpan now = Times[0][0].TimeOfDay;  // Note: these are UTC
    
    // Convert CT to UTC: CT = UTC - 5 (CDT) or UTC - 6 (CST)
    // Use NinjaTrader's TradingHours for robust conversion
    // Below assumes CDT (UTC-5):
    
    // 09:00 CT = 14:00 UTC
    // 10:30 CT = 15:30 UTC
    // 13:00 CT = 18:00 UTC
    // 13:30 CT = 18:30 UTC
    // 14:45 CT = 19:45 UTC
    
    if (now >= new TimeSpan(14, 0, 0) && now <= new TimeSpan(15, 30, 0))
        return TradeWindow.Window1;
    
    if (now >= new TimeSpan(15, 30, 0) && now <= new TimeSpan(18, 0, 0))
        return TradeWindow.WindowMid;
    
    if (now >= new TimeSpan(18, 30, 0) && now <= new TimeSpan(19, 45, 0))
        return TradeWindow.Window2;
    
    return TradeWindow.None;
}
```

**Important:** Use NinjaTrader's `TradingHours` object or `ToTime()` helper for timezone-safe comparisons. The UTC offsets above assume CDT. During CST (November–March), all UTC times shift by 1 hour. Recommend using:

```csharp
int timeNow = ToTime(Times[0][0]);
// 09:00 CT → ToTime format: 90000
// 10:30 CT → 103000
// etc.
```

### Window Rules

| Window | Strategies Active | Max Trades in Window | Notes |
|--------|------------------|---------------------|-------|
| W1 (09:00–10:30) | A or B (per regime) | 1 from primary strategy | First opportunity of the day |
| Mid (10:30–13:00) | C only | 1 (conditional) | Only if ≤1 trades taken AND no 2 consecutive losses |
| W2 (13:30–14:45) | A or B (per regime) | 1 from primary strategy | Second opportunity |
| None | — | 0 | No entries outside windows |

---

## 9. Order Management & Position Tracking

### Position State

The orchestrator must track the state of each sub-strategy's orders independently to avoid conflicts.

```csharp
private enum PositionState
{
    Flat,           // No position, no working orders
    OrderPending,   // Limit or stop order working
    InPosition,     // Filled, managing exit
    ExitPending     // Exit order working, awaiting fill
}

private PositionState strategyAState = PositionState.Flat;
private PositionState strategyBState = PositionState.Flat;
private PositionState strategyCState = PositionState.Flat;
```

### Critical Rule: One Position at a Time

**Never have two positions open simultaneously.** If Strategy A has a pending limit order and Strategy B gets a sweep-reclaim signal, Strategy B must wait. The orchestrator enforces this:

```csharp
private bool IsAnyPositionActive()
{
    return strategyAState != PositionState.Flat
        || strategyBState != PositionState.Flat
        || strategyCState != PositionState.Flat;
}

// Before any sub-strategy places an order:
if (IsAnyPositionActive())
{
    Print("ORDER BLOCKED: Another strategy has an active position/order.");
    return;
}
```

### Order Naming Convention

Use consistent order names so `OnExecutionUpdate` and `OnOrderUpdate` can identify which sub-strategy owns each order:

```csharp
// Strategy A (LevelFade)
private const string ENTRY_A = "LF_Entry";
private const string STOP_A  = "LF_Stop";
private const string TARGET_A = "LF_Target";

// Strategy B (SweepReclaim)
private const string ENTRY_B = "SR_Entry";
private const string STOP_B  = "SR_Stop";
private const string TARGET_B = "SR_Target";
private const string BE_STOP_B = "SR_BEStop";

// Strategy C (VWAPReversal)
private const string ENTRY_C = "VR_Entry";
private const string STOP_C  = "VR_Stop";
private const string TARGET_C = "VR_Target";
```

### Trade Result Tracking

```csharp
protected override void OnExecutionUpdate(Execution execution, string executionId, 
    double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
{
    // Detect trade closure (position goes flat)
    if (Position.MarketPosition == MarketPosition.Flat && execution.Order.OrderState == OrderState.Filled)
    {
        // Calculate P&L from the completed trade
        double tradePnL = Performance.AllTrades.Count > 0 
            ? Performance.AllTrades[Performance.AllTrades.Count - 1].ProfitCurrency 
            : 0;
        
        // Determine which strategy this trade belonged to
        string strategyId = "Unknown";
        if (execution.Order.Name.StartsWith("LF_")) strategyId = "A_LevelFade";
        else if (execution.Order.Name.StartsWith("SR_")) strategyId = "B_SweepReclaim";
        else if (execution.Order.Name.StartsWith("VR_")) strategyId = "C_VWAPReversal";
        
        riskManager.RecordTradeResult(tradePnL, strategyId);
        
        // Reset position states
        ResetStrategyState(strategyId);
    }
}
```

### Unfilled Order Cancellation

If a limit/stop entry order is not filled by the end of its window, cancel it:

```csharp
// Check on every bar:
if (strategyAState == PositionState.OrderPending && GetCurrentWindow() != TradeWindow.Window1 
    && GetCurrentWindow() != TradeWindow.Window2)
{
    CancelOrder(pendingStrategyAOrder);
    strategyAState = PositionState.Flat;
    Print("Strategy A order cancelled — outside trade window.");
}
```

---

## 10. Shared Utilities & Base Class

### ATR Helper

Used by all three strategies. Compute once, share the value.

```csharp
private ATR atr2m;  // ATR(14) on 2-minute bars

protected override void OnStateChange()
{
    if (State == State.DataLoaded)
    {
        atr2m = ATR(BarsArray[0], 14);
    }
}

// Access: atr2m[0] for current ATR value
```

### Session Level Computation

Used by Strategy A. Compute once at session start.

```csharp
private double prevSessionHigh, prevSessionLow;
private double preMarketHigh, preMarketLow;
private bool levelsFrozen = false;

// Previous session levels: use PriorDayOHLC indicator or manual tracking
// Pre-market tracking: track high/low from overnight session
// Freeze at 08:29:59 CT

private void UpdatePreMarketLevels()
{
    if (levelsFrozen) return;
    
    TimeSpan now = Times[0][0].TimeOfDay;
    // Freeze at 08:30 CT (13:30 UTC for CDT)
    if (now >= new TimeSpan(13, 30, 0))
    {
        levelsFrozen = true;
        Print("PRE-MARKET LEVELS FROZEN: High=" + preMarketHigh + " Low=" + preMarketLow);
        return;
    }
    
    // Track running high/low during pre-market
    if (High[0] > preMarketHigh) preMarketHigh = High[0];
    if (Low[0] < preMarketLow) preMarketLow = Low[0];
}
```

### VWAP Computation

Used by Strategies B and C. The `new_strategy/ES_2m_SweepReclaim.cs` computes VWAP manually (no indicator dependency). Use the same approach:

```csharp
private double vwapNumerator = 0;
private double vwapDenominator = 0;
private double sessionVWAP = 0;

private void UpdateSessionVWAP()
{
    double typicalPrice = (High[0] + Low[0] + Close[0]) / 3.0;
    double barVolume = Volume[0];
    
    vwapNumerator += typicalPrice * barVolume;
    vwapDenominator += barVolume;
    
    if (vwapDenominator > 0)
        sessionVWAP = vwapNumerator / vwapDenominator;
}

// Reset at session start:
private void ResetVWAP()
{
    vwapNumerator = 0;
    vwapDenominator = 0;
    sessionVWAP = 0;
}
```

### Swing Detection

Used by Strategy B (for sweep detection). Reuse the swing logic from `new_strategy/ES_2m_SweepReclaim.cs`:

```csharp
private double recentSwingLow = double.MaxValue;
private double recentSwingHigh = double.MinValue;
private int swingStrength = 3;  // bars on each side

private void UpdateSwings()
{
    // Check for swing low: bar[swingStrength] is lower than all bars within swingStrength on each side
    if (CurrentBar < swingStrength * 2) return;
    
    int pivotBar = swingStrength;
    double pivotLow = Low[pivotBar];
    bool isSwingLow = true;
    
    for (int i = 0; i < swingStrength; i++)
    {
        if (Low[i] <= pivotLow || Low[pivotBar + 1 + i] <= pivotLow)
        {
            isSwingLow = false;
            break;
        }
    }
    
    if (isSwingLow)
        recentSwingLow = pivotLow;
    
    // Mirror logic for swing high...
}
```

### Logging

All strategies should use a consistent log format for post-session analysis:

```csharp
private void LogTrade(string strategyId, string action, double price, string reason)
{
    string timestamp = Times[0][0].ToString("HH:mm:ss");
    string message = string.Format("[{0}] {1} | {2} @ {3:F2} | {4} | Regime: {5} | Trades: {6} | PnL: {7:C2}",
        timestamp, strategyId, action, price, reason, currentRegime, 
        riskManager.TradesToday, riskManager.PnLToday);
    Print(message);
}

// Usage:
LogTrade("A_LevelFade", "ENTRY_LONG", fillPrice, "Fade PrevSessionLow at 5845.50");
LogTrade("B_SweepReclaim", "STOP_HIT", stopPrice, "Stopped out -6 ticks");
LogTrade("C_VWAPReversal", "TARGET_HIT", targetPrice, "Target hit +16 ticks");
```

---

## 11. Modifications to Existing Strategies

### Summary of Changes per File

#### `ESLevelFadeV0.cs` → Extract into `ESUnifiedPlan.cs`

| Change | What | Why |
|--------|------|-----|
| Trade windows | Replace 08:30–15:00 with W1 (09:00–10:30) + W2 (13:30–14:45) | Avoid first 30 min noise, align with regime filter |
| Trade count | Add global trade count check | Enforce 3-trade daily cap |
| Strategy cap | Max 2 trades for this strategy | Leave room for Strategy C |
| Regime gate | Only active when `regime == Range \|\| regime == Throttled` | Don't fade levels on trend days |
| Cluster tagging | Log clustered levels as high-confidence | Informational for now |

**Do NOT change:** Level computation, approach direction logic, stop/target ticks, clustering logic, excursion filter. These are frozen per spec.

#### `new_strategy/ES_2m_SweepReclaim.cs` → Extract into `ESUnifiedPlan.cs`

| Change | What | Why |
|--------|------|-----|
| Breakeven | Add BE at 1.5R (entry + 1 tick for stop) | Minimize drawdown |
| Trade count | Add global trade count check | Enforce 3-trade daily cap |
| Strategy cap | Max 2 trades for this strategy | Leave room for Strategy C |
| Regime gate | Only active when `regime == Trend` | Don't trade sweeps in range days |

**Do NOT change:** State machine, sweep detection, reclaim conditions, bias logic, ADX filter, setup scoring, window definitions.

#### `VWAPReversal2m.cs` → Extract into `ESUnifiedPlan.cs`

| Change | What | Why |
|--------|------|-----|
| Trade window | Change to 10:30–13:00 CT only | Fill the midday gap |
| Activation condition | Only if ≤1 trades + no 2 consecutive losses | Conditional "bonus" trade |
| Max trades | Change from 4 to 1 per day | One midday trade only |
| Strategy cap | Max 1 trade | Selective use |

**Do NOT change:** 3-bar pattern detection, stop calculation, target calculation, breakeven at 6 ticks, VWAP priority logic.

---

## 12. NinjaTrader Implementation Details

### File Structure

```
ESUnifiedPlan.cs           ← Main strategy file (Option A)
ESUnifiedState.json        ← Persisted state (consecutive losing days, etc.)
                              Location: NinjaTrader.Core.Globals.UserDataDir
```

### Strategy Properties

```csharp
[NinjaScriptProperty]
[Display(Name = "Max Daily Trades", GroupName = "Global Risk", Order = 1)]
public int MaxDailyTrades { get; set; } = 3;

[NinjaScriptProperty]
[Display(Name = "Max Daily Loss ($)", GroupName = "Global Risk", Order = 2)]
public double MaxDailyLoss { get; set; } = 400;

[NinjaScriptProperty]
[Display(Name = "Max Consecutive Losing Days", GroupName = "Global Risk", Order = 3)]
public int MaxConsecutiveLosingDays { get; set; } = 2;

// --- Strategy A Parameters ---
[NinjaScriptProperty]
[Display(Name = "LF: Stop Ticks", GroupName = "Strategy A: LevelFade", Order = 1)]
public int LF_StopTicks { get; set; } = 6;

[NinjaScriptProperty]
[Display(Name = "LF: Target Ticks", GroupName = "Strategy A: LevelFade", Order = 2)]
public int LF_TargetTicks { get; set; } = 18;

[NinjaScriptProperty]
[Display(Name = "LF: Arm Distance ATR", GroupName = "Strategy A: LevelFade", Order = 3)]
public double LF_ArmDistanceAtr { get; set; } = 1.5;

[NinjaScriptProperty]
[Display(Name = "LF: Cluster Ticks", GroupName = "Strategy A: LevelFade", Order = 4)]
public int LF_ClusterDistanceTicks { get; set; } = 18;

// --- Strategy B Parameters ---
[NinjaScriptProperty]
[Display(Name = "SR: ADX Min", GroupName = "Strategy B: SweepReclaim", Order = 1)]
public double SR_AdxMin { get; set; } = 18;

[NinjaScriptProperty]
[Display(Name = "SR: Target RR", GroupName = "Strategy B: SweepReclaim", Order = 2)]
public double SR_TargetRR { get; set; } = 3.0;

[NinjaScriptProperty]
[Display(Name = "SR: BE Trigger R", GroupName = "Strategy B: SweepReclaim", Order = 3)]
public double SR_BreakEvenTriggerR { get; set; } = 1.5;

[NinjaScriptProperty]
[Display(Name = "SR: Min Setup Score", GroupName = "Strategy B: SweepReclaim", Order = 4)]
public int SR_MinSetupScore { get; set; } = 5;

// --- Strategy C Parameters ---
[NinjaScriptProperty]
[Display(Name = "VR: Max Stop Ticks", GroupName = "Strategy C: VWAPReversal", Order = 1)]
public int VR_MaxStopTicks { get; set; } = 8;

[NinjaScriptProperty]
[Display(Name = "VR: RR Ratio", GroupName = "Strategy C: VWAPReversal", Order = 2)]
public double VR_RiskRewardRatio { get; set; } = 2.0;

[NinjaScriptProperty]
[Display(Name = "VR: BE Ticks", GroupName = "Strategy C: VWAPReversal", Order = 3)]
public int VR_BreakevenTicks { get; set; } = 6;
```

### OnStateChange Setup

```csharp
protected override void OnStateChange()
{
    if (State == State.SetDefaults)
    {
        Description = "Unified ES Trading Plan: LevelFade + SweepReclaim + VWAPReversal";
        Name = "ESUnifiedPlan";
        Calculate = Calculate.OnBarClose;
        EntriesPerDirection = 1;
        EntryHandling = EntryHandling.AllEntries;
        IsExitOnSessionCloseStrategy = true;
        ExitOnSessionCloseSeconds = 120;  // 2 minutes before close
        IsFillLimitOnTouch = false;
        MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
        OrderFillResolution = OrderFillResolution.Standard;
        Slippage = 1;
        StartBehavior = StartBehavior.WaitUntilFlat;
        TimeInForce = TimeInForce.Day;
        TraceOrders = true;
        RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
        StopTargetHandling = StopTargetHandling.PerEntryExecution;
        BarsRequiredToTrade = 20;
        IsInstantiatedOnEachOptimizationIteration = true;
    }
    
    if (State == State.Configure)
    {
        // Add 15-minute data series for regime filter and EMA/ADX
        AddDataSeries(BarsPeriodType.Minute, 15);  // BarsArray[1]
    }
    
    if (State == State.DataLoaded)
    {
        atr2m = ATR(BarsArray[0], 14);
        adx15m = ADX(BarsArray[1], 14);
        ema15m = EMA(BarsArray[1], 20);
        priorOHLC = PriorDayOHLC(BarsArray[0]);
        
        riskManager = new GlobalRiskManager();
        LoadState();
    }
    
    if (State == State.Terminated)
    {
        SaveState();
    }
}
```

### OnBarUpdate Main Loop

```csharp
protected override void OnBarUpdate()
{
    // --- Multi-series routing ---
    if (BarsInProgress == 1)
    {
        // 15-minute bar closed
        CheckRegimeUpgrade();
        return;
    }
    
    if (BarsInProgress != 0) return;  // Only process primary 2-minute bars below
    
    // --- Guard: minimum bars ---
    if (CurrentBar < BarsRequiredToTrade) return;
    
    // --- Session Management ---
    UpdatePreMarketLevels();
    UpdateSessionVWAP();
    UpdateSwings();
    
    // --- Regime Decision (once at 09:00 CT) ---
    if (!regimeDecisionMade)
        TrySetRegime();
    
    // --- Position Management (if in a trade) ---
    if (Position.MarketPosition != MarketPosition.Flat)
    {
        ManageOpenPosition();
        return;  // Don't scan for new entries while in a position
    }
    
    // --- Flatten check ---
    if (IsPastTradingHours()) return;
    
    // --- Risk check ---
    if (!riskManager.CanOpenNewTrade()) return;
    
    // --- Window + Regime routing ---
    TradeWindow window = GetCurrentWindow();
    
    switch (window)
    {
        case TradeWindow.Window1:
        case TradeWindow.Window2:
            if (currentRegime == RegimeType.Range || currentRegime == RegimeType.Throttled)
                ScanStrategyA_LevelFade();
            else if (currentRegime == RegimeType.Trend)
                ScanStrategyB_SweepReclaim();
            break;
        
        case TradeWindow.WindowMid:
            if (CanFireStrategyC())
                ScanStrategyC_VWAPReversal();
            break;
        
        case TradeWindow.None:
            CancelAnyPendingOrders();
            break;
    }
}
```

### ManageOpenPosition

```csharp
private void ManageOpenPosition()
{
    // Identify which strategy owns the current position
    if (strategyAState == PositionState.InPosition)
    {
        ManageStrategyA_Exit();
    }
    else if (strategyBState == PositionState.InPosition)
    {
        ManageStrategyB_Exit();
    }
    else if (strategyCState == PositionState.InPosition)
    {
        ManageStrategyC_Exit();
    }
    
    // Emergency flatten at 14:58 CT
    if (IsFlattenTime())
    {
        if (Position.MarketPosition == MarketPosition.Long)
            ExitLong("EmergencyFlatten", "");
        else if (Position.MarketPosition == MarketPosition.Short)
            ExitShort("EmergencyFlatten", "");
    }
}
```

---

## 13. Backtesting Plan

### Phase 1: Individual Strategy Validation

Before testing the unified plan, validate each sub-strategy independently.

| Test | Data Range | Expected Result |
|------|-----------|-----------------|
| Strategy A (LevelFade) on range days only | 6 months, filtered for ADX<20 | Win rate >35%, Profit Factor >1.2 |
| Strategy B (SweepReclaim) on trend days only | 6 months, filtered for ADX>20 | Win rate >30%, Profit Factor >1.3 |
| Strategy C (VWAPReversal) all days | 6 months, unrestricted | Win rate >40%, Profit Factor >1.1 |

### Phase 2: Unified Plan Backtesting

| Test | Metric | Target |
|------|--------|--------|
| Avg trades per day | — | 1.5–2.5 |
| Win rate | All strategies combined | >35% |
| Profit factor | — | >1.5 |
| Max consecutive losses | — | <6 |
| Max drawdown (peak-to-trough) | — | <$2,000 |
| Max single-day loss | — | <$400 (enforced) |
| Sharpe ratio | Annualized | >1.0 |
| Average winning trade | — | >$180 |
| Average losing trade | — | <$120 |

### Phase 3: Paper Trading

- Run the unified strategy in **Sim101** for 2 full weeks minimum (10 trading days).
- Track every regime decision, every trade, every skip.
- Review daily:
  - Was the regime call correct?
  - Did any strategy fire when it shouldn't have?
  - Were there missed opportunities outside the windows?

### Backtesting Caveats

1. **Use OnBarClose** for all backtests. The strategy is designed for OnBarClose.
2. **Set slippage to 1 tick** ($12.50 per side on ES).
3. **Commission:** Use your actual broker commission (typically $2.09/side for NinjaTrader).
4. **Do NOT optimize parameters to the backtest.** The parameters in this plan are structural, not curve-fitted. If backtesting shows the edge is not there, the strategy concept needs rethinking — not the parameters.

---

## 14. Deployment Checklist

### Pre-Live Checklist

- [ ] All three sub-strategies pass individual backtests (Phase 1 targets met)
- [ ] Unified plan backtest meets Phase 2 targets
- [ ] 10+ days paper trading completed with daily review
- [ ] Regime filter accuracy >70% (correct call on range vs. trend days)
- [ ] State persistence file (`ESUnifiedState.json`) tested across session restarts
- [ ] Emergency flatten at 14:58 CT confirmed working
- [ ] Daily loss cap ($400) confirmed working — triggers mid-trade if needed
- [ ] Consecutive losing day throttle tested (reduce to 1 trade)
- [ ] All order names unique and correctly routed in `OnExecutionUpdate`
- [ ] No orphaned orders after session close
- [ ] Log output is clean and includes all fields for post-analysis
- [ ] Strategy runs on Sim101 with zero errors for 5 consecutive sessions

### Go-Live Steps

1. Apply strategy to a live chart (ES 2-minute, RTH session template).
2. Set `Account` to live account.
3. Verify `TraceOrders = true` for the first week.
4. Start with 1 contract per trade (regardless of position sizing calculations).
5. After 20+ live trades with positive expectancy, consider increasing to calculated size.

### Post-Live Monitoring

- Review `NinjaTrader Output` window daily for log messages.
- Track weekly:
  - Win rate by strategy (A, B, C)
  - Regime accuracy (was it actually a range/trend day?)
  - Trades skipped vs. trades taken (are we being too selective or not enough?)
  - Max drawdown progression

---

## 15. File Reference Map

### Files to READ (Existing Code to Port)

| File | What to Extract | For |
|------|----------------|-----|
| `ESLevelFadeV0.cs` | Level computation, arming, approach direction, clustering, limit entry, fixed stop/target | Strategy A |
| `ESLevelFadeV0_SPEC.md` | Frozen parameter values, design decisions, deliberate omissions (no BE) | Strategy A |
| `new_strategy/ES_2m_SweepReclaim.cs` | State machine, sweep detection, reclaim conditions, bias logic, ADX filter, setup scoring, manual VWAP | Strategy B |
| `VWAPReversal2m.cs` | 3-bar pattern detection, VWAP source priority, stop/target calculation, breakeven | Strategy C |
| `ESVwapLite.cs` | Multi-anchor VWAP infrastructure, zone clustering, anchor cooldown (reference only — not porting the full strategy) | Shared Utils |
| `ESTrendline_v1.cs` | Swing detection algorithm (fractal lookback), can reuse for Strategy B's swing detection | Shared Utils |

### Files to IGNORE (Not Part of This Plan)

All ATR Quartile variants, all AVWAP variants (except VWAPReversal2m), all Pivot variants, all GK variants, EMA/SMA pullback strategies, MTF scalp strategies, RevAVWAP variants, ESStructureAnchorAVWAP (has critical bugs). These remain in the repo as the template pool but are not part of the unified plan.

### File to CREATE

| File | Purpose |
|------|---------|
| `ESUnifiedPlan.cs` | The single orchestrator strategy containing all logic |
| `ESUnifiedState.json` | Runtime state persistence (auto-generated at `UserDataDir`) |

---

## Appendix A: Quick Reference Card

Print this and keep it next to your trading screen.

```
┌──────────────────────────────────────────────────────────────┐
│                    ES UNIFIED PLAN v1.0                       │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  08:30         Market opens. Watch. Don't trade.             │
│  09:00         REGIME CALL: ADX(15m) > 20 → Trend            │
│                              ADX(15m) < 20 → Range            │
│                              3+ VWAP crosses → Range override │
│                                                              │
│  RANGE DAY (Strategy A — Level Fade):                        │
│    • Fade PrevSession H/L + PreMarket H/L                    │
│    • Limit at level, 6t stop, 18t target (3:1)               │
│    • Max 2 trades: one per window                            │
│                                                              │
│  TREND DAY (Strategy B — Sweep & Reclaim):                   │
│    • Wait for sweep below swing low / above swing high       │
│    • Enter on reclaim (stop-market)                          │
│    • ATR stop clamped [0.5, 1.5] × ATR, target = 3× stop    │
│    • Breakeven at 1.5R                                       │
│    • Max 2 trades: one per window                            │
│                                                              │
│  MIDDAY (Strategy C — VWAP Reversal, conditional):           │
│    • 3-bar pattern: touch → signal → entry                   │
│    • Stop = signal bar range + 2t, target = 2× stop          │
│    • Only if ≤1 trades so far, no 2 consecutive losses       │
│    • Max 1 trade                                             │
│                                                              │
│  WINDOWS:  W1: 09:00–10:30  |  Mid: 10:30–13:00             │
│            W2: 13:30–14:45  |  Flatten: 14:58                │
│                                                              │
│  HARD LIMITS:  3 trades/day  |  $400 daily loss cap          │
│                2 losing days → throttle to 1 trade           │
│                                                              │
└──────────────────────────────────────────────────────────────┘
```

---

## Appendix B: Decision Flowchart

```
START (09:00 CT)
│
├─ Is it throttled? (2+ consecutive losing days)
│   └─ YES → Strategy A only, max 1 trade → END
│
├─ ADX(15m) > 20?
│   ├─ YES → TREND DAY
│   │   ├─ W1 (09:00-10:30): Strategy B — scan for sweeps
│   │   ├─ Mid (10:30-13:00): Strategy C — if ≤1 trades
│   │   └─ W2 (13:30-14:45): Strategy B — scan for sweeps
│   │
│   └─ NO → check VWAP crosses
│       ├─ 3+ crosses in first 30 min → RANGE DAY (confirmed)
│       └─ <3 crosses → RANGE DAY (default)
│           ├─ W1 (09:00-10:30): Strategy A — fade levels
│           ├─ Mid (10:30-13:00): Strategy C — if ≤1 trades
│           └─ W2 (13:30-14:45): Strategy A — fade levels
│
├─ Before ANY trade → check riskManager.CanOpenNewTrade()
│   ├─ NO → skip, wait for next window
│   └─ YES → execute strategy logic
│
├─ After EVERY trade close → riskManager.RecordTradeResult()
│   ├─ Daily loss ≤ -$400? → DONE for day
│   ├─ 3 trades taken? → DONE for day
│   └─ Continue to next window
│
└─ 14:58 CT → Flatten everything → Save state → END
```

---

## Appendix C: Estimated Performance Envelope

These are NOT predictions. They are mathematical scenarios based on the strategy parameters.

### Per-Trade P&L (1 Contract)

| Strategy | Win | Loss |
|----------|-----|------|
| A (LevelFade) | +$225 (18t) | -$75 (6t) |
| B (SweepReclaim, avg ATR=2.5pts) | +$375 (30t at 3:1) | -$125 (10t) |
| B (SweepReclaim, low ATR=1.5pts) | +$225 (18t at 3:1) | -$75 (6t) |
| B (SweepReclaim, high ATR=3.5pts) | +$525 (42t at 3:1) | -$175 (14t) |
| C (VWAPReversal, avg 6t stop) | +$150 (12t at 2:1) | -$75 (6t) |

### Daily Scenarios (1 Contract)

| Day Type | Trades | Outcomes | Day P&L |
|----------|--------|----------|---------|
| Great range day | 3 (2A + 1C) | 2W + 1L | +$525 |
| Decent range day | 2 (2A) | 1W + 1L | +$150 |
| Bad range day | 2 (2A) | 0W + 2L | -$150 |
| Great trend day | 3 (2B + 1C) | 2W + 1L | +$675 |
| Decent trend day | 2 (2B) | 1W + 1L | +$250 |
| Bad trend day | 2 (2B) | 0W + 2L | -$250 |
| Worst case (capped) | 3 | 0W + 3L | -$400 (capped) |

### Monthly Projection (20 Trading Days)

At 35% win rate, ~2 trades/day:

- Expected wins: 14
- Expected losses: 26
- Avg win (blended): ~$250
- Avg loss (blended): ~$100
- Expected monthly P&L: (14 × $250) - (26 × $100) = **+$900/month** per contract

This is conservative. At 40% win rate: **+$1,400/month** per contract.

---

**End of Implementation Specification**

*This document should be treated as the single source of truth for the ESUnifiedPlan implementation. Any deviations from this plan should be discussed and documented before coding.*

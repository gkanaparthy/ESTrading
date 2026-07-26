# ES Trendline Strategy — NinjaTrader Implementation Plan (v2)
**Strategy Name:** `ESTrendline_v1`  
**File:** `ESTrendline_v1.cs`  
**Timeframe:** 2-Minute bars, ES futures (Globex session data, RTH entries only)  
**Based on:** Tori Trades trendline methodology — adapted for intraday  
**Status:** 📋 Step 2 — Implementation Plan (Risk-First Revision)  

---

## 🔒 TRANSCRIPT-LOCKED NON-NEGOTIABLES (added from provided YouTube notes)

These are now explicit constraints for implementation and future patches.

1. **Top-Down Chaining is mandatory**
   - For our intraday adaptation, base chain is: **Daily → 4H → 1H → 2M execution** (instead of full Monthly→... ladder).
   - Each lower-timeframe line must connect from the previous higher-timeframe line’s latest valid **Point B**.

2. **Action/Safety assignment is event-driven**
   - Before break: lines are just structural up/down lines.
   - On break:
     - **Action line = broken line**
     - **Safety line = opposing line**
   - On bounce continuation: treat the bounced structure as primary risk reference for stop placement logic.

3. **Continuity over clutter**
   - Trendlines must form a connected structural narrative (A→B, then B→C, then C→D... where valid).
   - Avoid disconnected micro-lines that do not chain from prior structure.
   - Prefer obvious, connected lines over dense overdraw.

4. **Wick-based construction priority**
   - Swing points and trendline anchors use wick extremes as primary reference.

---

## ⚠️ RISK MANAGEMENT PHILOSOPHY (READ FIRST)

This strategy is built **stop-first, entry-second**. Every module exists to protect capital. The entry is the least important part. What matters:

1. **Know your max loss before you click the button** — if you can't define the stop precisely, you don't take the trade.
2. **Stops are sacred** — Tori's words. Never widened, never "given room."
3. **The house always has an edge on noise** — on 2-min charts, wick noise is extreme. We separate the *disaster stop* (hard, tick-triggered) from the *trade management exit* (close-based, logical). They are NOT the same.
4. **Partial profits are mandatory** — holding 100% of your position for a trailing exit is a fantasy on 2-min charts. Lock in half, let the rest ride.
5. **Daily loss limits are in dollars, not trades** — 3 small wins then 1 massive loss = net negative. Dollar limits prevent ruin.

---

## 🏗️ Architecture Overview

```
Module 1: Swing Point Detection
        ↓
Module 2: Trendline Object & Management
        ↓
Module 3: Touch Detection & Validation
        ↓
Module 4: Break Detection
        ↓
Module 5: Safety Line Logic
        ↓
Module 6: HTF Bias Filter + Volatility Guard
        ↓
Module 7: Pre-Entry Risk Gate (R:R Check)
        ↓
Module 8: Entry Execution (Bounce + Break)
        ↓
Module 9: Stop, Trail, Partial & Exit Management
```

---

## 📦 Module 1: Swing Point Detection Engine

**Purpose:** Identify confirmed Pivot Highs and Pivot Lows on the 2-min chart  
**Trigger:** Runs on every bar close  

### Logic
```
For a Pivot High at bar[i]:
  - bars[i].High is the highest high from bars[i - SwingStrength] to bars[i + SwingStrength]
  - Requires SwingStrength confirmed bars to the right (detection is delayed)

For a Pivot Low at bar[i]:
  - bars[i].Low is the lowest low from bars[i - SwingStrength] to bars[i + SwingStrength]
  - Same confirmation delay
```

### SwingPoint Data Structure
```csharp
struct SwingPoint {
    int BarIndex;       // which bar
    double Price;       // High or Low price (wick tip)
    DateTime Time;      // bar timestamp
    bool IsHigh;        // true = pivot high, false = pivot low
}
```

### Parameters
| Parameter | Default | Notes |
|---|---|---|
| `SwingStrength` | 3 | Bars left + right for pivot confirmation |
| `MaxSwingLookback` | 200 | Max historical swings to keep in memory |
| `MinSwingDiffTicks` | 4 | Min price difference between consecutive same-type swings (filters noise) |

### Rules
- Only append to list on fully confirmed (closed) bars — never current bar
- Trim list to MaxSwingLookback to avoid memory bloat
- Swing price is the **wick tip** (High for pivot high, Low for pivot low) — per Tori's "always use wicks" rule

### Visual
- Green upward triangle below bar = confirmed Pivot Low
- Red downward triangle above bar = confirmed Pivot High

---

## 📦 Module 2: Trendline Object & Management

**Purpose:** Construct and maintain two active trendlines — one through swing highs (downtrend resistance) and one through swing lows (uptrend support)

### Trendline Data Structure
```csharp
class TrendLine {
    SwingPoint PointA;                // Anchor — oldest swing (never changes)
    SwingPoint PointB;                // Most recent qualifying swing (updates)
    List<SwingPoint> Touches;         // All confirmed touch points
    bool IsValid;                     // Passes all validity checks
    bool IsConsumed;                  // Already fired a trade — retire this line
    int FirstTouchBar;                // Bar index of first touch
    bool IsUptrend;                   // true = connects lows; false = connects highs
    
    double GetValueAtBar(int barIndex) {
        return PointA.Price + Slope * (barIndex - PointA.BarIndex);
    }
    
    double Slope {
        get { return (PointB.Price - PointA.Price) / (double)(PointB.BarIndex - PointA.BarIndex); }
    }
}
```

### Construction Rules
1. **Uptrend line (support):** Connect 2+ confirmed Pivot Lows where each low > previous low (higher lows)
2. **Downtrend line (resistance):** Connect 2+ confirmed Pivot Highs where each high < previous high (lower highs)
3. **Per-line anchor rule:** Point A = older swing (anchor for that specific segment)
4. **Point B** = newer qualifying swing
5. **Continuity rule:** next continuation segment must start from prior segment’s Point B (B→C→D chain)

### Top-Down Chaining Rule (intraday adaptation)
- Build structure in this order: **Daily → 4H → 1H → 2M**.
- Lower TF line initialization must inherit continuity from the higher TF endpoint (latest valid B).
- 2M execution lines are invalid if disconnected from the active Daily/4H/1H structural chain.
- If a timeframe has no further valid pullback/segment, move down to the next timeframe.
- As you move lower, allow slight point refinement for precision, but preserve chain continuity.

### Validity Checks (Zero-Intersection Rule)
Between PointA and PointB (and forward to current bar):
- **Uptrend line:** no bar's **Close** below the line value at that bar
- **Downtrend line:** no bar's **Close** above the line value at that bar
- If violated → line is invalid, rebuild from newer swing points

### Slope Validity
- **Uptrend line must have positive slope** (going up)
- **Downtrend line must have negative slope** (going down)
- If slope crosses zero (line goes flat or inverts) → delete line, rebuild
- Minimum absolute slope: `MinSlopeTicksPerBar = 0.01` (rejects nearly-flat lines that are really horizontal S/R, not trendlines)

### Line Maintenance
- New qualifying swing → update PointB, re-run validity check on entire line
- Validity fails → mark invalid, attempt rebuild with next swing pair
- `IsConsumed = true` → stop using this line, build fresh on next valid pair

### Active Lines (Always Maintain Two)
- `TrendLine uptrendLine` — connects pivot lows (support / potential Safety Line for shorts)
- `TrendLine downtrendLine` — connects pivot highs (resistance / potential Safety Line for longs)
- Neither is pre-designated as Action or Safety — that's determined at trade time by which one breaks

---

## 📦 Module 3: Touch Detection & Validation

**Purpose:** Count and validate trendline touch points  

### Touch Definition
```
For uptrend line:
  Low[0] <= lineValue + (TouchZoneTicks * TickSize)
  AND Close[0] >= lineValue  (price didn't close through — respected the line)

For downtrend line:
  High[0] >= lineValue - (TouchZoneTicks * TickSize)
  AND Close[0] <= lineValue  (price didn't close through — respected the line)
```

### Touch Validation Rules (ALL must pass)
| # | Rule | Check |
|---|---|---|
| 1 | Spacing | `CurrentBar - lastTouchBar >= MinBarsBetweenTouches` |
| 2 | No intersection | No candle Close has crossed the line since previous touch |
| 3 | Bar confirmed | Touch registered only on closed bars (not current bar) |
| 4 | Minimum swing diff | Swing price differs from last touch by ≥ `MinSwingDiffTicks` |

### Parameters
| Parameter | Default | Notes |
|---|---|---|
| `TouchZoneTicks` | 4 | How close = a touch (1 point on ES) |
| `MinBarsBetweenTouches` | 6 | 12 min minimum spacing on 2-min chart |
| `MinTouchCount` | 3 | Minimum touches for a tradeable line |
| `MinBarsFromFirstTouch` | 30 | 60 min minimum from first touch to entry |

### Logic (Per Bar Close)
```
For each active trendline:
    If touch zone conditions met:
        If spacing >= MinBarsBetweenTouches:
            If ZeroIntersectionCheck passes since last touch:
                Append to line.Touches
                Update PointB if this bar's swing qualifies
```

---

## 📦 Module 4: Break Detection

**Purpose:** Identify when a valid trendline is broken by a qualifying candle close  

### Break Conditions

**Long Break (downtrend resistance line broken):**
```
Close[0] > downtrendLine.GetValueAtBar(CurrentBar)    // Candle CLOSE above the line
AND downtrendLine.Touches.Count >= MinTouchCount       // 3+ touches
AND (CurrentBar - downtrendLine.FirstTouchBar) >= MinBarsFromFirstTouch  // Line is "mature"
AND downtrendLine.IsValid                              // Zero-Intersection still holds
AND NOT downtrendLine.IsConsumed                       // Hasn't already fired a trade
```

**Short Break (uptrend support line broken):**
```
Close[0] < uptrendLine.GetValueAtBar(CurrentBar)
AND uptrendLine.Touches.Count >= MinTouchCount
AND (CurrentBar - uptrendLine.FirstTouchBar) >= MinBarsFromFirstTouch
AND uptrendLine.IsValid
AND NOT uptrendLine.IsConsumed
```

> **⚠️ IMPORTANT:** The break condition is **Close past the line** — NOT "full body past the line." Tori's rule is "candle closes on the other side." The "full body" (Open AND Close) filter was in the fakeout section as an aggressive filter, but on 2-min charts it kills too many legitimate entries because bars frequently open right at the line. The retest filter (Module 8) is the primary fakeout defense.

### State After Break Detection
```csharp
bool breakDetected = true;
int breakDirection = 1;  // 1 = Long, -1 = Short
double brokenLineValue = line.GetValueAtBar(CurrentBar);  // For retest detection
int breakBar = CurrentBar;
line.IsConsumed = true;
```

---

## 📦 Module 5: Safety Line Logic

**Purpose:** Identify and project the opposing trendline for stop placement  

### Definition
- **Break Long** (downtrend line broke) → Action=`downtrendLine`, Safety=`uptrendLine`
- **Break Short** (uptrend line broke) → Action=`uptrendLine`, Safety=`downtrendLine`
- **Bounce Long** (bounce on uptrend support) → Action=`uptrendLine`, Safety reference anchored to bounced structure for stop logic
- **Bounce Short** (bounce on downtrend resistance) → Action=`downtrendLine`, Safety reference anchored to bounced structure for stop logic

> Assignment must be persisted per-trade at entry (`activeAction`, `activeSafety`) and never inferred loosely after the fact.

### Safety Line Requirements (Hard Filter — No Safety = No Trade)
| Check | Rule |
|---|---|
| Exists | Safety Line object exists and has PointA + PointB |
| Touches | ≥ 2 confirmed touch points (can be less than Action Line) |
| Valid | Passes Zero-Intersection Rule at time of entry |
| Projecting | Line is projecting forward (not flat, not inverted) |
| Distance | Stop distance ≤ `MaxSafetyStopTicks` — trade is economically viable |

**If ANY of these fail → DO NOT ENTER. Skip the trade entirely.**

### Safety Line Value
```
safetyStop = safetyLine.GetValueAtBar(CurrentBar)
// Updated every bar during active trade for trailing
```

---

## 📦 Module 6: HTF Bias Filter + Volatility Guard

**Purpose:** Only trade in direction of higher timeframe trend AND avoid dangerous volatility regimes  

> Transcript rule reinforcement: do not take lower-TF "A+" breaks that fight higher-TF structure. If HTF chain is bullish, short breaks on 2M are treated as likely fakeouts unless full chain context has flipped.

### 6A: HTF Bias Filter

```csharp
// In Initialize()
AddDataSeries(BarsPeriodType.Minute, 15);  // BarsArray[1] = 15-min bars
```

```
htfEma = EMA(Closes[1], HTFEmaPeriod)   // 20 EMA on 15-min chart
slope = htfEma[0] - htfEma[HTFSlopeLookback]

if slope > HTFSlopeThreshold:
    bias = Bullish → only Long trades allowed
elif slope < -HTFSlopeThreshold:
    bias = Bearish → only Short trades allowed
else:
    bias = Neutral → NO trades allowed (choppy/indecisive)
```

### 6B: Volatility Guard

**Purpose:** Avoid trading when the 2-min ATR is abnormally high (news spikes, circuit breakers) or abnormally low (dead market, no range to capture)

```
atr = ATR(ATRPeriod)[0]   // on 2-min chart

if atr > MaxATRTicks * TickSize:
    volatilityOK = false   // Too volatile — stops will be massive, slippage huge
elif atr < MinATRTicks * TickSize:
    volatilityOK = false   // Dead market — no room to profit
else:
    volatilityOK = true
```

### 6C: News Blackout (Optional but Recommended)

Avoid entries within `NewsBlackoutMinutes` of known high-impact events:
- FOMC announcement (2:00 PM ET)
- CPI/PPI release (8:30 AM ET)
- NFP (8:30 AM ET, first Friday)

Implementation: hardcoded list of times OR parameter-driven blackout windows. NinjaTrader doesn't have a native news calendar, so use time-based filters.

```
// Simple approach: avoid first 5 min after open + known release times
if (Time[0].Hour == 8 && Time[0].Minute < 35):  // 8:30 AM ET releases
    newsBlackout = true
if (Time[0].Hour == 14 && Time[0].Minute >= 0 && Time[0].Minute <= 5):  // FOMC 2 PM ET
    newsBlackout = true
```

### Parameters
| Parameter | Default | Notes |
|---|---|---|
| `UseHTFFilter` | true | Toggle 15-min bias on/off |
| `HTFEmaPeriod` | 20 | EMA period on 15-min chart |
| `HTFSlopeLookback` | 3 | Bars back to measure slope |
| `HTFSlopeThreshold` | 0.5 | Min slope (points) to be directional |
| `ATRPeriod` | 14 | ATR calculation period (2-min bars) |
| `MaxATRTicks` | 16 | 4 points ES — too volatile above this |
| `MinATRTicks` | 2 | 0.5 points ES — dead below this |
| `UseNewsBlackout` | true | Skip entries near known release times |
| `NewsBlackoutMinutes` | 5 | Minutes around event to avoid |

---

## 📦 Module 7: Pre-Entry Risk Gate (R:R Check)

**Purpose:** Calculate risk and reward BEFORE entry. If the math doesn't work, don't trade.

This is what separates a 10-year trader from a beginner. **Never enter without knowing your R:R.**

### Risk Calculation
```
For Long:
    entryPrice = estimated entry (current bar close for immediate; broken line value for retest)
    stopPrice = max(safetyLine.GetValueAtBar(entryBar), entryPrice - HardStopTicks * TickSize)
    riskTicks = (entryPrice - stopPrice) / TickSize

For Short:
    entryPrice = estimated entry
    stopPrice = min(safetyLine.GetValueAtBar(entryBar), entryPrice + HardStopTicks * TickSize)
    riskTicks = (stopPrice - entryPrice) / TickSize
```

### Reward Estimation
```
// Use channel height as initial target estimate
channelHeight = abs(actionLine.GetValueAtBar(CurrentBar) - safetyLine.GetValueAtBar(CurrentBar))
estimatedRewardTicks = channelHeight / TickSize

// Alternative: use ATR-based target
atrTarget = ATR(ATRPeriod)[0] * TargetATRMultiplier
```

### R:R Gate
```
rr_ratio = estimatedRewardTicks / riskTicks

if rr_ratio < MinRiskRewardRatio:
    SKIP TRADE — reward doesn't justify risk
```

### Risk Per Trade (Dollar Limit)
```
riskDollars = riskTicks * TickValue * contracts
// ES: TickValue = $12.50 per tick per contract

if riskDollars > MaxRiskDollarsPerTrade:
    SKIP TRADE — too expensive
```

### Parameters
| Parameter | Default | Notes |
|---|---|---|
| `MinRiskRewardRatio` | 1.5 | Minimum R:R to take a trade |
| `MaxRiskDollarsPerTrade` | 200.0 | Max dollar risk per trade (for 1 contract: 16 ticks × $12.50 = $200) |
| `TargetATRMultiplier` | 2.0 | Reward estimate = 2× ATR |
| `MaxSafetyStopTicks` | 16 | Max ticks for Safety Line stop (if further → skip) |

---

## 📦 Module 8: Entry Execution

**Purpose:** Execute the trade on confirmed setup after all gates pass  

### Pre-Entry Checklist (ALL Must Pass — Hard Stops)
```
[ ] Break or Bounce detected (Module 4 or touch count ≥ MinTouchCount)
[ ] Safety Line valid and projecting (Module 5)
[ ] HTF bias aligns with trade direction (Module 6A)
[ ] Volatility within bounds (Module 6B)
[ ] Not in news blackout window (Module 6C)
[ ] R:R ≥ MinRiskRewardRatio (Module 7)
[ ] Risk $ ≤ MaxRiskDollarsPerTrade (Module 7)
[ ] Session time: after SessionStartET and before SessionEndET
[ ] tradesThisSession < MaxTradesPerSession
[ ] dailyPnL > -MaxDailyLossDollars (daily loss limit not hit)
[ ] No active position already open
[ ] cooldownBarsRemaining == 0 (post-loss cooldown expired)
```

### 8A: Break Entry — Two Modes

#### Mode A: Immediate Entry (WaitForRetest = false)
```
On bar AFTER break bar:
    EnterLong/Short at market (next bar open)
```

#### Mode B: Retest Entry (WaitForRetest = true) ← DEFAULT
```
After break detected:
    state = WaitingForRetest
    
    On each subsequent bar:
        // For Long retest: price must pull back DOWN to the broken line
        If Low[0] <= brokenLineValue + (RetestZoneTicks * TickSize):
            state = RetestTouched
        
        If state == RetestTouched:
            If Close[0] > brokenLineValue:
                // Retest held — line now acts as support
                Enter Long at next bar open
                state = Entered
            If Close[0] < brokenLineValue:
                // Retest failed — fakeout confirmed
                state = RetestFailed → abort
        
        If (CurrentBar - breakBar) > MaxRetestWaitBars:
            state = Expired → abort (price ran without retest)
```

### 8B: Bounce Entry
```
If activeTrendline.Touches.Count >= MinTouchCount:
    If touch zone hit on current bar AND Close respects line:
        If all pre-entry checks pass:
            Enter at next bar open in direction of the trend
            // Bounce long: price touches uptrend line → enter long
            // Bounce short: price touches downtrend line → enter short
```

**Bounce Stop:** Stop is set just beyond the Action Line itself (the line being bounced) + a small buffer:
```
For bounce long:  stop = lineValue - BounceStopBufferTicks * TickSize
For bounce short: stop = lineValue + BounceStopBufferTicks * TickSize
```

### Parameters
| Parameter | Default | Notes |
|---|---|---|
| `WaitForRetest` | true | Retest entry vs. immediate |
| `RetestZoneTicks` | 4 | How close = valid retest touch |
| `MaxRetestWaitBars` | 15 | 30 min max wait for retest (2-min bars) |
| `MaxTradesPerSession` | 2 | Per RTH session |
| `CooldownBarsAfterLoss` | 5 | 10 min wait after a stopped-out trade |
| `BounceStopBufferTicks` | 2 | Extra ticks beyond line for bounce stop |
| `SessionStartET` | 09:30 | Entry window open (ET) |
| `SessionEndET` | 14:45 | No new entries after this (ET) |

---

## 📦 Module 9: Stop, Trail, Partial & Exit Management

**Purpose:** Manage the trade from entry to exit with capital preservation as #1 priority  

### ⚠️ CRITICAL: Two-Layer Stop Architecture

On 2-min charts, wick noise is extreme. We use TWO separate stop mechanisms:

| Layer | Type | Purpose | Triggers On |
|---|---|---|---|
| **Hard Stop** (disaster) | NinjaTrader native `SetStopLoss()` | Prevents catastrophic loss if price crashes | Any tick hitting the price (real-time) |
| **Logical Exit** (trade management) | Code-based check on bar Close | Tori's "candle close through Safety Line" rule | Bar close only — ignores wicks |

**These are NOT the same price.** The hard stop sits BEYOND the Safety Line (wider) as a catastrophe guard. The logical exit fires FIRST when a candle closes through the Safety Line. This prevents getting stopped by wick noise while still honoring Tori's close-based exit rule.

### 9A: Initial Stop Placement (At Entry)

**For Break Trades:**
```
safetyStopPrice = safetyLine.GetValueAtBar(CurrentBar)

// Hard stop = Safety Line value + buffer (wider — disaster protection)
For Long:  hardStop = safetyStopPrice - HardStopBufferTicks * TickSize
For Short: hardStop = safetyStopPrice + HardStopBufferTicks * TickSize

// Cap: never exceed HardStopMaxTicks from entry
distFromEntry = abs(entryPrice - hardStop) / TickSize
if distFromEntry > HardStopMaxTicks:
    hardStop = entryPrice -/+ HardStopMaxTicks * TickSize

SetStopLoss(CalculationMode.Price, hardStop)
```

**For Bounce Trades:**
```
// Stop is beyond the Action Line (the line being bounced)
For Long:  hardStop = lineValue - BounceStopBufferTicks * TickSize
For Short: hardStop = lineValue + BounceStopBufferTicks * TickSize
SetStopLoss(CalculationMode.Price, hardStop)
```

### 9B: Breakeven Rule

Tori: *"After 1:1 R:R, move stop to breakeven."*

```
unrealizedTicks = (currentPrice - entryPrice) / TickSize  // for longs; negate for shorts

if unrealizedTicks >= initialRiskTicks:   // 1:1 R:R reached
    if NOT breakevenMoved:
        // Move hard stop to entry + BreakevenBufferTicks (small profit lock)
        newStop = entryPrice + BreakevenBufferTicks * TickSize  // longs
        SetStopLoss(CalculationMode.Price, newStop)
        breakevenMoved = true
```

### 9C: Partial Profit Exit (50% Scale-Out)

Tori: *"Scale out at key levels: 50% at first target, trail the rest."*

```
// First target = channel height from entry, or TargetATRMultiplier × ATR
partialTargetTicks = max(channelHeightTicks, atrTarget)

if unrealizedTicks >= partialTargetTicks AND NOT partialTaken:
    ExitHalf()   // Close 50% of position at market
    partialTaken = true
    
    // After partial: tighten the hard stop to breakeven + small profit
    SetStopLoss for remaining qty at entryPrice + PartialLockTicks * TickSize
```

### 9D: Trailing Stop Along Safety Line (After Partial)

After the partial exit, trail the remaining position using the Safety Line:

```
On each bar close:
    newSafetyValue = safetyLine.GetValueAtBar(CurrentBar)
    
    // Ratchet — only move in profit direction
    For Long:
        if newSafetyValue > currentLogicalStop:
            currentLogicalStop = newSafetyValue
            // Update hard stop (with buffer)
            SetStopLoss(newSafetyValue - HardStopBufferTicks * TickSize)
    
    For Short:
        if newSafetyValue < currentLogicalStop:
            currentLogicalStop = newSafetyValue
            SetStopLoss(newSafetyValue + HardStopBufferTicks * TickSize)
```

### 9E: Exit Conditions (Priority Order)

| Priority | Condition | Action |
|---|---|---|
| 1 | **Hard Stop Hit** | NinjaTrader native stop fires — position flat (disaster protection) |
| 2 | **Safety Line Close Violation** | Bar Close crosses Safety Line → market exit immediately on next bar open |
| 3 | **Partial Profit Target Hit** | 50% position closed at market, rest trails |
| 4 | **Session End** | Time ≥ SessionCloseET → ExitAll at market |
| 5 | **Daily Loss Limit Hit** | dailyPnL ≤ -MaxDailyLossDollars → close all, halt trading |

### 9F: Safety Line Close Violation (The Primary Exit)

```
On each bar close:
    safetyValue = safetyLine.GetValueAtBar(CurrentBar)
    
    For Long:
        if Close[0] < safetyValue:
            // Candle closed THROUGH the Safety Line — Tori's non-negotiable exit
            ExitLong("SafetyLineViolation") at market
    
    For Short:
        if Close[0] > safetyValue:
            ExitShort("SafetyLineViolation") at market
```

This fires BEFORE the hard stop in most cases because it checks the Close, not intrabar price. The hard stop is the parachute if price gaps or crashes through without a clean close.

### Parameters
| Parameter | Default | Notes |
|---|---|---|
| `HardStopBufferTicks` | 4 | Buffer beyond Safety Line for hard stop (1 point ES) |
| `HardStopMaxTicks` | 20 | Absolute max stop from entry (5 points ES) |
| `BreakevenBufferTicks` | 2 | Lock 2 ticks profit when moving to BE |
| `PartialExitPct` | 50 | Percentage of position to close at first target |
| `PartialLockTicks` | 4 | Lock 1 point profit after partial exit |
| `BounceStopBufferTicks` | 2 | Buffer beyond Action Line for bounce stop |
| `SessionCloseET` | 15:00 | Force close all |
| `MaxDailyLossDollars` | 500.0 | Halt after this much daily loss ($500 = 2 full stops on 1 contract) |
| `CooldownBarsAfterLoss` | 5 | 10 min cooldown after a loss before next entry |

---

## 📊 Full Parameter Reference (All Modules)

### Module 1: Swing Detection
| Parameter | Default | Description |
|---|---|---|
| `SwingStrength` | 3 | Pivot detection bars left+right |
| `MaxSwingLookback` | 200 | Max historical swings to keep |
| `MinSwingDiffTicks` | 4 | Min price diff between consecutive same-type swings |

### Module 2: Trendline Construction
| Parameter | Default | Description |
|---|---|---|
| `MinSlopeTicksPerBar` | 0.01 | Reject nearly-flat lines |

### Module 3: Touch Detection
| Parameter | Default | Description |
|---|---|---|
| `TouchZoneTicks` | 4 | Proximity to count as touch |
| `MinBarsBetweenTouches` | 6 | 12 min spacing on 2-min |
| `MinTouchCount` | 3 | Touches required for tradeable line |
| `MinBarsFromFirstTouch` | 30 | 60 min maturity from first touch |

### Module 6: Filters
| Parameter | Default | Description |
|---|---|---|
| `UseHTFFilter` | true | 15-min bias filter |
| `HTFEmaPeriod` | 20 | EMA period on 15-min |
| `HTFSlopeLookback` | 3 | Bars back for slope |
| `HTFSlopeThreshold` | 0.5 | Min slope (points) |
| `ATRPeriod` | 14 | ATR period on 2-min |
| `MaxATRTicks` | 16 | Skip if ATR > 4 points |
| `MinATRTicks` | 2 | Skip if ATR < 0.5 points |
| `UseNewsBlackout` | true | Avoid known release windows |
| `NewsBlackoutMinutes` | 5 | Buffer around events |

### Module 7: Risk Gate
| Parameter | Default | Description |
|---|---|---|
| `MinRiskRewardRatio` | 1.5 | Minimum R:R to take trade |
| `MaxRiskDollarsPerTrade` | 200.0 | Max dollar risk per trade |
| `TargetATRMultiplier` | 2.0 | Reward estimate multiplier |
| `MaxSafetyStopTicks` | 16 | Max acceptable Safety Line distance |

### Module 8: Entry
| Parameter | Default | Description |
|---|---|---|
| `WaitForRetest` | true | Retest entry (default) vs. immediate |
| `RetestZoneTicks` | 4 | Proximity for retest touch |
| `MaxRetestWaitBars` | 15 | 30 min max wait |
| `MaxTradesPerSession` | 2 | Per RTH session |
| `CooldownBarsAfterLoss` | 5 | 10 min cooldown after loss |
| `BounceStopBufferTicks` | 2 | Buffer for bounce stop |
| `SessionStartET` | 09:30 | Entry window open |
| `SessionEndET` | 14:45 | Entry window close |

### Module 9: Exits
| Parameter | Default | Description |
|---|---|---|
| `HardStopBufferTicks` | 4 | Buffer beyond Safety Line for hard stop |
| `HardStopMaxTicks` | 20 | Absolute max stop distance |
| `BreakevenBufferTicks` | 2 | Profit lock at breakeven move |
| `PartialExitPct` | 50 | % position closed at first target |
| `PartialLockTicks` | 4 | Profit lock after partial |
| `SessionCloseET` | 15:00 | Force close |
| `MaxDailyLossDollars` | 500.0 | Daily loss halt |

---

## 🔧 Gap & Session Reset Handling

**Problem:** ES gaps overnight. A valid trendline from yesterday's close can be gapped through on today's open.

### Rules
1. **On session start (RTH open):** Check all active trendlines against the first bar's Open
   - If Open gaps through a trendline → mark that line as **invalid** (not consumed — just invalid)
   - Rebuild lines from fresh swings formed during current session
2. **Trendlines built from overnight Globex data CAN be used** — they just need to survive the session open gap check
3. **First 5 minutes of RTH (9:30 - 9:35 ET):** No entries. Let the open settle. This is the `SessionStartET + 5min` warm-up period.
4. **All trendlines reset daily** — no multi-day line carryover for an intraday strategy
   - `OnSessionStart()`: clear all trendline objects, swing lists start fresh from Globex bars loaded for today's session

---

## 📈 Trade Logging & Journaling

**Purpose:** Tori's "Scientist" workflow — every trade must be logged for review

### Log Each Trade (to NinjaTrader output + file)
```
Entry Time, Exit Time
Entry Price, Exit Price
Direction (Long/Short)
Setup Type (Bounce/Break)
Action Line: touch count, age (bars from first touch)
Safety Line: touch count, stop distance at entry
HTF Bias at entry
ATR at entry
R:R at entry
Result: P&L ticks, P&L dollars
Exit Reason: SafetyLineViolation / HardStop / Partial / SessionClose / DailyLimit
Breakeven moved? (Y/N)
Partial taken? (Y/N)
```

This data feeds the weekly review process.

---

## 🗺️ Build Sequence (Coding Order)

### Phase 1 — Foundation (No Trading, Visual Validation Only)
- [ ] **Step 1:** Swing detection + visual dots on chart
- [ ] **Step 2:** Trendline construction + draw lines on chart
- [ ] **Step 3:** Touch counting + highlight touches + print to output
- [ ] **Step 4:** Break detection + print signals to output

### Phase 2 — Risk Logic (Still No Trading)
- [ ] **Step 5:** Safety Line identification + draw on chart
- [ ] **Step 6:** HTF bias filter + volatility guard + print state
- [ ] **Step 7:** R:R pre-entry gate — print pass/fail for each signal

### Phase 3 — Execution
- [ ] **Step 8:** Entry execution (test immediate first, then retest mode)
- [ ] **Step 9:** Initial stop placement (two-layer architecture)
- [ ] **Step 10:** Breakeven move at 1:1
- [ ] **Step 11:** Partial exit at target
- [ ] **Step 12:** Trailing stop along Safety Line
- [ ] **Step 13:** Safety Line close violation exit
- [ ] **Step 14:** Session close + daily loss halt
- [ ] **Step 15:** Cooldown timer after losses
- [ ] **Step 16:** Gap handling + session reset

### Phase 4 — Validation
- [ ] **Step 17:** Paper trade 2+ weeks — zero crashes, all exits clean
- [ ] **Step 18:** Backtest on 6-12 months of ES 2-min data
- [ ] **Step 19:** Review trade logs — are lines correct? Are entries logical?
- [ ] **Step 20:** Parameter tuning based on results
- [ ] **Step 21:** Stress test — run on high-vol days (FOMC, CPI, NFP) separately

---

## 📁 File Structure

```
/ESTrading/
  tori-strategy/
    TORI_TRADES_NOTES.md        ← Research notes (Step 1) ✅
    IMPLEMENTATION_PLAN.md      ← This file (Step 2) ✅
  ESTrendline_v1.cs             ← Strategy code (Step 3)
```

---

## ⚠️ Known Challenges & Mitigations

| Challenge | Risk | Mitigation |
|---|---|---|
| Swing detection lag | Pivots confirmed late (SwingStrength bars delay) | Acceptable for 2-min; reduces noise |
| Zero-Intersection is computationally expensive | Slow on large lookbacks | Cache line values; only validate from last touch forward |
| Safety Line may not exist at break time | Missed valid trades | Hard filter: no trade. Capital preservation > opportunity |
| Retest never comes | Entry window expires | MaxRetestWaitBars = 15 expiry prevents hanging state |
| Trendline slope goes flat/inverts | Invalid line persists | Slope validity check every bar; auto-delete |
| 2-min wick noise hits hard stops | Premature exits | Two-layer architecture: hard stop wider, logical exit on Close |
| ES gaps overnight | Yesterday's lines violated at open | Session reset: validate all lines at first RTH bar |
| Multiple breaks at same time | State confusion | Only process one break at a time; second is queued or ignored |
| Globex data needed but RTH entries only | Wrong bar indexing | Load full Globex; filter entries by session time |
| HTF 15-min series alignment | Wrong bias signal | Use BarsInProgress check; only compute on correct series |
| Slippage on market orders | Unexpected cost | Budget 1 tick slippage per side in R:R calculation |
| Partial exits change position size | Stop recalculation needed | Recalculate stops on remaining qty after partial |

---

## 💰 Expected Cost Per Trade (Worst Case)

```
Max stop: 16 ticks = 4 points = $200 per contract
Slippage: 2 ticks round trip = $25
Commission: ~$4.50 round trip (typical)
Total cost per losing trade: ~$230 per contract

Daily max loss: $500 = ~2 full stops + slippage + commission
```

---

*Status: Step 2 Complete (v2 — Risk-First Revision) — Ready for Step 3 (coding)*  
*Last updated: 2026-03-13*

# Tori Trades — Strategy Research Notes
**Purpose:** Information gathering phase (Step 1 of 3)  
**Goal:** Replicate Tori's trendline strategy in NinjaTrader for ES futures  
**Status:** 🔄 IN PROGRESS — still collecting source material  

---

## 📡 X (Twitter) Research — via xAI Grok Search

*Source: @toritrades X account — scraped 2026-03-13 via xAI Grok API*

### 🔑 Key Terminology — Her Own Words
- She calls the entry trendline the **"attack line"** (not just "action line") in some posts
- The backup trendline is the **"safety line"**
- Her system is described as "two trendlines" — nothing more
- She explicitly says: "I've blown accounts ignoring risk — don't be me"

### 💰 Confirmed Instrument & Timeframe
- She trades **futures** — confirmed multiple times (Gold futures, general futures)
- **No explicit ES/S&P 500 mentions found** in scraped posts, but her strategy is explicitly stated to apply to "any liquid market and any timeframe"
- **Primary trading style: ONE trade per day, ~30 minutes** — she day trades intraday then is done
- Timeframe: Intraday (5-min or 15-min) for entries, but checks higher TFs (1H, Daily) for context
- She has evolved toward **longer holds** (overnight, swing) when conviction is high — "3x returns while trading half the time"

### 📐 Trendline Construction (from X posts)
- Wicks (not bodies) preferred for touch points — "true price extremes"
- The "most obvious" lines are the best — if you have to squint to see it, it's not valid
- Quote: *"Trendlines are subjective, but the best ones are the ones everyone sees"*
- Use **logarithmic charts** for exponential-growth assets (crypto/stocks); for ES futures this is less relevant
- Common mistake: **overdrawing** — keep only the most obvious, highest-quality lines
- Draw **left to right**, starting from oldest confirmed swing point

### 🎯 Entry Rules (from X posts)
- Quote: *"Don't trade every touch — wait for confirmation candles"*
- Quote: *"No entry without a plan — define your invalidation first"*
- Avoid FOMO — wait for the setup to come to you
- **3-Touchpoint Setup**: trendline with 3 confirmed touches = high probability; less than 3 = higher fakeout risk
- After a break: look for **retest of broken line** as new support/resistance before entering

### 🚪 Exit Rules (from X posts)
- Scale out at key levels: **50% at first target** (prior high/low), trail the rest
- Targets: measured move from channel height OR Fibonacci extensions
- Quote: *"Let winners run, but don't get greedy — take partials to lock in profits"*
- **Safety line dictates trailing stop** — she regrets when she doesn't let it dictate exits
- 2024 self-review finding: she was closing too early at S/R levels instead of letting safety line trail

### 🛑 Stop Loss (from X posts)
- Below action line for longs, above for shorts — with **1-2% buffer** for wick accommodation
- After 1:1 R:R: **move stop to breakeven**, then trail
- Quote: *"Stops are sacred — never move them to 'give it room'"*
- Use **ATR** for stop distance in volatile conditions

### ⚠️ Choppy Market Rules (from X posts)
- In chop/ranging: **don't trade** — pass and wait
- Range is a *setup* for the breakout, not something to trade within
- Wait for a **decisive trendline break** before entering
- Quote: *"Fake-outs hurt confidence, win rate, and profit"*
- Key lesson from near-$8K loss: block out social media/noise and stick to the rules in choppy conditions

### 📊 Indicators Used
- **Minimalist setup** — just trendlines on price
- **No VWAP, no EMAs explicitly mentioned**
- Volume referenced occasionally for break confirmation — increase in volume on the break = higher probability
- She critiques complicated strategies: her profitable system is explicitly "just two trendlines"

### 📏 Scaling
- Scales OUT at key levels — partial profit at first target, trail the rest
- Scales IN cautiously — warns against copying others' large positions when you aren't ready
- Story: lost thousands by scaling in too aggressively mimicking others

### 📰 Pre-Market / News Handling
- No specific pre-market or news event rules found in posts
- General approach: "block out outside noise" — this implies avoiding news-reactive trades
- Trading starts **post-open** in her routine (implying she avoids pre-market)
- The ONE trait that made her profitable: **discipline** to stick to trendline rules when news causes volatility

### ⏱️ Touch Point Timing
- **No minimum time between touches explicitly stated**
- Focus is entirely on **quality and count** of touches, not timing
- 3 touches = high probability regardless of how much time passed between them

### 💡 Specific Trade Examples Found
1. **Gold futures short** — Entry: 1926.3 at 9:00 AM, Exit: 1894.9 next morning 6:00 AM, Profit: +$3,000 — held overnight
2. **Near-$8K loss** — futures trade, entered on trendline break, let emotions interfere, lesson: trust the system
3. **$1,000 trade video** — futures, entered after break, scaled in on confirmation, exited at profit target
4. **2024 self-review** — identified pattern of exiting too early at S/R instead of letting safety line trail

### 🧠 Psychology / Mindset
- Losses = "business fees" — expected and necessary
- Journal every trade: time of day, setup type, emotion, result
- Discipline is the #1 trait she credits for profitability
- Avoid trading from emotion or outside influence
- Start at 1% risk, build up only after proven consistency

---

## 📺 Source Videos

| # | Title | URL | Length | Status |
|---|---|---|---|---|
| 1 | How To Trade TRENDLINES (Full Guide) | https://www.youtube.com/watch?v=Y8efWZ2M1y8 | 45 min | ✅ Summarized |
| 2 | Breaking Down My SIMPLE Trading Strategy | https://www.youtube.com/watch?v=qLtq73bTPBA | 55 min | ✅ Summarized |
| 3 | The Best Exit Criteria For Trading Trendlines | https://www.youtube.com/watch?v=l0InoYnlM-A | 7 min | ✅ Summarized |
| 4 | How To Stop Fakeouts With Trendlines | https://www.youtube.com/watch?v=G29LbG1Xkvw | 8 min | ✅ Summarized |
| 5 | The Perfect Beginner TRENDLINE Trade | https://www.youtube.com/watch?v=WoY_tI10jCs | 13 min | ✅ Summarized |
| 6 | You're Drawing Trendlines WRONG | https://www.youtube.com/watch?v=OjZ8djwrm4I | 12 min | ✅ Summarized |
| 7 | Trendlines DON'T Work (Here's What Does) | https://www.youtube.com/watch?v=ipUbsQZWFIU | 12 min | ✅ Summarized |
| 8 | Trade Trendlines In Under 3 Minutes | https://www.youtube.com/watch?v=zT2hSb9IEZw | 3 min | ✅ Summarized |

---

## 🧠 Core Philosophy

- Trendlines are NOT prediction tools — they are **reaction tools**
- Draw both lines (up and down), react to whichever one price breaks
- Losses = "business fees" — expected, not failures
- Trade like a scientist: track every variable to find what actually works
- **Never predict direction.** Let price tell you where it wants to go.

---

## 📐 The Two Lines

Every trade uses exactly two trendlines:

### Action Line
- The trendline that gets **broken or bounced**
- This is your **entry trigger**
- Can be either the uptrend line (support) or downtrend line (resistance)

### Safety Line
- The **opposing** trendline
- This is your **risk management line**
- If price closes through it → **exit immediately, no questions**
- As price moves in your favor, you trail your stop along this line

---

## 📍 How to Draw Trendlines (Tori's Rules)

### Construction
1. **Minimum 2 touch points** — ideally 3+
2. **Point A** = anchor (the oldest/first swing point) — never moves
3. **Point B** = adjustment point (most recent higher low OR lower high) — updates as price develops
4. Price **cannot** close through the line between touch points — if it does, the line is invalid
5. **Never draw or adjust on an open (unconfirmed) candle** — wait for close
6. Line must connect **wicks** OR **bodies** consistently (don't mix)

### Maintenance
- **Adjust, don't delete**: When price pokes through briefly but recovers, drag Point B to the new swing
- **When to delete a line:**
  - It becomes horizontal → it's now a support/resistance level, not a trendline
  - It inverts (now slopes the wrong direction)
  - Price has fully closed through it (it's been broken)

### Validity
- A valid trendline has clear, distinct touch points
- Shallow/nearly-flat lines are less reliable
- Steeper lines = stronger trend but break sooner when violated

---

## 🔄 The Two Setups

### Setup 1: Bounce
- Price approaches the Action Line from the expected side
- Touches it (or comes very close) without breaking through
- You enter in the direction of the prevailing trend (continuation)
- Stop goes on the Safety Line
- Best when: 3+ confirmed touches, clear trend direction

### Setup 2: Break
- Price closes **past** the Action Line with a **full-bodied candle** (not a wick)
- This is the critical entry signal — body must close beyond the line
- You enter in the direction of the break (momentum)
- The broken Action Line now becomes support/resistance (role reversal)
- Stop goes on the Safety Line

---

## 🚫 Fakeout Filters (Critical Rules)

From video 4 ("How To Stop Fakeouts"):

1. **Candle Close Filter** ← MOST IMPORTANT
   - NEVER enter on a wick piercing the line
   - Wait for a **full-body candle close** beyond the trendline
   - If only the wick crosses → it's a fakeout, do not enter

2. **Break and Retest** (optional, conservative entry)
   - Price breaks the line → pulls back to test it from outside → continues
   - Entering on the retest gives better R:R and filters many fakeouts
   - Trade-off: sometimes price doesn't retest and you miss the move

3. **Double Confirmation**
   - Strongest setups also clear a horizontal Support/Resistance level on the break
   - Trendline break + horizontal level break = higher probability

4. **HTF Context Check**
   - If Daily/Weekly is strongly bullish, a bearish break on 5-min is likely a fakeout
   - Always check the "headlights" (higher timeframe) before entering

---

## 🎯 Exit Rules

### Primary Exit: Safety Line Violation
- If any bar **closes** through the Safety Line → exit immediately
- No averaging down, no "give it room" — get out

### Trailing Stop Method
- As price moves in your favor, the Safety Line extends forward
- Update your stop to match the Safety Line's current value each bar
- Stop only moves in profit direction — never widened

### Confluence Targets
- Use Higher Time Frame (HTF) trendlines as natural take-profit zones
- Key horizontal S/R levels = natural exits
- When price reaches a major level → consider full or partial exit

### Time-Based Exit
- Tori doesn't explicitly mention time stops (she's swing trading 4H)
- For intraday adaptation: close all positions at session end

---

## 📊 Higher Time Frame (HTF) Analysis

### Tori's Top-Down Process
1. **Monthly** → identify macro trend direction
2. **Weekly** → identify intermediate trend
3. **Daily** → identify current trend + key levels
4. **Trading TF (4H)** → look for entries aligned with above

### The Headlight Analogy
- HTF = headlights on a car
- Without HTF analysis: you can only see 5 feet ahead
- With HTF analysis: you see the whole road
- **Trades against the HTF trend = fakeout candidates**

### The "Squeeze" Setup
- Look for converging upward and downward trendlines on the same chart
- Price is getting squeezed between them
- Trade whichever line breaks first
- This is a high-probability setup because volatility compression precedes expansion

---

## ⚠️ Key Warnings / Things Tori Emphasizes

1. **Never trade against higher timeframe** — most common beginner mistake
2. **Never adjust on open candles** — leads to bad decisions
3. **Wick entries = fakeout trap** — always wait for body close
4. **Don't delete lines prematurely** — adjust when possible
5. **Journaling is mandatory** — treat it like a science experiment
   - Track: time of day, setup type, emotions, result
   - Find which variables actually matter for YOUR trading

---

## 🔢 Risk Management Rules

- Risk **1–3% of capital per trade** (she recommends starting at 1%)
- Think of losses as **"business fees"** — not failures
- No specific R:R target mentioned (she uses trailing stops, not fixed targets)
- Stop based on **Safety Line**, not arbitrary fixed distance
- Journal every trade with full context

---

## 📱 Tori's Instruments & Timeframes

- Primary instruments: **Platinum futures, Crude Oil futures, ES (S&P 500 futures)**
- She DOES trade ES futures — direct relevance
- Primary trading timeframe: **4-hour charts** (swing trading)
- Principle: same strategy applies to any liquid market and any timeframe

---

---

## 📋 Structured Playbook (Phase-by-Phase Rules)

*Source: Detailed strategy breakdown — verified against video summaries*

---

### Phase 1: Foundation — Chart Hygiene (Non-Negotiable Rules)

1. **Zero-Intersection Rule**
   - A trendline is ONLY valid if price has NEVER closed through it between touch points
   - It must act as a clean "floor" (uptrend) or "ceiling" (downtrend) at all times
   - Even one candle close through the line = line is invalid and must be redrawn or deleted

2. **Ray Tool**
   - Use the "Ray" tool in TradingView — NOT the standard line tool
   - Ray extends infinitely into the future so you can see where price will react ahead of time
   - NinjaTrader equivalent: extend line projection forward indefinitely

3. **Point A and Point B**
   - **Point A** = anchor (the extreme high or low) — never moves
   - **Point B** = most recent "lower high" (downtrend) OR "higher low" (uptrend) — updates with each new qualified swing
   - Adjust Point B forward as new swings form; never delete the line, just update B

4. **Always Use Wicks**
   - Draw to the tips of candle wicks, NOT the candle bodies
   - Wicks represent the true price extreme that the market "tested"

---

### Phase 2: Top-Down Analysis ("Headlights")

Mandatory directional bias process — execute in this exact order before any trade:

| Timeframe | Purpose | Action |
|---|---|---|
| Monthly / Weekly | "Dinosaur" trends — major boundaries | Identify overall direction; be very cautious trading against it |
| Daily | Refine lines | HTF lines may look slightly off when zoomed in — adjust for precision |
| 4H / 1H | "Squeeze" zone | Look for price trapped between a downward trendline AND an upward trendline |
| Entry TF (5m, 15m, 1H) | Execution | Look for the actual break here |

**The Squeeze**: Converging upward and downward trendlines on 4H/1H chart. Price is compressed between them. Trade whichever one breaks. This is a high-probability setup because compression precedes expansion.

**Rule**: Monthly/Weekly is bullish → be very cautious shorting on lower TFs. Trading against the "Dinosaur" = fakeout territory.

---

### Phase 3: The "Action and Safety" Playbook — Core Execution

#### The Three-Touch Setup (Required for A+ Setup)
- Find a trendline that price has respected **exactly 3 times**
- 3 touches = market is "aware" of the line = higher probability
- Fewer than 3 touches = lower confidence, higher fakeout risk
- More touches = even stronger (but line eventually breaks — that's the trade)

#### The Action Line
- The trendline that **gets broken**
- Entry signal: **candle closes on the other side of this line**
- **NEVER enter on a wick** — must be a full candle close past the line
- The moment the close is confirmed past the line → that is your trigger

#### The Safety Line
- The **opposing** trendline — the one that ISN'T being broken
- If you go long because a downward line broke → your Safety Line is the upward trendline
- If you go short because an upward line broke → your Safety Line is the downward trendline
- Stop placement: on or just beyond the Safety Line

#### The Exit Rule (Non-Negotiable)
- Stay in the trade **as long as price respects the Safety Line**
- The moment a **candle closes on the wrong side of the Safety Line** → EXIT IMMEDIATELY
- No exceptions. No "give it more room." Out.

---

### Phase 4: Risk Management & Scaling

1. **"Fee" Mentality**
   - Every loss = a business operating fee — expected, not a failure
   - Standard loss = 1–2% of total account per trade

2. **Trailing Stops**
   - As price moves in your favor, trail your stop along the Safety Line
   - Lock in profit while giving the trade room to develop
   - Stop only moves in the direction of profit — never widened

3. **Fakeout Filter: Double Confirmation**
   - Strongest setups: trendline break + **horizontal S/R level** also breaks simultaneously
   - If trendline breaks but horizontal level holds → potential fakeout, wait for more confirmation

4. **Position Sizing / Scaling**
   - Only increase size AFTER 30–90 days of demo data proving consistent execution
   - Rule: prove discipline first, then scale
   - Do NOT increase size to recover losses

---

### Phase 5: The "Scientist" Workflow

1. **Backtest**: Find 100 examples of the Three-Touch Break on TradingView history
2. **Forward Test**: Trade demo/paper account for at least 1 month before going live
3. **Journal**: Record numbers + emotions — note early exits (fear) and overstays (greed)
4. **Weekly Review**: Check all Action and Safety lines. Did you follow the rules? If not — why? Fix before next Monday.

---

### ✅ Trade Checklist (Must Pass All Before Entry)

- [ ] Top-Down Analysis complete? (Monthly → Daily → 4H/1H → Entry TF)
- [ ] Trendline has at least **3 clean touches**?
- [ ] Zero-Intersection Rule satisfied? (Price never closed through line between touches)
- [ ] A candle has **closed** past the Action Line (not just a wick)?
- [ ] Safety Line clearly identified and drawn?
- [ ] Potential loss is **less than 2% of account**?

---

---

## ⚙️ Algorithmic Implementation Requirements (from video breakdowns)

*These are the specific logical components needed to code the strategy — extracted verbatim from source material*

### 1. Swing Point Detection
- Identify **Pivot Highs** and **Pivot Lows** algorithmically
- Standard approach: N bars to left and right must be lower (high) or higher (low) than the pivot bar
- These are the candidate points for trendline construction

### 2. Trendline Construction (Ray / Action Line)
- Connect confirmed swing points into a **Ray** (extends infinitely forward)
- Uptrend line: connect two or more confirmed **Pivot Lows** (higher lows)
- Downtrend line: connect two or more confirmed **Pivot Highs** (lower highs)
- Line projects its value at every future bar using slope calculation

### 3. Touch Detection
- A valid "touch" = price comes **within X ticks** of the trendline value (proximity threshold)
- Does NOT require exact hit — within a defined tolerance counts
- Parameter needed: `TouchZoneTicks` (how close = a touch)

### 4. Touch Count & Validation Rules
- **Minimum 3 touches** required for an A+ setup
- **Minimum 6 candles spacing** between any two touches ← KEY RULE
  - On 2-min chart: 6 bars = 12 minutes minimum between touches
  - On 5-min chart: 6 bars = 30 minutes minimum between touches
- Zero-Intersection Rule: between any two touches, zero candle closes through the line

### 5. Timeframe Context
- Tori checks for the "Squeeze" on **1-hour / 4-hour** charts
- Entry on **5-minute or 15-minute** chart
- For our NinjaTrader adaptation: HTF = 15-min or 1H; entry = 2-min or 5-min

### 6. Break Detection
- Trigger = **candle CLOSE** on the opposite side of the Action Line
- Wick-only crosses are ignored — must be a close
- For long entry (break of downtrend line): `Close[0] > lineValue`
- For short entry (break of uptrend line): `Close[0] < lineValue`

### 7. Safety Line Logic
- Identify the **opposing set of swing points** (if action line is downtrend → safety line uses uptrend pivots, and vice versa)
- Construct safety line the same way as action line (ray through opposing pivots)
- Both lines must be active simultaneously for a valid trade setup

### 8. Trailing Stop Logic
- Each new bar: update stop loss = **Safety Line's projected value at that bar**
- Stop only moves in the direction of profit (ratchet — never widens)
- When Safety Line value crosses through current price → exit condition triggered

### 9. Trade Management
- **One trade per trendline** — once a trendline fires a trade entry, that trendline is consumed
- New trendline must be constructed from fresh swing points for the next trade
- This prevents re-entering on a weakened/violated line

---

---

## 🔬 Deep Dive — Entry, Lines, Edge Cases (X Search Round 2)

*Source: @toritrades X account — second pass via xAI Grok, 2026-03-13*

### Entry Method
- **Market order on next bar after break candle closes** — she does NOT use pre-set limit orders at the line
- She waits, confirms, then enters
- "Sit on your hands" until the break or bounce is validated, THEN enter

### Bounce Setup — Exact Rules
- Trendline must have **2–3 clean touches**
- Must have **at least 1 week of data** from the first touch to entry
  - ⚠️ Intraday adaptation note: On 2-min chart, 1 week ≈ 975 bars. This will require significant scaling — this rule was designed for 4H/daily swing trading
- Enter on the **3rd touch** (the moment price touches and respects the line)
- The Action Line itself acts as the stop — exit if price closes through it
- Described as "simple and low-stress — no indicators needed"

### Safety Line Construction
- Always the **opposing trendline** in the channel
- Draw using 2–3 opposing swing points (if Action = downtrend highs → Safety = uptrend lows, and vice versa)
- It is **dynamic** — adjusts as the trend evolves and new swing points form
- Trail stop exactly on the Safety Line — **no buffer mentioned**
- She explicitly says: let the safety line "dictate most of my trades" — regrets not doing this in 2024 review

### Action Line vs Safety Line — Which Is Which
- Whichever line **gets broken** = Action Line (entry trigger)
- The **opposing** line = Safety Line (stop/exit)
- This is determined at the time of the trade — not predetermined
- Both lines must exist and be valid before any trade is taken

### Retest Entry (She Prefers This)
- She frequently skips the immediate break and waits for a retest
- Process:
  1. Price breaks the Action Line
  2. Price pulls back and retests the broken line from the other side
  3. If retest holds (line acts as new S/R) → enter
  4. If retest fails → fakeout, log and move on
- Combined with: full candle close + HTF alignment + double break for max confirmation

### Whipsaw Handling
- No explicit re-entry rules found
- Primary defense: don't enter on first break — wait for retest + confirmation
- "Most traders chase every break and get faked out" — her warning against reactive entries

### Session / Trading Hours
- Pre-market: gets up at 6 AM for chart analysis (not trading)
- Trading during **regular market hours** — no explicit pre-market trading
- Has shifted from pure day trading to incorporating longer holds: "3x returns while trading half the time"

### Her Actual Statistics (2024)
- 💰 **$202,000 profit** in 2024
- 📊 **76% win rate**
- 🔢 Only **17 trades** for the entire year
- ⚡ All from her own capital — one account, verified with proof
- This is **swing trading** — not intraday scalping. 17 trades/year = ~1.4 trades/month

### Trailing Stop
- Trails **exactly on the Safety Line** — no explicit buffer
- Does NOT close at support/resistance levels — lets Safety Line trail dictate the exit
- Her 2024 lesson: she had been exiting early at S/R instead of trailing properly — this cost her significant profits

### Steepness / Slope Rules
- **No explicit rules found** on minimum or maximum trendline angle
- Focus is on quality of touch points and validity — not slope angle

---

## ⚠️ Critical Intraday Adaptation Notes

These are Tori's rules designed for **swing trading 4H/Daily charts**. Key adaptations needed for 2-min ES intraday:

| Tori's Rule (Swing) | 2-Min ES Adaptation |
|---|---|
| "1 week of data from first touch" | ~975 bars on 2-min — likely needs to be a parameter, e.g., `MinBarsFromFirstTouch = 100` |
| "6+ candles between touches" on entry TF | Stays as-is on 2-min chart (6 bars = 12 min spacing) |
| "1 trade per day, 30 minutes" | May allow 1-3 trades per session on 2-min |
| 4H → 1H → 15m hierarchy | 1H/15m → 5m → 2m hierarchy for ES |
| 3 touches minimum | Stays — parameterized |
| Stop: Safety Line (no buffer) | Safety Line projected forward; hard stop as backup |

---

---

## 🔄 Official 2-Minute ES Intraday Adaptation

*This section translates Tori's 4H swing rules into equivalent 2-min intraday rules for ES futures*

### Timeframe Hierarchy (Adapted)

| Tori's Original | Our ES 2-Min Equivalent | Purpose |
|---|---|---|
| Monthly / Weekly | Daily ES chart | "Dinosaur" trend — don't fight it |
| Daily | 1-Hour chart | Intermediate trend + key S/R |
| 4-Hour / 1-Hour | 15-Minute chart | "Squeeze" zone, HTF bias |
| 5m / 15m (entry) | **2-Minute chart** | Execution — break/bounce detection |

### "1 Week of Data" Rule — Adapted
- Tori's original: 1 calendar week from first touch before entry is valid
- On 4H chart: 1 week ≈ **30 bars** (5 days × 6 bars/day)
- On 2-min chart: 1 week ≈ 975 bars — way too long for intraday
- **Adapted rule**: Use proportional bar count → `MinBarsFromFirstTouch = 30` (default)
  - 30 bars × 2 min = 60 minutes minimum from first touch to entry
  - Rationale: 1 hour on 2-min ≈ 1 week on 4H (same proportional "maturity" of the line)
  - This is a parameter — can tune: 20 = 40 min minimum, 50 = 100 min minimum

### "6 Candles Between Touches" Rule — Kept As-Is
- On 4H: 6 bars = 24 hours between touches
- On 2-min: 6 bars = 12 minutes between touches
- This is reasonable for intraday ES — keeps touches meaningfully spaced
- Parameter: `MinBarsBetweenTouches = 6`

### Trade Frequency — Adapted
- Tori: 1 trade per day (~17/year → swing)
- ES 2-min adaptation: max **2 trades per session** (RTH)
- Rationale: Intraday produces more signals; but quality > quantity — stay selective
- Parameter: `MaxTradesPerSession = 2`

### Session Definition
- Tori trades regular hours, does pre-market chart analysis only
- ES adaptation: **RTH only — 9:30 AM to 3:00 PM ET** (8:30 AM to 2:00 PM CT)
- No pre-market entries
- No trading last 15 min of session (2:45 PM CT cutoff) to avoid erratic close behavior

### HTF Bias Filter — Adapted
- Tori: Check Monthly/Weekly before any trade
- ES adaptation: Check **15-min EMA slope** for directional bias
  - 15-min 20 EMA slope positive → bullish bias → long trades only
  - 15-min 20 EMA slope negative → bearish bias → short trades only
  - Flat slope (within threshold) → no trades (choppy)

### Bounce Setup — Adapted Timing
- 3rd touch on line + 60+ min since first touch + 12+ min between any two touches
- Enter: market order at open of bar following the touch bar
- Stop: just beyond the Action Line (same line acts as stop on bounce)

### Break Setup — Adapted
- Full body candle close past Action Line
- `WaitForRetest = true` (default) — enter on retest of broken line
- Alternative: `WaitForRetest = false` → enter immediately next bar open
- Stop: Safety Line value projected forward

### Risk Management — Adapted
- Max risk per trade: **fixed ticks** (not % of account — NinjaTrader is per-contract)
- Hard stop: `HardStopTicks = 20` (5 points on ES)
- Safety Line stop takes precedence if Safety Line stop < HardStopTicks
- Daily loss limit: `-3 trades stopped out` → halt for the day

### What Stays Identical from Tori's Rules
- ✅ Zero-Intersection Rule (strictly enforced)
- ✅ Minimum 3 touches required
- ✅ Candle close filter (never wick entry)
- ✅ One trade per trendline pair
- ✅ Safety Line = exit trigger (candle close through it = exit)
- ✅ Trailing stop exactly on Safety Line (no buffer)
- ✅ Retest confirmation preferred over immediate break entry
- ✅ Double confirmation: trendline break + horizontal level (session H/L)

---

## ❓ Open Questions (Things Still Unclear)

- [x] ~~Exact touch point timing~~ → No minimum time. Quality + count only.
- [x] ~~Does she use VWAP/EMAs?~~ → No. Pure price action, trendlines only.
- [x] ~~Does she scale in?~~ → Yes, cautiously. Warns against aggressive scaling.
- [ ] How does she handle gaps (pre-market gaps in ES)?
- [ ] Specific entry timing — does she enter at the open of the next bar or use a limit order at the line?
- [ ] How many trendlines does she have active simultaneously? One pair or multiple?
- [ ] How does she define "close enough" for a bounce touch? Ticks? ATR-based?
- [ ] Does she have a max loss per day or per week rule?
- [ ] Does she use any specific bar type (candlestick, Heikin-Ashi, Renko)?

---

## 📥 Information Still Needed

Feed the following when available:
- Any live trade examples or trade recaps
- Specific ES trades she's made
- Her exact chart setup (indicators if any, bar type)
- Any mention of pre-market levels, VWAP, or other confluence tools
- Her account size / typical position sizing
- How she handles losing streaks (drawdown rules)
- Any additional videos, posts, or written content

---

*Last updated: 2026-03-13 | Status: Step 1 — Information Gathering*

# SMA1021PullbackTrailES2m_v2

## Summary
A 2-minute ES pullback strategy that trades in the direction of trend using the 10 SMA and 21 SMA, with added higher-timeframe alignment, normalized slope scoring, and room-to-structure filtering.

This is a v2 of the original `SMA1021PullbackTrailES2m.cs` concept.

## Core idea
Trade pullbacks in a strong trend:
- first preference: pullback to the 10 SMA
- if no fill after enough bars: switch entry reference to the 21 SMA
- split the trade into a profit-target leg and a trailing leg

## Timeframe
- Primary execution timeframe: **2-minute ES**
- Higher timeframe filter: **15-minute**

## Entry logic

### Long setup requires
1. Price structure is bullish on 2m:
   - recent lows remain above the 10 SMA
   - 10 SMA is above 21 SMA
2. Several recent bars stayed above the 10 SMA
3. Trend has enough quality:
   - normalized slope score of SMA10 must exceed threshold
4. Price has taken out prior highs decisively
5. 15m trend alignment is bullish:
   - 15m fast EMA > 15m slow EMA
   - 15m fast EMA slope is positive
   - 15m close is above 15m slow EMA
6. There is enough room before nearby structure blocks the trade

### Short setup requires
Mirror image of long:
1. Price structure bearish on 2m
2. Several recent bars stayed below the 10 SMA
3. Normalized slope score strong enough in bearish direction
4. Price has broken prior lows decisively
5. 15m trend alignment bearish
6. Enough room before nearby support blocks the trade

## Structure / room filter
Before entry, the strategy checks these four structure references:
- prior session high
- prior session low
- current session high
- current session low

For longs:
- finds the nearest relevant resistance level above planned entry
- requires at least `MinRoomPoints` to that level

For shorts:
- finds the nearest relevant support level below planned entry
- requires at least `MinRoomPoints` to that level

## Entry order behavior
When a valid setup is detected:
- place two limit orders at the **10 SMA**
- if price does not pull back and fill after `SwitchAfterBars`, move the working entry reference to the **21 SMA**

## Trade management
Each trade is split into two parts:

### Leg 1: PT leg
- fixed stop loss
- fixed profit target

### Leg 2: trail leg
- starts with fixed stop loss
- once PT leg hits target:
  - move trail leg stop to breakeven
  - enable trailing logic
- trailing logic then tightens based on closes relative to the 10 SMA

## Default parameters in v2
- `SlopeLookbackBars = 8`
- `SlopeAtrPeriod = 14`
- `MinNormalizedSlopeScore = 0.25`
- `PriorHighLookback = 5`
- `DecisiveTicks = 2`
- `HTFFastPeriod = 10`
- `HTFSlowPeriod = 21`
- `MinRoomPoints = 6.0`
- `SwitchAfterBars = 20`
- `StopTicks = 8`
- `ProfitTicks = 16`

## What changed from v1
Compared with the original version, v2 adds:
1. **15-minute trend alignment filter**
2. **Normalized slope score** instead of strict monotonic SMA stair-step logic
3. **Room-to-structure filter** using prior/current session high/low
4. cleanup of leftover debug noise in entry logic

## Notes
- This is still a prototype strategy version, not a production-proven system.
- The room-to-structure filter is intentionally pragmatic and uses session-based levels rather than full swing-structure modeling.
- Best next step is backtesting and reviewing trade logs, especially around the new 15m filter and room filter behavior.

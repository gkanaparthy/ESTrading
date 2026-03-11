# ESLevelFadeV0 (LOCKED)

Locked baseline spec per Gautham on 2026-03-11.

## Levels
Only these four levels are used:
- PrevSessionHigh
- PrevSessionLow
- PreMarketHigh (frozen at 08:29:59 CT)
- PreMarketLow (frozen at 08:29:59 CT)

## Session
- RTH only: 08:30:00–15:00:00 CT
- No new entries outside RTH
- Cancel all working orders at 15:00 CT

## Core params
- StopTicks = 6
- TargetTicks = 18
- MaxTradesPerLevelPerSession = N (parameter)
- ArmDistanceAtr = 1.5 (parameter)
- ReentryExcursionAtr = 2.0 (parameter)
- ClusterDistance = parameter ("180r" equivalent)

## Entry logic (true limit-order implementation)
- Do not leave blind resting orders all day.
- Only arm a level when price comes within ArmDistanceAtr * ATR.
- When armed, place a limit order at the level with side based on approach direction:
  - approach from below -> resistance behavior -> Sell Limit @ level
  - approach from above -> support behavior -> Buy Limit @ level
- If price moves away beyond arm distance before fill, cancel order.

## Re-entry / cooldown
- After a fill on a level, lock that level.
- Next trade on the same level is allowed only after price excursions at least ReentryExcursionAtr * ATR away from the level.

## Clustering / conservative selection
- If multiple of the four levels are within ClusterDistance, treat as one zone.
- Conservative selection:
  - for shorts: choose highest level in cluster
  - for longs: choose lowest level in cluster

## Exits
- Stop loss: fixed StopTicks
- Profit target: fixed TargetTicks
- No break-even logic

## Notes
This file is the frozen v0 blueprint to implement first before further variants.

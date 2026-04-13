# ESStructureAnchorAVWAP — Change Log

---

## Session: WTD Anchor Feature + Bug Fixes

### What was added

**Week-to-Date (WTD) AVWAP Anchor**

A new anchor type was added that tracks the AVWAP (volume-weighted average price) from the
start of the CME trading week — which begins every Sunday at 5:00 PM Central Time.

Think of it like this: just as the LOD/HOD AVWAP tracks fair value since the day's low or
high was made, the WTD anchor tracks fair value since the weekly session opened. Price
tends to respect this level as a point of interest throughout the week.

**How it works:**
- Every Sunday when the CME clock hits 5:00 PM CT, the strategy records that bar as the
  weekly anchor point.
- From that point forward, it computes a running AVWAP (cumulative price × volume divided
  by cumulative volume) across all bars since that Sunday open.
- If the daily LOD or HOD anchor becomes degraded (too much choppy price action around it)
  or gets invalidated, the WTD AVWAP steps in as a fallback anchor.
- If price is above the WTD AVWAP and it's clean → it can trigger a long signal.
- If price is below the WTD AVWAP and it's clean → it can trigger a short signal.
- It respects all the same filters as other anchors: trend direction, ATR, ADX, reclaim/
  reject confirmation, risk cap, etc.

**Priority order for anchor selection (unchanged hierarchy):**
1. Structural override (StructuralBull / StructuralBear) — highest priority
2. WTD AVWAP — used only when LOD/HOD is degraded or invalidated
3. LOD / HOD — default daily anchors

**New parameter added:**
- "Enable WTD Anchor (Sun 17:00 CT)" — shown in the Anchors group, defaults to ON.
  You can turn it off to go back to the old behavior for comparison.

**What you see on the chart / in the log:**
- The status box now shows a "WTD AVWAP:" line with the current value and whether
  it's choppy.
- The log prints "WTD_ANCHOR_RESET" each Sunday when it sets the new weekly anchor,
  showing the bar number, price, and week key.
- On strategy start/restart, the log prints "WTD_ANCHOR_COLD_START" showing the Sunday
  bar that was found in history, how many bars back it was, and the computed AVWAP value.

---

### Bugs fixed during review

**Bug 1 — WTD anchor was re-setting every bar during the 5 PM hour (not just once)**

The original code checked `Hour == 17`, which is true for every bar from 5:00 PM to
5:59 PM. On a 5-minute chart that's 12 bars — the anchor would move 12 times before
the hour was up. Fixed by adding a "week key" (year + week number) so the anchor only
sets once per calendar week, no matter how many bars fall in that hour.

**Bug 2 — ISOWeek.GetWeekOfYear not available in NinjaTrader's .NET version**

NinjaTrader 8 runs on .NET Framework 4.8. The ISOWeek class was mistakenly used — it
only exists in .NET 5+. Fixed by using the standard Calendar API that has been in
.NET Framework since version 1.0.

**Bug 3 — AVWAP value was wrong when the anchor bar was more than 256 bars ago**

The original implementation re-computed AVWAP by looping from the anchor bar to the
current bar on every tick. NinjaTrader's 256-bar lookback limit meant that once the
anchor was more than 255 bars in the past, the loop was silently clamped — producing
an AVWAP calculated from only the last 256 bars instead of the full week. This caused
a visible drift in the value (observed: 6878.60 vs. correct ~6867).

Fixed by replacing the per-tick loop with a running accumulator (`wtdPV`, `wtdVSum`).
The accumulator is seeded once when the anchor is set and updated by one bar per tick
going forward — so the calculation is always exact regardless of how many bars have
passed since Sunday 17:00.

**Bug 4 — Cold start was anchoring to the wrong bar (mid-week instead of Sunday 17:00)**

On strategy start or backtest, the original code used `!wtdAnchorSet` as the cold-start
condition — which fired immediately at the first eligible bar (`BarsRequiredToTrade = 50`).
If bar 50 happened to be mid-week (e.g. Wednesday 18:42), the anchor was set there,
excluding all bars from Sunday 17:00 through that point. This caused the AVWAP to be
computed from the wrong starting point (observed: 6863.80 vs. correct ~6867.05).

Fixed by splitting the logic into two distinct paths:
- **Normal weekly reset:** Fires on the live Sunday 17:00 bar, exactly as before.
- **Cold start:** Scans backward through available bar history (up to 255 bars) to find
  the most recent Sunday 17:xx bar. If found, the anchor is set there and the full
  accumulator is pre-computed from that bar to the current bar in one pass. If no
  Sunday 17:xx bar exists in the available history, the anchor is deferred and a
  "WTD_ANCHOR_DEFERRED" message is logged — the anchor will activate naturally on
  the next Sunday 17:00.

**Bug 5 — Cold-start scan was finding the oldest Sunday in history, not the most recent**

The backward scan in `TryInitWtdAnchorFromHistory` originally looped from the oldest
bar to the newest (`i = maxLookback` down to `0`), returning on the first Sunday 17:xx
hit. When two or more Sundays were present in the 255-bar history window, it would
find the oldest one — producing a two-week AVWAP instead of a one-week AVWAP.

Fixed by reversing the loop direction to scan newest-first (`i = 0` up to `maxLookback`),
so the first Sunday 17:xx encountered is always the most recent weekly open.

---

### Session transitions — how the WTD state behaves

- **Daily session reset (Mon–Fri RTH open):** The LOD/HOD and structural override reset
  every day at the new session. The WTD anchor does NOT reset — it intentionally persists
  across daily sessions because it represents the whole week.
- **New CME week (Sunday 5 PM CT):** The WTD anchor resets to that Sunday's opening bar.
  The week key advances, so it only happens once.
- **Cold start (first bar of a backtest or live session):** The strategy scans backward
  through available history to find the most recent Sunday 17:xx bar and seeds the
  running accumulator from there. If no Sunday bar is found in the available window,
  the WTD anchor is deferred until the next Sunday 17:00.
- **Degraded WTD anchor:** If price chops through the WTD AVWAP too many times during the
  week (same IsAnchorDegraded check used for all anchors), it will show "choppy=True" in
  the log and chart, and the strategy will not use it for entries.

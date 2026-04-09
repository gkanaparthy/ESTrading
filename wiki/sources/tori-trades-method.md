# Tori Trades Method — Source Synthesis

## Purpose
This page is the canonical source synthesis for the Tori-inspired trendline methodology used in ESTrendline research. It compiles the meaning of key ideas from Tori notes, video summaries, and implementation planning material.

## Core Philosophy
- Trendlines are reaction tools, not prediction tools
- The trader should let price reveal which structure matters
- Overdrawing and disconnected micro-lines reduce quality
- The best lines are obvious and structurally continuous

## Two-Line Model
### Action Line
- The line that price is interacting with for entry purposes
- In break trades, this is the broken line
- In bounce trades, this is the line being respected/rejected

### Safety Line
- The opposing structural line used for risk/invalidation
- Safety-line behavior governs exit logic and hold validity

## Construction Principles
- Wick-based construction remains primary
- Point A is the older anchor and should not drift casually
- Point B is the evolving adjustment point
- Trendlines should tell a continuous structural story, not appear as isolated unrelated rays

## Continuity / Handoff Meaning
In Gautham's language, "handoff" and "continuity" mean the same thing.

Operationally:
- higher-timeframe structure establishes the main story
- lower timeframes refine/select the active continuation of that story
- lower timeframes may introduce a new meaningful continuation only if it remains structurally consistent with the broader chain
- relevance comes from continuity and durability, not just recency

## Current Adaptation for ESTrendline_v2
The current intended timeframe stack for the ESTrading implementation is:
- 2H
- 30M
- 10M
- 2M execution

This is an adaptation for ES intraday execution. It is not a claim that Tori literally teaches this exact stack.

## Important Caution
The existing source notes also preserve an older adaptation that framed continuity through Daily → 4H → 1H → 2M. That should now be treated as legacy interpretation, not the current target design for ESTrendline_v2.

## Implications for Coding
- preserve wick-based lines
- preserve action/safety identity
- preserve continuity across timeframes
- avoid line selection that overweights fresh micro-noise
- prefer lines that span meaningful time and remain active

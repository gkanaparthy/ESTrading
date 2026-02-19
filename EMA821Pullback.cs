#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using System.Windows.Media;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class EMA821Pullback : Strategy
    {
        // Parameters
        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Trade size (contracts)", GroupName = "Parameters", Order = 0)]
        public int TradeSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use RTH (8:30-15:00)", GroupName = "Parameters", Order = 1)]
        public bool UseRTH { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Slope lookback (bars)", GroupName = "Trend Filters", Order = 10)]
        public int SlopeLookbackBars { get; set; }

        [Range(0, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Min EMA8 net move (ticks)", GroupName = "Trend Filters", Order = 11)]
        public int MinSlopeRiseTicks { get; set; }

        [Range(3, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Prior-high/low lookback (bars)", GroupName = "Trend Filters", Order = 12)]
        public int PriorHighLookback { get; set; }

        [Range(0, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Decisive take-out (ticks)", GroupName = "Trend Filters", Order = 13)]
        public int DecisiveTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Switch to EMA21 after N bars (no fill)", GroupName = "Order Mgmt", Order = 20)]
        public int SwitchAfterBars { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Stop (ticks)", GroupName = "Risk", Order = 30)]
        public int StopTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Profit target (ticks) for PT leg", GroupName = "Risk", Order = 31)]
        public int ProfitTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Trail profit multiple (x StopTicks)", GroupName = "Risk", Order = 32)]
        public int TrailProfitMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable debug logs", GroupName = "Diagnostics", Order = 40)]
        public bool DebugLogs { get; set; }

        // Indicators
        private EMA ema8;
        private EMA ema21;

        // NEW: SMA925 and Session VWAP (JeffSun-style VWAP1)
        private SMA sma925;
        private VWAP1 sessionVwap;

        // Order refs (long)
        private Order orderPT_L;    // "L_PT"
        private Order orderTrail_L; // "L_Trail"

        // Order refs (short)
        private Order orderPT_S;    // "S_PT"
        private Order orderTrail_S; // "S_Trail"

        // State (long)
        private bool setupArmedLong;
        private bool trailActiveLong;
        private bool trailingEnabledLong;    // only true after PT (L_PT) target fill
        private double trailCurrentSL_Long = double.NaN;
        private int barsAbove10SinceOrder;
        private bool usingSMA21EntryPriceLong;
        private double pendingRepriceToLong;
        private bool repriceInProgressLong;

        // State (short)
        private bool setupArmedShort;
        private bool trailActiveShort;
        private bool trailingEnabledShort;    // only true after PT (S_PT) target fill
        private double trailCurrentSL_Short = double.NaN;
        private int barsBelow10SinceOrder;
        private bool usingSMA21EntryPriceShort;
        private double pendingRepriceToShort;
        private bool repriceInProgressShort;

        // Signal names
        private const string SigPT_L    = "L_PT";
        private const string SigTrail_L = "L_Trail";
        private const string SigPT_S    = "S_PT";
        private const string SigTrail_S = "S_Trail";

        // trail leg entry prices for BE stop
        private double trailEntryPriceLong = double.NaN;
        private double trailEntryPriceShort = double.NaN;

        // session halting and loss tracking
        private bool sessionTradingHalted = false;
        private int consecutiveFullLossFamilies = 0;
        private DateTime sessionDate = DateTime.MinValue;

        // Track family outcomes per side
        private bool familyOpenLong = false;
        private bool familyOpenShort = false;
        private bool ptLegClosedLong = false;
        private bool trailLegClosedLong = false;
        private bool ptLegClosedShort = false;
        private bool trailLegClosedShort = false;
        private bool ptLegWasPTHitLong = false;
        private bool trailLegWasPTHitLong = false; // trail can’t PT, but keep symmetry
        private bool ptLegWasFullSL_Long = false;
        private bool trailLegWasFullSL_Long = false;

        private bool ptLegWasPTHitShort = false;
        private bool trailLegWasPTHitShort = false;
        private bool ptLegWasFullSL_Short = false;
        private bool trailLegWasFullSL_Short = false;

        private void D(string msg) { if (DebugLogs) Print("[EMA8_21] " + msg); }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "EMA8_21_PullbackTrail_ES2m";
                Description = "2-min ES: Pullback-to-EMA entries (long & short) with PT leg and PT-gated EMA8-trailing leg.";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 4;
                EntryHandling = EntryHandling.UniqueEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 3600;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                BarsRequiredToTrade = 50;

                TradeSize = 2;
                UseRTH = true;

                SlopeLookbackBars = 8;
                MinSlopeRiseTicks = 4;
                PriorHighLookback = 5;
                DecisiveTicks = 2;

                SwitchAfterBars = 20;

                StopTicks = 5;//8;
                ProfitTicks = 10;
                TrailProfitMultiple = 8;//4; can be 8 too 
				//AIMING FOR LEAST DRAWDOWN
				//original 8:16:4 = 100:200:400

                DebugLogs = true;
            }
            else if (State == State.Configure)
            {
            }
            else if (State == State.DataLoaded)
            {
                ema8  = EMA(8);
                ema21 = EMA(21);
                ema8.Plots[0].Brush = Brushes.DodgerBlue;
                ema8.Plots[0].Width = 2;
                ema21.Plots[0].Brush = Brushes.OrangeRed;
                ema21.Plots[0].Width = 2;
                AddChartIndicator(ema8);
                AddChartIndicator(ema21);

                // NEW: SMA(925)
                sma925 = SMA(925);
                sma925.Plots[0].Brush = Brushes.MediumPurple;
              //  AddChartIndicator(sma925);

                // NEW: Session VWAP using JeffSun-style VWAP1
                sessionVwap = VWAP1(BarsArray[0],
                    new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true);
                sessionVwap.Plots[0].Brush = Brushes.Gold;
                AddChartIndicator(sessionVwap);

                // Reset state
                setupArmedLong = setupArmedShort = false;
                trailActiveLong = trailActiveShort = false;
                trailingEnabledLong = trailingEnabledShort = false;
                orderPT_L = orderTrail_L = null;
                orderPT_S = orderTrail_S = null;

                usingSMA21EntryPriceLong = usingSMA21EntryPriceShort = false;
                repriceInProgressLong = repriceInProgressShort = false;

                barsAbove10SinceOrder = barsBelow10SinceOrder = 0;
                trailEntryPriceLong = double.NaN;
                trailEntryPriceShort = double.NaN;

                // init session tracking
                sessionTradingHalted = false;
                consecutiveFullLossFamilies = 0;
                sessionDate = DateTime.MinValue;
                ResetFamilyTracking();
            }
        }

        // helper to reset per-family flags
        private void ResetFamilyTracking()
        {
            familyOpenLong = familyOpenShort = false;

            ptLegClosedLong = trailLegClosedLong = false;
            ptLegClosedShort = trailLegClosedShort = false;

            ptLegWasPTHitLong = trailLegWasPTHitLong = false;
            ptLegWasFullSL_Long = trailLegWasFullSL_Long = false;

            ptLegWasPTHitShort = trailLegWasPTHitShort = false;
            ptLegWasFullSL_Short = trailLegWasFullSL_Short = false;
        }

        // called when any family fully closed; evaluate loss rule
        private void EvaluateAndMaybeHaltAfterFamilyClose(bool wasBothFullStops)
        {
            if (wasBothFullStops)
            {
                consecutiveFullLossFamilies++;
                D($"[HaltCheck] Family ended with BOTH legs full {StopTicks}-tick stops. Streak={consecutiveFullLossFamilies}.");
            }
            else
            {
                if (consecutiveFullLossFamilies != 0)
                    D($"[HaltCheck] Family ended NOT as double full-stop; streak reset from {consecutiveFullLossFamilies} to 0.");
                consecutiveFullLossFamilies = 0;
            }

            if (consecutiveFullLossFamilies >= 3)
            {
                // sessionTradingHalted = true;
                D("[HALT] Reached 3 consecutive full-loss families (6 full-stop losses). Halting trading for the session.");
            }
        }

        // detect session change and reset halting
        private void EnsureSessionResetIfNeeded()
        {
            // Use trading day date from bar time
            DateTime curSessionDate = Time[0].Date;
            if (sessionDate == DateTime.MinValue)
                sessionDate = curSessionDate;

            if (curSessionDate != sessionDate)
            {
                sessionDate = curSessionDate;
                sessionTradingHalted = false;
                consecutiveFullLossFamilies = 0;
                ResetFamilyTracking();
                D("[Session] New session detected. Reset halt flag and counters.");
            }
        }

       // NEW: Check if overall position is in profit using price vs. average price
private bool TradeIsInProfit()
{
    if (Position.MarketPosition == MarketPosition.Long)
        return Close[0] > Position.AveragePrice + TickSize;   // at least 1 tick in profit

    if (Position.MarketPosition == MarketPosition.Short)
        return Close[0] < Position.AveragePrice - TickSize;   // at least 1 tick in profit

    return false;
}

        // NEW: Check if this bar hit SMA(925) or session VWAP
        private bool HitImportantLevelThisBar()
        {
            if (CurrentBar < 1)
                return false;

            if (sma925 == null || sessionVwap == null)
                return false;

            if (sma925.CurrentBar < 1 || sessionVwap.CurrentBar < 1)
                return false;

            double barLow  = Low[0];
            double barHigh = High[0];

            double lvlSma925 = sma925[0];
            double lvlVwap   = sessionVwap.Output[0]; // VWAP1 main output

            bool hitSma925 = (barLow <= lvlSma925 && barHigh >= lvlSma925);
            bool hitVwap   = (barLow <= lvlVwap   && barHigh >= lvlVwap);

            bool hit = hitSma925 || hitVwap;

            if (DebugLogs && hit)
            {
                D($"[ProfitExit] Hit important level. SMA925={lvlSma925:F2} hit={hitSma925}, " +
                  $"SessionVWAP={lvlVwap:F2} hit={hitVwap}, BarRange=[{barLow:F2},{barHigh:F2}]");
            }

            return hit;
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (CurrentBar < Math.Max(BarsRequiredToTrade, 50)) return;

            // session reset check each bar
            EnsureSessionResetIfNeeded();

            // per-bar diagnostics
            if (IsFirstTickOfBar)
                LogBarDiagnostics();

            double t = ToTime(Time[0]) / 100.0;

            // RTH filter 8:30–15:00 CT
            if (UseRTH)
            {
                if (t < 830 || t >= 1500)
                {
                    if (setupArmedLong)  { CancelWorkingEntryOrdersLong();  setupArmedLong  = false; }
                    if (setupArmedShort) { CancelWorkingEntryOrdersShort(); setupArmedShort = false; }
                    return;
                }
            }

            if (t == 1702 || t == 830)
            {
                sessionTradingHalted = false;
                consecutiveFullLossFamilies = 0;
                sessionDate = DateTime.MinValue;
                ResetFamilyTracking();
            }

            // NEW: Profit-protect exit at SMA(925) or Session VWAP
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if (TradeIsInProfit() && HitImportantLevelThisBar())
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                    {
                        D("[ProfitExit] Long position in profit hit SMA925/SessionVWAP. Flattening.");
                        ExitLong("ProfitExit_SMA_VWAP");
                    }
                    else if (Position.MarketPosition == MarketPosition.Short)
                    {
                        D("[ProfitExit] Short position in profit hit SMA925/SessionVWAP. Flattening.");
                        ExitShort("ProfitExit_SMA_VWAP");
                    }
                }
            }

            // Manage trailing stops / exits
            if (IsFirstTickOfBar)
            {
                // LONG trail leg
                if (trailActiveLong && trailingEnabledLong)
                {
                    // 1) Check for profit target first (TrailProfitMultiple * StopTicks)
                    if (ShouldExitTrailLongByProfit())
                    {
                        D($"[Long] Trail leg exiting by profit: >= {TrailProfitMultiple}x StopTicks from entry.");
                        ExitLong(SigTrail_L);
                    }
                    // 2) If no profit exit, manage SL using EMA close-cross rule
                    else if (Close[1] < ema8[1])
                    {
                        double candidate = Instrument.MasterInstrument.RoundToTickSize(Low[1] - TickSize);
                        if (double.IsNaN(trailCurrentSL_Long) || candidate > trailCurrentSL_Long)
                        {
                            SetStopLoss(SigTrail_L, CalculationMode.Price, candidate, false);
                            trailCurrentSL_Long = candidate;
                            D($"[Long] Trail stop tightened to {candidate} after close below EMA8 (post-PT).");
                        }
                    }
                }

                // SHORT trail leg
                if (trailActiveShort && trailingEnabledShort)
                {
                    // 1) Check for profit target first
                    if (ShouldExitTrailShortByProfit())
                    {
                        D($"[Short] Trail leg exiting by profit: >= {TrailProfitMultiple}x StopTicks from entry.");
                        ExitShort(SigTrail_S);
                    }
                    // 2) Otherwise, EMA-based trailing
                    else if (Close[1] > ema8[1])
                    {
                        double candidate = Instrument.MasterInstrument.RoundToTickSize(High[1] + TickSize);
                        if (double.IsNaN(trailCurrentSL_Short) || candidate < trailCurrentSL_Short)
                        {
                            SetStopLoss(SigTrail_S, CalculationMode.Price, candidate, false);
                            trailCurrentSL_Short = candidate;
                            D($"[Short] Trail stop tightened to {candidate} after close above EMA8 (post-PT).");
                        }
                    }
                }
            }

            // If not flat, don't arm new families
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            // if session halted, do not arm new trades
            if (sessionTradingHalted)
            {
                if (setupArmedLong)  { CancelWorkingEntryOrdersLong();  setupArmedLong  = false; }
                if (setupArmedShort) { CancelWorkingEntryOrdersShort(); setupArmedShort = false; }
                return;
            }

            // Maintain pending long orders (reprice/invalidations/switch)
            if (setupArmedLong)
            {
                if (IsFirstTickOfBar)
                {
                    if (Low[1] > ema8[1]) barsAbove10SinceOrder++;
                    else barsAbove10SinceOrder = 0;

                    bool structureOkL = Close[1] > ema8[1] && ema8[1] > ema21[1];
                    if (!structureOkL)
                    {
                        D("[Long] Setup invalidated; canceling working entries.");
                        CancelWorkingEntryOrdersLong();
                        setupArmedLong = false;
                        usingSMA21EntryPriceLong = false;
                        barsAbove10SinceOrder = 0;
                    }
                    else if (!usingSMA21EntryPriceLong && barsAbove10SinceOrder >= SwitchAfterBars)
                    {
                        usingSMA21EntryPriceLong = true;
                        barsAbove10SinceOrder = 0;
                        D($"[Long] Switching entry reference to EMA21 after {SwitchAfterBars} bars above EMA8. Will keep repricing live orders each bar.");
                    }

                    double desiredLongEntry = usingSMA21EntryPriceLong ? ema21[0] : ema8[0];
                    RepricePendingLongEntries(desiredLongEntry);
                }
                return;
            }

            // Maintain pending short orders (reprice/invalidations/switch)
            if (setupArmedShort)
            {
                if (IsFirstTickOfBar)
                {
                    if (High[1] < ema8[1]) barsBelow10SinceOrder++;
                    else barsBelow10SinceOrder = 0;

                    bool structureOkS = Close[1] < ema8[1] && ema8[1] < ema21[1];
                    if (!structureOkS)
                    {
                        D("[Short] Setup invalidated; canceling working entries.");
                        CancelWorkingEntryOrdersShort();
                        setupArmedShort = false;
                        usingSMA21EntryPriceShort = false;
                        barsBelow10SinceOrder = 0;
                    }
                    else if (!usingSMA21EntryPriceShort && barsBelow10SinceOrder >= SwitchAfterBars)
                    {
                        usingSMA21EntryPriceShort = true;
                        barsBelow10SinceOrder = 0;
                        D($"[Short] Switching entry reference to EMA21 after {SwitchAfterBars} bars below EMA8. Will keep repricing live orders each bar.");
                    }

                    double desiredShortEntry = usingSMA21EntryPriceShort ? ema21[0] : ema8[0];
                    RepricePendingShortEntries(desiredShortEntry);
                }
                return;
            }

            // Not armed: try to arm one side
            if (IsFirstTickOfBar)
            {
                if (LongEntryConditionsMet())
                {
                    double entryPriceL = Instrument.MasterInstrument.RoundToTickSize(ema8[0]);
                    usingSMA21EntryPriceLong = false;
                    barsAbove10SinceOrder = 0;
                    SubmitTwoLongLimits(entryPriceL);
                    setupArmedLong = true;

                    // mark new family started (long)
                    familyOpenLong = true;
                    ptLegClosedLong = trailLegClosedLong = false;
                    ptLegWasPTHitLong = trailLegWasPTHitLong = false;
                    ptLegWasFullSL_Long = trailLegWasFullSL_Long = false;

                    D($"[Long] Setup armed. Submitted 2 limit buys at {entryPriceL} (EMA8).");
                    return;
                }

                if (ShortEntryConditionsMet())
                {
                    double entryPriceS = Instrument.MasterInstrument.RoundToTickSize(ema8[0]);
                    usingSMA21EntryPriceShort = false;
                    barsBelow10SinceOrder = 0;
                    SubmitTwoShortLimits(entryPriceS);
                    setupArmedShort = true;

                    // mark new family started (short)
                    familyOpenShort = true;
                    ptLegClosedShort = trailLegClosedShort = false;
                    ptLegWasPTHitShort = trailLegWasPTHitShort = false;
                    ptLegWasFullSL_Short = trailLegWasFullSL_Short = false;

                    D($"[Short] Setup armed. Submitted 2 limit sells at {entryPriceS} (EMA8).");
                    return;
                }
            }
        }

        private bool LongEntryConditionsMet()
        {
            bool above10 = Low[1] > ema8[1];
            bool ema8Above21 = ema8[1] > ema21[1];
            if (!above10 || !ema8Above21) return false;

            bool last3Above = Low[1] > ema8[1] && Low[2] > ema8[2] && Low[3] > ema8[3]
                && Low[4] > ema8[4] && Low[5] > ema8[5] && Low[6] > ema8[6] && ema8[6] > ema21[6];
            Print("last3Above " + last3Above);
            if (!last3Above) return false;

            bool monotonicUp = ema8[1] > ema8[2] && ema8[2] > ema8[3] && ema8[3] > ema8[4] && ema8[4] > ema8[5];
            double netRise = ema8[1] - ema8[SlopeLookbackBars + 1];
            double minAbs = MinSlopeRiseTicks * TickSize;
            Print("monotonicUp " + monotonicUp);
            Print("netRise " + netRise);
            if (!(monotonicUp && netRise >= minAbs)) return false;

            double last3High = Math.Max(High[1], Math.Max(High[2], High[3]));
            double priorHigh = double.MinValue;
            for (int i = 4; i <= 3 + PriorHighLookback; i++)
                priorHigh = Math.Max(priorHigh, High[i]);
            double required = priorHigh + DecisiveTicks * TickSize;
            if (last3High < required) return false;
            return true;
        }

        private bool ShortEntryConditionsMet()
        {
            bool below10 = High[1] < ema8[1];
            bool ema8Below21 = ema8[1] < ema21[1];
            if (!below10 || !ema8Below21) return false;

            bool last3Below = High[1] < ema8[1] && High[2] < ema8[2] && High[3] < ema8[3]
                && High[4] < ema8[4] && High[5] < ema8[5] && High[6] < ema8[6] && ema8[6] < ema21[6];
            if (!last3Below) return false;

            bool monotonicDown = ema8[1] < ema8[2] && ema8[2] < ema8[3] && ema8[3] < ema8[4] && ema8[4] < ema8[5];
            double netFall = ema8[SlopeLookbackBars + 1] - ema8[1]; // positive when falling
            double minAbs = MinSlopeRiseTicks * TickSize;
            if (!(monotonicDown && netFall >= minAbs)) return false;

            double last3Low = Math.Min(Low[1], Math.Min(Low[2], Low[3]));
            double priorLow = double.MaxValue;
            for (int i = 4; i <= 3 + PriorHighLookback; i++)
                priorLow = Math.Min(priorLow, Low[i]);
            double required = priorLow - DecisiveTicks * TickSize;
            if (last3Low > required) return false;

            return true;
        }

        private void SubmitTwoLongLimits(double entryPrice)
        {
            int total = Math.Max(2, TradeSize);
            int qPT = Math.Max(1, total / 2);
            int qTrail = Math.Max(1, total - qPT);

            trailingEnabledLong = false;

            SetStopLoss(SigPT_L, CalculationMode.Ticks, StopTicks, false);
            SetProfitTarget(SigPT_L, CalculationMode.Ticks, ProfitTicks);
            SetStopLoss(SigTrail_L, CalculationMode.Ticks, StopTicks, false);

            EnterLongLimit(0, true, qPT, entryPrice, SigPT_L);
            EnterLongLimit(0, true, qTrail, entryPrice, SigTrail_L);
        }

        private void SubmitTwoShortLimits(double entryPrice)
        {
            int total = Math.Max(2, TradeSize);
            int qPT = Math.Max(1, total / 2);
            int qTrail = Math.Max(1, total - qPT);

            trailingEnabledShort = false;

            SetStopLoss(SigPT_S, CalculationMode.Ticks, StopTicks, false);
            SetProfitTarget(SigPT_S, CalculationMode.Ticks, ProfitTicks);
            SetStopLoss(SigTrail_S, CalculationMode.Ticks, StopTicks, false);

            EnterShortLimit(0, true, qPT, entryPrice, SigPT_S);
            EnterShortLimit(0, true, qTrail, entryPrice, SigTrail_S);
        }

        private void CancelWorkingEntryOrdersLong()
        {
            if (orderPT_L != null)    { try { CancelOrder(orderPT_L); } catch { } }
            if (orderTrail_L != null) { try { CancelOrder(orderTrail_L); } catch { } }
        }

        private void CancelWorkingEntryOrdersShort()
        {
            if (orderPT_S != null)    { try { CancelOrder(orderPT_S); } catch { } }
            if (orderTrail_S != null) { try { CancelOrder(orderTrail_S); } catch { } }
        }

        private bool IsOrderActive(Order o)
        {
            return o != null && (o.OrderState == OrderState.Working
                || o.OrderState == OrderState.Accepted
                || o.OrderState == OrderState.PartFilled
                || o.OrderState == OrderState.ChangePending
                || o.OrderState == OrderState.Submitted);
        }

        private void TryChangeOrderPrice(Order o, double newLimit)
        {
            if (!IsOrderActive(o))
                return;

            int workingQty = Math.Max(0, o.Quantity - o.Filled);
            if (workingQty <= 0)
                return;

            double curr = o.LimitPrice;
            newLimit = Instrument.MasterInstrument.RoundToTickSize(newLimit);

            if (!double.IsNaN(curr) && Math.Abs(curr - newLimit) < TickSize * 0.5)
                return;

            ChangeOrder(o, workingQty, newLimit, o.StopPrice);
        }

        private void RepricePendingLongEntries(double desiredPrice)
        {
            desiredPrice = Instrument.MasterInstrument.RoundToTickSize(desiredPrice);
            TryChangeOrderPrice(orderPT_L, desiredPrice);
            TryChangeOrderPrice(orderTrail_L, desiredPrice);
            D($"[Long] Repriced working entry orders to {desiredPrice} (EMA{(usingSMA21EntryPriceLong ? "21" : "8")}).");
        }

        private void RepricePendingShortEntries(double desiredPrice)
        {
            desiredPrice = Instrument.MasterInstrument.RoundToTickSize(desiredPrice);
            TryChangeOrderPrice(orderPT_S, desiredPrice);
            TryChangeOrderPrice(orderTrail_S, desiredPrice);
            D($"[Short] Repriced working entry orders to {desiredPrice} (EMA{(usingSMA21EntryPriceShort ? "21" : "8")}).");
        }

        private bool ShouldExitTrailLongByProfit()
        {
            if (!trailActiveLong || double.IsNaN(trailEntryPriceLong))
                return false;

            double profitTicks = TrailProfitMultiple * StopTicks * TickSize;
            double targetPrice = trailEntryPriceLong + profitTicks;

            return High[0] >= targetPrice;
        }

        private bool ShouldExitTrailShortByProfit()
        {
            if (!trailActiveShort || double.IsNaN(trailEntryPriceShort))
                return false;

            double profitTicks = TrailProfitMultiple * StopTicks * TickSize;
            double targetPrice = trailEntryPriceShort - profitTicks;

            return Low[0] <= targetPrice;
        }

        private void LogBarDiagnostics()
        {
            try
            {
                double t = ToTime(Time[0]) / 100.0;
                bool inRTH = !UseRTH || (t >= 830 && t < 1500);

                double px    = Instrument.MasterInstrument.RoundToTickSize(Close[0]);
                double s10_0 = ema8[0];
                double s21_0 = ema21[0];
                double s10_1 = ema8[1];
                double s21_1 = ema21[1];

                bool canCheck3    = CurrentBar >= 4;
                bool canCheckSlope = CurrentBar >= (SlopeLookbackBars + 2);
                bool canCheckMono  = CurrentBar >= 6;
                bool canCheckPHPL  = CurrentBar >= (4 + PriorHighLookback);

                bool longStructure  = canCheck3 && (Low[1] > s10_1 && s10_1 > s21_1);
                bool shortStructure = canCheck3 && (High[1] < s10_1 && s10_1 < s21_1);

                bool last3Above = canCheck3 &&
                    (Low[1] > ema8[1] && Low[2] > ema8[2] && Low[3] > ema8[3]);
                bool last3Below = canCheck3 &&
                    (High[1] < ema8[1] && High[2] < ema8[2] && High[3] < ema8[3]);

                bool monoUp   = canCheckMono &&
                    (ema8[1] > ema8[2] && ema8[2] > ema8[3] && ema8[3] > ema8[4] && ema8[4] > ema8[5]);
                bool monoDown = canCheckMono &&
                    (ema8[1] < ema8[2] && ema8[2] < ema8[3] && ema8[3] < ema8[4] && ema8[4] < ema8[5]);

                double minAbs = MinSlopeRiseTicks * TickSize;
                string slopeLongStr = "n/a";
                string slopeShortStr = "n/a";
                if (canCheckSlope)
                {
                    double netRise = ema8[1] - ema8[SlopeLookbackBars + 1];
                    double netFall = ema8[SlopeLookbackBars + 1] - ema8[1];
                    slopeLongStr  = $"{netRise:F2} {(netRise >= minAbs ? ">=" : "<")} {minAbs:F2}";
                    slopeShortStr = $"{netFall:F2} {(netFall >= minAbs ? ">=" : "<")} {minAbs:F2}";
                }

                string takeoutPH = "n/a";
                string takeoutPL = "n/a";
                if (canCheckPHPL)
                {
                    double last3High = Math.Max(High[1], Math.Max(High[2], High[3]));
                    double priorHigh = double.MinValue;
                    for (int i = 4; i <= 3 + PriorHighLookback; i++)
                        priorHigh = Math.Max(priorHigh, High[i]);
                    double reqH = priorHigh + DecisiveTicks * TickSize;
                    takeoutPH = $"{last3High:F2} {(last3High >= reqH ? ">=" : "<")} req {reqH:F2}";

                    double last3Low = Math.Min(Low[1], Math.Min(Low[2], Low[3]));
                    double priorLow = double.MaxValue;
                    for (int i = 4; i <= 3 + PriorHighLookback; i++)
                        priorLow = Math.Min(priorLow, Low[i]);
                    double reqL = priorLow - DecisiveTicks * TickSize;
                    takeoutPL = $"{last3Low:F2} {(last3Low <= reqL ? "<=" : ">")} req {reqL:F2}";
                }

                D($"[Diag] {Time[0]} | InRTH={inRTH} | Flat={(Position.MarketPosition == MarketPosition.Flat)} " +
                  $"| ArmedL={setupArmedLong} ArmedS={setupArmedShort} | RepriceL={repriceInProgressLong} RepriceS={repriceInProgressShort}");

                D($"[Diag] Price={px} | EMA8[0]={s10_0:F2} EMA21[0]={s21_0:F2} | EMA8[1]={s10_1:F2} EMA21[1]={s21_1:F2}");

                D($"[Diag][Long] structure(Low[1]>EMA8[1] && EMA8[1]>EMA21[1])={longStructure} " +
                  $"last3Above={last3Above} monoUp={monoUp} slope={slopeLongStr} takeOutPH={takeoutPH}");

                D($"[Diag][Short] structure(High[1]<EMA8[1] && EMA8[1]<EMA21[1])={shortStructure} " +
                  $"last3Below={last3Below} monoDown={monoDown} slope={slopeShortStr} takeOutPL={takeoutPL}");

                D($"[Diag] Switch: barsAbove8={barsAbove10SinceOrder} usingEMA21L={usingSMA21EntryPriceLong} | " +
                  $"barsBelow8={barsBelow10SinceOrder} usingEMA21S={usingSMA21EntryPriceShort}");

                D($"[Diag] Trail: activeL={trailActiveLong} enabledL={trailingEnabledLong} curSL_L={(double.IsNaN(trailCurrentSL_Long) ? double.NaN : trailCurrentSL_Long)} | " +
                  $"activeS={trailActiveShort} enabledS={trailingEnabledShort} curSL_S={(double.IsNaN(trailCurrentSL_Short) ? double.NaN : trailCurrentSL_Short)}");

                D($"[Diag][Halt] sessionHalted={sessionTradingHalted} consecutiveFullLossFamilies={consecutiveFullLossFamilies}");
            }
            catch (Exception ex)
            {
                D($"[Diag] Error logging diagnostics: {ex.Message}");
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
            int filled, double averageFillPrice, OrderState orderState, DateTime time,
            ErrorCode error, string nativeError)
        {
            if (order == null) return;

            // Track long entries
            if (order.Name == SigPT_L)
            {
                orderPT_L = order;
                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected) orderPT_L = null;
            }
            else if (order.Name == SigTrail_L)
            {
                orderTrail_L = order;
                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected) orderTrail_L = null;
            }

            // Track short entries
            if (order.Name == SigPT_S)
            {
                orderPT_S = order;
                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected) orderPT_S = null;
            }
            else if (order.Name == SigTrail_S)
            {
                orderTrail_S = order;
                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected) orderTrail_S = null;
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution?.Order == null) return;

            string on = execution.Order.Name ?? string.Empty;
            string fromSig = execution.Order.FromEntrySignal ?? string.Empty;

            // Long trailing leg fill
            if (execution.Order.Name == SigTrail_L &&
                execution.Order.OrderAction == OrderAction.Buy &&
                execution.Order.OrderState != OrderState.PartFilled)
            {
                trailActiveLong = true;
                double ep = execution.Order.AverageFillPrice == 0 ? price : execution.Order.AverageFillPrice;

                trailEntryPriceLong = Instrument.MasterInstrument.RoundToTickSize(ep);
                trailCurrentSL_Long = Instrument.MasterInstrument.RoundToTickSize(ep - StopTicks * TickSize);
                D($"[Long] L_Trail filled @ {ep}. Initial SL ~ {trailCurrentSL_Long}. Trailing disabled until PT hit.");
                setupArmedLong = false;
                barsAbove10SinceOrder = 0;
            }

            // Short trailing leg fill
            if (execution.Order.Name == SigTrail_S &&
                execution.Order.OrderAction == OrderAction.SellShort &&
                execution.Order.OrderState != OrderState.PartFilled)
            {
                trailActiveShort = true;
                double ep = execution.Order.AverageFillPrice == 0 ? price : execution.Order.AverageFillPrice;

                trailEntryPriceShort = Instrument.MasterInstrument.RoundToTickSize(ep);
                trailCurrentSL_Short = Instrument.MasterInstrument.RoundToTickSize(ep + StopTicks * TickSize);
                D($"[Short] S_Trail filled @ {ep}. Initial SL ~ {trailCurrentSL_Short}. Trailing disabled until PT hit.");
                setupArmedShort = false;
                barsBelow10SinceOrder = 0;
            }

            // Detect PT target fills to enable trailing
            bool isPTExit = on.IndexOf("Profit target", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isPTExit && fromSig == SigPT_L)
            {
                // Mark PT leg as PT hit for long family
                ptLegClosedLong = true;
                ptLegWasPTHitLong = true;

                if (trailActiveLong && !double.IsNaN(trailEntryPriceLong))
                {
                    double be = Instrument.MasterInstrument.RoundToTickSize(trailEntryPriceLong);
                    SetStopLoss(SigTrail_L, CalculationMode.Price, be, false);
                    trailCurrentSL_Long = be;
                    D($"[Long] PT1 hit ({ProfitTicks}t). L_Trail SL moved to BE @ {be}. Trailing ENABLED.");
                }
                else
                {
                    D($"[Long] PT1 hit ({ProfitTicks}t). Trailing ENABLED. (No active trail leg to move to BE.)");
                }

                trailingEnabledLong = true;  // now we can start trailing on close below EMA8
            }

            if (isPTExit && fromSig == SigPT_S)
            {
                // Mark PT leg as PT hit for short family
                ptLegClosedShort = true;
                ptLegWasPTHitShort = true;

                if (trailActiveShort && !double.IsNaN(trailEntryPriceShort))
                {
                    double be = Instrument.MasterInstrument.RoundToTickSize(trailEntryPriceShort);
                    SetStopLoss(SigTrail_S, CalculationMode.Price, be, false);
                    trailCurrentSL_Short = be;
                    D($"[Short] PT1 hit ({ProfitTicks}t). S_Trail SL moved to BE @ {be}. Trailing ENABLED.");
                }
                else
                {
                    D($"[Short] PT1 hit ({ProfitTicks}t). Trailing ENABLED. (No active trail leg to move to BE.)");
                }

                trailingEnabledShort = true; // now we can start trailing on close above EMA8
            }

            // Clear trail state on exits
            bool isStopExit = on.IndexOf("Stop loss", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isExitEvent = isStopExit || isPTExit;

            if (isExitEvent && fromSig == SigTrail_L)
            {
                // classify trail leg exit for family accounting
                trailLegClosedLong = true;
                if (isStopExit)
                {
                    // approximation: before PT, it's full SL
                    bool full = !trailingEnabledLong;
                    trailLegWasFullSL_Long = full;
                }
                else
                {
                    trailLegWasPTHitLong = false; // trail has no PT in this design
                }

                trailActiveLong = false;
                trailingEnabledLong = false;
                trailCurrentSL_Long = double.NaN;
                D("[Long] L_Trail exit detected; trail state cleared.");

                // If both legs have closed for this family, evaluate outcome
                if (ptLegClosedLong && trailLegClosedLong && familyOpenLong)
                {
                    bool bothFull = ptLegWasFullSL_Long && trailLegWasFullSL_Long;
                    EvaluateAndMaybeHaltAfterFamilyClose(bothFull);
                    familyOpenLong = false;
                }
            }

            if (isExitEvent && fromSig == SigTrail_S)
            {
                // classify trail leg exit for family accounting
                trailLegClosedShort = true;
                if (isStopExit)
                {
                    bool full = !trailingEnabledShort; // approximation: before PT, it's full SL
                    trailLegWasFullSL_Short = full;
                }
                else
                {
                    trailLegWasPTHitShort = false;
                }

                trailActiveShort = false;
                trailingEnabledShort = false;
                trailCurrentSL_Short = double.NaN;
                D("[Short] S_Trail exit detected; trail state cleared.");

                if (ptLegClosedShort && trailLegClosedShort && familyOpenShort)
                {
                    bool bothFull = ptLegWasFullSL_Short && trailLegWasFullSL_Short;
                    EvaluateAndMaybeHaltAfterFamilyClose(bothFull);
                    familyOpenShort = false;
                }
            }

            // classify PT leg stop exits (full loss) when they happen
            if (isStopExit && fromSig == SigPT_L)
            {
                ptLegClosedLong = true;
                ptLegWasFullSL_Long = true;

                if (ptLegClosedLong && trailLegClosedLong && familyOpenLong)
                {
                    bool bothFull = ptLegWasFullSL_Long && trailLegWasFullSL_Long;
                    EvaluateAndMaybeHaltAfterFamilyClose(bothFull);
                    familyOpenLong = false;
                }
            }
            if (isStopExit && fromSig == SigPT_S)
            {
                ptLegClosedShort = true;
                ptLegWasFullSL_Short = true;

                if (ptLegClosedShort && trailLegClosedShort && familyOpenShort)
                {
                    bool bothFull = ptLegWasFullSL_Short && trailLegWasFullSL_Short;
                    EvaluateAndMaybeHaltAfterFamilyClose(bothFull);
                    familyOpenShort = false;
                }
            }
        }
    }
}
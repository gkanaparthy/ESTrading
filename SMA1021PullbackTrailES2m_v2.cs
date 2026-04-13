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
    public class SMA10_21_PullbackTrail_ES2m_v2 : Strategy
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
        [Display(Name = "Min SMA10 net move (ticks)", GroupName = "Trend Filters", Order = 11)]
        public int MinSlopeRiseTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Slope normalization ATR bars", GroupName = "Trend Filters", Order = 12)]
        public int SlopeAtrPeriod { get; set; }

        [Range(0.0, double.MaxValue), NinjaScriptProperty]
        [Display(Name = "Min normalized slope score", GroupName = "Trend Filters", Order = 13)]
        public double MinNormalizedSlopeScore { get; set; }

        [Range(3, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Prior-high/low lookback (bars)", GroupName = "Trend Filters", Order = 13)]
        public int PriorHighLookback { get; set; }

        [Range(0, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Decisive take-out (ticks)", GroupName = "Trend Filters", Order = 14)]
        public int DecisiveTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "15m fast EMA", GroupName = "Higher TF Filter", Order = 15)]
        public int HTFFastPeriod { get; set; }

        [Range(2, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "15m slow EMA", GroupName = "Higher TF Filter", Order = 16)]
        public int HTFSlowPeriod { get; set; }

        [Range(0.0, double.MaxValue), NinjaScriptProperty]
        [Display(Name = "Min room to next structure (points)", GroupName = "Structure Filter", Order = 17)]
        public double MinRoomPoints { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Switch to SMA21 after N bars (no fill)", GroupName = "Order Mgmt", Order = 20)]
        public int SwitchAfterBars { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Stop (ticks)", GroupName = "Risk", Order = 30)]
        public int StopTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Profit target (ticks) for PT leg", GroupName = "Risk", Order = 31)]
        public int ProfitTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable debug logs", GroupName = "Diagnostics", Order = 40)]
        public bool DebugLogs { get; set; }

        // Indicators
        private SMA sma10;
        private SMA sma21;
        private EMA htfEmaFast;
        private EMA htfEmaSlow;
        private ATR atr;
        private PriorDayOHLC priorDay;

        // Order refs (long)
        private Order orderPT_L;      // "L_PT"
        private Order orderTrail_L;   // "L_Trail"

        // Order refs (short)
        private Order orderPT_S;      // "S_PT"
        private Order orderTrail_S;   // "S_Trail"

        // State (long)
        private bool setupArmedLong;
        private bool trailActiveLong;
        private bool trailingEnabledLong;       // NEW: only true after PT (L_PT) target fill
        private double trailCurrentSL_Long = double.NaN;
        private int barsAbove10SinceOrder;
        private bool usingSMA21EntryPriceLong;
        private double pendingRepriceToLong;
        private bool repriceInProgressLong;

        // State (short)
        private bool setupArmedShort;
        private bool trailActiveShort;
        private bool trailingEnabledShort;      // NEW: only true after PT (S_PT) target fill
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
		
		// NEW: trail leg entry prices for BE stop
		private double trailEntryPriceLong = double.NaN;
		private double trailEntryPriceShort = double.NaN;

        // ADD: session halting and loss tracking
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

        private void D(string msg) { if (DebugLogs) Print("[SMA10_21] " + msg); }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "SMA10_21_PullbackTrail_ES2m_v2";
                Description = "2-min ES v2: pullback-to-SMA entries with 15m trend alignment, structure-room filter, and normalized slope score.";
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
                SlopeAtrPeriod = 14;
                MinNormalizedSlopeScore = 0.25;
                PriorHighLookback = 5;//20;
                DecisiveTicks = 2;
                HTFFastPeriod = 10;
                HTFSlowPeriod = 21;
                MinRoomPoints = 6.0;

                SwitchAfterBars = 20;

                StopTicks = 8;
                ProfitTicks = 16;

                DebugLogs = true;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 15);
            }
            else if (State == State.DataLoaded)
            {
				sma10 = SMA(10);
				sma21 = SMA(21);
                htfEmaFast = EMA(Closes[1], HTFFastPeriod);
                htfEmaSlow = EMA(Closes[1], HTFSlowPeriod);
                atr = ATR(SlopeAtrPeriod);
                priorDay = PriorDayOHLC();
				sma10.Plots[0].Brush = Brushes.DodgerBlue;
				sma10.Plots[0].Width = 2;
				sma21.Plots[0].Brush = Brushes.OrangeRed;
				sma21.Plots[0].Width = 2;
				AddChartIndicator(sma10);
				AddChartIndicator(sma21);

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

                // ADD: init session tracking
                sessionTradingHalted = false;
                consecutiveFullLossFamilies = 0;
                sessionDate = DateTime.MinValue;
                ResetFamilyTracking();
            }
        }

        // ADD: helper to reset per-family flags
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

        // ADD: called when any family fully closed; evaluate loss rule
        private void EvaluateAndMaybeHaltAfterFamilyClose(bool wasBothFullStops)
        {
            if (wasBothFullStops)
            {
                consecutiveFullLossFamilies++;
                D($"[HaltCheck] Family ended with BOTH legs full 8-tick stops. Streak={consecutiveFullLossFamilies}.");
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

        // ADD: detect session change and reset halting
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

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (CurrentBar < Math.Max(BarsRequiredToTrade, 50)) return;

            // ADD: session reset check each bar
            EnsureSessionResetIfNeeded();
			
			// NEW: per-bar diagnostics
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
			
			if (t==1702 || t==830)
			{
				sessionTradingHalted = false;
                consecutiveFullLossFamilies = 0;
                sessionDate = DateTime.MinValue;
                ResetFamilyTracking();
			}

            // Manage trailing stops on bar close conditions
            if (IsFirstTickOfBar)
            {
                if (trailActiveLong && trailingEnabledLong && Close[1] < sma10[1])
                {
                    double candidate = Instrument.MasterInstrument.RoundToTickSize(Low[1] - TickSize);
                    if (double.IsNaN(trailCurrentSL_Long) || candidate > trailCurrentSL_Long)
                    {
                        SetStopLoss(SigTrail_L, CalculationMode.Price, candidate, false);
                        trailCurrentSL_Long = candidate;
                        D($"[Long] Trail stop tightened to {candidate} after close below SMA10 (post-PT).");
                    }
                }

                if (trailActiveShort && trailingEnabledShort && Close[1] > sma10[1])
                {
                    double candidate = Instrument.MasterInstrument.RoundToTickSize(High[1] + TickSize);
                    if (double.IsNaN(trailCurrentSL_Short) || candidate < trailCurrentSL_Short)
                    {
                        SetStopLoss(SigTrail_S, CalculationMode.Price, candidate, false);
                        trailCurrentSL_Short = candidate;
                        D($"[Short] Trail stop tightened to {candidate} after close above SMA10 (post-PT).");
                    }
                }
            }

            // If not flat, don't arm new families
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            // ADD: if session halted, do not arm new trades
            if (sessionTradingHalted)
            {
                // Also ensure any pending entries are canceled
                if (setupArmedLong)  { CancelWorkingEntryOrdersLong();  setupArmedLong  = false; }
                if (setupArmedShort) { CancelWorkingEntryOrdersShort(); setupArmedShort = false; }
                return;
            }

            // Maintain pending long orders (reprice/invalidations/switch)
            if (setupArmedLong)
            {
                if (IsFirstTickOfBar)
                {
                    if (Low[1] > sma10[1]) barsAbove10SinceOrder++;
                    else barsAbove10SinceOrder = 0;

                    bool structureOkL = Close[1] > sma10[1] && sma10[1] > sma21[1];
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
					    D($"[Long] Switching entry reference to SMA21 after {SwitchAfterBars} bars above SMA10. Will keep repricing live orders each bar.");
					}
					
					double desiredLongEntry = usingSMA21EntryPriceLong ? sma21[0] : sma10[0];
				    RepricePendingLongEntries(desiredLongEntry);
                }
                return;
            }

            // Maintain pending short orders (reprice/invalidations/switch)
            if (setupArmedShort)
            {
                if (IsFirstTickOfBar)
                {
                    if (High[1] < sma10[1]) barsBelow10SinceOrder++;
                    else barsBelow10SinceOrder = 0;

                    bool structureOkS = Close[1] < sma10[1] && sma10[1] < sma21[1];
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
					    D($"[Short] Switching entry reference to SMA21 after {SwitchAfterBars} bars below SMA10. Will keep repricing live orders each bar.");
					}
					double desiredShortEntry = usingSMA21EntryPriceShort ? sma21[0] : sma10[0];
					RepricePendingShortEntries(desiredShortEntry);
                }
                return;
            }

            // Not armed: try to arm one side
            if (IsFirstTickOfBar)
            {
                if (LongEntryConditionsMet())
                {
                    double entryPriceL = Instrument.MasterInstrument.RoundToTickSize(sma10[0]);
                    usingSMA21EntryPriceLong = false;
                    barsAbove10SinceOrder = 0;
                    SubmitTwoLongLimits(entryPriceL);
                    setupArmedLong = true;

                    // ADD: mark new family started (long)
                    familyOpenLong = true;
                    ptLegClosedLong = trailLegClosedLong = false;
                    ptLegWasPTHitLong = trailLegWasPTHitLong = false;
                    ptLegWasFullSL_Long = trailLegWasFullSL_Long = false;

                    D($"[Long] Setup armed. Submitted 2 limit buys at {entryPriceL} (SMA10).");
                    return;
                }

                if (ShortEntryConditionsMet())
                {
                    double entryPriceS = Instrument.MasterInstrument.RoundToTickSize(sma10[0]);
                    usingSMA21EntryPriceShort = false;
                    barsBelow10SinceOrder = 0;
                    SubmitTwoShortLimits(entryPriceS);
                    setupArmedShort = true;

                    // ADD: mark new family started (short)
                    familyOpenShort = true;
                    ptLegClosedShort = trailLegClosedShort = false;
                    ptLegWasPTHitShort = trailLegWasPTHitShort = false;
                    ptLegWasFullSL_Short = trailLegWasFullSL_Short = false;

                    D($"[Short] Setup armed. Submitted 2 limit sells at {entryPriceS} (SMA10).");
                    return;
                }
            }
        }

        private bool HTFTrendAlignedLong()
        {
            if (BarsArray.Length < 2 || CurrentBars[1] < Math.Max(HTFFastPeriod, HTFSlowPeriod) + 3) return false;
            bool fastAboveSlow = htfEmaFast[1] > htfEmaSlow[1];
            bool fastSlopePositive = htfEmaFast[1] > htfEmaFast[2];
            bool closeAboveSlow = Closes[1][1] > htfEmaSlow[1];
            return fastAboveSlow && fastSlopePositive && closeAboveSlow;
        }

        private bool HTFTrendAlignedShort()
        {
            if (BarsArray.Length < 2 || CurrentBars[1] < Math.Max(HTFFastPeriod, HTFSlowPeriod) + 3) return false;
            bool fastBelowSlow = htfEmaFast[1] < htfEmaSlow[1];
            bool fastSlopeNegative = htfEmaFast[1] < htfEmaFast[2];
            bool closeBelowSlow = Closes[1][1] < htfEmaSlow[1];
            return fastBelowSlow && fastSlopeNegative && closeBelowSlow;
        }

        private double PriorSessionHigh()
        {
            double val = priorDay.PriorHigh[0];
            return double.IsNaN(val) ? double.NaN : val;
        }

        private double PriorSessionLow()
        {
            double val = priorDay.PriorLow[0];
            return double.IsNaN(val) ? double.NaN : val;
        }

        private double CurrentSessionHigh()
        {
            if (CurrentBar < 2) return double.NaN;
            double hi = double.MinValue;
            DateTime d = Time[0].Date;
            for (int i = 0; i < CurrentBar; i++)
            {
                if (Time[i].Date != d) break;
                hi = Math.Max(hi, High[i]);
            }
            return hi == double.MinValue ? double.NaN : hi;
        }

        private double CurrentSessionLow()
        {
            if (CurrentBar < 2) return double.NaN;
            double lo = double.MaxValue;
            DateTime d = Time[0].Date;
            for (int i = 0; i < CurrentBar; i++)
            {
                if (Time[i].Date != d) break;
                lo = Math.Min(lo, Low[i]);
            }
            return lo == double.MaxValue ? double.NaN : lo;
        }

        private bool HasRoomForLong()
        {
            double plannedEntry = usingSMA21EntryPriceLong ? sma21[0] : sma10[0];
            double[] levels = new double[] { PriorSessionHigh(), PriorSessionLow(), CurrentSessionHigh(), CurrentSessionLow() };
            double nearestResistance = double.MaxValue;
            foreach (double level in levels)
            {
                if (double.IsNaN(level)) continue;
                if (level > plannedEntry && level < nearestResistance)
                    nearestResistance = level;
            }
            if (nearestResistance == double.MaxValue) return true;
            return (nearestResistance - plannedEntry) >= MinRoomPoints;
        }

        private bool HasRoomForShort()
        {
            double plannedEntry = usingSMA21EntryPriceShort ? sma21[0] : sma10[0];
            double[] levels = new double[] { PriorSessionHigh(), PriorSessionLow(), CurrentSessionHigh(), CurrentSessionLow() };
            double nearestSupport = double.MinValue;
            foreach (double level in levels)
            {
                if (double.IsNaN(level)) continue;
                if (level < plannedEntry && level > nearestSupport)
                    nearestSupport = level;
            }
            if (nearestSupport == double.MinValue) return true;
            return (plannedEntry - nearestSupport) >= MinRoomPoints;
        }

        private double NormalizedSlopeScore()
        {
            if (CurrentBar < SlopeLookbackBars + 2 || atr == null || atr[1] <= 0) return double.NaN;
            double netMove = sma10[1] - sma10[SlopeLookbackBars + 1];
            return netMove / atr[1];
        }

        private bool LongEntryConditionsMet()
        {
            if (CurrentBar < (3 + PriorHighLookback + 5)) return false;

            bool above10 = Low[1] > sma10[1];
            bool sma10Above21 = sma10[1] > sma21[1];
            if (!above10 || !sma10Above21) return false;
            if (!HTFTrendAlignedLong()) return false;

            bool last3Above = Low[1] > sma10[1] && Low[2] > sma10[2] && Low[3] > sma10[3]
								&& Low[4] > sma10[4] && Low[5] > sma10[5] && Low[6] > sma10[6] && sma10[6]> sma21[6];
            if (!last3Above) return false;

            double netMove = sma10[1] - sma10[SlopeLookbackBars + 1];
            if (netMove < MinSlopeRiseTicks * TickSize) return false;
            double slopeScore = NormalizedSlopeScore();
            if (double.IsNaN(slopeScore) || slopeScore < MinNormalizedSlopeScore) return false;

            double last3High = Math.Max(High[1], Math.Max(High[2], High[3]));
            double priorHigh = double.MinValue;
            for (int i = 4; i <= 3 + PriorHighLookback; i++)
                priorHigh = Math.Max(priorHigh, High[i]);
            double required = priorHigh + DecisiveTicks * TickSize;
            if (last3High < required) return false;
            if (!HasRoomForLong()) return false;
            return true;
        }

        private bool ShortEntryConditionsMet()
        {
            if (CurrentBar < (3 + PriorHighLookback + 5)) return false;

            bool below10 = High[1] < sma10[1];
            bool sma10Below21 = sma10[1] < sma21[1];
            if (!below10 || !sma10Below21) return false;
            if (!HTFTrendAlignedShort()) return false;

            bool last3Below = High[1] < sma10[1] && High[2] < sma10[2] && High[3] < sma10[3]
								&& High[4] < sma10[4] && High[5] < sma10[5] && High[6] < sma10[6] && sma10[6]< sma21[6];
            if (!last3Below) return false;

            double netMove = sma10[SlopeLookbackBars + 1] - sma10[1];
            if (netMove < MinSlopeRiseTicks * TickSize) return false;
            double slopeScore = -NormalizedSlopeScore();
            if (double.IsNaN(slopeScore) || slopeScore < MinNormalizedSlopeScore) return false;

            double last3Low = Math.Min(Low[1], Math.Min(Low[2], Low[3]));
            double priorLow = double.MaxValue;
            for (int i = 4; i <= 3 + PriorHighLookback; i++)
                priorLow = Math.Min(priorLow, Low[i]);
            double required = priorLow - DecisiveTicks * TickSize;
            if (last3Low > required) return false;
            if (!HasRoomForShort()) return false;

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
                              || o.OrderState == OrderState.Submitted
		    );
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
            D($"[Long] Repriced working entry orders to {desiredPrice} (SMA{(usingSMA21EntryPriceLong ? "21" : "10")}).");
        }

        private void RepricePendingShortEntries(double desiredPrice)
        {
            desiredPrice = Instrument.MasterInstrument.RoundToTickSize(desiredPrice);
            TryChangeOrderPrice(orderPT_S, desiredPrice);
            TryChangeOrderPrice(orderTrail_S, desiredPrice);
            D($"[Short] Repriced working entry orders to {desiredPrice} (SMA{(usingSMA21EntryPriceShort ? "21" : "10")}).");
        }

		private void LogBarDiagnostics()
        {
            try
            {
                double t = ToTime(Time[0]) / 100.0;
                bool inRTH = !UseRTH || (t >= 830 && t < 1500);

                double px     = Instrument.MasterInstrument.RoundToTickSize(Close[0]);
                double s10_0  = sma10[0];
                double s21_0  = sma21[0];
                double s10_1  = sma10[1];
                double s21_1  = sma21[1];

                bool canCheck3     = CurrentBar >= 4;
                bool canCheckSlope = CurrentBar >= (SlopeLookbackBars + 2);
                bool canCheckMono  = CurrentBar >= 6;
                bool canCheckPHPL  = CurrentBar >= (4 + PriorHighLookback);

                bool longStructure  = canCheck3 && (Low[1] > s10_1 && s10_1 > s21_1);
                bool shortStructure = canCheck3 && (High[1] < s10_1 && s10_1 < s21_1);

                bool last3Above = canCheck3 &&
                                  (Low[1] > sma10[1] && Low[2] > sma10[2] && Low[3] > sma10[3]);
                bool last3Below = canCheck3 &&
                                  (High[1] < sma10[1] && High[2] < sma10[2] && High[3] < sma10[3]);

                bool monoUp   = canCheckMono &&
                                (sma10[1] > sma10[2] && sma10[2] > sma10[3] && sma10[3] > sma10[4] && sma10[4] > sma10[5]);
                bool monoDown = canCheckMono &&
                                (sma10[1] < sma10[2] && sma10[2] < sma10[3] && sma10[3] < sma10[4] && sma10[4] < sma10[5]);

                double minAbs = MinSlopeRiseTicks * TickSize;
                string slopeLongStr = "n/a";
                string slopeShortStr = "n/a";
                if (canCheckSlope)
                {
                    double netRise = sma10[1] - sma10[SlopeLookbackBars + 1];
                    double netFall = sma10[SlopeLookbackBars + 1] - sma10[1];
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

                D($"[Diag] Price={px} | SMA10[0]={s10_0:F2} SMA21[0]={s21_0:F2} | SMA10[1]={s10_1:F2} SMA21[1]={s21_1:F2}");

                D($"[Diag][Long] structure(Low[1]>SMA10[1] && SMA10[1]>SMA21[1])={longStructure} " +
                  $"last3Above={last3Above} monoUp={monoUp} slope={slopeLongStr} takeOutPH={takeoutPH}");

                D($"[Diag][Short] structure(High[1]<SMA10[1] && SMA10[1]<SMA21[1])={shortStructure} " +
                  $"last3Below={last3Below} monoDown={monoDown} slope={slopeShortStr} takeOutPL={takeoutPL}");

                D($"[Diag] Switch: barsAbove10={barsAbove10SinceOrder} usingSMA21L={usingSMA21EntryPriceLong} | " +
                  $"barsBelow10={barsBelow10SinceOrder} usingSMA21S={usingSMA21EntryPriceShort}");

                D($"[Diag] Trail: activeL={trailActiveLong} enabledL={trailingEnabledLong} curSL_L={(double.IsNaN(trailCurrentSL_Long) ? double.NaN : trailCurrentSL_Long)} | " +
                  $"activeS={trailActiveShort} enabledS={trailingEnabledShort} curSL_S={(double.IsNaN(trailCurrentSL_Short) ? double.NaN : trailCurrentSL_Short)}");

                // ADD: show halting state
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

                trailingEnabledLong = true;  // now we can start trailing on close below SMA10
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

                trailingEnabledShort = true; // now we can start trailing on close above SMA10
            }

            // Clear trail state on exits
            bool isStopExit = on.IndexOf("Stop loss", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isExitEvent = isStopExit || isPTExit;

            if (isExitEvent && fromSig == SigTrail_L)
            {
                // ADD: classify trail leg exit for family accounting
                trailLegClosedLong = true;
                if (isStopExit)
                {
                    // We consider full stop if stop exit fired from initial full distance; Ninja doesn't expose offset easily.
                    // We assume any stop-loss exit on the trail leg before trailing is a full SL. Given we move to BE after PT,
                    // a later stop at BE is NOT full. So we approximate: if trailingEnabledLong == false at exit -> full SL.
                    bool full = !trailingEnabledLong; // approximation for full 8-tick loss before PT
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
                // ADD: classify trail leg exit for family accounting
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

            // ADD: classify PT leg stop exits (full loss) when they happen
            if (isStopExit && fromSig == SigPT_L)
            {
                ptLegClosedLong = true;
                // If PT leg hits its stop, that is a full SL by definition here (StopTicks = 8)
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
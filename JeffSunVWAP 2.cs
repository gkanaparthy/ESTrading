#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using System.Xml.Serialization;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class JeffSunVWAP : Strategy
    {
        // Config
        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Trade size (contracts)", GroupName = "Parameters", Order = 0)]
        public double TradeSize { get; set; }

        [Display(Name = "Use RTH window (8:30-15:00)?", GroupName = "Parameters", Order = 1)]
        public bool UseRTH { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
        [Display(Name = "Anchor VWAP from 1st", GroupName = "Parameters", Order = 2)]
        public DateTime AnchorFrom { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.ChartAnchorTimeEditor")]
        [Display(Name = "Anchor VWAP from 2nd", GroupName = "Parameters", Order = 3)]
        public DateTime AnchorFrom2 { get; set; }

        // VWAP flag applies only to the 2nd set
        [Display(Name = "Use VWAP for 2nd set (instead of AVWAP2)?", GroupName = "Parameters", Order = 4)]
        public bool UseVWAPForSecondSet { get; set; }

        [Display(Name = "Enable debug logs", GroupName = "Parameters", Order = 5)]
        public bool DebugLogs { get; set; }

        // Indicators
        private VWAP1  vwap;    // Daily VWAP
        private AVWAP2 avwap1;  // From AnchorFrom
        private AVWAP2 avwap2;  // From AnchorFrom2

        // Cached levels
        private double vwPrice;  // rounded VWAP
        private double gateATR;  // 1.5 * ATR(1m) gating

        // Entry arming flags
        private bool long1Flag, short1Flag; // 1st set (AVWAP1)
        private bool long2Flag, short2Flag; // 2nd set (AVWAP2 or VWAP)

        // State for PT2-level handling for Leg3
        private readonly HashSet<string> activeLeg3 = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> leg3MovedToBE = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> leg3EntryPrice = new Dictionary<string, double>(StringComparer.Ordinal);

        // Leg3 AVWAP + SL state
        private readonly Dictionary<string, int> l3AnchorBar = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> l3CumPV = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> l3CumVol = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> l3EntryTime = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> l3CurrentSL = new Dictionary<string, double>(StringComparer.Ordinal);

        // Debug helper
        private void D(string msg) { if (DebugLogs) Print(msg); }

        protected override void OnStateChange()
        {
   
   

            if (State == State.SetDefaults)
            {
                Name = "JeffSunVWAP";
                Description = "AVWAP/VWAP scaled strategy for ES 1-min. Three legs per entry with 3/6/9-tick stops and 18/36/48-tick targets. After PT1: reduce remaining SLs by 3 ticks each. At PT2-level: move Leg3 to BE.";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection =  9; // three legs per set, up to two sets sequentially
                EntryHandling = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 3600;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                BarsRequiredToTrade = 24;
                IsInstantiatedOnEachOptimizationIteration = true;

                TradeSize = 3;    // default: 1 per leg
                UseRTH = true;    // trade 8:30–15:00 by default
                UseVWAPForSecondSet = true;
                DebugLogs = true;

                AnchorFrom = DateTime.Parse("12:30 AM");
                AnchorFrom2 = DateTime.Parse("12:30 AM");

                PrintTo = PrintTo.OutputTab2;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 1); // for ATR
                AddDataSeries(BarsPeriodType.Tick,   1); // for OnEachTick responsiveness
            }
            else if (State == State.DataLoaded)
            {
                vwap = VWAP1(BarsArray[0],
                    new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true);
                AddChartIndicator(vwap);

                avwap1 = AVWAP2(BarsArray[0], AnchorFrom,
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true);
                AddChartIndicator(avwap1);

                avwap2 = AVWAP2(BarsArray[0], AnchorFrom2,
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true);
                AddChartIndicator(avwap2);

                D("[JeffSunVWAP] Indicators loaded.");
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < BarsRequiredToTrade || BarsInProgress != 0)
                return;
			
			  double t = ToTime(Time[0]) / 100.0;

            // RTH filter: 8:30 to 15:00
            if (UseRTH)
            {
              
                if (t < 830 || t >= 1500)
                    return;
            }
			
			if (BarsInProgress == 0 && IsFirstTickOfBar && t == 1702 )
{
              long2Flag  = false;
                short1Flag = false;
                long2Flag      = false;
                short2Flag     = false;

              //  dayOverVar = false;
                //lastThreeTrades = 0;

              //  lastVwapTouchBarIndex = -1;
                //lastVwapTouchedValue  = double.NaN;

              //  entryOrder = null;
                Print("[JeffSunVWAP] Daily reset completed.");
}

            // Anchor checks
            bool anchor1Active = AnchorFrom  != DateTime.Parse("12:30 AM");
            bool anchor2Active = AnchorFrom2 != DateTime.Parse("12:30 AM");

            // Cache per-tick values (only compute AVWAP outputs if anchor active)
            double price = Close[0];

            double av1 = double.NaN;
            if (anchor1Active)
                av1 = Instrument.MasterInstrument.RoundToTickSize(avwap1.Output[0]);
				  
																											 

            double av2 = double.NaN;
            if (anchor2Active)
                av2 = Instrument.MasterInstrument.RoundToTickSize(avwap2.Output[0]);
            else if (!UseVWAPForSecondSet)
                D("[JeffSunVWAP] AVWAP2 anchor not set (AnchorFrom2 == 12:30 AM). Skipping 2nd set AVWAP logic.");

            double atr1m = ATR(Closes[1], 14)[0];
            gateATR    = Instrument.MasterInstrument.RoundToTickSize(3 * atr1m);
            vwPrice    = Instrument.MasterInstrument.RoundToTickSize(vwap.Output[0]);

											
													 
								
			 
																					
																						 
			 
				
			 
											   
			   

            // 2nd set: VWAP if UseVWAPForSecondSet; otherwise AVWAP2 only if anchor2Active
            double level2;
            bool useSecondSet = false;
            if (UseVWAPForSecondSet)
            {
                level2 = vwPrice;
                useSecondSet = true;
										  
            }
            else if (anchor2Active)
            {
                level2 = av2;
                useSecondSet = true;
            }
            else
            {
                level2 = double.NaN;
                long2Flag = short2Flag = false;
            }

							   
			 
																																	 
																																			  
															 
			   

            // Move Leg3 to BE when PT2 level is reached (price-based), for any active Leg3
            if (activeLeg3.Count > 0)
            {
                foreach (var sig in activeLeg3)
                {
                    if (leg3MovedToBE.Contains(sig))
                        continue;

                    if (!leg3EntryPrice.TryGetValue(sig, out var ep))
                        continue;

                    bool isLong = sig.StartsWith("L3_");
                    double pt2Level = isLong
                        ? Instrument.MasterInstrument.RoundToTickSize(ep + 36 * TickSize)
                        : Instrument.MasterInstrument.RoundToTickSize(ep - 36 * TickSize);

                    bool crossed = isLong ? (price >= pt2Level) : (price <= pt2Level);
                    if (crossed)
                    {
                        // Set BE stop for Leg3
                        double be = Instrument.MasterInstrument.RoundToTickSize(ep);
                        SetStopLoss(sig, CalculationMode.Price, be, false);
                        leg3MovedToBE.Add(sig);
                        l3CurrentSL[sig] = be; // track current SL to prevent widening later
                        D($"[JeffSunVWAP] PT2 level reached for {sig}. Stop moved to BE @ {be}");
                    }
                }
            }

            // Leg3 AVWAP-based stop logic (evaluated on bar close; use IsFirstTickOfBar to update using prior bar)
            if (activeLeg3.Count > 0 && IsFirstTickOfBar)
            {
                foreach (var sig in activeLeg3)
                {
                    if (!l3AnchorBar.TryGetValue(sig, out var ab))
                        continue; // not initialized yet

                    // Update cumulative sums with the bar that just closed (index 1)
                    if (CurrentBar > ab)
                    {
                        double tpPrev = (Open[1] + High[1] + Low[1] + Close[1]) / 4.0;
                        if (!l3CumPV.ContainsKey(sig)) l3CumPV[sig] = 0;
                        if (!l3CumVol.ContainsKey(sig)) l3CumVol[sig] = 0;

                        l3CumPV[sig]  += tpPrev * Volume[1];
                        l3CumVol[sig]  += Volume[1];

                        double avwapFromEntry = l3CumVol[sig] > 0 ? l3CumPV[sig] / l3CumVol[sig] : double.NaN;
                        if (!l3EntryTime.TryGetValue(sig, out var et)) continue;

                        // Require > 10 minutes since entry, evaluated against the bar that just closed
                        if ((Time[1] - et).TotalMinutes >= 10)
                        {
                            bool isLong = sig.StartsWith("L3_");
                            if (!leg3EntryPrice.TryGetValue(sig, out var ep)) continue;
                            double be = Instrument.MasterInstrument.RoundToTickSize(ep);

                            if (isLong)
                            {
                                if (Close[1] < avwapFromEntry && Close[0] > Close[1])
                                {
                                    double candidate = Instrument.MasterInstrument.RoundToTickSize(Math.Max(be, Low[1]-0.25));
                                    double curSL = l3CurrentSL.TryGetValue(sig, out var v) ? v : double.NegativeInfinity;
                                    // Only tighten (never widen)
                                    if (candidate > curSL)
                                    {
                                        SetStopLoss(sig, CalculationMode.Price, candidate, false);
                                        l3CurrentSL[sig] = candidate;
                                        D($"[JeffSunVWAP] AVWAP cross-down for {sig}. Tightened SL to {candidate} (BE={be}, Low[1]={Low[1]-0.25})");
                                    }
                                }
                            }
                            else // short
                            {
                                if (Close[1] > avwapFromEntry &&  Close[0] < Close[1])
                                {
                                    double candidate = Instrument.MasterInstrument.RoundToTickSize(Math.Min(be, High[1]+0.5));
                                    double curSL = l3CurrentSL.TryGetValue(sig, out var v) ? v : double.PositiveInfinity;
                                    // Only tighten (never widen)
                                    if (candidate < curSL )
                                    {
                                        SetStopLoss(sig, CalculationMode.Price, candidate, false);
                                        l3CurrentSL[sig] = candidate;
                                        D($"[JeffSunVWAP] AVWAP cross-up for {sig}. Tightened SL to {candidate} (BE={be}, High[1]={High[1]})");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Entries only when flat to avoid overlapping families
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            int totalQty = Math.Max(1, (int)Math.Round(TradeSize));
            int q1 = Math.Max(1, totalQty / 3);
            int q2 = Math.Max(1, totalQty / 3);
            int q3 = Math.Max(1, totalQty - q1 - q2);

            // 1st set (AVWAP1) – only if anchor1Active
            if (anchor1Active)
            {
                if (long1Flag)
                {
                    SubmitScaledEntriesLong(av1, "1st", q1, q2, q3);
													 
                    return;
                }
                else if (short1Flag)
                {
                    SubmitScaledEntriesShort(av1, "1st", q1, q2, q3);
													 
                    return;
                }
                else
                {
                    if (price - gateATR > av1) { long1Flag = true; short1Flag = false; }
                    else if (price + gateATR < av1) { short1Flag = true; long1Flag = false; }
                }
            }

            // 2nd set (VWAP or AVWAP2) – only if UseVWAPForSecondSet OR anchor2Active
            if (useSecondSet)
            {
						  
                if (long2Flag)
                {
                    SubmitScaledEntriesLong(level2, "2nd", q1, q2, q3);
													 
                    return;
                }
                else if (short2Flag)
                {
                    SubmitScaledEntriesShort(level2, "2nd", q1, q2, q3);
													 
                    return;
                }
                else
                {
						   
                    if (price - gateATR > level2) { long2Flag = true; short2Flag = false; D("[JeffSunVWAP] 2nd SET long flag " + Time[0]); }
                    else if (price + gateATR < level2) { short2Flag = true; long2Flag = false; D("[JeffSunVWAP] 2nd SET SHORT flag at " + Time[0]); }
                    else
                        Print("gateATR : " + gateATR + " long2Flag: " + long2Flag + " short2Flag: " + short2Flag);
                }
            }
        }

        private void SubmitScaledEntriesLong(double entryPrice, string setSuffix, int q1, int q2, int q3)
        {
            string s1 = $"L1_{setSuffix}";
            string s2 = $"L2_{setSuffix}";
            string s3 = $"L3_{setSuffix}";

            // Initial SLs and PTs
            SetStopLoss(s1, CalculationMode.Ticks, 3, false);
            SetStopLoss(s2, CalculationMode.Ticks, 6, false);
            SetStopLoss(s3, CalculationMode.Ticks, 9, false);

            SetProfitTarget(s1, CalculationMode.Ticks, 18);
            SetProfitTarget(s2, CalculationMode.Ticks, 36);
            // No fixed PT for Leg3 per user request

            EnterLongLimit(0, false, q1, entryPrice, s1);
            EnterLongLimit(0, false, q2, entryPrice, s2);
            EnterLongLimit(0, false, q3, entryPrice, s3);

																														 
        }

        private void SubmitScaledEntriesShort(double entryPrice, string setSuffix, int q1, int q2, int q3)
        {
            string s1 = $"S1_{setSuffix}";
            string s2 = $"S2_{setSuffix}";
            string s3 = $"S3_{setSuffix}";

            SetStopLoss(s1, CalculationMode.Ticks, 3, false);
            SetStopLoss(s2, CalculationMode.Ticks, 6, false);
            SetStopLoss(s3, CalculationMode.Ticks, 9, false);

            SetProfitTarget(s1, CalculationMode.Ticks, 18);
            SetProfitTarget(s2, CalculationMode.Ticks, 36);
            // No fixed PT for Leg3 per user request

            EnterShortLimit(0, false, q1, entryPrice, s1);
            EnterShortLimit(0, false, q2, entryPrice, s2);
            EnterShortLimit(0, false, q3, entryPrice, s3);

																														  
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution?.Order == null)
                return;

            string orderName = execution.Order.Name ?? string.Empty;
            string fromSig   = execution.Order.FromEntrySignal ?? string.Empty;

            // Record Leg3 entry price/time and activate it; initialize AVWAP anchor and SL state
            if ((orderName.StartsWith("L3_") && execution.Order.OrderAction == OrderAction.Buy)
             || (orderName.StartsWith("S3_") && execution.Order.OrderAction == OrderAction.SellShort))
            {
                double ep = execution.Order.AverageFillPrice == 0 ? price : execution.Order.AverageFillPrice;
                leg3EntryPrice[orderName] = ep;
                activeLeg3.Add(orderName);
                leg3MovedToBE.Remove(orderName);

                // Initialize AVWAP state for this Leg3
                l3AnchorBar[orderName] = CurrentBar;
                l3CumPV[orderName] = 0;
                l3CumVol[orderName] = 0;
                l3EntryTime[orderName] = execution.Order.Time;

                // Initialize current SL state to initial 9-tick SL
                if (orderName.StartsWith("L3_"))
                    l3CurrentSL[orderName] = Instrument.MasterInstrument.RoundToTickSize(ep - 9 * TickSize);
                else
                    l3CurrentSL[orderName] = Instrument.MasterInstrument.RoundToTickSize(ep + 9 * TickSize);

                D($"[JeffSunVWAP] Leg3 entry filled {orderName} @ {ep}");
                long1Flag  = false;
                short1Flag = false;
                long2Flag  = false;
                short2Flag = false;
            }

            // Remove Leg3 from active set on its exit (stop or target) and clear its AVWAP/SL state
            if (fromSig.StartsWith("L3_") || fromSig.StartsWith("S3_"))
            {
                if (orderName.IndexOf("Profit target", StringComparison.OrdinalIgnoreCase) >= 0
                 || orderName.IndexOf("Stop loss", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    activeLeg3.Remove(fromSig);
                    leg3MovedToBE.Remove(fromSig);
                    leg3EntryPrice.Remove(fromSig);
                    l3AnchorBar.Remove(fromSig);
                    l3CumPV.Remove(fromSig);
                    l3CumVol.Remove(fromSig);
                    l3EntryTime.Remove(fromSig);
                    l3CurrentSL.Remove(fromSig);

                    D($"[JeffSunVWAP] Leg3 exit detected for {fromSig} via {orderName}");
                }
            }

            // PT1 filled? Reduce both remaining SLs by 3 ticks (Leg2: 6→3, Leg3: 9→6)
            if (orderName.IndexOf("Profit target", StringComparison.OrdinalIgnoreCase) >= 0
                && (fromSig.StartsWith("L1_") || fromSig.StartsWith("S1_")))
            {
                string family = fromSig.Substring(fromSig.IndexOf('_') + 1); // "1st" / "2nd"
                bool isLong = fromSig[0] == 'L';

                string leg2 = isLong ? $"L2_{family}" : $"S2_{family}";
                string leg3 = isLong ? $"L3_{family}" : $"S3_{family}";

                SetStopLoss(leg2, CalculationMode.Ticks, 3, false);
                SetStopLoss(leg3, CalculationMode.Ticks, 6, false);

                // Update tracked current SL for Leg3 to 6 ticks from entry
                if (leg3EntryPrice.TryGetValue(leg3, out var ep))
                {
                    if (isLong)
                        l3CurrentSL[leg3] = Instrument.MasterInstrument.RoundToTickSize(ep - 6 * TickSize);
                    else
                        l3CurrentSL[leg3] = Instrument.MasterInstrument.RoundToTickSize(ep + 6 * TickSize);
                }

                D($"[JeffSunVWAP] PT1 filled ({fromSig}). Adjusted {leg2} SL->3t and {leg3} SL->6t.");
            }

            // PT2 for Leg2 filled? Move remaining Leg3 to BE
            if (orderName.IndexOf("Profit target", StringComparison.OrdinalIgnoreCase) >= 0
                && (fromSig.StartsWith("L2_") || fromSig.StartsWith("S2_")))
            {
                string family = fromSig.Substring(fromSig.IndexOf('_') + 1); // "1st" / "2nd"
                bool isLong = fromSig[0] == 'L';

																		 
                string leg3 = isLong ? $"L3_{family}" : $"S3_{family}";

                // Use Leg3 entry price as BE (safer than Position.AveragePrice)
                if (leg3EntryPrice.TryGetValue(leg3, out var ep3))
                {
                    double be = Instrument.MasterInstrument.RoundToTickSize(ep3);
                    SetStopLoss(leg3, CalculationMode.Price, be, false);
                    leg3MovedToBE.Add(leg3);
                    l3CurrentSL[leg3] = be;
                    D($"[JeffSunVWAP] PT2 filled ({fromSig}). Adjusted {leg3} SL->BE @ {be}.");
                }
                else
                {
                    // Fallback (should rarely happen)
                    SetStopLoss(leg3, CalculationMode.Price, Position.AveragePrice, false);
                    l3CurrentSL[leg3] = Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice);
                    D($"[JeffSunVWAP] PT2 filled ({fromSig}). Adjusted {leg3} SL->BE @ Position.AveragePrice fallback.");
                }
            }
        }
    }
}
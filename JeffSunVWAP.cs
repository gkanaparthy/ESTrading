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
        private SMA    sma10;   // currently unused (kept to minimize chart disruption)

        // Cached levels
        private double vwPrice;  // rounded VWAP
        private double gateATR;  // 3 * ATR(1m) gating

        // Entry arming flags
        private bool long1Flag, short1Flag; // 1st set (AVWAP1)
        private bool long2Flag, short2Flag; // 2nd set (AVWAP2 or VWAP)

        // Active legs and entry prices (per signal)
        private readonly HashSet<string> activeLeg1 = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> activeLeg2 = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> activeLeg3 = new HashSet<string>(StringComparer.Ordinal);

        private readonly Dictionary<string, double> leg1EntryPrice = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> leg2EntryPrice = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> leg3EntryPrice = new Dictionary<string, double>(StringComparer.Ordinal);

        // Track families ("1st"/"2nd")
        private readonly HashSet<string> pt1FilledFamilies = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> pt1AssignedLeg = new Dictionary<string, string>(StringComparer.Ordinal); // family -> current PT1 owner (e.g., "L2_1st")
        private readonly Dictionary<string, bool> familyIsLong = new Dictionary<string, bool>(StringComparer.Ordinal); // family -> direction
// Track L3 entry time for SMA(10) gated exit
private readonly Dictionary<string, DateTime> l3EntryTime = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        // Debug helper
        private void D(string msg) { if (DebugLogs) Print(msg); }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "JeffSunVWAP";
                Description = "AVWAP/VWAP scaled strategy for ES 1-min. Three legs per entry with immediate, trigger-based exits (no SetStopLoss/SetProfitTarget). PT1 rolls to next leg if earlier leg exited.";
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

                // Keep SMA on chart (not used for exits) to minimize disruption
                sma10 = SMA(Closes[0], 10);
                AddChartIndicator(sma10);

                D("[JeffSunVWAP] Indicators loaded.");
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < BarsRequiredToTrade || BarsInProgress != 0)
                return;

            double t = ToTime(Time[0]) / 100.0;

            // Close any open trades at 3:00 PM before applying RTH filter
            if (t == 1501)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong();
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort();

                // No more processing after 3 PM
                long2Flag  = false;
                short1Flag = false;
                long2Flag  = false;
                short2Flag = false;
                return;
            }

            // RTH filter: 8:30 to 15:00 for entries/management
            if (UseRTH)
            {
                if (t < 830 || t >= 1500)
                    return;
            }

            if (BarsInProgress == 0 && IsFirstTickOfBar && t == 1701 )
            {
                long2Flag  = false;
                short1Flag = false;
                long2Flag  = false;
                short2Flag = false;

                Print("[JeffSunVWAP] Daily reset completed.");
            }

            // Anchor checks
            bool anchor1Active = AnchorFrom  != DateTime.Parse("12:30 AM");
            bool anchor2Active = AnchorFrom2 != DateTime.Parse("12:30 AM");

            // Cache per-tick values
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

            // =========================
            // Position management block
            // =========================

            // Immediate adverse exits per leg
            if (activeLeg1.Count > 0 || activeLeg2.Count > 0 || activeLeg3.Count > 0)
            {
                // Helper local function to compute thresholds and exit
                void CheckAdverseAndExit(string sig)
                {
                    if (!TryGetEntryPrice(sig, out var ep))
                        return;

                    bool isLong = sig[0] == 'L';
                    // Tighten after PT1 is filled for the family:
// L2: 6 -> 3, L3: 9 -> 6 (L1 remains 3)
string family = GetFamily(sig);
bool tightened = !string.IsNullOrEmpty(family) && pt1FilledFamilies.Contains(family);

int ticks;
if (sig[1] == '1')
    ticks = 3;
else if (sig[1] == '2')
    ticks = tightened ? 3 : 6;
else
    ticks = tightened ? 6 : 9;
                    double thresh = Instrument.MasterInstrument.RoundToTickSize(
                        isLong ? ep - ticks * TickSize : ep + ticks * TickSize
                    );

                    bool trigger = isLong ? (price <= thresh) : (price >= thresh);
                    if (trigger)
                    {
                       D($"[JeffSunVWAP] Adverse trigger for {sig} at {price} (ep={ep}, ticks={ticks}, tightened={tightened}).");
                        ExitLeg(sig);
                    }
                }

                foreach (var sig in activeLeg1.ToArray()) CheckAdverseAndExit(sig);
                foreach (var sig in activeLeg2.ToArray()) CheckAdverseAndExit(sig);
                foreach (var sig in activeLeg3.ToArray()) CheckAdverseAndExit(sig);

                // PT1 rolling owner trigger
                foreach (var kvp in pt1AssignedLeg.ToArray())
                {
                    string family = kvp.Key;
                    string owner  = kvp.Value;

                    if (pt1FilledFamilies.Contains(family))
                        continue; // already filled

                    // Owner must still be active
                    if (!IsLegActive(owner))
                    {
                        // If owner is gone (stopped earlier), try to reassign on the fly
                        AssignPt1ToNextOpenLeg(family);
                        continue;
                    }

                    if (!TryGetEntryPrice(owner, out var epOwner))
                        continue;

                    bool isLongFamily = GetFamilyIsLong(family);
                    double pt1Level = Instrument.MasterInstrument.RoundToTickSize(
                        isLongFamily ? epOwner + 18 * TickSize : epOwner - 18 * TickSize
                    );
                    bool hit = isLongFamily ? (price >= pt1Level) : (price <= pt1Level);

                    if (hit)
                    {
                        D($"[JeffSunVWAP] PT1 trigger hit for {owner} at {price} (ep={epOwner}). Exiting {owner}.");
                        // Mark PT1 filled BEFORE sending exit to avoid mis-assignment on the fill callback
                        pt1FilledFamilies.Add(family);
                        pt1AssignedLeg.Remove(family);
                        ExitLeg(owner);
                    }
                }

                // PT2 trigger on Leg2 only if Leg2 did not take rolled PT1
                foreach (var sig in activeLeg2.ToArray())
                {
                    int us = sig.IndexOf('_');
                    if (us <= 0 || us >= sig.Length - 1) continue;
                    string family = sig.Substring(us + 1);

                    // If family PT1 is not filled AND Leg2 is the current owner, skip (this would be PT1, not PT2)
                    if (pt1AssignedLeg.TryGetValue(family, out var assigned) && string.Equals(assigned, sig, StringComparison.Ordinal))
                        continue;

                    if (!TryGetEntryPrice(sig, out var ep2))
                        continue;

                    bool isLong = sig[0] == 'L';
                    double pt2Level = Instrument.MasterInstrument.RoundToTickSize(
                        isLong ? ep2 + 36 * TickSize : ep2 - 36 * TickSize
                    );
                    bool hit = isLong ? (price >= pt2Level) : (price <= pt2Level);

                    if (hit)
                    {
                        D($"[JeffSunVWAP] PT2 trigger hit for {sig} at {price} (ep={ep2}). Exiting {sig}.");
                        ExitLeg(sig);
                    }
                }
            }
			
			// L3/S3 exit based on SMA(10) break of the previous bar's candle, gated by 10 minutes since entry.
// Run once per bar on the first tick to avoid duplicate processing.
if (IsFirstTickOfBar && activeLeg3.Count > 0 && CurrentBar > 2)
{
    double smaPrev  = sma10[1];
    double smaPrev2 = sma10[2];

    foreach (var sig in activeLeg3.ToArray())
    {
        if (!l3EntryTime.TryGetValue(sig, out var et))
            continue;

        // Require >= 10 minutes since entry, evaluated against the bar that just closed (Time[1])
        if ((Time[1] - et).TotalMinutes < 10)
            continue;

        bool isLong = sig.StartsWith("L3_");
        if (isLong)
        {
            // Long: prior bar breaks down through SMA(10)
            bool brokeDown = Close[2] >= smaPrev2 && Close[1] < smaPrev;
            if (brokeDown)
            {
                double candleLow = Instrument.MasterInstrument.RoundToTickSize(Low[1]);
                D($"[JeffSunVWAP] L3 SMA(10) break-down for {sig}. Exiting at market (ref Low[1]={candleLow}).");
                ExitLeg(sig); // Immediate market exit; no stop/target orders
            }
        }
        else
        {
            // Short: prior bar breaks up through SMA(10)
            bool brokeUp = Close[2] <= smaPrev2 && Close[1] > smaPrev;
            if (brokeUp)
            {
                double candleHigh = Instrument.MasterInstrument.RoundToTickSize(High[1]);
                D($"[JeffSunVWAP] S3 SMA(10) break-up for {sig}. Exiting at market (ref High[1]={candleHigh}).");
                ExitLeg(sig); // Immediate market exit; no stop/target orders
            }
        }
    }
}

            // =========================
            // Entries only when flat
            // =========================
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

            // No SetStopLoss / SetProfitTarget. Just enter.
            EnterLongLimit(0, false, q1, entryPrice, s1);
            EnterLongLimit(0, false, q2, entryPrice, s2);
            EnterLongLimit(0, false, q3, entryPrice, s3);
        }

        private void SubmitScaledEntriesShort(double entryPrice, string setSuffix, int q1, int q2, int q3)
        {
            string s1 = $"S1_{setSuffix}";
            string s2 = $"S2_{setSuffix}";
            string s3 = $"S3_{setSuffix}";

            // No SetStopLoss / SetProfitTarget. Just enter.
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
            var    action    = execution.Order.OrderAction;

            // Detect entry fills per leg and record entry prices; mark family direction; initialize PT1 owner at L1
            if ((orderName.StartsWith("L1_") && action == OrderAction.Buy)
             || (orderName.StartsWith("S1_") && action == OrderAction.SellShort))
            {
                double ep = execution.Order.AverageFillPrice == 0 ? price : execution.Order.AverageFillPrice;
                leg1EntryPrice[orderName] = ep;
                activeLeg1.Add(orderName);

                string family = GetFamily(orderName);
                bool isLong   = orderName[0] == 'L';
                familyIsLong[family] = isLong;
				
				

                // Initialize PT1 owner at L1 if not already assigned and PT1 not filled
                if (!pt1FilledFamilies.Contains(family) && !pt1AssignedLeg.ContainsKey(family))
                {
                    pt1AssignedLeg[family] = orderName;
                    D($"[JeffSunVWAP] PT1 initially assigned to {orderName} for {family}.");
                }

                // clear entry arming flags
                long1Flag  = false;
                short1Flag = false;
                long2Flag  = false;
                short2Flag = false;

                D($"[JeffSunVWAP] Leg1 entry filled {orderName} @ {ep}");
            }

            if ((orderName.StartsWith("L2_") && action == OrderAction.Buy)
             || (orderName.StartsWith("S2_") && action == OrderAction.SellShort))
            {
                double ep = execution.Order.AverageFillPrice == 0 ? price : execution.Order.AverageFillPrice;
                leg2EntryPrice[orderName] = ep;
                activeLeg2.Add(orderName);

                string family = GetFamily(orderName);
                bool isLong   = orderName[0] == 'L';
                familyIsLong[family] = isLong;

                D($"[JeffSunVWAP] Leg2 entry filled {orderName} @ {ep}");
            }

            if ((orderName.StartsWith("L3_") && action == OrderAction.Buy)
             || (orderName.StartsWith("S3_") && action == OrderAction.SellShort))
            {
                double ep = execution.Order.AverageFillPrice == 0 ? price : execution.Order.AverageFillPrice;
                leg3EntryPrice[orderName] = ep;
                activeLeg3.Add(orderName);

                string family = GetFamily(orderName);
                bool isLong   = orderName[0] == 'L';
                familyIsLong[family] = isLong;
				
				// NEW: record entry time for SMA exit gating
    l3EntryTime[orderName] = execution.Order.Time;

                // clear arming flags on any leg fill
                long1Flag  = false;
                short1Flag = false;
                long2Flag  = false;
                short2Flag = false;

                D($"[JeffSunVWAP] Leg3 entry filled {orderName} @ {ep}");
            }

            // Remove legs from active sets on our explicit exits ("X_*" orders)
            if (orderName.StartsWith("X_"))
            {
                if (fromSig.StartsWith("L1_") || fromSig.StartsWith("S1_"))
                {
                    activeLeg1.Remove(fromSig);
                    leg1EntryPrice.Remove(fromSig);
                    HandlePt1OwnerExitIfNeeded(fromSig);
                    D($"[JeffSunVWAP] Leg1 exit detected for {fromSig} via {orderName}");
                }
                else if (fromSig.StartsWith("L2_") || fromSig.StartsWith("S2_"))
                {
                    activeLeg2.Remove(fromSig);
                    leg2EntryPrice.Remove(fromSig);
                    HandlePt1OwnerExitIfNeeded(fromSig);
                    D($"[JeffSunVWAP] Leg2 exit detected for {fromSig} via {orderName}");
                }
               else if (fromSig.StartsWith("L3_") || fromSig.StartsWith("S3_"))
{
    activeLeg3.Remove(fromSig);
    leg3EntryPrice.Remove(fromSig);
    l3EntryTime.Remove(fromSig); // NEW

    HandlePt1OwnerExitIfNeeded(fromSig);
    D($"[JeffSunVWAP] Leg3 exit detected for {fromSig} via {orderName}");
}

                // If strategy is now flat, clean up family-level state
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    pt1AssignedLeg.Clear();
                    pt1FilledFamilies.Clear();
                    familyIsLong.Clear();
                }
            }
        }

        // ===== Helpers =====

        private void ExitLeg(string sig)
        {
            if (sig.StartsWith("L"))
                ExitLong("X_" + sig, sig);      // exit only this entry signal
            else
                ExitShort("X_" + sig, sig);     // exit only this entry signal
        }

        private bool TryGetEntryPrice(string sig, out double ep)
        {
            if (sig.StartsWith("L1_") || sig.StartsWith("S1_")) return leg1EntryPrice.TryGetValue(sig, out ep);
            if (sig.StartsWith("L2_") || sig.StartsWith("S2_")) return leg2EntryPrice.TryGetValue(sig, out ep);
            if (sig.StartsWith("L3_") || sig.StartsWith("S3_")) return leg3EntryPrice.TryGetValue(sig, out ep);
            ep = 0;
            return false;
        }

        private string GetFamily(string sig)
        {
            int us = sig.IndexOf('_');
            if (us <= 0 || us >= sig.Length - 1) return string.Empty;
            return sig.Substring(us + 1);
        }

        private bool GetFamilyIsLong(string family)
        {
            if (familyIsLong.TryGetValue(family, out var v)) return v;
            // Fallback: infer from any active leg string if present
            if (activeLeg1.Any(s => s.EndsWith("_" + family))) return activeLeg1.First(s => s.EndsWith("_" + family)).StartsWith("L");
            if (activeLeg2.Any(s => s.EndsWith("_" + family))) return activeLeg2.First(s => s.EndsWith("_" + family)).StartsWith("L");
            if (activeLeg3.Any(s => s.EndsWith("_" + family))) return activeLeg3.First(s => s.EndsWith("_" + family)).StartsWith("L");
            return true; // default long (shouldn't happen)
        }

        private bool IsLegActive(string sig)
        {
            if (sig.StartsWith("L1_") || sig.StartsWith("S1_")) return activeLeg1.Contains(sig);
            if (sig.StartsWith("L2_") || sig.StartsWith("S2_")) return activeLeg2.Contains(sig);
            if (sig.StartsWith("L3_") || sig.StartsWith("S3_")) return activeLeg3.Contains(sig);
            return false;
        }

        // Assign PT1 to the next open leg in the family: prefer Leg2, else Leg3 (only if PT1 not filled)
        private void AssignPt1ToNextOpenLeg(string family)
        {
            if (pt1FilledFamilies.Contains(family)) { pt1AssignedLeg.Remove(family); return; }

            bool isLong = GetFamilyIsLong(family);
            string leg2 = (isLong ? $"L2_{family}" : $"S2_{family}");
            string leg3 = (isLong ? $"L3_{family}" : $"S3_{family}");

            string assign = null;
            if (activeLeg2.Contains(leg2))
                assign = leg2;
            else if (activeLeg3.Contains(leg3))
                assign = leg3;

            if (assign != null)
            {
                pt1AssignedLeg[family] = assign;
                D($"[JeffSunVWAP] Rolling PT1 -> {assign} for {family}");
            }
            else
            {
                pt1AssignedLeg.Remove(family);
                D($"[JeffSunVWAP] Rolling PT1: no open leg to assign for {family}.");
            }
        }

        private void HandlePt1OwnerExitIfNeeded(string fromSig)
        {
            string family = GetFamily(fromSig);
            if (string.IsNullOrEmpty(family)) return;

            if (pt1FilledFamilies.Contains(family)) return; // nothing to do

            if (pt1AssignedLeg.TryGetValue(family, out var assigned) && string.Equals(assigned, fromSig, StringComparison.Ordinal))
            {
                // Owner exited before PT1 was filled => roll to next
                AssignPt1ToNextOpenLeg(family);
            }
        }
    }
}
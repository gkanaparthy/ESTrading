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
    public class GKPreMarketNewSL : Strategy
    {
        // ==== Core variables (from GKATRNewSL) ====
        private bool BE_Set = false; // conceptually kept, but 2-leg logic handles BE
        private double PrevDayPnL = 0, PrevDayTradeCount = 0;
        private double AccountRealizedPL, AccountUnrealizedPL;
        private bool dayOverVar = false;
        private DateTime orderTime;
        private bool Trail_Set = false; // unused, but kept for compatibility
        private double currentTrailStopPrice = 0;

        // ==== Session & premarket levels ====
        // last regular session values (previous RTH session)
        private double lastSessionHigh = double.NaN;
        private double lastSessionLow = double.NaN;
        private double lastSessionOpen = double.NaN;
        private double lastSessionClose = double.NaN;

        // premarket levels for current session (from session start until 8:30)
        private double preMarketHigh = double.NaN;
        private double preMarketLow = double.NaN;
        private bool preMarketInitialized = false;

        // All raw levels & band variables
        private List<double> pmLevels;             // raw levels (after dedupe/sort)
        private List<Zone> zones;                  // merged zones from close levels
        private Zone currentZone;                  // zone that contains current band, if any

        private double pmLowBand;
        private double pmHighBand;
        private double pmNextBandL;
        private double pmNextBandS;

        private bool pmLowBandLongFlag;
        private bool pmHighBandShortFlag;

        // level touch tracking
        private double pmNowLevel = double.NaN;
        private double pmPrevTouchedLevel = double.NaN;
        private double pmCurrRecordedLevel = double.NaN;
        private Dictionary<double, int> lastLevelTouchBarIndex = new Dictionary<double, int>();

        // For info/diagnostics
        private double orderDiLowBand;
        private double orderDiHighBand;
        private double orderDiNextBand;

        // ==== 2‑leg SL / PT1 / PT2 & order‑family state (same as GKATRNewSL) ====
        private Order orderPT_L;    // "L_PT"
        private Order orderTrail_L; // "L_Trail"
        private Order orderPT_S;    // "S_PT"
        private Order orderTrail_S; // "S_Trail"

        // State for long family
        private bool familyOpenLong = false;
        private bool ptLegClosedLong = false;
        private bool trailLegClosedLong = false;
        private bool ptLegWasFullSL_Long = false;
        private bool trailLegWasFullSL_Long = false;
        private bool trailActiveLong;
        private bool trailingEnabledLong;
        private double trailCurrentSL_Long = double.NaN;
        private double trailEntryPriceLong = double.NaN;

        // State for short family
        private bool familyOpenShort = false;
        private bool ptLegClosedShort = false;
        private bool trailLegClosedShort = false;
        private bool ptLegWasFullSL_Short = false;
        private bool trailLegWasFullSL_Short = false;
        private bool trailActiveShort;
        private bool trailingEnabledShort;
        private double trailCurrentSL_Short = double.NaN;
        private double trailEntryPriceShort = double.NaN;

        private bool sessionTradingHalted = false;
        private int consecutiveFullLossFamilies = 0;
        private DateTime sessionDate = DateTime.MinValue;

        // VWAP + SMA for profit exits
        private SMA sma925;
        private VWAP1 sessionVwap;

        // Daily series to compute prior session OHLC
        private Series<double> dailyOpen;
        private Series<double> dailyHigh;
        private Series<double> dailyLow;
        private Series<double> dailyClose;

        // ==== Parameters ====

        // RTH parameter
        [NinjaScriptProperty]
        [Display(Name = "Use RTH (8:30-15:00)", GroupName = "Parameters", Order = 3)]
        public bool UseRTH { get; set; }

        // 2‑leg risk parameters
        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Stop (ticks)", GroupName = "Risk", Order = 10)]
        public int StopTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Profit target (ticks) for PT1", GroupName = "Risk", Order = 11)]
        public int ProfitTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Trail profit multiple (x StopTicks) for PT2", GroupName = "Risk", Order = 12)]
        public int TrailProfitMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable debug logs", GroupName = "Diagnostics", Order = 20)]
        public bool DebugLogs { get; set; }

        // Trade sizing & risk
        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Trade size", GroupName = "Parameters", Order = 0)]
        public double TradeSize { get; set; }

        [Range(double.MinValue, double.MaxValue), NinjaScriptProperty]
        [Display(ResourceType = typeof(Custom.Resource), Name = "Max Loss", GroupName = "Parameters", Order = 1)]
        public double MaxLoss { get; set; }

        // signal names for 2‑leg families
        private const string SigPT_L = "L_PT";
        private const string SigTrail_L = "L_Trail";
        private const string SigPT_S = "S_PT";
        private const string SigTrail_S = "S_Trail";

        // zone struct for merged close levels
        private class Zone
        {
            public double Low;
            public double High;
            public List<double> Levels = new List<double>();
        }

        private void D(string msg)
        {
            if (DebugLogs)
                Print("[GKPreMkt] " + msg);
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Pre-market & last-session level strategy with GKPP-style 2-leg SL/PT management. 
Clusters of nearby levels are merged into zones, and trades only occur at the conservative edge of the zone (bottom for longs, top for shorts).";
                Name = "GKPreMarketNewSL";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 3;
                EntryHandling = EntryHandling.UniqueEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 3600;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                BarsRequiredToTrade = 24;

                TradeSize = 2;
                MaxLoss = -75;

                UseRTH = true;

                StopTicks = 5;
                ProfitTicks = 10;
                TrailProfitMultiple = 8;
                DebugLogs = true;

                // Plots for visualizing levels
                AddPlot(Brushes.Yellow, "LastSessionHigh");
                AddPlot(Brushes.Yellow, "LastSessionLow");
                AddPlot(Brushes.Cyan, "LastSessionOpen");
                AddPlot(Brushes.Cyan, "LastSessionClose");
                AddPlot(Brushes.Magenta, "PreMarketHigh");
                AddPlot(Brushes.Magenta, "PreMarketLow");
            }
            else if (State == State.Configure)
            {
                // 0 = primary
                AddDataSeries(Data.BarsPeriodType.Minute, 1);  // 1-min
                AddDataSeries(Data.BarsPeriodType.Tick, 1);    // tick
                AddDataSeries(BarsPeriodType.Day, 1);          // daily
            }
            else if (State == State.DataLoaded)
            {
                // session VWAP + SMA925 for profit exits
                sma925 = SMA(925);
                sma925.Plots[0].Brush = Brushes.MediumPurple;

                sessionVwap = VWAP1(BarsArray[0],
                    new VWAPDesign.StdDesign { Enabled = false, Num = 1 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 2 },
                    new VWAPDesign.StdDesign { Enabled = false, Num = 3 },
                    true, true, true);
                sessionVwap.Plots[0].Brush = Brushes.Gold;

                AddChartIndicator(sma925);
                AddChartIndicator(sessionVwap);

                dailyOpen = new Series<double>(this);
                dailyHigh = new Series<double>(this);
                dailyLow = new Series<double>(this);
                dailyClose = new Series<double>(this);

                ResetFamilyTracking();
                sessionTradingHalted = false;
                consecutiveFullLossFamilies = 0;
                sessionDate = DateTime.MinValue;

                trailCurrentSL_Long = double.NaN;
                trailCurrentSL_Short = double.NaN;
                trailEntryPriceLong = double.NaN;
                trailEntryPriceShort = double.NaN;

                trailActiveLong = trailActiveShort = false;
                trailingEnabledLong = trailingEnabledShort = false;

                zones = new List<Zone>();
                currentZone = null;
            }
        }

        private void ResetFamilyTracking()
        {
            familyOpenLong = familyOpenShort = false;
            ptLegClosedLong = trailLegClosedLong = false;
            ptLegClosedShort = trailLegClosedShort = false;
            ptLegWasFullSL_Long = trailLegWasFullSL_Long = false;
            ptLegWasFullSL_Short = trailLegWasFullSL_Short = false;
        }

        private void EnsureSessionResetIfNeeded()
        {
            DateTime curSessionDate = Time[0].Date;
            if (sessionDate == DateTime.MinValue)
                sessionDate = curSessionDate;

            if (curSessionDate != sessionDate)
            {
                sessionDate = curSessionDate;
                sessionTradingHalted = false;
                consecutiveFullLossFamilies = 0;
                ResetFamilyTracking();
                preMarketInitialized = false;
                preMarketHigh = double.NaN;
                preMarketLow = double.NaN;
                zones = new List<Zone>();
                currentZone = null;
                D("[Session] New session detected. Reset halt flag, counters, premarket, and zones.");
            }
        }

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
                sessionTradingHalted = true;
                D("[HALT] Reached 3 consecutive full-loss families (6 full-stop losses). Halting trading for the session.");
            }
        }

        private bool TradeIsInProfit()
        {
            if (Position.MarketPosition == MarketPosition.Long)
                return Close[0] > Position.AveragePrice + TickSize;

            if (Position.MarketPosition == MarketPosition.Short)
                return Close[0] < Position.AveragePrice - TickSize;

            return false;
        }

        // Use SMA925 & session VWAP to flatten profitable trades
        private bool HitImportantLevelThisBar()
        {
            if (CurrentBar < 1)
                return false;
            if (sma925 == null || sessionVwap == null)
                return false;
            if (sma925.CurrentBar < 1 || sessionVwap.CurrentBar < 1)
                return false;

            double barLow = Low[0];
            double barHigh = High[0];

            double lvlSma925 = sma925[0];
            double lvlVwap = sessionVwap.Output[0];

            bool hitSma925 = (barLow <= lvlSma925 && barHigh >= lvlSma925);
            bool hitVwap = (barLow <= lvlVwap && barHigh >= lvlVwap);

            bool hit = hitSma925 || hitVwap;
            if (DebugLogs && hit)
            {
                D($"[ProfitExit] Hit important level. SMA925={lvlSma925:F2} hit={hitSma925}, " +
                  $"SessionVWAP={lvlVwap:F2} hit={hitVwap}, BarRange=[{barLow:F2},{barHigh:F2}]");
            }
            return hit;
        }

        // Update last session OHLC from daily series (BarsArray[3])
        private void UpdateLastSessionFromDaily()
        {
            if (BarsArray[3].Count < 2)
                return; // need at least 2 days to reference yesterday

            int idx = 1; // previous completed daily bar
            double dOpen = Opens[3][idx];
            double dHigh = Highs[3][idx];
            double dLow = Lows[3][idx];
            double dClose = Closes[3][idx];

            lastSessionOpen = dOpen;
            lastSessionHigh = dHigh;
            lastSessionLow = dLow;
            lastSessionClose = dClose;

            dailyOpen[0] = dOpen;
            dailyHigh[0] = dHigh;
            dailyLow[0] = dLow;
            dailyClose[0] = dClose;

            D($"[Daily] Last session OHLC set: O={dOpen:F2} H={dHigh:F2} L={dLow:F2} C={dClose:F2}");
        }

        // Build raw levels from last session + premarket
        private void BuildLevels()
        {
            pmLevels = new List<double>();

            if (!double.IsNaN(lastSessionLow)) pmLevels.Add(Instrument.MasterInstrument.RoundToTickSize(lastSessionLow));
            if (!double.IsNaN(lastSessionOpen)) pmLevels.Add(Instrument.MasterInstrument.RoundToTickSize(lastSessionOpen));
            if (!double.IsNaN(lastSessionClose)) pmLevels.Add(Instrument.MasterInstrument.RoundToTickSize(lastSessionClose));
            if (!double.IsNaN(lastSessionHigh)) pmLevels.Add(Instrument.MasterInstrument.RoundToTickSize(lastSessionHigh));
            if (!double.IsNaN(preMarketLow)) pmLevels.Add(Instrument.MasterInstrument.RoundToTickSize(preMarketLow));
            if (!double.IsNaN(preMarketHigh)) pmLevels.Add(Instrument.MasterInstrument.RoundToTickSize(preMarketHigh));

            pmLevels = pmLevels.Distinct().ToList();
            pmLevels.Sort();

            if (pmLevels.Count >= 2)
            {
                D($"[Levels] Built {pmLevels.Count} raw levels: " + string.Join(", ", pmLevels.Select(v => v.ToString("F2"))));
            }

            BuildZonesFromLevels();
        }

        // Merge nearby levels into zones; conservative trading uses edge of zone
        private void BuildZonesFromLevels()
        {
            zones.Clear();
            currentZone = null;

            if (pmLevels == null || pmLevels.Count == 0)
                return;

            // Threshold for "close" levels (in points)
            double clusterThreshold = 4.0;

            Zone zone = new Zone
            {
                Low = pmLevels[0],
                High = pmLevels[0]
            };
            zone.Levels.Add(pmLevels[0]);

            for (int i = 1; i < pmLevels.Count; i++)
            {
                double level = pmLevels[i];

                // If this level is within clusterThreshold of the last level in the zone, merge into same zone
                if (Math.Abs(level - zone.High) <= clusterThreshold)
                {
                    zone.High = level;
                    zone.Levels.Add(level);
                }
                else
                {
                    zones.Add(zone);
                    zone = new Zone
                    {
                        Low = level,
                        High = level
                    };
                    zone.Levels.Add(level);
                }
            }

            zones.Add(zone);

            if (zones.Count > 0)
            {
                string zInfo = string.Join(" | ", zones.Select(z => $"[{z.Low:F2}-{z.High:F2}]({z.Levels.Count} lvls)"));
                D($"[Zones] Built {zones.Count} zones: {zInfo}");
            }
        }

        // Set band around current price using pmLevels and track its zone
        private void SetBands(double price)
        {
            pmLowBand = pmHighBand = pmNextBandL = pmNextBandS = double.NaN;
            currentZone = null;

            if (pmLevels == null || pmLevels.Count < 2)
                return;

            for (int i = 0; i < pmLevels.Count - 1; i++)
            {
                if (price >= pmLevels[i] && price < pmLevels[i + 1])
                {
                    pmLowBand = pmLevels[i];
                    pmHighBand = pmLevels[i + 1];
                    pmNextBandL = (i + 2 < pmLevels.Count) ? pmLevels[i + 2] : pmLevels[i + 1];
                    pmNextBandS = (i - 1 >= 0) ? pmLevels[i - 1] : pmLevels[i];

                    // Find zone that contains this band (any overlap)
                    foreach (var z in zones)
                    {
                        if (pmHighBand >= z.Low && pmLowBand <= z.High)
                        {
                            currentZone = z;
                            break;
                        }
                    }

                    if (currentZone != null)
                    {
                        D($"[Bands] Price={price:F2} Band=[{pmLowBand:F2}-{pmHighBand:F2}] in Zone=[{currentZone.Low:F2}-{currentZone.High:F2}] NextL={pmNextBandL:F2} NextS={pmNextBandS:F2}");
                    }
                    else
                    {
                        D($"[Bands] Price={price:F2} Band=[{pmLowBand:F2}-{pmHighBand:F2}] (no zone)");
                    }
                    break;
                }
            }
        }

        protected override void OnBarUpdate()
        {
            // daily series: update last session OHLC
            if (BarsInProgress == 3)
            {
                if (CurrentBars[3] < 2)
                    return;
                if (IsFirstTickOfBar)
                    UpdateLastSessionFromDaily();
                return;
            }

            // ignore non-primary (except daily above)
            if (BarsInProgress != 0)
                return;

            if (CurrentBars[0] < BarsRequiredToTrade)
                return;

            EnsureSessionResetIfNeeded();

            double toTime = ToTime(Time[0]) / 100.0;
            bool isitEarly = toTime >= 1500 && toTime < 2359;

            // RTH window 8:30–15:00
            bool inRTH = !UseRTH || (toTime >= 830 && toTime < 1500);

            // Track pre‑market extremes up to 8:30
            if (toTime < 832)
            {
                if (!preMarketInitialized)
                {
                    preMarketHigh = High[0];
                    preMarketLow = Low[0];
                    preMarketInitialized = true;
                }
                else
                {
                    preMarketHigh = Math.Max(preMarketHigh, High[0]);
                    preMarketLow = Math.Min(preMarketLow, Low[0]);
                }
            }

            // Daily reset at 15:10 or 21:00
            if ((toTime == 1510 || toTime == 2100) && IsFirstTickOfBar)
            {
                PrevDayPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                PrevDayTradeCount = SystemPerformance.AllTrades.Count;
                dayOverVar = false;

                pmLowBandLongFlag = pmHighBandShortFlag = false;
                pmCurrRecordedLevel = double.NaN;
                pmNowLevel = double.NaN;
                pmPrevTouchedLevel = double.MinValue;

                ResetFamilyTracking();
                sessionTradingHalted = false;
                consecutiveFullLossFamilies = 0;

                preMarketInitialized = false;
                preMarketHigh = double.NaN;
                preMarketLow = double.NaN;

                zones = new List<Zone>();
                currentZone = null;

                D("[DEBUG] Daily reset - All trading flags & zones reset");
            }

            // PnL-based exit logic
            if (SystemPerformance.AllTrades.Count > 0 && !dayOverVar)
            {
                double cumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - PrevDayPnL;
                if (AccountRealizedPL < MaxLoss || AccountRealizedPL > 200 * TradeSize || AccountUnrealizedPL < -500)
                {
                    dayOverVar = true;

                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong();
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort();

                    D($"[DEBUG] Day trading stopped - PnL limits hit: CumProfit={cumProfit}, RealizedPL={AccountRealizedPL}, UnrealizedPL={AccountUnrealizedPL}");
                }
            }

            // Profit-protect exit using VWAP/SMA
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if (TradeIsInProfit() && HitImportantLevelThisBar())
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                    {
                        D("[ProfitExit] Long in profit hit SMA925/SessionVWAP. Flattening.");
                        ExitLong("ProfitExit_SMA_VWAP");
                    }
                    else if (Position.MarketPosition == MarketPosition.Short)
                    {
                        D("[ProfitExit] Short in profit hit SMA925/SessionVWAP. Flattening.");
                        ExitShort("ProfitExit_SMA_VWAP");
                    }
                }
            }

            // Update plots for visual reference
            if (!double.IsNaN(lastSessionHigh)) Values[0][0] = lastSessionHigh;
            if (!double.IsNaN(lastSessionLow)) Values[1][0] = lastSessionLow;
            if (!double.IsNaN(lastSessionOpen)) Values[2][0] = lastSessionOpen;
            if (!double.IsNaN(lastSessionClose)) Values[3][0] = lastSessionClose;
            if (!double.IsNaN(preMarketHigh)) Values[4][0] = preMarketHigh;
            if (!double.IsNaN(preMarketLow)) Values[5][0] = preMarketLow;

            // MAIN TRADING LOGIC – use merged zones & conservative edges
            if (IsFirstTickOfBar && inRTH && !isitEarly && toTime > 830 && !dayOverVar && !sessionTradingHalted)
            {
                BuildLevels();

                if (pmLevels == null || pmLevels.Count < 2)
                    return;

                // Level touch detection
                pmNowLevel = double.NaN;
                for (int i = 0; i < pmLevels.Count; i++)
                {
                    if (High[1] >= pmLevels[i] && pmLevels[i] >= Low[1])
                        pmNowLevel = pmLevels[i];
                }

                if (!double.IsNaN(pmNowLevel))
                {
                    lastLevelTouchBarIndex[pmNowLevel] = CurrentBar;
                    D("[DEBUG] Level " + pmNowLevel + " touched at bar " + CurrentBar);
                }

                if (!double.IsNaN(pmNowLevel))
                {
                    pmPrevTouchedLevel = pmCurrRecordedLevel;
                    pmCurrRecordedLevel = pmNowLevel;
                }

                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    SetBands(Close[0]);
                    if (double.IsNaN(pmLowBand) || double.IsNaN(pmHighBand))
                        return;

                    // determine side by where we are within band
                    pmLowBandLongFlag = (Close[0] - pmLowBand) < (pmHighBand - Close[0]);
                    pmHighBandShortFlag = (Close[0] - pmLowBand) > (pmHighBand - Close[0]);

                    bool levelRevisitedWithSufficientRetracement = false;
                    double bandWidth = pmHighBand - pmLowBand;
                    double currentATR = ATR(Closes[1], 14)[0];

                    if (bandWidth > 0 && lastLevelTouchBarIndex.ContainsKey(pmCurrRecordedLevel))
                    {
                        int barsSinceTouch = CurrentBar - lastLevelTouchBarIndex[pmCurrRecordedLevel];
                        double deviation = GetMaxDeviationSinceLastTouch(pmCurrRecordedLevel, barsSinceTouch);
                        // same 3*ATR retracement logic
                        levelRevisitedWithSufficientRetracement = deviation > 3 * currentATR;

                        if (levelRevisitedWithSufficientRetracement)
                            D($"[DEBUG] Level {pmCurrRecordedLevel} retraced: {deviation:F2} > {3 * currentATR:F2}");
                    }

                    int total = Math.Max(2, (int)TradeSize);
                    int qPT = Math.Max(1, total / 2);
                    int qTrail = Math.Max(1, total - qPT);

                    // conservative: use zone edges if we are inside a zone
                    double conservativeLongEntry = pmLowBand;
                    double conservativeShortEntry = pmHighBand;

                    if (currentZone != null)
                    {
                        // bottom of zone for longs, top of zone for shorts
                        conservativeLongEntry = currentZone.Low;
                        conservativeShortEntry = currentZone.High;
                    }

                    // SHORT FAMILY ENTRY – 2 legs at conservativeShortEntry
                    if (pmHighBandShortFlag
                        && (pmPrevTouchedLevel == double.MinValue || pmCurrRecordedLevel != pmPrevTouchedLevel || levelRevisitedWithSufficientRetracement)
                        && currentATR < 10)
                    {
                        double entryPrice = Instrument.MasterInstrument.RoundToTickSize(conservativeShortEntry);

                        SetStopLoss(SigPT_S, CalculationMode.Ticks, StopTicks, false);
                        SetProfitTarget(SigPT_S, CalculationMode.Ticks, ProfitTicks);

                        SetStopLoss(SigTrail_S, CalculationMode.Ticks, StopTicks, false);
                        int pt2Ticks = TrailProfitMultiple * StopTicks;
                        SetProfitTarget(SigTrail_S, CalculationMode.Ticks, pt2Ticks);

                        EnterShortLimit(0, true, qPT, entryPrice, SigPT_S);
                        EnterShortLimit(0, true, qTrail, entryPrice, SigTrail_S);

                        BE_Set = false;
                        Trail_Set = false;
                        currentTrailStopPrice = 0;

                        familyOpenShort = true;
                        ptLegClosedShort = trailLegClosedShort = false;
                        ptLegWasFullSL_Short = trailLegWasFullSL_Short = false;

                        trailActiveShort = false;
                        trailingEnabledShort = false;

                        orderTime = Time[0];

                        orderDiLowBand = pmLowBand;
                        orderDiHighBand = pmHighBand;
                        orderDiNextBand = pmNextBandS;

                        string zInfo = currentZone != null ? $"Zone=[{currentZone.Low:F2}-{currentZone.High:F2}]" : "NoZone";
                        D($"[DEBUG] SHORT FAMILY ENTRY @ {entryPrice:F2}, band=({pmLowBand:F2},{pmHighBand:F2}), {zInfo}, StopTicks={StopTicks}, PT1={ProfitTicks}, PT2={pt2Ticks}, ATR={currentATR:F2}");
                    }
                    // LONG FAMILY ENTRY – 2 legs at conservativeLongEntry
                    else if (pmLowBandLongFlag
                        && (pmPrevTouchedLevel == double.MinValue || pmCurrRecordedLevel != pmPrevTouchedLevel || levelRevisitedWithSufficientRetracement)
                        && currentATR < 10)
                    {
                        double entryPrice = Instrument.MasterInstrument.RoundToTickSize(conservativeLongEntry);

                        SetStopLoss(SigPT_L, CalculationMode.Ticks, StopTicks, false);
                        SetProfitTarget(SigPT_L, CalculationMode.Ticks, ProfitTicks);

                        SetStopLoss(SigTrail_L, CalculationMode.Ticks, StopTicks, false);
                        int pt2Ticks = TrailProfitMultiple * StopTicks;
                        SetProfitTarget(SigTrail_L, CalculationMode.Ticks, pt2Ticks);

                        EnterLongLimit(0, true, qPT, entryPrice, SigPT_L);
                        EnterLongLimit(0, true, qTrail, entryPrice, SigTrail_L);

                        BE_Set = false;
                        Trail_Set = false;
                        currentTrailStopPrice = 0;

                        familyOpenLong = true;
                        ptLegClosedLong = trailLegClosedLong = false;
                        ptLegWasFullSL_Long = trailLegWasFullSL_Long = false;

                        trailActiveLong = false;
                        trailingEnabledLong = false;

                        orderTime = Time[0];

                        orderDiLowBand = pmLowBand;
                        orderDiHighBand = pmHighBand;
                        orderDiNextBand = pmNextBandL;

                        string zInfo = currentZone != null ? $"Zone=[{currentZone.Low:F2}-{currentZone.High:F2}]" : "NoZone";
                        D($"[DEBUG] LONG FAMILY ENTRY @ {entryPrice:F2}, band=({pmLowBand:F2},{pmHighBand:F2}), {zInfo}, StopTicks={StopTicks}, PT1={ProfitTicks}, PT2={pt2Ticks}, ATR={currentATR:F2}");
                    }
                }
            }

            // 2nd leg BE/trail is handled in OnExecutionUpdate
        }

        // Max deviation from level
        private double GetMaxDeviationSinceLastTouch(double level, int barsSinceTouch)
        {
            int lookback = Math.Min(barsSinceTouch, CurrentBar);
            double maxDeviation = 0;

            for (int i = 0; i < lookback; i++)
            {
                double upDeviation = High[i] - level;
                double downDeviation = level - Low[i];
                double barDeviation = Math.Max(upDeviation, downDeviation);
                if (barDeviation > maxDeviation)
                    maxDeviation = barDeviation;
            }

            return maxDeviation;
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null) return;

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
            else if (order.Name == SigPT_S)
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

            bool isPTExit = on.IndexOf("Profit target", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isStopExit = on.IndexOf("Stop loss", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isExitEvent = isStopExit || isPTExit;

            // Trail leg long filled
            if (execution.Order.Name == SigTrail_L &&
                execution.Order.OrderAction == OrderAction.Buy &&
                execution.Order.OrderState != OrderState.PartFilled)
            {
                trailActiveLong = true;
                double ep = execution.Order.AverageFillPrice == 0 ? price : execution.Order.AverageFillPrice;

                trailEntryPriceLong = Instrument.MasterInstrument.RoundToTickSize(ep);
                trailCurrentSL_Long = Instrument.MasterInstrument.RoundToTickSize(ep - StopTicks * TickSize);
                D($"[Long] L_Trail filled @ {ep}. Initial SL ~ {trailCurrentSL_Long}. Trailing disabled until PT1 hit.");
            }

            // Trail leg short filled
            if (execution.Order.Name == SigTrail_S &&
                execution.Order.OrderAction == OrderAction.SellShort &&
                execution.Order.OrderState != OrderState.PartFilled)
            {
                trailActiveShort = true;
                double ep = execution.Order.AverageFillPrice == 0 ? price : execution.Order.AverageFillPrice;

                trailEntryPriceShort = Instrument.MasterInstrument.RoundToTickSize(ep);
                trailCurrentSL_Short = Instrument.MasterInstrument.RoundToTickSize(ep + StopTicks * TickSize);
                D($"[Short] S_Trail filled @ {ep}. Initial SL ~ {trailCurrentSL_Short}. Trailing disabled until PT1 hit.");
            }

            // PT1 hit for long family
            if (isPTExit && fromSig == SigPT_L)
            {
                ptLegClosedLong = true;

                if (trailActiveLong && !double.IsNaN(trailEntryPriceLong))
                {
                    double be = Instrument.MasterInstrument.RoundToTickSize(trailEntryPriceLong);
                    SetStopLoss(SigTrail_L, CalculationMode.Price, be, false);
                    trailCurrentSL_Long = be;
                    D($"[Long] PT1 hit ({ProfitTicks}t). L_Trail SL moved to BE @ {be}. No EMA trailing.");
                }

                trailingEnabledLong = true;
            }

            // PT1 hit for short family
            if (isPTExit && fromSig == SigPT_S)
            {
                ptLegClosedShort = true;

                if (trailActiveShort && !double.IsNaN(trailEntryPriceShort))
                {
                    double be = Instrument.MasterInstrument.RoundToTickSize(trailEntryPriceShort);
                    SetStopLoss(SigTrail_S, CalculationMode.Price, be, false);
                    trailCurrentSL_Short = be;
                    D($"[Short] PT1 hit ({ProfitTicks}t). S_Trail SL moved to BE @ {be}. No EMA trailing.");
                }

                trailingEnabledShort = true;
            }

            // Trail leg exit classification - long
            if (isExitEvent && fromSig == SigTrail_L)
            {
                trailLegClosedLong = true;
                if (isStopExit)
                {
                    bool full = !trailingEnabledLong; // full loss if before BE activation
                    trailLegWasFullSL_Long = full;
                }

                trailActiveLong = false;
                trailingEnabledLong = false;
                trailCurrentSL_Long = double.NaN;
                D("[Long] L_Trail exit detected; trail state cleared.");

                if (ptLegClosedLong && trailLegClosedLong && familyOpenLong)
                {
                    bool bothFull = ptLegWasFullSL_Long && trailLegWasFullSL_Long;
                    EvaluateAndMaybeHaltAfterFamilyClose(bothFull);
                    familyOpenLong = false;
                }
            }

            // Trail leg exit classification - short
            if (isExitEvent && fromSig == SigTrail_S)
            {
                trailLegClosedShort = true;
                if (isStopExit)
                {
                    bool full = !trailingEnabledShort;
                    trailLegWasFullSL_Short = full;
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

            // PT leg full-stop classification
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

        #region Level Series (for plots)
        [XmlIgnore] public Series<double> LastSessionHighSeries => Values[0];
        [XmlIgnore] public Series<double> LastSessionLowSeries => Values[1];
        [XmlIgnore] public Series<double> LastSessionOpenSeries => Values[2];
        [XmlIgnore] public Series<double> LastSessionCloseSeries => Values[3];
        [XmlIgnore] public Series<double> PreMarketHighSeries => Values[4];
        [XmlIgnore] public Series<double> PreMarketLowSeries => Values[5];
        #endregion

        protected override void OnAccountItemUpdate(Account account, AccountItem accountItem, double value)
        {
            AccountRealizedPL = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
            AccountUnrealizedPL = account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
        }
    }
}
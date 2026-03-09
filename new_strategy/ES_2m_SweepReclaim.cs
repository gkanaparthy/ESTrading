#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// ============================================================
// ES_2m_SweepReclaim  v1.0 — Spec v2 (2026-03-09)
// 2-minute ES intraday: liquidity sweep + reclaim continuation
// Target: ~50% WR, fixed 1:3 RR, no break-even (baseline)
// ============================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    [Description("ES 2-min Sweep/Reclaim Trend Strategy v1.0 — 1:3 RR, spec_v2")]
    public class ES_2m_SweepReclaim : Strategy
    {
        // ─────────────────────────────────────────────────────────────────────
        // PARAMETERS
        // ─────────────────────────────────────────────────────────────────────
        #region Parameters

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "EMA Period (15m)", GroupName = "Regime", Order = 0)]
        public int EmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 10.0)]
        [Display(Name = "EMA Slope Threshold (pts over 3 bars)", GroupName = "Regime", Order = 1)]
        public double EmaSlopeThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max VWAP Crosses (30 min whipsaw)", GroupName = "Regime", Order = 2)]
        public int MaxVwapCrossesWhipsaw { get; set; }

        [NinjaScriptProperty]
        [Range(3, 20)]
        [Display(Name = "Swing Fractal Lookback Bars", GroupName = "Swing", Order = 0)]
        public int SwingFractalBars { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "Max Swing Age (bars)", GroupName = "Swing", Order = 1)]
        public int MaxSwingAgeBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 2.0)]
        [Display(Name = "Reclaim Body Min (ATR fraction)", GroupName = "Entry", Order = 0)]
        public double ReclaimBodyAtrFraction { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Setup Expiry (bars after reclaim)", GroupName = "Entry", Order = 1)]
        public int SetupExpiryBars { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "ATR Period (2m)", GroupName = "Risk", Order = 0)]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 3.0)]
        [Display(Name = "Stop Min ATR Multiplier", GroupName = "Risk", Order = 1)]
        public double StopMinAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.3, 5.0)]
        [Display(Name = "Stop Max ATR Multiplier", GroupName = "Risk", Order = 2)]
        public double StopMaxAtrMult { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 6.0)]
        [Display(Name = "Reward/Risk Ratio", GroupName = "Risk", Order = 3)]
        public double RewardRiskRatio { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Max Trades Per Window", GroupName = "Governance", Order = 0)]
        public int MaxTradesPerWindow { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 20.0)]
        [Display(Name = "Max Daily Loss (R)", GroupName = "Governance", Order = 1)]
        public double MaxDailyLossR { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "News Blackout (mins each side)", GroupName = "News", Order = 0)]
        public int NewsBlackoutMinutes { get; set; }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        // PRIVATE STATE
        // ─────────────────────────────────────────────────────────────────────
        #region Private state

        // Secondary series
        private const int IDX_15M = 1;

        // Indicators
        private ATR  _atr2m;
        private EMA  _ema15m;

        // Session VWAP (manual, avoids indicator dependency mismatch across NT8 installs)
        private double _cumPv;
        private double _cumVol;
        private double _sessionVwap;

        // Swing state
        private double _swingLow;
        private double _swingHigh;
        private int    _swingLowBar;
        private int    _swingHighBar;

        // State machine
        private enum SetupState { Idle, SweptLong, SweptShort, AwaitingLong, AwaitingShort }
        private SetupState _state;
        private int        _reclaimBarIdx;

        // Pending order parameters (set at reclaim detection)
        private double _pendingEntry;
        private double _pendingStop;
        private double _pendingTarget;
        private double _pendingStopDist;

        // Active trade
        private bool   _inTrade;
        private double _tradeStopDist;    // 1R in price distance at entry
        private bool   _tradeWasWindow1;  // which window the entry filled in

        // Governance per-day
        private int    _w1Trades;
        private int    _w2Trades;
        private int    _w1ConsecLoss;
        private int    _w2ConsecLoss;
        private bool   _w1Paused;
        private bool   _w2Paused;
        private double _dailyLossR;
        private bool   _dailyStopped;
        private DateTime _sessionDate;    // CT date; used to detect new day

        // VWAP cross tracking (minutes-from-midnight rolling list)
        private readonly List<double> _vwapCrossMins = new List<double>();
        private bool   _prevAboveVwap;
        private bool   _vwapInit;

        // Bias
        private bool   _biasReady;
        private enum   Bias { None, Long, Short }
        private Bias   _bias;

        // News blackout: set of (dayOfYear * 10000 + minute-of-day) keys in CT
        private readonly HashSet<long> _newsKeys = new HashSet<long>();

        // Window flags (recomputed each bar)
        private bool _inW1;
        private bool _inW2;

        // Logging
        private int _seq;

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        // LIFECYCLE
        // ─────────────────────────────────────────────────────────────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "ES_2m_SweepReclaim";
                Description = "v1.0 spec_v2 — Sweep/Reclaim 1:3 RR";
                Calculate   = Calculate.OnBarClose;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                BarsRequiredToTrade          = 30;
                DefaultQuantity              = 1;

                // Spec v2 defaults
                EmaPeriod              = 20;
                EmaSlopeThreshold      = 0.5;
                MaxVwapCrossesWhipsaw  = 3;
                SwingFractalBars       = 5;
                MaxSwingAgeBars        = 20;
                ReclaimBodyAtrFraction = 0.25;
                SetupExpiryBars        = 3;
                AtrPeriod              = 14;
                StopMinAtrMult         = 0.5;
                StopMaxAtrMult         = 1.5;
                RewardRiskRatio        = 3.0;
                MaxTradesPerWindow     = 2;
                MaxDailyLossR          = 3.0;
                NewsBlackoutMinutes    = 10;
            }
            else if (State == State.Configure)
            {
                // Secondary 15-minute series — index 1
                AddDataSeries(BarsPeriodType.Minute, 15);

                // ATR on primary (2m) series
                _atr2m  = ATR(AtrPeriod);

                BuildNewsCalendar();
            }
            else if (State == State.DataLoaded)
            {
                // EMA must reference the 15m bar array
                _ema15m = EMA(BarsArray[IDX_15M], EmaPeriod);

                // Initialise state (use epoch date as "no session yet")
                InitDayState(new DateTime(2000, 1, 1));
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // MAIN UPDATE — only processes 2-minute (primary) bars
        // ─────────────────────────────────────────────────────────────────────
        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;   // ignore 15m bar events

            if (CurrentBars[0]     < BarsRequiredToTrade) return;
            if (CurrentBars[IDX_15M] < EmaPeriod + 5)    return;

            DateTime ctBar = ToCT(Time[0]);
            TimeSpan tod   = ctBar.TimeOfDay;

            // ── New-day reset ────────────────────────────────────────────
            if (ctBar.Date != _sessionDate)
                InitDayState(ctBar);

            // ── EOD flatten at 15:00 CT ──────────────────────────────────
            if (tod >= new TimeSpan(15, 0, 0))
            {
                if (Position.MarketPosition != MarketPosition.Flat)
                {
                    ExitLong("FlattenEOD",  "SweepReclaim_L");
                    ExitShort("FlattenEOD", "SweepReclaim_S");
                    Log("FLATTEN_EOD");
                }
                return;
            }

            if (_dailyStopped) return;

            // ── Window flags ─────────────────────────────────────────────
            // Window 1: 09:00–10:30 CT  (includes bias warm-up gate)
            // Window 2: 13:30–14:45 CT
            _inW1 = tod >= new TimeSpan(9,  0, 0) && tod < new TimeSpan(10, 30, 0);
            _inW2 = tod >= new TimeSpan(13, 30, 0) && tod < new TimeSpan(14, 45, 0);

            // Bias warm-up: 2 × 15m bars after RTH open (08:30 CT) = ready at 09:00 CT
            if (!_biasReady && tod >= new TimeSpan(9, 0, 0))
                _biasReady = true;

            // ── Per-bar indicator reads ───────────────────────────────────
            double atr = _atr2m[0];

            // Session VWAP update (typical price weighted by bar volume)
            if (Bars.IsFirstBarOfSession)
            {
                _cumPv = 0;
                _cumVol = 0;
                _sessionVwap = Close[0];
            }
            double typ = (High[0] + Low[0] + Close[0]) / 3.0;
            double vol = Math.Max(1.0, Volume[0]);
            _cumPv += typ * vol;
            _cumVol += vol;
            double vwap = _cumVol > 0 ? (_cumPv / _cumVol) : Close[0];
            _sessionVwap = vwap;

            // ── Supporting calculations ───────────────────────────────────
            TrackVwapCross(Close[0], vwap, tod.TotalMinutes);
            UpdateBias(Close[0], vwap, tod);
            UpdateSwings();

            // ── If in a trade, nothing to do (SL/TP manage themselves) ────
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            // ── Check whether any window has capacity ────────────────────
            bool w1Active = _inW1 && !_w1Paused && _w1Trades < MaxTradesPerWindow;
            bool w2Active = _inW2 && !_w2Paused && _w2Trades < MaxTradesPerWindow;
            if ((!w1Active && !w2Active) || !_biasReady) return;

            bool newsBlock = IsNewsBlackout(ctBar);

            // ── State machine ─────────────────────────────────────────────
            switch (_state)
            {
                case SetupState.Idle:
                    if (!newsBlock)
                        TryDetectSweep(atr);
                    break;

                case SetupState.SweptLong:
                case SetupState.SweptShort:
                    if (newsBlock || _bias == Bias.None)
                    {
                        Log($"ABORT bias={_bias} news={newsBlock}");
                        _state = SetupState.Idle;
                    }
                    else
                        TryDetectReclaim(atr);
                    break;

                case SetupState.AwaitingLong:
                case SetupState.AwaitingShort:
                    if (CurrentBar - _reclaimBarIdx >= SetupExpiryBars)
                    {
                        Log($"EXPIRED age={CurrentBar - _reclaimBarIdx}");
                        _state = SetupState.Idle;
                        // NT8 automatically cancels unmatched strategy orders when
                        // we stop re-submitting, but we explicitly cancel to be safe
                        CancelOpenEntries();
                    }
                    break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // SWEEP DETECTION
        // ─────────────────────────────────────────────────────────────────────
        private void TryDetectSweep(double atr)
        {
            if (_bias == Bias.Long
                && !double.IsNaN(_swingLow)
                && (CurrentBar - _swingLowBar) <= MaxSwingAgeBars
                && Low[0] < _swingLow - TickSize)
            {
                _state = SetupState.SweptLong;
                Log($"SWEEP_L swingLow={_swingLow:F2} thisLow={Low[0]:F2}");
                TryDetectReclaim(atr);   // check if same bar also reclaims
                return;
            }

            if (_bias == Bias.Short
                && !double.IsNaN(_swingHigh)
                && (CurrentBar - _swingHighBar) <= MaxSwingAgeBars
                && High[0] > _swingHigh + TickSize)
            {
                _state = SetupState.SweptShort;
                Log($"SWEEP_S swingHigh={_swingHigh:F2} thisHigh={High[0]:F2}");
                TryDetectReclaim(atr);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // RECLAIM DETECTION + ENTRY ARMING
        // ─────────────────────────────────────────────────────────────────────
        private void TryDetectReclaim(double atr)
        {
            if (_state == SetupState.SweptLong)
            {
                // Must close back ABOVE swing low
                if (Close[0] <= _swingLow) return;

                double body      = Math.Abs(Close[0] - Open[0]);
                double range     = High[0] - Low[0];
                double closeRank = range > 1e-8 ? (Close[0] - Low[0]) / range : 0;

                if (body < ReclaimBodyAtrFraction * atr)
                {
                    Reject($"BODY_SMALL body={body:F2} need={ReclaimBodyAtrFraction * atr:F2}");
                    _state = SetupState.Idle; return;
                }
                if (closeRank < 0.60)  // close must be in top 40% of bar
                {
                    Reject($"CLOSE_RANK_LOW rank={closeRank:P0}");
                    _state = SetupState.Idle; return;
                }

                // Sweep extreme: lowest low over last 5 bars (incl. this bar)
                double sweepExt = _swingLow;
                for (int i = 0; i < 5 && i <= CurrentBar; i++)
                    if (Low[i] < sweepExt) sweepExt = Low[i];

                double stopPx   = sweepExt - TickSize;
                double entryPx  = High[0] + TickSize;          // buy-stop above reclaim bar
                double stopDist = entryPx - stopPx;

                if (!CheckStopBounds(stopDist, atr)) { _state = SetupState.Idle; return; }

                double targetPx = entryPx + RewardRiskRatio * stopDist;

                ArmLong(entryPx, stopPx, targetPx, stopDist);
            }
            else if (_state == SetupState.SweptShort)
            {
                if (Close[0] >= _swingHigh) return;

                double body      = Math.Abs(Close[0] - Open[0]);
                double range     = High[0] - Low[0];
                double closeRank = range > 1e-8 ? (High[0] - Close[0]) / range : 0;

                if (body < ReclaimBodyAtrFraction * atr)
                {
                    Reject($"BODY_SMALL body={body:F2} need={ReclaimBodyAtrFraction * atr:F2}");
                    _state = SetupState.Idle; return;
                }
                if (closeRank < 0.60)
                {
                    Reject($"CLOSE_RANK_LOW rank={closeRank:P0}");
                    _state = SetupState.Idle; return;
                }

                double sweepExt = _swingHigh;
                for (int i = 0; i < 5 && i <= CurrentBar; i++)
                    if (High[i] > sweepExt) sweepExt = High[i];

                double stopPx   = sweepExt + TickSize;
                double entryPx  = Low[0] - TickSize;           // sell-stop below reclaim bar
                double stopDist = stopPx - entryPx;

                if (!CheckStopBounds(stopDist, atr)) { _state = SetupState.Idle; return; }

                double targetPx = entryPx - RewardRiskRatio * stopDist;

                ArmShort(entryPx, stopPx, targetPx, stopDist);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // ARM ENTRY ORDERS
        // ─────────────────────────────────────────────────────────────────────
        private void ArmLong(double entry, double stop, double target, double stopDist)
        {
            _pendingEntry    = entry;
            _pendingStop     = stop;
            _pendingTarget   = target;
            _pendingStopDist = stopDist;
            _reclaimBarIdx   = CurrentBar;
            _state           = SetupState.AwaitingLong;

            SetStopLoss("SweepReclaim_L",    CalculationMode.Price, stop,   false);
            SetProfitTarget("SweepReclaim_L", CalculationMode.Price, target);
            EnterLongStopMarket(DefaultQuantity, entry, "SweepReclaim_L");

            Log($"ARMED_L entry={entry:F2} stop={stop:F2} target={target:F2} R={stopDist/TickSize:F1}tks");
        }

        private void ArmShort(double entry, double stop, double target, double stopDist)
        {
            _pendingEntry    = entry;
            _pendingStop     = stop;
            _pendingTarget   = target;
            _pendingStopDist = stopDist;
            _reclaimBarIdx   = CurrentBar;
            _state           = SetupState.AwaitingShort;

            SetStopLoss("SweepReclaim_S",    CalculationMode.Price, stop,   false);
            SetProfitTarget("SweepReclaim_S", CalculationMode.Price, target);
            EnterShortStopMarket(DefaultQuantity, entry, "SweepReclaim_S");

            Log($"ARMED_S entry={entry:F2} stop={stop:F2} target={target:F2} R={stopDist/TickSize:F1}tks");
        }

        // ─────────────────────────────────────────────────────────────────────
        // EXECUTION — track fill for governance
        // ─────────────────────────────────────────────────────────────────────
        protected override void OnExecutionUpdate(Execution exec, string execId,
            double price, int qty, MarketPosition mp, string orderId, DateTime time)
        {
            // Only care about entry fills
            if (!exec.Name.StartsWith("SweepReclaim")) return;
            if (exec.Order.OrderState != OrderState.Filled) return;

            _inTrade        = true;
            _tradeStopDist  = _pendingStopDist;

            DateTime ct  = ToCT(time);
            TimeSpan tod = ct.TimeOfDay;
            _tradeWasWindow1 = tod >= new TimeSpan(9, 0, 0) && tod < new TimeSpan(10, 30, 0);

            if (_tradeWasWindow1) _w1Trades++;
            else                  _w2Trades++;

            _state = SetupState.Idle;
            Log($"FILL {(mp == MarketPosition.Long ? "L" : "S")} @{price:F2} stop={_pendingStop:F2} tgt={_pendingTarget:F2}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // POSITION CLOSED — governance update
        // ─────────────────────────────────────────────────────────────────────
        protected override void OnPositionUpdate(Position pos, double avgPx,
            int qty, MarketPosition mp)
        {
            if (mp != MarketPosition.Flat || !_inTrade) return;

            _inTrade = false;

            if (SystemPerformance.AllTrades.Count == 0) return;
            var    last    = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
            double netPnl  = last.ProfitCurrency;

            // 1R in dollars: stopDist (pts) × (TickValue / TickSize)
            // ES: TickSize=0.25 pt, TickValue=$12.50 → point value $50
            double rDollars = _tradeStopDist / TickSize
                              * Instrument.MasterInstrument.TickValue;
            double resultR  = rDollars > 0 ? netPnl / rDollars : 0;

            bool lost = netPnl < 0;
            if (lost) _dailyLossR += resultR;   // resultR is negative for losses

            // Per-window consecutive-loss logic
            if (_tradeWasWindow1)
            {
                if (lost) { _w1ConsecLoss++; if (_w1ConsecLoss >= 2) { _w1Paused = true; Log("W1_PAUSED"); } }
                else      { _w1ConsecLoss = 0; }
            }
            else
            {
                if (lost) { _w2ConsecLoss++; if (_w2ConsecLoss >= 2) { _w2Paused = true; Log("W2_PAUSED"); } }
                else      { _w2ConsecLoss = 0; }
            }

            // Daily circuit breaker
            if (_dailyLossR <= -MaxDailyLossR)
            {
                _dailyStopped = true;
                Log($"DAILY_STOP lossR={_dailyLossR:F2}");
            }

            Log($"CLOSE pnl={netPnl:F2} R={resultR:F2} dayR={_dailyLossR:F2}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // SWING — 5-bar fractal
        // ─────────────────────────────────────────────────────────────────────
        private void UpdateSwings()
        {
            int n = SwingFractalBars;
            if (CurrentBar < 2 * n + 1) return;

            bool isLow  = true;
            bool isHigh = true;

            for (int i = 0; i < n; i++)
            {
                // Compare candidate bar (index n) against the n bars on each side
                if (isLow  && (Low[n]  >= Low[i]       || Low[n]  >= Low[n + 1 + i]))  isLow  = false;
                if (isHigh && (High[n] <= High[i]      || High[n] <= High[n + 1 + i])) isHigh = false;
                if (!isLow && !isHigh) break;
            }

            if (isLow)  { _swingLow    = Low[n];   _swingLowBar  = CurrentBar - n; }
            if (isHigh) { _swingHigh   = High[n];  _swingHighBar = CurrentBar - n; }
        }

        // ─────────────────────────────────────────────────────────────────────
        // BIAS
        // ─────────────────────────────────────────────────────────────────────
        private void UpdateBias(double price, double vwap, TimeSpan tod)
        {
            if (!_biasReady || CurrentBars[IDX_15M] < EmaPeriod + 5)
            { _bias = Bias.None; return; }

            double slope = _ema15m[0] - _ema15m[3];   // 3-bar ROC on 15m EMA

            // Whipsaw filter: count crosses in last 30 minutes
            double nowMins = tod.TotalMinutes;
            int crosses = _vwapCrossMins.Count(m => nowMins - m >= 0 && nowMins - m <= 30.0);

            if (crosses >= MaxVwapCrossesWhipsaw)
            { Reject($"WHIPSAW crosses={crosses}"); _bias = Bias.None; return; }

            if (price > vwap && slope > EmaSlopeThreshold)
                _bias = Bias.Long;
            else if (price < vwap && slope < -EmaSlopeThreshold)
                _bias = Bias.Short;
            else
                _bias = Bias.None;
        }

        private void TrackVwapCross(double close, double vwap, double todMins)
        {
            bool aboveNow = close > vwap;
            if (_vwapInit && aboveNow != _prevAboveVwap)
                _vwapCrossMins.Add(todMins);
            _prevAboveVwap = aboveNow;
            _vwapInit      = true;

            // Purge entries older than 30 minutes
            _vwapCrossMins.RemoveAll(m => todMins - m > 30.0);
        }

        // ─────────────────────────────────────────────────────────────────────
        // NEWS BLACKOUT
        // ─────────────────────────────────────────────────────────────────────
        private bool IsNewsBlackout(DateTime ct)
        {
            long baseKey = (long)ct.DayOfYear * 10000;
            int  nowMin  = (int)ct.TimeOfDay.TotalMinutes;
            for (int d = -NewsBlackoutMinutes; d <= NewsBlackoutMinutes; d++)
            {
                int min = nowMin + d;
                if (min < 0 || min >= 1440) continue;
                if (_newsKeys.Contains(baseKey + min)) return true;
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // HELPERS
        // ─────────────────────────────────────────────────────────────────────
        private bool CheckStopBounds(double stopDist, double atr)
        {
            double mn = StopMinAtrMult * atr;
            double mx = StopMaxAtrMult * atr;
            if (stopDist < mn) { Reject($"STOP_TIGHT {stopDist/TickSize:F1}tks min={mn/TickSize:F1}"); return false; }
            if (stopDist > mx) { Reject($"STOP_WIDE  {stopDist/TickSize:F1}tks max={mx/TickSize:F1}"); return false; }
            return true;
        }

        private void CancelOpenEntries()
        {
            // NT8 strategy orders are cancelled when position stays flat and we stop
            // re-issuing them.  Explicit cancel via account order list (no direct API needed).
            // Nothing more required — the state reset above prevents re-issue.
        }

        private void InitDayState(DateTime ct)
        {
            _w1Trades    = _w2Trades    = 0;
            _w1ConsecLoss= _w2ConsecLoss= 0;
            _w1Paused    = _w2Paused    = false;
            _dailyLossR  = 0;
            _dailyStopped= false;
            _biasReady   = false;
            _bias        = Bias.None;
            _state       = SetupState.Idle;
            _inTrade     = false;
            _vwapCrossMins.Clear();
            _vwapInit    = false;
            _cumPv       = 0;
            _cumVol      = 0;
            _sessionVwap = 0;
            _sessionDate = ct.Date;
        }

        // ─────────────────────────────────────────────────────────────────────
        // NEWS CALENDAR (CT times for backtest window 2026-02-01 to 2026-03-06)
        // ─────────────────────────────────────────────────────────────────────
        private void BuildNewsCalendar()
        {
            var eventsCt = new DateTime[]
            {
                new DateTime(2026, 2,  4,  7, 30, 0), // ISM Services
                new DateTime(2026, 2,  5,  7, 30, 0), // Jobless Claims
                new DateTime(2026, 2,  7,  7, 30, 0), // NFP Jan
                new DateTime(2026, 2, 10, 14,  0, 0), // Fed speak (approx)
                new DateTime(2026, 2, 12,  7, 30, 0), // CPI Jan
                new DateTime(2026, 2, 13,  7, 30, 0), // Jobless Claims
                new DateTime(2026, 2, 14,  7, 30, 0), // PPI / Retail Sales
                new DateTime(2026, 2, 19,  7, 30, 0), // PPI
                new DateTime(2026, 2, 20,  7, 30, 0), // Jobless Claims
                new DateTime(2026, 2, 25, 13,  0, 0), // CB Consumer Confidence
                new DateTime(2026, 2, 26,  7, 30, 0), // PCE / Core PCE
                new DateTime(2026, 2, 27,  7, 30, 0), // GDP / Jobless Claims
                new DateTime(2026, 3,  4, 14,  0, 0), // ISM Manufacturing
                new DateTime(2026, 3,  5,  9,  0, 0), // JOLTS
                new DateTime(2026, 3,  6,  7, 15, 0), // ADP Employment
                new DateTime(2026, 3,  7,  7, 30, 0), // NFP Feb
            };

            foreach (var dt in eventsCt)
            {
                long key = (long)dt.DayOfYear * 10000 + dt.Hour * 60 + dt.Minute;
                _newsKeys.Add(key);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // TIMEZONE CONVERSION
        // ─────────────────────────────────────────────────────────────────────
        private static readonly TimeZoneInfo _ctZone =
            TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

        private DateTime ToCT(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utc, DateTimeKind.Utc), _ctZone);

        // ─────────────────────────────────────────────────────────────────────
        // LOGGING
        // ─────────────────────────────────────────────────────────────────────
        private void Log(string msg)   => Print($"[{++_seq:D4}][{Time[0]:MM-dd HH:mm}] {msg}");
        private void Reject(string msg)=> Print($"[{++_seq:D4}][{Time[0]:MM-dd HH:mm}] REJECT: {msg}");
    }
}

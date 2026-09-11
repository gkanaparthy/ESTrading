// ESAVWAPAnchor.cs
// NinjaTrader 8 strategy - AVWAP Anchor v1.1.1
// ES 5-minute chart; use an ETH session template.

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ESAVWAPAnchor : Strategy
    {
        private enum AnchorState { Idle, Anchored, Armed, Contact, InTrade, Dead }

        private Series<bool> bullPP, bearPP, greenBar, redBar;
        private ATR atr;
        private SMA volumeSma;
        private AnchorState state = AnchorState.Idle;

        private int anchorBar = -1, comboEndBar = -1, contactBar = -1;
        private double anchorClose, anchorATR, avwap, cumPV, cumV;
        private string anchorTag = string.Empty;
        private int plotStartBar = -1, anchorSequence, anchorStopOuts;
        private DateTime lastAnchorKillDate, lastResetDate;

        private const string AnchorDrawTag = "ESAVWAPAnchor.ActiveAnchor";
        private const string AnchorTextTag = "ESAVWAPAnchor.ActiveAnchorText";
        private const string ContactDrawTag = "ESAVWAPAnchor.ActiveContact";

        private double plannedStopPrice, lastEntryPrice, initialStopTicks;
        private int lastEntryDirection, lastExitBar = -1;
        private string currentSignalName, tradeTag;
        private bool stopHalved, fillRejected, wasInPosition;
        private double tradeMAE, tradeMFE, tradeSLPoints, tradeTargetPoints;
        private DateTime tradeEntryTime;

        private int dailyTradeCount;
        private double dailyRealizedPnL;
        private bool dailyLossHit, dailyProfitHit;
        private StreamWriter tradeLog;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ESAVWAPAnchor";
                Description = "Anchored-VWAP pullback strategy using Pocket Pivot anchor qualification.";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 60;
                IsInstantiatedOnEachOptimizationIteration = false;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;

                PocketPivotLookback = 10;
                VolumeAverageLength = 50;
                ATRPeriod = 14;
                SwingLookbackBars = 8;
                SwingToleranceATR = 0.5;
                SwingLegATR = 1.0;
                BigBodyATR = 1.5;
                BigBodyRangePct = 0.70;
                EnableContinuationFilter = false;
                PreComboThrustBars = 3;
                PreComboThrustATR = 1.0;

                ArmDistanceATR = 2.0;
                ArmMinBars = 3;
                TriggerWindowBars = 2;
                MaxStopOutsPerAnchor = 2;
                MaxStopPoints = 7.0;
                RRThresholdPoints = 5.0;
                HighRR = 2.0;
                LowRR = 1.0;
                ContractQty = 1;
                EnableStopHalving = false;
                CooldownBars = 1;

                RTHStartHHMMSS = 83000;
                RTHEndHHMMSS = 150000;
                LastEntryHHMMSS = 143000;
                NoNewAnchorHHMMSS = 143000;
                FlattenAtRTHEnd = true;

                MaxTradesPerDay = 6;
                DailyLossLimit = 500.0;
                DailyProfitLimit = 0.0;
                EnableTradeLog = true;
                DrawAnchors = true;

                AddPlot(new Stroke(Brushes.Gold, 2), PlotStyle.Line, "AVWAP");
            }
            else if (State == State.DataLoaded)
            {
                bullPP = new Series<bool>(this, MaximumBarsLookBack.Infinite);
                bearPP = new Series<bool>(this, MaximumBarsLookBack.Infinite);
                greenBar = new Series<bool>(this, MaximumBarsLookBack.Infinite);
                redBar = new Series<bool>(this, MaximumBarsLookBack.Infinite);
                atr = ATR(ATRPeriod);
                volumeSma = SMA(Volume, VolumeAverageLength);
                lastResetDate = Core.Globals.MinDate;
                lastAnchorKillDate = Core.Globals.MinDate;
                ClearActiveDrawings();
                ClearAvwapPlot();

                if (EnableTradeLog)
                {
                    string path = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "trades_" + Name + ".csv");
                    bool newFile = !File.Exists(path);
                    tradeLog = new StreamWriter(path, true);
                    if (newFile)
                    {
                        tradeLog.WriteLine("EntryTime,ExitTime,Dir,Tag,EntryPx,ExitPx,Qty,SLPts,TgtPts,PnL,MAEPts,MFEPts,ExitName,AnchorATR");
                        tradeLog.Flush();
                    }
                }
            }
            else if (State == State.Terminated && tradeLog != null)
            {
                tradeLog.Flush();
                tradeLog.Close();
                tradeLog = null;
            }
        }

        protected override void OnBarUpdate()
        {
            int warmup = Math.Max(Math.Max(PocketPivotLookback, VolumeAverageLength), ATRPeriod) + SwingLookbackBars + PreComboThrustBars + 4;
            if (CurrentBar < warmup) return;

            if (Time[0].Date != lastResetDate.Date)
            {
                lastResetDate = Time[0].Date;
                dailyTradeCount = 0;
                dailyRealizedPnL = 0.0;
                dailyLossHit = false;
                dailyProfitHit = false;
            }

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (wasInPosition)
                {
                    wasInPosition = false;
                    if (lastExitBar < CurrentBar) lastExitBar = CurrentBar;
                    if (state == AnchorState.InTrade) state = AnchorState.Armed;
                }
            }
            else
            {
                wasInPosition = true;
                TrackExcursion();
            }

            ClassifyCurrentBar();

            if (ToTime(Time[0]) >= RTHEndHHMMSS && lastAnchorKillDate.Date != Time[0].Date)
            {
                lastAnchorKillDate = Time[0].Date;
                if (FlattenAtRTHEnd && Position.MarketPosition != MarketPosition.Flat)
                {
                    if (Position.MarketPosition == MarketPosition.Long) ExitLong("RTHClose", currentSignalName);
                    else if (Position.MarketPosition == MarketPosition.Short) ExitShort("RTHClose", currentSignalName);
                    Print(string.Format("{0:yyyy-MM-dd HH:mm} RTH END | flattening position", Time[0]));
                }
                if (state != AnchorState.Idle)
                {
                    Print(string.Format("{0:yyyy-MM-dd HH:mm} RTH END | anchor #{1} killed", Time[0], anchorSequence));
                    ResetAnchor(AnchorState.Idle);
                }
            }

            if (IsLiveAnchor())
            {
                double typical = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;
                cumPV += typical * Volume[0];
                cumV += Volume[0];
                avwap = cumV > 0.0 ? cumPV / cumV : Close[0];
                Values[0][0] = avwap;
            }
            else Values[0][0] = double.NaN;

            if (EnableStopHalving) ManageStopHalving();
            if (state != AnchorState.InTrade) TryDetectCandidate();

            if (state == AnchorState.Anchored) HandleAnchored();
            else if (state == AnchorState.Armed) HandleArmed();
            else if (state == AnchorState.Contact) HandleContact();
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null || string.IsNullOrEmpty(currentSignalName) || order.Name != currentSignalName) return;
            bool rejected = orderState == OrderState.Rejected;
            bool cancelled = orderState == OrderState.Cancelled && filled == 0;
            if (!rejected && !cancelled) return;
            Print(string.Format("{0:yyyy-MM-dd HH:mm} ENTRY {1} | {2} - anchor back to Armed", time, orderState, error));
            if (state == AnchorState.InTrade) state = AnchorState.Armed;
            plannedStopPrice = 0.0;
            currentSignalName = null;
            tradeTag = null;
        }

        private void HandleAnchored()
        {
            if (anchorATR <= 0.0) return;
            bool enoughBars = CurrentBar - comboEndBar >= ArmMinBars;
            bool enoughDistance = Math.Abs(Close[0] - anchorClose) >= ArmDistanceATR * anchorATR;
            if (enoughBars && enoughDistance)
            {
                state = AnchorState.Armed;
                Print(string.Format("{0:yyyy-MM-dd HH:mm} ARMED #{1} | close {2:F2} is {3:F1} ATR from anchor {4:F2}", Time[0], anchorSequence, Close[0], Math.Abs(Close[0] - anchorClose) / anchorATR, anchorClose));
            }
        }

        private void HandleArmed()
        {
            if (!IsContactBar()) return;
            state = AnchorState.Contact;
            contactBar = CurrentBar;
            if (DrawAnchors) Draw.Dot(this, ContactDrawTag, false, 0, avwap, Brushes.White);
            Print(string.Format("{0:yyyy-MM-dd HH:mm} CONTACT #{1} | AVWAP={2:F2}", Time[0], anchorSequence, avwap));
            TryTrigger();
        }

        private void HandleContact()
        {
            if (CurrentBar - contactBar >= TriggerWindowBars)
            {
                state = AnchorState.Armed;
                Print(string.Format("{0:yyyy-MM-dd HH:mm} WINDOW EXPIRED #{1} | back to Armed", Time[0], anchorSequence));
                if (IsContactBar())
                {
                    state = AnchorState.Contact;
                    contactBar = CurrentBar;
                    if (DrawAnchors) Draw.Dot(this, ContactDrawTag, false, 0, avwap, Brushes.White);
                    TryTrigger();
                }
            }
            else TryTrigger();
        }

        private bool IsLiveAnchor()
        {
            return state == AnchorState.Anchored || state == AnchorState.Armed || state == AnchorState.Contact || state == AnchorState.InTrade;
        }

        private void TryDetectCandidate()
        {
            int comboLength = 0;
            if (bullPP[0] && bullPP[1]) comboLength = 2;
            else if (bearPP[0] && bearPP[1]) comboLength = 2;
            else if (bullPP[0] && greenBar[1] && bullPP[2]) comboLength = 3;
            else if (bearPP[0] && redBar[1] && bearPP[2]) comboLength = 3;
            if (comboLength == 0) return;

            int firstOffset = comboLength - 1;
            int candidateBar = CurrentBar - firstOffset;
            if (state != AnchorState.Idle && candidateBar <= comboEndBar) return;
            int now = ToTime(Time[0]);
            if (now > NoNewAnchorHHMMSS && now < RTHEndHHMMSS) return;

            double candidateATR = atr[firstOffset];
            if (candidateATR <= 0.0) return;
            string qualification = null;
            if (IsBigBody(firstOffset, candidateATR)) qualification = "BigBody";
            else if (IsSwing(firstOffset, candidateATR)) qualification = "Swing";
            if (qualification == null) return;

            bool replacing = state != AnchorState.Idle;
            ClearActiveDrawings();
            ClearAvwapPlot();
            ResetAnchor(AnchorState.Anchored);
            anchorSequence++;
            anchorBar = candidateBar;
            comboEndBar = CurrentBar;
            anchorClose = Close[firstOffset];
            anchorATR = candidateATR;
            anchorTag = qualification;
            RebuildAvwapPlot(firstOffset);

            if (DrawAnchors)
            {
                double y = qualification == "BigBody" ? Close[firstOffset] : High[firstOffset] + 2.0 * TickSize;
                Brush brush = qualification == "BigBody" ? Brushes.Magenta : Brushes.Cyan;
                Draw.Diamond(this, AnchorDrawTag, false, firstOffset, y, brush);
                Draw.Text(this, AnchorTextTag, qualification + " #" + anchorSequence, firstOffset, High[firstOffset] + 6.0 * TickSize, Brushes.White);
            }
            Print(string.Format("{0:yyyy-MM-dd HH:mm} ANCHOR #{1} {2} | bar {3:HH:mm} close={4:F2} ATR={5:F2}{6}", Time[0], anchorSequence, qualification, Time[firstOffset], anchorClose, anchorATR, replacing ? " (replaced previous)" : string.Empty));
        }

        private bool IsBigBody(int barsAgo, double valueATR)
        {
            double body = Math.Abs(Close[barsAgo] - Open[barsAgo]);
            double range = High[barsAgo] - Low[barsAgo];
            return body >= BigBodyATR * valueATR && range > 0.0 && body >= BigBodyRangePct * range;
        }

        private bool IsSwing(int firstOffset, double valueATR)
        {
            double comboHigh = double.MinValue, comboLow = double.MaxValue;
            for (int i = firstOffset; i >= 0; i--)
            {
                comboHigh = Math.Max(comboHigh, High[i]);
                comboLow = Math.Min(comboLow, Low[i]);
            }
            double priorHigh = double.MinValue, priorLow = double.MaxValue;
            int lastPrior = firstOffset + SwingLookbackBars;
            for (int i = firstOffset + 1; i <= lastPrior && i <= CurrentBar; i++)
            {
                priorHigh = Math.Max(priorHigh, High[i]);
                priorLow = Math.Min(priorLow, Low[i]);
            }
            bool swingHigh = comboHigh >= priorHigh - SwingToleranceATR * valueATR && comboHigh - priorLow >= SwingLegATR * valueATR;
            bool swingLow = comboLow <= priorLow + SwingToleranceATR * valueATR && priorHigh - comboLow >= SwingLegATR * valueATR;
            if (EnableContinuationFilter)
            {
                int firstPre = firstOffset + 1;
                int lastPre = firstPre + PreComboThrustBars;
                if (lastPre <= CurrentBar)
                {
                    double thrust = Close[firstPre] - Close[lastPre];
                    if (swingHigh && thrust >= PreComboThrustATR * valueATR) swingHigh = false;
                    if (swingLow && -thrust >= PreComboThrustATR * valueATR) swingLow = false;
                }
            }
            return swingHigh || swingLow;
        }

        private bool IsContactBar()
        {
            bool touches = Low[0] <= avwap && avwap <= High[0];
            bool crosses = (Open[0] - avwap) * (Close[1] - avwap) < 0.0;
            return touches || crosses;
        }

        private void TryTrigger()
        {
            bool longTrigger = Close[0] > avwap && Close[0] > Open[0];
            bool shortTrigger = Close[0] < avwap && Close[0] < Open[0];
            if (!longTrigger && !shortTrigger) return;
            string direction = longTrigger ? "LONG" : "SHORT";
            int now = ToTime(Time[0]);

            if (now < RTHStartHHMMSS || now > LastEntryHHMMSS)
            {
                Print(string.Format("{0:yyyy-MM-dd HH:mm} {1} BLOCKED | outside entry hours", Time[0], direction));
                return;
            }
            if (dailyLossHit || dailyProfitHit)
            {
                Print(string.Format("{0:yyyy-MM-dd HH:mm} {1} BLOCKED | daily limit", Time[0], direction));
                return;
            }
            if (dailyTradeCount >= MaxTradesPerDay)
            {
                Print(string.Format("{0:yyyy-MM-dd HH:mm} {1} BLOCKED | max trades", Time[0], direction));
                return;
            }
            if (Position.MarketPosition != MarketPosition.Flat) return;
            if (CooldownBars > 0 && lastExitBar >= 0 && CurrentBar - lastExitBar < CooldownBars)
            {
                Print(string.Format("{0:yyyy-MM-dd HH:mm} {1} BLOCKED | cooldown", Time[0], direction));
                return;
            }

            int sinceContact = CurrentBar - contactBar;
            double highest = double.MinValue, lowest = double.MaxValue;
            for (int i = 0; i <= sinceContact; i++)
            {
                highest = Math.Max(highest, High[i]);
                lowest = Math.Min(lowest, Low[i]);
            }
            plannedStopPrice = longTrigger ? lowest - TickSize : highest + TickSize;
            double stopProxy = Math.Abs(Close[0] - plannedStopPrice);
            if (stopProxy > MaxStopPoints)
            {
                Print(string.Format("{0:yyyy-MM-dd HH:mm} {1} BLOCKED | SL {2:F2} pts > max {3:F2}", Time[0], direction, stopProxy, MaxStopPoints));
                plannedStopPrice = 0.0;
                return;
            }

            double rr = stopProxy < RRThresholdPoints ? HighRR : LowRR;
            double stopTicks = Math.Max(1.0, Math.Round(stopProxy / TickSize));
            double targetTicks = Math.Max(1.0, Math.Round(stopTicks * rr));
            currentSignalName = longTrigger ? "AVWAPLong" : "AVWAPShort";
            tradeTag = anchorTag;
            SetStopLoss(currentSignalName, CalculationMode.Ticks, stopTicks, false);
            SetProfitTarget(currentSignalName, CalculationMode.Ticks, targetTicks);
            if (longTrigger) EnterLong(ContractQty, currentSignalName);
            else EnterShort(ContractQty, currentSignalName);
            state = AnchorState.InTrade;
            Print(string.Format("{0:yyyy-MM-dd HH:mm} {1} SIGNAL #{2} {3} | stop={4:F2} SL~{5:F2}pts RR={6:F1}", Time[0], direction, anchorSequence, anchorTag, plannedStopPrice, stopProxy, rr));
        }

        private void ManageStopHalving()
        {
            if (stopHalved || initialStopTicks <= 0.0 || lastEntryPrice <= 0.0 || string.IsNullOrEmpty(currentSignalName)) return;
            if (Position.MarketPosition == MarketPosition.Flat) return;
            double favorableTicks = Position.MarketPosition == MarketPosition.Long ? (High[0] - lastEntryPrice) / TickSize : (lastEntryPrice - Low[0]) / TickSize;
            if (favorableTicks >= initialStopTicks)
            {
                double reduced = Math.Max(1.0, Math.Round(initialStopTicks / 2.0));
                SetStopLoss(currentSignalName, CalculationMode.Ticks, reduced, false);
                stopHalved = true;
                Print(string.Format("{0:yyyy-MM-dd HH:mm} STOP HALVED | {1:F0}t -> {2:F0}t", Time[0], initialStopTicks, reduced));
            }
        }

        private void TrackExcursion()
        {
            if (lastEntryPrice <= 0.0) return;
            double favorable = lastEntryDirection > 0 ? High[0] - lastEntryPrice : lastEntryPrice - Low[0];
            double adverse = lastEntryDirection > 0 ? lastEntryPrice - Low[0] : High[0] - lastEntryPrice;
            tradeMFE = Math.Max(tradeMFE, favorable);
            tradeMAE = Math.Max(tradeMAE, adverse);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null || execution.Order.OrderState != OrderState.Filled) return;
            OrderAction action = execution.Order.OrderAction;
            if (action == OrderAction.Buy || action == OrderAction.SellShort)
            {
                bool isLong = action == OrderAction.Buy;
                lastEntryPrice = price;
                lastEntryDirection = isLong ? 1 : -1;
                dailyTradeCount++;
                wasInPosition = true;
                tradeMAE = 0.0;
                tradeMFE = 0.0;
                tradeEntryTime = time;
                fillRejected = false;
                if (plannedStopPrice <= 0.0) return;

                double stopPoints = isLong ? price - plannedStopPrice : plannedStopPrice - price;
                stopPoints = Math.Max(TickSize, stopPoints);
                if (stopPoints > MaxStopPoints)
                {
                    fillRejected = true;
                    dailyTradeCount--;
                    tradeSLPoints = stopPoints;
                    tradeTargetPoints = 0.0;
                    Print(string.Format("{0:yyyy-MM-dd HH:mm} FILL REJECTED | SL {1:F2} pts > max {2:F2} - flattening", time, stopPoints, MaxStopPoints));
                    if (isLong) ExitLong("MaxStopExceeded", currentSignalName);
                    else ExitShort("MaxStopExceeded", currentSignalName);
                    return;
                }

                double rr = stopPoints < RRThresholdPoints ? HighRR : LowRR;
                double targetPoints = stopPoints * rr;
                double targetPrice = isLong ? price + targetPoints : price - targetPoints;
                SetStopLoss(currentSignalName, CalculationMode.Price, plannedStopPrice, false);
                SetProfitTarget(currentSignalName, CalculationMode.Price, targetPrice);
                initialStopTicks = Math.Round(stopPoints / TickSize);
                stopHalved = false;
                tradeSLPoints = stopPoints;
                tradeTargetPoints = targetPoints;
                Print(string.Format("{0:yyyy-MM-dd HH:mm} {1} FILL @ {2:F2} | stop={3:F2} ({4:F2}pts) target={5:F2} ({6:F2}pts, {7:F1}:1)", time, isLong ? "LONG" : "SHORT", price, plannedStopPrice, stopPoints, targetPrice, targetPoints, rr));
                return;
            }

            if (action != OrderAction.Sell && action != OrderAction.BuyToCover) return;
            if (lastExitBar < CurrentBar) lastExitBar = CurrentBar;
            if (Position.MarketPosition == MarketPosition.Flat) wasInPosition = false;
            if (lastEntryPrice == 0.0) return;

            double pnl = lastEntryDirection * (price - lastEntryPrice) * quantity * Instrument.MasterInstrument.PointValue;
            dailyRealizedPnL += pnl;
            string exitName = execution.Order.Name;

            if (fillRejected)
            {
                fillRejected = false;
                if (state == AnchorState.InTrade) state = AnchorState.Armed;
                Print(string.Format("{0:yyyy-MM-dd HH:mm} REJECTED-FILL FLATTENED @ {1:F2} | P&L=${2:F2} (not counted as trade/stop-out)", time, price, pnl));
            }
            else
            {
                bool stop = exitName == "Stop loss" || (exitName != "Profit target" && pnl < 0.0);
                if (stop) anchorStopOuts++; else anchorStopOuts = 0;
                if (state == AnchorState.InTrade)
                {
                    if (anchorStopOuts >= MaxStopOutsPerAnchor)
                    {
                        state = AnchorState.Dead;
                        Print(string.Format("{0:yyyy-MM-dd HH:mm} ANCHOR #{1} DEAD | {2} consecutive stop-outs", time, anchorSequence, anchorStopOuts));
                    }
                    else state = AnchorState.Armed;
                }
                Print(string.Format("{0:yyyy-MM-dd HH:mm} EXIT @ {1:F2} ({2}) | P&L=${3:F2} Daily=${4:F2} MAE={5:F2} MFE={6:F2}", time, price, exitName, pnl, dailyRealizedPnL, tradeMAE, tradeMFE));
            }

            if (tradeLog != null)
            {
                tradeLog.WriteLine(string.Join(",", tradeEntryTime.ToString("yyyy-MM-dd HH:mm:ss"), time.ToString("yyyy-MM-dd HH:mm:ss"), lastEntryDirection > 0 ? "Long" : "Short", tradeTag, lastEntryPrice.ToString("F2"), price.ToString("F2"), quantity, tradeSLPoints.ToString("F2"), tradeTargetPoints.ToString("F2"), pnl.ToString("F2"), tradeMAE.ToString("F2"), tradeMFE.ToString("F2"), exitName, anchorATR.ToString("F2")));
                tradeLog.Flush();
            }
            if (DailyLossLimit > 0.0 && dailyRealizedPnL <= -Math.Abs(DailyLossLimit)) dailyLossHit = true;
            if (DailyProfitLimit > 0.0 && dailyRealizedPnL >= Math.Abs(DailyProfitLimit)) dailyProfitHit = true;
            lastEntryPrice = 0.0;
            lastEntryDirection = 0;
            currentSignalName = null;
            plannedStopPrice = 0.0;
            initialStopTicks = 0.0;
            stopHalved = false;
        }

        private void ClearActiveDrawings()
        {
            RemoveDrawObject(AnchorDrawTag);
            RemoveDrawObject(AnchorTextTag);
            RemoveDrawObject(ContactDrawTag);
        }

        private void ClearAvwapPlot()
        {
            if (CurrentBar < 0 || plotStartBar < 0) return;
            int maxBarsAgo = Math.Min(CurrentBar - plotStartBar, CurrentBar);
            if (maxBarsAgo < 0) { plotStartBar = -1; return; }
            for (int i = 0; i <= maxBarsAgo; i++) Values[0][i] = double.NaN;
            plotStartBar = -1;
        }

        private void RebuildAvwapPlot(int firstOffset)
        {
            cumPV = 0.0;
            cumV = 0.0;
            avwap = 0.0;
            for (int i = firstOffset; i >= 0; i--)
            {
                double typical = (Open[i] + High[i] + Low[i] + Close[i]) / 4.0;
                cumPV += typical * Volume[i];
                cumV += Volume[i];
                avwap = cumV > 0.0 ? cumPV / cumV : Close[i];
                Values[0][i] = avwap;
            }
            plotStartBar = CurrentBar - firstOffset;
        }

        private void ResetAnchor(AnchorState newState)
        {
            if (newState == AnchorState.Idle || newState == AnchorState.Dead)
            {
                ClearActiveDrawings();
                ClearAvwapPlot();
            }
            state = newState;
            anchorBar = -1;
            comboEndBar = -1;
            contactBar = -1;
            anchorClose = 0.0;
            anchorATR = 0.0;
            anchorTag = string.Empty;
            cumPV = 0.0;
            cumV = 0.0;
            avwap = 0.0;
            anchorStopOuts = 0;
        }

        private void ClassifyCurrentBar()
        {
            if (CurrentBar < 2)
            {
                bullPP[0] = false; bearPP[0] = false; greenBar[0] = false; redBar[0] = false; return;
            }
            bool up = Close[0] > Close[1];
            bool down = Close[0] < Close[1];
            double maxDown = double.NaN, maxUp = double.NaN;
            int scan = Math.Min(PocketPivotLookback, CurrentBar - 1);
            for (int i = 1; i <= scan; i++)
            {
                if (Close[i] < Close[i + 1] && (double.IsNaN(maxDown) || Volume[i] > maxDown)) maxDown = Volume[i];
                if (Close[i] > Close[i + 1] && (double.IsNaN(maxUp) || Volume[i] > maxUp)) maxUp = Volume[i];
            }
            bullPP[0] = up && !double.IsNaN(maxDown) && Volume[0] > maxDown;
            bearPP[0] = down && !double.IsNaN(maxUp) && Volume[0] > maxUp;
            double avg = volumeSma[0];
            bool aboveAverage = avg > 0.0 && Volume[0] > avg;
            greenBar[0] = up && aboveAverage && !bullPP[0];
            redBar[0] = down && aboveAverage && !bearPP[0];
        }

        #region Properties
        [NinjaScriptProperty, Range(2, 500), Display(Name="Pocket Pivot Lookback (bars)", Order=1, GroupName="01 | Pocket Pivot")] public int PocketPivotLookback { get; set; }
        [NinjaScriptProperty, Range(2, 500), Display(Name="Volume Average Length (bars)", Order=2, GroupName="01 | Pocket Pivot")] public int VolumeAverageLength { get; set; }
        [NinjaScriptProperty, Range(2, 100), Display(Name="ATR Period", Order=1, GroupName="02 | Anchor Qualification")] public int ATRPeriod { get; set; }
        [NinjaScriptProperty, Range(2, 100), Display(Name="Swing Lookback P (bars)", Order=2, GroupName="02 | Anchor Qualification")] public int SwingLookbackBars { get; set; }
        [NinjaScriptProperty, Range(0.0, 3.0), Display(Name="Swing Tolerance (ATR)", Order=3, GroupName="02 | Anchor Qualification")] public double SwingToleranceATR { get; set; }
        [NinjaScriptProperty, Range(0.0, 5.0), Display(Name="Swing Leg (ATR)", Order=4, GroupName="02 | Anchor Qualification")] public double SwingLegATR { get; set; }
        [NinjaScriptProperty, Range(0.5, 5.0), Display(Name="Big Body (ATR)", Order=5, GroupName="02 | Anchor Qualification")] public double BigBodyATR { get; set; }
        [NinjaScriptProperty, Range(0.1, 1.0), Display(Name="Big Body Range %", Order=6, GroupName="02 | Anchor Qualification")] public double BigBodyRangePct { get; set; }
        [NinjaScriptProperty, Display(Name="Enable Continuation Filter", Order=7, GroupName="02 | Anchor Qualification")] public bool EnableContinuationFilter { get; set; }
        [NinjaScriptProperty, Range(1, 20), Display(Name="Pre-Combo Thrust Bars", Order=8, GroupName="02 | Anchor Qualification")] public int PreComboThrustBars { get; set; }
        [NinjaScriptProperty, Range(0.0, 5.0), Display(Name="Pre-Combo Thrust (ATR)", Order=9, GroupName="02 | Anchor Qualification")] public double PreComboThrustATR { get; set; }
        [NinjaScriptProperty, Range(0.5, 10.0), Display(Name="Arm Distance (ATR)", Order=1, GroupName="03 | Arming / Contact")] public double ArmDistanceATR { get; set; }
        [NinjaScriptProperty, Range(0, 50), Display(Name="Arm Min Bars", Order=2, GroupName="03 | Arming / Contact")] public int ArmMinBars { get; set; }
        [NinjaScriptProperty, Range(1, 10), Display(Name="Trigger Window (bars)", Order=3, GroupName="03 | Arming / Contact")] public int TriggerWindowBars { get; set; }
        [NinjaScriptProperty, Range(1, 10), Display(Name="Max Consecutive Stop-outs per Anchor", Order=4, GroupName="03 | Arming / Contact")] public int MaxStopOutsPerAnchor { get; set; }
        [NinjaScriptProperty, Range(0.25, 100.0), Display(Name="Max Stop (points)", Order=1, GroupName="04 | Trade Management")] public double MaxStopPoints { get; set; }
        [NinjaScriptProperty, Range(0.25, 100.0), Display(Name="R:R Threshold (points)", Order=2, GroupName="04 | Trade Management")] public double RRThresholdPoints { get; set; }
        [NinjaScriptProperty, Range(0.5, 10.0), Display(Name="High R:R", Order=3, GroupName="04 | Trade Management")] public double HighRR { get; set; }
        [NinjaScriptProperty, Range(0.5, 10.0), Display(Name="Low R:R", Order=4, GroupName="04 | Trade Management")] public double LowRR { get; set; }
        [NinjaScriptProperty, Range(1, 50), Display(Name="Contracts", Order=5, GroupName="04 | Trade Management")] public int ContractQty { get; set; }
        [NinjaScriptProperty, Display(Name="Enable Stop Halving", Order=6, GroupName="04 | Trade Management")] public bool EnableStopHalving { get; set; }
        [NinjaScriptProperty, Range(0, 100), Display(Name="Post-Exit Cooldown (bars)", Order=7, GroupName="04 | Trade Management")] public int CooldownBars { get; set; }
        [NinjaScriptProperty, Display(Name="RTH Start (HHMMSS)", Order=1, GroupName="05 | Session")] public int RTHStartHHMMSS { get; set; }
        [NinjaScriptProperty, Display(Name="RTH End (HHMMSS)", Order=2, GroupName="05 | Session")] public int RTHEndHHMMSS { get; set; }
        [NinjaScriptProperty, Display(Name="Last Entry (HHMMSS)", Order=3, GroupName="05 | Session")] public int LastEntryHHMMSS { get; set; }
        [NinjaScriptProperty, Display(Name="No New Anchor After (HHMMSS)", Order=4, GroupName="05 | Session")] public int NoNewAnchorHHMMSS { get; set; }
        [NinjaScriptProperty, Display(Name="Flatten At RTH End", Order=5, GroupName="05 | Session")] public bool FlattenAtRTHEnd { get; set; }
        [NinjaScriptProperty, Range(1, 50), Display(Name="Max Trades Per Day", Order=1, GroupName="06 | Risk Controls")] public int MaxTradesPerDay { get; set; }
        [NinjaScriptProperty, Range(0, 1000000), Display(Name="Daily Loss Limit ($)", Order=2, GroupName="06 | Risk Controls")] public double DailyLossLimit { get; set; }
        [NinjaScriptProperty, Range(0, 1000000), Display(Name="Daily Profit Limit ($)", Order=3, GroupName="06 | Risk Controls")] public double DailyProfitLimit { get; set; }
        [NinjaScriptProperty, Display(Name="Write Trade CSV", Order=1, GroupName="07 | Diagnostics")] public bool EnableTradeLog { get; set; }
        [NinjaScriptProperty, Display(Name="Draw Anchors / Contacts", Order=2, GroupName="07 | Diagnostics")] public bool DrawAnchors { get; set; }
        #endregion
    }
}

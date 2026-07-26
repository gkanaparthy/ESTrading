using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.NinjaScript;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.Gui;

namespace NinjaTrader.NinjaScript.Strategies
{
    // Drawing-first rebuild (Tori-style top-down continuity scaffold)
    // Phase 1: identify/draw quality trendlines only. No trade execution in this version.
    public class ESTrendline_v2 : Strategy
    {
        private struct SwingPoint
        {
            public int BIP;
            public int BarIndex;
            public DateTime Time;
            public double Price;
            public bool IsHigh;

            public SwingPoint(int bip, int barIndex, DateTime time, double price, bool isHigh)
            {
                BIP = bip;
                BarIndex = barIndex;
                Time = time;
                Price = price;
                IsHigh = isHigh;
            }
        }

        private class TrendLineModel
        {
            public string Tf;
            public bool IsUp;
            public SwingPoint A;
            public SwingPoint B;
            public bool IsValid;
            public bool IsContinuation;

            public TrendLineModel(string tf, bool isUp, SwingPoint a, SwingPoint b, bool isContinuation)
            {
                Tf = tf;
                IsUp = isUp;
                A = a;
                B = b;
                IsValid = true;
                IsContinuation = isContinuation;
            }

            public double MinutesSpan
            {
                get
                {
                    double m = (B.Time - A.Time).TotalMinutes;
                    return Math.Max(1e-6, m);
                }
            }

            public double SlopePerMinute => (B.Price - A.Price) / MinutesSpan;

            public double ValueAt(DateTime t)
            {
                return A.Price + SlopePerMinute * (t - A.Time).TotalMinutes;
            }

            public string Key => Tf + "_" + (IsUp ? "UP" : "DN") + "_" + A.BarIndex + "_" + B.BarIndex;
        }

        private const int BIP_PRIMARY = 0; // 2m
        private const int BIP_1H = 1;
        private const int BIP_4H = 2;
        private const int BIP_DAILY = 3;

        private readonly List<SwingPoint> pivHi2 = new List<SwingPoint>();
        private readonly List<SwingPoint> pivLo2 = new List<SwingPoint>();
        private readonly List<SwingPoint> pivHi1h = new List<SwingPoint>();
        private readonly List<SwingPoint> pivLo1h = new List<SwingPoint>();
        private readonly List<SwingPoint> pivHi4h = new List<SwingPoint>();
        private readonly List<SwingPoint> pivLo4h = new List<SwingPoint>();
        private readonly List<SwingPoint> pivHiD = new List<SwingPoint>();
        private readonly List<SwingPoint> pivLoD = new List<SwingPoint>();

        private TrendLineModel upD, dnD;
        private TrendLineModel up4, dn4;
        private TrendLineModel up1, dn1;
        private TrendLineModel up2, dn2;

        private TrendLineModel up2Parent, dn2Parent;

        [NinjaScriptProperty]
        [Range(2, 20)]
        [Display(Name = "SwingStrength", GroupName = "1. Structure", Order = 1)]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(50, 2000)]
        [Display(Name = "MaxSwingLookback", GroupName = "1. Structure", Order = 2)]
        public int MaxSwingLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, 40)]
        [Display(Name = "MinSwingDiffTicks", GroupName = "1. Structure", Order = 3)]
        public int MinSwingDiffTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ShowTopDownLines", GroupName = "2. Visual", Order = 1)]
        public bool ShowTopDownLines { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show2mParentAndContinuation", GroupName = "2. Visual", Order = 2)]
        public bool Show2mParentAndContinuation { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EnableLogs", GroupName = "2. Visual", Order = 3)]
        public bool EnableLogs { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ESTrendline_v2";
                Description = "Drawing-first Tori-style top-down trendline continuity strategy scaffold (Daily→4H→1H→2M).";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                IsExitOnSessionCloseStrategy = false;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                BarsRequiredToTrade = 100;

                SwingStrength = 5;
                MaxSwingLookback = 400;
                MinSwingDiffTicks = 4;

                ShowTopDownLines = true;
                Show2mParentAndContinuation = true;
                EnableLogs = true;
            }
            else if (State == State.Configure)
            {
                // Primary is expected to be 2m chart.
                AddDataSeries(BarsPeriodType.Minute, 60);  // 1H
                AddDataSeries(BarsPeriodType.Minute, 240); // 4H
                AddDataSeries(BarsPeriodType.Day, 1);      // Daily
            }
        }

        protected override void OnBarUpdate()
        {
            int bip = BarsInProgress;
            if (CurrentBars[bip] < SwingStrength * 2 + 5)
                return;

            DetectConfirmedSwing(bip);

            // Only render once per primary bar.
            if (bip != BIP_PRIMARY)
                return;

            // Build top-down lines with continuity
            RebuildDaily();
            Rebuild4H();
            Rebuild1H();
            Rebuild2M();

            DrawTopDown();
        }

        private void DetectConfirmedSwing(int bip)
        {
            int i = CurrentBars[bip] - SwingStrength;
            if (i < SwingStrength)
                return;

            double hi = Highs[bip][CurrentBars[bip] - i];
            double lo = Lows[bip][CurrentBars[bip] - i];
            bool isHi = true;
            bool isLo = true;

            for (int j = i - SwingStrength; j <= i + SwingStrength; j++)
            {
                if (j < 0 || j > CurrentBars[bip] || j == i)
                    continue;

                double h = Highs[bip][CurrentBars[bip] - j];
                double l = Lows[bip][CurrentBars[bip] - j];
                if (h >= hi) isHi = false;
                if (l <= lo) isLo = false;
                if (!isHi && !isLo) break;
            }

            if (isHi)
                TryAddSwing(GetHiList(bip), new SwingPoint(bip, i, Times[bip][CurrentBars[bip] - i], hi, true));
            if (isLo)
                TryAddSwing(GetLoList(bip), new SwingPoint(bip, i, Times[bip][CurrentBars[bip] - i], lo, false));
        }

        private List<SwingPoint> GetHiList(int bip)
        {
            if (bip == BIP_DAILY) return pivHiD;
            if (bip == BIP_4H) return pivHi4h;
            if (bip == BIP_1H) return pivHi1h;
            return pivHi2;
        }

        private List<SwingPoint> GetLoList(int bip)
        {
            if (bip == BIP_DAILY) return pivLoD;
            if (bip == BIP_4H) return pivLo4h;
            if (bip == BIP_1H) return pivLo1h;
            return pivLo2;
        }

        private bool TryAddSwing(List<SwingPoint> list, SwingPoint sp)
        {
            if (list.Count > 0)
            {
                SwingPoint last = list[list.Count - 1];
                if (sp.BarIndex <= last.BarIndex) return false;
                if (Math.Abs((sp.Price - last.Price) / TickSize) < MinSwingDiffTicks) return false;
            }
            list.Add(sp);
            while (list.Count > MaxSwingLookback) list.RemoveAt(0);
            return true;
        }

        private TrendLineModel BuildSimpleLine(string tf, bool isUp, List<SwingPoint> swings)
        {
            if (swings.Count < 2) return null;
            for (int b = swings.Count - 1; b >= 1; b--)
            {
                for (int a = b - 1; a >= 0; a--)
                {
                    SwingPoint A = swings[a];
                    SwingPoint B = swings[b];
                    if (isUp && B.Price <= A.Price) continue;
                    if (!isUp && B.Price >= A.Price) continue;
                    TrendLineModel line = new TrendLineModel(tf, isUp, A, B, false);
                    if (!IsSlopeValid(line)) continue;
                    if (!ValidateNoCrossByClose(line, BIPFromTf(tf), A.Time, Times[BIP_PRIMARY][0])) continue;
                    return line;
                }
            }
            return null;
        }

        // Continuation build: previous B must become new A.
        private TrendLineModel BuildContinuationFromParent(string tf, bool isUp, TrendLineModel parent, List<SwingPoint> swings)
        {
            if (parent == null || swings.Count == 0)
                return null;

            SwingPoint A = parent.B;
            for (int i = swings.Count - 1; i >= 0; i--)
            {
                SwingPoint B = swings[i];
                if (B.Time <= A.Time) continue;
                if (isUp && B.Price <= A.Price) continue;
                if (!isUp && B.Price >= A.Price) continue;

                TrendLineModel line = new TrendLineModel(tf, isUp, A, B, true);
                if (!IsSlopeValid(line)) continue;
                if (!ValidateNoCrossByClose(line, BIPFromTf(tf), A.Time, Times[BIP_PRIMARY][0])) continue;
                return line;
            }
            return null;
        }

        private bool IsSlopeValid(TrendLineModel line)
        {
            double slopeTicksPerMinute = (line.SlopePerMinute / TickSize);
            if (line.IsUp && slopeTicksPerMinute <= 0) return false;
            if (!line.IsUp && slopeTicksPerMinute >= 0) return false;
            if (Math.Abs(slopeTicksPerMinute) < 1e-6) return false;
            return true;
        }

        private int BIPFromTf(string tf)
        {
            if (tf == "D") return BIP_DAILY;
            if (tf == "4H") return BIP_4H;
            if (tf == "1H") return BIP_1H;
            return BIP_PRIMARY;
        }

        private bool ValidateNoCrossByClose(TrendLineModel line, int bip, DateTime fromTime, DateTime toTime)
        {
            int cb = CurrentBars[bip];
            for (int barsAgo = 0; barsAgo <= cb; barsAgo++)
            {
                DateTime t = Times[bip][barsAgo];
                if (t < fromTime) break;
                if (t > toTime) continue;
                double close = Closes[bip][barsAgo];
                double lv = line.ValueAt(t);
                if (line.IsUp && close < lv) return false;
                if (!line.IsUp && close > lv) return false;
            }
            return true;
        }

        private void RebuildDaily()
        {
            upD = BuildSimpleLine("D", true, pivLoD);
            dnD = BuildSimpleLine("D", false, pivHiD);
        }

        private void Rebuild4H()
        {
            TrendLineModel upBase = BuildSimpleLine("4H", true, pivLo4h);
            TrendLineModel dnBase = BuildSimpleLine("4H", false, pivHi4h);

            up4 = BuildContinuationFromParent("4H", true, upD, pivLo4h) ?? upBase;
            dn4 = BuildContinuationFromParent("4H", false, dnD, pivHi4h) ?? dnBase;
        }

        private void Rebuild1H()
        {
            TrendLineModel upBase = BuildSimpleLine("1H", true, pivLo1h);
            TrendLineModel dnBase = BuildSimpleLine("1H", false, pivHi1h);

            up1 = BuildContinuationFromParent("1H", true, up4, pivLo1h) ?? upBase;
            dn1 = BuildContinuationFromParent("1H", false, dn4, pivHi1h) ?? dnBase;
        }

        private void Rebuild2M()
        {
            TrendLineModel upBase = BuildSimpleLine("2M", true, pivLo2);
            TrendLineModel dnBase = BuildSimpleLine("2M", false, pivHi2);

            up2Parent = up1;
            dn2Parent = dn1;

            up2 = BuildContinuationFromParent("2M", true, up1, pivLo2) ?? upBase;
            dn2 = BuildContinuationFromParent("2M", false, dn1, pivHi2) ?? dnBase;
        }

        private int PrimaryBarsAgoAt(DateTime t)
        {
            int cb = CurrentBars[BIP_PRIMARY];
            for (int barsAgo = 0; barsAgo <= cb; barsAgo++)
            {
                DateTime x = Times[BIP_PRIMARY][barsAgo];
                if (x <= t)
                    return barsAgo;
            }
            return cb;
        }

        private void DrawRay(string tag, TrendLineModel line, Brush brush, DashStyleHelper dash, int width)
        {
            if (line == null || !line.IsValid)
            {
                RemoveDrawObject(tag);
                return;
            }

            int aAgo = PrimaryBarsAgoAt(line.A.Time);
            if (aAgo < 0) aAgo = CurrentBars[BIP_PRIMARY];
            if (aAgo > CurrentBars[BIP_PRIMARY]) aAgo = CurrentBars[BIP_PRIMARY];

            double yNow = line.ValueAt(Times[BIP_PRIMARY][0]);
            Draw.Line(this, tag, false,
                aAgo, line.A.Price,
                0, yNow,
                brush, dash, width);
        }

        private void DrawTopDown()
        {
            if (!ShowTopDownLines)
                return;

            // HTF context lines (faint)
            DrawRay("ESTv2.Up.D", upD, Brushes.DarkGreen, DashStyleHelper.Dot, 1);
            DrawRay("ESTv2.Dn.D", dnD, Brushes.DarkRed, DashStyleHelper.Dot, 1);
            DrawRay("ESTv2.Up.4H", up4, Brushes.Green, DashStyleHelper.Dash, 1);
            DrawRay("ESTv2.Dn.4H", dn4, Brushes.OrangeRed, DashStyleHelper.Dash, 1);
            DrawRay("ESTv2.Up.1H", up1, Brushes.LimeGreen, DashStyleHelper.Dash, 1);
            DrawRay("ESTv2.Dn.1H", dn1, Brushes.Orange, DashStyleHelper.Dash, 1);

            // 2m active lines (bold)
            DrawRay("ESTv2.Up.2M", up2, Brushes.LimeGreen, DashStyleHelper.Solid, 2);
            DrawRay("ESTv2.Dn.2M", dn2, Brushes.OrangeRed, DashStyleHelper.Solid, 2);

            if (Show2mParentAndContinuation)
            {
                DrawRay("ESTv2.Up.2M.Parent", up2Parent, Brushes.LightGreen, DashStyleHelper.Dash, 1);
                DrawRay("ESTv2.Dn.2M.Parent", dn2Parent, Brushes.DeepSkyBlue, DashStyleHelper.Solid, 2);
            }
            else
            {
                RemoveDrawObject("ESTv2.Up.2M.Parent");
                RemoveDrawObject("ESTv2.Dn.2M.Parent");
            }

            if (EnableLogs && IsFirstTickOfBar)
            {
                Print($"[ESTrendline_v2] lines: D(up={upD!=null},dn={dnD!=null}) 4H(up={up4!=null},dn={dn4!=null}) 1H(up={up1!=null},dn={dn1!=null}) 2M(up={up2!=null},dn={dn2!=null})");
            }
        }
    }
}

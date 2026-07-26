#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using System.Windows.Media;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SimpleVolumePocketPivotsNT : Indicator
    {
        private Brush ppvBrush;
        private Brush upBrush;
        private Brush downBrush;
        private Brush dryBrush;
        private Brush noiseBrush;
        private Brush bullSnortBrush;
        private Brush bullSnortBackBrush;
        private Brush volumeAverageBrush;
        private Brush statsTextBrush;
        private Brush statsBackBrush;
        private Brush statsBorderBrush;
        private SimpleFont statsFont;

        [Range(2, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Volume average length", GroupName = "Parameters", Order = 0)]
        public int VolumeAverageLength { get; set; }

        [Range(2, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Pocket pivot lookback", GroupName = "Parameters", Order = 1)]
        public int PocketPivotLookback { get; set; }

        [Range(0.01, 1.0), NinjaScriptProperty]
        [Display(Name = "Low volume fraction", GroupName = "Parameters", Order = 2)]
        public double DryVolumeFraction { get; set; }

        [Range(2, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Relative volume length", GroupName = "Parameters", Order = 3)]
        public int RelativeVolumeLength { get; set; }

        [Range(2, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Up/down ratio length", GroupName = "Parameters", Order = 4)]
        public int UpDownRatioLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Include current bar in RVol avg", GroupName = "Parameters", Order = 5)]
        public bool IncludeCurrentBarInRvolAverage { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use previous close for up/down", GroupName = "Parameters", Order = 6)]
        public bool UsePreviousCloseForUpDown { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show volume average", GroupName = "Display", Order = 10)]
        public bool ShowVolumeAverage { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show stats box", GroupName = "Display", Order = 11)]
        public bool ShowStatsBox { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display RVol as multiplier", GroupName = "Display", Order = 12)]
        public bool DisplayRvolAsMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Paint price bars", GroupName = "Display", Order = 13)]
        public bool PaintPriceBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show bull snorts", GroupName = "Bull Snort", Order = 20)]
        public bool ShowBullSnorts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show bull snort background", GroupName = "Bull Snort", Order = 21)]
        public bool ShowBullSnortBackground { get; set; }

        [Range(2, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Bull snort avg length", GroupName = "Bull Snort", Order = 22)]
        public int BullSnortAverageLength { get; set; }

        [Range(1.0, 10.0), NinjaScriptProperty]
        [Display(Name = "Bull snort volume multiple", GroupName = "Bull Snort", Order = 23)]
        public double BullSnortVolumeMultiple { get; set; }

        [Range(0.05, 1.0), NinjaScriptProperty]
        [Display(Name = "Close in top fraction", GroupName = "Bull Snort", Order = 24)]
        public double BullSnortCloseInTopFraction { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show highest volume labels", GroupName = "Labels", Order = 30)]
        public bool ShowHighestVolumeLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show lowest volume labels", GroupName = "Labels", Order = 31)]
        public bool ShowLowestVolumeLabels { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Minimalist volume indicator with pocket pivots, dry volume, relative volume stats, bull snorts, and highest-volume labels.";
                Name = "SimpleVolumePocketPivotsNT";
                Calculate = Calculate.OnEachTick;
                IsOverlay = false;
                DrawOnPricePanel = false;
                DisplayInDataBox = true;
                PaintPriceMarkers = true;
                ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = true;

                VolumeAverageLength = 50;
                PocketPivotLookback = 10;
                DryVolumeFraction = 0.20;
                RelativeVolumeLength = 50;
                UpDownRatioLength = 50;
                IncludeCurrentBarInRvolAverage = false;
                UsePreviousCloseForUpDown = true;

                ShowVolumeAverage = true;
                ShowStatsBox = true;
                DisplayRvolAsMultiple = false;
                PaintPriceBars = false;

                ShowBullSnorts = true;
                ShowBullSnortBackground = true;
                BullSnortAverageLength = 50;
                BullSnortVolumeMultiple = 3.0;
                BullSnortCloseInTopFraction = 0.35;

                ShowHighestVolumeLabels = true;
                ShowLowestVolumeLabels = true;

                AddPlot(new Stroke(Brushes.DimGray, 4), PlotStyle.Bar, "VolumeBars");
                AddPlot(new Stroke(Brushes.SlateGray, 1), PlotStyle.Line, "VolumeAverage");
                AddPlot(new Stroke(Brushes.MediumPurple, 5), PlotStyle.Dot, "BullSnortMarker");
            }
            else if (State == State.DataLoaded)
            {
                ppvBrush = MakeBrush(33, 150, 243);
                downBrush = MakeBrush(242, 54, 69);
                upBrush = MakeBrush(34, 171, 148);
                dryBrush = MakeBrush(255, 152, 0);
                noiseBrush = MakeBrush(120, 123, 134);
                bullSnortBrush = MakeBrush(171, 71, 188);
                bullSnortBackBrush = MakeBrush(171, 71, 188, 50);
                volumeAverageBrush = MakeBrush(144, 164, 174);
                statsTextBrush = MakeBrush(38, 50, 56);
                statsBackBrush = MakeBrush(255, 255, 255, 225);
                statsBorderBrush = MakeBrush(120, 123, 134, 180);
                statsFont = new SimpleFont("Segoe UI", 12);

                Plots[0].Width = 4;
                Plots[1].Brush = volumeAverageBrush;
                Plots[2].Brush = bullSnortBrush;
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1)
            {
                Values[0][0] = Volume[0];
                Values[1][0] = Volume[0];
                Values[2][0] = double.NaN;
                PlotBrushes[0][0] = noiseBrush;
                PlotBrushes[1][0] = volumeAverageBrush;
                PlotBrushes[2][0] = bullSnortBrush;
                return;
            }

            double currentVolume = Volume[0];
            double avgVolume = AverageVolume(VolumeAverageLength, true);
            double rvolBase = AverageVolume(RelativeVolumeLength, IncludeCurrentBarInRvolAverage);
            double relativeVolume = (!double.IsNaN(rvolBase) && rvolBase > 0.0) ? currentVolume / rvolBase : double.NaN;
            double upDownRatio = UpDownVolumeRatio(UpDownRatioLength);
            double currentTurnover = Close[0] * currentVolume;
            double avgDollarVolume = AverageDollarVolume(RelativeVolumeLength, IncludeCurrentBarInRvolAverage);

            bool isUpBar = IsUpBar(0);
            bool isDownBar = IsDownBar(0);
            bool isPocketPivot = IsPocketPivot(isUpBar);
            bool isDryVolume = !double.IsNaN(avgVolume) && avgVolume > 0.0 && currentVolume <= avgVolume * DryVolumeFraction;
            bool isHighUpVolume = isUpBar && !double.IsNaN(avgVolume) && currentVolume > avgVolume;
            bool isHighDownVolume = isDownBar && !double.IsNaN(avgVolume) && currentVolume > avgVolume;
            bool isBullSnort = IsBullSnort();

            Brush volumeBrush = noiseBrush;
            if (isDryVolume)
                volumeBrush = dryBrush;
            else if (isPocketPivot)
                volumeBrush = ppvBrush;
            else if (isHighDownVolume)
                volumeBrush = downBrush;
            else if (isHighUpVolume)
                volumeBrush = upBrush;

            Values[0][0] = currentVolume;
            Values[1][0] = ShowVolumeAverage ? avgVolume : double.NaN;
            Values[2][0] = (ShowBullSnorts && isBullSnort)
                ? currentVolume + Math.Max(1.0, Math.Max(currentVolume, avgVolume) * 0.06)
                : double.NaN;

            PlotBrushes[0][0] = volumeBrush;
            PlotBrushes[1][0] = volumeAverageBrush;
            PlotBrushes[2][0] = bullSnortBrush;

            if (PaintPriceBars)
            {
                BarBrush = volumeBrush;
                CandleOutlineBrush = volumeBrush;
            }
            else
            {
                BarBrush = null;
                CandleOutlineBrush = null;
            }

            if (ShowBullSnorts && ShowBullSnortBackground && isBullSnort)
                BackBrush = bullSnortBackBrush;
            else
                BackBrush = null;

            if (ShowStatsBox)
                DrawStatsText(avgVolume, relativeVolume, upDownRatio, currentTurnover, avgDollarVolume);
            else
                RemoveDrawObject(Name + "_Stats");

            DrawVolumeLabels(avgVolume);
        }

        private void DrawStatsText(double avgVolume, double relativeVolume, double upDownRatio, double currentTurnover, double avgDollarVolume)
        {
            string rvolText = double.IsNaN(relativeVolume)
                ? "n/a"
                : (DisplayRvolAsMultiple ? relativeVolume.ToString("0.00", CultureInfo.InvariantCulture) + "x"
                                         : (relativeVolume * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%");

            string ratioText = double.IsNaN(upDownRatio)
                ? "n/a"
                : (double.IsInfinity(upDownRatio)
                    ? "inf"
                    : upDownRatio.ToString("0.00", CultureInfo.InvariantCulture));

            string text =
                "AvgVol (" + VolumeAverageLength + "): " + FormatCompactNumber(avgVolume) + Environment.NewLine +
                "RVol (" + RelativeVolumeLength + "): " + rvolText + Environment.NewLine +
                "U/D Vol (" + UpDownRatioLength + "): " + ratioText + Environment.NewLine +
                "Turnover: " + FormatCurrencyCompact(currentTurnover) + Environment.NewLine +
                "Avg$Vol: " + FormatCurrencyCompact(avgDollarVolume);

            int statsBarsAgo = Math.Min(3, CurrentBar);
            double statsY = GetStatsAnchorVolume();

            Draw.Text(
                this,
                Name + "_Stats",
                false,
                text,
                statsBarsAgo,
                statsY,
                0,
                statsTextBrush,
                statsFont,
                TextAlignment.Right,
                statsBorderBrush,
                statsBackBrush,
                90);
        }

        private void DrawVolumeLabels(double avgVolume)
        {
            if (!IsDailyOrHigher())
                return;

            int barsAgo = 0;
            if (State == State.Realtime)
            {
                if (!IsFirstTickOfBar || CurrentBar < 1)
                    return;

                barsAgo = 1;
            }

            int barIndex = CurrentBar - barsAgo;
            string highTag = Name + "_HighVol_" + barIndex;
            string lowTag = Name + "_LowVol_" + barIndex;

            double labelVolume = Volume[barsAgo];
            double labelAverage = AverageVolumeForBar(VolumeAverageLength, barsAgo, true);
            double offset = Math.Max(1.0, Math.Max(labelAverage, labelVolume) * 0.06);

            if (ShowHighestVolumeLabels)
            {
                string highLabel = GetHighestVolumeLabel(barsAgo);
                if (!string.IsNullOrEmpty(highLabel))
                    Draw.Text(this, highTag, highLabel, barsAgo, labelVolume + offset, ppvBrush);
                else
                    RemoveDrawObject(highTag);
            }
            else
                RemoveDrawObject(highTag);

            if (ShowLowestVolumeLabels)
            {
                string lowLabel = GetLowestVolumeLabel(barsAgo);
                if (!string.IsNullOrEmpty(lowLabel))
                    Draw.Text(this, lowTag, lowLabel, barsAgo, labelVolume + offset, dryBrush);
                else
                    RemoveDrawObject(lowTag);
            }
            else
                RemoveDrawObject(lowTag);
        }

        private string GetHighestVolumeLabel(int barsAgo)
        {
            int priorBarsAvailable = CurrentBar - barsAgo;
            if (priorBarsAvailable <= 0)
                return string.Empty;

            double priorAll = MaxVolumePrior(barsAgo, priorBarsAvailable);
            if (!double.IsNaN(priorAll) && Volume[barsAgo] >= priorAll)
                return "HVE";

            if (priorBarsAvailable >= 252)
            {
                double priorYear = MaxVolumePrior(barsAgo, 252);
                if (!double.IsNaN(priorYear) && Volume[barsAgo] >= priorYear)
                    return "HVY";
            }

            if (priorBarsAvailable >= 63)
            {
                double priorQuarter = MaxVolumePrior(barsAgo, 63);
                if (!double.IsNaN(priorQuarter) && Volume[barsAgo] >= priorQuarter)
                    return "HVQ";
            }

            return string.Empty;
        }

        private string GetLowestVolumeLabel(int barsAgo)
        {
            int priorBarsAvailable = CurrentBar - barsAgo;
            if (priorBarsAvailable < 63)
                return string.Empty;

            if (priorBarsAvailable >= 252)
            {
                double priorYear = MinVolumePrior(barsAgo, 252);
                if (!double.IsNaN(priorYear) && Volume[barsAgo] <= priorYear)
                    return "LVY";
            }

            double priorQuarter = MinVolumePrior(barsAgo, 63);
            if (!double.IsNaN(priorQuarter) && Volume[barsAgo] <= priorQuarter)
                return "LVQ";

            return string.Empty;
        }

        private bool IsPocketPivot(bool isUpBar)
        {
            if (!isUpBar)
                return false;

            double maxDownVolume = MaxDownVolume(PocketPivotLookback);
            return !double.IsNaN(maxDownVolume) && Volume[0] > maxDownVolume;
        }

        private bool IsBullSnort()
        {
            if (!ShowBullSnorts || CurrentBar < 1)
                return false;

            double bullAverage = AverageVolume(BullSnortAverageLength, true);
            if (double.IsNaN(bullAverage) || bullAverage <= 0.0)
                return false;

            double range = High[0] - Low[0];
            if (range <= 0.0)
                return false;

            bool heavyVolume = Volume[0] >= bullAverage * BullSnortVolumeMultiple;
            bool closesInUpperPart = Close[0] >= High[0] - range * BullSnortCloseInTopFraction;
            bool abovePreviousClose = Close[0] > Close[1];

            return heavyVolume && closesInUpperPart && abovePreviousClose;
        }

        private bool IsUpBar(int barsAgo)
        {
            if (barsAgo < 0 || CurrentBar < barsAgo)
                return false;

            if (!UsePreviousCloseForUpDown)
                return Close[barsAgo] >= Open[barsAgo];

            if (CurrentBar == barsAgo)
                return Close[barsAgo] >= Open[barsAgo];

            return Close[barsAgo] > Close[barsAgo + 1];
        }

        private bool IsDownBar(int barsAgo)
        {
            if (barsAgo < 0 || CurrentBar < barsAgo)
                return false;

            if (!UsePreviousCloseForUpDown)
                return Close[barsAgo] < Open[barsAgo];

            if (CurrentBar == barsAgo)
                return Close[barsAgo] < Open[barsAgo];

            return Close[barsAgo] < Close[barsAgo + 1];
        }

        private double AverageVolume(int length, bool includeCurrentBar)
        {
            return AverageVolumeForBar(length, 0, includeCurrentBar);
        }

        private double AverageVolumeForBar(int length, int barsAgo, bool includeEvaluatedBar)
        {
            int startBarsAgo = includeEvaluatedBar ? barsAgo : barsAgo + 1;
            int available = CurrentBar - startBarsAgo + 1;
            if (available <= 0)
                return double.NaN;

            int count = Math.Min(length, available);
            double sum = 0.0;
            for (int i = startBarsAgo; i < startBarsAgo + count; i++)
                sum += Volume[i];

            return count > 0 ? sum / count : double.NaN;
        }

        private double AverageDollarVolume(int length, bool includeCurrentBar)
        {
            int startBarsAgo = includeCurrentBar ? 0 : 1;
            int available = CurrentBar - startBarsAgo + 1;
            if (available <= 0)
                return double.NaN;

            int count = Math.Min(length, available);
            double sum = 0.0;
            for (int i = startBarsAgo; i < startBarsAgo + count; i++)
                sum += Close[i] * Volume[i];

            return count > 0 ? sum / count : double.NaN;
        }

        private double UpDownVolumeRatio(int lookback)
        {
            int count = Math.Min(lookback, CurrentBar + 1);
            if (count <= 0)
                return double.NaN;

            double upSum = 0.0;
            double downSum = 0.0;

            for (int i = 0; i < count; i++)
            {
                if (IsUpBar(i))
                    upSum += Volume[i];
                else if (IsDownBar(i))
                    downSum += Volume[i];
            }

            if (downSum <= 0.0)
                return upSum > 0.0 ? double.PositiveInfinity : double.NaN;

            return upSum / downSum;
        }

        private double MaxDownVolume(int lookback)
        {
            int count = Math.Min(lookback, CurrentBar);
            double maxDown = double.NaN;

            for (int i = 1; i <= count; i++)
            {
                if (!IsDownBar(i))
                    continue;

                if (double.IsNaN(maxDown) || Volume[i] > maxDown)
                    maxDown = Volume[i];
            }

            return maxDown;
        }

        private double MaxVolumePrior(int barsAgo, int lookback)
        {
            int startBarsAgo = barsAgo + 1;
            int endBarsAgo = Math.Min(CurrentBar, barsAgo + lookback);
            if (startBarsAgo > endBarsAgo)
                return double.NaN;

            double maxVolume = Volume[startBarsAgo];
            for (int i = startBarsAgo + 1; i <= endBarsAgo; i++)
                maxVolume = Math.Max(maxVolume, Volume[i]);

            return maxVolume;
        }

        private double MinVolumePrior(int barsAgo, int lookback)
        {
            int startBarsAgo = barsAgo + 1;
            int endBarsAgo = Math.Min(CurrentBar, barsAgo + lookback);
            if (startBarsAgo > endBarsAgo)
                return double.NaN;

            double minVolume = Volume[startBarsAgo];
            for (int i = startBarsAgo + 1; i <= endBarsAgo; i++)
                minVolume = Math.Min(minVolume, Volume[i]);

            return minVolume;
        }

        private double GetStatsAnchorVolume()
        {
            int lookback = Math.Min(100, CurrentBar + 1);
            double maxVolume = 0.0;

            for (int i = 0; i < lookback; i++)
                maxVolume = Math.Max(maxVolume, Volume[i]);

            return maxVolume > 0.0 ? maxVolume * 0.92 : 1.0;
        }

        private bool IsDailyOrHigher()
        {
            return BarsPeriod.BarsPeriodType == BarsPeriodType.Day
                || BarsPeriod.BarsPeriodType == BarsPeriodType.Week
                || BarsPeriod.BarsPeriodType == BarsPeriodType.Month;
        }

        private static Brush MakeBrush(byte r, byte g, byte b, byte a = 255)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private static string FormatCompactNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "n/a";

            double abs = Math.Abs(value);
            if (abs >= 1000000000)
                return (value / 1000000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "B";
            if (abs >= 1000000)
                return (value / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "M";
            if (abs >= 1000)
                return (value / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "K";

            return value.ToString("0", CultureInfo.InvariantCulture);
        }

        private static string FormatCurrencyCompact(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "n/a";

            string prefix = value < 0 ? "-$" : "$";
            double abs = Math.Abs(value);

            if (abs >= 1000000000)
                return prefix + (abs / 1000000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "B";
            if (abs >= 1000000)
                return prefix + (abs / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "M";
            if (abs >= 1000)
                return prefix + (abs / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "K";

            return prefix + abs.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}

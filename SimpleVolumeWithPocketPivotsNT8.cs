#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SimpleVolumeWithPocketPivotsNT8 : Indicator
    {
        private Brush ppvBrush;
        private Brush upBrush;
        private Brush downBrush;
        private Brush dryBrush;
        private Brush noiseBrush;
        private Brush volumeAverageBrush;

        [Range(2, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Volume average length", GroupName = "Parameters", Order = 0)]
        public int VolumeAverageLength { get; set; }

        [Range(2, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "Pocket pivot lookback", GroupName = "Parameters", Order = 1)]
        public int PocketPivotLookback { get; set; }

        [Range(0.01, 1.0), NinjaScriptProperty]
        [Display(Name = "Low volume fraction", GroupName = "Parameters", Order = 2)]
        public double DryVolumeFraction { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use previous close for up/down", GroupName = "Parameters", Order = 3)]
        public bool UsePreviousCloseForUpDown { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show volume average", GroupName = "Display", Order = 10)]
        public bool ShowVolumeAverage { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Paint price bars", GroupName = "Display", Order = 11)]
        public bool PaintPriceBars { get; set; }

        [XmlIgnore]
        [Display(Name = "Pocket Pivot Color", GroupName = "Colors", Order = 20)]
        public Brush PocketPivotBrush { get; set; }

        [Browsable(false)]
        public string PocketPivotBrushSerializable
        {
            get { return Serialize.BrushToString(PocketPivotBrush); }
            set { PocketPivotBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Up Volume Color", GroupName = "Colors", Order = 21)]
        public Brush UpVolumeBrush { get; set; }

        [Browsable(false)]
        public string UpVolumeBrushSerializable
        {
            get { return Serialize.BrushToString(UpVolumeBrush); }
            set { UpVolumeBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Down Volume Color", GroupName = "Colors", Order = 22)]
        public Brush DownVolumeBrush { get; set; }

        [Browsable(false)]
        public string DownVolumeBrushSerializable
        {
            get { return Serialize.BrushToString(DownVolumeBrush); }
            set { DownVolumeBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Dry Volume Color", GroupName = "Colors", Order = 23)]
        public Brush DryVolumeBrush { get; set; }

        [Browsable(false)]
        public string DryVolumeBrushSerializable
        {
            get { return Serialize.BrushToString(DryVolumeBrush); }
            set { DryVolumeBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Noise Color", GroupName = "Colors", Order = 24)]
        public Brush NoiseBrush { get; set; }

        [Browsable(false)]
        public string NoiseBrushSerializable
        {
            get { return Serialize.BrushToString(NoiseBrush); }
            set { NoiseBrush = Serialize.StringToBrush(value); }
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Simple volume histogram with pocket pivots, high-volume up/down bars, dry-volume bars, and optional price-bar painting, modeled after the TradingView Simple Volume with Pocket Pivots indicator.";
                Name = "SimpleVolumeWithPocketPivotsNT8";
                Calculate = Calculate.OnBarClose;
                IsOverlay = false;
                DrawOnPricePanel = false;
                DisplayInDataBox = true;
                PaintPriceMarkers = true;
                ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = true;

                VolumeAverageLength = 50;
                PocketPivotLookback = 10;
                DryVolumeFraction = 0.20;
                UsePreviousCloseForUpDown = true;

                ShowVolumeAverage = false;
                PaintPriceBars = false;

                PocketPivotBrush = MakeBrush(33, 150, 243);
                UpVolumeBrush = MakeBrush(34, 171, 148);
                DownVolumeBrush = MakeBrush(242, 54, 69);
                DryVolumeBrush = MakeBrush(255, 152, 0);
                NoiseBrush = MakeBrush(120, 123, 134);

                AddPlot(new Stroke(MakeBrush(120, 123, 134), 4), PlotStyle.Bar, "VolumeBars");
                AddPlot(new Stroke(MakeBrush(144, 164, 174), 1), PlotStyle.Line, "VolumeAverage");
            }
            else if (State == State.DataLoaded)
            {
                ppvBrush = FreezeClone(PocketPivotBrush);
                upBrush = FreezeClone(UpVolumeBrush);
                downBrush = FreezeClone(DownVolumeBrush);
                dryBrush = FreezeClone(DryVolumeBrush);
                noiseBrush = FreezeClone(NoiseBrush);
                volumeAverageBrush = FreezeClone(MakeBrush(144, 164, 174));

                Plots[0].Width = 4;
                Plots[1].Brush = volumeAverageBrush;
            }
        }

        protected override void OnBarUpdate()
        {
            double currentVolume = Volume[0];
            double avgVolume = AverageVolume(VolumeAverageLength, true);

            bool isUpBar = IsUpBar(0);
            bool isDownBar = IsDownBar(0);
            bool isPocketPivot = IsPocketPivot(isUpBar);
            bool isDryVolume = !double.IsNaN(avgVolume) && avgVolume > 0.0 && currentVolume <= avgVolume * DryVolumeFraction;
            bool isHighUpVolume = isUpBar && !double.IsNaN(avgVolume) && currentVolume > avgVolume;
            bool isHighDownVolume = isDownBar && !double.IsNaN(avgVolume) && currentVolume > avgVolume;

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

            PlotBrushes[0][0] = volumeBrush;
            PlotBrushes[1][0] = volumeAverageBrush;

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
        }

        private bool IsPocketPivot(bool isUpBar)
        {
            if (!isUpBar)
                return false;

            double maxDownVolume = MaxDownVolume(PocketPivotLookback);
            return !double.IsNaN(maxDownVolume) && Volume[0] > maxDownVolume;
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
            int startBarsAgo = includeCurrentBar ? 0 : 1;
            int available = CurrentBar - startBarsAgo + 1;
            if (available <= 0)
                return double.NaN;

            int count = Math.Min(length, available);
            double sum = 0.0;
            for (int i = startBarsAgo; i < startBarsAgo + count; i++)
                sum += Volume[i];

            return count > 0 ? sum / count : double.NaN;
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

        private static Brush FreezeClone(Brush brush)
        {
            if (brush == null)
                return Brushes.Transparent;

            Brush clone = brush.Clone();
            clone.Freeze();
            return clone;
        }

        private static Brush MakeBrush(byte r, byte g, byte b, byte a = 255)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}

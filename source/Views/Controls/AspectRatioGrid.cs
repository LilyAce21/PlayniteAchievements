using System;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// A <see cref="Grid"/> whose height follows its width at <see cref="AspectRatio"/>
    /// (width / height). It sizes itself in the measure pass, so an offscreen render that
    /// measures with an unbounded height gets the same shape in one layout pass. With an
    /// unbounded width it falls back to <see cref="FallbackWidth"/>.
    /// </summary>
    public class AspectRatioGrid : Grid
    {
        public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
            nameof(AspectRatio),
            typeof(double),
            typeof(AspectRatioGrid),
            new FrameworkPropertyMetadata(16d / 9d, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty FallbackWidthProperty = DependencyProperty.Register(
            nameof(FallbackWidth),
            typeof(double),
            typeof(AspectRatioGrid),
            new FrameworkPropertyMetadata(640d, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double AspectRatio
        {
            get => (double)GetValue(AspectRatioProperty);
            set => SetValue(AspectRatioProperty, value);
        }

        public double FallbackWidth
        {
            get => (double)GetValue(FallbackWidthProperty);
            set => SetValue(FallbackWidthProperty, value);
        }

        protected override Size MeasureOverride(Size constraint)
        {
            var size = Resolve(constraint);
            base.MeasureOverride(size);
            return size;
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            return base.ArrangeOverride(Resolve(arrangeSize));
        }

        private Size Resolve(Size constraint)
        {
            var ratio = AspectRatio > 0 && !double.IsInfinity(AspectRatio) ? AspectRatio : 16d / 9d;
            var width = double.IsInfinity(constraint.Width) ? FallbackWidth : constraint.Width;
            var height = width / ratio;
            if (!double.IsInfinity(constraint.Height) && height > constraint.Height && !double.IsInfinity(constraint.Width))
            {
                // Bounded on both sides: the height is the limit, so the width shrinks to keep the shape.
                height = constraint.Height;
                width = height * ratio;
            }

            return new Size(Math.Max(0, width), Math.Max(0, height));
        }
    }
}

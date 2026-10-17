using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Workshop.Preview;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Workshop.Preview
{
    /// <summary>
    /// A previewed showcase page (<see cref="ShowcasePagePreviewModel"/> as the DataContext): its
    /// name, its grid on a 16:9 canvas with each block at its row, column and span and named by
    /// the widget it holds, and thumbnails of the images it bundles.
    /// </summary>
    public partial class ShowcasePreviewControl : UserControl
    {
        public ShowcasePreviewControl()
        {
            InitializeComponent();
            DataContextChanged += (sender, args) => Rebuild();
        }

        private void Rebuild()
        {
            var model = DataContext as ShowcasePagePreviewModel;
            BuildGrid(model);
            ImageList.ItemsSource = model?.Thumbnails ?? Array.Empty<object>();
        }

        private void BuildGrid(ShowcasePagePreviewModel model)
        {
            LayoutGrid.Children.Clear();
            LayoutGrid.RowDefinitions.Clear();
            LayoutGrid.ColumnDefinitions.Clear();
            if (model == null)
            {
                return;
            }

            // Star tracks by the page's weights, as ShowcaseControl.BuildDashboard lays the page out.
            foreach (var weight in Tracks(model.RowWeights))
            {
                LayoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(weight, GridUnitType.Star) });
            }

            foreach (var weight in Tracks(model.ColumnWeights))
            {
                LayoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
            }

            var rows = LayoutGrid.RowDefinitions.Count;
            var columns = LayoutGrid.ColumnDefinitions.Count;
            foreach (var block in model.Blocks)
            {
                var row = Clamp(block.Row, 0, rows - 1);
                var column = Clamp(block.Column, 0, columns - 1);
                var container = CreateBlock(block);
                Grid.SetRow(container, row);
                Grid.SetColumn(container, column);
                Grid.SetRowSpan(container, Clamp(block.RowSpan, 1, rows - row));
                Grid.SetColumnSpan(container, Clamp(block.ColumnSpan, 1, columns - column));
                LayoutGrid.Children.Add(container);
            }
        }

        private static IReadOnlyList<double> Tracks(IReadOnlyList<double> weights)
        {
            return weights != null && weights.Count > 0 ? weights : new[] { 1d };
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        /// <summary>A block surface as ShowcaseControl.CreateBlockContainer draws it, labeled with its widget.</summary>
        private static FrameworkElement CreateBlock(ShowcaseBlockPreview block)
        {
            var border = new Border { Margin = new Thickness(4), Padding = new Thickness(8, 6, 8, 6) };
            border.SetResourceReference(Border.CornerRadiusProperty, "PlayAch.Radius.Section");
            border.SetResourceReference(Border.BackgroundProperty, "PlayAch.Brush.GridSurface");
            if (!block.WidgetKind.HasValue)
            {
                return border;
            }

            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var kind = new TextBlock
            {
                Text = KindLabel(block.WidgetKind.Value),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            label.Children.Add(kind);

            if (!string.IsNullOrWhiteSpace(block.Title))
            {
                var title = new TextBlock
                {
                    Text = block.Title,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                title.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
                title.SetResourceReference(UIElement.OpacityProperty, "PlayAch.Opacity.Muted");
                label.Children.Add(title);
            }

            border.Child = label;
            return border;
        }

        private static string KindLabel(ShowcaseWidgetKind kind)
        {
            var definition = ShowcaseWidgetCatalog.Definitions.FirstOrDefault(candidate => candidate.Kind == kind);
            return definition == null ? kind.ToString() : ResourceProvider.GetString(definition.NameKey);
        }
    }
}

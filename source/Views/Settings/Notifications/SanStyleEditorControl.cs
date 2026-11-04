using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Settings.Notifications
{
    /// <summary>
    /// Editor for one official-SAN notification's style: every setting the SAN customiser has
    /// (<see cref="SanStyleCatalog"/>), grouped, with a tick box per row. An unticked row follows
    /// the .san theme and shows the theme's own value; a ticked row overrides it for this slot only.
    /// Edits go straight into <see cref="OfficialSanSlot.StyleOverrides"/>. The rows are built from
    /// the catalog, so a setting added there appears here without any layout work.
    /// </summary>
    public sealed class SanStyleEditorControl : UserControl
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly OfficialSanSlot _slot;
        private readonly JObject _baseline;
        private readonly Func<string, string> _pickColor;
        private readonly Action<Dictionary<string, string>> _copyToAll;
        private readonly List<Row> _rows = new List<Row>();
        private bool _loading;

        private sealed class Row
        {
            public SanStyleOption Option;
            public CheckBox Override;
            public UIElement Editor;
            public Func<string> Get;
            public Action<string> Set;
        }

        /// <param name="slot">The profile slot being edited.</param>
        /// <param name="baseline">The theme's own customisation values; null when unreadable.</param>
        /// <param name="pickColor">Opens the colour picker with WPF colour text and returns WPF text, or null.</param>
        /// <param name="test">Shows a test notification with the current settings.</param>
        /// <param name="copyToAll">Copies the given overrides to every slot that has a theme.</param>
        public SanStyleEditorControl(
            OfficialSanSlot slot,
            JObject baseline,
            string title,
            Func<string, string> pickColor,
            Action test,
            Action<Dictionary<string, string>> copyToAll)
        {
            _slot = slot ?? throw new ArgumentNullException(nameof(slot));
            _baseline = baseline;
            _pickColor = pickColor;
            _copyToAll = copyToAll;

            var root = new DockPanel { Margin = new Thickness(14) };

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(header, Dock.Top);
            header.Children.Add(Themed(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold }));
            header.Children.Add(Themed(new TextBlock
            {
                Text = "Tick a setting to override it for this notification only. Unticked settings follow the " +
                       ".san theme and show its own value.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 8),
                Opacity = 0.8
            }));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(MakeButton("Test notification", () => test?.Invoke()));
            buttons.Children.Add(MakeButton("Reset all to theme", ResetAll));
            if (copyToAll != null)
            {
                buttons.Children.Add(MakeButton("Copy to all profiles", () => _copyToAll(new Dictionary<string, string>(_slot.StyleOverrides))));
            }

            header.Children.Add(buttons);
            root.Children.Add(header);

            var body = new StackPanel();
            var first = true;
            foreach (var group in SanStyleCatalog.All.GroupBy(o => o.Group))
            {
                var panel = new StackPanel { Margin = new Thickness(8, 4, 0, 8) };
                foreach (var option in group)
                {
                    var row = BuildRow(option);
                    _rows.Add(row);
                    panel.Children.Add(WrapRow(row));
                }

                var expander = new Expander
                {
                    Header = group.Key,
                    IsExpanded = first || group.Any(o => _slot.StyleOverrides.ContainsKey(o.Key)),
                    Content = panel,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                expander.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                body.Children.Add(expander);
                first = false;
            }

            root.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = body
            });

            Content = root;
            LoadAllFromSlot();
        }

        private static T Themed<T>(T element) where T : FrameworkElement
        {
            element.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return element;
        }

        private static Button MakeButton(string text, Action click)
        {
            var button = new Button { Content = text, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, __) => click();
            return button;
        }

        private UIElement WrapRow(Row row)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(row.Override, 0);
            grid.Children.Add(row.Override);

            var label = Themed(new TextBlock
            {
                Text = row.Option.Label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = string.IsNullOrEmpty(row.Option.Tip) ? row.Option.Label : row.Option.Tip
            });
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            Grid.SetColumn(row.Editor, 2);
            grid.Children.Add(row.Editor);
            return grid;
        }

        private Row BuildRow(SanStyleOption option)
        {
            var row = new Row { Option = option };
            row.Override = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Override the theme's value for this setting"
            };

            switch (option.Kind)
            {
                case SanStyleKind.Range:
                    BuildRange(row);
                    break;
                case SanStyleKind.Bool:
                    BuildBool(row);
                    break;
                case SanStyleKind.Select:
                    BuildSelect(row);
                    break;
                default:
                    BuildColor(row);
                    break;
            }

            row.Override.Checked += (_, __) =>
            {
                row.Editor.IsEnabled = true;
                Commit(row);
            };
            row.Override.Unchecked += (_, __) =>
            {
                row.Editor.IsEnabled = false;
                Commit(row);
                if (!_loading)
                {
                    LoadRow(row);
                }
            };
            return row;
        }

        private void BuildRange(Row row)
        {
            var option = row.Option;
            var slider = new Slider
            {
                Minimum = option.Min,
                Maximum = option.Max,
                SmallChange = option.Step,
                LargeChange = option.Step * 5,
                TickFrequency = option.Step,
                IsSnapToTickEnabled = true,
                Width = 220,
                VerticalAlignment = VerticalAlignment.Center
            };
            var value = Themed(new TextBlock { Width = 74, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(slider);
            panel.Children.Add(value);

            void Refresh() => value.Text = slider.Value.ToString("0.##", Inv) + option.Unit;
            slider.ValueChanged += (_, __) =>
            {
                Refresh();
                if (!_loading && row.Override.IsChecked == true)
                {
                    Commit(row);
                }
            };

            row.Editor = panel;
            row.Get = () => slider.Value.ToString("0.####", Inv);
            row.Set = text =>
            {
                slider.Value = double.TryParse(text, NumberStyles.Float, Inv, out var number)
                    ? Math.Max(option.Min, Math.Min(option.Max, number))
                    : option.Min;
                Refresh();
            };
        }

        private void BuildBool(Row row)
        {
            var box = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Content = "On" };
            box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            RoutedEventHandler changed = (_, __) =>
            {
                if (!_loading && row.Override.IsChecked == true)
                {
                    Commit(row);
                }
            };
            box.Checked += changed;
            box.Unchecked += changed;

            row.Editor = box;
            row.Get = () => box.IsChecked == true ? "true" : "false";
            row.Set = text => box.IsChecked = bool.TryParse(text, out var flag) && flag;
        }

        private void BuildSelect(Row row)
        {
            var option = row.Option;
            var combo = new ComboBox { ItemsSource = option.Options, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
            combo.SelectionChanged += (_, __) =>
            {
                if (!_loading && row.Override.IsChecked == true)
                {
                    Commit(row);
                }
            };

            row.Editor = combo;
            row.Get = () => combo.SelectedItem as string ?? option.Default;
            row.Set = text => combo.SelectedItem = option.Options.Contains(text) ? text : option.Default;
        }

        private void BuildColor(Row row)
        {
            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 8, 0)
            };
            var hex = new TextBox { Width = 110, VerticalAlignment = VerticalAlignment.Center };
            var pick = new Button { Content = "Pick...", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(swatch);
            panel.Children.Add(hex);
            panel.Children.Add(pick);

            hex.TextChanged += (_, __) =>
            {
                // A half-typed value leaves the swatch and the stored value alone.
                if (!SanColor.TryParse(hex.Text, out var color))
                {
                    return;
                }

                swatch.Background = new SolidColorBrush(color);
                if (!_loading && row.Override.IsChecked == true)
                {
                    Commit(row);
                }
            };
            pick.Click += (_, __) =>
            {
                var picked = _pickColor?.Invoke(SanColor.ToWpf(hex.Text));
                if (!string.IsNullOrWhiteSpace(picked))
                {
                    hex.Text = SanColor.FromWpf(picked);
                }
            };

            row.Editor = panel;
            row.Get = () => hex.Text.Trim();
            row.Set = text => hex.Text = text ?? string.Empty;
        }

        /// <summary>The theme's own value for the row as text (the catalog default when it has none).</summary>
        private string BaselineText(SanStyleOption option)
        {
            if (!option.IsPluginExtra && _baseline != null && _baseline.TryGetValue(option.Key, out var token) && token != null)
            {
                switch (token.Type)
                {
                    case JTokenType.Boolean:
                        return token.Value<bool>() ? "true" : "false";
                    case JTokenType.Integer:
                    case JTokenType.Float:
                        return token.Value<double>().ToString("0.####", Inv);
                    case JTokenType.String:
                        var text = token.Value<string>();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }

                        break;
                }
            }

            return option.Default;
        }

        private void LoadRow(Row row)
        {
            var wasLoading = _loading;
            _loading = true;
            try
            {
                if (_slot.StyleOverrides.TryGetValue(row.Option.Key, out var overridden))
                {
                    row.Set(overridden);
                    row.Override.IsChecked = true;
                    row.Editor.IsEnabled = true;
                }
                else
                {
                    row.Set(BaselineText(row.Option));
                    row.Override.IsChecked = false;
                    row.Editor.IsEnabled = false;
                }
            }
            finally
            {
                _loading = wasLoading;
            }
        }

        private void LoadAllFromSlot()
        {
            foreach (var row in _rows)
            {
                LoadRow(row);
            }
        }

        private void Commit(Row row)
        {
            if (_loading)
            {
                return;
            }

            // A new dictionary each time, so the slot raises its change notification.
            var map = new Dictionary<string, string>(_slot.StyleOverrides ?? new Dictionary<string, string>());
            if (row.Override.IsChecked == true)
            {
                var value = row.Get();
                if (!string.IsNullOrEmpty(value))
                {
                    map[row.Option.Key] = value;
                }
            }
            else
            {
                map.Remove(row.Option.Key);
            }

            _slot.StyleOverrides = map;
        }

        private void ResetAll()
        {
            _slot.StyleOverrides = new Dictionary<string, string>();
            LoadAllFromSlot();
        }
    }
}

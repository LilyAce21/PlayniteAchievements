using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Formatting shortcuts for a plain TextBox holding the inline markdown that
    /// <see cref="InlineMarkdownFormatter"/> renders: Ctrl+B bold, Ctrl+I italic, Ctrl+U underline,
    /// Ctrl+Shift+X strikethrough and Ctrl+K link. Each emphasis shortcut toggles: it removes the
    /// markers when the selection is already wrapped in them, and wraps it otherwise.
    /// </summary>
    internal static class MarkdownEditing
    {
        private const string LinkPlaceholder = "https://";

        public static readonly DependencyProperty ShortcutsProperty =
            DependencyProperty.RegisterAttached(
                "Shortcuts",
                typeof(bool),
                typeof(MarkdownEditing),
                new PropertyMetadata(false, OnShortcutsChanged));

        public static bool GetShortcuts(DependencyObject element) => (bool)element.GetValue(ShortcutsProperty);

        public static void SetShortcuts(DependencyObject element, bool value) => element.SetValue(ShortcutsProperty, value);

        private static void OnShortcutsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is TextBox box))
            {
                return;
            }

            box.PreviewKeyDown -= Box_PreviewKeyDown;
            if (e.NewValue is true)
            {
                box.PreviewKeyDown += Box_PreviewKeyDown;
            }
        }

        private static void Box_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox box && HandleShortcut(box, e, box.MaxLength > 0 ? box.MaxLength : int.MaxValue))
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// The formatting buttons shown above a markdown text box: bold, italic, underline,
        /// strikethrough and link, each with its shortcut in the tooltip. The note editor and the
        /// showcase Text widget share this one toolbar.
        /// </summary>
        public static Panel CreateToolbar(TextBox box)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.SetResourceReference(FrameworkElement.MarginProperty, "PlayAch.Thickness.Bottom.Sm");
            int Max() => box.MaxLength > 0 ? box.MaxLength : int.MaxValue;

            var bold = new TextBlock { Text = "B", FontWeight = FontWeights.Bold };
            var italic = new TextBlock { Text = "I", FontStyle = FontStyles.Italic };
            var underline = new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline };
            var strike = new TextBlock { Text = "S", TextDecorations = TextDecorations.Strikethrough };
            var link = new TextBlock { Text = "" };
            link.SetResourceReference(TextBlock.FontFamilyProperty, "PlayAch.FontFamily.Icon");
            TextOptions.SetTextFormattingMode(link, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(link, TextRenderingMode.Grayscale);

            AddButton(panel, bold, "LOCPlayAch_NotesDialog_Bold", "Ctrl+B", () => ToggleMarker(box, "**", Max()));
            AddButton(panel, italic, "LOCPlayAch_NotesDialog_Italic", "Ctrl+I", () => ToggleMarker(box, "*", Max()));
            AddButton(panel, underline, "LOCPlayAch_NotesDialog_Underline", "Ctrl+U", () => ToggleMarker(box, "__", Max()));
            AddButton(panel, strike, "LOCPlayAch_NotesDialog_Strikethrough", "Ctrl+Shift+X", () => ToggleMarker(box, "~~", Max()));
            AddButton(panel, link, "LOCPlayAch_NotesDialog_Link", "Ctrl+K", () => InsertLink(box, Max()));
            return panel;
        }

        private static void AddButton(Panel panel, UIElement content, string toolTipKey, string shortcut, System.Action apply)
        {
            var button = new Button
            {
                Content = content,
                Width = 34,
                Padding = new Thickness(0),
                Focusable = false,
                ToolTip = Playnite.SDK.ResourceProvider.GetString(toolTipKey) + " (" + shortcut + ")"
            };
            button.SetResourceReference(FrameworkElement.HeightProperty, "PlayAch.Size.Control.Height");
            button.SetResourceReference(FrameworkElement.MarginProperty, "PlayAch.Thickness.Right.Compact");

            button.Click += (_, e) =>
            {
                apply();
                e.Handled = true;
            };
            panel.Children.Add(button);
        }

        /// <summary>Applies the formatting a shortcut names; false when the key is not one.</summary>
        public static bool HandleShortcut(TextBox box, KeyEventArgs e, int maxLength = int.MaxValue)
        {
            if (box == null || e == null || box.IsReadOnly)
            {
                return false;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            if (modifiers == ModifierKeys.Control)
            {
                switch (key)
                {
                    case Key.B:
                        ToggleMarker(box, "**", maxLength);
                        return true;
                    case Key.I:
                        ToggleMarker(box, "*", maxLength);
                        return true;
                    case Key.U:
                        ToggleMarker(box, "__", maxLength);
                        return true;
                    case Key.K:
                        InsertLink(box, maxLength);
                        return true;
                }
            }
            else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.X)
            {
                ToggleMarker(box, "~~", maxLength);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Removes <paramref name="marker"/> from around (or just inside) the selection when it is
        /// there, and wraps the selection in it otherwise. A single '*' counts as present only on
        /// an odd run of stars, so italic never eats half of a bold marker.
        /// </summary>
        public static void ToggleMarker(TextBox box, string marker, int maxLength = int.MaxValue)
        {
            if (box == null || string.IsNullOrEmpty(marker))
            {
                return;
            }

            var text = box.Text ?? string.Empty;
            var start = box.SelectionStart;
            var length = box.SelectionLength;
            var end = start + length;
            var c = marker[0];

            // Markers just outside the selection.
            if (Has(Run(text, start - 1, -1, c), marker.Length) &&
                Has(Run(text, end, 1, c), marker.Length))
            {
                // Through the selection rather than Text, so the edit stays undoable.
                box.Select(start - marker.Length, length + marker.Length * 2);
                box.SelectedText = text.Substring(start, length);
                box.Select(start - marker.Length, length);
                box.Focus();
                return;
            }

            // Markers at the ends of the selection itself.
            if (length >= marker.Length * 2 &&
                Has(Run(text, start, 1, c, end), marker.Length) &&
                Has(Run(text, end - 1, -1, c, start), marker.Length))
            {
                var inner = text.Substring(start + marker.Length, length - marker.Length * 2);
                box.SelectedText = inner;
                box.Select(start, inner.Length);
                box.Focus();
                return;
            }

            var selected = text.Substring(start, length);
            if (text.Length + marker.Length * 2 > maxLength)
            {
                return;
            }

            box.SelectedText = marker + selected + marker;
            box.Select(start + marker.Length, selected.Length);
            box.Focus();
        }

        /// <summary>
        /// Turns the selection into a link label, <c>[label](https://)</c>, and selects the address
        /// so typing replaces it; with nothing selected the caret lands in the empty label.
        /// </summary>
        public static void InsertLink(TextBox box, int maxLength = int.MaxValue)
        {
            if (box == null)
            {
                return;
            }

            var start = box.SelectionStart;
            var label = box.SelectedText ?? string.Empty;
            var replacement = "[" + label + "](" + LinkPlaceholder + ")";
            if ((box.Text ?? string.Empty).Length - label.Length + replacement.Length > maxLength)
            {
                return;
            }

            box.SelectedText = replacement;
            if (label.Length == 0)
            {
                box.Select(start + 1, 0);
            }
            else
            {
                box.Select(start + label.Length + 3, LinkPlaceholder.Length);
            }

            box.Focus();
        }

        // A two-character marker needs a run of at least two; a single one an odd run.
        private static bool Has(int run, int markerLength) =>
            markerLength == 1 ? run % 2 == 1 : run >= markerLength;

        // Length of the run of c starting at index and walking in direction, stopping at limit.
        private static int Run(string text, int index, int direction, char c, int? limit = null)
        {
            var count = 0;
            var lower = direction < 0 ? limit ?? 0 : 0;
            var upper = direction > 0 ? limit ?? text.Length : text.Length;
            for (var i = index; i >= lower && i < upper && text[i] == c; i += direction)
            {
                count++;
            }

            return count;
        }
    }
}

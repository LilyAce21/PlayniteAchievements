using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PlayniteAchievements.Services.Sound;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>What a <see cref="FilePickTarget"/> accepts.</summary>
    public enum FilePickKind
    {
        /// <summary>A decodable image file or an http(s) URL; see <see cref="ImageDropHelper.TryGetImageSource"/>.</summary>
        Image,

        /// <summary>A local file in a supported unlock sound format.</summary>
        Sound
    }

    /// <summary>Carries the file or URL a pick target received.</summary>
    public sealed class FilePickedEventArgs : RoutedEventArgs
    {
        public FilePickedEventArgs(RoutedEvent routedEvent, object source, string pickedSource)
            : base(routedEvent, source)
        {
            PickedSource = pickedSource;
        }

        /// <summary>A local file path, or for <see cref="FilePickKind.Image"/> an http(s) URL.</summary>
        public string PickedSource { get; }
    }

    /// <summary>
    /// Turns a slot's preview into its input: it takes keyboard focus on click, accepts a dropped
    /// file (or browser image), and takes Ctrl+V or Shift+Insert from the clipboard, as does a
    /// button whose <see cref="PasteTargetProperty"/> names the slot. Every
    /// accepted input raises <see cref="PickedEvent"/> with the same source, so a slot handles drop
    /// and paste in one place. <see cref="KindProperty"/> decides what is accepted.
    /// </summary>
    public static class FilePickTarget
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(FilePickTarget),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static readonly DependencyProperty KindProperty =
            DependencyProperty.RegisterAttached(
                "Kind",
                typeof(FilePickKind),
                typeof(FilePickTarget),
                new PropertyMetadata(FilePickKind.Image));

        /// <summary>
        /// Set on a button to make it a visible Paste for a pick target: a click pastes the
        /// clipboard into the target as Ctrl+V on the target would.
        /// </summary>
        public static readonly DependencyProperty PasteTargetProperty =
            DependencyProperty.RegisterAttached(
                "PasteTarget",
                typeof(UIElement),
                typeof(FilePickTarget),
                new PropertyMetadata(null, OnPasteTargetChanged));

        public static readonly RoutedEvent PickedEvent =
            EventManager.RegisterRoutedEvent(
                "Picked",
                RoutingStrategy.Bubble,
                typeof(EventHandler<FilePickedEventArgs>),
                typeof(FilePickTarget));

        public static bool GetIsEnabled(DependencyObject element) =>
            (bool)element.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject element, bool value) =>
            element.SetValue(IsEnabledProperty, value);

        public static FilePickKind GetKind(DependencyObject element) =>
            (FilePickKind)element.GetValue(KindProperty);

        public static void SetKind(DependencyObject element, FilePickKind value) =>
            element.SetValue(KindProperty, value);

        public static UIElement GetPasteTarget(DependencyObject element) =>
            (UIElement)element.GetValue(PasteTargetProperty);

        public static void SetPasteTarget(DependencyObject element, UIElement value) =>
            element.SetValue(PasteTargetProperty, value);

        public static void AddPickedHandler(DependencyObject element, EventHandler<FilePickedEventArgs> handler) =>
            (element as UIElement)?.AddHandler(PickedEvent, handler);

        public static void RemovePickedHandler(DependencyObject element, EventHandler<FilePickedEventArgs> handler) =>
            (element as UIElement)?.RemoveHandler(PickedEvent, handler);

        /// <summary>The first sound file a drop or paste payload carries: a dropped file, or text naming one.</summary>
        public static bool TryGetSoundFile(IDataObject data, out string path)
        {
            path = null;
            if (data == null)
            {
                return false;
            }

            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    path = (data.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault(IsSoundFile);
                    return path != null;
                }

                var text = (data.GetData(DataFormats.UnicodeText) as string ?? string.Empty).Trim().Trim('"');
                if (IsSoundFile(text))
                {
                    path = text;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool IsSoundFile(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) &&
                       File.Exists(path) &&
                       UnlockSoundPortableStore.IsSupportedExtension(Path.GetExtension(path));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// A local image file a paste payload carries, for stores that take files only: a pasted
        /// image file or text naming one, then raw image data saved into <paramref name="directory"/>.
        /// URLs are skipped in favor of the image data a browser copies beside them.
        /// </summary>
        public static bool TryGetLocalImageFile(IDataObject data, string directory, out string path)
        {
            if (ImageDropHelper.TryGetImageSource(data, out path) && File.Exists(path))
            {
                return true;
            }

            return ClipboardImageFile.TryWrite(data, directory, out path);
        }

        /// <summary>Reads the clipboard through <see cref="TryGetLocalImageFile"/>.</summary>
        public static bool TryPasteLocalImageFile(out string path)
        {
            path = null;
            var clipboard = TryGetClipboard();
            return clipboard != null &&
                   TryGetLocalImageFile(clipboard, ClipboardImageFile.DefaultDirectory, out path);
        }

        /// <summary>
        /// Pastes the clipboard into a pick target: raises <see cref="PickedEvent"/> on it exactly as
        /// Ctrl+V does, so the target's handler takes the result. Does nothing when the clipboard
        /// holds nothing the target accepts.
        /// </summary>
        public static void PasteInto(UIElement target)
        {
            var clipboard = target == null ? null : TryGetClipboard();
            if (clipboard != null && TryGetPasteSource(target, clipboard, out var source))
            {
                Raise(target, source);
            }
        }

        private static IDataObject TryGetClipboard()
        {
            try
            {
                return Clipboard.GetDataObject();
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetSource(DependencyObject element, IDataObject data, out string source)
        {
            return GetKind(element) == FilePickKind.Sound
                ? TryGetSoundFile(data, out source)
                : ImageDropHelper.TryGetImageSource(data, out source);
        }

        // Paste also takes raw image data. Drag-over polls the payload repeatedly, so it stays on
        // TryGetSource and never writes a file.
        private static bool TryGetPasteSource(DependencyObject element, IDataObject data, out string source)
        {
            return TryGetSource(element, data, out source) ||
                   (GetKind(element) == FilePickKind.Image &&
                    ClipboardImageFile.TryWrite(data, ClipboardImageFile.DefaultDirectory, out source));
        }

        private static void OnPasteTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ButtonBase button)
            {
                button.Click -= OnPasteButtonClick;
                if (e.NewValue != null)
                {
                    button.Click += OnPasteButtonClick;
                }
            }
        }

        private static void OnPasteButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is DependencyObject button)
            {
                PasteInto(GetPasteTarget(button));
            }
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is UIElement element))
            {
                return;
            }

            element.PreviewDragOver -= OnPreviewDragOver;
            element.PreviewDrop -= OnPreviewDrop;
            element.PreviewKeyDown -= OnPreviewKeyDown;
            element.MouseLeftButtonDown -= OnMouseLeftButtonDown;

            var enabled = (bool)e.NewValue;
            element.Focusable = enabled;
            element.AllowDrop = enabled;
            if (!enabled)
            {
                return;
            }

            element.PreviewDragOver += OnPreviewDragOver;
            element.PreviewDrop += OnPreviewDrop;
            element.PreviewKeyDown += OnPreviewKeyDown;
            element.MouseLeftButtonDown += OnMouseLeftButtonDown;
        }

        private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is UIElement element && element.Focus())
            {
                e.Handled = true;
            }
        }

        private static void OnPreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = TryGetSource(sender as DependencyObject, e.Data, out _)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private static void OnPreviewDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (TryGetSource(sender as DependencyObject, e.Data, out var source))
            {
                Raise(sender as UIElement, source);
            }
        }

        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var modifiers = Keyboard.Modifiers;
            var isPaste = (e.Key == Key.V && modifiers == ModifierKeys.Control) ||
                          (e.Key == Key.Insert && modifiers == ModifierKeys.Shift);
            if (!isPaste)
            {
                return;
            }

            e.Handled = true;
            PasteInto(sender as UIElement);
        }

        private static void Raise(UIElement element, string source)
        {
            element?.RaiseEvent(new FilePickedEventArgs(PickedEvent, element, source));
        }
    }
}

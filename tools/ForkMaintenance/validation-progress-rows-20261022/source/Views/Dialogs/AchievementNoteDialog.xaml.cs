using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Dialogs
{
    public partial class AchievementNoteDialog : UserControl
    {
        public static readonly DependencyProperty NoteTextProperty =
            DependencyProperty.Register(
                nameof(NoteText),
                typeof(string),
                typeof(AchievementNoteDialog),
                new PropertyMetadata(string.Empty, OnNoteTextChanged));

        private readonly bool _isReadOnly;

        public AchievementNoteDialog()
            : this(string.Empty, string.Empty, string.Empty, isReadOnly: false, achievementIconSource: null)
        {
        }

        public AchievementNoteDialog(
            string achievementTitle,
            string achievementApiName,
            string noteText,
            bool isReadOnly,
            string achievementIconSource = null)
        {
            _isReadOnly = isReadOnly;
            InitializeComponent();
            DataContext = this;

            AchievementTitleTextBlock.Text = achievementTitle ?? string.Empty;
            AchievementApiNameTextBlock.Text = achievementApiName ?? string.Empty;
            ApplyAchievementIcon(achievementIconSource);
            NoteTextBox.MaxLength = AchievementNoteHelper.MaxNoteLength;
            FormatToolbarHost.Content = MarkdownEditing.CreateToolbar(NoteTextBox);
            NoteText = noteText ?? string.Empty;

            Loaded += AchievementNoteDialog_Loaded;
            UpdateModeUi();
            UpdatePreview();
        }

        public event EventHandler RequestClose;

        public string NoteText
        {
            get => (string)GetValue(NoteTextProperty);
            set => SetValue(NoteTextProperty, value ?? string.Empty);
        }

        public string SavedNote => AchievementNoteHelper.NormalizeNote(NoteText);

        public bool? DialogResult { get; private set; }

        private void ApplyAchievementIcon(string achievementIconSource)
        {
            if (AchievementIconBorder == null || AchievementIconImage == null)
            {
                return;
            }

            AchievementIconBorder.Visibility = string.IsNullOrWhiteSpace(achievementIconSource)
                ? Visibility.Collapsed
                : Visibility.Visible;
            AsyncImage.SetUri(AchievementIconImage, achievementIconSource);
        }

        private void AchievementNoteDialog_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_isReadOnly)
            {
                NoteTextBox?.Focus();
                Keyboard.Focus(NoteTextBox);
                NoteTextBox.CaretIndex = NoteTextBox.Text?.Length ?? 0;
            }
        }

        private static void OnNoteTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementNoteDialog dialog)
            {
                dialog.UpdatePreview();
            }
        }

        private void UpdateModeUi()
        {
            if (EditorPanel == null)
            {
                return;
            }

            EditorPanel.Visibility = _isReadOnly ? Visibility.Collapsed : Visibility.Visible;
            ViewScrollViewer.Visibility = _isReadOnly ? Visibility.Visible : Visibility.Collapsed;
            SaveButton.Visibility = _isReadOnly ? Visibility.Collapsed : Visibility.Visible;
            ClearButton.Visibility = _isReadOnly ? Visibility.Collapsed : Visibility.Visible;
            CancelButton.Visibility = _isReadOnly ? Visibility.Collapsed : Visibility.Visible;
            CloseButton.Visibility = _isReadOnly ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdatePreview()
        {
            if (CharacterCountTextBlock != null)
            {
                var format = L("LOCPlayAch_NotesDialog_CharacterCountFormat");
                CharacterCountTextBlock.Text = string.Format(
                    format,
                    (NoteText ?? string.Empty).Length.ToString("N0", FormattingCulture.Current),
                    AchievementNoteHelper.MaxNoteLength.ToString("N0", FormattingCulture.Current));
            }

            if (ClearButton != null)
            {
                ClearButton.IsEnabled = !string.IsNullOrWhiteSpace(NoteText);
            }

            InlineMarkdownFormatter.ApplyFormattedText(PreviewTextBlock, NoteText);
            InlineMarkdownFormatter.ApplyFormattedText(ViewTextBlock, NoteText);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            NoteText = string.Empty;
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void NoteTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                RequestClose?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}

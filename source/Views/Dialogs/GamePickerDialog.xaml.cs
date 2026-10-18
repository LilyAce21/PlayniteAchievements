using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// Picks one library game from a list, or several with checkboxes, filtered as the user
    /// types. Hosted in a Playnite extension window by <see cref="Helpers.PlayniteGamePickerDialog"/>,
    /// so it draws with the plugin's themed controls and brushes rather than WPF defaults.
    /// </summary>
    public partial class GamePickerDialog : UserControl
    {
        private readonly ICollectionView _view;
        private readonly List<PickerItem> _items;
        private readonly bool _multiple;

        public GamePickerDialog()
        {
            InitializeComponent();
        }

        /// <summary>Picks one game.</summary>
        public GamePickerDialog(IEnumerable<Game> games, string initialSearch)
            : this(games, initialSearch, multiple: false, checkedIds: null, lockedIds: null)
        {
        }

        /// <summary>
        /// Picks several games. <paramref name="checkedIds"/> start checked; <paramref name="lockedIds"/>
        /// are checked and cannot be unchecked. The games keep the order given.
        /// </summary>
        public GamePickerDialog(IEnumerable<Game> games, string initialSearch, ISet<Guid> checkedIds, ISet<Guid> lockedIds)
            : this(games, initialSearch, multiple: true, checkedIds, lockedIds)
        {
        }

        private GamePickerDialog(IEnumerable<Game> games, string initialSearch, bool multiple, ISet<Guid> checkedIds, ISet<Guid> lockedIds)
            : this()
        {
            _multiple = multiple;
            var valid = (games ?? Enumerable.Empty<Game>()).Where(game => game != null && game.Id != Guid.Empty);
            if (!multiple)
            {
                valid = valid.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase);
            }

            _items = valid
                .GroupBy(game => game.Id)
                .Select(group => group.First())
                .Select(game =>
                {
                    var locked = lockedIds?.Contains(game.Id) == true;
                    return new PickerItem(game, multiple, locked || checkedIds?.Contains(game.Id) == true, locked);
                })
                .ToList();

            _view = CollectionViewSource.GetDefaultView(new ObservableCollection<PickerItem>(_items));
            _view.Filter = FilterGame;
            GamesList.ItemsSource = _view;
            SearchBox.Text = initialSearch ?? string.Empty;
            SelectFirst();

            Loaded += (sender, args) => Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    // Keyboard focus is taken back when the host window shows; asking again at
                    // input priority runs after that.
                    SearchBox.Focus();
                    Keyboard.Focus(SearchBox);
                    SearchBox.SelectAll();
                }),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        public Game SelectedGame { get; private set; }

        /// <summary>For a multiple pick, every checked game, locked ones included, in list order.</summary>
        public IReadOnlyList<Game> CheckedGames { get; private set; } = Array.Empty<Game>();

        public bool? DialogResult { get; private set; }

        public event EventHandler RequestClose;

        private bool FilterGame(object value)
        {
            if (!(value is PickerItem item))
            {
                return false;
            }

            var query = SearchBox?.Text;
            return string.IsNullOrWhiteSpace(query) ||
                   item.SearchText.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SelectFirst()
        {
            if (GamesList.SelectedItem == null && GamesList.Items.Count > 0)
            {
                GamesList.SelectedIndex = 0;
            }
        }

        private void Accept()
        {
            if (_multiple)
            {
                CheckedGames = _items.Where(item => item.IsChecked).Select(item => item.Game).ToList();
            }
            else
            {
                if (!(GamesList.SelectedItem is PickerItem item))
                {
                    return;
                }

                SelectedGame = item.Game;
            }

            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void Cancel()
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void ToggleSelected()
        {
            if (GamesList.SelectedItem is PickerItem item && item.CanToggle)
            {
                item.IsChecked = !item.IsChecked;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _view?.Refresh();
            SelectFirst();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down when GamesList.Items.Count > 0:
                    GamesList.SelectedIndex = Math.Min(GamesList.SelectedIndex + 1, GamesList.Items.Count - 1);
                    GamesList.ScrollIntoView(GamesList.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Up when GamesList.Items.Count > 0:
                    GamesList.SelectedIndex = Math.Max(GamesList.SelectedIndex - 1, 0);
                    GamesList.ScrollIntoView(GamesList.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Space when _multiple && Keyboard.Modifiers == ModifierKeys.Control:
                    ToggleSelected();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Cancel();
                    e.Handled = true;
                    break;
            }
        }

        private void GamesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_multiple)
            {
                ToggleSelected();
                return;
            }

            Accept();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Accept();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Cancel();
        }

        private sealed class PickerItem : INotifyPropertyChanged
        {
            private bool _isChecked;

            public PickerItem(Game game, bool showCheck, bool isChecked, bool isLocked)
            {
                Game = game;
                ShowCheck = showCheck;
                _isChecked = isChecked;
                IsLocked = isLocked;
                Name = game.Name ?? string.Empty;
                var source = game.Source?.Name;
                var platforms = string.Join(
                    ", ",
                    (game.Platforms ?? Enumerable.Empty<Platform>())
                        .Where(platform => !string.IsNullOrWhiteSpace(platform?.Name))
                        .Select(platform => platform.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                Metadata = string.Join(
                    " | ",
                    new[] { source, platforms }.Where(value => !string.IsNullOrWhiteSpace(value)));
                SearchText = string.Join(" ", Name, source, platforms);
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public Game Game { get; }

            public string Name { get; }

            public string Metadata { get; }

            public bool HasMetadata => Metadata.Length > 0;

            public string SearchText { get; }

            /// <summary>True in a multiple pick, where each row has a checkbox.</summary>
            public bool ShowCheck { get; }

            /// <summary>True for a game that stays checked, such as one that already has what is being applied.</summary>
            public bool IsLocked { get; }

            public bool CanToggle => ShowCheck && !IsLocked;

            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked == value || IsLocked)
                    {
                        return;
                    }

                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }
    }
}

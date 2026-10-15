using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.Windows;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Shows the themed <see cref="GamePickerDialog"/> in a Playnite extension window and returns
    /// the chosen game or games, or null when the user cancels.
    /// </summary>
    internal static class PlayniteGamePickerDialog
    {
        public static Game Pick(
            Window owner,
            IEnumerable<Game> games,
            string title,
            string initialSearch)
        {
            var dialog = new GamePickerDialog(games, initialSearch);
            Show(owner, dialog, title);
            return dialog.DialogResult == true ? dialog.SelectedGame : null;
        }

        /// <summary>
        /// Picks several games with checkboxes, in the order given. <paramref name="lockedIds"/>
        /// stay checked; the result includes them.
        /// </summary>
        public static IReadOnlyList<Game> PickMany(
            Window owner,
            IEnumerable<Game> games,
            string title,
            ISet<Guid> checkedIds,
            ISet<Guid> lockedIds)
        {
            var dialog = new GamePickerDialog(games, string.Empty, checkedIds, lockedIds);
            Show(owner, dialog, title);
            return dialog.DialogResult == true ? dialog.CheckedGames : null;
        }

        private static void Show(Window owner, GamePickerDialog dialog, string title)
        {
            var window = PlayniteUiProvider.CreateExtensionWindow(
                string.IsNullOrWhiteSpace(title)
                    ? ResourceProvider.GetString("LOCPlayAch_Menu_MapToPlayniteGame")
                    : title,
                dialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 680,
                    Height = 520
                });

            WindowPlacementPersistenceService.Attach(window, "GamePicker");

            try
            {
                window.Owner = owner ?? API.Instance?.Dialogs?.GetCurrentAppWindow();
            }
            catch
            {
            }

            dialog.RequestClose += (sender, args) => window.Close();
            window.ShowDialog();
        }
    }
}

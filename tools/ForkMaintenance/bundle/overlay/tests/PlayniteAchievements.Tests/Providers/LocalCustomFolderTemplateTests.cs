using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using System;
using System.Linq;
using System.Collections.ObjectModel;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class LocalCustomFolderTemplateTests
    {
        [DataTestMethod]
        [DataRow(@"{InstallDir}\steam_settings", @"E:\Games\My Game\steam_settings")]
        [DataRow(@"C:\Saves\{GameDirName}", @"C:\Saves\My Game")]
        [DataRow(@"{PlayniteDir}\Saves\{GameDirName}", @"E:\Playnite\Saves\My Game")]
        [DataRow(@"{EmulatorDir}\Saves\{GameDirName}", @"E:\Emulators\Example\Saves\My Game")]
        [DataRow(@"{installdir}\{GAMEDIRNAME}", @"E:\Games\My Game\My Game")]
        public void ExpandsPlayniteFolderVariables(string template, string expected)
        {
            Assert.AreEqual(expected, PathExpansion.ExpandFolderTemplate(
                template, @"E:\Games\My Game\", @"E:\Playnite", @"E:\Emulators\Example"));
        }

        [TestMethod]
        public void MissingContextSkipsPathInsteadOfScanningPartialPath()
        {
            Assert.IsNull(PathExpansion.ExpandFolderTemplate(@"{EmulatorDir}\Saves", @"E:\Games\Game", @"E:\Playnite", null));
            Assert.IsNull(PathExpansion.ExpandFolderTemplate(@"{InstallDir}\Saves", null, @"E:\Playnite", null));
            Assert.IsNull(PathExpansion.ExpandFolderTemplate(@"C:\Saves\{GameDirName}", null, null, null));
            Assert.IsNull(PathExpansion.ExpandFolderTemplate(@"{Unknown}\Saves", null, null, null));
        }

        [TestMethod]
        public void ExpandsEnvironmentVariablesAlongsideGameVariables()
        {
            Assert.AreEqual(Environment.ExpandEnvironmentVariables(@"%APPDATA%\Saves\Game"),
                PathExpansion.ExpandFolderTemplate(@"%APPDATA%\Saves\{GameDirName}", @"E:\Games\Game", null, null));
        }

        [TestMethod]
        public void ResolvesSameTemplateSeparatelyForEachGame()
        {
            var first = new Game { InstallDirectory = @"E:\Games\First" };
            var second = new Game { InstallDirectory = @"F:\Games\Second" };
            Assert.AreEqual(@"E:\Games\First\steam_settings", PathExpansion.ExpandCustomFolderPaths(null, first, @"{InstallDir}\steam_settings").Single());
            Assert.AreEqual(@"F:\Games\Second\steam_settings", PathExpansion.ExpandCustomFolderPaths(null, second, @"{InstallDir}\steam_settings").Single());
            Assert.IsFalse(PathExpansion.ExpandCustomFolderPaths(null, first, @"{EmulatorDir}\Saves").Any());
        }

        [TestMethod]
        public void ExpandsGameNameAndUniqueIdWithoutAnInstallDirectory()
        {
            var game = new Game { Name = "Game: One?", Id = Guid.NewGuid() };
            Assert.AreEqual(@"C:\Saves\Game_ One_\" + game.Id,
                PathExpansion.ExpandCustomFolderPaths(null, game, @"C:\Saves\{GameName}\{GameId}").Single());
        }

        [TestMethod]
        public void SteamAppIdUsesProvidedResolverRatherThanUnrelatedLibraryId()
        {
            var game = new Game { GameId = "123" };
            Assert.AreEqual(@"C:\Saves\456", PathExpansion.ExpandCustomFolderPaths(
                null, game, @"C:\Saves\{SteamAppId}", _ => "456").Single());
            Assert.IsFalse(PathExpansion.ExpandCustomFolderPaths(
                null, game, @"C:\Saves\{SteamAppId}", _ => null).Any());
        }

        [TestMethod]
        public void ExecutableDirectoryUsesPlayActionAndWorkingDirectory()
        {
            var game = new Game
            {
                InstallDirectory = @"E:\Games\Example",
                GameActions = new ObservableCollection<GameAction>
                {
                    new GameAction { Type = GameActionType.File, IsPlayAction = false, Path = @"C:\Tools\config.exe" },
                    new GameAction { Type = GameActionType.File, IsPlayAction = true, Path = @"Bin\game.exe", WorkingDir = @"{InstallDir}\Runtime" }
                }
            };
            Assert.AreEqual(@"E:\Games\Example\Runtime\Bin\steam_settings",
                PathExpansion.ExpandCustomFolderPaths(null, game, @"{ExecutableDir}\steam_settings").Single());
        }

        [TestMethod]
        public void RomDirectoryUsesFirstConfiguredRom()
        {
            var game = new Game
            {
                InstallDirectory = @"E:\Games\Example",
                Roms = new ObservableCollection<GameRom>
                {
                    new GameRom { Path = @"Roms\first.iso" },
                    new GameRom { Path = @"Other\second.iso" }
                }
            };
            Assert.AreEqual(@"E:\Games\Example\Roms\Saves",
                PathExpansion.ExpandCustomFolderPaths(null, game, @"{RomDir}\Saves").Single());
            Assert.IsFalse(PathExpansion.ExpandCustomFolderPaths(null, new Game(), @"{RomDir}\Saves").Any());
        }

        [TestMethod]
        public void AllDocumentedVariablesAreRecognized()
        {
            Assert.AreEqual(9, FolderPathVariables.All.Count);
            foreach (var variable in FolderPathVariables.All)
            {
                Assert.IsTrue(PathExpansion.IsCustomFolderTemplate(variable.Token));
                Assert.IsFalse(string.IsNullOrWhiteSpace(variable.Description));
            }
        }
    }
}

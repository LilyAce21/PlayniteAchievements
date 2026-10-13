using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Minecraft;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class MinecraftProviderTests
    {
        private const string PlayerUuid = "0123456789abcdef0123456789abcdef";
        private const string OtherUuid = "fedcba9876543210fedcba9876543210";

        [TestMethod]
        public async Task Refresh_MergesWorldsWithEarliestUnlockAndTabCategories()
        {
            var root = CreateTempDirectory();
            try
            {
                var gameFolder = CreateFixture(root);
                var data = await RefreshAsync(gameFolder, root).ConfigureAwait(false);

                Assert.IsNotNull(data);
                Assert.AreEqual("Minecraft", data.ProviderKey);
                var byId = data.Achievements.ToDictionary(a => Strip(a.ApiName));

                var mineStone = byId["minecraft:story/mine_stone"];
                Assert.IsTrue(mineStone.Unlocked);
                Assert.AreEqual(new DateTime(2024, 2, 1, 13, 0, 0, DateTimeKind.Utc), mineStone.UnlockTimeUtc);
                Assert.AreEqual("Stone Age", mineStone.DisplayName);
                Assert.AreEqual("Mine Stone with your new Pickaxe", mineStone.Description);
                Assert.AreEqual("Fixture Pack::Minecraft", mineStone.Category);
                Assert.AreEqual(Path.GetFileName(root) + "|minecraft:story/mine_stone", data.Achievements.Single(a => a.DisplayName == "Stone Age").ApiName);
                Assert.AreEqual("Base", mineStone.CategoryType);
                Assert.AreEqual(RarityTier.Common, mineStone.Rarity);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public async Task Refresh_ExcludesRecipesDisplaylessAndOtherPlayers()
        {
            var root = CreateTempDirectory();
            try
            {
                var gameFolder = CreateFixture(root);
                var data = await RefreshAsync(gameFolder, root).ConfigureAwait(false);
                var ids = data.Achievements.Select(a => Strip(a.ApiName)).ToList();

                Assert.IsFalse(ids.Any(id => id.Contains("recipes/")));
                Assert.IsFalse(ids.Contains("minecraft:story/technical"));
                Assert.IsFalse(ids.Contains("minecraft:story/removed_by_datapack"));
                Assert.IsFalse(ids.Contains("coolmod:tracking/ritual"));

                // Only the other player's file in World B has this one done.
                var secret = data.Achievements.Single(a => Strip(a.ApiName) == "minecraft:nether/secret");
                Assert.IsFalse(secret.Unlocked);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public async Task Refresh_MapsFramesAndProgressFromFurthestWorld()
        {
            var root = CreateTempDirectory();
            try
            {
                var gameFolder = CreateFixture(root);
                var data = await RefreshAsync(gameFolder, root).ConfigureAwait(false);
                var byId = data.Achievements.ToDictionary(a => Strip(a.ApiName));

                var adventuring = byId["minecraft:adventure/adventuring_time"];
                Assert.IsFalse(adventuring.Unlocked);
                Assert.AreEqual(2, adventuring.ProgressNum);
                Assert.AreEqual(3, adventuring.ProgressDenom);
                Assert.AreEqual(RarityTier.Rare, adventuring.Rarity);
                Assert.AreEqual("Fixture Pack::Adventure", adventuring.Category);
                Assert.AreEqual("Base", adventuring.CategoryType);

                var secret = byId["minecraft:nether/secret"];
                Assert.IsTrue(secret.Hidden);
                Assert.AreEqual(RarityTier.UltraRare, secret.Rarity);
                Assert.IsNull(secret.ProgressNum);

                Assert.AreEqual(RarityTier.Uncommon, byId["minecraft:story/root"].Rarity);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public async Task Refresh_ReadsModAdvancementsFromLegacyFolderWithoutGroupTag()
        {
            var root = CreateTempDirectory();
            try
            {
                var gameFolder = CreateFixture(root);
                var data = await RefreshAsync(gameFolder, root).ConfigureAwait(false);
                var first = data.Achievements.Single(a => Strip(a.ApiName) == "coolmod:first");

                Assert.AreEqual("Fixture Pack::Cool Mod", first.Category);
                Assert.IsNull(first.CategoryType);
                Assert.AreEqual("First Steps", first.DisplayName);
                Assert.IsTrue(first.Unlocked);
                Assert.IsNull(first.UnlockTimeUtc);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public async Task Refresh_OrdersVanillaTabsFirstDepthFirst()
        {
            var root = CreateTempDirectory();
            try
            {
                var gameFolder = CreateFixture(root);
                var data = await RefreshAsync(gameFolder, root).ConfigureAwait(false);
                var ids = data.Achievements.Select(a => Strip(a.ApiName)).ToList();

                CollectionAssert.AreEqual(
                    new[]
                    {
                        "minecraft:story/root",
                        "minecraft:story/mine_stone",
                        "minecraft:nether/root",
                        "minecraft:nether/secret",
                        "minecraft:adventure/root",
                        "minecraft:adventure/adventuring_time",
                        "coolmod:root",
                        "coolmod:badge",
                        "coolmod:first",
                        "coolmod:oil"
                    },
                    ids);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public async Task Refresh_ExtractsUpscaledFirstFrameIcons()
        {
            var root = CreateTempDirectory();
            try
            {
                var gameFolder = CreateFixture(root);
                var data = await RefreshAsync(gameFolder, root).ConfigureAwait(false);
                var mineStone = data.Achievements.Single(a => Strip(a.ApiName) == "minecraft:story/mine_stone");
                var rootRow = data.Achievements.Single(a => Strip(a.ApiName) == "minecraft:story/root");

                Assert.IsNotNull(mineStone.UnlockedIconPath);
                Assert.IsTrue(File.Exists(mineStone.UnlockedIconPath));
                using (var stream = File.OpenRead(mineStone.UnlockedIconPath))
                {
                    var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                    Assert.AreEqual(MinecraftIconExtractor.IconSize, frame.PixelWidth);
                    Assert.AreEqual(MinecraftIconExtractor.IconSize, frame.PixelHeight);
                }

                // grass_block has no model or plain texture; the _side block texture is used.
                StringAssert.EndsWith(rootRow.UnlockedIconPath, "grass_block_side.png");

                // diamond_boots' item model points at a texture in a subfolder.
                var adventuring = data.Achievements.Single(a => Strip(a.ApiName) == "minecraft:adventure/adventuring_time");
                StringAssert.EndsWith(adventuring.UnlockedIconPath, Path.Combine("item", "tools", "boots_custom.png"));

                // coolmod:gadget's 1.21.4 item definition names a block model whose texture is the face.
                var first = data.Achievements.Single(a => Strip(a.ApiName) == "coolmod:first");
                StringAssert.EndsWith(first.UnlockedIconPath, "gadget_face.png");

                // A NeoForge fluid-container bucket falls back to the vanilla bucket.
                StringAssert.EndsWith(data.Achievements.Single(a => Strip(a.ApiName) == "coolmod:oil").UnlockedIconPath, Path.Combine("item", "bucket.png"));

                // Custom model data 2 picks the second override's model, not the base texture.
                StringAssert.EndsWith(data.Achievements.Single(a => Strip(a.ApiName) == "coolmod:badge").UnlockedIconPath, "badge_two.png");
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public async Task Refresh_ReturnsNullWhenNoWorldHasThePlayersFile()
        {
            var root = CreateTempDirectory();
            try
            {
                var gameFolder = CreateFixture(root);
                var data = await RefreshAsync(gameFolder, root, playerUuid: "11111111222233334444555555555555").ConfigureAwait(false);

                // World A has one file and still counts, so make it ambiguous too.
                Assert.IsNotNull(data);
                WriteText(Path.Combine(gameFolder, "saves", "World A", "advancements", OtherUuid + ".json"), "{}");
                var ambiguous = await RefreshAsync(gameFolder, root, playerUuid: "11111111222233334444555555555555").ConfigureAwait(false);
                Assert.IsNull(ambiguous);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public async Task Refresh_ReturnsNullWhenAProgressFileIsUnreadable()
        {
            var root = CreateTempDirectory();
            try
            {
                var gameFolder = CreateFixture(root);
                WriteText(Path.Combine(gameFolder, "saves", "World A", "advancements", PlayerUuid + ".json"), "{ \"minecraft:story/mine_stone\": ");
                var data = await RefreshAsync(gameFolder, root).ConfigureAwait(false);
                Assert.IsNull(data);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public async Task Refresh_KeepsInstancesApartUnderInstanceCategories()
        {
            var root = CreateTempDirectory();
            try
            {
                var prism = Path.Combine(root, "PrismLauncher");
                var packA = Path.Combine(prism, "instances", "packa");
                var packB = Path.Combine(prism, "instances", "packb");
                CreateFixture(packA, "Pack A");
                CreateFixture(packB, "Pack B");
                // Pack B's only world progress: a fresh world with nothing done.
                foreach (var world in Directory.GetDirectories(Path.Combine(packB, ".minecraft", "saves")))
                {
                    foreach (var file in Directory.GetFiles(Path.Combine(world, "advancements")))
                    {
                        File.WriteAllText(file, "{\"DataVersion\":3953}");
                    }
                }

                var data = await RefreshAsync(null, root, configuredFolders: new[] { prism }).ConfigureAwait(false);

                var stoneA = data.Achievements.Single(a => a.ApiName == "packa|minecraft:story/mine_stone");
                var stoneB = data.Achievements.Single(a => a.ApiName == "packb|minecraft:story/mine_stone");
                Assert.IsTrue(stoneA.Unlocked);
                Assert.IsFalse(stoneB.Unlocked);
                Assert.AreEqual("Pack A::Minecraft", stoneA.Category);
                Assert.AreEqual("Pack B::Minecraft", stoneB.Category);
                Assert.AreEqual(data.Achievements.Count / 2, data.Achievements.Count(a => a.ApiName.StartsWith("packa|", StringComparison.Ordinal)));
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void ParseTimestamp_ConvertsCompactOffsetToUtc()
        {
            Assert.AreEqual(
                new DateTime(2024, 2, 1, 13, 0, 0, DateTimeKind.Utc),
                MinecraftProgressReader.ParseTimestamp("2024-02-01 08:00:00 -0500"));
            Assert.IsNull(MinecraftProgressReader.ParseTimestamp("yesterday"));
        }

        [TestMethod]
        public void ResolveText_HandlesTranslateFallbackArgumentsAndExtra()
        {
            var table = new Dictionary<string, string> { ["greet"] = "Hello %s and %2$s", ["name"] = "Steve" };
            Func<string, string> translate = key => table.TryGetValue(key, out var value) ? value : null;

            var component = JToken.Parse("{\"translate\":\"greet\",\"with\":[{\"translate\":\"name\"},\"Alex\"],\"extra\":[{\"text\":\"!\"}]}");
            Assert.AreEqual("Hello Steve and Alex!", MinecraftAdvancementCatalog.ResolveText(component, translate));

            var fallback = JToken.Parse("{\"translate\":\"missing\",\"fallback\":\"Fallback\"}");
            Assert.AreEqual("Fallback", MinecraftAdvancementCatalog.ResolveText(fallback, translate));

            Assert.AreEqual("missing.key", MinecraftAdvancementCatalog.ResolveText(JToken.Parse("{\"translate\":\"missing.key\"}"), translate));
        }

        [TestMethod]
        public void ParseLaunchInstanceId_ReadsShortLongAndQuotedForms()
        {
            Assert.AreEqual("My Pack", MinecraftGameResolver.ParseLaunchInstanceId("-l \"My Pack\""));
            Assert.AreEqual("fabric", MinecraftGameResolver.ParseLaunchInstanceId("--launch fabric --server x"));
            Assert.AreEqual("vanilla", MinecraftGameResolver.ParseLaunchInstanceId("--launch=vanilla"));
            Assert.IsNull(MinecraftGameResolver.ParseLaunchInstanceId("--show fabric"));
        }

        [TestMethod]
        public void IsJavaTitle_RejectsOtherMinecraftGames()
        {
            Assert.IsTrue(MinecraftGameResolver.IsJavaTitle("Minecraft"));
            Assert.IsTrue(MinecraftGameResolver.IsJavaTitle("Minecraft: Java Edition"));
            Assert.IsFalse(MinecraftGameResolver.IsJavaTitle("Minecraft: Hypixel"));
            Assert.IsFalse(MinecraftGameResolver.IsJavaTitle("Minecraft Dungeons"));
            Assert.IsFalse(MinecraftGameResolver.IsJavaTitle("Minecraft Legends"));
            Assert.IsFalse(MinecraftGameResolver.IsJavaTitle("Minecraft: Story Mode"));
            Assert.IsFalse(MinecraftGameResolver.IsJavaTitle("Minecraft for Windows"));
            Assert.IsFalse(MinecraftGameResolver.IsJavaTitle("Terraria"));
        }

        [TestMethod]
        public void FromFolder_WorldFolderCoversOnlyThatWorld()
        {
            var root = CreateTempDirectory();
            try
            {
                var gameFolder = CreateFixture(root);
                var world = Path.Combine(gameFolder, "saves", "World B");
                var source = MinecraftGameResolver.FromFolder(world, "test");

                CollectionAssert.AreEqual(new[] { world }, source.Instances.Single().Worlds);
                Assert.AreEqual(gameFolder, source.Instances.Single().GameFolder);
                Assert.AreEqual(Path.GetFileName(root), source.Instances.Single().Key);
                Assert.AreEqual("Fixture Pack", source.Instances.Single().Name);

                var instance = MinecraftGameResolver.FromFolder(root, "test");
                Assert.AreEqual(2, instance.Instances.Single().Worlds.Count);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void ExpandGameFolders_ExpandsLauncherDataAndInstanceFolders()
        {
            var root = CreateTempDirectory();
            try
            {
                var prism = Path.Combine(root, "PrismLauncher");
                Directory.CreateDirectory(Path.Combine(prism, "instances", "Pack A", "minecraft", "saves"));
                Directory.CreateDirectory(Path.Combine(prism, "instances", "Pack B", ".minecraft", "saves"));
                Directory.CreateDirectory(Path.Combine(prism, "instances", "Empty"));
                var modrinth = Path.Combine(root, "ModrinthApp");
                Directory.CreateDirectory(Path.Combine(modrinth, "profiles", "Pack C", "saves"));
                var carbon = Path.Combine(root, "gdlauncher_carbon");
                Directory.CreateDirectory(Path.Combine(carbon, "data", "instances", "Pack D", "instance", "saves"));

                Assert.AreEqual(2, MinecraftPaths.ExpandGameFolders(prism).Count);
                Assert.AreEqual(2, MinecraftPaths.ExpandGameFolders(Path.Combine(prism, "instances")).Count);
                StringAssert.EndsWith(MinecraftPaths.ExpandGameFolders(modrinth).Single(), "Pack C");
                StringAssert.EndsWith(MinecraftPaths.ExpandGameFolders(carbon).Single(), "instance");
                StringAssert.EndsWith(MinecraftPaths.ExpandGameFolders(Path.Combine(prism, "instances", "Pack A")).Single(), "minecraft");
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void SelectPlayerFiles_UsesSoleFileOrMatchingUuid()
        {
            var root = CreateTempDirectory();
            try
            {
                var gameFolder = CreateFixture(root);
                var uuids = new HashSet<string> { PlayerUuid };
                Assert.AreEqual(1, MinecraftProgressReader.SelectPlayerFiles(Path.Combine(gameFolder, "saves", "World A"), new HashSet<string>()).Count);

                var worldB = MinecraftProgressReader.SelectPlayerFiles(Path.Combine(gameFolder, "saves", "World B"), uuids);
                Assert.AreEqual(1, worldB.Count);
                StringAssert.Contains(worldB[0], PlayerUuid);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void ReadProfileIds_ReadsOfficialAndPrismAccountFiles()
        {
            var root = CreateTempDirectory();
            try
            {
                var official = Path.Combine(root, "launcher_accounts.json");
                WriteText(official, "{\"accounts\":{\"abc\":{\"minecraftProfile\":{\"id\":\"0123456789ABCDEF0123456789ABCDEF\",\"name\":\"Steve\"}}}}");
                var prism = Path.Combine(root, "accounts.json");
                WriteText(prism, "{\"accounts\":[{\"profile\":{\"id\":\"fedcba98-7654-3210-fedc-ba9876543210\",\"name\":\"Alex\"}}],\"formatVersion\":3}");

                CollectionAssert.AreEqual(new[] { PlayerUuid }, MinecraftAccounts.ReadProfileIds(official).ToList());
                CollectionAssert.AreEqual(new[] { OtherUuid }, MinecraftAccounts.ReadProfileIds(prism).ToList());
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        /// <summary>The advancement id of a row, without its instance prefix.</summary>
        private static string Strip(string apiName) => apiName.Substring(apiName.IndexOf('|') + 1);

        /// <summary>
        /// Refreshes a game whose install folder is <paramref name="gameFolder"/>, or, when
        /// <paramref name="configuredFolders"/> is given, a game named Minecraft read from those folders.
        /// </summary>
        private static async Task<GameAchievementData> RefreshAsync(
            string gameFolder,
            string userDataRoot,
            string playerUuid = PlayerUuid,
            IEnumerable<string> configuredFolders = null)
        {
            var settings = new PlayniteAchievementsSettings();
            var registry = new ProviderRegistry(settings, new[] { "Minecraft" });
            var providerSettings = registry.GetSettings<MinecraftSettings>();
            providerSettings.PlayerUuid = playerUuid;
            if (configuredFolders != null)
            {
                providerSettings.GameFolders = configuredFolders.ToList();
            }

            registry.Save(providerSettings);

            var provider = new MinecraftDataProvider(new FakeLogger(), settings, new FakePlayniteApi(), Path.Combine(userDataRoot, "userdata"));
            var game = configuredFolders == null
                ? new Game("Fixture Pack") { Id = Guid.NewGuid(), InstallDirectory = gameFolder }
                : new Game("Minecraft") { Id = Guid.NewGuid() };
            Assert.IsTrue(provider.IsCapable(game));

            GameAchievementData captured = null;
            await provider.RefreshAsync(
                new[] { game },
                onGameStarting: null,
                onGameCompleted: (completedGame, data) =>
                {
                    captured = data;
                    return Task.CompletedTask;
                },
                cancel: CancellationToken.None).ConfigureAwait(false);
            return captured;
        }

        /// <summary>
        /// A Prism-style instance: root/mmc-pack.json names 1.21, root/.minecraft holds the game jar,
        /// a mod, two worlds and a datapack.
        /// </summary>
        private static string CreateFixture(string root, string instanceName = "Fixture Pack")
        {
            var gameFolder = Path.Combine(root, ".minecraft");
            WriteText(Path.Combine(root, "mmc-pack.json"), "{\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.21\"}]}");
            WriteText(Path.Combine(root, "instance.cfg"), "[General]\nname=" + instanceName + "\n");

            var jarEntries = new Dictionary<string, byte[]>
            {
                ["data/minecraft/advancement/story/root.json"] = Json("{\"display\":{\"title\":{\"translate\":\"advancements.story.root.title\"},\"description\":{\"translate\":\"advancements.story.root.description\"},\"icon\":{\"id\":\"minecraft:grass_block\"},\"frame\":\"goal\"},\"criteria\":{\"crafting_table\":{}}}"),
                ["data/minecraft/advancement/story/mine_stone.json"] = Json("{\"parent\":\"minecraft:story/root\",\"display\":{\"title\":{\"translate\":\"advancements.story.mine_stone.title\"},\"description\":{\"translate\":\"advancements.story.mine_stone.description\"},\"icon\":{\"item\":\"minecraft:wooden_pickaxe\"}},\"criteria\":{\"get_stone\":{}}}"),
                ["data/minecraft/advancement/story/technical.json"] = Json("{\"parent\":\"minecraft:story/root\",\"criteria\":{\"tick\":{}}}"),
                ["data/minecraft/advancement/story/removed_by_datapack.json"] = Json("{\"parent\":\"minecraft:story/root\",\"display\":{\"title\":\"Removed\",\"description\":\"\",\"icon\":{\"id\":\"minecraft:stone\"}},\"criteria\":{\"x\":{}}}"),
                ["data/minecraft/advancement/recipes/misc/bread.json"] = Json("{\"parent\":\"minecraft:recipes/root\",\"criteria\":{\"has\":{}}}"),
                ["data/minecraft/advancement/nether/root.json"] = Json("{\"display\":{\"title\":{\"translate\":\"advancements.nether.root.title\"},\"description\":\"\",\"icon\":{\"id\":\"minecraft:red_nether_bricks\"}},\"criteria\":{\"entered\":{}}}"),
                ["data/minecraft/advancement/nether/secret.json"] = Json("{\"parent\":\"minecraft:nether/root\",\"display\":{\"title\":\"Secret\",\"description\":\"\",\"icon\":{\"id\":\"minecraft:stone\"},\"frame\":\"challenge\",\"hidden\":true},\"criteria\":{\"a\":{},\"b\":{}}}"),
                ["data/minecraft/advancement/adventure/root.json"] = Json("{\"display\":{\"title\":{\"translate\":\"advancements.adventure.root.title\"},\"description\":\"\",\"icon\":{\"id\":\"minecraft:map\"}},\"criteria\":{\"killed\":{}}}"),
                ["data/minecraft/advancement/adventure/adventuring_time.json"] = Json("{\"parent\":\"minecraft:adventure/root\",\"display\":{\"title\":\"Adventuring Time\",\"description\":\"Discover every biome\",\"icon\":{\"id\":\"minecraft:diamond_boots\"},\"frame\":\"challenge\"},\"criteria\":{\"a\":{},\"b\":{},\"c\":{}},\"requirements\":[[\"a\"],[\"b\"],[\"c\"]]}"),
                ["assets/minecraft/lang/en_us.json"] = Json("{\"advancements.story.root.title\":\"Minecraft\",\"advancements.story.root.description\":\"The heart and story of the game\",\"advancements.story.mine_stone.title\":\"Stone Age\",\"advancements.story.mine_stone.description\":\"Mine Stone with your new Pickaxe\",\"advancements.nether.root.title\":\"Nether\",\"advancements.adventure.root.title\":\"Adventure\"}"),
                ["assets/minecraft/textures/item/wooden_pickaxe.png"] = Png(16, 32),
                ["assets/minecraft/textures/block/grass_block_side.png"] = Png(16, 16),
                ["assets/minecraft/models/item/diamond_boots.json"] = Json("{\"parent\":\"minecraft:item/generated\",\"textures\":{\"layer0\":\"minecraft:item/tools/boots_custom\"}}"),
                ["assets/minecraft/models/item/generated.json"] = Json("{\"parent\":\"builtin/generated\"}"),
                ["assets/minecraft/textures/item/tools/boots_custom.png"] = Png(16, 16),
                ["assets/minecraft/textures/item/bucket.png"] = Png(16, 16),
                ["assets/minecraft/models/block/cube_all.json"] = Json("{\"parent\":\"block/cube\",\"textures\":{\"particle\":\"#all\",\"down\":\"#all\"}}"),
                // Pads the jar past the loader-stub size threshold.
                ["padding.bin"] = Padding(1100 * 1024)
            };
            WriteZip(Path.Combine(gameFolder, "versions", "1.21", "1.21.jar"), jarEntries);

            WriteZip(Path.Combine(gameFolder, "mods", "coolmod.jar"), new Dictionary<string, byte[]>
            {
                ["data/coolmod/advancements/root.json"] = Json("{\"display\":{\"title\":{\"text\":\"Cool \",\"extra\":[\"Mod\"]},\"description\":\"\",\"icon\":{\"item\":\"coolmod:gadget\"}},\"criteria\":{\"x\":{}}}"),
                ["data/coolmod/advancements/first.json"] = Json("{\"parent\":\"coolmod:root\",\"display\":{\"title\":{\"translate\":\"coolmod.first\"},\"description\":\"\",\"icon\":{\"item\":\"coolmod:gadget\"}},\"criteria\":{\"x\":{}}}"),
                ["assets/coolmod/lang/en_us.json"] = Json("{\"coolmod.first\":\"First Steps\"}"),
                ["data/coolmod/advancements/oil.json"] = Json("{\"parent\":\"coolmod:root\",\"display\":{\"title\":\"Oil\",\"description\":\"\",\"icon\":{\"id\":\"coolmod:oil_bucket\"}},\"criteria\":{\"x\":{}}}"),
                ["assets/coolmod/models/item/oil_bucket.json"] = Json("{\"parent\":\"neoforge:item/bucket\",\"loader\":\"neoforge:fluid_container\",\"fluid\":\"coolmod:oil\"}"),
                ["data/coolmod/advancements/badge.json"] = Json("{\"parent\":\"coolmod:root\",\"display\":{\"title\":\"Badge\",\"description\":\"\",\"icon\":{\"id\":\"coolmod:badge\",\"components\":{\"minecraft:custom_model_data\":2}}},\"criteria\":{\"x\":{}}}"),
                ["assets/coolmod/models/item/badge.json"] = Json("{\"parent\":\"minecraft:item/generated\",\"textures\":{\"layer0\":\"coolmod:item/badge_zero\"},\"overrides\":[{\"predicate\":{\"minecraft:custom_model_data\":1},\"model\":\"coolmod:item/badge_one\"},{\"predicate\":{\"minecraft:custom_model_data\":2},\"model\":\"coolmod:item/badge_two\"},{\"predicate\":{\"minecraft:custom_model_data\":3},\"model\":\"coolmod:item/badge_three\"}]}"),
                ["assets/coolmod/models/item/badge_two.json"] = Json("{\"parent\":\"minecraft:item/generated\",\"textures\":{\"layer0\":\"coolmod:item/badge_two\"}}"),
                ["assets/coolmod/textures/item/badge_zero.png"] = Png(16, 16),
                ["assets/coolmod/textures/item/badge_two.png"] = Png(16, 16),
                ["assets/coolmod/items/gadget.json"] = Json("{\"model\":{\"type\":\"minecraft:model\",\"model\":\"coolmod:block/gadget_block\"}}"),
                ["assets/coolmod/models/block/gadget_block.json"] = Json("{\"parent\":\"minecraft:block/cube_all\",\"textures\":{\"all\":\"coolmod:block/gadget_face\"}}"),
                ["assets/coolmod/textures/block/gadget_face.png"] = Png(16, 16),
                // A tracking tree: the root has no display, so the game never shows its children.
                ["data/coolmod/advancements/tracking/root.json"] = Json("{\"criteria\":{\"tick\":{}}}"),
                ["data/coolmod/advancements/tracking/ritual.json"] = Json("{\"parent\":\"coolmod:tracking/root\",\"display\":{\"title\":\"Ritual\",\"description\":\"\",\"icon\":{\"id\":\"coolmod:gadget\"},\"hidden\":true},\"criteria\":{\"x\":{}}}")
            });

            var worldA = Path.Combine(gameFolder, "saves", "World A");
            var worldB = Path.Combine(gameFolder, "saves", "World B");
            foreach (var world in new[] { worldA, worldB })
            {
                WriteText(Path.Combine(world, "level.dat"), string.Empty);
            }

            // A datapack in World B replaces an advancement with a display-less one, hiding it.
            WriteText(
                Path.Combine(worldB, "datapacks", "tweaks", "data", "minecraft", "advancement", "story", "removed_by_datapack.json"),
                "{\"parent\":\"minecraft:story/root\",\"criteria\":{\"x\":{}}}");

            WriteText(Path.Combine(worldA, "advancements", PlayerUuid + ".json"),
                "{\"minecraft:story/mine_stone\":{\"criteria\":{\"get_stone\":\"2024-03-01 12:00:00 +0000\"},\"done\":true}," +
                "\"minecraft:adventure/adventuring_time\":{\"criteria\":{\"a\":\"2024-03-01 12:00:00 +0000\"},\"done\":false}," +
                "\"minecraft:recipes/misc/bread\":{\"criteria\":{\"has\":\"2024-03-01 12:00:00 +0000\"},\"done\":true}," +
                "\"coolmod:first\":{\"criteria\":{},\"done\":true},\"DataVersion\":3953}");
            WriteText(Path.Combine(worldB, "advancements", PlayerUuid + ".json"),
                "{\"minecraft:story/mine_stone\":{\"criteria\":{\"get_stone\":\"2024-02-01 08:00:00 -0500\"},\"done\":true}," +
                "\"minecraft:adventure/adventuring_time\":{\"criteria\":{\"a\":\"2024-02-01 08:00:00 -0500\",\"b\":\"2024-02-01 08:00:00 -0500\"},\"done\":false}," +
                "\"DataVersion\":3953}");
            WriteText(Path.Combine(worldB, "advancements", OtherUuid + ".json"),
                "{\"minecraft:nether/secret\":{\"criteria\":{\"a\":\"2024-01-01 00:00:00 +0000\",\"b\":\"2024-01-01 00:00:00 +0000\"},\"done\":true},\"DataVersion\":3953}");

            return gameFolder;
        }

        private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

        private static byte[] Padding(int length)
        {
            var bytes = new byte[length];
            new Random(7).NextBytes(bytes);
            return bytes;
        }

        private static byte[] Png(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0x20;
                pixels[i + 1] = 0x60;
                pixels[i + 2] = 0x86;
                pixels[i + 3] = 0xFF;
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        private static void WriteZip(string path, IReadOnlyDictionary<string, byte[]> entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var entry in entries)
                {
                    var zipEntry = archive.CreateEntry(entry.Key, CompressionLevel.NoCompression);
                    using (var stream = zipEntry.Open())
                    {
                        stream.Write(entry.Value, 0, entry.Value.Length);
                    }
                }
            }
        }

        private static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", nameof(MinecraftProviderTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }

        private sealed class FakeLogger : ILogger
        {
            public void Debug(string message) { }
            public void Debug(Exception exception, string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
            public void Info(string message) { }
            public void Info(Exception exception, string message) { }
            public void Trace(string message) { }
            public void Trace(Exception exception, string message) { }
            public void Warn(string message) { }
            public void Warn(Exception exception, string message) { }
        }

        private sealed class FakePlayniteApi : IPlayniteAPI
        {
            public IMainViewAPI MainView => null;
            public IGameDatabaseAPI Database => null;
            public IDialogsFactory Dialogs => null;
            public IPlaynitePathsAPI Paths => null;
            public INotificationsAPI Notifications => null;
            public IPlayniteInfoAPI ApplicationInfo => null;
            public IWebViewFactory WebViews => null;
            public IResourceProvider Resources => null;
            public IUriHandlerAPI UriHandler => null;
            public IPlayniteSettingsAPI ApplicationSettings => null;
            public IAddons Addons => null;
            public IEmulationAPI Emulation => null;

            public string ExpandGameVariables(Game game, string source) => source;
            public string ExpandGameVariables(Game game, string source, string fallbackValue) => source ?? fallbackValue;
            public GameAction ExpandGameVariables(Game game, GameAction source) => source;
            public void StartGame(Guid id) { }
            public void InstallGame(Guid id) { }
            public void UninstallGame(Guid id) { }
            public void AddCustomElementSupport(Plugin plugin, AddCustomElementSupportArgs args) { }
            public void AddSettingsSupport(Plugin plugin, AddSettingsSupportArgs args) { }
            public void AddConvertersSupport(Plugin plugin, AddConvertersSupportArgs args) { }
        }
    }
}

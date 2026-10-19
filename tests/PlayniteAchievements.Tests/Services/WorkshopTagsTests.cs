using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Workshop;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class WorkshopTagsTests
    {
        private static HashSet<string> Tags(params string[] tags) => new HashSet<string>(tags);

        [TestMethod]
        public void Parse_SplitsTrimsLowercasesAndDedupes()
        {
            CollectionAssert.AreEqual(
                new[] { "emulation", "retro", "jrpg" },
                new List<string>(WorkshopTags.Parse(" Emulation, retro;JRPG ,, emulation ")));
        }

        [TestMethod]
        public void Parse_EmptyOrWhitespace_ReturnsEmpty()
        {
            Assert.AreEqual(0, WorkshopTags.Parse(null).Count);
            Assert.AreEqual(0, WorkshopTags.Parse("  , ; ").Count);
        }

        [TestMethod]
        public void Matches_EmptyFilter_MatchesEverything()
        {
            Assert.IsTrue(WorkshopTags.Matches(Tags(), WorkshopTags.Parse(""), exclude: false));
            Assert.IsTrue(WorkshopTags.Matches(Tags("emulation"), WorkshopTags.Parse(""), exclude: true));
        }

        [TestMethod]
        public void Matches_Include_RequiresAnyFilterTag()
        {
            var filter = WorkshopTags.Parse("emulation, retro");
            Assert.IsTrue(WorkshopTags.Matches(Tags("retro"), filter, exclude: false));
            Assert.IsTrue(WorkshopTags.Matches(Tags("emulation", "jrpg"), filter, exclude: false));
            Assert.IsFalse(WorkshopTags.Matches(Tags("jrpg"), filter, exclude: false));
            Assert.IsFalse(WorkshopTags.Matches(Tags(), filter, exclude: false));
        }

        [TestMethod]
        public void Matches_Exclude_HidesAnyFilterTag()
        {
            var filter = WorkshopTags.Parse("emulation");
            Assert.IsFalse(WorkshopTags.Matches(Tags("emulation", "retro"), filter, exclude: true));
            Assert.IsTrue(WorkshopTags.Matches(Tags("retro"), filter, exclude: true));
            Assert.IsTrue(WorkshopTags.Matches(Tags(), filter, exclude: true));
        }

        [TestMethod]
        public void Matches_IsExactNotSubstring()
        {
            Assert.IsFalse(WorkshopTags.Matches(Tags("emulation"), WorkshopTags.Parse("emu"), exclude: false));
            Assert.IsTrue(WorkshopTags.Matches(Tags("emulation"), WorkshopTags.Parse("emu"), exclude: true));
        }
    }
}

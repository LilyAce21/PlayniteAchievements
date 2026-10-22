using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Services.ThemeMigration;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class Ps5RankMigrationTests
    {
        internal static string Badges(string plugin = "PlayniteAchievements")
        {
            return "<StackPanel>" + string.Concat(new[] { "Bronze", "Silver", "Gold", "Plat" }
                .SelectMany(tier => new[] { 1, 2, 3 }.Select(number => tier + number)).Concat(new[] { "Plat" })
                .Select(rank => "<Image Source=\"{ThemeFile 'Assets/Welcome/Widgets/Trophies/Badges/" + rank + ".png'}\"><Image.Style><Style><Setter Property=\"Visibility\" Value=\"Collapsed\"/><Style.Triggers><DataTrigger Binding=\"{PluginSettings Plugin=" + plugin + ", Path=GSRank}\" Value=\"" + rank + "\"><Setter Property=\"Visibility\" Value=\"Visible\"/></DataTrigger></Style.Triggers></Style></Image.Style></Image>")) + "</StackPanel>";
        }

        [TestMethod]
        public void EveryRankResolvesToExactlyOneBadgeInEachThemeSection()
        {
            var original=Badges();
            int count=0;
            var migrated=ThemeMigrationService.MigratePs5Ranks(original,ref count);
            Assert.AreEqual(13,count);
            var document=XDocument.Parse(migrated);
            foreach(AchievementRank rank in Enum.GetValues(typeof(AchievementRank)))
            {
                var matches=document.Descendants("DataTrigger").Where(trigger=>(string)trigger.Attribute("Value")==rank.ToString()).ToList();
                Assert.AreEqual(1,matches.Count,rank.ToString());
                Assert.AreEqual("Visible",(string)matches.Single().Element("Setter").Attribute("Value"));
            }
            foreach(var rank in new[]{"Bronze1","Bronze2","Bronze3","Silver1","Silver2","Silver3","Gold1","Gold2","Gold3","Plat1","Plat2","Plat3","Plat"})
                Assert.AreEqual(1,document.Descendants("DataTrigger").Count(trigger=>(string)trigger.Attribute("Value")==rank));
            Assert.IsTrue(migrated.Contains("Value=\"Silver5\""));
            Assert.IsFalse(ThemeMigrationService.NeedsPs5RankMigration(migrated));
            count=0;Assert.AreEqual(migrated,ThemeMigrationService.MigratePs5Ranks(migrated,ref count));Assert.AreEqual(0,count);
            count=0;var both=ThemeMigrationService.MigratePs5Ranks("<Grid>"+original+original+"</Grid>",ref count);
            Assert.AreEqual(26,count);Assert.AreEqual(2,XDocument.Parse(both).Descendants("DataTrigger").Count(trigger=>(string)trigger.Attribute("Value")=="Silver5"));
        }

        [TestMethod]
        public void AlreadyMigratedThemesAreDetectedAndUnrelatedBindingsArePreserved()
        {
            Assert.IsTrue(ThemeMigrationService.NeedsPs5RankMigration(Badges()));
            var unrelated=Badges("OtherPlugin");int count=0;
            Assert.AreEqual(unrelated,ThemeMigrationService.MigratePs5Ranks(unrelated,ref count));
            Assert.AreEqual(0,count);
            var noTop=Regex.Replace(Badges(),"<Image Source=\"\\{ThemeFile 'Assets/Welcome/Widgets/Trophies/Badges/Plat.png'[\\s\\S]*?</Image>","");
            var migrated=ThemeMigrationService.MigratePs5Ranks(noTop,ref count);
            var master=XDocument.Parse(migrated).Descendants("DataTrigger").Single(trigger=>(string)trigger.Attribute("Value")=="Master1");
            StringAssert.Contains((string)master.Ancestors("Image").Single().Attribute("Source"),"Plat1.png");
        }
    }
}

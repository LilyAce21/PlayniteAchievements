using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PlayniteAchievements.Tests.Localization
{
    /// <summary>
    /// A duplicate x:Key makes WPF reject a whole language dictionary, which blanks every label
    /// in the add-on at runtime without any build error. These guard against that.
    /// </summary>
    [TestClass]
    public class PsnCapstoneLocalizationTests
    {
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        [TestMethod]
        public void NoLanguageFileHasDuplicateKeys_PsnCapstone()
        {
            var directory = Path.GetDirectoryName(FindRepoFile("source", "Localization", "en_US.xaml"));
            var problems = Directory.GetFiles(directory, "*.xaml")
                .SelectMany(file => XDocument.Load(file)
                    .Descendants()
                    .Select(element => (string)element.Attribute(Xaml + "Key"))
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .GroupBy(key => key, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => Path.GetFileName(file) + ": " + group.Key))
                .ToList();

            CollectionAssert.AreEqual(Array.Empty<string>(), problems);
        }

        [TestMethod]
        public void EnUsContainsEveryPsnCapstoneViewKey()
        {
            var keys = XDocument.Load(FindRepoFile("source", "Localization", "en_US.xaml"))
                .Descendants()
                .Select(element => (string)element.Attribute(Xaml + "Key"))
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToList();
            var views = new[]
            {
                FindRepoFile("source", "Views", "Settings", "General", "EditorSettingsSection.xaml"),
                FindRepoFile("source", "Views", "ManageAchievements", "ManageAchievementsEditorTab.xaml")
            };
            var missing = views
                .SelectMany(view => Regex.Matches(File.ReadAllText(view), @"DynamicResource (LOCPlayAch_[A-Za-z0-9_]+)")
                    .Cast<Match>()
                    .Select(match => match.Groups[1].Value))
                .Distinct(StringComparer.Ordinal)
                .Where(key => !keys.Contains(key))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            CollectionAssert.AreEqual(Array.Empty<string>(), missing);
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            Assert.Fail("Could not find " + Path.Combine(parts));
            return null;
        }
    }
}

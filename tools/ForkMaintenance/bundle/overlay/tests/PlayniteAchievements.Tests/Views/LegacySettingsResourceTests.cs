using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class LegacySettingsResourceTests
    {
        [TestMethod]
        public void LegacySettingsResourceScopesHaveUniqueKeys()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName,
                "source", "Views", "LegacyNotificationSettingsControl.xaml"))) directory = directory.Parent;
            Assert.IsNotNull(directory, "Repository source must be available.");
            var document = XDocument.Load(Path.Combine(directory.FullName,
                "source", "Views", "LegacyNotificationSettingsControl.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            foreach (var scope in document.Descendants().Where(element =>
                element.Name.LocalName == "ResourceDictionary" || element.Name.LocalName.EndsWith(".Resources")))
            {
                var keys = new HashSet<string>();
                foreach (var resource in scope.Elements())
                {
                    var key = (string)resource.Attribute(x + "Key");
                    if (key == null && resource.Name.LocalName == "Style")
                        key = ((string)resource.Attribute("TargetType"))?.Replace("{x:Type ", "").TrimEnd('}');
                    if (key != null) Assert.IsTrue(keys.Add(key), "Duplicate resource key: " + key);
                }
            }
        }
    }
}

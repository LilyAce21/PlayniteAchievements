using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Recording;

namespace PlayniteAchievements.Services.Tests.Recording
{
    [TestClass]
    public class UnlockNotifyMessageTests
    {
        private static readonly DateTime Received = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

        private const string Valid =
            "{\"v\":1,\"type\":\"unlock\",\"game\":{\"name\":\"Elden Ring (Steam)\",\"appid\":\"1245620\",\"platform\":\"steam-official\"}," +
            "\"achievement\":{\"name\":\"Elden Lord\",\"apiName\":\"ACH_ELDEN_LORD\",\"tier\":\"gold\",\"isPlatinum\":false}}";

        [TestMethod]
        public void ParsesAValidMessage()
        {
            Assert.IsTrue(UnlockNotifyMessage.TryParse(Valid, Received, out var message));
            Assert.AreEqual("Elden Ring (Steam)", message.GameName);
            Assert.AreEqual("1245620", message.AppId);
            Assert.AreEqual("steam-official", message.Platform);
            Assert.AreEqual("Elden Lord", message.AchievementName);
            Assert.AreEqual("ACH_ELDEN_LORD", message.ApiName);
            Assert.AreEqual("gold", message.Tier);
            Assert.IsFalse(message.IsPlatinum);
            Assert.AreEqual(Received, message.ReceivedUtc);
        }

        [TestMethod]
        public void UsesTheReceiveTime_NotAClockFromTheSender()
        {
            var json = Valid.Replace("{\"v\":1,", "{\"v\":1,\"sentAtUtc\":\"1999-01-01T00:00:00Z\",");
            Assert.IsTrue(UnlockNotifyMessage.TryParse(json, Received, out var message));
            Assert.AreEqual(Received, message.ReceivedUtc);
        }

        [TestMethod]
        public void RejectsWrongVersionTypeOrMissingAchievement()
        {
            Assert.IsFalse(UnlockNotifyMessage.TryParse(Valid.Replace("\"v\":1", "\"v\":2"), Received, out _));
            Assert.IsFalse(UnlockNotifyMessage.TryParse(Valid.Replace("\"unlock\"", "\"something\""), Received, out _));
            Assert.IsFalse(UnlockNotifyMessage.TryParse(
                "{\"v\":1,\"type\":\"unlock\",\"game\":{\"name\":\"X\"},\"achievement\":{}}", Received, out _));
            Assert.IsFalse(UnlockNotifyMessage.TryParse(
                "{\"v\":1,\"type\":\"unlock\",\"game\":{\"name\":\"X\"}}", Received, out _));
        }

        [TestMethod]
        public void RejectsGarbageAndOversizedInput()
        {
            Assert.IsFalse(UnlockNotifyMessage.TryParse(null, Received, out _));
            Assert.IsFalse(UnlockNotifyMessage.TryParse("   ", Received, out _));
            Assert.IsFalse(UnlockNotifyMessage.TryParse("not json at all", Received, out _));
            Assert.IsFalse(UnlockNotifyMessage.TryParse("[1,2,3]", Received, out _));
            Assert.IsFalse(UnlockNotifyMessage.TryParse(new string('x', UnlockNotifyMessage.MaxMessageBytes + 1), Received, out _));
        }

        [TestMethod]
        public void AcceptsAnAchievementWithOnlyAnInternalName()
        {
            var json = "{\"v\":1,\"type\":\"unlock\",\"achievement\":{\"apiName\":\"ACH_1\"}}";
            Assert.IsTrue(UnlockNotifyMessage.TryParse(json, Received, out var message));
            Assert.AreEqual("ACH_1", message.ApiName);
            Assert.AreEqual(string.Empty, message.GameName);
        }

        [TestMethod]
        public void OverlongTextIsShortened()
        {
            var longName = new string('a', 1000);
            var json = "{\"v\":1,\"type\":\"unlock\",\"achievement\":{\"name\":\"" + longName + "\"}}";
            Assert.IsTrue(UnlockNotifyMessage.TryParse(json, Received, out var message));
            Assert.AreEqual(300, message.AchievementName.Length);
        }
    }
}

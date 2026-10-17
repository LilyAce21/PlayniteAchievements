using System.Linq;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    public class KeySequenceMatcherTests
    {
        private static bool[] Feed(KeySequenceMatcher matcher, params Key[] keys)
        {
            return keys.Select(matcher.Push).ToArray();
        }

        [TestMethod]
        public void Push_CompletesOnLastLetter()
        {
            var matcher = new KeySequenceMatcher("abc");

            var results = Feed(matcher, Key.A, Key.B, Key.C);

            CollectionAssert.AreEqual(new[] { false, false, true }, results);
        }

        [TestMethod]
        public void Push_MatchesAfterPrecedingLetters()
        {
            var matcher = new KeySequenceMatcher("abc");

            var results = Feed(matcher, Key.X, Key.A, Key.A, Key.B, Key.C);

            Assert.IsTrue(results.Last());
        }

        [TestMethod]
        public void Push_NonLetterKeyClearsTheBuffer()
        {
            var matcher = new KeySequenceMatcher("abc");

            var results = Feed(matcher, Key.A, Key.B, Key.Space, Key.C);

            Assert.IsFalse(results.Any(r => r));
        }

        [TestMethod]
        public void Push_ModifierKeysAreIgnored()
        {
            var matcher = new KeySequenceMatcher("abc");

            var results = Feed(matcher, Key.A, Key.LeftShift, Key.B, Key.C);

            Assert.IsTrue(results.Last());
        }

        [TestMethod]
        public void Push_MatchesAgainAfterAHit()
        {
            var matcher = new KeySequenceMatcher("ab");

            var results = Feed(matcher, Key.A, Key.B, Key.B, Key.A, Key.B);

            CollectionAssert.AreEqual(new[] { false, true, false, false, true }, results);
        }
    }
}

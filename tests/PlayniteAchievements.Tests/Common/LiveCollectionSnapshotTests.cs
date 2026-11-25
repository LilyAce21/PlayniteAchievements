using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    public class LiveCollectionSnapshotTests
    {
        /// <summary>A collection that misbehaves the way a list being changed on another thread does.</summary>
        private sealed class FlakyCollection : ICollection<string>
        {
            private readonly string[] _items;
            public Func<int, Exception> FailOnCopy = attempt => null;
            public int ReportedCount;
            public int CopyCalls;

            public FlakyCollection(params string[] items) { _items = items; ReportedCount = items.Length; }

            public int Count => ReportedCount;
            public bool IsReadOnly => true;

            public void CopyTo(string[] array, int arrayIndex)
            {
                CopyCalls++;
                var failure = FailOnCopy(CopyCalls);
                if (failure != null) throw failure;
                if (array.Length - arrayIndex < _items.Length) throw new ArgumentException("destination too small");
                Array.Copy(_items, 0, array, arrayIndex, _items.Length);
            }

            public IEnumerator<string> GetEnumerator() { throw new InvalidOperationException("Collection was modified; enumeration operation may not execute."); }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public void Add(string item) { throw new NotSupportedException(); }
            public void Clear() { throw new NotSupportedException(); }
            public bool Contains(string item) => _items.Contains(item);
            public bool Remove(string item) { throw new NotSupportedException(); }
        }

        [TestMethod]
        public void Take_ReturnsAllItemsOfAnOrdinaryCollection()
        {
            var list = new List<string> { "a", "b", "c" };
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, LiveCollectionSnapshot.Take(list));
        }

        [TestMethod]
        public void Take_NullOrEmptyGivesAnEmptyArray()
        {
            Assert.AreEqual(0, LiveCollectionSnapshot.Take<string>(null).Length);
            Assert.AreEqual(0, LiveCollectionSnapshot.Take(new List<string>()).Length);
        }

        [TestMethod]
        public void Take_RetriesWhenTheCollectionIsModifiedDuringTheCopy()
        {
            var flaky = new FlakyCollection("a", "b")
            {
                FailOnCopy = attempt => attempt <= 2 ? new InvalidOperationException("Collection was modified") : null
            };

            CollectionAssert.AreEqual(new[] { "a", "b" }, LiveCollectionSnapshot.Take(flaky));
            Assert.AreEqual(3, flaky.CopyCalls);
        }

        [TestMethod]
        public void Take_RetriesWhenTheCollectionGrewBetweenCountAndCopy()
        {
            var flaky = new FlakyCollection("a", "b", "c") { ReportedCount = 2 };
            flaky.FailOnCopy = attempt => { if (attempt == 2) flaky.ReportedCount = 3; return null; };

            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, LiveCollectionSnapshot.Take(flaky));
        }

        [TestMethod]
        public void Take_DropsTheEmptyTailWhenTheCollectionShrankBetweenCountAndCopy()
        {
            var flaky = new FlakyCollection("a", "b") { ReportedCount = 4 };   // Count said 4, only 2 are copied
            CollectionAssert.AreEqual(new[] { "a", "b" }, LiveCollectionSnapshot.Take(flaky));
        }

        [TestMethod]
        public void Take_GivesUpQuietlyWhenTheCollectionNeverSettles()
        {
            var flaky = new FlakyCollection("a") { FailOnCopy = attempt => new InvalidOperationException("Collection was modified") };

            Assert.AreEqual(0, LiveCollectionSnapshot.Take(flaky, attempts: 3).Length);
            Assert.AreEqual(3, flaky.CopyCalls);
        }

        [TestMethod]
        public void Take_NeverEnumeratesTheLiveCollection()
        {
            // The fake throws "Collection was modified" if enumerated, which is exactly the startup error in the log.
            var flaky = new FlakyCollection("a", "b");
            CollectionAssert.AreEqual(new[] { "a", "b" }, LiveCollectionSnapshot.Take(flaky));
        }
    }
}

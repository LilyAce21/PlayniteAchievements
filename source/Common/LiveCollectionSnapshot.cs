using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// A private copy of a collection that another thread may be changing, such as WPF's live list of merged
    /// resource dictionaries while Playnite is still loading a theme. Looping over the live list throws
    /// "Collection was modified"; a copy taken here is safe to loop over, and is retried if the collection
    /// changes while it is being copied.
    /// </summary>
    internal static class LiveCollectionSnapshot
    {
        private const int DefaultAttempts = 4;

        /// <summary>
        /// The items as they were at some moment during the call, without nulls. After a few failed attempts
        /// (the collection kept changing) it returns what it last managed to read, or an empty array: callers
        /// treat a snapshot as best effort, never as an error.
        /// </summary>
        public static T[] Take<T>(ICollection<T> source, int attempts = DefaultAttempts) where T : class
        {
            if (source == null) return new T[0];

            for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
            {
                try
                {
                    var copy = new T[source.Count];
                    source.CopyTo(copy, 0);

                    // If the collection shrank between reading Count and copying, the tail is left empty.
                    return copy.Where(item => item != null).ToArray();
                }
                catch (InvalidOperationException)
                {
                    // Collection was modified during the copy: try again.
                }
                catch (ArgumentException)
                {
                    // It grew between reading Count and copying, so the array was too small: try again.
                }
            }

            return new T[0];
        }
    }
}

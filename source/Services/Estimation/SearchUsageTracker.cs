using System;
using System.IO;
using Newtonsoft.Json;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>
    /// Counts the search credits spent this month, so a free allowance is not used up by accident and the user can see
    /// how much is left. Saved in a small file; a new month starts the count again. Never stores a key or a query.
    /// </summary>
    public sealed class SearchUsageTracker
    {
        private sealed class State
        {
            public string Month { get; set; }
            public int Used { get; set; }
        }

        private readonly object _sync = new object();
        private readonly string _path;
        private readonly Func<DateTime> _utcNow;
        private State _state;

        public SearchUsageTracker(string filePath, Func<DateTime> utcNow = null)
        {
            _path = filePath;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        private string MonthKey => _utcNow().ToString("yyyy-MM");

        /// <summary>Credits spent so far this month.</summary>
        public int UsedThisMonth
        {
            get
            {
                lock (_sync) { return Load().Used; }
            }
        }

        public void Record(int credits)
        {
            if (credits <= 0) return;
            lock (_sync)
            {
                var state = Load();
                state.Used += credits;
                Save(state);
            }
        }

        private State Load()
        {
            if (_state != null && _state.Month == MonthKey) return _state;

            State loaded = null;
            try
            {
                if (!string.IsNullOrEmpty(_path) && File.Exists(_path)) loaded = JsonConvert.DeserializeObject<State>(File.ReadAllText(_path));
            }
            catch (Exception)
            {
                // A damaged file just means the count starts again.
            }

            _state = loaded != null && loaded.Month == MonthKey ? loaded : new State { Month = MonthKey, Used = 0 };
            return _state;
        }

        private void Save(State state)
        {
            try
            {
                if (string.IsNullOrEmpty(_path)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, JsonConvert.SerializeObject(state));
            }
            catch (Exception)
            {
                // Best effort: the count is a convenience, not something the run depends on.
            }
        }
    }
}

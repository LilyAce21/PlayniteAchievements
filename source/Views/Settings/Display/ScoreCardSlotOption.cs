namespace PlayniteAchievements.Views.Settings.Display
{
    /// <summary>An Overview score card slot choice: the persisted slot key and its display name.</summary>
    public sealed class ScoreCardSlotOption
    {
        public ScoreCardSlotOption(string key, string displayName)
        {
            Key = key;
            DisplayName = displayName;
        }

        public string Key { get; }

        public string DisplayName { get; }
    }
}

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// How notifications share the GPU with a running game. Saved numerically, so new values go at
    /// the end.
    /// </summary>
    public enum NotificationRenderMode
    {
        /// <summary>
        /// Normal GPU scheduling: the game keeps its frame rate, and the notification can stutter
        /// while the game keeps the GPU busy.
        /// </summary>
        Performance,

        /// <summary>
        /// Realtime GPU scheduling while a notification is shown: smooth over a GPU-bound game, at
        /// some cost to the game's frame rate for that span.
        /// </summary>
        Quality
    }
}

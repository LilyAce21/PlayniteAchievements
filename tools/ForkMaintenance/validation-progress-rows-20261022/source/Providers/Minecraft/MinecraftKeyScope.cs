namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>
    /// Minecraft API names are <c>instanceKey|advancementId</c> (<see cref="MinecraftScanner"/>).
    /// The instance key is a folder name, which cannot contain <c>|</c>, so the first <c>|</c>
    /// separates the two.
    /// </summary>
    internal sealed class MinecraftKeyScope : IAchievementKeyScope
    {
        public static readonly MinecraftKeyScope Instance = new MinecraftKeyScope();

        private MinecraftKeyScope()
        {
        }

        public bool TrySplit(string apiName, out string scope, out string portableKey)
        {
            scope = null;
            portableKey = null;
            var value = apiName?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            var separator = value.IndexOf('|');
            if (separator <= 0 || separator == value.Length - 1)
            {
                return false;
            }

            scope = value.Substring(0, separator).Trim();
            portableKey = value.Substring(separator + 1).Trim();
            return scope.Length > 0 && portableKey.Length > 0;
        }
    }
}

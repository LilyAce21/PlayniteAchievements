using System.IO;
using System.Runtime.CompilerServices;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Logging
{
    /// <summary>Marks explicitly identified fork messages while preserving their log destination.</summary>
    public static class ForkLogger
    {
        public static ILogger Wrap(ILogger logger, [CallerFilePath] string sourcePath = "")
        {
            if (logger == null) return null;
            if (logger is NamedLoggerWrapper named) return named.ForFork();
            return new NamedLoggerWrapper(logger,
                Path.GetFileNameWithoutExtension(sourcePath) + "-Santodan");
        }
    }
}

using System;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// A "this unlock just fired" message from the Achievements desktop app, received over a local
    /// named pipe. It carries only what is needed to pair a screenshot with the right achievement:
    /// the game it belongs to and the achievement's name. Nothing in it is trusted beyond that —
    /// unknown fields are ignored and anything malformed is dropped.
    /// </summary>
    internal sealed class UnlockNotifyMessage
    {
        /// <summary>Largest message accepted, in bytes. Real messages are a few hundred bytes.</summary>
        public const int MaxMessageBytes = 16 * 1024;

        public const int SupportedVersion = 1;

        /// <summary>The Achievements app's name for the game's config (for example "Elden Ring (Steam)").</summary>
        public string GameName { get; private set; }

        public string AppId { get; private set; }

        public string Platform { get; private set; }

        /// <summary>The achievement's display name as the other app showed it.</summary>
        public string AchievementName { get; private set; }

        /// <summary>The achievement's internal name when the other app has one.</summary>
        public string ApiName { get; private set; }

        public string Tier { get; private set; }

        public bool IsPlatinum { get; private set; }

        /// <summary>When this app received the message. The sender's own clock is not trusted.</summary>
        public DateTime ReceivedUtc { get; private set; }

        public static bool TryParse(string json, DateTime receivedUtc, out UnlockNotifyMessage message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxMessageBytes)
            {
                return false;
            }

            try
            {
                var root = JObject.Parse(json);
                if ((int?)root["v"] != SupportedVersion ||
                    !string.Equals((string)root["type"], "unlock", StringComparison.Ordinal))
                {
                    return false;
                }

                var game = root["game"] as JObject;
                var achievement = root["achievement"] as JObject;
                var name = Clean((string)achievement?["name"]);
                var apiName = Clean((string)achievement?["apiName"]);
                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(apiName))
                {
                    return false;
                }

                message = new UnlockNotifyMessage
                {
                    GameName = Clean((string)game?["name"]),
                    AppId = Clean((string)game?["appid"]),
                    Platform = Clean((string)game?["platform"]),
                    AchievementName = name,
                    ApiName = apiName,
                    Tier = Clean((string)achievement?["tier"]),
                    IsPlatinum = (bool?)achievement?["isPlatinum"] ?? false,
                    ReceivedUtc = receivedUtc
                };
                return true;
            }
            catch (Exception)
            {
                message = null;
                return false;
            }
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            return trimmed.Length > 300 ? trimmed.Substring(0, 300) : trimmed;
        }
    }
}

using System;
using System.Text;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>
    /// Light obfuscation for the API key stored in settings, so it is not readable at a glance in the
    /// settings file or a screenshot of it. This is not encryption and does not protect the key from
    /// anyone who reads the code: it is a deliberate, simple choice for a free-tier key.
    /// </summary>
    public static class SecretObfuscator
    {
        private const string Prefix = "obf1:";
        private static readonly byte[] Mask = Encoding.UTF8.GetBytes("PlayniteAchievements.EstimatedValues.v1");

        public static string Obfuscate(string plain)
        {
            var trimmed = (plain ?? string.Empty).Trim();
            if (trimmed.Length == 0) return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(trimmed);
            for (var i = 0; i < bytes.Length; i++) bytes[i] ^= Mask[i % Mask.Length];
            return Prefix + Convert.ToBase64String(bytes);
        }

        /// <summary>The key as typed. A value without the prefix is taken as a plain key pasted straight into the file.</summary>
        public static string Reveal(string stored)
        {
            var value = (stored ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;

            try
            {
                var bytes = Convert.FromBase64String(value.Substring(Prefix.Length));
                for (var i = 0; i < bytes.Length; i++) bytes[i] ^= Mask[i % Mask.Length];
                return Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                return string.Empty;
            }
        }
    }
}

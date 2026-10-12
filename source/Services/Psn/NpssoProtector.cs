using System;
using System.Security.Cryptography;
using System.Text;

namespace PlayniteAchievements.Services.Psn
{
    /// <summary>
    /// Keeps the user's NPSSO token off disk in plain text. It is encrypted with Windows' own
    /// per-user data protection, so only this Windows account on this PC can read it back; a
    /// copied settings file is useless elsewhere. The token is never logged or displayed.
    /// </summary>
    internal static class NpssoProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PlayniteAchievements.Psn.Npsso.v1");

        public static string Protect(string npsso)
        {
            if (string.IsNullOrWhiteSpace(npsso))
            {
                return string.Empty;
            }

            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(npsso.Trim()), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>The token, or null when nothing is stored or it cannot be decrypted here.</summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
            {
                return null;
            }

            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A cheap sanity check (no whitespace, plausible length), not a validity proof.</summary>
        public static bool LooksPlausible(string npsso)
        {
            if (string.IsNullOrWhiteSpace(npsso))
            {
                return false;
            }

            var value = npsso.Trim();
            if (value.Length < 20 || value.Length > 200)
            {
                return false;
            }

            foreach (var ch in value)
            {
                if (char.IsWhiteSpace(ch))
                {
                    return false;
                }
            }

            return true;
        }
    }
}

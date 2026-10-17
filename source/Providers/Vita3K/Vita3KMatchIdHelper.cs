using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Vita3K
{
    internal enum Vita3KMatchIdKind
    {
        None = 0,
        TitleId,
        NpCommId
    }

    /// <summary>
    /// Classifies Vita3K match ids: a PS Vita title id (PCSE00001, the folder name under
    /// ux0/app) or an NP communication id (NPWR01234_00, the trophy set folder name).
    /// </summary>
    internal static class Vita3KMatchIdHelper
    {
        private static readonly Regex TitleIdPattern =
            new Regex(@"^[A-Z]{4}\d{5}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NpCommIdPattern =
            new Regex(@"^NPWR\d{5}_\d{2}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool TryNormalize(string value, out string normalized)
        {
            normalized = Normalize(value);
            return normalized != null;
        }

        public static string Normalize(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
            return GetKind(normalized) == Vita3KMatchIdKind.None ? null : normalized;
        }

        public static Vita3KMatchIdKind GetKind(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (NpCommIdPattern.IsMatch(normalized))
            {
                return Vita3KMatchIdKind.NpCommId;
            }

            return TitleIdPattern.IsMatch(normalized) ? Vita3KMatchIdKind.TitleId : Vita3KMatchIdKind.None;
        }

        public static string NormalizeTitleId(string value)
        {
            var normalized = Normalize(value);
            return GetKind(normalized) == Vita3KMatchIdKind.TitleId ? normalized : null;
        }

        public static string NormalizeNpCommId(string value)
        {
            var normalized = Normalize(value);
            return GetKind(normalized) == Vita3KMatchIdKind.NpCommId ? normalized : null;
        }
    }
}

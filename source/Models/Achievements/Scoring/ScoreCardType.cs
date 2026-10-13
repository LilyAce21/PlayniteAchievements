using System;
using System.Globalization;

namespace PlayniteAchievements.Models.Achievements.Scoring
{
    /// <summary>
    /// The score a score card shows: one of the two rarity-weighted library scores, or the
    /// provider points summed for one points family (see <see cref="PointsFamilies"/>).
    /// The default value is <see cref="Collection"/>.
    /// </summary>
    public readonly struct ScoreCardType : IEquatable<ScoreCardType>
    {
        private const string CollectionKey = "Collection";
        private const string PrestigeKey = "Prestige";
        private const string PointsPrefix = "Points:";

        private enum Kind
        {
            Collection = 0,
            Prestige,
            Points
        }

        private readonly Kind _kind;
        private readonly string _family;

        private ScoreCardType(Kind kind, string family)
        {
            _kind = kind;
            _family = family;
        }

        public static ScoreCardType Collection => default(ScoreCardType);

        public static ScoreCardType Prestige => new ScoreCardType(Kind.Prestige, null);

        public static ScoreCardType Gamerscore => Points(PointsFamilies.Xbox);

        public static ScoreCardType EpicXp => Points(PointsFamilies.Epic);

        public static ScoreCardType RetroPoints => Points(PointsFamilies.RetroAchievements);

        /// <summary>The points card of <paramref name="family"/>; Collection when the family is blank.</summary>
        public static ScoreCardType Points(string family)
        {
            var normalized = PointsFamilies.Normalize(family);
            return normalized == null ? Collection : new ScoreCardType(Kind.Points, normalized);
        }

        public bool IsCollection => _kind == Kind.Collection;

        public bool IsPrestige => _kind == Kind.Prestige;

        public bool IsPoints => _kind == Kind.Points;

        /// <summary>The points family a points card sums; null for Collection and Prestige.</summary>
        public string PointsFamily => IsPoints ? _family : null;

        /// <summary>The persisted form: Collection, Prestige or Points:&lt;family&gt;.</summary>
        public string Key
        {
            get
            {
                switch (_kind)
                {
                    case Kind.Prestige:
                        return PrestigeKey;
                    case Kind.Points:
                        return PointsPrefix + _family;
                    default:
                        return CollectionKey;
                }
            }
        }

        /// <summary>
        /// Reads a persisted card. Besides <see cref="Key"/> it accepts the names and numbers of
        /// the enum this type replaced (Collection 0, Prestige 1, Gamerscore 2, EpicXp 3,
        /// RetroPoints 4), so cards saved by earlier versions keep their choice.
        /// </summary>
        public static bool TryParse(string raw, out ScoreCardType type)
        {
            type = Collection;
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            if (value.StartsWith(PointsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var family = PointsFamilies.Normalize(value.Substring(PointsPrefix.Length));
                if (family == null)
                {
                    return false;
                }

                type = Points(family);
                return true;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return TryFromLegacyName(LegacyNameOf(number), out type);
            }

            return TryFromLegacyName(value, out type);
        }

        public static ScoreCardType ParseOrDefault(string raw, ScoreCardType fallback) =>
            TryParse(raw, out var type) ? type : fallback;

        private static string LegacyNameOf(int number)
        {
            switch (number)
            {
                case 0:
                    return CollectionKey;
                case 1:
                    return PrestigeKey;
                case 2:
                    return "Gamerscore";
                case 3:
                    return "EpicXp";
                case 4:
                    return "RetroPoints";
                default:
                    return null;
            }
        }

        private static bool TryFromLegacyName(string name, out ScoreCardType type)
        {
            if (string.Equals(name, CollectionKey, StringComparison.OrdinalIgnoreCase))
            {
                type = Collection;
                return true;
            }

            if (string.Equals(name, PrestigeKey, StringComparison.OrdinalIgnoreCase))
            {
                type = Prestige;
                return true;
            }

            if (string.Equals(name, "Gamerscore", StringComparison.OrdinalIgnoreCase))
            {
                type = Gamerscore;
                return true;
            }

            if (string.Equals(name, "EpicXp", StringComparison.OrdinalIgnoreCase))
            {
                type = EpicXp;
                return true;
            }

            if (string.Equals(name, "RetroPoints", StringComparison.OrdinalIgnoreCase))
            {
                type = RetroPoints;
                return true;
            }

            type = Collection;
            return false;
        }

        public bool Equals(ScoreCardType other) =>
            _kind == other._kind &&
            string.Equals(PointsFamily, other.PointsFamily, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object obj) => obj is ScoreCardType other && Equals(other);

        public override int GetHashCode() =>
            ((int)_kind * 397) ^ (PointsFamily == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(PointsFamily));

        public static bool operator ==(ScoreCardType left, ScoreCardType right) => left.Equals(right);

        public static bool operator !=(ScoreCardType left, ScoreCardType right) => !left.Equals(right);

        public override string ToString() => Key;
    }

    public static class ScoreCardTypes
    {
        /// <summary>The level curve each card type climbs.</summary>
        public static AchievementLevelCurveSettings GetCurve(ScoreCardType type) =>
            type.IsPoints
                ? PointsFamilies.GetCurve(type.PointsFamily)
                : AchievementLevelCurveSettings.ModernDefault;

        public static AchievementLevelSnapshot Calculate(ScoreCardType type, int score) =>
            AchievementLevelCalculator.Calculate(score, GetCurve(type));
    }

    /// <summary>
    /// A score card slot: a <see cref="ScoreCardType"/>, or no card. Persisted as the card's
    /// <see cref="ScoreCardType.Key"/> or <see cref="NoneKey"/>.
    /// </summary>
    public static class ScoreCardSlots
    {
        public const string NoneKey = "None";

        public static readonly string Collection = ScoreCardType.Collection.Key;

        public static readonly string Prestige = ScoreCardType.Prestige.Key;

        /// <summary>
        /// The card a slot shows, or false for an empty slot. Accepts the names and numbers of
        /// the slot enum this replaced (None 0, Collection 1, Prestige 2, Gamerscore 3, EpicXp 4,
        /// RetroPoints 5).
        /// </summary>
        public static bool TryGetCardType(string slot, out ScoreCardType type)
        {
            type = ScoreCardType.Collection;
            var value = slot?.Trim();
            if (string.IsNullOrEmpty(value) ||
                string.Equals(value, NoneKey, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                // Slot numbers ran one ahead of the card type numbers, with None at 0.
                return number > 0 &&
                    ScoreCardType.TryParse((number - 1).ToString(CultureInfo.InvariantCulture), out type);
            }

            return ScoreCardType.TryParse(value, out type);
        }

        /// <summary>The persisted form of a slot: the card's key, or <see cref="NoneKey"/>.</summary>
        public static string Normalize(string slot) =>
            TryGetCardType(slot, out var type) ? type.Key : NoneKey;

        public static string ToSlot(ScoreCardType type) => type.Key;
    }
}

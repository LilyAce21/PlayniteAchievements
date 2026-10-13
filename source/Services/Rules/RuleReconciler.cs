using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Rules
{
    /// <summary>Field keys for values an automatic rule can own.</summary>
    public static class RuleFields
    {
        public const string ExcludedFromRefreshes = nameof(GameCustomDataFile.ExcludedFromRefreshes);
        public const string ExcludedFromSummaries = nameof(GameCustomDataFile.ExcludedFromSummaries);
        public const string CompletionStatus = "CompletionStatus";

        /// <summary>The stored text for a set exclusion flag.</summary>
        public const string True = "true";
    }

    /// <summary>
    /// What one reconcile step decided for a field: the value to write, if any, and the claim and
    /// pin the record should hold afterwards.
    /// </summary>
    public readonly struct RuleDecision
    {
        public RuleDecision(bool write, string value, RuleClaim claim, bool pinned, bool stateChanged)
        {
            Write = write;
            Value = value;
            Claim = claim;
            Pinned = pinned;
            StateChanged = stateChanged;
        }

        /// <summary>True when <see cref="Value"/> must be written to the field.</summary>
        public bool Write { get; }

        public string Value { get; }

        /// <summary>The claim the record holds afterwards, or null for none.</summary>
        public RuleClaim Claim { get; }

        public bool Pinned { get; }

        /// <summary>True when the stored claim or pin differs from what was passed in.</summary>
        public bool StateChanged { get; }

        public bool IsNoOp => !Write && !StateChanged;
    }

    /// <summary>
    /// The one rule for values that automatic rules write: a rule changes or restores only a value
    /// it wrote and nobody has touched since, and never a value the user set.
    /// </summary>
    public static class RuleReconciler
    {
        /// <param name="current">The field's value now, null when unset.</param>
        /// <param name="desired">The value the rules want, or null when no rule applies.</param>
        /// <param name="claim">The stored claim for this field, or null.</param>
        /// <param name="pinned">Whether the user pinned this field against the rules.</param>
        public static RuleDecision Decide(
            string field,
            string current,
            string desired,
            RuleClaim claim,
            bool pinned)
        {
            if (desired == null)
            {
                // The rules let go. Restore only an untouched value the rule wrote; a value someone
                // changed since stays as they left it.
                if (claim != null && Same(current, claim.Written))
                {
                    return new RuleDecision(!Same(current, claim.Prior), claim.Prior, null, false, true);
                }

                return new RuleDecision(false, null, null, false, claim != null || pinned);
            }

            if (pinned)
            {
                return new RuleDecision(false, null, null, true, claim != null);
            }

            if (claim == null)
            {
                return Same(current, desired)
                    // Already holds the value, so it is not the rule's to take back later.
                    ? new RuleDecision(false, null, null, false, false)
                    : new RuleDecision(true, desired, NewClaim(field, desired, current), false, true);
            }

            if (!Same(current, claim.Written))
            {
                // Changed outside the rule: whoever did it owns the value now.
                return new RuleDecision(false, null, null, true, true);
            }

            if (Same(current, desired))
            {
                return new RuleDecision(false, null, claim, false, false);
            }

            // The rule's own target moved (a different configured status): follow it, keeping the
            // value from before the rule first wrote.
            return new RuleDecision(true, desired, NewClaim(field, desired, claim.Prior), false, true);
        }

        public static RuleClaim FindClaim(GameCustomDataFile data, string field)
        {
            var claims = data?.RuleClaims;
            if (claims == null)
            {
                return null;
            }

            foreach (var claim in claims)
            {
                if (claim != null && string.Equals(claim.Field, field, StringComparison.Ordinal))
                {
                    return claim;
                }
            }

            return null;
        }

        public static bool IsPinned(GameCustomDataFile data, string field)
        {
            return data?.UserPinnedFields?.Contains(field) == true;
        }

        /// <summary>Stores the decision's claim and pin for the field on the record.</summary>
        public static void ApplyState(GameCustomDataFile data, string field, RuleDecision decision)
        {
            SetClaim(data, field, decision.Claim);
            SetPinned(data, field, decision.Pinned);
        }

        public static void SetClaim(GameCustomDataFile data, string field, RuleClaim claim)
        {
            if (data == null)
            {
                return;
            }

            data.RuleClaims?.RemoveAll(item => item == null || string.Equals(item.Field, field, StringComparison.Ordinal));
            if (claim != null)
            {
                (data.RuleClaims ?? (data.RuleClaims = new List<RuleClaim>())).Add(claim.Clone());
            }
        }

        public static void SetPinned(GameCustomDataFile data, string field, bool pinned)
        {
            if (data == null)
            {
                return;
            }

            if (pinned)
            {
                var fields = data.UserPinnedFields ?? (data.UserPinnedFields = new List<string>());
                if (!fields.Contains(field))
                {
                    fields.Add(field);
                }
            }
            else
            {
                data.UserPinnedFields?.Remove(field);
            }
        }

        /// <summary>
        /// Records a user's own write to a rule-owned field: the user owns it from now on, so any
        /// claim goes, and the pin keeps the rule off it while the rule still applies.
        /// </summary>
        public static void RecordUserWrite(GameCustomDataFile data, string field)
        {
            SetClaim(data, field, null);
            SetPinned(data, field, true);
        }

        public static string ToFlagText(bool? value) => value == true ? RuleFields.True : null;

        public static bool? FromFlagText(string value) => value == RuleFields.True ? true : (bool?)null;

        private static RuleClaim NewClaim(string field, string written, string prior)
        {
            return new RuleClaim { Field = field, Written = written, Prior = prior };
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}

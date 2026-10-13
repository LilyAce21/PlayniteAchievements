namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Records that an automatic rule wrote a game's value, so the rule can take that value back
    /// when it stops applying without ever touching a value the user set.
    /// </summary>
    /// <remarks>
    /// Values are stored as text so one shape serves every field: "true" for an exclusion flag,
    /// a status id for Playnite's completion status, and null for "unset".
    /// </remarks>
    public sealed class RuleClaim
    {
        /// <summary>The field the rule wrote, one of <c>RuleFields</c>.</summary>
        public string Field { get; set; }

        /// <summary>The value the rule wrote.</summary>
        public string Written { get; set; }

        /// <summary>The value before the rule wrote, restored when the rule lets go.</summary>
        public string Prior { get; set; }

        public RuleClaim Clone()
        {
            return new RuleClaim
            {
                Field = Field,
                Written = Written,
                Prior = Prior
            };
        }
    }
}

using PlayniteAchievements.Services.Estimation;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Settings for generated (estimated) PSN trophy grades and Xbox Gamerscore. Off by default.
    /// Estimated values never replace native data; see ESTIMATED_VALUES_DESIGN.md.
    /// </summary>
    public partial class PersistedSettings
    {
        public bool EstimatedValuesEnabled { get; set; } = false;

        public bool EstimatedValuesGenerateTrophies { get; set; } = true;

        public bool EstimatedValuesGenerateGamerscore { get; set; } = true;

        /// <summary>
        /// Research every achievement with Google Search before scoring it, so the model understands what each one
        /// really is. Uses extra requests and Gemini's search grounding, which has its own quota.
        /// </summary>
        public bool EstimatedValuesResearchEnabled { get; set; } = true;

        /// <summary>
        /// The Gemini model used for the research step, or blank to use the main model. Google Search grounding is not
        /// available on the free tier for Gemini 3 models, so a free key may need a different model here.
        /// </summary>
        public string EstimatedValuesResearchModel { get; set; } = string.Empty;

        /// <summary>Achievements per research request. Smaller is more thorough and uses more requests.</summary>
        public int EstimatedValuesResearchBatchSize { get; set; } = 25;

        /// <summary>How strictly the PSN point budget shapes grades: strict (Native) or a strong guide (Value-based).</summary>
        public BudgetPreset EstimatedValuesBudgetPreset { get; set; } = BudgetPreset.Native;

        /// <summary>Non-platinum trophy point budget (about 1,000). To be fitted from the user's native library.</summary>
        public int EstimatedValuesTrophyBudget
        {
            get => _estimatedValuesTrophyBudget;
            set
            {
                if (_estimatedValuesTrophyBudget == value) return;
                _estimatedValuesTrophyBudget = value;
                OnPropertyChanged(nameof(EstimatedValuesTrophyBudget)); // so "Fit from my library" updates the box
            }
        }

        private int _estimatedValuesTrophyBudget = 1000;

        /// <summary>Gemini model id. Stays configurable because lineups and quotas change.</summary>
        public string EstimatedValuesGeminiModel { get; set; } = "gemini-3.5-flash-lite";

        /// <summary>The Gemini API key, lightly obfuscated (see <see cref="SecretObfuscator"/>). Use EstimatedValuesConfig to read or set it.</summary>
        public string EstimatedValuesGeminiKey { get; set; } = string.Empty;

        /// <summary>Minimum seconds between requests, for the free tier's per-minute limit.</summary>
        public int EstimatedValuesRequestIntervalSeconds { get; set; } = 8;
    }
}

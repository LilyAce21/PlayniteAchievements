using System;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>Turns the persisted settings into the objects the estimation code uses, and handles the key.</summary>
    public static class EstimatedValuesConfig
    {
        public static string GetApiKey(PersistedSettings settings) =>
            SecretObfuscator.Reveal(settings?.EstimatedValuesGeminiKey);

        public static void SetApiKey(PersistedSettings settings, string plainKey)
        {
            if (settings == null) return;
            settings.EstimatedValuesGeminiKey = SecretObfuscator.Obfuscate(plainKey);
        }

        public static bool HasApiKey(PersistedSettings settings) => !string.IsNullOrWhiteSpace(GetApiKey(settings));

        public static string GetTavilyKey(PersistedSettings settings) =>
            SecretObfuscator.Reveal(settings?.EstimatedValuesTavilyKey);

        public static void SetTavilyKey(PersistedSettings settings, string plainKey)
        {
            if (settings == null) return;
            settings.EstimatedValuesTavilyKey = SecretObfuscator.Obfuscate(plainKey);
        }

        public static bool UsesWebSearchResearch(PersistedSettings settings) =>
            settings != null && settings.EstimatedValuesResearchEnabled && settings.EstimatedValuesResearchProvider == ResearchProvider.TavilyWebSearch;

        /// <summary>How the evidence gatherer searches, from the settings (limits are clamped to sane values).</summary>
        public static EvidenceOptions ToEvidenceOptions(PersistedSettings settings)
        {
            return new EvidenceOptions
            {
                MonthlyCreditBudget = Math.Max(0, Math.Min(100000, settings?.EstimatedValuesSearchCreditsPerMonth ?? 900)),
                MaxAchievementQueries = Math.Max(0, Math.Min(50, settings?.EstimatedValuesSearchesPerGame ?? 10))
            };
        }

        public static EstimationSettings ToEstimationSettings(PersistedSettings settings)
        {
            var result = new EstimationSettings();
            if (settings == null) return result;

            result.Preset = settings.EstimatedValuesBudgetPreset;
            result.TrophyBudget = Math.Max(100, Math.Min(5000, settings.EstimatedValuesTrophyBudget));
            return result;
        }

        public static EstimationRunOptions ToRunOptions(PersistedSettings settings, CalibrationProfile calibration = null)
        {
            return new EstimationRunOptions
            {
                Settings = ToEstimationSettings(settings),
                Calibration = calibration,
                GenerateTrophies = settings?.EstimatedValuesGenerateTrophies ?? true,
                GenerateGamerscore = settings?.EstimatedValuesGenerateGamerscore ?? true,
                UseResearch = settings?.EstimatedValuesResearchEnabled ?? true,
                ResearchBatchSize = Math.Max(5, Math.Min(50, settings?.EstimatedValuesResearchBatchSize ?? 25)),
                Model = settings?.EstimatedValuesGeminiModel
            };
        }

        /// <summary>The key is read through a callback at call time, so it is never held on a long-lived object.</summary>
        public static GeminiClientOptions ToClientOptions(Func<PersistedSettings> settingsAccessor)
        {
            var current = settingsAccessor?.Invoke();
            return new GeminiClientOptions
            {
                ApiKey = () => GetApiKey(settingsAccessor?.Invoke()),
                Model = string.IsNullOrWhiteSpace(current?.EstimatedValuesGeminiModel) ? "gemini-3.5-flash-lite" : current.EstimatedValuesGeminiModel.Trim(),
                ResearchModel = string.IsNullOrWhiteSpace(current?.EstimatedValuesResearchModel) ? null : current.EstimatedValuesResearchModel.Trim(),
                MinRequestInterval = TimeSpan.FromSeconds(Math.Max(0, Math.Min(120, current?.EstimatedValuesRequestIntervalSeconds ?? 8)))
            };
        }
    }
}

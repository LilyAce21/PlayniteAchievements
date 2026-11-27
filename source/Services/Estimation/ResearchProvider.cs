namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>Where research about a game's achievements comes from.</summary>
    public enum ResearchProvider
    {
        /// <summary>Gemini searches the web itself with Google Search grounding. Paid-tier only for Gemini 3 models.</summary>
        GoogleSearchViaGemini = 0,

        /// <summary>The plugin searches with Tavily's web search (free monthly allowance) and gives the findings to Gemini.</summary>
        TavilyWebSearch = 1
    }
}

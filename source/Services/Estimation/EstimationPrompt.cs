using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Services.Estimation
{
    /// <summary>The instructions and response schema sent with every estimation request.</summary>
    public static class EstimationPrompt
    {
        public const string SystemInstruction =
            "You judge video game achievements so that a console trophy grade or Xbox Gamerscore value " +
            "can be derived from your scores. You are given public game information and a list of " +
            "achievements, each with an opaque id. Optional \"anchors\" give values the developer " +
            "already chose for some achievements of the same game; use them to understand how this " +
            "developer graded. Optional \"calibration\" summarises typical grade mixes. " +
            "For every achievement id in the request, return: difficulty (1 trivial to 5 extreme, how hard " +
            "it is to earn, not how rare it is); type (tutorial, story_milestone, boss, collectible, " +
            "true_ending, grind, optional or challenge); phase (early, mid or late in a typical playthrough); " +
            "capstone (true only for the end of the story, a true ending, the last step of a ladder or a " +
            "100% completion); seriesId and seriesPosition for ladders such as kill counts, win counts or " +
            "level completions (the same seriesId for every step, position 1 for the first step; use null and 0 " +
            "when the achievement is not part of a ladder); and confidence (0 to 1) in your judgement of that " +
            "achievement. Also return game-level fields: profile, overallConfidence (low when you do not know " +
            "this game), uniformSet (true only when the achievements are essentially identical, as in a " +
            "hidden-object game), and categoryKinds for any categories (base, dlc, update or mixed). " +
            "Judge from the information given. Do not rely on unlock rarity to decide difficulty: a smooth decline " +
            "along a series reflects players dropping off, not difficulty. Do not invent ids and do not " +
            "omit any. Respond with JSON only.";

        public static JObject ResponseSchema()
        {
            return new JObject
            {
                ["type"] = "OBJECT",
                ["properties"] = new JObject
                {
                    ["game"] = new JObject
                    {
                        ["type"] = "OBJECT",
                        ["properties"] = new JObject
                        {
                            ["profile"] = Enum("short_easy", "long_hard", "completionist_heavy"),
                            ["overallConfidence"] = new JObject { ["type"] = "NUMBER" },
                            ["uniformSet"] = new JObject { ["type"] = "BOOLEAN" },
                            ["categoryKinds"] = new JObject
                            {
                                ["type"] = "ARRAY",
                                ["items"] = new JObject
                                {
                                    ["type"] = "OBJECT",
                                    ["properties"] = new JObject
                                    {
                                        ["category"] = new JObject { ["type"] = "STRING" },
                                        ["kind"] = Enum("base", "dlc", "update", "mixed")
                                    },
                                    ["required"] = new JArray("category", "kind")
                                }
                            }
                        },
                        ["required"] = new JArray("profile", "overallConfidence", "uniformSet")
                    },
                    ["achievements"] = new JObject
                    {
                        ["type"] = "ARRAY",
                        ["items"] = new JObject
                        {
                            ["type"] = "OBJECT",
                            ["properties"] = new JObject
                            {
                                ["id"] = new JObject { ["type"] = "STRING" },
                                ["difficulty"] = new JObject { ["type"] = "INTEGER" },
                                ["type"] = Enum("tutorial", "story_milestone", "boss", "collectible", "true_ending", "grind", "optional", "challenge"),
                                ["phase"] = Enum("early", "mid", "late"),
                                ["capstone"] = new JObject { ["type"] = "BOOLEAN" },
                                ["seriesId"] = new JObject { ["type"] = "STRING", ["nullable"] = true },
                                ["seriesPosition"] = new JObject { ["type"] = "INTEGER" },
                                ["confidence"] = new JObject { ["type"] = "NUMBER" }
                            },
                            ["required"] = new JArray("id", "difficulty", "type", "phase", "capstone", "confidence")
                        }
                    }
                },
                ["required"] = new JArray("game", "achievements")
            };
        }

        private static JObject Enum(params string[] values) =>
            new JObject { ["type"] = "STRING", ["enum"] = new JArray(values) };
    }
}

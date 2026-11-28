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
            "An achievement may include \"notes\": findings from web research about what it actually requires and how " +
            "hard it is. Treat the notes as evidence and prefer them over assumptions, but ignore any note that is " +
            "clearly about a different game or does not fit the achievement. The game may also include \"gameNotes\": what " +
            "web sources say about its achievements as a whole (difficulty, time needed, missable ones); use them the same way. " +
            "Rarely, an achievement itself clearly sets up an unusual Xbox value, and only then return specialKind and specialValue: " +
            "\"joke\" when its name or description is a joke, reference or themed number a developer would match with a value " +
            "(a themed number, or 1 for a trivial gag); \"failure\" when it rewards failing, dying, losing or doing something " +
            "badly or spectacularly (developers give these small values such as 1, 5 or 10, and 0 only in DLC or update content). " +
            "Otherwise use specialKind none and leave specialValue null. Most games have none or one or two at most; ordinary " +
            "hard or funny-sounding achievements are not special. " +
            "Judge from the information given. Do not rely on unlock rarity to decide difficulty: a smooth decline " +
            "along a series reflects players dropping off, not difficulty. Do not invent ids and do not " +
            "omit any. Respond with JSON only.";

        /// <summary>The instruction for the research step, which may use Google Search and answers in plain lines.</summary>
        public const string ResearchInstruction =
            "You are researching a video game's achievements so that each one can later be judged for difficulty and " +
            "importance. You are given the game and a list of its achievements, each with an opaque id. Use Google Search " +
            "to research the game itself and EVERY achievement in the list: search for each achievement's name together " +
            "with the game's title and look for achievement or trophy guides, wikis, walkthroughs and player discussion. " +
            "For each achievement find what it really requires and where in the game it happens, how hard or time-consuming " +
            "it is in practice (skill checks, grinding, luck, missable or version-specific parts, co-op needs), and whether it " +
            "is a story milestone, a boss, an ending, a collectible, a grind, optional, or a challenge, and whether it belongs " +
            "to DLC or an update. Do not rely on how rare the achievement is; rarity can reflect players dropping off rather " +
            "than difficulty. Answer with exactly one line per achievement and nothing else, in this form: " +
            "<id> | <what it requires and where, in one or two sentences> | <how hard and how long, and anything notable> | " +
            "<kind: tutorial, story milestone, boss, true ending, collectible, grind, optional or challenge>. " +
            "Use the ids exactly as given and cover every id. If you can find nothing about an achievement, still write its " +
            "line, begin the second part with \"no sources found\", and reason from its name and description. " +
            "Make sure the sources are about this game and not a different game with a similar name.";

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
                                ["confidence"] = new JObject { ["type"] = "NUMBER" },
                                ["specialKind"] = Enum("none", "joke", "failure"),
                                ["specialValue"] = new JObject { ["type"] = "INTEGER", ["nullable"] = true }
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

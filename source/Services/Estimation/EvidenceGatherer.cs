using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Estimation
{
    public sealed class EvidenceOptions
    {
        /// <summary>Searches for guide pages that cover the whole game (about three credits).</summary>
        public int MaxGameQueries { get; set; } = 3;

        /// <summary>Targeted searches for achievements the guides did not mention, most important first.</summary>
        public int MaxAchievementQueries { get; set; } = 10;

        public int MaxResultsPerQuery { get; set; } = 5;
        public int MaxPages { get; set; } = 10;
        public int PageCharacterLimit { get; set; } = 120000;

        /// <summary>The most credits to spend in a month. Zero or less means no limit.</summary>
        public int MonthlyCreditBudget { get; set; } = 900;
    }

    public sealed class EvidenceResult
    {
        /// <summary>True when searching could not be used at all (no key, rejected key, no credits left) and nothing was found.</summary>
        public bool Unavailable { get; set; }
        public string Reason { get; set; }

        /// <summary>The note for each achievement that evidence was found for, by api name (earlier findings included).</summary>
        public Dictionary<string, string> NotesByApiName { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>What the sources say about the game as a whole (difficulty, time, missables), or null.</summary>
        public string GameNotes { get; set; }

        public int Searches { get; set; }
        public int Credits { get; set; }
        public int Sources { get; set; }

        /// <summary>Achievements whose note came from the guide pages, and from targeted searches.</summary>
        public int FromGuides { get; set; }
        public int FromTargetedSearches { get; set; }

        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>Finds written evidence about a game's achievements. The model then scores with that evidence in hand.</summary>
    public interface IEvidenceSource
    {
        Task<EvidenceResult> GatherAsync(
            EstimationGameContext context,
            IDictionary<string, string> cachedNotes,
            ISet<string> cachedMisses,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Plugin-driven research. A few searches find guide pages for the game; the pages' text is read once and every
    /// achievement is looked up in it by name; the ones the guides skipped get a limited number of targeted searches.
    /// Everything found is returned as a note per achievement, and remembered so it is never paid for twice.
    /// </summary>
    public sealed class EvidenceGatherer : IEvidenceSource
    {
        /// <summary>The key under which search availability is remembered (see <see cref="ResearchAvailability"/>).</summary>
        public const string AvailabilityKey = "web-search";

        internal const int MaxNoteLength = 600;
        private const int MaxConsecutiveFailures = 3;

        private static readonly string[] PriorityWords =
        {
            "ending", "final", "boss", "secret", "hidden", "all ", "every", "100%", "complete", "hard", "nightmare",
            "without", "no damage", "perfect", "speedrun", "legendary", "master", "true "
        };

        private readonly ISearchProvider _provider;
        private readonly SearchUsageTracker _usage;
        private readonly EvidenceOptions _options;
        private readonly Action<string> _log;

        public EvidenceGatherer(ISearchProvider provider, SearchUsageTracker usage, EvidenceOptions options, Action<string> log = null)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _usage = usage;
            _options = options ?? new EvidenceOptions();
            _log = log ?? (_ => { });
        }

        public async Task<EvidenceResult> GatherAsync(
            EstimationGameContext context,
            IDictionary<string, string> cachedNotes,
            ISet<string> cachedMisses,
            CancellationToken cancellationToken)
        {
            var result = new EvidenceResult();
            var notes = cachedNotes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var misses = cachedMisses ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var achievements = (context?.Achievements ?? new List<PublicAchievement>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .ToList();

            // What an earlier run already found comes back for free.
            foreach (var a in achievements)
            {
                if (notes.TryGetValue(a.ApiName, out var note) && !string.IsNullOrWhiteSpace(note)) result.NotesByApiName[a.ApiName] = note;
            }

            if (notes.TryGetValue(ResearchNotesCache.GameNotesKey, out var cachedGameNotes)) result.GameNotes = cachedGameNotes;

            var pending = achievements.Where(a => !result.NotesByApiName.ContainsKey(a.ApiName) && !misses.Contains(a.ApiName)).ToList();
            if (pending.Count == 0) { LogUncovered(context.Title, achievements, result, misses); return result; }

            if (ResearchAvailability.IsUnavailable(AvailabilityKey, out var knownReason))
            {
                return Refused(result, knownReason);
            }

            var budgetLeft = RemainingBudget();
            if (budgetLeft <= 0)
            {
                return Refused(result, "This month's search credit budget (" + _options.MonthlyCreditBudget + ") is used up.", rememberRefusal: false);
            }

            var failures = 0;

            // ---- 1. guide pages that cover the whole game ------------------------------------------------------------
            var corpus = new List<CorpusPage>();
            notes.TryGetValue(ResearchNotesCache.RetryRoundKey, out var retryRoundText);
            var retryRound = int.TryParse(retryRoundText, out var parsedRound) ? parsedRound : 0;
            var alreadyDidGameSearches = notes.ContainsKey(ResearchNotesCache.GameSearchedKey);

            // A retry reads the guide pages again, once per retry round, when the earlier guide search found no usable page
            // (or was made before that was recorded). Everything already found is kept; this costs only the guide searches.
            if (alreadyDidGameSearches && retryRound > 0 && GuideSearchCameUpEmpty(notes, retryRound)) alreadyDidGameSearches = false;
            if (!alreadyDidGameSearches)
            {
                var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var query in GameQueries(context).Take(Math.Max(1, _options.MaxGameQueries)))
                {
                    if (budgetLeft <= 0) { result.Notes.Add("The monthly search budget ran out before every guide was read."); break; }
                    cancellationToken.ThrowIfCancellationRequested();

                    var response = await SearchAsync(query, includeRaw: true, _options.MaxResultsPerQuery, cancellationToken).ConfigureAwait(false);
                    result.Searches++;
                    if (!response.Success)
                    {
                        if (response.Failure == SearchFailureKind.Unavailable) return Refused(result, response.Error);
                        if (++failures >= MaxConsecutiveFailures) break;
                        continue;
                    }

                    failures = 0;
                    budgetLeft = Spend(response, result, budgetLeft);
                    foreach (var hit in response.Hits)
                    {
                        if (corpus.Count >= _options.MaxPages) break;
                        if (!seenUrls.Add(hit.Url)) continue;

                        var page = ToPage(hit, context.Title);
                        if (page != null) corpus.Add(page);
                    }
                }

                notes[ResearchNotesCache.GameSearchedKey] = "1";
                notes[ResearchNotesCache.GuidePagesKey] = corpus.Count + "@" + retryRound;
                result.Sources = corpus.Count;
                result.GameNotes = ExtractGameNotes(corpus) ?? result.GameNotes;
                if (!string.IsNullOrWhiteSpace(result.GameNotes)) notes[ResearchNotesCache.GameNotesKey] = result.GameNotes;
            }

            // ---- 2. find every achievement in those pages ------------------------------------------------------------
            var unmatched = new List<PublicAchievement>();
            foreach (var a in pending)
            {
                var passages = PassageMatcher.Find(corpus, a.DisplayName, a.Description, 2);
                if (passages.Count == 0) { unmatched.Add(a); continue; }

                var note = FormatNote(passages);
                result.NotesByApiName[a.ApiName] = note;
                notes[a.ApiName] = note;
                result.FromGuides++;
            }

            // ---- 3. a limited number of targeted searches for the rest, most important first ----------------------------
            var targeted = 0;
            foreach (var a in unmatched.OrderByDescending(Priority).ThenBy(a => a.ApiName, StringComparer.OrdinalIgnoreCase))
            {
                if (targeted >= Math.Max(0, _options.MaxAchievementQueries)) break;
                if (budgetLeft <= 0) { result.Notes.Add("The monthly search budget ran out before every achievement was searched."); break; }
                cancellationToken.ThrowIfCancellationRequested();

                var response = await SearchAsync(AchievementQuery(context, a, retryRound), includeRaw: false, 3, cancellationToken).ConfigureAwait(false);
                targeted++;
                result.Searches++;
                if (!response.Success)
                {
                    if (response.Failure == SearchFailureKind.Unavailable) return Refused(result, response.Error, keepWhatWasFound: true);
                    if (++failures >= MaxConsecutiveFailures) break;
                    continue;
                }

                failures = 0;
                budgetLeft = Spend(response, result, budgetLeft);

                var pages = response.Hits
                    .Select(hit => ToPage(hit, context.Title, snippetOnly: true))
                    .Where(p => p != null)
                    .ToList();
                var passages = PassageMatcher.Find(pages, a.DisplayName, a.Description, 2);
                if (passages.Count == 0) { misses.Add(a.ApiName); continue; }

                var note = FormatNote(passages);
                result.NotesByApiName[a.ApiName] = note;
                notes[a.ApiName] = note;
                result.FromTargetedSearches++;
                result.Sources++;
            }

            _log("[Research] " + context.Title + ": " + result.NotesByApiName.Count + " of " + achievements.Count + " achievements have evidence (" +
                 result.FromGuides + " from guides, " + result.FromTargetedSearches + " from targeted searches), " + result.Searches + " search(es), " +
                 result.Credits + " credit(s)" + (_usage != null ? "; " + _usage.UsedThisMonth + " used this month" : string.Empty) + ".");
            LogUncovered(context.Title, achievements, result, misses);
            return result;
        }

        // ------------------------------------------------------------------------------------------------ pieces

        /// <summary>
        /// Says which achievements still have no research, so it is clear what is missing: those searched with nothing
        /// found, and those not searched yet (the per-run search limit stops short of them). Names only, never
        /// anything about the player.
        /// </summary>
        private void LogUncovered(string title, IList<PublicAchievement> achievements, EvidenceResult result, ISet<string> misses)
        {
            var uncovered = achievements.Where(a => !result.NotesByApiName.ContainsKey(a.ApiName)).ToList();
            if (uncovered.Count == 0) return;

            var searchedEmpty = uncovered.Where(a => misses.Contains(a.ApiName)).ToList();
            var notSearched = uncovered.Where(a => !misses.Contains(a.ApiName)).ToList();
            string Names(IList<PublicAchievement> list) => string.Join("; ", list.Take(12).Select(a => a.DisplayName ?? a.ApiName)) +
                                                           (list.Count > 12 ? "; +" + (list.Count - 12) + " more" : string.Empty);

            _log("[Research] " + title + ": no evidence yet for " + uncovered.Count + " achievement(s). " +
                 "Not searched yet (" + notSearched.Count + ", the next generate or regenerate continues with these): " + (notSearched.Count == 0 ? "none" : Names(notSearched)) + ". " +
                 "Searched, nothing found (" + searchedEmpty.Count + "): " + (searchedEmpty.Count == 0 ? "none" : Names(searchedEmpty)) + ".");
        }

        private async Task<SearchResponse> SearchAsync(string query, bool includeRaw, int maxResults, CancellationToken cancellationToken)
        {
            return await _provider.SearchAsync(new SearchRequest { Query = query, MaxResults = maxResults, IncludeRawContent = includeRaw }, cancellationToken)
                .ConfigureAwait(false);
        }

        private int RemainingBudget()
        {
            if (_options.MonthlyCreditBudget <= 0) return int.MaxValue;
            return _options.MonthlyCreditBudget - (_usage?.UsedThisMonth ?? 0);
        }

        private int Spend(SearchResponse response, EvidenceResult result, int budgetLeft)
        {
            result.Credits += response.CreditsUsed;
            _usage?.Record(response.CreditsUsed);
            return budgetLeft == int.MaxValue ? budgetLeft : budgetLeft - response.CreditsUsed;
        }

        private EvidenceResult Refused(EvidenceResult result, string reason, bool rememberRefusal = true, bool keepWhatWasFound = false)
        {
            if (rememberRefusal) ResearchAvailability.MarkUnavailable(AvailabilityKey, reason);

            // If some evidence was found (earlier runs or this one), carry on with it; otherwise research is simply unavailable.
            if (result.NotesByApiName.Count == 0 && !keepWhatWasFound)
            {
                result.Unavailable = true;
            }

            result.Reason = reason;
            result.Notes.Add("Web search stopped: " + reason);
            _log("[Research] Web search is unavailable: " + reason);
            return result;
        }

        internal static IEnumerable<string> GameQueries(EstimationGameContext context)
        {
            var title = (context.Title ?? string.Empty).Trim();
            var year = context.ReleaseYear.HasValue ? " " + context.ReleaseYear.Value : string.Empty;

            yield return "\"" + title + "\"" + year + " video game achievements guide all achievements how to unlock";
            yield return "\"" + title + "\"" + year + " video game trophy guide roadmap difficulty hours to platinum";
            yield return "\"" + title + "\"" + year + " video game achievement list tips missable";
        }

        internal static string AchievementQuery(EstimationGameContext context, PublicAchievement a, int round = 0)
        {
            var title = (context.Title ?? string.Empty).Trim();
            var name = (a.DisplayName ?? string.Empty).Trim();
            var words = string.Join(" ", (a.Description ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(10));
            switch (round % 3)
            {
                case 1: // looser wording, nothing quoted, no description
                    return title + " " + name + " achievement how to unlock guide";
                case 2: // without the name, in case guides spell it differently
                    return "\"" + title + "\" achievement " + words;
                default: // game, exact name and what it asks for, so an obscure one is not drowned out by unrelated results
                    return ("\"" + title + "\" \"" + name + "\" achievement " + words).TrimEnd();
            }
        }

        /// <summary>Likely-important achievements first: hidden ones and ones whose wording suggests an ending, a boss or a hard challenge.</summary>
        internal static int Priority(PublicAchievement a)
        {
            var text = ((a.DisplayName ?? string.Empty) + " " + (a.Description ?? string.Empty)).ToLowerInvariant();
            return (a.IsHidden ? 3 : 0) + PriorityWords.Count(word => text.Contains(word));
        }

        /// <summary>
        /// A page counts only if it is about this game: the game's title must appear in its address, its title or its opening
        /// text. That keeps results for a different game with a similar name out of the notes.
        /// </summary>
        private CorpusPage ToPage(SearchHit hit, string gameTitle, bool snippetOnly = false)
        {
            var text = !snippetOnly && !string.IsNullOrWhiteSpace(hit.RawContent) ? hit.RawContent : hit.Snippet;
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (text.Length > _options.PageCharacterLimit) text = text.Substring(0, _options.PageCharacterLimit);

            var titles = TitleVariants(gameTitle);
            if (titles.Count > 0)
            {
                var head = PassageMatcher.Normalize(hit.Url + " " + hit.Title + " " + text.Substring(0, Math.Min(text.Length, 3000)));
                if (!titles.Any(t => head.Contains(t))) return null;
            }

            return new CorpusPage
            {
                Url = hit.Url,
                Title = hit.Title,
                Text = text,
                Domain = PassageMatcher.Domain(hit.Url),
                Weight = PassageMatcher.PageWeight(hit.Url, hit.Title)
            };
        }

        /// <summary>True when the last guide search read no usable page, or when nothing recorded how it went, and it has not been redone in this retry round.</summary>
        internal static bool GuideSearchCameUpEmpty(IDictionary<string, string> notes, int retryRound)
        {
            if (!notes.TryGetValue(ResearchNotesCache.GuidePagesKey, out var recorded) || string.IsNullOrWhiteSpace(recorded)) return true;

            var parts = recorded.Split('@');
            if (!int.TryParse(parts[0], out var pages) || pages > 0) return false;
            return !(parts.Length > 1 && int.TryParse(parts[1], out var round) && round >= retryRound);
        }

        private static readonly string[] EditionWords =
        {
            "definitive edition", "complete edition", "game of the year edition", "goty edition", "deluxe edition", "enhanced edition",
            "remastered", "remaster", "special edition", "ultimate edition", "anniversary edition", "directors cut", "gold edition"
        };

        /// <summary>
        /// The ways a guide might name the game: the full title, the part before a subtitle ("Ori and the Blind Forest" for
        /// "Ori and the Blind Forest: Definitive Edition"), and the title without edition words. Very short variants are
        /// ignored so a generic word cannot match the wrong game. Normalized for comparison.
        /// </summary>
        internal static List<string> TitleVariants(string gameTitle)
        {
            var variants = new List<string>();
            void Add(string value)
            {
                var normalized = PassageMatcher.Normalize(value);
                if (normalized.Length >= 4 && !variants.Contains(normalized)) variants.Add(normalized);
            }

            var title = (gameTitle ?? string.Empty).Trim();
            if (title.Length == 0) return variants;

            Add(title);
            var cut = title.IndexOfAny(new[] { ':', '\u2013', '\u2014' });
            if (cut < 0) cut = title.IndexOf(" - ", StringComparison.Ordinal);
            if (cut > 0) Add(title.Substring(0, cut));

            var stripped = PassageMatcher.Normalize(title);
            foreach (var word in EditionWords) stripped = stripped.Replace(word, " ");
            Add(string.Join(" ", stripped.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)));
            return variants;
        }

        private static string FormatNote(IList<Passage> passages)
        {
            var note = string.Join(" … ", passages.Select(p => (string.IsNullOrEmpty(p.Domain) ? string.Empty : "[" + p.Domain + "] ") + p.Text));
            return note.Length > MaxNoteLength ? note.Substring(0, MaxNoteLength).TrimEnd() + "…" : note;
        }

        private static readonly Regex DifficultyOrTime = new Regex(
            @"(\b\d{1,2}(\.\d)?\s*/\s*10\b)|(\b\d{1,3}(\s*-\s*\d{1,3})?\s*\+?\s*hours?\b)|(\bplaythroughs?\b)|(\bmissable\b)|(\bonline\b.*\btrophy|\bmultiplayer\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

        /// <summary>The few sentences in the guides that say how hard, how long or how missable the game's achievements are.</summary>
        internal static string ExtractGameNotes(IList<CorpusPage> corpus)
        {
            var lines = new List<string>();
            foreach (var page in corpus.OrderByDescending(p => p.Weight))
            {
                foreach (var raw in page.Text.Split('\n'))
                {
                    var line = Regex.Replace(raw, @"\s+", " ").Trim();
                    if (line.Length < 15 || line.Length > 240) continue;

                    bool matched;
                    try { matched = DifficultyOrTime.IsMatch(line); }
                    catch (RegexMatchTimeoutException) { matched = false; }
                    if (!matched) continue;

                    var tagged = "[" + page.Domain + "] " + line;
                    if (lines.Any(existing => existing.EndsWith(line, StringComparison.OrdinalIgnoreCase))) continue;
                    lines.Add(tagged);
                    if (lines.Count >= 4) break;
                }

                if (lines.Count >= 4) break;
            }

            if (lines.Count == 0) return null;
            var joined = string.Join(" | ", lines);
            return joined.Length > MaxNoteLength ? joined.Substring(0, MaxNoteLength).TrimEnd() + "…" : joined;
        }
    }
}

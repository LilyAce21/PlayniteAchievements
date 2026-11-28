# Estimated Trophy & Gamerscore Generation: build notes

Companion to the design spec (kept outside the repo as a PDF). This file records what is built on
`feature/estimated-trophy-gamerscore`, what I found in the codebase for the spec's section 12 open
items, the places where I had to make a call, and what comes next.

## Status

| Phase | Scope | State |
|---|---|---|
| 1 | Pure logic in `source/Services/Estimation/`: native-wins guard, value scoring, PSN grade estimator, Gamerscore estimator, privacy filter, request builder, calibration profile. Nothing is wired into the running plugin yet, so behaviour of the existing build is unchanged. | Done, 26 tests pass |
| 2 | Estimated data layer in the cache: table, guarded writes, resolver fallback, resolved views, "delete all generated data" | Built; SQL and resolver verified here, the plugin itself needs a Windows build. No UI yet for the delete action |
| 3 | Gemini client, pacing/retry/backoff, JSON validation, structure-based fallback | Built; tested with a fake HTTP layer only, never against the live API (needs a key) |
| 4a | Planner and runner (what may be generated per game, anchors from each row's own native values, end to end through the model and estimators), settings fields, obfuscated key | Built and tested with a fake model; not yet triggered from anywhere in the plugin |
| 4b | Settings card, Generate / Regenerate / Delete buttons, estimated marker, purge before runs | Built (UI is XAML and not built or seen here; needs a Windows build and a look). Per-game menu entries added |
| 5 | Blind validation run on the user's own native lists (spec section 11) | Not started |

Tests: `tests/PlayniteAchievements.Tests/Services/Estimation/EstimationTests.cs`. The engine was
compiled and the tests run on Linux/.NET 8 with a stand-in for MSTest. The WPF plugin project and
the repo's own test project target .NET Framework 4.6.2 and have not been built in this environment,
so they need a build on Windows. The new files use only features the existing code already uses.

## What "native" means (confirmed)

Anything PA already holds for a slot counts as native and is never overwritten: the platform's own
values (PlayStation, Xbox, Steam and so on), what metadata sources supplied (Exophase, SteamHunters,
TrueGaming), and the user's own overrides. A slot a source or the user deliberately cleared also stays
empty. Only a slot nothing supplies can hold an estimate.

## Phase 2: the estimated layer

- `AchievementEstimatedLayer` table (`Services/Database/AchievementEstimatedLayerSql.cs`), created
  on start like the metadata layer. No schema version bump, so no cache rebuild. Holds only
  Points (estimated Gamerscore) and TrophyType (estimated grade), plus reason, confidence, model and time.
- Three guards in the data layer: every write statement skips a slot another layer supplies;
  readers fall back to the estimate only when no layer supplies the slot; `PurgeSuperseded` drops an
  estimate once native data arrives. A trophy type other than bronze/silver/gold (platinum) is never stored.
- Resolved views (`AchievementDefinitions`, `ResolvedAchievements`) show estimated values in the
  same Points / TrophyType columns, and gain two columns, `PointsIsEstimated` and `TrophyTypeIsEstimated`,
  for the estimated marker. Existing columns are unchanged. Summaries read these views, so
  estimated values count toward trophy and points totals (confirmed with the user). No totals are
  stored anywhere: they are computed on read, so deleting the estimated data removes them from totals
  at once (tested), and clearing also resets the in-memory copies and tells the UI to reload.
- In memory, `AchievementDetail.EstimatedLayer` plus two flags; `AchievementLayers.Resolve` applies
  the fallback on every resolve, and an estimate can never be captured as a platform value.
- Roll-back: `CacheManager.ClearEstimatedValues()` deletes the whole table and nothing else. Clearing
  the platform cache also clears it. A settings button for it comes with the UI phase.

Verification here: the SQL ran against SQLite fixtures (precedence, cleared slots, user overrides,
native arrival, clear-all, delete trigger); both resolved views compile and return the right values;
the real resolver files compile and pass scenario tests; the new writer compiles against the real
SqlNado library. Not verified here: the full plugin build and the repo's own test project (Windows).

## Phase 3: the Gemini client

- `GeminiClient`: POST to `generativelanguage.googleapis.com/v1beta/models/{model}:generateContent`,
  key in the `x-goog-api-key` header (never the URL), JSON-only structured output with a response
  schema. Default model `gemini-3.5-flash-lite`, configurable. Google has been pointing new projects
  to the 3.5 Flash-Lite and 3.8 Flash line; 2.5 models can return 404 for new keys, and the client
  says so in plain words when that happens. Verify the current lineup and limits in AI Studio.
- Paced (8 s between requests by default, for free-tier per-minute limits), retried with backoff and
  Retry-After on 429 and server errors, not retried on bad key / unavailable model / blocked request.
  Errors shown to the user are scrubbed of the key.
- The request is checked against the privacy allowlist a second time just before sending.
- `LlmScoringService`: one request per game, 100 achievements per chunk. A network, quota, key or model
  problem is a failure that generates and caches nothing, so it can be retried later. A model that
  answered unusably (not JSON, truncated, missing entries) falls back to `StructureFallback`, which
  scores from list position and numbers in names (ladders), at low confidence so the estimators lean on
  structure and budget.
- Free tier note: Google may use free-tier content to improve its products. Only public game-level
  data is ever sent, but this is why the allowlist matters.
- Not decided yet: where the API key is stored (settings UI phase).

## Phase 4a: planner, runner, settings

- PA keeps one resolved achievement list per game, with cross-platform data already merged onto each row
  by the metadata overlay (Xbox points, PSN trophy types). So the anchors are simply each row's own native
  Points and TrophyType; no second lookup is needed. A value that is itself an estimate is never counted as native.
- `EstimationPlanner` decides, per game, which slots may be generated into:
  - platinum rows are excluded from everything (never graded, budgeted, anchored or sent);
  - only bronze, silver and gold count as grade anchors. Other providers reuse the trophy field for their own
    values (RetroAchievements, GameJolt), and where one of those is present the trophy slot is left alone;
  - native Points count as Gamerscore only for Xbox-family providers. Where a game has native Points of another
    kind (for example RetroAchievements points), no Gamerscore is generated next to them. A native 0 still
    occupies the slot. A PlayStation game has no native Points, so Gamerscore can be generated for it.
- `EstimationRunner`: plan, ask the model (public facts and anchors only), grade, build rows, and strip any value
  for a slot with native data one more time before returning rows. A model that cannot be reached returns Failed and generates nothing.
- Settings (off by default): enable, trophies on/off, Gamerscore on/off, budget preset, trophy budget, Gemini model,
  Gemini key, seconds between requests. Registered in the settings copy and clone, which a reflection test checks.
- The API key is stored lightly obfuscated, not encrypted (the owner's choice for a free-tier key). It is not
  hidden from anyone who reads the code or the settings file's source, and it would be included if settings are
  exported. It is never put in a URL, a log line or an error message.

### Points cards are per provider (decided: leave as is)

PA adds a game's Points to the points card of its provider (`PointsFamilies`): an Xbox game to Gamerscore, a Steam
game to "Steam Points", an Epic game to Epic XP. An estimated Gamerscore on a non-Xbox game therefore counts toward
that provider's own card. The owner decided to leave this alone: those cards already hold the points fetched for
non-Xbox games, and turning estimates into real Gamerscore would be confusing. Estimated points are not routed to
the Gamerscore card.

## Phase 4b so far: running it

- `EstimationCoordinator` runs one game or the whole library, one game at a time (the client paces requests).
  Results are kept permanently: a kind (trophies or Gamerscore) is skipped when every target already carries an
  estimate, and regenerated together when any target is new, so a list stays graded as one. A regenerate option
  redoes everything that has no native data.
- A run stops early on a problem every remaining game would share (missing or rejected key, unavailable model,
  used-up quota, no network); games already finished are kept. A refusal of one game's content is that game's
  problem alone and does not stop the run.
- `CacheEstimationHost` connects the coordinator to the cache.
- Settings, General tab, "Estimated trophy grades and Gamerscore" card: master switch, trophy and Gamerscore
  switches, budget style, trophy budget, Gemini model, masked API key, seconds between requests, and buttons:
  Generate for all games, Regenerate all, Delete all generated values. Runs show Playnite's cancelable progress
  window and finish with a summary. Superseded estimates are purged before each run.
- Marker: estimated points show with a leading "\u2248" in the achievement grid, and an estimated trophy grade's
  tooltip says "(estimated)" (never while a hidden grade is masked). The display item also exposes
  `IsPointsEstimated` and `IsTrophyTypeEstimated` for themes.
- Game menu (single game or a multi-selection): "Generate estimated trophies and Gamerscore" and "Regenerate ...".
  Shown only while the feature is switched on. Generate keeps earlier results; Regenerate redoes everything that has
  no native data.

## What is sent about the game itself

For the game being estimated: its title, release year and genre, and for every achievement its name, description,
hidden flag, category and rarity, plus any native values for the same game (as anchors). The title is sent because it
is the main thing the model uses to recognise the game. What is not sent: anything about the player (unlocks, dates,
profile, keys), and the titles of any other game in the library; the calibration examples are anonymous counts.

## Calibration (spec sections 5 and 11)

`CalibrationProfile` summarises the user's own native lists, computed locally once per run:

- grade mix and average points by list size (1-12, 13-30, 31-65, 66-100, 101+ trophies);
- how DLC and update packs split a game's points (average share of points and trophies in those categories);
- Gamerscore by list size (average total, median value, share of capstone spikes);
- the three closest real lists to the game being estimated, as anonymous examples.

Which lists count: trophy lists only from PSN-family providers (PSN, RPCS3, ShadPS4, Vita3K), since other providers
reuse the trophy field for their own tiers; only complete lists, where every non-platinum achievement has a native
bronze, silver or gold, so partial lists never skew the mix; at least 3 trophies. Gamerscore lists are complete
Xbox-family lists with a native value on every achievement.

Privacy: the request carries summary statistics only. The "closest lists" are size and grade counts, never a title,
so nothing about which games the user owns is sent. The profile is skipped quietly if the library cannot be read.

"Fit from my library" (next to the trophy budget): shows the median points of complete native lists with 5 to 45
trophies (needs at least 5) and applies it only if the user agrees. This is the fitting spec section 11 asks for,
for the budget figure; the fade between 65 and 200 trophies and the grade thresholds still need the blind
validation run on real data.

## Rules added after the first real runs

**Gamerscore needs at least 10 achievements.** Xbox requires a minimum of 10 achievements per title, and a smaller list
could not reach a 1,000 G block anyway, so a list with fewer than 10 achievements (platinum not counted) never gets
Gamerscore. Trophy grades are still generated for it. Enforced in the planner (with a note) and again in the
Gamerscore estimator itself. The 10 is `EstimationSettings.GamerscoreMinAchievements`, to be re-verified against
Microsoft's current rules.

**Value-based preset reworked: the budget is a strong guide, at every list size.** Native stays strict. Value-based
still aims for the budget (the same curve, a tighter-than-before 8% band) but lets clear cases keep their grade:
- a clearly deserved grade is kept even when the budget would change it: a gold with a stated reason well above the
  gold cut, a silver well above the silver cut, a clearly trivial bronze (never one with a stated gold reason);
- only the borderline achievements are moved toward the budget;
- the exceptions are limited. If keeping them would leave the total more than 25% above the budget, the least clear
  silvers and golds are handed back to it. If the list would sit more than 25% below, the least clear silvers are handed
  back so its strongest can be lifted, and golds without a stated reason are allowed, but only as many as it takes to get
  back within the limit. Clear bronzes are never lifted.
- gold still needs a stated reason except in that last case.
Example, 12 achievements (six trivial, six strong): strict gives gold to almost everything (960 points); value-based keeps
the trivial ones bronze, gives gold to the stronger half, and lands at 705. The 25% and the clear margin (0.08 past a
cut) are starting values for the validation run.

**Research each achievement with web search.** Before scoring, a research step asks Gemini to use Google Search on the
game and on every achievement (name plus game title: guides, wikis, walkthroughs) and report, one line per achievement,
what it requires, how hard it really is, and what kind it is. The notes are attached to each achievement in the scoring
request, which stays strict JSON with no tools.
- Two steps on purpose: search and strict JSON output have not always worked together on Gemini (grounding was
  reported silently switching off; Google says it is fixed), so the research step answers in plain lines and the scoring
  step never depends on it.
- Batches of 25 achievements (setting) so each one gets looked at; one retry for any the model skipped.
- Verifiable: the log reports how many web searches and sources each research call used, and the per-game line says how
  many achievements were researched. A warning is logged if no searches were reported.
- An achievement that could not be researched has its confidence capped at 0.5, so the estimators lean on structure.
- Rarity is left out of the research request so it cannot colour what is found.
- A quota, key or network failure in research fails the game like any other model failure (nothing generated, retry later).
- Cost: more requests per game, and search grounding has its own quota and may be billed separately from plain
  generation. The setting can be switched off. What is searched for: the game title and achievement names and
  descriptions only.
- Whether Gemini really searched each achievement individually cannot be guaranteed; the counts are the evidence.

## Rate limits (429) with research on

Research multiplies the requests per game (about one per 25 achievements, plus a retry for skipped ones, plus scoring),
and search-grounded calls can have tighter limits than plain ones. The first version only read the standard
`Retry-After` header, but Gemini puts the wait in the error body, so it retried after 2, 4 and 8 seconds, used up every
attempt within about 15 seconds against a limit that resets per minute, and failed. Now:
- the wait Gemini names (`retryDelay`, or "retry in Ns" in the message) is honoured, plus a second of margin; 6 attempts;
- the log names the quota that was hit and its limit (Google's quota id and value, never the key) so the real limits can
  be seen rather than guessed;
- a per-day quota is recognised and not retried (it cannot help); the message says to try tomorrow, switch model or turn
  research off;
- adaptive pacing: after a rate limit the gap between requests grows (15s, 30s, up to 60s) and eases off as requests
  succeed; research requests are always at least 15s apart;
- research notes already gathered are kept in memory for the session, so an interruption on batch 3 does not make the
  next attempt pay for batches 1 and 2 again; cleared when a game is regenerated or all generated data is deleted.
If it still hits limits: raise "Seconds between requests", raise "Achievements per research request", or turn research
off. Quota ids in the log tell which limit it is.

## Why research kept returning 429: search grounding is not on the free tier for Gemini 3

The real cause of the research 429s was not request rate. Google's pricing page lists "Grounding with Google Search" as not
available on the free tier for the Gemini 3 models (including 3.5 Flash-Lite); it is a paid-tier feature there (a monthly
allowance, then per-query pricing, with a daily cap). The signature in the log: an immediate 429 on the very first research
request, and the same answer on every retry for minutes. Google lists free search grounding (500 requests a day) for Gemini 2.5
Flash and Flash-Lite, but newer keys are sometimes refused 2.5 models (404), so that route is not guaranteed.

This conflicts with the free-models-only rule when research is on and the model is a Gemini 3 one, so research is now best effort:
- Research gives up fast: two 429s in a row with no earlier success, an explicit zero limit, or a 404/403 means "research is
  not available for this key and model". The game is then scored without research (no confidence penalty, since nothing was
  missed), the log and summary say so, and research is skipped for 4 hours before being tried once more. One success clears it.
  Regenerate also clears it. A genuine rate limit (research has worked before) is still retried and paced.
- "Research model" setting: research can use a different model from scoring (for example gemini-2.5-flash-lite). Availability is
  tracked per model, so refusing the research model never blocks scoring.
- Research and plain requests now have separate pacing, so a search problem cannot slow scoring.
- The log now carries Google's own error text when a 429 has no quota fields ("no detail given" is gone).

## Research with free web search (Tavily) instead of Gemini's Google Search

Google Search grounding is paid-only for Gemini 3 models, so research now defaults to plugin-driven web search. The plugin
searches itself and hands the findings to Gemini as notes; Gemini only scores, so requests per game are back to one.

Why this rather than Gemini function calling or URL Context: function calling would make Gemini run its own search loop, several
Gemini requests per game, which is what hits free-tier request limits. URL Context fetches pages Gemini is given the addresses
of; it does not search. Plugin-driven search is deterministic, cheap on Gemini, and can be cached.

How it works (`EvidenceGatherer`):
1. Up to three searches per game for guide pages (achievement guides, trophy roadmaps with difficulty and hours, tip pages), each
   asking for the full page text. A page only counts if it is about this game (the title appears in its address, title or opening
   text), so a different game with a similar name is dropped.
2. Every achievement is looked up by name in those pages (`PassageMatcher`: tolerant of punctuation and spacing; short generic
   names find nothing rather than something misleading). One guide usually covers dozens of achievements, which is what stretches
   the free credits.
3. Achievements the guides skipped get targeted searches, up to a per-game limit (default 10), most likely important first
   (hidden, endings, bosses, hard challenges). Ones that find nothing are remembered so they are not searched again.
4. Game-level notes (stated difficulty, hours, playthroughs, missables) are pulled from the guides and sent as `gameNotes`.
5. Each achievement's note (up to two passages with the site named, 600 characters at most) goes in the scoring request as `notes`.
   Achievements with no evidence keep Gemini's own knowledge and are trusted slightly less (confidence capped at 0.7).

Tavily (`TavilySearchClient`): `POST https://api.tavily.com/search`, key in an Authorization header, basic depth (one credit). Free
plan: 1,000 credits a month, no card. Per-game cost is about 3 credits plus the targeted searches, so very roughly 60 to 150
games a month. Exa (about $10 of free credit) and Serper (2,500 one-time queries) are alternatives if a second provider is wanted;
the code has an `ISearchProvider` interface for that. Brave now requires a card.

Spending: a monthly credit budget (default 900) is tracked in a small file and shown in the run summary; the run stops searching at
the budget. Findings are saved per game in the plugin's data folder (`EstimatedValuesResearch`), so research is paid for once:
- Generate keeps earlier results, and anything new is researched only for the new achievements.
- Regenerate redoes the grading on the saved research and spends no search credits. (An earlier version wiped the game's research on
  Regenerate; that was changed.)
- "Delete all generated values" removes the generated trophy grades and Gamerscore only; saved research is kept, so generating again
  is free. (An earlier note here claimed it also deleted research. It did not, and now it deliberately does not.)
- "Delete saved research" is the separate action that removes the saved findings. After it, the next generation researches from
  scratch and spends credits. The monthly credit count is not reset, because those credits were really spent.
- The same action exists per game: the game right-click menu (single or multi-select) has "Delete saved research for estimated
  trophies and Gamerscore". It confirms first, removes only the selected games' saved findings, and leaves their generated values alone.
If the key is missing or rejected, or credits run out, the game is scored without research (no confidence penalty), the log says so,
and it is tried again after a few hours (Regenerate also tries again at once).

Privacy: only the game title and achievement names (public) go to Tavily. Nothing about the player. The key is lightly obfuscated like
the Gemini key and goes only in a header. The Google Search via Gemini route remains as a setting for paid keys.

## Findings for spec section 12 (open items)

**How PA stores native PSN/Xbox data.** SQLite through SqlNado. The platform's own values live in
`PlatformDefinitions`. Metadata sources (Exophase, SteamHunters, TrueGaming) write to
`AchievementMetadataLayer`. The user's overrides are a third layer. `LayerResolver` resolves each
field as custom over metadata over platform (`Services/Achievements/LayerResolver.cs`). `Points`
and `TrophyType` are already fields in every layer, and the `AchievementDefinitions` /
`ResolvedAchievements` SQL views expose resolved values to other add-ons.

**Where the estimated store and flag should live.** A fourth, lowest layer: an
`AchievementEstimatedLayer` table beside `AchievementMetadataLayer`, resolved below the platform
layer. That makes "native always wins" a property of the resolver rather than of the UI, and the
existing pattern (`ClearMetadataLayers`) shows how a delete-all action stays isolated: it is a
`DELETE` on one table that holds nothing native. Because `Points` holds Gamerscore and
`TrophyType` holds the trophy grade, an estimated trophy on an Xbox game and an estimated
Gamerscore on a Steam game both fit without new concepts.

**Category data.** `Category`, `CategoryKind` and `Tags` exist per layer; `PsnTrophyCategoryHelper`
already derives PSN groups. Enough to fill the request's category block. Calibrating DLC budget
shares from native examples still needs a closer read.

**Cross-platform matching key.** `Services/Metadata/CrossSourceAchievementMatcher.cs`: optional exact
ApiName pass, then normalized title, ties broken by description / trophy type / points, a tie
leaves the achievement unmatched, each source row used once. This is the "skip if uncertain" rule
the spec asks for, so Phase 4 reuses it rather than adding a second matcher.

**Settings UI and estimated marker.** Not located yet (`Views/Settings`, `Models/PluginSettings.cs`,
`Models/Settings/PersistedSettings*.cs` are the candidates).

**Re-verify before hardcoding.** The 15/30/90 trophy points, the 1,000 budget, the Xbox 1,000 G /
100 achievements / 5 to 200 G limits, and Gemini free-tier models are all settings or constants in
`EstimationSettings`, none hardcoded deep in the logic. They still need a check against current
rules, as the spec says.

## Calls I made where the spec was ambiguous (please confirm)

1. **Budget versus "gold needs a reason", small lists (confirmed).** In the Native preset a small
   list is pulled toward about 1,000 points, which for a 12-trophy list means nearly everything is
   gold. Golds go to the best-justified trophies first (capstone, true ending, hard challenge, then
   by value), and the budget may still promote one without a stated reason when the list needs it.
   In the Value-based preset and in large lists, gold still needs a reason.
2. **The 65-trophy handover (confirmed, then reworked).** Taken literally, "target = max(1,000,
   15 x count)" makes a 67-trophy list all bronze, and my first version switched the budget off there,
   which made the mix jump. It is now one continuous curve: the native budget for small lists, and
   for longer ones the all-bronze floor plus a headroom (35% at 65 trophies) that fades smoothly to
   nothing by 200. The mix is scaled to that target keeping its shape, so there is a real middle of
   silvers. The numbers (35%, 65, 200, cuts 0.45 / 0.75) are starting values for the validation run.
3. **Large lists (confirmed).** From 100 trophies on, gold is reserved for capstones (end-of-story
   beats), true endings and the last step of a ladder; a challenge only counts at the extreme end
   of difficulty (5).
4. **Gamerscore blocks.** Chunked in list order, 100 per block, 1,000 G each. A very small final
   block still receives up to 1,000 G, capped at 200 per achievement, so a lone 701st achievement
   would get 200 G. Category-aware blocks (a DLC pack as its own block) are the better fix and
   belong with Phase 4.
5. **Opaque ids.** Requests send "a0001"-style ids, not real api names; the map back stays local.

## Privacy

`PrivacyFilter`: the request is built from an allowlisted set of public fields (title, year, genre,
achievement name/description/hidden flag/category/rarity, native anchors, calibration summary).
There is no unlock state, date or profile field on the input types. Text is scrubbed for emails,
bearer tokens, `password=`/`token=`-style assignments, Windows user paths and long token-like
strings, and the finished JSON is rejected if it contains any key outside the allowlist.

Still to decide for Phase 4: where the Gemini API key is stored. It must never enter a request, a
log line or the exported settings.

## Gamerscore common values, special values, metadata calibration, research follow-ups
- Gamerscore prefers the values developers use most (5, 10, 15, 20, 25, 30, 40, 50, 75, 100, 150, 200). Each ideal value snaps to the
  nearest common value, then the exact block total is repaired by moving whole values along that ladder; another multiple of 5 is used
  only when no mix can reach the total, and never on a milestone if avoidable. Uniform sets still put their leftover on the milestone.
  `GamerscorePreferCommonValues` switches back to plain rounding.
- Rare special values: the model may flag an achievement as a joke/themed number or a failure moment with a proposed value. Code decides:
  never a capstone, true ending or ladder step, never in uniform or low-confidence games, only when the proposal is confident, at most one
  per 50 achievements in a block. Failure is 1-10 G (0 G only inside DLC or update categories); a joke is 1-100 G. An odd value is paired
  with one balancing value (for example 9 G beside 1 G) so the block still sums exactly. `GamerscoreAllowSpecialValues` turns it off.
- Calibration ("fit from my library" and the Gamerscore profile) now also counts games whose official PSN grades or Xbox Gamerscore come
  from the PlayStation or Xbox metadata source, not only games serviced by those platforms. Only those two sources supply those fields.
- The log now lists which achievements still have no research (not searched yet vs searched, nothing found). A new game menu entry,
  "Retry missed research and regenerate", forgets only the "nothing found" marks, keeps everything found, then regenerates.
- Special values, tightened: several may share a block (never more than a tenth of it), but each needs the model's own certainty
  (specialConfidence >= 0.95), scoring confidence >= 0.9 and game confidence >= 0.8. If the achievement has a native PlayStation trophy,
  the special value must agree with it (a bronze and 10 G or less); otherwise it is not treated as special and is valued normally,
  anchored to that grade. A generated 0 G now shows a visible 0; a missing value stays empty. A native 0 still renders blank, because
  providers also store 0 for "no points" and the two cannot be told apart.
- Research reporting: the end-of-run dialog now lists, per game (first five, the log has every game), how many achievements have written
  evidence, which are missing, and which websites the evidence came from with how many achievements each supplied. The same line goes to the log.
- Targeted searches now carry the game, the exact achievement name and the start of its description; retries vary the wording each round (name + description, loose wording, description without the name). The dialog shows evidence counts only, no achievement names (the log still lists them).
- Guide pages are accepted when they name the game by its full title, the part before a subtitle, or the title without edition words
  ("Ori and the Blind Forest" for "Ori and the Blind Forest: Definitive Edition").
- "Retry missed research and regenerate" only regenerates a game when the retry found evidence it did not have before. If nothing new
  turns up (or search is unavailable), no Gemini request is made and the existing values are left exactly as they are; the game is reported as skipped.

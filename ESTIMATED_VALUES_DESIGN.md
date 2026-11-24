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
| 4b | Settings card, Generate / Regenerate / Delete buttons, estimated marker, purge before runs | Built (UI is XAML and not built or seen here; needs a Windows build and a look). Still to do: calibration profile from the user's native lists, per-game menu action |
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
- Still to do: the calibration profile (grade mix by list size from the user's native lists), and a per-game
  "Generate" menu entry.

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

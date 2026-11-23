# Estimated Trophy & Gamerscore Generation: build notes

Companion to the design spec (kept outside the repo as a PDF). This file records what is built on
`feature/estimated-trophy-gamerscore`, what I found in the codebase for the spec's section 12 open
items, the places where I had to make a call, and what comes next.

## Status

| Phase | Scope | State |
|---|---|---|
| 1 | Pure logic in `source/Services/Estimation/`: native-wins guard, value scoring, PSN grade estimator, Gamerscore estimator, privacy filter, request builder, calibration profile. Nothing is wired into the running plugin yet, so behaviour of the existing build is unchanged. | Done, 26 tests pass |
| 2 | Estimated data layer in the cache (table, resolver, views), "delete all generated data" maintenance action | Not started |
| 3 | Gemini client, queue/retry/backoff, JSON validation, structure-based fallback | Not started |
| 4 | Gathering native anchors and categories from PA, settings UI (preset, model name, key), estimated marker in the UI | Not started |
| 5 | Blind validation run on the user's own native lists (spec section 11) | Not started |

Tests: `tests/PlayniteAchievements.Tests/Services/Estimation/EstimationTests.cs`. The engine was
compiled and the tests run on Linux/.NET 8 with a stand-in for MSTest. The WPF plugin project and
the repo's own test project target .NET Framework 4.6.2 and have not been built in this environment,
so they need a build on Windows. The new files use only features the existing code already uses.

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

1. **Budget versus "gold needs a reason", small lists.** In the Native preset a small list (under
   about 67 trophies) is pulled toward about 1,000 points, which for a 12-trophy list means nearly
   everything is gold. The budget may therefore promote a trophy to gold without a stated reason
   (its reason is recorded as "budget"). In the Value-based preset, promotion to gold still needs a
   reason.
2. **The 65-trophy handover.** Taken literally, "target = max(1,000, 15 x count)" makes a 67-trophy
   list all bronze (15 x 67 = 1,005), which contradicts "bronze-dominant". I read it as: once the
   floor exceeds the budget, the budget stops constraining, and the silver and gold cuts tighten
   smoothly from 65 to 200 trophies. The numbers (0.45 / 0.75 cuts, tightening 0.20 / 0.15) are
   starting values for the validation run to fit.
3. **Large lists.** From 100 trophies on, gold is reserved for capstones, true endings and the last
   step of a ladder; "hard challenge" no longer earns it. This implements the spec's "gold only for
   true capstones" for Geometry Dash / Cookie Clicker style lists. Found by a failing test.
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

# Fork maintenance bundle

## v4.1.1 integration - 2026-10-20

The first settings tab is Santodan-Fork, containing General, Platforms,
Achievement Notification, and Theme Migration. General hosts cached-unlock
protection and the log tools; upstream General/Maintenance retains its upstream
content. Fork Platforms hosts Local and Steam's Secondary settings, while the
original Steam settings remain under the main Platforms tab. Local's General,
Import, and SuccessStory tabs use settings, download, and trophy icons; Secondary
uses an accounts icon. Steam import is a full-width section below Active account
with the same heading style. Release rebuilds and layout checks passed; live
verification of the final import layout remains pending.

The migration pages under Display and Themes use the upstream v4.1.1 layout,
retaining the two-way Revert selection fix. Fork migration controls remain in the
separate Theme Migration tab on the main settings page. The Release rebuild passed;
live page verification remains pending.

Installation discovery also finds nested steam_settings folders, including their
settings/stats subfolders, and lists each matching location. The nested search
respects excluded paths and skips directory links. The Automatic folders display
describes this behavior with a Bin\x64 example. Folder dropdown entries and source
path displays use the casing stored on disk with uppercase drive letters.
Discovery checks covered multiple depths/candidates, exclusions, root deduplication,
and a junction loop; path-casing checks passed. Live Playnite verification is pending.

Local folder discovery uses the same candidate list with and without an override,
so applying a folder override keeps the game's installation folder in the dropdown.
The Release rebuild passed; live dropdown verification remains pending.

The localization recipes preserve the missing Clear, Configure, OK, Reset, Save,
and Search button labels and the Providers header/tooltip. All 22 referenced
button keys and XAML button content/tooltip localization references resolve in
the English resources. The Release rebuild passed; live rendering remains pending.

View Achievements labels the upstream action `Edit Achievements` and the local-file
action `Edit Local Achievements`. The local action remains visible for Local-provider
games even when file discovery fails, and also appears when a writable local file
is detected. Opening the local editor resets folder discovery before resolving the
file; unsupported or missing files produce an editor message. The Release rebuild
passed; live Playnite verification remains pending.

The Local Automatic folders display also includes each game's Playnite installation
folder and supported subfolders, Documents OnlineFix, ProgramData Steam, and the
Steam installation roots already searched by discovery.

The bundle preserves the English `LOCPlayAch_Button_Remove` resource used by
Local settings, including the custom-folder and excluded-folder Remove buttons.
The fix passed the Release rebuild with no errors and two existing CS0649 warnings.
Live button rendering remains pending.
The refreshed bundle passed a dry run against a clean upstream v4.1.1 checkout,
including patch preflight, 155 overlays, localization, hashes, and protected files.

The bundle also preserves the Overview tab label fix using upstream's existing
`LOCPlayAch_Achievements` resource ("Achievements"). No fork localization key
is added; the obsolete `LOCPlayAch_RecentAchievements` reference is removed.
The refreshed bundle passed a dry run against clean upstream v4.1.1.

The bundle now targets upstream v4.1.1
(`002eaa8b2cdcc5df4c6f4bf2d886c5fbbc2390b2`). The in-place update
retains that commit as the pending merge parent; no commit or push was made.
Steam scanning combines upstream cached-category preservation with the fork's
per-game credentials. Notifications retain upstream progress coalescing and
exception handling alongside custom capture content and selective overlay tracks.
The badge test follows upstream's default/custom color sequence, and the Workshop
package field fixture includes the fork's RetroAchievements subset selection.

The required Release rebuild produced version 4.1.1.1 successfully. The refreshed
bundle passed validation in a clean v4.1.1 checkout, including the three-way patch,
155 overlays, localization recipes, component hashes, and protected-file checks.
The full regression suite passed: 5,027 tests passed, eight skipped, none failed.
Live Playnite verification remains pending.

## Bundle refresh — 2026-10-20

This refresh also preserves the Solaris resource-key collision fix and its
cross-file resource-reference regression test, the Revert button label and
two-way selection bindings, and the detached migration page's name scope.
The Release rebuild, 13 theme discovery/migration tests, and WPF rehosting
binding check passed. Full Revert interaction in Playnite remains pending.
The regenerated bundle passed a dry run in an isolated checkout of the v4.1.0
baseline, including patch preflight, 155 overlays, localization, hashes, and
protected-file checks.

The bundle captures the current working fork against the existing v4.1.0
baseline, including the missing English `LOCPlayAch_Button_Cancel` resource
used by all three Maintenance log selection dialogs. The fix's Release rebuild
passed with no errors and two existing CS0649 warnings. Live dialog verification
remains pending. This refresh does not fetch upstream, commit, or push.
The regenerated bundle passed the dry run in an isolated checkout of that
baseline: three-way patch preflight, 155 overlay files, localization recipes,
component hashes, and protected-file checks.

## v4.1.0 integration — 2026-10-18

The current bundle targets upstream v4.1.0
(`bd9a01d8a87c82ae90c5278ac82f324f227dafff`). The in-place update retains
that commit as the pending merge parent; committing and pushing the reviewed
merge is required before GitHub's behind count changes.

The Release rebuild passed. The regression suite passed 4,997 tests, with eight
skipped and none failing. The PS5 completion-icon regression script also passed.
Live Playnite checks remain necessary for notification rendering, Memories
capture, account switching, and the updated Overview UI.

Export now constructs the shared patch using a temporary index seeded from the
upstream baseline. This preserves upstream-added files during an uncommitted
integration without changing the user's staging. The refreshed bundle is checked
by replaying it into an isolated upstream checkout and comparing project files.

This directory turns the Santodan fork into a repeatable layer over an upstream
PlayniteAchievements checkout. It intentionally does not modify or package:

- `README.md`
- `AddonDBManifest.yaml` (the fork's add-on database identity)
- `source/extension.yaml`
- `InstallerManifest.yaml`

The bundle has three parts:

1. **Overlay files** — files that exist only in the fork. These are copied into
   a new upstream checkout and normally have no merge conflicts.
2. **A three-way Git patch** — only upstream-owned files changed by the fork.
   Full-index blob IDs let Git merge unchanged portions automatically.
3. **Localization recipes** — translation entries are merged by `x:Key`
   instead of by line, avoiding conflicts caused by reordered or reformatted
   resource dictionaries.

Git `rerere` is enabled while applying a bundle, so a conflict resolution Git
has seen before can be reused on later upstream updates.

The behavioral inventory in
[`FORK_FEATURE_CHECKLIST.md`](FORK_FEATURE_CHECKLIST.md) is the post-merge test
checklist for the fork features that have historically been lost during
upstream integrations.

The v4.0.1 recovery status and remaining live checks are recorded in
[`../../UPSTREAM_V4.0.1_CONFLICT_REVIEW.md`](../../UPSTREAM_V4.0.1_CONFLICT_REVIEW.md).
The 2026-10-07 bundle refresh captures the complete current working tree,
including uncommitted fixes; it is not a new upstream fetch, commit, or push.
Implementation coverage does not replace the pending live checks in that review.
This refresh replaces the obsolete cache schema-v19 reconciliation with the
working PS5 Experience Theme Migration/runtime adapter. The migrated theme
enables correction of PS5Core Local trophy progress in memory, including after
refreshes, without changing PS5Core.dll or the achievement database. Theme XAML
contains no dependency on plugin assemblies/resources during startup. The user
confirmed corrected fullscreen percentages on 2026-10-07, including the selected
game's trophy-detail header. That header uses a separate PS5Core selected-game
object. The adapter covers its percentage, earned count and progress bar, as well
as the trophy overlay. PS5Core can omit Provider or populate it with a platform
label such as `PC (Windows)`. Both list and detail progress use trophy counts
consistently rather than gating correction on that unreliable provider label.
The 2026-10-08 refresh also captures synchronization of individual achievement
states and unlock dates from the fork cache by game ID and exact achievement API
name. The adapter refreshes PS5Core filtering/sorting and notifies
`CanSelectUnlocked`/`CanSelectLocked`, so the Unlocked filter becomes selectable
after synchronization. The user confirmed the progress and filter fixes on
2026-10-08. Regression coverage includes actual PS5Core 0.7.4 objects and a live
WPF filter-button binding.
The refresh also captures persistent Theme Migration scrollable/highlight and
per-control choices, the automatic Limited/Full/Custom mode selector, and use of
those saved options for first-time and upgraded themes. Desktop defaults to Full;
Fullscreen retains Limited migration. The 15 targeted regression tests and
Release rebuild passed; live settings-dialog/startup checks remain to be verified.
Automatic theme migration notifications now restart Playnite when clicked, using
Playnite's built-in restart method on the UI thread. The English notification
explains this action. The Release rebuild passed; a live notification-click
restart check remains pending.
Git output and generated JSON/XML/patch reads explicitly use UTF-8, including
under Windows PowerShell 5.1, to preserve non-ASCII text during export/application.
The bundle also captures session unlock tracking to prevent cached achievements
from replaying notifications after a refresh relocks them. Starting-cache unlocks,
silent baseline observations, and announced unlocks remain remembered until the
game stops. Seven targeted tests passed and the Release rebuild succeeded.
The refresh also includes General > Maintenance log actions with file selection
for opening, copying, and deleting logs, theme-aware dialog instructions, and the
localized Cancel button. Opening logs only reports errors. The Release rebuild
passed; live dialog checks are listed in the feature checklist.

The refreshed bundle was dry-run validated against a separate clean checkout of
upstream v4.0.1 (`79108753e3f8ddc775aa569ff8f5ed0362f3f22b`): the three-way
patch, fork-only overlays, localization recipes, hashes, and protected-file
checks passed. This validates replay against that baseline, not conflict-free
application to an as-yet-unseen future upstream release.

## Refresh the bundle from the working fork

Run this after the fork is working and tested:

```powershell
cd E:\Programs\Playnite\CustomExtension\PlayniteAchievements-main
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\ForkMaintenance\Export-ForkBundle.ps1 `
    -Baseline upstream/main `
    -Force
```

The export compares the complete working tree (staged and unstaged changes)
with the selected upstream baseline. Fork-only files, shared-file changes, and
localization keys are captured separately. Ignored build output is never
included.

Review the result:

```powershell
git diff -- tools/ForkMaintenance
```

The generated `bundle/bundle.json` records the exact upstream commit and SHA-256
hashes for every generated component.

## Update the current checkout in place

Run the `Update` action from the normal project directory. It fetches upstream,
replaces the tracked non-protected project files with `upstream/main`, and applies
the fork bundle directly in the current checkout:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\ForkMaintenance\Run-ForkMaintenance.ps1 `
    -Action Update `
    -UpstreamRef upstream/main `
    -NoPause
```

The changes remain uncommitted and appear immediately in the current checkout's
VS Code Source Control panel. The action preserves `README.md`,
`source/extension.yaml`, `InstallerManifest.yaml`, and `tools/ForkMaintenance`.
`AddonDBManifest.yaml` is also protected to retain the fork's update identity.
It refuses to start if other local changes exist, preventing unrelated work from
being overwritten. When upstream is not already an ancestor, the action also
opens a no-content merge before assembling the updated files. After review,
stage and commit normally in VS Code or Git: that commit will have the previous
fork tip and the selected upstream commit as its two parents, so GitHub will not
report the fork as behind those upstream commits. Use `-SkipFetch` only when the
selected upstream ref is already current locally.

If the patch reports conflicts, resolve and stage those files, then resume the
same in-place workflow without resetting the checkout again:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\ForkMaintenance\Run-ForkMaintenance.ps1 `
    -Action Update `
    -ResumeAfterConflict `
    -NoPause
```

`-ExecutionPolicy Bypass` applies only to that PowerShell process. It does not
change the machine-wide or user-wide execution policy.

The dry run checks:

- patch applicability using Git's three-way merge;
- collisions with fork-only overlay files;
- independent upstream edits to localization keys;
- bundle hashes;
- protected manifests.

The same validation can be invoked through `Test-ForkBundle.ps1`.

If application leaves a real shared-file conflict, resolve it normally. Git
`rerere` records that resolution for later updates. Do not use
`-ForceOverlay` or `-ForceSemantic` until the reported collision has been
reviewed; those switches deliberately choose the fork's version.

## Recommended upstream workflow

1. Run `Run-ForkMaintenance.ps1 -Action Update` in the normal checkout.
2. Resolve only the reported shared-file conflicts, if any.
3. Build and test the extension.
4. Review every change in VS Code.
5. Stage and commit the reviewed update; the pending upstream merge parent is
   retained automatically.
6. Export a fresh bundle against the new upstream baseline.
7. Update the protected manifests manually, outside this tooling.

## Reducing the remaining shared patch

The bundle makes future updates repeatable immediately. Conflicts can be reduced
further over time by moving fork implementations into additive files:

- C# partial classes named `*.Fork.cs`;
- fork-specific services behind small upstream integration hooks;
- separate XAML controls/resource dictionaries;
- one registration table for providers, tabs, and context-menu commands.

After such a refactor, the shared patch should contain mostly small registration
hooks while the implementation remains in conflict-free overlay files.

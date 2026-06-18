# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog, and this project follows Semantic Versioning.

## [Unreleased]

### Fixed
- **Critical**: HTTP socket exhaustion — plugin now uses a single shared `HttpClient` instead of instantiating one per API call. On .NET Framework, this prevented port exhaustion on large library scans and eliminated silent HTTP failures.
- **Critical**: Scraping no longer silently fails — enabled automatic decompression (`gzip`/`deflate`) on the HTTP client so IMDb HTML responses are properly decompressed before parsing.
- **High**: All HTTP/file I/O errors now logged at Debug level instead of silently swallowed (`catch {}`). Diagnosing "scan found nothing" now requires checking logs, not reverse-engineering from missing data.
- **High**: API endpoint input parsing — `long.Parse()` calls replaced with `TryParse()` guards to prevent unhandled 500 errors on malformed IDs in `GetSeasons`, `GetEpisodes`, and `RunSelected`.
- **Medium**: Library path matching now uses directory separator boundary instead of bare `StartsWith()` — prevents "D:\Movies" filter from matching "D:\Movies2".
- **Medium**: Float comparison at display precision — ratings now compared at rounded precision (1 decimal for community, whole percent for critic) to avoid spurious "updated" entries when API values round to the same display value.
- **Medium**: `UsedMdbList` flag now only set when an actual MDBList API call was made, not unconditionally in fallback branches. Improves accuracy of source labels in logs and scan history.
- **Minor**: Progress bar no longer stalls on early skip paths (missing season/episode info, per-item errors).

## [1.1.6] - 2026-06-18

- Fix: Selected scans targeting a specific series or season now always include episodes, regardless of the "Update Episodes" global toggle. Previously, if "Update Episodes" was disabled (the default), selecting a series in the Run tab would only queue the series item itself — all episodes were silently skipped.

## [1.1.5] - 2026-06-18

- Feature: Critic Rating Source card now uses separate primary + fallback dropdowns, matching the Community Rating Source card layout.
- Feature: MDBList episode ratings — episode IMDb ratings are now read from the seasons/episodes array embedded in the MDBList show response. One API call per series covers all its episodes (cached for the run).
- Feature: imdbapi.dev fallback extended to movies and series — previously only fired for episodes; now fires for any item type when configured sources return no rating.
- Feature: Unrated items are now processed before already-rated items in every run (selected and full scans), maximising API quota impact.
- Fix: `ExtractMdbCommunityRating` no longer falls through to IMDb logic when source is `None`.
- Polish: Renamed all "IMDb scraping" references to "imdbapi.dev" across UI labels, run log, Emby server log, and API usage counters — the feature was never scraping imdb.com directly.

## [1.1.4] - 2026-06-16

- Feature: Community rating source fallback — configure a secondary source per type (Movies / TV Series) that is tried when the primary source has no rating. The fallback is read from the same MDBList API response, so no extra network call is needed. Update log shows "IMDb [fallback from Trakt]: none → 7.5 (MDBList)" when the fallback fires.

## [1.1.3] - 2026-06-16

- Feature: Manual runs now respect the rescan interval by default — items scanned within the configured window are skipped, matching the behaviour of scheduled runs.
- Feature: Added "Force refresh" checkbox to the Run tab to bypass the rescan interval on demand.
- Fix: Interval filter notice now correctly appears when all selected items were skipped due to the rescan interval (previously the count was lost because `ProgressTracker.Start()` reset the progress object before the value was preserved).
- Improvement: Source label now shows `OMDb→Scraped` when OMDb was tried but returned no episode rating and IMDb scraping succeeded, making the fallback chain visible in the UI.
- Improvement: Log message added when OMDb returns no rating for an episode — now distinguishes between: scrape succeeded, scrape limit reached, scraping disabled, and scrape also returned nothing.
- Improvement: UI skip reason now includes fallback context when OMDb has no episode rating — e.g. "No ratings found [OMDb] — IMDb scrape limit exhausted (250/250)" or "— IMDb scrape fallback not enabled".
- Polish: Audited all UI and Emby log messages — removed "MDBList does not support episode lookups" noise from episode skip reasons, fixed "No critic rating in API" → "No critic rating found", unified IMDb scraping terminology, replaced "API" source label fallback with "unknown source".

## [1.1.2] - 2026-06-16

- Improvement: Settings page restructured into three logical sections — API Setup (keys + mode + rate limits), Rating Sources (community + critic per type), Content (item types + scanning).
- Improvement: Target framework upgraded from net462 to net472; eliminates ~90 netstandard shim DLLs from build output (deploy only `RatingSync.dll`).
- Improvement: "Enable Community/Critic Rating updates" toggles moved from API Mode card to top of Rating Sources section where they logically belong.
- Improvement: IMDb episode fallback toggle merged into the IMDb Fallback rate limit card in API Setup — all fallback config in one place.
- Fix: Roger Ebert removed from TV Series community source options (Ebert only reviewed films; MDBList has no TV data for this source).
- Improvement: Community source labels clarified — "Metacritic (Critic)" → "Metacritic Score", "Metacritic (User)" → "Metacritic User Score".
- Improvement: API Mode "Both" option labelled and described more clearly to distinguish it from fallback modes.
- Fix: Episode skip messages no longer mention RT/critic ratings (episodes never receive critic ratings).

## [1.1.1] - 2026-06-15

- Feature: Added 7 new community rating sources via MDBList API — Metacritic (Critic), Metacritic (User), MDBList Score, Trakt, TMDb, Letterboxd, Roger Ebert.
- Feature: Community Rating source is now configurable independently for Movies and TV Series (episodes always use IMDb).
- Feature: New Critic Rating Source selector per type — RT Tomatometer, Metacritic, RT with Metacritic fallback, Metacritic with RT fallback, or None.
- Improvement: Settings UI redesigned with blue accent colour scheme and card layout.

## [1.0.10.1] - 2026-05-26

- Fix: IMDb scraping rate limit now properly enforced - prevents scraping when daily limit is reached and stops at the configured limit during batch processing.

## [1.0.10] - 2026-05-26

- Feature: Added IMDb scraping daily rate limit setting to control how many IMDb API requests are made per day.

## [1.0.9] - 2026-04-08

- Fix: Resolved `ReferenceError` in `saveConfig` where the `self` variable was undefined.
- Fix: Added robust element checks to the settings page to prevent crashes and improved error logging for configuration loading/saving.
- Fix: Restricted "Enable IMDb Scraping (Episode Fallback)" to strictly target episodes only, removing unintended fallback for movies and series.
- Fix: Corrected manual library refreshes (Start Refresh) to respect the "Update Movies", "Update Series", and "Update Episodes" checkboxes.
- Fix: Additional UI cache-bust by bumping versioned plugin page names.

## [1.0.8] - 2026-04-07

- Fix: Force configuration page cache-bust by versioning plugin web page names to ensure latest UI loads after updates.

## [1.0.7] - 2026-04-07

- Feature: Simplified settings with a single `API Mode` selector (`OMDb only`, `MDBList only`, fallback modes, `Both`).
- Feature: Simplified Community Rating source to one selector for movies/series (`IMDb` or `Popcorn`), with episodes fixed to IMDb.
- Improvement: Removed overlapping per-type source controls and legacy fallback checkbox from the UI to reduce confusion.

## [1.0.6] - 2026-04-07

- Feature: Added `Update Community Rating` toggle so community and critic updates can be controlled independently.
- Improvement: Run tab now reports source-aware "no ratings found" and clearer messages when MDBList is unavailable due to daily/API limits.

## [1.0.5] - 2026-04-07

- Feature: Added `Allow fallback to alternate API source` toggle for strict preferred-source mode.
- Improvement: Episodes Community Rating is now forced to IMDb in UI and runtime logic.
- Improvement: Run tab skip reasons now include API source labels (e.g., `[OMDb]`, `[MDBList]`) and clearer MDBList-unavailable messaging.
- Fix: Handle MDBList 429 responses with retry-until tracking and temporary API cooldown.

## [1.0.4] - 2026-04-07

- Fix: README.md formatting error in the header.
- Improvement: Cleaned up MDBList rating search logic in Plugin.cs.

## [1.0.3] - 2026-04-07

- Feature: Allow configuring the Community Rating source per media type (Movies, TV Series, Episodes).
- Feature: Added support for Rotten Tomatoes Audience (Popcorn) scores via MDBList API for Emby's Community Rating.

## [1.0.2] - 2026-03-25

- Fix: Replace broken IMDb HTML scraping for episodes with `api.imdbapi.dev` (unofficial IMDb API), which bypasses the AWS WAF bot protection now blocking direct imdb.com requests.
- Fix: OMDb episode lookup now queries the episode's own IMDb ID directly (`?i={episodeId}`) when Emby provides it, improving coverage for recently aired episodes.
- Fix: `api.imdbapi.dev` fallback also applied to movies and TV series when configured sources return no community rating.
- Improvement: IMDb HTML scraper (last-resort fallback) now sends realistic browser headers to improve chances of bypassing bot-detection.

## [1.0.1] - 2026-01-08

- Fix: Prevent IMDb scraping from applying the series rating to episodes without episode IMDb IDs by resolving the episode title ID from the series episodes page.
- UI: API usage bar now notes it resets at 00:00 UTC.

## [1.0.0] - 2026-01-07

- Initial public release.

# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog, and this project follows Semantic Versioning.

## [1.1.2] - 2026-06-16

- Improvement: Settings page restructured into three logical sections — API Setup (keys + mode + rate limits), Rating Sources (community + critic per type), Content (item types + scanning).
- Improvement: Target framework upgraded from net462 to net472; eliminates ~90 netstandard shim DLLs from build output (deploy only `RatingSync.dll`).

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

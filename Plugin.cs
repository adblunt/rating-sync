using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RatingSync
{
    #region Progress Tracking

    public class RefreshProgress
    {
        public bool IsRunning { get; set; }
        public int TotalItems { get; set; }
        public int ProcessedItems { get; set; }
        public int UpdatedItems { get; set; }
        public int SkippedItems { get; set; }
        public int ErrorItems { get; set; }
        public double PercentComplete { get; set; }
        public string CurrentItem { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public double? EstimatedSecondsRemaining { get; set; }
        public int FilteredByInterval { get; set; }
        public Dictionary<string, string> UpdatedDetails { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> SkippedDetails { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> FailureDetails { get; set; } = new Dictionary<string, string>();
    }

    public static class ProgressTracker
    {
        private static readonly object _lock = new object();
        private static RefreshProgress _progress = new RefreshProgress();

        public static RefreshProgress GetProgress()
        {
            lock (_lock)
            {
                // Calculate ETA
                double? eta = null;
                if (_progress.IsRunning && _progress.StartTime.HasValue && _progress.ProcessedItems > 0 && _progress.TotalItems > 0)
                {
                    var elapsed = (DateTime.UtcNow - _progress.StartTime.Value).TotalSeconds;
                    var avgTimePerItem = elapsed / _progress.ProcessedItems;
                    var remainingItems = _progress.TotalItems - _progress.ProcessedItems;
                    eta = avgTimePerItem * remainingItems;
                }

                return new RefreshProgress
                {
                    IsRunning = _progress.IsRunning,
                    TotalItems = _progress.TotalItems,
                    ProcessedItems = _progress.ProcessedItems,
                    UpdatedItems = _progress.UpdatedItems,
                    SkippedItems = _progress.SkippedItems,
                    ErrorItems = _progress.ErrorItems,
                    FilteredByInterval = _progress.FilteredByInterval,
                    PercentComplete = _progress.PercentComplete,
                    CurrentItem = _progress.CurrentItem,
                    StartTime = _progress.StartTime,
                    EndTime = _progress.EndTime,
                    EstimatedSecondsRemaining = eta,
                    UpdatedDetails = new Dictionary<string, string>(_progress.UpdatedDetails),
                    SkippedDetails = new Dictionary<string, string>(_progress.SkippedDetails),
                    FailureDetails = new Dictionary<string, string>(_progress.FailureDetails)
                };
            }
        }

        public static void Start(int totalItems)
        {
            lock (_lock)
            {
                _progress = new RefreshProgress
                {
                    IsRunning = true,
                    TotalItems = totalItems,
                    StartTime = DateTime.UtcNow
                };
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                _progress.IsRunning = false;
                _progress.EndTime = DateTime.UtcNow;
            }
        }

        public static void UpdateProgress(int processed, int updated, int skipped, int errors, string currentItem)
        {
            lock (_lock)
            {
                _progress.ProcessedItems = processed;
                _progress.UpdatedItems = updated;
                _progress.SkippedItems = skipped;
                _progress.ErrorItems = errors;
                _progress.CurrentItem = currentItem;
                _progress.PercentComplete = _progress.TotalItems > 0 
                    ? (double)processed / _progress.TotalItems * 100 
                    : 0;
            }
        }

        public static void AddUpdated(string itemName, string details)
        {
            lock (_lock)
            {
                _progress.UpdatedDetails[itemName] = details;
            }
        }

        public static void AddSkipped(string itemName, string reason)
        {
            lock (_lock)
            {
                _progress.SkippedDetails[itemName] = reason;
            }
        }

        public static void AddFailure(string itemName, string reason)
        {
            lock (_lock)
            {
                _progress.FailureDetails[itemName] = reason;
            }
        }

        public static void ClearProgress()
        {
            lock (_lock)
            {
                _progress = new RefreshProgress();
                _progress.FilteredByInterval = 0;
            }
        }

        public static void SetFilteredByInterval(int count)
        {
            lock (_lock)
            {
                _progress.FilteredByInterval = count;
            }
        }

        public static void SetMessage(string message)
        {
            lock (_lock)
            {
                _progress.CurrentItem = message;
            }
        }
    }

    public static class SelectedItemsStore
    {
        private static readonly object _lock = new object();
        private static List<BaseItem> _selectedItems = new List<BaseItem>();
        private static bool _hasItems = false;
        private static bool _forceRefresh = false;

        public static void SetItems(List<BaseItem> items, bool forceRefresh = false)
        {
            lock (_lock)
            {
                _selectedItems = items ?? new List<BaseItem>();
                _hasItems = _selectedItems.Count > 0;
                _forceRefresh = forceRefresh;
            }
        }

        public static List<BaseItem> GetAndClearItems()
        {
            lock (_lock)
            {
                var items = _selectedItems;
                _selectedItems = new List<BaseItem>();
                _hasItems = false;
                _forceRefresh = false;
                return items;
            }
        }

        public static bool HasItems
        {
            get { lock (_lock) { return _hasItems; } }
        }

        public static bool ForceRefresh
        {
            get { lock (_lock) { return _forceRefresh; } }
        }
    }

    #endregion

    #region API Service

    [Route("/RatingSync/Progress", "GET", Summary = "Gets the current progress of the rating refresh task")]
    [Authenticated]
    public class GetProgressRequest : IReturn<RefreshProgress>
    {
    }

    [Route("/RatingSync/ClearProgress", "POST", Summary = "Clears the progress data")]
    [Authenticated]
    public class ClearProgressRequest : IReturnVoid
    {
    }

    [Route("/RatingSync/History", "GET", Summary = "Gets scan history sessions")]
    [Authenticated]
    public class GetHistoryRequest : IReturn<List<ScanSession>>
    {
        public int Count { get; set; }
    }

    [Route("/RatingSync/HistoryReport", "GET", Summary = "Gets a detailed scan report for a session")]
    [Authenticated]
    public class GetHistoryReportRequest : IReturn<ScanSessionReport>
    {
        public string SessionId { get; set; }
    }

    [Route("/RatingSync/ApiCounters", "GET", Summary = "Gets today's API usage counters")]
    [Authenticated]
    public class GetApiCountersRequest : IReturn<ApiCountersResponse>
    {
    }

    [DataContract]
    public class ApiCountersResponse
    {
        [DataMember]
        public string Today { get; set; }

        [DataMember]
        public int OmdbUsed { get; set; }
        [DataMember]
        public int OmdbLimit { get; set; }
        [DataMember]
        public bool OmdbHasKey { get; set; }
        [DataMember]
        public bool OmdbRateLimitEnabled { get; set; }

        [DataMember]
        public int MdbListUsed { get; set; }
        [DataMember]
        public int MdbListLimit { get; set; }
        [DataMember]
        public bool MdbListHasKey { get; set; }
        [DataMember]
        public bool MdbListRateLimitEnabled { get; set; }

        [DataMember]
        public int ImdbScrapesUsed { get; set; }
        [DataMember]
        public int ImdbLimit { get; set; }
        [DataMember]
        public bool ImdbRateLimitEnabled { get; set; }
    }

    [Route("/RatingSync/DeleteScan", "POST", Summary = "Deletes a scan session and its report")]
    [Authenticated]
    public class DeleteScanRequest : IReturn<DeleteScanResponse>
    {
        public string SessionId { get; set; }
    }

    [DataContract]
    public class DeleteScanResponse
    {
        [DataMember]
        public bool Success { get; set; }
        [DataMember]
        public string Message { get; set; }
    }

    [Route("/RatingSync/MissingData", "GET", Summary = "Gets items with missing IMDb or rating data")]
    [Authenticated]
    public class GetMissingDataRequest : IReturn<List<MissingDataItem>>
    {
        public string Type { get; set; } // "movies", "series", "episodes", or empty for all
    }

    [Route("/RatingSync/ItemHistory", "GET", Summary = "Gets scan history for specific items")]
    [Authenticated]
    public class GetItemHistoryRequest : IReturn<List<ItemHistoryEntry>>
    {
        public string Search { get; set; }
        public int Limit { get; set; }
    }

    [Route("/RatingSync/Libraries", "GET", Summary = "Gets available libraries")]
    [Authenticated]
    public class GetLibrariesRequest : IReturn<List<LibraryInfo>>
    {
    }

    [Route("/RatingSync/Series", "GET", Summary = "Gets TV series in a library")]
    [Authenticated]
    public class GetSeriesRequest : IReturn<List<SeriesInfo>>
    {
        public string LibraryId { get; set; }
    }

    [Route("/RatingSync/Seasons", "GET", Summary = "Gets seasons for a series")]
    [Authenticated]
    public class GetSeasonsRequest : IReturn<List<SeasonInfo>>
    {
        public string SeriesId { get; set; }
    }

    [Route("/RatingSync/Episodes", "GET", Summary = "Gets episodes for a season")]
    [Authenticated]
    public class GetEpisodesRequest : IReturn<List<EpisodeInfoDto>>
    {
        public string SeasonId { get; set; }
    }

    [Route("/RatingSync/RunSelected", "POST", Summary = "Runs refresh on selected items")]
    [Authenticated]
    public class RunSelectedRequest : IReturnVoid
    {
        public string LibraryId { get; set; }
        public string SeriesId { get; set; }
        public string SeasonId { get; set; }
        public string EpisodeId { get; set; }
        public string MovieId { get; set; }

        // When scanning at the "All Movies" / "All Series" level, optionally filter to only items added within N days.
        // 0 means no filter.
        public int AddedWithinDays { get; set; }

        // When true, bypasses the rescan interval and processes all selected items regardless of last scan time.
        public bool ForceRefresh { get; set; }
    }

    [Route("/RatingSync/Movies", "GET", Summary = "Gets movies in a library")]
    [Authenticated]
    public class GetMoviesRequest : IReturn<List<MovieInfo>>
    {
        public string LibraryId { get; set; }
    }

    public class LibraryInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string CollectionType { get; set; }
    }

    public class SeriesInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int? Year { get; set; }
        public string ImdbId { get; set; }
    }

    public class SeasonInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int? SeasonNumber { get; set; }
        public int EpisodeCount { get; set; }
    }

    public class EpisodeInfoDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int? SeasonNumber { get; set; }
        public int? EpisodeNumber { get; set; }
        public string ImdbId { get; set; }
    }

    public class MovieInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int? Year { get; set; }
        public string ImdbId { get; set; }
    }

    public class MissingDataItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public int? Year { get; set; }
        public string SeriesName { get; set; }
        public int? SeasonNumber { get; set; }
        public string SeasonName { get; set; }
        public int? EpisodeNumber { get; set; }
        public string ImdbId { get; set; }
        public bool HasImdbId { get; set; }
        public bool HasCommunityRating { get; set; }
        public bool HasCriticRating { get; set; }
        public float? CommunityRating { get; set; }
        public float? CriticRating { get; set; }
        public string MissingReason { get; set; }
    }

    public class ItemHistoryEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public DateTime? LastScanned { get; set; }
        public float? LastRating { get; set; }
        public float? LastCriticRating { get; set; }
        public string LastChange { get; set; }
        public float? CurrentRating { get; set; }
        public float? CurrentCriticRating { get; set; }
    }

    public class ProgressApiService : IService
    {
        private ILibraryManager _libraryManager;

        public ProgressApiService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        // True when itemPath is the library path itself or sits beneath it.
        // Uses a separator boundary so "D:\Movies" does not match "D:\Movies2".
        // Inputs are expected pre-lowercased by callers.
        private static bool PathUnderAny(string itemPath, List<string> libraryPaths)
        {
            if (string.IsNullOrEmpty(itemPath) || libraryPaths == null)
                return false;

            foreach (var lp in libraryPaths)
            {
                if (string.IsNullOrEmpty(lp))
                    continue;
                if (itemPath.Equals(lp, StringComparison.Ordinal))
                    return true;
                var prefix = (lp.EndsWith("/") || lp.EndsWith("\\")) ? lp : lp + Path.DirectorySeparatorChar;
                if (itemPath.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private string TryGetSeasonName(Episode episode)
        {
            if (episode == null)
                return null;

            try
            {
                // Some Emby builds expose an actual Season object
                var seasonProp = episode.GetType().GetProperty("Season");
                var seasonObj = seasonProp?.GetValue(episode, null);
                var seasonName = seasonObj?.GetType().GetProperty("Name")?.GetValue(seasonObj, null) as string;
                if (!string.IsNullOrWhiteSpace(seasonName))
                    return seasonName;

                // Otherwise try to resolve a season id (if available)
                var idProp = episode.GetType().GetProperty("SeasonId") ?? episode.GetType().GetProperty("ParentId");
                var idVal = idProp?.GetValue(episode, null);
                if (idVal == null)
                    return null;

                long seasonId;
                if (idVal is long l)
                    seasonId = l;
                else if (idVal is int i)
                    seasonId = i;
                else if (idVal is string s && long.TryParse(s, out var parsed))
                    seasonId = parsed;
                else
                    return null;

                return (_libraryManager.GetItemById(seasonId) as Season)?.Name;
            }
            catch
            {
                return null;
            }
        }

        public object Get(GetProgressRequest request)
        {
            return ProgressTracker.GetProgress();
        }

        public object Get(GetHistoryRequest request)
        {
            var count = request.Count > 0 ? request.Count : 20;
            return ScanHistoryManager.GetSessions(count);
        }

        public object Get(GetHistoryReportRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.SessionId))
                return null;
            return ScanHistoryManager.GetReport(request.SessionId);
        }

        public object Get(GetApiCountersRequest request)
        {
            var config = Plugin.Instance.Configuration;
            var todayKey = DateTime.UtcNow.ToString("yyyy-MM-dd");

            return new ApiCountersResponse
            {
                Today = todayKey,
                OmdbUsed = ScanHistoryManager.GetTodayRequestCount("omdb"),
                OmdbLimit = config?.OmdbDailyLimit ?? 0,
                OmdbHasKey = config != null && !string.IsNullOrWhiteSpace(config.OmdbApiKey),
                OmdbRateLimitEnabled = config?.OmdbRateLimitEnabled ?? false,
                MdbListUsed = ScanHistoryManager.GetTodayRequestCount("mdblist"),
                MdbListLimit = config?.MdbListDailyLimit ?? 0,
                MdbListHasKey = config != null && !string.IsNullOrWhiteSpace(config.MdbListApiKey),
                MdbListRateLimitEnabled = config?.MdbListRateLimitEnabled ?? false,
                ImdbScrapesUsed = ScanHistoryManager.GetTodayImdbScrapeCount(),
                ImdbLimit = config?.ImdbDailyLimit ?? 0,
                ImdbRateLimitEnabled = config?.ImdbRateLimitEnabled ?? false
            };
        }

        public object Post(DeleteScanRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.SessionId))
                return new DeleteScanResponse { Success = false, Message = "Missing SessionId" };

            var result = ScanHistoryManager.DeleteSession(request.SessionId, out var message);
            if (result)
            {
                ScanHistoryManager.Save();
            }

            return new DeleteScanResponse { Success = result, Message = message };
        }

        public object Get(GetMissingDataRequest request)
        {
            var results = new List<MissingDataItem>();
            var itemTypes = new List<string>();
            
            if (string.IsNullOrEmpty(request.Type) || request.Type == "movies")
                itemTypes.Add("Movie");
            if (string.IsNullOrEmpty(request.Type) || request.Type == "series")
                itemTypes.Add("Series");
            if (string.IsNullOrEmpty(request.Type) || request.Type == "episodes")
                itemTypes.Add("Episode");
            
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = itemTypes.ToArray(),
                Recursive = true
            });
            
            foreach (var item in items)
            {
                var imdbId = item.GetProviderId(MetadataProviders.Imdb);
                var hasImdb = !string.IsNullOrEmpty(imdbId);
                var hasRating = item.CommunityRating.HasValue;
                var hasCritic = item.CriticRating.HasValue;

                // Episodes often don't have an IMDb ID in Emby; only flag Episodes if rating is missing.
                var isEpisode = item is Episode;

                // Skip Specials / Season 0 episodes entirely
                if (isEpisode)
                {
                    var ep = (Episode)item;
                    var seasonNum = ep.ParentIndexNumber ?? 0;
                    if (seasonNum == 0)
                        continue;
                }

                var include = isEpisode ? !hasRating : (!hasImdb || !hasRating || !hasCritic);

                if (include)
                {
                    string missingReason = "";
                    if (isEpisode)
                    {
                        if (!hasRating) missingReason = "No Community Rating";
                    }
                    else
                    {
                        var reasons = new List<string>();
                        if (!hasImdb) reasons.Add("No IMDb ID");
                        if (!hasRating) reasons.Add("No Community Rating");
                        if (!hasCritic) reasons.Add("No Critic Rating");
                        missingReason = string.Join("; ", reasons);
                    }
                    
                    string typeName = "Unknown";
                    int? year = null;

                    string seriesName = null;
                    int? seasonNumber = null;
                    string seasonName = null;
                    int? episodeNumber = null;
                    
                    if (item is Movie movie)
                    {
                        typeName = "Movie";
                        year = movie.ProductionYear;
                    }
                    else if (item is MediaBrowser.Controller.Entities.TV.Series series)
                    {
                        typeName = "Series";
                        year = series.ProductionYear;
                    }
                    else if (item is Episode episode)
                    {
                        typeName = "Episode";
                        year = episode.ProductionYear;

                        seriesName = episode.SeriesName;
                        seasonNumber = episode.ParentIndexNumber;
                        seasonName = TryGetSeasonName(episode);
                        episodeNumber = episode.IndexNumber;
                    }
                    
                    results.Add(new MissingDataItem
                    {
                        Id = item.InternalId.ToString(),
                        Name = item.Name,
                        Type = typeName,
                        Year = year,
                        SeriesName = seriesName,
                        SeasonNumber = seasonNumber,
                        SeasonName = seasonName,
                        EpisodeNumber = episodeNumber,
                        ImdbId = imdbId,
                        HasImdbId = hasImdb,
                        HasCommunityRating = hasRating,
                        HasCriticRating = hasCritic,
                        CommunityRating = item.CommunityRating,
                        CriticRating = item.CriticRating,
                        MissingReason = missingReason
                    });
                }
            }
            
            return results.OrderBy(r => r.Type).ThenBy(r => r.Name).Take(500).ToList();
        }

        public object Get(GetItemHistoryRequest request)
        {
            var results = new List<ItemHistoryEntry>();
            var historyEntries = ScanHistoryManager.GetAllEntries();
            var search = request.Search?.ToLowerInvariant() ?? "";
            var limit = request.Limit > 0 ? request.Limit : 100;
            
            // Get all items with history
            foreach (var kvp in historyEntries)
            {
                if (long.TryParse(kvp.Key, out var itemId))
                {
                    var item = _libraryManager.GetItemById(itemId);
                    if (item != null)
                    {
                        // Filter by search if provided
                        if (!string.IsNullOrEmpty(search) && !item.Name.ToLowerInvariant().Contains(search))
                            continue;
                        
                        string typeName = "Unknown";
                        if (item is Movie) typeName = "Movie";
                        else if (item is MediaBrowser.Controller.Entities.TV.Series) typeName = "Series";
                        else if (item is Episode) typeName = "Episode";
                        
                        results.Add(new ItemHistoryEntry
                        {
                            Id = kvp.Key,
                            Name = item.Name,
                            Type = typeName,
                            LastScanned = kvp.Value.LastScanned,
                            LastRating = kvp.Value.LastRating,
                            LastCriticRating = kvp.Value.LastCriticRating,
                            LastChange = kvp.Value.LastChange,
                            CurrentRating = item.CommunityRating,
                            CurrentCriticRating = item.CriticRating
                        });
                    }
                }
            }
            
            return results.OrderByDescending(r => r.LastScanned).Take(limit).ToList();
        }

        public object Get(GetLibrariesRequest request)
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == "movies" || f.CollectionType == "tvshows" || string.IsNullOrEmpty(f.CollectionType))
                .Select(f => new LibraryInfo
                {
                    Id = f.ItemId,
                    Name = f.Name,
                    CollectionType = f.CollectionType ?? "mixed"
                })
                .ToList();
            return libraries;
        }

        public object Get(GetSeriesRequest request)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Series" },
                Recursive = true
            };
            
            var allSeries = _libraryManager.GetItemList(query)
                .OfType<MediaBrowser.Controller.Entities.TV.Series>();
            
            // Filter by library if specified
            if (!string.IsNullOrEmpty(request.LibraryId))
            {
                // Library ItemId could be a GUID string or a long - find library by matching
                var targetLibraryId = request.LibraryId;
                var virtualFolders = _libraryManager.GetVirtualFolders();
                var matchingFolder = virtualFolders.FirstOrDefault(f => f.ItemId == targetLibraryId);
                
                if (matchingFolder != null)
                {
                    // Filter series by checking if they're in the library's paths
                    var libraryPaths = matchingFolder.Locations.Select(l => l.ToLowerInvariant()).ToList();
                    allSeries = allSeries.Where(s => {
                        var seriesPath = s.Path?.ToLowerInvariant() ?? "";
                        return PathUnderAny(seriesPath, libraryPaths);
                    });
                }
            }
            
            var series = allSeries
                .OrderBy(s => s.Name)
                .Select(s => new SeriesInfo
                {
                    Id = s.InternalId.ToString(),
                    Name = s.Name,
                    Year = s.ProductionYear,
                    ImdbId = s.GetProviderId(MetadataProviders.Imdb)
                })
                .ToList();
            return series;
        }

        public object Get(GetSeasonsRequest request)
        {
            if (string.IsNullOrEmpty(request.SeriesId) || !long.TryParse(request.SeriesId, out var seriesId))
                return new List<SeasonInfo>();

            var series = _libraryManager.GetItemById(seriesId) as MediaBrowser.Controller.Entities.TV.Series;
            if (series == null)
                return new List<SeasonInfo>();

            var seasons = series.GetRecursiveChildren()
                .OfType<Season>()
                .Where(s => (s.IndexNumber ?? 0) != 0) // Skip Specials / Season 0
                .OrderBy(s => s.IndexNumber ?? 0)
                .Select(s => new SeasonInfo
                {
                    Id = s.InternalId.ToString(),
                    Name = s.Name,
                    SeasonNumber = s.IndexNumber,
                    EpisodeCount = s.GetRecursiveChildren().OfType<Episode>().Count()
                })
                .ToList();
            return seasons;
        }

        public object Get(GetEpisodesRequest request)
        {
            if (string.IsNullOrEmpty(request.SeasonId) || !long.TryParse(request.SeasonId, out var seasonId))
                return new List<EpisodeInfoDto>();

            var season = _libraryManager.GetItemById(seasonId) as Season;
            if (season == null)
                return new List<EpisodeInfoDto>();

            var episodes = season.GetRecursiveChildren()
                .OfType<Episode>()
                .OrderBy(e => e.IndexNumber ?? 0)
                .Select(e => new EpisodeInfoDto
                {
                    Id = e.InternalId.ToString(),
                    Name = e.Name,
                    SeasonNumber = e.ParentIndexNumber,
                    EpisodeNumber = e.IndexNumber,
                    ImdbId = e.GetProviderId(MetadataProviders.Imdb)
                })
                .ToList();
            return episodes;
        }

        public object Get(GetMoviesRequest request)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie" },
                Recursive = true
            };
            
            var allMovies = _libraryManager.GetItemList(query)
                .OfType<Movie>();
            
            // Filter by library if specified
            if (!string.IsNullOrEmpty(request.LibraryId))
            {
                var targetLibraryId = request.LibraryId;
                var virtualFolders = _libraryManager.GetVirtualFolders();
                var matchingFolder = virtualFolders.FirstOrDefault(f => f.ItemId == targetLibraryId);
                
                if (matchingFolder != null)
                {
                    var libraryPaths = matchingFolder.Locations.Select(l => l.ToLowerInvariant()).ToList();
                    allMovies = allMovies.Where(m => {
                        var moviePath = m.Path?.ToLowerInvariant() ?? "";
                        return PathUnderAny(moviePath, libraryPaths);
                    });
                }
            }
            
            var movies = allMovies
                .OrderBy(m => m.Name)
                .Select(m => new MovieInfo
                {
                    Id = m.InternalId.ToString(),
                    Name = m.Name,
                    Year = m.ProductionYear,
                    ImdbId = m.GetProviderId(MetadataProviders.Imdb)
                })
                .ToList();
            return movies;
        }

        public void Post(RunSelectedRequest request)
        {
            var config = Plugin.Instance.Configuration;
            // Build list of items to scan based on selection level
            var items = new List<BaseItem>();
            
            // Handle movie selection
            if (!string.IsNullOrEmpty(request.MovieId))
            {
                // Scan specific movie
                if (config.UpdateMovies && long.TryParse(request.MovieId, out var movieId))
                {
                    var movie = _libraryManager.GetItemById(movieId) as Movie;
                    if (movie != null) items.Add(movie);
                }
            }
            else if (!string.IsNullOrEmpty(request.EpisodeId))
            {
                // Scan specific episode — always include regardless of UpdateEpisodes (user explicitly selected it)
                if (long.TryParse(request.EpisodeId, out var episodeId))
                {
                    var episode = _libraryManager.GetItemById(episodeId) as Episode;
                    if (episode != null && (episode.ParentIndexNumber ?? 0) != 0) items.Add(episode);
                }
            }
            else if (!string.IsNullOrEmpty(request.SeasonId))
            {
                // Scan all episodes in season — always include regardless of UpdateEpisodes (user explicitly selected it)
                var season = long.TryParse(request.SeasonId, out var seasonId) ? _libraryManager.GetItemById(seasonId) as Season : null;
                if (season != null)
                {
                    items.AddRange(season.GetRecursiveChildren().OfType<Episode>().Where(e => (e.ParentIndexNumber ?? 0) != 0));
                }
            }
            else if (!string.IsNullOrEmpty(request.SeriesId))
            {
                // Scan series AND all episodes — always include episodes regardless of UpdateEpisodes (user explicitly selected it)
                var series = long.TryParse(request.SeriesId, out var seriesId) ? _libraryManager.GetItemById(seriesId) as MediaBrowser.Controller.Entities.TV.Series : null;
                if (series != null)
                {
                    if (config.UpdateSeries)
                        items.Add(series);
                    items.AddRange(series.GetRecursiveChildren().OfType<Episode>().Where(e => (e.ParentIndexNumber ?? 0) != 0));
                }
            }
            else if (!string.IsNullOrEmpty(request.LibraryId))
            {
                // Get library info to determine type
                var virtualFolders = _libraryManager.GetVirtualFolders();
                var matchingFolder = virtualFolders.FirstOrDefault(f => f.ItemId == request.LibraryId);
                
                if (matchingFolder != null)
                {
                    var libraryPaths = matchingFolder.Locations.Select(l => l.ToLowerInvariant()).ToList();
                    
                    if (matchingFolder.CollectionType == "movies")
                    {
                        // Scan all movies in library
                        if (config.UpdateMovies)
                        {
                            var allMovies = _libraryManager.GetItemList(new InternalItemsQuery
                            {
                                IncludeItemTypes = new[] { "Movie" },
                                Recursive = true
                            }).OfType<Movie>().Where(m => {
                                var moviePath = m.Path?.ToLowerInvariant() ?? "";
                                return libraryPaths.Any(lp => moviePath.StartsWith(lp));
                            });
                            items.AddRange(allMovies);
                        }
                    }
                    else
                    {
                        // Scan all series AND episodes in TV library
                        var allSeries = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { "Series" },
                            Recursive = true
                        }).OfType<MediaBrowser.Controller.Entities.TV.Series>().Where(s => {
                            var seriesPath = s.Path?.ToLowerInvariant() ?? "";
                            return libraryPaths.Any(lp => seriesPath.StartsWith(lp));
                        });
                        
                        var selectedSeries = allSeries.ToList();

                        if (config.UpdateSeries)
                        {
                            items.AddRange(selectedSeries);
                        }

                        if (config.UpdateEpisodes)
                        {
                            items.AddRange(selectedSeries
                                .SelectMany(s => s.GetRecursiveChildren().OfType<Episode>())
                                .Where(e => (e.ParentIndexNumber ?? 0) != 0));
                        }
                    }
                }
            }

            // Optional filter for "all movies" / "all series" runs: only include recently added items.
            var hasSpecificTarget = !string.IsNullOrEmpty(request.MovieId)
                || !string.IsNullOrEmpty(request.EpisodeId)
                || !string.IsNullOrEmpty(request.SeasonId)
                || !string.IsNullOrEmpty(request.SeriesId);

            if (!hasSpecificTarget && request.AddedWithinDays > 0 && items.Count > 0)
            {
                var days = request.AddedWithinDays;
                // Interpret "Today"/"Last N days" as inclusive of today (UTC midnight boundary).
                var cutoff = DateTime.UtcNow.Date.AddDays(-(days - 1));
                items = items.Where(i => i != null && i.DateCreated >= cutoff).ToList();
            }
            
            if (items.Count > 0)
            {
                // Store selected items for the task to pick up
                SelectedItemsStore.SetItems(items, request.ForceRefresh);
                if (!hasSpecificTarget && request.AddedWithinDays > 0)
                {
                    ProgressTracker.SetMessage($"Queued {items.Count} items (added last {request.AddedWithinDays} day(s))");
                }
                else
                {
                    ProgressTracker.SetMessage($"Queued {items.Count} items for selected scan");
                }
            }
        }

        public void Post(ClearProgressRequest request)
        {
            ProgressTracker.ClearProgress();
        }
    }

    #endregion

    #region Plugin

    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            // Initialize scan history with plugin data path
            ScanHistoryManager.Initialize(applicationPaths.PluginConfigurationsPath);
        }

        public override string Name => "Rating Sync";

        public override Guid Id => new Guid("35f648d2-437a-48c3-9449-9e8c2dbfd143");

        public override string Description => "Refreshes community and critic ratings from IMDb, Rotten Tomatoes, Metacritic, MDBList, and more";

        public static Plugin Instance { get; private set; }

        public Stream GetThumbImage()
        {
            return GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".images.logo.jpg");
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Jpg;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "RatingSyncConfiguration_v122",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                },
                new PluginPageInfo
                {
                    Name = "RatingSyncConfigurationjs_v122",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
                }
            };
        }
    }

    #endregion

    #region Configuration

    public class PluginConfiguration : BasePluginConfiguration
    {
        // API Keys
        public string OmdbApiKey { get; set; }
        public string MdbListApiKey { get; set; }
        
        // Rate Limiting - OMDb
        public bool OmdbRateLimitEnabled { get; set; }
        public int OmdbDailyLimit { get; set; }
        
        // Rate Limiting - MDBList
        public bool MdbListRateLimitEnabled { get; set; }
        public int MdbListDailyLimit { get; set; }
        
        // Rate Limiting - IMDb Scraping
        public bool ImdbRateLimitEnabled { get; set; }
        public int ImdbDailyLimit { get; set; }

        // Rating Options
        public ApiMode ApiMode { get; set; }
        public bool UpdateCommunityRating { get; set; }
        public bool UpdateCriticRating { get; set; }
        public CommunityRatingSource CommunityRatingSource { get; set; }

        // Legacy options kept for backward compatibility with older config payloads/UI.
        public RatingSource PreferredSource { get; set; }
        public bool AllowAlternateSourceFallback { get; set; }
        
        // Community Rating Source configured by item type
        public CommunityRatingSource MoviesRatingSource { get; set; }
        public CommunityRatingSource SeriesRatingSource { get; set; }
        public CommunityRatingSource EpisodesRatingSource { get; set; }
        public CommunityRatingSource MoviesFallbackRatingSource { get; set; }
        public CommunityRatingSource SeriesFallbackRatingSource { get; set; }

        // Critic Rating Source configured by item type
        public CriticRatingSource MoviesCriticRatingSource { get; set; }
        public CriticRatingSource SeriesCriticRatingSource { get; set; }
        
        // Item Types
        public bool UpdateMovies { get; set; }
        public bool UpdateSeries { get; set; }
        public bool UpdateEpisodes { get; set; }
        
        // IMDb Fallback (imdbapi.dev)
        public bool EnableImdbScraping { get; set; }
        
        // Smart Scanning
        public int RescanIntervalDays { get; set; }
        public bool PrioritizeRecentlyAdded { get; set; }
        public int RecentlyAddedDays { get; set; }
        public bool SkipUnratedOnly { get; set; }
        
        // Testing
        public bool TestMode { get; set; }

        public PluginConfiguration()
        {
            OmdbApiKey = string.Empty;
            MdbListApiKey = string.Empty;
            OmdbRateLimitEnabled = true;
            OmdbDailyLimit = 1000;
            MdbListRateLimitEnabled = true;
            MdbListDailyLimit = 1000;
            ImdbRateLimitEnabled = true;
            ImdbDailyLimit = 1000;
            UpdateCommunityRating = true;
            UpdateCriticRating = true;
            CommunityRatingSource = CommunityRatingSource.IMDb;
            PreferredSource = RatingSource.OMDb;
            AllowAlternateSourceFallback = true;
            MoviesRatingSource = CommunityRatingSource.IMDb;
            SeriesRatingSource = CommunityRatingSource.IMDb;
            EpisodesRatingSource = CommunityRatingSource.IMDb;
            MoviesFallbackRatingSource = CommunityRatingSource.None;
            SeriesFallbackRatingSource = CommunityRatingSource.None;
            MoviesCriticRatingSource = CriticRatingSource.RottenTomatoes;
            SeriesCriticRatingSource = CriticRatingSource.RottenTomatoes;
             UpdateMovies = true;
             UpdateSeries = true;
             UpdateEpisodes = false;
             EnableImdbScraping = false;
             RescanIntervalDays = 30;
             PrioritizeRecentlyAdded = true;
             RecentlyAddedDays = 7;
             SkipUnratedOnly = false;
             TestMode = false;
         }
     }

    public enum RatingSource
    {
        OMDb,
        MDBList,
        Both
    }

    public enum ApiMode
    {
        OMDbOnly,
        MDBListOnly,
        OMDbWithMDBListFallback,
        MDBListWithOMDbFallback,
        Both
    }

    public enum CommunityRatingSource
    {
        IMDb,
        Popcorn,
        Metacritic,
        MetacriticUser,
        MdbList,
        Trakt,
        Tmdb,
        Letterboxd,
        RogerEbert,
        None
    }

    public enum CriticRatingSource
    {
        RottenTomatoes,
        Metacritic,
        RottenTomatoesWithMetacriticFallback,
        MetacriticWithRottenTomatoesFallback,
        None
    }

    #endregion

    #region Scan History

    [DataContract]
    public class ScanHistoryEntry
    {
        [DataMember]
        public DateTime LastScanned { get; set; }
        [DataMember]
        public float? LastRating { get; set; }
        [DataMember]
        public float? LastCriticRating { get; set; }
        [DataMember]
        public string LastChange { get; set; }
    }

    [DataContract]
    public class ScanSession
    {
        [DataMember]
        public string SessionId { get; set; }
        [DataMember]
        public DateTime StartTime { get; set; }
        [DataMember]
        public DateTime? EndTime { get; set; }
        [DataMember]
        public int TotalItems { get; set; }
        [DataMember]
        public int ProcessedItems { get; set; }
        [DataMember]
        public int UpdatedItems { get; set; }
        [DataMember]
        public int SkippedItems { get; set; }
        [DataMember]
        public int ErrorItems { get; set; }

        [DataMember]
        public int OmdbRequests { get; set; }
        [DataMember]
        public int MdbListRequests { get; set; }
        [DataMember]
        public int ImdbScrapeRequests { get; set; }
        [DataMember]
        public bool WasCancelled { get; set; }
        [DataMember]
        public List<string> UpdatedItemNames { get; set; } = new List<string>();

        // Report samples (capped to avoid bloating scan_history.json)
        [DataMember]
        public Dictionary<string, string> UpdatedDetails { get; set; } = new Dictionary<string, string>();
        [DataMember]
        public Dictionary<string, string> SkippedDetails { get; set; } = new Dictionary<string, string>();
        [DataMember]
        public Dictionary<string, string> FailureDetails { get; set; } = new Dictionary<string, string>();
    }

    [DataContract]
    public class ScanReportEntry
    {
        [DataMember]
        public string Name { get; set; }
        [DataMember]
        public string Detail { get; set; }
    }

    [DataContract]
    public class ScanSessionReport
    {
        [DataMember]
        public string SessionId { get; set; }
        [DataMember]
        public DateTime StartTime { get; set; }
        [DataMember]
        public DateTime? EndTime { get; set; }
        [DataMember]
        public int TotalItems { get; set; }
        [DataMember]
        public int ProcessedItems { get; set; }
        [DataMember]
        public int UpdatedItems { get; set; }
        [DataMember]
        public int SkippedItems { get; set; }
        [DataMember]
        public int ErrorItems { get; set; }
        [DataMember]
        public bool WasCancelled { get; set; }

        [DataMember]
        public int OmdbRequests { get; set; }
        [DataMember]
        public int MdbListRequests { get; set; }
        [DataMember]
        public int ImdbScrapeRequests { get; set; }

        [DataMember]
        public List<ScanReportEntry> Updated { get; set; } = new List<ScanReportEntry>();
        [DataMember]
        public List<ScanReportEntry> Skipped { get; set; } = new List<ScanReportEntry>();
        [DataMember]
        public List<ScanReportEntry> Errors { get; set; } = new List<ScanReportEntry>();
    }

    [DataContract]
    public class ScanHistory
    {
        [DataMember]
        public Dictionary<string, ScanHistoryEntry> Items { get; set; } = new Dictionary<string, ScanHistoryEntry>();
        [DataMember]
        public Dictionary<string, int> OmdbDailyRequests { get; set; } = new Dictionary<string, int>();
        [DataMember]
        public Dictionary<string, int> MdbListDailyRequests { get; set; } = new Dictionary<string, int>();

        [DataMember]
        public Dictionary<string, int> ImdbDailyScrapes { get; set; } = new Dictionary<string, int>();
        [DataMember]
        public List<ScanSession> Sessions { get; set; } = new List<ScanSession>();
        
        public int GetTodayRequestCount(string api)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var dict = api == "omdb" ? OmdbDailyRequests : MdbListDailyRequests;
            return dict.TryGetValue(today, out var count) ? count : 0;
        }

        public int GetTodayImdbScrapeCount()
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            return ImdbDailyScrapes != null && ImdbDailyScrapes.TryGetValue(today, out var count) ? count : 0;
        }
        
        public void IncrementRequestCount(string api, int amount = 1)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var dict = api == "omdb" ? OmdbDailyRequests : MdbListDailyRequests;
            
            if (dict.ContainsKey(today))
                dict[today] += amount;
            else
                dict[today] = amount;
            
            // Clean up old entries (keep last 7 days)
            var cutoff = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
            var keysToRemove = dict.Keys.Where(k => string.Compare(k, cutoff) < 0).ToList();
            foreach (var key in keysToRemove)
                dict.Remove(key);
        }

        public void IncrementImdbScrapeCount(int amount = 1)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (ImdbDailyScrapes == null) ImdbDailyScrapes = new Dictionary<string, int>();

            if (ImdbDailyScrapes.ContainsKey(today))
                ImdbDailyScrapes[today] += amount;
            else
                ImdbDailyScrapes[today] = amount;

            // Clean up old entries (keep last 7 days)
            var cutoff = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
            var keysToRemove = ImdbDailyScrapes.Keys.Where(k => string.Compare(k, cutoff) < 0).ToList();
            foreach (var key in keysToRemove)
                ImdbDailyScrapes.Remove(key);
        }
    }

    public static class ScanHistoryManager
    {
        private static ScanHistory _history;
        private static readonly object _lock = new object();
        private static string _historyPath;
        private static string _reportsDir;
        private static ILogger _logger;

        public static void SetLogger(ILogger logger)
        {
            _logger = logger;
        }

        public static void Initialize(string dataPath)
        {
            _historyPath = Path.Combine(dataPath, "scan_history.json");
            _reportsDir = Path.Combine(dataPath, "scan_reports");
            try
            {
                if (!Directory.Exists(_reportsDir))
                    Directory.CreateDirectory(_reportsDir);
            }
            catch (Exception ex) { _logger?.ErrorException("RatingSync: failed to create reports directory", ex); }
            Load();
        }

        private static string GetReportPath(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(_reportsDir) || string.IsNullOrWhiteSpace(sessionId))
                return null;
            return Path.Combine(_reportsDir, sessionId + ".json");
        }

        private static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_historyPath))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(ScanHistory));
                        using (var stream = File.OpenRead(_historyPath))
                        {
                            _history = (ScanHistory)serializer.ReadObject(stream) ?? new ScanHistory();
                        }
                        // Ensure all collections are initialized (for backward compatibility)
                        if (_history.Items == null) _history.Items = new Dictionary<string, ScanHistoryEntry>();
                        if (_history.Sessions == null) _history.Sessions = new List<ScanSession>();
                        if (_history.OmdbDailyRequests == null) _history.OmdbDailyRequests = new Dictionary<string, int>();
                        if (_history.MdbListDailyRequests == null) _history.MdbListDailyRequests = new Dictionary<string, int>();
                        if (_history.ImdbDailyScrapes == null) _history.ImdbDailyScrapes = new Dictionary<string, int>();

                        foreach (var session in _history.Sessions)
                        {
                            if (session == null) continue;
                            if (session.UpdatedItemNames == null) session.UpdatedItemNames = new List<string>();
                            if (session.UpdatedDetails == null) session.UpdatedDetails = new Dictionary<string, string>();
                            if (session.SkippedDetails == null) session.SkippedDetails = new Dictionary<string, string>();
                            if (session.FailureDetails == null) session.FailureDetails = new Dictionary<string, string>();
                        }
                    }
                    else
                    {
                        _history = new ScanHistory();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.ErrorException("RatingSync: failed to load scan history, starting fresh", ex);
                    _history = new ScanHistory();
                }
            }
        }

        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    var serializer = new DataContractJsonSerializer(typeof(ScanHistory));
                    using (var stream = File.Create(_historyPath))
                    {
                        serializer.WriteObject(stream, _history);
                    }
                }
                catch (Exception ex) { _logger?.ErrorException("RatingSync: failed to save scan history", ex); }
            }
        }

        public static ScanHistoryEntry GetEntry(string itemId)
        {
            lock (_lock)
            {
                return _history.Items.TryGetValue(itemId, out var entry) ? entry : null;
            }
        }

        public static void SetEntry(string itemId, float? rating)
        {
            lock (_lock)
            {
                _history.Items[itemId] = new ScanHistoryEntry
                {
                    LastScanned = DateTime.UtcNow,
                    LastRating = rating
                };
            }
        }

        public static int GetTodayRequestCount(string api)
        {
            lock (_lock)
            {
                return _history.GetTodayRequestCount(api);
            }
        }

        public static void IncrementRequestCount(string api, int amount = 1)
        {
            lock (_lock)
            {
                _history.IncrementRequestCount(api, amount);
            }
        }

        public static int GetTodayImdbScrapeCount()
        {
            lock (_lock)
            {
                return _history.GetTodayImdbScrapeCount();
            }
        }

        public static void IncrementImdbScrapeCount(int amount = 1)
        {
            lock (_lock)
            {
                _history.IncrementImdbScrapeCount(amount);
            }
        }

        public static void CleanupOldEntries(int keepDays = 90)
        {
            lock (_lock)
            {
                var cutoff = DateTime.UtcNow.AddDays(-keepDays);
                var keysToRemove = _history.Items.Where(kvp => kvp.Value.LastScanned < cutoff).Select(kvp => kvp.Key).ToList();
                foreach (var key in keysToRemove)
                    _history.Items.Remove(key);
                
                // Also cleanup old sessions (keep last 50)
                if (_history.Sessions.Count > 50)
                {
                    _history.Sessions = _history.Sessions.OrderByDescending(s => s.StartTime).Take(50).ToList();
                }
            }
        }

        public static ScanSession StartSession(int totalItems)
        {
            lock (_lock)
            {
                var session = new ScanSession
                {
                    SessionId = Guid.NewGuid().ToString("N"),
                    StartTime = DateTime.UtcNow,
                    TotalItems = totalItems
                };
                _history.Sessions.Insert(0, session);
                return session;
            }
        }

        public static void EndSession(ScanSession session, int processed, int updated, int skipped, int errors, bool cancelled,
            int omdbRequests,
            int mdbListRequests,
            int imdbScrapeRequests,
            List<string> updatedNames,
            Dictionary<string, string> updatedDetails,
            Dictionary<string, string> skippedDetails,
            Dictionary<string, string> failureDetails)
        {
            lock (_lock)
            {
                session.EndTime = DateTime.UtcNow;
                session.ProcessedItems = processed;
                session.UpdatedItems = updated;
                session.SkippedItems = skipped;
                session.ErrorItems = errors;
                session.WasCancelled = cancelled;
                session.OmdbRequests = omdbRequests;
                session.MdbListRequests = mdbListRequests;
                session.ImdbScrapeRequests = imdbScrapeRequests;
                session.UpdatedItemNames = updatedNames.Take(100).ToList(); // Keep last 100 item names per session

                session.UpdatedDetails = (updatedDetails ?? new Dictionary<string, string>()).Take(100).ToDictionary(k => k.Key, v => v.Value);
                session.SkippedDetails = (skippedDetails ?? new Dictionary<string, string>()).Take(100).ToDictionary(k => k.Key, v => v.Value);
                session.FailureDetails = (failureDetails ?? new Dictionary<string, string>()).Take(200).ToDictionary(k => k.Key, v => v.Value);
            }
        }

        public static List<ScanSession> GetSessions(int count = 20)
        {
            lock (_lock)
            {
                return _history.Sessions.Take(count).ToList();
            }
        }

        public static void SaveReport(ScanSession session,
            Dictionary<string, string> updatedDetails,
            Dictionary<string, string> skippedDetails,
            Dictionary<string, string> failureDetails)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.SessionId))
                return;

            var path = GetReportPath(session.SessionId);
            if (string.IsNullOrWhiteSpace(path))
                return;

            var report = new ScanSessionReport
            {
                SessionId = session.SessionId,
                StartTime = session.StartTime,
                EndTime = session.EndTime,
                TotalItems = session.TotalItems,
                ProcessedItems = session.ProcessedItems,
                UpdatedItems = session.UpdatedItems,
                SkippedItems = session.SkippedItems,
                ErrorItems = session.ErrorItems,
                WasCancelled = session.WasCancelled,
                OmdbRequests = session.OmdbRequests,
                MdbListRequests = session.MdbListRequests,
                ImdbScrapeRequests = session.ImdbScrapeRequests,
                Updated = (updatedDetails ?? new Dictionary<string, string>()).Select(kvp => new ScanReportEntry { Name = kvp.Key, Detail = kvp.Value }).ToList(),
                Skipped = (skippedDetails ?? new Dictionary<string, string>()).Select(kvp => new ScanReportEntry { Name = kvp.Key, Detail = kvp.Value }).ToList(),
                Errors = (failureDetails ?? new Dictionary<string, string>()).Select(kvp => new ScanReportEntry { Name = kvp.Key, Detail = kvp.Value }).ToList(),
            };

            lock (_lock)
            {
                try
                {
                    var serializer = new DataContractJsonSerializer(typeof(ScanSessionReport));
                    using (var stream = File.Create(path))
                    {
                        serializer.WriteObject(stream, report);
                    }
                }
                catch (Exception ex) { _logger?.ErrorException("RatingSync: failed to save scan report", ex); }
            }
        }

        public static ScanSessionReport GetReport(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return null;

            var path = GetReportPath(sessionId);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            lock (_lock)
            {
                try
                {
                    var serializer = new DataContractJsonSerializer(typeof(ScanSessionReport));
                    using (var stream = File.OpenRead(path))
                    {
                        return (ScanSessionReport)serializer.ReadObject(stream);
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        public static void CleanupOldReports(int keepSessions = 20)
        {
            if (string.IsNullOrWhiteSpace(_reportsDir) || !Directory.Exists(_reportsDir))
                return;

            lock (_lock)
            {
                try
                {
                    var keepIds = new HashSet<string>(_history.Sessions
                        .Where(s => s != null && !string.IsNullOrWhiteSpace(s.SessionId))
                        .OrderByDescending(s => s.StartTime)
                        .Take(keepSessions)
                        .Select(s => s.SessionId));

                    var files = Directory.GetFiles(_reportsDir, "*.json");
                    foreach (var file in files)
                    {
                        var id = Path.GetFileNameWithoutExtension(file);
                        if (!keepIds.Contains(id))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                }
                catch { }
            }
        }

        public static void UpdateItemEntry(string itemId, float? rating, float? criticRating, string change)
        {
            lock (_lock)
            {
                _history.Items[itemId] = new ScanHistoryEntry
                {
                    LastScanned = DateTime.UtcNow,
                    LastRating = rating,
                    LastCriticRating = criticRating,
                    LastChange = change
                };
            }
        }

        public static bool DeleteSession(string sessionId, out string message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                message = "Missing SessionId";
                return false;
            }

            lock (_lock)
            {
                var session = _history.Sessions.FirstOrDefault(s => s != null && string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
                if (session == null)
                {
                    message = "Scan not found";
                    return false;
                }

                // Avoid deleting in-progress scans
                if (!session.EndTime.HasValue && !session.WasCancelled)
                {
                    message = "Cannot delete a scan that is still in progress";
                    return false;
                }

                _history.Sessions.Remove(session);

                try
                {
                    var reportPath = GetReportPath(sessionId);
                    if (!string.IsNullOrWhiteSpace(reportPath) && File.Exists(reportPath))
                        File.Delete(reportPath);
                }
                catch { }

                message = "Scan deleted";
                return true;
            }
        }

        public static Dictionary<string, ScanHistoryEntry> GetAllEntries()
        {
            lock (_lock)
            {
                return new Dictionary<string, ScanHistoryEntry>(_history.Items);
            }
        }
    }

    #endregion

    #region Scheduled Task

    public class RatingRefreshTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;

        // Single shared HttpClient — avoids socket/port exhaustion from per-call instantiation.
        // AutomaticDecompression handles gzip/deflate responses (IMDb serves compressed HTML).
        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler();
            if (handler.SupportsAutomaticDecompression)
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(20);
            return client;
        }

        // Browser-like headers for direct IMDb HTML scraping (APIs don't need these).
        private static void AddScrapeHeaders(HttpRequestMessage request)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        }

        private static readonly object _imdbEpisodeCacheLock = new object();
        private static readonly Dictionary<string, Dictionary<int, string>> _imdbEpisodeIdCache = new Dictionary<string, Dictionary<int, string>>();
        private static readonly object _mdbListRateLimitLock = new object();
        private static DateTime? _mdbListBlockedUntilUtc = null;
        private static string _mdbListBlockedMessage = null;
        // Per-run cache: series IMDb ID → MDBList show response (null = tried, not found)
        private Dictionary<string, MdbListResponse> _mdbShowCache;

        public RatingRefreshTask(ILibraryManager libraryManager, ILogManager logManager, IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger(GetType().Name);
            _jsonSerializer = jsonSerializer;
            ScanHistoryManager.SetLogger(_logger);
        }

        public string Name => "Refresh Ratings";

        public string Description => "Updates community and critic ratings from IMDb, Rotten Tomatoes, Metacritic, MDBList, and more.";

        public string Category => "Library";

        public string Key => "RatingSync";

        private void Log(string message, string type = "info")
        {
            switch (type)
            {
                case "error":
                    _logger.Error(message);
                    break;
                case "warning":
                    _logger.Warn(message);
                    break;
                case "skip":
                    // Skips are high-volume on large libraries — keep out of Info to avoid log spam.
                    _logger.Debug(message);
                    break;
                case "success":
                case "info":
                default:
                    _logger.Info(message);
                    break;
            }
        }

        private bool IsMdbListTemporarilyBlocked(out string message)
        {
            lock (_mdbListRateLimitLock)
            {
                if (_mdbListBlockedUntilUtc.HasValue)
                {
                    var now = DateTime.UtcNow;
                    if (now < _mdbListBlockedUntilUtc.Value)
                    {
                        message = string.IsNullOrWhiteSpace(_mdbListBlockedMessage)
                            ? $"MDBList API limit reached (blocked until {_mdbListBlockedUntilUtc.Value:u})"
                            : _mdbListBlockedMessage;
                        return true;
                    }

                    _mdbListBlockedUntilUtc = null;
                    _mdbListBlockedMessage = null;
                }
            }

            message = null;
            return false;
        }

        private void SetMdbListTemporaryBlock(DateTime? blockedUntilUtc, string message)
        {
            if (!blockedUntilUtc.HasValue || blockedUntilUtc.Value <= DateTime.UtcNow)
                return;

            lock (_mdbListRateLimitLock)
            {
                if (!_mdbListBlockedUntilUtc.HasValue || blockedUntilUtc.Value > _mdbListBlockedUntilUtc.Value)
                {
                    _mdbListBlockedUntilUtc = blockedUntilUtc.Value;
                    _mdbListBlockedMessage = message;
                }
            }
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            Log("Starting rating refresh task...", "info");

            var config = Plugin.Instance.Configuration;

            if (string.IsNullOrEmpty(config.OmdbApiKey) && string.IsNullOrEmpty(config.MdbListApiKey))
            {
                Log("No API keys configured. Please configure at least one API key in plugin settings.", "error");
                return;
            }

            // Check daily API limits for each API
            var omdbRequests = ScanHistoryManager.GetTodayRequestCount("omdb");
            var mdblistRequests = ScanHistoryManager.GetTodayRequestCount("mdblist");
            var imdbScrapesUsed = ScanHistoryManager.GetTodayImdbScrapeCount();
            var omdbLimitReached = config.OmdbRateLimitEnabled && omdbRequests >= config.OmdbDailyLimit;
            var mdblistLimitReached = config.MdbListRateLimitEnabled && mdblistRequests >= config.MdbListDailyLimit;
            var imdbLimitReached = config.ImdbRateLimitEnabled && imdbScrapesUsed >= config.ImdbDailyLimit;
            
            // Log current API usage
            if (config.OmdbRateLimitEnabled && !string.IsNullOrEmpty(config.OmdbApiKey))
            {
                Log($"OMDb: {omdbRequests}/{config.OmdbDailyLimit} requests used today{(omdbLimitReached ? " (LIMIT REACHED)" : "")}", omdbLimitReached ? "warning" : "info");
            }
            if (config.MdbListRateLimitEnabled && !string.IsNullOrEmpty(config.MdbListApiKey))
            {
                Log($"MDBList: {mdblistRequests}/{config.MdbListDailyLimit} requests used today{(mdblistLimitReached ? " (LIMIT REACHED)" : "")}", mdblistLimitReached ? "warning" : "info");
            }
            if (config.ImdbRateLimitEnabled && config.EnableImdbScraping)
            {
                Log($"imdbapi.dev: {imdbScrapesUsed}/{config.ImdbDailyLimit} calls used today{(imdbLimitReached ? " (LIMIT REACHED)" : "")}", imdbLimitReached ? "warning" : "info");
            }

            var mdbListBlockedByApi = IsMdbListTemporarilyBlocked(out var mdbListBlockMessage);
            if (mdbListBlockedByApi && !string.IsNullOrWhiteSpace(mdbListBlockMessage))
            {
                Log(mdbListBlockMessage, "warning");
            }
            
            // Check if any API is available
            var hasOmdb = !string.IsNullOrEmpty(config.OmdbApiKey) && !omdbLimitReached;
            var hasMdbList = !string.IsNullOrEmpty(config.MdbListApiKey) && !mdblistLimitReached && !mdbListBlockedByApi;
            var hasImdbScraping = config.EnableImdbScraping && !imdbLimitReached;
            
            if (!hasOmdb && !hasMdbList && !hasImdbScraping)
            {
                Log("All configured APIs have reached their daily limits. Task will resume tomorrow.", "warning");
                return;
            }

            // Declare these outside try block for catch access
            ScanSession scanSession = null;
            var updatedNames = new List<string>();
            int updated = 0;
            int skipped = 0;
            int errors = 0;
            int processed = 0;
            int omdbCalls = 0;
            int mdblistCalls = 0;
            int imdbScrapeCalls = 0;
                bool mdbListApiRateLimited = false;
            bool wasCancelled = false;

            try
            {
                List<BaseItem> items;
                bool isSelectedScan = SelectedItemsStore.HasItems;
                int skippedByInterval = 0;
                int skippedHasRating = 0;
                
                if (isSelectedScan)
                {
                    bool forceRefresh = SelectedItemsStore.ForceRefresh;
                    items = SelectedItemsStore.GetAndClearItems();

                    if (!forceRefresh && config.RescanIntervalDays > 0)
                    {
                        var rescanCutoff = DateTime.UtcNow.AddDays(-config.RescanIntervalDays);
                        var beforeFilter = items.Count;
                        items = items.Where(i =>
                        {
                            var entry = ScanHistoryManager.GetEntry(i.InternalId.ToString());
                            return entry == null || entry.LastScanned <= rescanCutoff;
                        }).ToList();
                        skippedByInterval = beforeFilter - items.Count;
                        if (skippedByInterval > 0)
                            Log($"Skipped {skippedByInterval} item(s) scanned within the last {config.RescanIntervalDays} day(s). Enable Force Refresh to override.", "info");
                    }

                    if (!forceRefresh && config.SkipUnratedOnly)
                    {
                        var beforeSkip = items.Count;
                        items = items.Where(i => !i.CommunityRating.HasValue && !i.CriticRating.HasValue).ToList();
                        skippedHasRating = beforeSkip - items.Count;
                        if (skippedHasRating > 0)
                            Log($"Skipped {skippedHasRating} item(s) with existing ratings (Only scan unrated items is enabled). Enable Force Refresh to override.", "info");
                    }

                    Log($"Running targeted scan on {items.Count} selected item(s){(forceRefresh ? " (force refresh)" : "")}", "info");
                }
                else
                {
                    // Build list of item types based on configuration
                    var itemTypes = new List<string>();
                    if (config.UpdateMovies) itemTypes.Add("Movie");
                    if (config.UpdateSeries) itemTypes.Add("Series");
                    if (config.UpdateEpisodes) itemTypes.Add("Episode");

                    if (itemTypes.Count == 0)
                    {
                        Log("No item types selected. Please enable at least one item type in settings.", "error");
                        return;
                    }

                    Log($"Processing item types: {string.Join(", ", itemTypes)}", "info");

                    items = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = itemTypes.ToArray(),
                        Recursive = true
                    }).Where(i => 
                    {
                        // Include items with direct IMDb ID
                        if (i.HasProviderId(MetadataProviders.Imdb))
                            return true;
                        
                        // Include episodes where parent series has IMDb ID
                        if (config.UpdateEpisodes && i is Episode episode)
                        {
                            // Skip Specials / Season 0
                            if ((episode.ParentIndexNumber ?? 0) == 0)
                                return false;
                            var series = episode.Series;
                            return series != null && series.HasProviderId(MetadataProviders.Imdb);
                        }
                        
                        return false;
                    }).ToList();

                    // Never process Specials / Season 0 episodes
                    if (config.UpdateEpisodes)
                    {
                        items = items.Where(i => !(i is Episode ep && (ep.ParentIndexNumber ?? 0) == 0)).ToList();
                    }

                    // Log breakdown by type
                    var movieCount = items.Count(i => i is Movie);
                    var seriesCount = items.Count(i => i is MediaBrowser.Controller.Entities.TV.Series);
                    var episodeCount = items.Count(i => i is Episode);
                    Log($"Found {items.Count} items: {movieCount} movies, {seriesCount} series, {episodeCount} episodes", "info");

                    // Apply smart scanning filters
                    var originalCount = items.Count;
                    var rescanCutoff = DateTime.UtcNow.AddDays(-config.RescanIntervalDays);
                    var recentlyAddedCutoff = DateTime.UtcNow.AddDays(-config.RecentlyAddedDays);
                    int skippedByHistory = 0;

                    items = items.Where(i =>
                    {
                        var itemId = i.InternalId.ToString();
                        var historyEntry = ScanHistoryManager.GetEntry(itemId);
                        
                        // Skip if scanned recently (within rescan interval)
                        if (historyEntry != null && historyEntry.LastScanned > rescanCutoff)
                        {
                            skippedByHistory++;
                            return false;
                        }
                        
                        // Optionally skip items that already have ratings
                        if (config.SkipUnratedOnly && (i.CommunityRating.HasValue || i.CriticRating.HasValue))
                        {
                            skippedHasRating++;
                            return false;
                        }
                        
                        return true;
                    }).ToList();

                    if (skippedByHistory > 0 || skippedHasRating > 0)
                    {
                        Log($"Smart scan: Skipped {skippedByHistory} recently scanned, {skippedHasRating} already rated", "info");
                    }
                    Log($"Items to process after filtering: {items.Count} (of {originalCount} total)", "info");

                    // Prioritize recently added content
                    if (config.PrioritizeRecentlyAdded)
                    {
                        var recentItems = items.Where(i => i.DateCreated > recentlyAddedCutoff).ToList();
                        var olderItems = items.Where(i => i.DateCreated <= recentlyAddedCutoff).ToList();
                        
                        if (recentItems.Any())
                        {
                            Log($"Prioritizing {recentItems.Count} recently added items (last {config.RecentlyAddedDays} days)", "info");
                            items = recentItems.Concat(olderItems).ToList();
                        }
                    }

                    // Test mode: only process first item
                    if (config.TestMode && items.Count > 0)
                    {
                        items = new List<BaseItem> { items[0] };
                        Log($"TEST MODE: Processing only first item", "warning");
                    }
                }

                // Safety: never process Specials / Season 0 episodes (also applies to selected scans)
                items = items.Where(i => !(i is Episode ep && (ep.ParentIndexNumber ?? 0) == 0)).ToList();

                // Prioritize items with no rating so API quota goes to filling gaps first
                {
                    bool IsUnrated(BaseItem i) =>
                        (config.UpdateCommunityRating && (!i.CommunityRating.HasValue || i.CommunityRating.Value == 0f)) ||
                        (config.UpdateCriticRating && (!i.CriticRating.HasValue || i.CriticRating.Value == 0f));
                    var unratedItems = items.Where(IsUnrated).ToList();
                    if (unratedItems.Count > 0 && unratedItems.Count < items.Count)
                    {
                        var ratedItems = items.Where(i => !IsUnrated(i)).ToList();
                        Log($"Prioritizing {unratedItems.Count} unrated item(s) ahead of {ratedItems.Count} already rated", "info");
                        items = unratedItems.Concat(ratedItems).ToList();
                    }
                }

                ProgressTracker.Start(items.Count);
                if (skippedByInterval > 0)
                    ProgressTracker.SetFilteredByInterval(skippedByInterval);
                scanSession = ScanHistoryManager.StartSession(items.Count);

                _mdbShowCache = new Dictionary<string, MdbListResponse>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Log("Task cancelled by user", "warning");
                        wasCancelled = true;
                        break;
                    }

                    // Re-check API limits dynamically
                    var currentOmdbLimitReached = config.OmdbRateLimitEnabled && (omdbRequests + omdbCalls) >= config.OmdbDailyLimit;
                    var currentMdbListLimitReached = config.MdbListRateLimitEnabled && (mdblistRequests + mdblistCalls) >= config.MdbListDailyLimit;
                    var currentImdbLimitReached = config.ImdbRateLimitEnabled && (imdbScrapesUsed + imdbScrapeCalls) >= config.ImdbDailyLimit;
                    var currentMdbListBlockedByApi = IsMdbListTemporarilyBlocked(out var currentMdbListBlockedMessage);
                    var currentHasOmdb = !string.IsNullOrEmpty(config.OmdbApiKey) && !currentOmdbLimitReached;
                    var currentHasMdbList = !string.IsNullOrEmpty(config.MdbListApiKey) && !currentMdbListLimitReached && !mdbListApiRateLimited && !currentMdbListBlockedByApi;
                    var currentHasImdbScraping = config.EnableImdbScraping && !currentImdbLimitReached;
                    string currentMdbListUnavailableReason = null;
                    if (!currentHasMdbList)
                    {
                        if (string.IsNullOrWhiteSpace(config.MdbListApiKey))
                        {
                            currentMdbListUnavailableReason = "MDBList unavailable: no API key configured";
                        }
                        else if (currentMdbListBlockedByApi && !string.IsNullOrWhiteSpace(currentMdbListBlockedMessage))
                        {
                            currentMdbListUnavailableReason = currentMdbListBlockedMessage;
                        }
                        else if (mdbListApiRateLimited)
                        {
                            currentMdbListUnavailableReason = "MDBList API limit reached (HTTP 429)";
                        }
                        else if (currentMdbListLimitReached && config.MdbListRateLimitEnabled)
                        {
                            currentMdbListUnavailableReason = $"MDBList daily limit reached ({mdblistRequests + mdblistCalls}/{config.MdbListDailyLimit})";
                        }
                    }
                    
                    var itemName = $"{item.Name}";
                    var itemId = item.InternalId.ToString();
                    var imdbId = item.GetProviderId(MetadataProviders.Imdb);
                    EpisodeInfo episodeInfo = null;
                    
                    // Handle episodes - get series IMDb ID and season/episode numbers
                    if (item is Episode episode)
                    {
                        var series = episode.Series;
                        if (series != null)
                        {
                            var seriesImdbId = series.GetProviderId(MetadataProviders.Imdb);
                            if (!string.IsNullOrEmpty(seriesImdbId))
                            {
                                episodeInfo = new EpisodeInfo
                                {
                                    SeriesImdbId = seriesImdbId,
                                    SeasonNumber = episode.ParentIndexNumber ?? 0,
                                    EpisodeNumber = episode.IndexNumber ?? 0,
                                    SeriesName = series.Name
                                };
                                itemName = $"{series.Name} S{episodeInfo.SeasonNumber:D2}E{episodeInfo.EpisodeNumber:D2} - {item.Name}";
                            }
                        }
                        
                        if (episodeInfo == null || episodeInfo.SeasonNumber == 0 || episodeInfo.EpisodeNumber == 0)
                        {
                            skipped++;
                            processed++;
                            ProgressTracker.AddSkipped(item.Name, "Missing season/episode info");
                            Log($"⊘ Skipped '{item.Name}' - missing season/episode info", "skip");
                            ProgressTracker.UpdateProgress(processed, updated, skipped, errors, itemName);
                            progress.Report((double)processed / items.Count * 100);
                            continue;
                        }
                    }

                    // For episodes, we can use OMDb, MDBList (via show seasons array), or imdbapi.dev fallback.
                    // For movies/series, we need at least one of OMDb or MdbList.
                    var currentHasAvailableApi = item is Episode
                        ? currentHasOmdb || currentHasMdbList || currentHasImdbScraping
                        : currentHasOmdb || currentHasMdbList;

                    if (!currentHasAvailableApi)
                    {
                        if (item is Episode)
                        {
                            var episodeSkipReasons = new List<string>();
                            if (string.IsNullOrWhiteSpace(config.OmdbApiKey))
                                episodeSkipReasons.Add("OMDb unavailable: no API key configured");
                            else if (currentOmdbLimitReached && config.OmdbRateLimitEnabled)
                                episodeSkipReasons.Add($"OMDb daily limit reached ({omdbRequests + omdbCalls}/{config.OmdbDailyLimit})");

                            if (!currentHasMdbList && !string.IsNullOrWhiteSpace(currentMdbListUnavailableReason))
                                episodeSkipReasons.Add(currentMdbListUnavailableReason);

                            if (!config.EnableImdbScraping)
                                episodeSkipReasons.Add("imdbapi.dev disabled");
                            else if (currentImdbLimitReached && config.ImdbRateLimitEnabled)
                                episodeSkipReasons.Add($"imdbapi.dev limit reached ({imdbScrapesUsed + imdbScrapeCalls}/{config.ImdbDailyLimit})");

                            var episodeSkipReason = string.Join(", ", episodeSkipReasons);
                            skipped++;
                            processed++;
                            ProgressTracker.AddSkipped(itemName, episodeSkipReason);
                            Log($"⊘ Skipped '{itemName}' - {episodeSkipReason}", "skip");
                            ProgressTracker.UpdateProgress(processed, updated, skipped, errors, itemName);
                            progress.Report((double)processed / items.Count * 100);
                            if (processed % 10 == 0)
                            {
                                ScanHistoryManager.Save();
                            }
                            continue;
                        }

                        var stopReason = currentMdbListLimitReached || mdbListApiRateLimited || currentMdbListBlockedByApi
                            ? "All usable APIs have reached their limits. Stopping for today."
                            : "No usable rating source is available. Stopping for today.";
                        Log(stopReason, "warning");
                        break;
                    }
                    
                    ProgressTracker.UpdateProgress(processed, updated, skipped, errors, itemName);

                    try
                    {
                        var logImdbId = !string.IsNullOrWhiteSpace(imdbId)
                            ? imdbId
                            : (episodeInfo != null ? episodeInfo.SeriesImdbId : "");
                        var itemTypeTag = item is Episode ? "Episode" : (item is MediaBrowser.Controller.Entities.TV.Series ? "Series" : "Movie");
                        Log($"Processing [{itemTypeTag}]: {itemName} ({logImdbId})", "info");
                        
                        CommunityRatingSource targetSource;
                        CriticRatingSource targetCriticSource;
                        CommunityRatingSource fallbackSource;
                        if (item is Episode)
                        {
                            targetSource = CommunityRatingSource.IMDb;
                            targetCriticSource = CriticRatingSource.None;
                            fallbackSource = CommunityRatingSource.None;
                        }
                        else if (item is Movie)
                        {
                            targetSource = config.MoviesRatingSource;
                            targetCriticSource = config.MoviesCriticRatingSource;
                            fallbackSource = config.MoviesFallbackRatingSource;
                        }
                        else
                        {
                            targetSource = config.SeriesRatingSource;
                            targetCriticSource = config.SeriesCriticRatingSource;
                            fallbackSource = config.SeriesFallbackRatingSource;
                        }

                        var currentImdbScrapesRemaining = config.ImdbRateLimitEnabled
                            ? Math.Max(0, config.ImdbDailyLimit - (imdbScrapesUsed + imdbScrapeCalls))
                            : int.MaxValue;

                        var ratings = await FetchRatings(imdbId, config, targetSource, targetCriticSource, episodeInfo, currentHasOmdb, currentHasMdbList, currentHasImdbScraping, currentImdbScrapesRemaining, fallbackSource);
                        
                        // Track API calls
                        if (ratings.UsedOmdb)
                        {
                            omdbCalls++;
                            ScanHistoryManager.IncrementRequestCount("omdb");
                        }
                        if (ratings.MdbListApiCallMade)
                        {
                            mdblistCalls++;
                            ScanHistoryManager.IncrementRequestCount("mdblist");
                            if (episodeInfo != null)
                                Log($"Fetched MDBList show data for '{episodeInfo.SeriesName}' — episode ratings cached for this run", "info");
                        }
                        if (ratings.MdbListRateLimited)
                        {
                            mdbListApiRateLimited = true;
                            SetMdbListTemporaryBlock(ratings.MdbListRateLimitUntilUtc, ratings.MdbListRateLimitMessage);
                            var limitMsg = string.IsNullOrWhiteSpace(ratings.MdbListRateLimitMessage)
                                ? "MDBList API limit reached (HTTP 429). Skipping further MDBList requests for this run."
                                : ratings.MdbListRateLimitMessage;
                            Log(limitMsg, "warning");
                        }
                        if (ratings.ImdbScrapeRequests > 0)
                        {
                            imdbScrapeCalls += ratings.ImdbScrapeRequests;
                            ScanHistoryManager.IncrementImdbScrapeCount(ratings.ImdbScrapeRequests);
                        }
                        // Per-source fallback log lines
                        if (episodeInfo != null)
                        {
                            if (ratings.MdbListHadNoEpisodeRating)
                                Log($"  MDBList: no episode rating found", "info");
                            if (ratings.OmdbHadNoEpisodeRating)
                                Log($"  OMDb: no episode rating found", "info");
                            if (ratings.ImdbScrapeAttempted && !ratings.UsedScraping)
                                Log($"  imdbapi.dev: no rating found", "info");
                        }
                        else
                        {
                            if (ratings.OmdbHadNoCommunityRating)
                                Log($"  OMDb: no community rating found", "info");
                            if (ratings.MdbListHadNoCommunityRating)
                                Log($"  MDBList: no community rating found", "info");
                            if (ratings.ImdbScrapeAttempted && !ratings.UsedScraping)
                                Log($"  imdbapi.dev: no rating found", "info");
                        }

                        // Determine source label for display
                        string sourceLabel;
                        if (ratings.UsedOmdb && ratings.UsedScraping)
                            sourceLabel = "imdbapi.dev (not in OMDb)";
                        else if (ratings.UsedScraping)
                            sourceLabel = "imdbapi.dev";
                        else if (ratings.UsedOmdb && ratings.UsedMdbList)
                            sourceLabel = "OMDb+MDBList";
                        else if (ratings.UsedOmdb)
                            sourceLabel = "OMDb";
                        else if (ratings.UsedMdbList)
                            sourceLabel = "MDBList";
                        else
                            sourceLabel = "unknown source";

                        bool itemUpdated = false;
                        var changes = new List<string>();

                        if (config.UpdateCommunityRating && ratings.CommunityRating.HasValue)
                        {
                            var oldRating = item.CommunityRating;
                            // Compare at display precision (1 decimal) to avoid thrashing on float noise.
                            if (!oldRating.HasValue || Math.Round(oldRating.Value, 1) != Math.Round(ratings.CommunityRating.Value, 1))
                            {
                                item.CommunityRating = ratings.CommunityRating.Value;
                                itemUpdated = true;
                                var actualSource = ratings.ActualCommunitySource ?? targetSource;
                                var communityLogName = GetCommunitySourceDisplayName(actualSource);
                                var fallbackSuffix = actualSource != targetSource ? $" [fallback from {GetCommunitySourceDisplayName(targetSource)}]" : "";
                                changes.Add($"{communityLogName}{fallbackSuffix}: {(oldRating.HasValue ? oldRating.Value.ToString("F1") : "none")} → {ratings.CommunityRating.Value:F1} ({sourceLabel})");
                            }
                        }

                        if (config.UpdateCriticRating && ratings.CriticRating.HasValue)
                        {
                            var oldCriticRating = item.CriticRating;
                            // Compare at display precision (whole percent) to avoid thrashing on float noise.
                            if (!oldCriticRating.HasValue || Math.Round(oldCriticRating.Value) != Math.Round(ratings.CriticRating.Value))
                            {
                                item.CriticRating = ratings.CriticRating.Value;
                                itemUpdated = true;
                                var criticLabel = (targetCriticSource == CriticRatingSource.Metacritic || targetCriticSource == CriticRatingSource.MetacriticWithRottenTomatoesFallback) ? "Metacritic" : "RT";
                                var criticApiSource = ratings.UsedMdbList ? "MDBList" : sourceLabel;
                                changes.Add($"{criticLabel}: {(oldCriticRating.HasValue ? oldCriticRating.Value.ToString("F0") + "%" : "none")} → {ratings.CriticRating.Value:F0}% ({criticApiSource})");
                            }
                        }

                        // Record in scan history with detailed info
                        var changeDetails = string.Join(", ", changes);
                        ScanHistoryManager.UpdateItemEntry(itemId, item.CommunityRating, item.CriticRating, itemUpdated ? changeDetails : null);

                        if (itemUpdated)
                        {
                            _libraryManager.UpdateItem(item, item.GetParent(), ItemUpdateType.MetadataEdit);
                            updated++;
                            updatedNames.Add(itemName);
                            ProgressTracker.AddUpdated(itemName, changeDetails);
                            Log($"✓ Updated '{itemName}': {changeDetails}", "success");
                        }
                        else
                        {
                            skipped++;
                            // Build detailed skip reason
                            var skipReasons = new List<string>();
                            if (ratings.MdbListRateLimited)
                            {
                                skipReasons.Add(string.IsNullOrWhiteSpace(ratings.MdbListRateLimitMessage)
                                    ? "MDBList rate limited"
                                    : ratings.MdbListRateLimitMessage);
                            }
                            if (!ratings.CommunityRating.HasValue && !ratings.CriticRating.HasValue)
                            {
                                if (!config.UpdateCommunityRating && !config.UpdateCriticRating)
                                {
                                    skipReasons.Add("Community and Critic updates are disabled in settings");
                                }
                                else if (!config.UpdateCommunityRating && config.UpdateCriticRating && targetCriticSource != CriticRatingSource.None && !ratings.CriticRating.HasValue)
                                {
                                    skipReasons.Add("Community updates disabled in settings");
                                    skipReasons.Add("No critic rating found");
                                }
                                else if (targetSource != CommunityRatingSource.IMDb && !currentHasMdbList && !string.IsNullOrWhiteSpace(currentMdbListUnavailableReason))
                                {
                                    skipReasons.Add(currentMdbListUnavailableReason);
                                }
                                else
                                {
                                    var sourceParts = new List<string>();
                                    if (ratings.MdbListHadNoEpisodeRating || ratings.MdbListHadNoCommunityRating || ratings.UsedMdbList)
                                        sourceParts.Add("MDBList");
                                    if (ratings.OmdbHadNoEpisodeRating || ratings.OmdbHadNoCommunityRating || ratings.UsedOmdb)
                                        sourceParts.Add("OMDb");
                                    if (ratings.ImdbScrapeAttempted)
                                        sourceParts.Add("imdbapi.dev");
                                    if (sourceParts.Count == 0)
                                        sourceParts.Add(targetSource != CommunityRatingSource.IMDb ? "MDBList" : "unknown source");
                                    skipReasons.Add($"No ratings found [{string.Join("+", sourceParts)}]");
                                }
                            }
                            else
                            {
                                var communityLogName = GetCommunitySourceDisplayName(targetSource);
                                if (config.UpdateCommunityRating && ratings.CommunityRating.HasValue && item.CommunityRating == ratings.CommunityRating.Value)
                                {
                                    skipReasons.Add($"{communityLogName} unchanged ({item.CommunityRating:F1})");
                                }
                                if (!config.UpdateCommunityRating)
                                {
                                    skipReasons.Add("Community updates disabled in settings");
                                }
                                if (config.UpdateCriticRating && targetCriticSource != CriticRatingSource.None && ratings.CriticRating.HasValue && item.CriticRating == ratings.CriticRating.Value)
                                {
                                    var criticUnchangedLabel = (targetCriticSource == CriticRatingSource.Metacritic || targetCriticSource == CriticRatingSource.MetacriticWithRottenTomatoesFallback) ? "Metacritic" : "RT";
                                    var criticUnchangedSource = ratings.UsedMdbList ? "MDBList" : (ratings.UsedOmdb ? "OMDb" : "API");
                                    skipReasons.Add($"{criticUnchangedLabel} unchanged ({item.CriticRating:F0}%) [{criticUnchangedSource}]");
                                }
                                if (config.UpdateCommunityRating && !ratings.CommunityRating.HasValue)
                                {
                                    if (targetSource != CommunityRatingSource.IMDb && !currentHasMdbList)
                                    {
                                        if (!string.IsNullOrWhiteSpace(currentMdbListUnavailableReason))
                                            skipReasons.Add(currentMdbListUnavailableReason);
                                        else
                                            skipReasons.Add($"No {communityLogName} rating [MDBList]");
                                    }
                                    else
                                    {
                                        var communitySourceLabel =
                                            targetSource != CommunityRatingSource.IMDb ? "MDBList"
                                            : (ratings.UsedOmdb && ratings.UsedMdbList ? "OMDb+MDBList"
                                            : (ratings.UsedOmdb ? "OMDb"
                                            : (ratings.UsedMdbList ? "MDBList" : "unknown source")));
                                        skipReasons.Add($"No {communityLogName} rating [{communitySourceLabel}]");
                                    }
                                }
                                if (config.UpdateCriticRating && targetCriticSource != CriticRatingSource.None && !ratings.CriticRating.HasValue)
                                {
                                    var criticMissingLabel = targetCriticSource switch
                                    {
                                        CriticRatingSource.Metacritic => "No Metacritic rating",
                                        CriticRatingSource.RottenTomatoesWithMetacriticFallback => "No RT or Metacritic rating",
                                        CriticRatingSource.MetacriticWithRottenTomatoesFallback => "No Metacritic or RT rating",
                                        _ => "No RT rating"
                                    };
                                    var criticMissingApi = ratings.UsedMdbList ? "MDBList" : (ratings.UsedOmdb ? "OMDb" : "API");
                                    skipReasons.Add($"{criticMissingLabel} [{criticMissingApi}]");
                                }
                            }
                            var skipReason = skipReasons.Count > 0 ? string.Join(", ", skipReasons) : "No changes needed";
                            ProgressTracker.AddSkipped(itemName, skipReason);
                            Log($"⊘ Skipped '{itemName}' - {skipReason}", "skip");
                        }

                        processed++;
                        ProgressTracker.UpdateProgress(processed, updated, skipped, errors, itemName);
                        progress.Report((double)processed / items.Count * 100);

                        // Save scan history periodically (every 10 items)
                        if (processed % 10 == 0)
                        {
                            ScanHistoryManager.Save();
                        }

                        // Rate limit to avoid overwhelming APIs — skip delay if no real HTTP call was made
                        bool madeApiCall = ratings.UsedOmdb || ratings.MdbListApiCallMade || ratings.ImdbScrapeRequests > 0;
                        if (madeApiCall)
                            await Task.Delay(1000, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        processed++;
                        ProgressTracker.AddFailure(itemName, ex.Message);
                        Log($"✗ Error processing '{itemName}': {ex.Message}", "error");
                        ProgressTracker.UpdateProgress(processed, updated, skipped, errors, itemName);
                        progress.Report((double)processed / items.Count * 100);
                    }
                }

                // Save scan session (include report samples)
                var progressSnapshot = ProgressTracker.GetProgress();
                ScanHistoryManager.EndSession(
                    scanSession,
                    processed,
                    updated,
                    skipped,
                    errors,
                    wasCancelled,
                    omdbCalls,
                    mdblistCalls,
                    imdbScrapeCalls,
                    updatedNames,
                    progressSnapshot.UpdatedDetails,
                    progressSnapshot.SkippedDetails,
                    progressSnapshot.FailureDetails);

                // Persist full report to a separate file (no sampling)
                ScanHistoryManager.SaveReport(scanSession,
                    progressSnapshot.UpdatedDetails,
                    progressSnapshot.SkippedDetails,
                    progressSnapshot.FailureDetails);
                
                // Save final scan history
                ScanHistoryManager.Save();
                ScanHistoryManager.CleanupOldEntries(90); // Keep 90 days of history
                ScanHistoryManager.CleanupOldReports(20);
                ScanHistoryManager.Save();
                
                ProgressTracker.Stop();
                _mdbShowCache = null;

                // Build API usage summary
                var apiUsage = new List<string>();
                if (!string.IsNullOrEmpty(config.OmdbApiKey))
                {
                    var omdbTotal = omdbRequests + omdbCalls;
                    apiUsage.Add($"OMDb: {omdbTotal}{(config.OmdbRateLimitEnabled ? "/" + config.OmdbDailyLimit : "")}");
                }
                if (!string.IsNullOrEmpty(config.MdbListApiKey))
                {
                    var mdblistTotal = mdblistRequests + mdblistCalls;
                    apiUsage.Add($"MDBList: {mdblistTotal}{(config.MdbListRateLimitEnabled ? "/" + config.MdbListDailyLimit : "")}");
                }
                if (config.EnableImdbScraping)
                {
                    var imdbTotal = imdbScrapesUsed + imdbScrapeCalls;
                    apiUsage.Add($"imdbapi.dev: {imdbTotal}{(config.ImdbRateLimitEnabled ? "/" + config.ImdbDailyLimit : "")}");
                }
                var apiInfo = apiUsage.Count > 0 ? $" | API calls: {string.Join(", ", apiUsage)}" : "";
                
                Log($"Rating refresh completed! Processed: {processed}, Updated: {updated}, Skipped: {skipped}, Errors: {errors}{apiInfo}", "success");
            }
            catch (Exception ex)
            {
                // End session on error
                if (scanSession != null)
                {
                    var progressSnapshot = ProgressTracker.GetProgress();
                    ScanHistoryManager.EndSession(
                        scanSession,
                        progressSnapshot.ProcessedItems,
                        updated,
                        skipped,
                        errors,
                        true,
                        omdbCalls,
                        mdblistCalls,
                        imdbScrapeCalls,
                        updatedNames,
                        progressSnapshot.UpdatedDetails,
                        progressSnapshot.SkippedDetails,
                        progressSnapshot.FailureDetails);

                    ScanHistoryManager.SaveReport(scanSession,
                        progressSnapshot.UpdatedDetails,
                        progressSnapshot.SkippedDetails,
                        progressSnapshot.FailureDetails);
                    ScanHistoryManager.Save();
                }
                
                ProgressTracker.Stop();
                _mdbShowCache = null;
                Log($"Fatal error during rating refresh: {ex.Message}", "error");
                _logger.ErrorException("Error during rating refresh", ex);
                throw;
            }
        }

        private async Task<RatingData> FetchRatings(string imdbId, PluginConfiguration config, CommunityRatingSource targetSource, CriticRatingSource criticSource, EpisodeInfo episodeInfo, bool canUseOmdb, bool canUseMdbList, bool canUseImdbScraping = true, int imdbScrapesRemaining = int.MaxValue, CommunityRatingSource fallbackSource = CommunityRatingSource.None)
        {
            var result = new RatingData();
            var remainingImdbScrapes = canUseImdbScraping ? Math.Max(0, imdbScrapesRemaining) : 0;

            async Task<T> TryImdbScrapeRequest<T>(Func<Task<T>> fetch, T defaultValue = default(T))
            {
                if (remainingImdbScrapes <= 0)
                    return defaultValue;

                remainingImdbScrapes--;
                result.ImdbScrapeAttempted = true;
                result.ImdbScrapeRequests++;
                return await fetch();
            }

            if (episodeInfo != null)
            {
                // MDBList first for episodes — one cached API call per series covers all episodes
                if (canUseMdbList && !string.IsNullOrEmpty(config.MdbListApiKey) && config.ApiMode != ApiMode.OMDbOnly)
                {
                    var mdbData = await FetchFromMdbList(imdbId, config.MdbListApiKey, targetSource, criticSource, episodeInfo);
                    if (mdbData.CommunityRating.HasValue)
                    {
                        result.CommunityRating = mdbData.CommunityRating;
                        result.ActualCommunitySource = mdbData.ActualCommunitySource;
                        result.UsedMdbList = true;
                    }
                    else if (!mdbData.MdbListRateLimited)
                    {
                        result.MdbListHadNoEpisodeRating = true;
                    }
                    if (mdbData.MdbListRateLimited)
                    {
                        result.MdbListRateLimited = true;
                        result.MdbListRateLimitMessage = mdbData.MdbListRateLimitMessage;
                        result.MdbListRateLimitUntilUtc = mdbData.MdbListRateLimitUntilUtc;
                    }
                    result.MdbListApiCallMade |= mdbData.MdbListApiCallMade;
                }

                // OMDb fallback if MDBList had no rating for this episode
                if (!result.CommunityRating.HasValue && canUseOmdb && !string.IsNullOrEmpty(config.OmdbApiKey) && config.ApiMode != ApiMode.MDBListOnly)
                {
                    var omdbData = await FetchFromOmdb(imdbId, config.OmdbApiKey, targetSource, criticSource, episodeInfo);
                    result.CommunityRating = omdbData.CommunityRating;
                    result.CriticRating = omdbData.CriticRating;
                    result.UsedOmdb = true;
                    if (!result.CommunityRating.HasValue)
                        result.OmdbHadNoEpisodeRating = true;
                }

                // Fallback: use api.imdbapi.dev (unofficial IMDb API, no key required) if enabled and no rating found.
                // This bypasses the AWS WAF that now blocks direct IMDb scraping.
                if (!result.CommunityRating.HasValue && canUseImdbScraping)
                {
                    float? apiRating = null;

                    if (!string.IsNullOrWhiteSpace(imdbId))
                    {
                        // Prefer querying the episode's own tt-id directly.
                        apiRating = await TryImdbScrapeRequest(() => FetchImdbApiDevRating(imdbId));
                    }

                    if (!apiRating.HasValue && !string.IsNullOrWhiteSpace(episodeInfo.SeriesImdbId))
                    {
                        // Fall back to the season list endpoint to find this episode.
                        apiRating = await TryImdbScrapeRequest(() => FetchImdbApiDevEpisodeRating(episodeInfo.SeriesImdbId, episodeInfo.SeasonNumber, episodeInfo.EpisodeNumber));
                    }

                    if (apiRating.HasValue)
                    {
                        result.CommunityRating = apiRating;
                        result.UsedScraping = true;
                    }
                    else
                    {
                        // Last resort: try direct HTML scraping (may be blocked by WAF).
                        float? scrapedRating = null;
                        if (!string.IsNullOrWhiteSpace(imdbId))
                        {
                            scrapedRating = await TryImdbScrapeRequest(() => ScrapeImdbRating(imdbId));
                        }
                        else
                        {
                            var episodeImdbId = await TryImdbScrapeRequest(
                                () => TryResolveEpisodeImdbIdFromSeriesEpisodesPage(
                                    episodeInfo.SeriesImdbId,
                                    episodeInfo.SeasonNumber,
                                    episodeInfo.EpisodeNumber),
                                null);

                            if (!string.IsNullOrWhiteSpace(episodeImdbId))
                                scrapedRating = await TryImdbScrapeRequest(() => ScrapeImdbRating(episodeImdbId));
                        }

                        if (scrapedRating.HasValue)
                        {
                            result.CommunityRating = scrapedRating;
                            result.UsedScraping = true;
                        }
                    }
                }
                return result;
            }

            switch (config.ApiMode)
            {
                case ApiMode.OMDbOnly:
                case ApiMode.OMDbWithMDBListFallback:
                    if (canUseOmdb && !string.IsNullOrEmpty(config.OmdbApiKey))
                    {
                        var omdbData = await FetchFromOmdb(imdbId, config.OmdbApiKey, targetSource, criticSource, episodeInfo);
                        result.CommunityRating = omdbData.CommunityRating;
                        result.CriticRating = omdbData.CriticRating;
                        result.UsedOmdb = true;
                        if (!result.CommunityRating.HasValue) result.OmdbHadNoCommunityRating = true;
                    }

                    var needsMdbFallback = !result.CommunityRating.HasValue ||
                                           (config.UpdateCriticRating && criticSource != CriticRatingSource.None && !result.CriticRating.HasValue);
                    if (config.ApiMode == ApiMode.OMDbWithMDBListFallback && needsMdbFallback && canUseMdbList && !string.IsNullOrEmpty(config.MdbListApiKey))
                    {
                        var mdbData = await FetchFromMdbList(imdbId, config.MdbListApiKey, targetSource, criticSource, episodeInfo, fallbackSource);
                        if (!result.CommunityRating.HasValue && mdbData.CommunityRating.HasValue)
                        {
                            result.CommunityRating = mdbData.CommunityRating;
                            result.ActualCommunitySource = mdbData.ActualCommunitySource;
                        }
                        if (!result.CriticRating.HasValue && mdbData.CriticRating.HasValue)
                            result.CriticRating = mdbData.CriticRating;
                        if (mdbData.MdbListRateLimited)
                        {
                            result.MdbListRateLimited = true;
                            result.MdbListRateLimitMessage = mdbData.MdbListRateLimitMessage;
                            result.MdbListRateLimitUntilUtc = mdbData.MdbListRateLimitUntilUtc;
                        }
                        // Only mark MDBList as used when an actual call was made (truthful source labels).
                        result.UsedMdbList |= mdbData.MdbListApiCallMade;
                        result.MdbListApiCallMade |= mdbData.MdbListApiCallMade;
                        if (!result.CommunityRating.HasValue) result.MdbListHadNoCommunityRating = true;
                    }
                    break;

                case ApiMode.MDBListOnly:
                case ApiMode.MDBListWithOMDbFallback:
                    if (canUseMdbList && !string.IsNullOrEmpty(config.MdbListApiKey))
                    {
                        var mdbData = await FetchFromMdbList(imdbId, config.MdbListApiKey, targetSource, criticSource, episodeInfo, fallbackSource);
                        result.CommunityRating = mdbData.CommunityRating;
                        result.ActualCommunitySource = mdbData.ActualCommunitySource;
                        result.CriticRating = mdbData.CriticRating;
                        if (mdbData.MdbListRateLimited)
                        {
                            result.MdbListRateLimited = true;
                            result.MdbListRateLimitMessage = mdbData.MdbListRateLimitMessage;
                            result.MdbListRateLimitUntilUtc = mdbData.MdbListRateLimitUntilUtc;
                        }
                        // Only mark MDBList as used when an actual call was made (truthful source labels).
                        result.UsedMdbList |= mdbData.MdbListApiCallMade;
                        result.MdbListApiCallMade |= mdbData.MdbListApiCallMade;
                        if (!result.CommunityRating.HasValue) result.MdbListHadNoCommunityRating = true;
                    }

                    var needsOmdbFallback = !result.CommunityRating.HasValue ||
                                            (config.UpdateCriticRating && criticSource != CriticRatingSource.None && !result.CriticRating.HasValue);
                    if (config.ApiMode == ApiMode.MDBListWithOMDbFallback && needsOmdbFallback && canUseOmdb && !string.IsNullOrEmpty(config.OmdbApiKey))
                    {
                        var omdbData = await FetchFromOmdb(imdbId, config.OmdbApiKey, targetSource, criticSource, episodeInfo);
                        if (!result.CommunityRating.HasValue && omdbData.CommunityRating.HasValue)
                            result.CommunityRating = omdbData.CommunityRating;
                        if (!result.CriticRating.HasValue && omdbData.CriticRating.HasValue)
                            result.CriticRating = omdbData.CriticRating;
                        result.UsedOmdb = true;
                        if (!result.CommunityRating.HasValue) result.OmdbHadNoCommunityRating = true;
                    }
                    break;

                case ApiMode.Both:
                    if (canUseOmdb && !string.IsNullOrEmpty(config.OmdbApiKey))
                    {
                        var omdbData = await FetchFromOmdb(imdbId, config.OmdbApiKey, targetSource, criticSource, episodeInfo);
                        result.CommunityRating = omdbData.CommunityRating;
                        result.CriticRating = omdbData.CriticRating;
                        result.UsedOmdb = true;
                        if (!result.CommunityRating.HasValue) result.OmdbHadNoCommunityRating = true;
                    }

                    if ((!result.CommunityRating.HasValue || !result.CriticRating.HasValue) && canUseMdbList && !string.IsNullOrEmpty(config.MdbListApiKey))
                    {
                        var mdbData = await FetchFromMdbList(imdbId, config.MdbListApiKey, targetSource, criticSource, episodeInfo, fallbackSource);
                        if (!result.CommunityRating.HasValue && mdbData.CommunityRating.HasValue)
                        {
                            result.CommunityRating = mdbData.CommunityRating;
                            result.ActualCommunitySource = mdbData.ActualCommunitySource;
                        }
                        if (!result.CriticRating.HasValue && mdbData.CriticRating.HasValue)
                            result.CriticRating = mdbData.CriticRating;
                        if (mdbData.MdbListRateLimited)
                        {
                            result.MdbListRateLimited = true;
                            result.MdbListRateLimitMessage = mdbData.MdbListRateLimitMessage;
                            result.MdbListRateLimitUntilUtc = mdbData.MdbListRateLimitUntilUtc;
                        }
                        // Only mark MDBList as used when an actual call was made (truthful source labels).
                        result.UsedMdbList |= mdbData.MdbListApiCallMade;
                        result.MdbListApiCallMade |= mdbData.MdbListApiCallMade;
                        if (!result.CommunityRating.HasValue) result.MdbListHadNoCommunityRating = true;
                    }
                    break;
            }

            // Last-resort: imdbapi.dev (free, no key) when all configured sources returned no community rating.
            if (!result.CommunityRating.HasValue && canUseImdbScraping && !string.IsNullOrWhiteSpace(imdbId))
            {
                var scraped = await TryImdbScrapeRequest(() => FetchImdbApiDevRating(imdbId));
                if (scraped.HasValue)
                {
                    result.CommunityRating = scraped;
                    result.ActualCommunitySource = CommunityRatingSource.IMDb;
                    result.UsedScraping = true;
                }
            }

            // Safety guard: all non-IMDb community ratings must come from MDBList only.
            var effectiveSource = result.ActualCommunitySource ?? targetSource;
            if (effectiveSource != CommunityRatingSource.IMDb && effectiveSource != CommunityRatingSource.None && !result.UsedMdbList)
            {
                result.CommunityRating = null;
                result.ActualCommunitySource = null;
            }

            return result;
        }

        private async Task<RatingData> FetchFromOmdb(string imdbId, string apiKey, CommunityRatingSource targetSource, CriticRatingSource criticSource, EpisodeInfo episodeInfo = null)
        {
            var result = new RatingData();
            
            try
            {
                {
                    string url;
                    if (episodeInfo != null)
                    {
                        // If Emby has the episode's own IMDb ID, query it directly — this gives
                        // OMDb the best chance of returning up-to-date data for recent episodes.
                        if (!string.IsNullOrWhiteSpace(imdbId))
                            url = $"http://www.omdbapi.com/?i={imdbId}&apikey={apiKey}";
                        else
                            url = $"http://www.omdbapi.com/?i={episodeInfo.SeriesImdbId}&Season={episodeInfo.SeasonNumber}&Episode={episodeInfo.EpisodeNumber}&apikey={apiKey}";
                    }
                    else
                    {
                        url = $"http://www.omdbapi.com/?i={imdbId}&apikey={apiKey}";
                    }

                    var response = await _httpClient.GetAsync(url);
                    var responseBody = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        return result;
                    }

                    var data = _jsonSerializer.DeserializeFromString<OmdbResponse>(responseBody);

                    if (data != null && data.Response == "True")
                    {
                        // Community Rating
                        if (targetSource == CommunityRatingSource.IMDb)
                        {
                            if (!string.IsNullOrEmpty(data.imdbRating) && data.imdbRating != "N/A")
                            {
                                if (float.TryParse(data.imdbRating, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var rating))
                                {
                                    result.CommunityRating = rating;
                                }
                            }
                        }

                        // Critic Rating
                        if (data.Ratings != null && criticSource != CriticRatingSource.None)
                        {
                            float? rtScore = null, metacriticScore = null;

                            var rtRating = data.Ratings.FirstOrDefault(r => r.Source == "Rotten Tomatoes");
                            if (rtRating != null && !string.IsNullOrEmpty(rtRating.Value))
                            {
                                var percentStr = rtRating.Value.Replace("%", "");
                                if (float.TryParse(percentStr, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                                    rtScore = v;
                            }

                            var mcRating = data.Ratings.FirstOrDefault(r => r.Source == "Metacritic");
                            if (mcRating != null && !string.IsNullOrEmpty(mcRating.Value))
                            {
                                var mcStr = mcRating.Value.Split('/')[0];
                                if (float.TryParse(mcStr, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                                    metacriticScore = v;
                            }

                            result.CriticRating = ResolveCriticRating(criticSource, rtScore, metacriticScore);
                        }
                    }

                }
            }
            catch (TaskCanceledException)
            {
                _logger.Debug("OMDb request timed out for {0}", imdbId ?? episodeInfo?.SeriesImdbId);
            }
            catch (Exception ex)
            {
                _logger.Debug("OMDb request failed for {0}: {1}", imdbId ?? episodeInfo?.SeriesImdbId, ex.Message);
            }

            return result;
        }

        private static float? ResolveCriticRating(CriticRatingSource source, float? rtScore, float? metacriticScore)
        {
            switch (source)
            {
                case CriticRatingSource.RottenTomatoes: return rtScore;
                case CriticRatingSource.Metacritic: return metacriticScore;
                case CriticRatingSource.RottenTomatoesWithMetacriticFallback: return rtScore ?? metacriticScore;
                case CriticRatingSource.MetacriticWithRottenTomatoesFallback: return metacriticScore ?? rtScore;
                default: return null;
            }
        }

        private static string GetCommunitySourceDisplayName(CommunityRatingSource source)
        {
            switch (source)
            {
                case CommunityRatingSource.Popcorn: return "RT Audience";
                case CommunityRatingSource.Metacritic: return "Metacritic";
                case CommunityRatingSource.MetacriticUser: return "Metacritic User";
                case CommunityRatingSource.MdbList: return "MDBList Score";
                case CommunityRatingSource.Trakt: return "Trakt";
                case CommunityRatingSource.Tmdb: return "TMDb";
                case CommunityRatingSource.Letterboxd: return "Letterboxd";
                case CommunityRatingSource.RogerEbert: return "Roger Ebert";
                default: return "IMDb";
            }
        }

        private float? ExtractMdbCommunityRating(MdbListResponse data, Func<string, MdbListRating> findRating, CommunityRatingSource source)
        {
            switch (source)
            {
                case CommunityRatingSource.Popcorn:
                    var popcorn = findRating("popcorn");
                    return popcorn != null && popcorn.value.HasValue && popcorn.value.Value > 0 ? popcorn.value.Value / 10f : (float?)null;
                case CommunityRatingSource.Metacritic:
                    var mc = findRating("metacritic");
                    return mc != null && mc.value.HasValue && mc.value.Value > 0 ? mc.value.Value / 10f : (float?)null;
                case CommunityRatingSource.MetacriticUser:
                    var mcu = findRating("metacriticuser");
                    return mcu != null && mcu.value.HasValue && mcu.value.Value > 0 ? (mcu.value.Value > 10f ? mcu.value.Value / 10f : mcu.value.Value) : (float?)null;
                case CommunityRatingSource.MdbList:
                    var mdb = findRating("mdblist");
                    if (mdb != null && mdb.value.HasValue && mdb.value.Value > 0) return mdb.value.Value / 10f;
                    return data.score.HasValue && data.score.Value > 0 ? data.score.Value / 10f : (float?)null;
                case CommunityRatingSource.Trakt:
                    var trakt = findRating("trakt");
                    return trakt != null && trakt.value.HasValue && trakt.value.Value > 0 ? trakt.value.Value / 10f : (float?)null;
                case CommunityRatingSource.Tmdb:
                    var tmdb = findRating("tmdb");
                    return tmdb != null && tmdb.value.HasValue && tmdb.value.Value > 0 ? tmdb.value.Value / 10f : (float?)null;
                case CommunityRatingSource.Letterboxd:
                    var lb = findRating("letterboxd");
                    return lb != null && lb.value.HasValue && lb.value.Value > 0 ? lb.value.Value / 10f : (float?)null;
                case CommunityRatingSource.RogerEbert:
                    var re = findRating("rogerebert");
                    return re != null && re.value.HasValue && re.value.Value > 0 ? re.value.Value * 2.5f : (float?)null;
                case CommunityRatingSource.None:
                    return null;
                default: // IMDb
                    var imdb = findRating("imdb");
                    if (imdb != null && imdb.value.HasValue && imdb.value.Value > 0) return imdb.value.Value;
                    return data.score.HasValue && data.score.Value > 0 ? data.score.Value / 10f : (float?)null;
            }
        }

        private async Task<RatingData> FetchFromMdbList(string imdbId, string apiKey, CommunityRatingSource targetSource, CriticRatingSource criticSource, EpisodeInfo episodeInfo = null, CommunityRatingSource fallbackSource = CommunityRatingSource.None)
        {
            var result = new RatingData();

            // Episode ratings are embedded in the show response under seasons[].episodes[]
            if (episodeInfo != null)
            {
                if (string.IsNullOrWhiteSpace(episodeInfo.SeriesImdbId) ||
                    episodeInfo.SeasonNumber <= 0 || episodeInfo.EpisodeNumber <= 0)
                    return result;

                try
                {
                    MdbListResponse showData;
                    if (_mdbShowCache != null && _mdbShowCache.ContainsKey(episodeInfo.SeriesImdbId))
                    {
                        showData = _mdbShowCache[episodeInfo.SeriesImdbId]; // null = previously tried, not found
                    }
                    else
                    {
                        {
                            var showUrl = $"https://api.mdblist.com/imdb/show/{episodeInfo.SeriesImdbId}?apikey={apiKey}";
                            HttpResponseMessage showResponse;
                            string showBody;
                            try
                            {
                                showResponse = await _httpClient.GetAsync(showUrl);
                                showBody = await showResponse.Content.ReadAsStringAsync();
                            }
                            catch (HttpRequestException ex) { _logger.Debug("MDBList show request failed for {0}: {1}", episodeInfo.SeriesImdbId, ex.Message); return result; }

                            result.MdbListApiCallMade = true;
                            if ((int)showResponse.StatusCode == 429)
                            {
                                result.MdbListRateLimited = true;
                                result.MdbListRateLimitUntilUtc = GetMdbListRetryAfterUtc(showResponse);
                                result.MdbListRateLimitMessage = BuildMdbListRateLimitMessage(showResponse);
                                return result;
                            }

                            showData = showResponse.IsSuccessStatusCode
                                ? _jsonSerializer.DeserializeFromString<MdbListResponse>(showBody)
                                : null;

                            if (_mdbShowCache != null)
                                _mdbShowCache[episodeInfo.SeriesImdbId] = showData; // cache even if null
                        }
                    }

                    var season = showData?.seasons?.FirstOrDefault(s => s.season_number == episodeInfo.SeasonNumber);
                    var episode = season?.episodes?.FirstOrDefault(e => e.episode_number == episodeInfo.EpisodeNumber);
                    if (episode?.rating != null && episode.rating.Value > 0)
                    {
                        result.CommunityRating = episode.rating.Value / 10f;
                        result.ActualCommunitySource = CommunityRatingSource.IMDb;
                        result.UsedMdbList = true;
                    }
                }
                catch (Exception ex) { _logger.Debug("MDBList episode lookup failed for {0}: {1}", episodeInfo.SeriesImdbId, ex.Message); }

                return result;
            }

            try
            {
                {
                    // MDBList API format: https://api.mdblist.com/{provider}/{type}/{id}?apikey={key}
                    // Try as show first for series, then movie
                    var url = $"https://api.mdblist.com/imdb/show/{imdbId}?apikey={apiKey}";

                    HttpResponseMessage response;
                    string responseBody;

                    try
                    {
                        response = await _httpClient.GetAsync(url);
                        responseBody = await response.Content.ReadAsStringAsync();
                        result.MdbListApiCallMade = true;

                        if ((int)response.StatusCode == 429)
                        {
                            result.MdbListRateLimited = true;
                            result.MdbListRateLimitUntilUtc = GetMdbListRetryAfterUtc(response);
                            result.MdbListRateLimitMessage = BuildMdbListRateLimitMessage(response);
                            return result;
                        }

                        // If show lookup fails or returns no rating data, try as movie
                        if (!response.IsSuccessStatusCode || !MdbListBodyHasRatings(responseBody))
                        {
                            url = $"https://api.mdblist.com/imdb/movie/{imdbId}?apikey={apiKey}";
                            response = await _httpClient.GetAsync(url);
                            responseBody = await response.Content.ReadAsStringAsync();

                            if ((int)response.StatusCode == 429)
                            {
                                result.MdbListRateLimited = true;
                                result.MdbListRateLimitUntilUtc = GetMdbListRetryAfterUtc(response);
                                result.MdbListRateLimitMessage = BuildMdbListRateLimitMessage(response);
                                return result;
                            }
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.Debug("MDBList request failed for {0}: {1}", imdbId, ex.Message);
                        return result;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        return result;
                    }

                    var data = _jsonSerializer.DeserializeFromString<MdbListResponse>(responseBody);

                    if (data != null && data.ratings != null && data.ratings.Count > 0)
                    {
                        Func<string, MdbListRating> findRating = sourceName =>
                            data.ratings.FirstOrDefault(r =>
                                !string.IsNullOrWhiteSpace(r.source) &&
                                string.Equals(r.source, sourceName, StringComparison.OrdinalIgnoreCase));

                        // Community Rating — try primary source, then fallback if configured
                        result.CommunityRating = ExtractMdbCommunityRating(data, findRating, targetSource);
                        result.ActualCommunitySource = targetSource;

                        if (!result.CommunityRating.HasValue && fallbackSource != CommunityRatingSource.None && fallbackSource != targetSource)
                        {
                            result.CommunityRating = ExtractMdbCommunityRating(data, findRating, fallbackSource);
                            if (result.CommunityRating.HasValue)
                                result.ActualCommunitySource = fallbackSource;
                        }

                        // Critic Rating
                        if (criticSource != CriticRatingSource.None)
                        {
                            float? rtScore = null, metacriticScore = null;

                            var rtRating = findRating("tomatoes");
                            if (rtRating != null && rtRating.value.HasValue && rtRating.value.Value > 0)
                                rtScore = rtRating.value.Value;

                            var mcRating = findRating("metacritic");
                            if (mcRating != null && mcRating.value.HasValue && mcRating.value.Value > 0)
                                metacriticScore = mcRating.value.Value;

                            result.CriticRating = ResolveCriticRating(criticSource, rtScore, metacriticScore);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("MDBList processing failed for {0}: {1}", imdbId, ex.Message);
            }

            return result;
        }

        // Robustly detect whether an MDBList JSON body actually contains rating data,
        // instead of sniffing for the literal "ratings":null substring.
        private bool MdbListBodyHasRatings(string body)
        {
            if (string.IsNullOrEmpty(body))
                return false;
            try
            {
                var probe = _jsonSerializer.DeserializeFromString<MdbListResponse>(body);
                return probe?.ratings != null && probe.ratings.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private string BuildMdbListRateLimitMessage(HttpResponseMessage response)
        {
            var retryUntilUtc = GetMdbListRetryAfterUtc(response);
            if (retryUntilUtc.HasValue)
            {
                return $"MDBList API limit reached (HTTP 429, blocked until {retryUntilUtc.Value:u})";
            }

            try
            {
                var retryAfter = response?.Headers?.RetryAfter;
                if (retryAfter != null)
                {
                    if (retryAfter.Delta.HasValue && retryAfter.Delta.Value.TotalSeconds > 0)
                    {
                        var totalSeconds = (int)Math.Ceiling(retryAfter.Delta.Value.TotalSeconds);
                        return $"MDBList API limit reached (HTTP 429, retry after {totalSeconds}s)";
                    }
                    if (retryAfter.Date.HasValue)
                    {
                        var utc = retryAfter.Date.Value.UtcDateTime.ToString("u");
                        return $"MDBList API limit reached (HTTP 429, retry at {utc})";
                    }
                }
            }
            catch
            {
                // Ignore header parsing issues.
            }

            return "MDBList API limit reached (HTTP 429)";
        }

        private DateTime? GetMdbListRetryAfterUtc(HttpResponseMessage response)
        {
            try
            {
                var retryAfter = response?.Headers?.RetryAfter;
                if (retryAfter == null)
                    return null;

                if (retryAfter.Delta.HasValue && retryAfter.Delta.Value.TotalSeconds > 0)
                    return DateTime.UtcNow.Add(retryAfter.Delta.Value);

                if (retryAfter.Date.HasValue)
                    return retryAfter.Date.Value.UtcDateTime;
            }
            catch
            {
                // Ignore header parsing issues.
            }

            return null;
        }

        // Fetch the IMDb rating for any title (episode, movie, series) via api.imdbapi.dev.
        // This unofficial API mirrors live IMDb data and is not subject to the AWS WAF
        // that now blocks direct imdb.com requests.
        private async Task<float?> FetchImdbApiDevRating(string imdbId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imdbId))
                    return null;

                {
                    var url = $"https://api.imdbapi.dev/titles/{imdbId}";
                    var response = await _httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                        return null;

                    var body = await response.Content.ReadAsStringAsync();
                    var data = _jsonSerializer.DeserializeFromString<ImdbApiDevTitleResponse>(body);
                    if (data?.rating?.aggregateRating.HasValue == true && data.rating.aggregateRating.Value >= 1)
                        return data.rating.aggregateRating.Value;
                }
            }
            catch (Exception ex) { _logger.Debug("imdbapi.dev title request failed for {0}: {1}", imdbId, ex.Message); }
            return null;
        }

        // Fetch an episode's IMDb rating via the season-episode list endpoint on api.imdbapi.dev.
        // Used when Emby doesn't supply an episode-level IMDb ID.
        private async Task<float?> FetchImdbApiDevEpisodeRating(string seriesImdbId, int seasonNumber, int episodeNumber)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(seriesImdbId) || seasonNumber <= 0 || episodeNumber <= 0)
                    return null;

                {
                    var url = $"https://api.imdbapi.dev/titles/{seriesImdbId}/episodes?season={seasonNumber}";
                    var response = await _httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                        return null;

                    var body = await response.Content.ReadAsStringAsync();
                    var data = _jsonSerializer.DeserializeFromString<ImdbApiDevEpisodesResponse>(body);
                    var ep = data?.episodes?.FirstOrDefault(e => e.episodeNumber == episodeNumber);
                    if (ep?.rating?.aggregateRating.HasValue == true && ep.rating.aggregateRating.Value >= 1)
                        return ep.rating.aggregateRating.Value;
                }
            }
            catch (Exception ex) { _logger.Debug("imdbapi.dev episodes request failed for {0} S{1}: {2}", seriesImdbId, seasonNumber, ex.Message); }
            return null;
        }

        private async Task<float?> ScrapeImdbRating(string imdbId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imdbId))
                    return null;

                {
                    var url = $"https://www.imdb.com/title/{imdbId}/";
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        AddScrapeHeaders(request);
                        var response = await _httpClient.SendAsync(request);

                        if (!response.IsSuccessStatusCode)
                            return null;

                        var html = await response.Content.ReadAsStringAsync();

                    // Prefer parsing near the requested tconst to avoid accidentally capturing a parent series rating
                    // that may also be embedded on an episode page.
                    string scopedHtml = html;
                    try
                    {
                        var tconstNeedle = $"\"tconst\":\"{imdbId}\"";
                        var idx = html.IndexOf(tconstNeedle, StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            var start = Math.Max(0, idx - 2000);
                            var length = Math.Min(html.Length - start, 25000);
                            scopedHtml = html.Substring(start, length);
                        }
                    }
                    catch
                    {
                        scopedHtml = html;
                    }
                    
                    // Pattern 1 (BEST): Look for ratingsSummary with aggregateRating - most reliable for episodes
                    // Format: "ratingsSummary":{"aggregateRating":6.7 or "ratingsSummary":{"topRanking":null,...,"aggregateRating":6.7
                    var match = System.Text.RegularExpressions.Regex.Match(scopedHtml, @"""ratingsSummary""[^}]*""aggregateRating""\s*:\s*([\d.]+)");
                    
                    // Pattern 2: JSON-LD AggregateRating object - "AggregateRating"..."ratingValue":8.5
                    // This is used for movies/shows with full JSON-LD structured data
                    if (!match.Success)
                    {
                        match = System.Text.RegularExpressions.Regex.Match(scopedHtml, @"""AggregateRating""[^}]*""ratingValue""\s*:\s*([\d.]+)");
                    }
                    
                    // Pattern 3: Fallback - aggregateRating as standalone object (not in ratingsSummary)
                    if (!match.Success)
                    {
                        match = System.Text.RegularExpressions.Regex.Match(scopedHtml, @"""aggregateRating""\s*:\s*\{[^}]*""ratingValue""\s*:\s*([\d.]+)");
                    }
                    
                    if (match.Success && float.TryParse(match.Groups[1].Value, 
                        System.Globalization.NumberStyles.Float, 
                        System.Globalization.CultureInfo.InvariantCulture, 
                        out var rating))
                    {
                        // Validate rating is in expected range
                        if (rating >= 1 && rating <= 10)
                        {
                            return rating;
                        }
                    }
                    }
                }
            }
            catch (TaskCanceledException)
            {
                _logger.Debug("IMDb scrape timed out for {0}", imdbId);
            }
            catch (Exception ex)
            {
                _logger.Debug("IMDb scrape failed for {0}: {1}", imdbId, ex.Message);
            }

            return null;
        }

        private async Task<float?> ScrapeImdbEpisodeRating(EpisodeInfo episodeInfo)
        {
            try
            {
                if (episodeInfo == null)
                    return null;
                if (string.IsNullOrWhiteSpace(episodeInfo.SeriesImdbId))
                    return null;
                if (episodeInfo.SeasonNumber <= 0 || episodeInfo.EpisodeNumber <= 0)
                    return null;

                var episodeImdbId = await TryResolveEpisodeImdbIdFromSeriesEpisodesPage(
                    episodeInfo.SeriesImdbId,
                    episodeInfo.SeasonNumber,
                    episodeInfo.EpisodeNumber);

                if (string.IsNullOrWhiteSpace(episodeImdbId))
                    return null;

                // Now scrape the episode title page.
                return await ScrapeImdbRating(episodeImdbId);
            }
            catch
            {
                return null;
            }
        }

        private async Task<string> TryResolveEpisodeImdbIdFromSeriesEpisodesPage(string seriesImdbId, int seasonNumber, int episodeNumber)
        {
            try
            {
                var cacheKey = seriesImdbId + "|S" + seasonNumber;
                lock (_imdbEpisodeCacheLock)
                {
                    if (_imdbEpisodeIdCache.TryGetValue(cacheKey, out var cachedMap)
                        && cachedMap != null
                        && cachedMap.TryGetValue(episodeNumber, out var cachedId)
                        && !string.IsNullOrWhiteSpace(cachedId))
                    {
                        return cachedId;
                    }
                }

                {
                    var url = $"https://www.imdb.com/title/{seriesImdbId}/episodes?season={seasonNumber}";
                    string html;
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        AddScrapeHeaders(request);
                        var response = await _httpClient.SendAsync(request);
                        if (!response.IsSuccessStatusCode)
                            return null;
                        html = await response.Content.ReadAsStringAsync();
                    }

                    if (string.IsNullOrWhiteSpace(html))
                        return null;

                    // Parse and cache all episode ids for this season in one pass.
                    // Example link: /title/tt39306204/?ref_=ttep_ep_1
                    var map = new Dictionary<int, string>();
                    var all = System.Text.RegularExpressions.Regex.Matches(
                        html,
                        "href\\s*=\\s*\\\"/title/(?<id>tt\\d{7,8})/\\?ref_=ttep_ep_(?<ep>\\d+)\\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    foreach (System.Text.RegularExpressions.Match m in all)
                    {
                        if (!m.Success) continue;
                        if (!int.TryParse(m.Groups["ep"].Value, out var epNum)) continue;
                        var id = m.Groups["id"].Value;
                        if (epNum > 0 && !string.IsNullOrWhiteSpace(id))
                            map[epNum] = id;
                    }

                    if (map.Count > 0)
                    {
                        lock (_imdbEpisodeCacheLock)
                        {
                            if (_imdbEpisodeIdCache.Count > 200)
                                _imdbEpisodeIdCache.Clear();
                            _imdbEpisodeIdCache[cacheKey] = map;
                        }

                        if (map.TryGetValue(episodeNumber, out var found) && !string.IsNullOrWhiteSpace(found))
                            return found;
                    }

                    // Preferred: links contain ref_=ttep_ep_{N} for the episode card.
                    var epRefNeedles = new[] { $"ttep_ep_{episodeNumber}", $"ttep_ep{episodeNumber}" };
                    foreach (var epRefNeedle in epRefNeedles)
                    {
                        var pattern = "href\\s*=\\s*\\\"/title/(?<id>tt\\d{7,8})/\\?ref_="
                            + System.Text.RegularExpressions.Regex.Escape(epRefNeedle)
                            + "[^\\\"]*\\\"";
                        var match = System.Text.RegularExpressions.Regex.Match(
                            html,
                            pattern,
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        if (match.Success)
                            return match.Groups["id"].Value;
                    }

                    // Fallback: sometimes the ref format changes; try finding a title id on the same line/near the episode ref.
                    foreach (var epRefNeedle in epRefNeedles)
                    {
                        var idx = html.IndexOf(epRefNeedle, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            var start = Math.Max(0, idx - 1000);
                            var length = Math.Min(html.Length - start, 3000);
                            var window = html.Substring(start, length);

                            var match = System.Text.RegularExpressions.Regex.Match(
                                window,
                                @"/title/(?<id>tt\d{7,8})/",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                            if (match.Success)
                                return match.Groups["id"].Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("IMDb episode-id resolve failed for {0} S{1}: {2}", seriesImdbId, seasonNumber, ex.Message);
            }

            return null;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
                }
            };
        }

        private class EpisodeInfo
        {
            public string SeriesImdbId { get; set; }
            public string SeriesName { get; set; }
            public int SeasonNumber { get; set; }
            public int EpisodeNumber { get; set; }
        }

        private class RatingData
        {
            public float? CommunityRating { get; set; }
            public float? CriticRating { get; set; }
            public bool UsedOmdb { get; set; }
            public bool UsedMdbList { get; set; }
            public bool MdbListApiCallMade { get; set; }
            public bool MdbListHadNoEpisodeRating { get; set; }
            public bool OmdbHadNoCommunityRating { get; set; }
            public bool MdbListHadNoCommunityRating { get; set; }
            public bool UsedScraping { get; set; }
            public bool OmdbHadNoEpisodeRating { get; set; }
            public bool ImdbScrapeAttempted { get; set; }
            public int ImdbScrapeRequests { get; set; }
            public bool MdbListRateLimited { get; set; }
            public string MdbListRateLimitMessage { get; set; }
            public DateTime? MdbListRateLimitUntilUtc { get; set; }
            public CommunityRatingSource? ActualCommunitySource { get; set; }
        }

        private class OmdbResponse
        {
            public string imdbRating { get; set; }
            public string Response { get; set; }
            public string Error { get; set; }
            public List<OmdbRating> Ratings { get; set; }
        }

        private class OmdbRating
        {
            public string Source { get; set; }
            public string Value { get; set; }
        }

        private class ImdbApiDevRating
        {
            public float? aggregateRating { get; set; }
            public int? voteCount { get; set; }
        }

        private class ImdbApiDevTitleResponse
        {
            public string id { get; set; }
            public ImdbApiDevRating rating { get; set; }
        }

        private class ImdbApiDevEpisodesResponse
        {
            public List<ImdbApiDevEpisodeEntry> episodes { get; set; }
        }

        private class ImdbApiDevEpisodeEntry
        {
            public string id { get; set; }
            public int? episodeNumber { get; set; }
            public ImdbApiDevRating rating { get; set; }
        }

        private class MdbListResponse
        {
            public string title { get; set; }
            public int? year { get; set; }
            public int? score { get; set; }
            public int? score_average { get; set; }
            public string type { get; set; }
            public List<MdbListRating> ratings { get; set; }
            public List<MdbListSeason> seasons { get; set; }
        }

        private class MdbListRating
        {
            public string source { get; set; }
            public float? value { get; set; }
            public int? score { get; set; }
            public int? votes { get; set; }
        }

        private class MdbListSeason
        {
            public int? season_number { get; set; }
            public List<MdbListEpisode> episodes { get; set; }
        }

        private class MdbListEpisode
        {
            public int? episode_number { get; set; }
            public int? rating { get; set; }
            public int? votes { get; set; }
        }
    }

    #endregion
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ProjectAmityServer
{
    public class DirectoryScannerService : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DirectoryScannerService> _logger;
        private readonly MetadataScraperService _scraperService;
        private readonly GlacierDbService _db;
        private bool _isScanning = false;
        
        // Cache episodes list in memory during scanning run
        private readonly Dictionary<int, List<TvmazeEpisode>> _showEpisodesCache = new();
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public DirectoryScannerService(
            IConfiguration configuration, 
            ILogger<DirectoryScannerService> logger,
            MetadataScraperService scraperService,
            GlacierDbService db)
        {
            _configuration = configuration;
            _logger = logger;
            _scraperService = scraperService;
            _db = db;
        }

        public bool IsScanning => _isScanning;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Directory Scanner Service starting...");

            // Check AutoScanOnStartup setting
            bool autoScan = true;
            try
            {
                var rows = await _db.ExecuteQueryAsync("SELECT SettingValue FROM SystemSettings WHERE SettingKey = 'AutoScanOnStartup'");
                if (rows.Count > 0 && rows[0]["SettingValue"]?.ToString() == "false")
                {
                    autoScan = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading AutoScanOnStartup setting. Defaulting to true.");
            }

            if (autoScan)
            {
                _logger.LogInformation("Auto-scan on startup is enabled. Triggering initial library scan...");
                _ = Task.Run(() => ScanNowAsync(), stoppingToken);
            }
            else
            {
                _logger.LogInformation("Auto-scan on startup is disabled. Skipping initial library scan.");
            }

            // Periodically scan every 5 minutes
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                await ScanNowAsync();
            }
        }

        public async Task ScanNowAsync()
        {
            if (_isScanning)
            {
                _logger.LogInformation("Scan already in progress. Skipping.");
                return;
            }

            _isScanning = true;
            _logger.LogInformation("Starting media directory scan based on database settings...");

            try
            {
                var scanFolders = await LoadScanFoldersFromDbAsync();
                if (scanFolders.Count == 0)
                {
                    _logger.LogWarning("No directories configured in database for media scanning.");
                    return;
                }

                var filesOnDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var folder in scanFolders)
                {
                    if (!Directory.Exists(folder.FolderPath))
                    {
                        _logger.LogWarning($"Scan directory does not exist: {folder.FolderPath}");
                        continue;
                    }

                    _logger.LogInformation($"Scanning directory: {folder.FolderPath} for {folder.MediaType}s");
                    await ScanDirectoryRecursiveAsync(folder.FolderPath, folder.MediaType, filesOnDisk);
                }

                // Run library cleanup for missing files
                await CleanMissingMediaItemsAsync(filesOnDisk, scanFolders);

                _logger.LogInformation("Media directory scan completed successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during media directory scan.");
            }
            finally
            {
                _isScanning = false;
            }
        }

        private async Task<List<ScanFolder>> LoadScanFoldersFromDbAsync()
        {
            var list = new List<ScanFolder>();
            var rows = await _db.ExecuteQueryAsync("SELECT Id, FolderPath, MediaType FROM ScanFolders");
            foreach (var row in rows)
            {
                list.Add(new ScanFolder(
                    Convert.ToInt32(row["Id"]),
                    row["FolderPath"]?.ToString() ?? "",
                    row["MediaType"]?.ToString() ?? ""
                ));
            }
            return list;
        }

        private async Task ScanDirectoryRecursiveAsync(string directoryPath, string mediaType, HashSet<string> filesOnDisk)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Could not read files from {directoryPath}: {ex.Message}");
                return;
            }

            foreach (var filePath in files)
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".mp4" || ext == ".mkv" || ext == ".m4v" || ext == ".webm" || ext == ".mov")
                {
                    filesOnDisk.Add(filePath);
                    await ProcessMediaFileAsync(filePath, mediaType);
                }
            }
        }

        private async Task CleanMissingMediaItemsAsync(HashSet<string> filesOnDisk, List<ScanFolder> scanFolders)
        {
            _logger.LogInformation("Checking for missing media files to remove from library...");
            try
            {
                var dbItems = new List<(int Id, string FilePath, string MediaType, int? TvShowId)>();
                var rows = await _db.ExecuteQueryAsync("SELECT Id, FilePath, MediaType, TvShowId FROM MediaItems");
                foreach (var row in rows)
                {
                    dbItems.Add((
                        Convert.ToInt32(row["Id"]),
                        row["FilePath"]?.ToString() ?? "",
                        row["MediaType"]?.ToString() ?? "",
                        row["TvShowId"] == null || row["TvShowId"] is DBNull ? null : Convert.ToInt32(row["TvShowId"])
                    ));
                }

                var tvShowsToCheck = new HashSet<int>();

                foreach (var item in dbItems)
                {
                    bool belongsToScanFolder = false;
                    foreach (var folder in scanFolders)
                    {
                        if (item.FilePath.StartsWith(folder.FolderPath, StringComparison.OrdinalIgnoreCase))
                        {
                            belongsToScanFolder = true;
                            break;
                        }
                    }

                    if (!belongsToScanFolder || !filesOnDisk.Contains(item.FilePath))
                    {
                        _logger.LogInformation($"File no longer exists or belongs to removed scan directory. Removing from database: {item.FilePath}");
                        
                        await _db.ExecuteNonQueryAsync("DELETE FROM MediaItems WHERE Id = @Id", new Dictionary<string, object?> { { "@Id", item.Id } });

                        if (item.MediaType == "Episode" && item.TvShowId.HasValue)
                        {
                            tvShowsToCheck.Add(item.TvShowId.Value);
                        }
                    }
                }

                // Clean empty TV Shows (all TV Shows in DB with 0 episodes)
                var allShows = await _db.ExecuteQueryAsync("SELECT Id, Title FROM TvShows");
                foreach (var showRow in allShows)
                {
                    int showId = Convert.ToInt32(showRow["Id"]);
                    string showTitle = showRow["Title"]?.ToString() ?? "";
                    int count = Convert.ToInt32(await _db.ExecuteScalarAsync(
                        "SELECT COUNT(*) FROM MediaItems WHERE TvShowId = @TvShowId",
                        new Dictionary<string, object?> { { "@TvShowId", showId } }
                    ) ?? 0);

                    if (count == 0)
                    {
                        _logger.LogInformation($"Show '{showTitle}' has no remaining episodes. Removing empty Show ID: {showId}");
                        await _db.ExecuteNonQueryAsync("DELETE FROM TvShows WHERE Id = @Id", new Dictionary<string, object?> { { "@Id", showId } });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during media cleanup scan.");
            }
        }

        private async Task ProcessMediaFileAsync(string filePath, string targetMediaType)
        {
            try
            {
                // Check if file is already in database
                int? existingId = null;
                string? existingMediaType = null;
                string? existingPosterPath = null;
                int? existingTvShowId = null;

                var rows = await _db.ExecuteQueryAsync(
                    "SELECT Id, MediaType, PosterPath, TvShowId FROM MediaItems WHERE FilePath = @FilePath",
                    new Dictionary<string, object?> { { "@FilePath", filePath } }
                );

                if (rows.Count > 0)
                {
                    var row = rows[0];
                    existingId = Convert.ToInt32(row["Id"]);
                    existingMediaType = row["MediaType"]?.ToString();
                    existingPosterPath = row["PosterPath"] == null || row["PosterPath"] is DBNull ? null : row["PosterPath"]?.ToString();
                    existingTvShowId = row["TvShowId"] == null || row["TvShowId"] is DBNull ? null : Convert.ToInt32(row["TvShowId"]);
                }

                if (existingId.HasValue)
                {
                    // Check if we need to repair/migrate this existing item
                    if (existingMediaType == "Movie" && string.IsNullOrWhiteSpace(existingPosterPath))
                    {
                        // Need to fetch metadata for existing movie!
                        var (title, year) = ParseMetadataForType(filePath, "Movie");
                        var meta = await _scraperService.ScrapeMovieMetadataAsync(title, year);
                        if (meta != null)
                        {
                            string castJson = JsonSerializer.Serialize(meta.Cast, _jsonOptions);
                            await _db.UpdateRowAsync("MediaItems", existingId.Value, new Dictionary<string, object?>
                            {
                                { "Title", meta.Title },
                                { "ReleaseYear", meta.ReleaseYear ?? (year ?? (object)DBNull.Value) },
                                { "PosterPath", string.IsNullOrWhiteSpace(meta.PosterPath) ? DBNull.Value : meta.PosterPath },
                                { "Overview", string.IsNullOrWhiteSpace(meta.Overview) ? DBNull.Value : meta.Overview },
                                { "Genres", string.IsNullOrWhiteSpace(meta.Genres) ? DBNull.Value : meta.Genres },
                                { "Director", string.IsNullOrWhiteSpace(meta.Director) ? DBNull.Value : meta.Director },
                                { "CastJson", castJson }
                            });

                             _logger.LogInformation($"Updated missing metadata for existing Movie: {meta.Title}");

                             if (!string.IsNullOrWhiteSpace(meta.CollectionName) && await ShouldAutoCreateCollectionsAsync())
                             {
                                 await EnsureCollectionAndLinkItemAsync(existingId.Value, meta.CollectionName, meta.PosterPath);
                             }
                         }
                     }
                    else if (existingMediaType == "Episode" && (!existingTvShowId.HasValue || string.IsNullOrWhiteSpace(existingPosterPath)))
                    {
                        // Need to link and update existing episode!
                        string filename = Path.GetFileName(filePath);
                        string nameWithoutExt = Path.GetFileNameWithoutExtension(filename);
                        var epRegex = new Regex(@"[sS](?<season>\d+)[eE](?<episode>\d+)", RegexOptions.Compiled);
                        var epMatch = epRegex.Match(nameWithoutExt);
                        int seasonNum = 1;
                        int episodeNum = 1;
                        string showName = "Unknown Show";

                        if (epMatch.Success)
                        {
                            int.TryParse(epMatch.Groups["season"].Value, out seasonNum);
                            int.TryParse(epMatch.Groups["episode"].Value, out episodeNum);
                            showName = nameWithoutExt.Substring(0, epMatch.Index).Replace(".", " ").Replace("_", " ").Trim();
                            showName = Regex.Replace(showName, @"\s*-\s*$", "").Trim();
                        }
                        
                        if (string.IsNullOrWhiteSpace(showName) || showName.Length <= 2 || !epMatch.Success)
                        {
                            try
                            {
                                var fileInfo = new FileInfo(filePath);
                                var parentDir = fileInfo.Directory;
                                if (parentDir != null)
                                {
                                    string parentName = parentDir.Name;
                                    if (Regex.IsMatch(parentName, @"^(?:[sS]eason|[sS])\s*\d+$", RegexOptions.IgnoreCase))
                                    {
                                        var showDir = parentDir.Parent;
                                        if (showDir != null) showName = showDir.Name;
                                    }
                                    else
                                    {
                                        showName = parentName;
                                    }
                                }
                            }
                            catch {}
                        }

                        if (string.IsNullOrWhiteSpace(showName)) showName = "Unknown Show";

                        int tvShowId = await GetOrCreateTvShowIdAsync(showName, filePath);
                        string episodeTitle = $"{showName} - S{seasonNum:D2}E{episodeNum:D2}";
                        string episodeOverview = "";
                        string? episodeImage = null;

                        if (_showEpisodesCache.TryGetValue(tvShowId, out var cachedEps))
                        {
                            var tvmazeEp = cachedEps.Find(e => e.Season == seasonNum && e.Number == episodeNum);
                            if (tvmazeEp != null)
                            {
                                episodeTitle = $"{showName} - S{seasonNum:D2}E{episodeNum:D2} - {tvmazeEp.Name}";
                                episodeOverview = tvmazeEp.Summary ?? "";
                                episodeImage = tvmazeEp.Image?.Original ?? tvmazeEp.Image?.Medium;
                            }
                        }

                        await _db.UpdateRowAsync("MediaItems", existingId.Value, new Dictionary<string, object?>
                        {
                            { "Title", episodeTitle },
                            { "TvShowId", tvShowId },
                            { "SeasonNumber", seasonNum },
                            { "EpisodeNumber", episodeNum },
                            { "Overview", string.IsNullOrWhiteSpace(episodeOverview) ? DBNull.Value : episodeOverview },
                            { "PosterPath", string.IsNullOrWhiteSpace(episodeImage) ? DBNull.Value : episodeImage }
                        });

                        _logger.LogInformation($"Linked existing Episode to Show: {episodeTitle}");
                    }

                    await EnsureSpecsAndSubtitlesAsync(existingId.Value, filePath);
                    return; // Already indexed
                }

                if (targetMediaType == "Movie")
                {
                    var (title, year) = ParseMetadataForType(filePath, "Movie");
                    var meta = await _scraperService.ScrapeMovieMetadataAsync(title, year);
                    
                    int newMediaId = await _db.GetNextIdAsync("MediaItems");

                    string insertQuery = @"
                        INSERT INTO MediaItems (Id, Title, FilePath, MediaType, ReleaseYear, DurationInSeconds, ResumePositionInSeconds, DateAdded, PosterPath, Overview, Genres, Director, CastJson)
                        VALUES (@Id, @Title, @FilePath, @MediaType, @ReleaseYear, 0, 0, @DateAdded, @PosterPath, @Overview, @Genres, @Director, @CastJson)";

                    var parameters = new Dictionary<string, object?>
                    {
                        { "@Id", newMediaId },
                        { "@FilePath", filePath },
                        { "@MediaType", "Movie" },
                        { "@DateAdded", DateTime.Now }
                    };

                    if (meta != null)
                    {
                        parameters.Add("@Title", meta.Title);
                        parameters.Add("@ReleaseYear", meta.ReleaseYear ?? (year ?? (object)DBNull.Value));
                        parameters.Add("@PosterPath", string.IsNullOrWhiteSpace(meta.PosterPath) ? DBNull.Value : meta.PosterPath);
                        parameters.Add("@Overview", string.IsNullOrWhiteSpace(meta.Overview) ? DBNull.Value : meta.Overview);
                        parameters.Add("@Genres", string.IsNullOrWhiteSpace(meta.Genres) ? DBNull.Value : meta.Genres);
                        parameters.Add("@Director", string.IsNullOrWhiteSpace(meta.Director) ? DBNull.Value : meta.Director);
                        string castJson = JsonSerializer.Serialize(meta.Cast, _jsonOptions);
                        parameters.Add("@CastJson", castJson);
                    }
                    else
                    {
                        parameters.Add("@Title", title);
                        parameters.Add("@ReleaseYear", year ?? (object)DBNull.Value);
                        parameters.Add("@PosterPath", DBNull.Value);
                        parameters.Add("@Overview", DBNull.Value);
                        parameters.Add("@Genres", DBNull.Value);
                        parameters.Add("@Director", DBNull.Value);
                        parameters.Add("@CastJson", "[]");
                    }

                    await _db.ExecuteNonQueryAsync(insertQuery, parameters);
                    _logger.LogInformation($"Indexed Movie: {title}");

                    // Scan for chapters (Skip Intro Feature)
                    await ScanAndSaveChaptersAsync(newMediaId, filePath);
                    await ScanAndSaveSpecsAsync(newMediaId, filePath);
                    await DiscoverAndSaveSubtitlesAsync(newMediaId, filePath);

                     if (meta != null && !string.IsNullOrWhiteSpace(meta.CollectionName) && await ShouldAutoCreateCollectionsAsync())
                     {
                         await EnsureCollectionAndLinkItemAsync(newMediaId, meta.CollectionName, meta.PosterPath);
                     }
                 }
                else if (targetMediaType == "Episode")
                {
                    string filename = Path.GetFileName(filePath);
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(filename);
                    var epRegex = new Regex(@"[sS](?<season>\d+)[eE](?<episode>\d+)", RegexOptions.Compiled);
                    var epMatch = epRegex.Match(nameWithoutExt);
                    int seasonNum = 1;
                    int episodeNum = 1;
                    string showName = "Unknown Show";

                    if (epMatch.Success)
                    {
                        int.TryParse(epMatch.Groups["season"].Value, out seasonNum);
                        int.TryParse(epMatch.Groups["episode"].Value, out episodeNum);
                        
                        showName = nameWithoutExt.Substring(0, epMatch.Index).Replace(".", " ").Replace("_", " ").Trim();
                        showName = Regex.Replace(showName, @"\s*-\s*$", "").Trim();
                        
                        if (string.IsNullOrWhiteSpace(showName) || showName.Length <= 2)
                        {
                            try
                            {
                                var fileInfo = new FileInfo(filePath);
                                var parentDir = fileInfo.Directory;
                                if (parentDir != null)
                                {
                                    string parentName = parentDir.Name;
                                    if (Regex.IsMatch(parentName, @"^(?:[sS]eason|[sS])\s*\d+$", RegexOptions.IgnoreCase))
                                    {
                                        var showDir = parentDir.Parent;
                                        if (showDir != null) showName = showDir.Name;
                                    }
                                    else
                                    {
                                        showName = parentName;
                                    }
                                }
                            }
                            catch {}
                        }
                    }

                    if (string.IsNullOrWhiteSpace(showName)) showName = "Unknown Show";

                    int tvShowId = await GetOrCreateTvShowIdAsync(showName, filePath);
                    
                    string episodeTitle = $"{showName} - S{seasonNum:D2}E{episodeNum:D2}";
                    string episodeOverview = "";
                    string? episodeImage = null;

                    if (_showEpisodesCache.TryGetValue(tvShowId, out var cachedEps))
                    {
                        var tvmazeEp = cachedEps.Find(e => e.Season == seasonNum && e.Number == episodeNum);
                        if (tvmazeEp != null)
                        {
                            episodeTitle = $"{showName} - S{seasonNum:D2}E{episodeNum:D2} - {tvmazeEp.Name}";
                            episodeOverview = tvmazeEp.Summary;
                            episodeImage = tvmazeEp.Image?.Original ?? tvmazeEp.Image?.Medium;
                        }
                    }

                    int newMediaId = await _db.GetNextIdAsync("MediaItems");

                    string insertQuery = @"
                        INSERT INTO MediaItems (Id, Title, FilePath, MediaType, ReleaseYear, DurationInSeconds, ResumePositionInSeconds, DateAdded, TvShowId, SeasonNumber, EpisodeNumber, Overview, PosterPath)
                        VALUES (@Id, @Title, @FilePath, @MediaType, @ReleaseYear, 0, 0, @DateAdded, @TvShowId, @SeasonNumber, @EpisodeNumber, @Overview, @PosterPath)";

                    await _db.ExecuteNonQueryAsync(insertQuery, new Dictionary<string, object?>
                    {
                        { "@Id", newMediaId },
                        { "@Title", episodeTitle },
                        { "@FilePath", filePath },
                        { "@MediaType", "Episode" },
                        { "@ReleaseYear", DBNull.Value },
                        { "@TvShowId", tvShowId },
                        { "@SeasonNumber", seasonNum },
                        { "@EpisodeNumber", episodeNum },
                        { "@Overview", string.IsNullOrWhiteSpace(episodeOverview) ? DBNull.Value : episodeOverview },
                        { "@PosterPath", string.IsNullOrWhiteSpace(episodeImage) ? DBNull.Value : episodeImage },
                        { "@DateAdded", DateTime.Now }
                    });

                    _logger.LogInformation($"Indexed Episode: {episodeTitle}");

                    // Scan for chapters (Skip Intro Feature)
                    await ScanAndSaveChaptersAsync(newMediaId, filePath);
                    await ScanAndSaveSpecsAsync(newMediaId, filePath);
                    await DiscoverAndSaveSubtitlesAsync(newMediaId, filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error processing file {filePath}: {ex.Message}");
            }
        }

        private string GetShowDirectoryPath(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var parentDir = fileInfo.Directory;
                if (parentDir != null)
                {
                    if (Regex.IsMatch(parentDir.Name, @"^(?:[sS]eason|[sS])\s*\d+$", RegexOptions.IgnoreCase))
                    {
                        var showDir = parentDir.Parent;
                        if (showDir != null) return showDir.FullName;
                    }
                    return parentDir.FullName;
                }
            }
            catch {}
            return "";
        }

        private async Task<int> GetOrCreateTvShowIdAsync(string showName, string filePath)
        {
            // 0. Check if this episode is in a folder where other episodes already mapped to a TV Show ID
            string showDirPath = GetShowDirectoryPath(filePath);
            if (!string.IsNullOrEmpty(showDirPath))
            {
                string searchPattern = showDirPath;
                if (!searchPattern.EndsWith(Path.DirectorySeparatorChar.ToString()))
                {
                    searchPattern += Path.DirectorySeparatorChar;
                }

                var existingItems = await _db.ExecuteQueryAsync(
                    "SELECT FilePath, TvShowId FROM MediaItems WHERE TvShowId IS NOT NULL"
                );

                var folderMatch = existingItems.Find(item =>
                {
                    string path = item["FilePath"]?.ToString() ?? "";
                    return path.StartsWith(searchPattern, StringComparison.OrdinalIgnoreCase);
                });

                if (folderMatch != null)
                {
                    if (folderMatch["TvShowId"] != null && folderMatch["TvShowId"] is not DBNull)
                    {
                        int existingShowId = Convert.ToInt32(folderMatch["TvShowId"]);
                        var verifyShow = await _db.ExecuteQueryAsync(
                            "SELECT Id, TvmazeId FROM TvShows WHERE Id = @Id",
                            new Dictionary<string, object?> { { "@Id", existingShowId } }
                        );
                        if (verifyShow.Count > 0)
                        {
                            _logger.LogInformation($"Found existing TV Show ID {existingShowId} for folder: '{showDirPath}' based on database file mappings.");
                            int? tvmazeId = verifyShow[0]["TvmazeId"] == null || verifyShow[0]["TvmazeId"] is DBNull ? null : Convert.ToInt32(verifyShow[0]["TvmazeId"]);
                            if (tvmazeId.HasValue && !_showEpisodesCache.ContainsKey(existingShowId))
                            {
                                var eps = await _scraperService.FetchEpisodesListAsync(tvmazeId.Value);
                                if (eps != null)
                                {
                                    _showEpisodesCache[existingShowId] = eps;
                                }
                            }
                            return existingShowId;
                        }
                    }
                }
            }

            // 1. Look up existing show case-insensitively in C# to prevent duplicates
            var rows = await _db.ExecuteQueryAsync("SELECT Id, Title, TvmazeId FROM TvShows");
            var match = rows.Find(r => r["Title"]?.ToString().Equals(showName, StringComparison.OrdinalIgnoreCase) == true);

            if (match != null)
            {
                int id = Convert.ToInt32(match["Id"]);
                int? tvmazeId = match["TvmazeId"] == null || match["TvmazeId"] is DBNull ? null : Convert.ToInt32(match["TvmazeId"]);

                // Populate episodes cache if missing
                if (tvmazeId.HasValue && !_showEpisodesCache.ContainsKey(id))
                {
                    var eps = await _scraperService.FetchEpisodesListAsync(tvmazeId.Value);
                    if (eps != null)
                    {
                        _showEpisodesCache[id] = eps;
                    }
                }

                return id;
            }

            // 2. Fetch new metadata online
            var meta = await _scraperService.FetchTvShowMetadataAsync(showName);
            int newTvShowId = await _db.GetNextIdAsync("TvShows");

            string insertShowQuery = @"
                INSERT INTO TvShows (Id, Title, Overview, PosterPath, Genres, ReleaseYear, CastJson, DateAdded, TvmazeId)
                VALUES (@Id, @Title, @Overview, @PosterPath, @Genres, @ReleaseYear, @CastJson, @DateAdded, @TvmazeId)";

            var parameters = new Dictionary<string, object?>
            {
                { "@Id", newTvShowId },
                { "@DateAdded", DateTime.Now }
            };

            if (meta != null)
            {
                parameters.Add("@Title", meta.Title);
                parameters.Add("@Overview", string.IsNullOrWhiteSpace(meta.Overview) ? DBNull.Value : meta.Overview);
                parameters.Add("@PosterPath", string.IsNullOrWhiteSpace(meta.PosterPath) ? DBNull.Value : meta.PosterPath);
                parameters.Add("@Genres", string.IsNullOrWhiteSpace(meta.Genres) ? DBNull.Value : meta.Genres);
                parameters.Add("@ReleaseYear", meta.ReleaseYear ?? (object)DBNull.Value);
                
                string castJson = JsonSerializer.Serialize(meta.Cast, _jsonOptions);
                parameters.Add("@CastJson", castJson);
                parameters.Add("@TvmazeId", meta.TvmazeId);
            }
            else
            {
                parameters.Add("@Title", showName);
                parameters.Add("@Overview", DBNull.Value);
                parameters.Add("@PosterPath", DBNull.Value);
                parameters.Add("@Genres", DBNull.Value);
                parameters.Add("@ReleaseYear", DBNull.Value);
                parameters.Add("@CastJson", "[]");
                parameters.Add("@TvmazeId", DBNull.Value);
            }

            await _db.ExecuteNonQueryAsync(insertShowQuery, parameters);

            // 3. Populate episodes cache for new show
            if (meta != null && newTvShowId > 0)
            {
                var eps = await _scraperService.FetchEpisodesListAsync(meta.TvmazeId);
                if (eps != null)
                {
                    _showEpisodesCache[newTvShowId] = eps;
                }
            }

            return newTvShowId;
        }

        private async Task ScanAndSaveChaptersAsync(int mediaItemId, string filePath)
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_entries chapter=start_time,end_time:tags=title -of json \"{filePath}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = new System.Diagnostics.Process { StartInfo = startInfo })
                {
                    process.Start();

                    var readTask = process.StandardOutput.ReadToEndAsync();
                    var delayTask = Task.Delay(3000); // 3 seconds timeout limit

                    var completedTask = await Task.WhenAny(readTask, delayTask);
                    if (completedTask == delayTask)
                    {
                        try { process.Kill(); } catch { }
                        _logger.LogWarning($"ffprobe timed out scanning chapters for: {filePath}");
                        return;
                    }

                    string json = await readTask;
                    await process.WaitForExitAsync();

                    if (string.IsNullOrWhiteSpace(json) || !json.Contains("\"chapters\""))
                    {
                        return;
                    }

                    using (var doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.TryGetProperty("chapters", out var chaptersArr) && chaptersArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var ch in chaptersArr.EnumerateArray())
                            {
                                string title = "";
                                if (ch.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var titleProp))
                                {
                                    title = titleProp.GetString() ?? "";
                                }

                                if (string.IsNullOrWhiteSpace(title)) continue;

                                string lowerTitle = title.ToLowerInvariant();
                                if (lowerTitle.Contains("intro") || lowerTitle.Contains("opening") || 
                                    lowerTitle.Contains("credits") || lowerTitle.Contains("ending") || 
                                    lowerTitle.Contains("prologue"))
                                {
                                    double start = 0;
                                    double end = 0;

                                    if (ch.TryGetProperty("start_time", out var startProp))
                                    {
                                        if (startProp.ValueKind == JsonValueKind.String)
                                            double.TryParse(startProp.GetString(), out start);
                                        else if (startProp.ValueKind == JsonValueKind.Number)
                                            start = startProp.GetDouble();
                                    }

                                    if (ch.TryGetProperty("end_time", out var endProp))
                                    {
                                        if (endProp.ValueKind == JsonValueKind.String)
                                            double.TryParse(endProp.GetString(), out end);
                                        else if (endProp.ValueKind == JsonValueKind.Number)
                                            end = endProp.GetDouble();
                                    }

                                    int nextChapId = await _db.GetNextIdAsync("MediaChapters");
                                    await _db.ExecuteNonQueryAsync(
                                        "INSERT INTO MediaChapters (Id, MediaItemId, Title, StartTime, EndTime) VALUES (@Id, @MediaItemId, @Title, @Start, @End)",
                                        new Dictionary<string, object?>
                                        {
                                            { "@Id", nextChapId },
                                            { "@MediaItemId", mediaItemId },
                                            { "@Title", title },
                                            { "@Start", start },
                                            { "@End", end }
                                        }
                                    );
                                    _logger.LogInformation($"Saved chapter '{title}' ({start}s - {end}s) for MediaItem ID={mediaItemId}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to scan and save chapters for MediaItem ID={mediaItemId}: {filePath}");
            }
        }

        public static (string Title, int? Year) ParseMetadataForType(string filePath, string mediaType)
        {
            string filename = Path.GetFileName(filePath);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(filename);
            
            if (mediaType == "Episode")
            {
                var epRegex = new Regex(@"[sS](?<season>\d+)[eE](?<episode>\d+)", RegexOptions.Compiled);
                var epMatch = epRegex.Match(nameWithoutExt);
                if (epMatch.Success)
                {
                    string season = epMatch.Groups["season"].Value;
                    string episode = epMatch.Groups["episode"].Value;
                    string showName = nameWithoutExt.Substring(0, epMatch.Index).Replace(".", " ").Replace("_", " ").Trim();
                    showName = Regex.Replace(showName, @"\s*-\s*$", "").Trim();
                    if (string.IsNullOrWhiteSpace(showName) || showName.Length <= 2)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(filePath);
                            var parentDir = fileInfo.Directory;
                            if (parentDir != null)
                            {
                                string parentName = parentDir.Name;
                                if (Regex.IsMatch(parentName, @"^(?:[sS]eason|[sS])\s*\d+$", RegexOptions.IgnoreCase))
                                {
                                    var showDir = parentDir.Parent;
                                    if (showDir != null) showName = showDir.Name;
                                }
                                else
                                {
                                    showName = parentName;
                                }
                            }
                        }
                        catch { }
                    }
                    if (string.IsNullOrWhiteSpace(showName)) showName = "Unknown Show";
                    return ($"{showName} - S{season}E{episode}", null);
                }
            }
            else // Movie
            {
                var movieYearRegex = new Regex(@"(?<title>.*?)(?:\s+|\.)\(?(?<year>19\d{2}|20\d{2})\)?", RegexOptions.Compiled);
                var movieMatch = movieYearRegex.Match(nameWithoutExt);
                if (movieMatch.Success)
                {
                    string title = movieMatch.Groups["title"].Value.Replace(".", " ").Replace("_", " ").Trim();
                    if (int.TryParse(movieMatch.Groups["year"].Value, out int year))
                    {
                        title = Regex.Replace(title, @"\s*-\s*$", "");
                        if (string.IsNullOrEmpty(title)) title = nameWithoutExt;
                        return (title, year);
                    }
                }
            }

            string cleanTitle = nameWithoutExt.Replace(".", " ").Replace("_", " ").Trim();
            return (cleanTitle, null);
        }

        private async Task EnsureCollectionAndLinkItemAsync(int mediaItemId, string collectionName, string? fallbackPosterPath)
        {
            if (string.IsNullOrWhiteSpace(collectionName)) return;

            // 1. Get or create collection
            int collectionId = 0;
            var rows = await _db.ExecuteQueryAsync(
                "SELECT Id FROM Collections WHERE Name = @Name",
                new Dictionary<string, object?> { { "@Name", collectionName } }
            );

            if (rows.Count > 0)
            {
                collectionId = Convert.ToInt32(rows[0]["Id"]);
            }

            if (collectionId == 0)
            {
                collectionId = await _db.GetNextIdAsync("Collections");
                string insertCollectionQuery = @"
                    INSERT INTO Collections (Id, Name, PosterPath) 
                    VALUES (@Id, @Name, @PosterPath)";

                await _db.ExecuteNonQueryAsync(insertCollectionQuery, new Dictionary<string, object?>
                {
                    { "@Id", collectionId },
                    { "@Name", collectionName },
                    { "@PosterPath", string.IsNullOrWhiteSpace(fallbackPosterPath) ? DBNull.Value : fallbackPosterPath }
                });
                _logger.LogInformation($"Auto-created Movie Collection: '{collectionName}'");
            }
            else if (!string.IsNullOrWhiteSpace(fallbackPosterPath))
            {
                // Update collection poster if it was empty
                string updatePosterQuery = "UPDATE Collections SET PosterPath = @PosterPath WHERE Id = @Id AND PosterPath IS NULL";
                await _db.ExecuteNonQueryAsync(updatePosterQuery, new Dictionary<string, object?>
                {
                    { "@Id", collectionId },
                    { "@PosterPath", fallbackPosterPath }
                });
            }

            // 2. Link item to collection if not already linked
            int exists = Convert.ToInt32(await _db.ExecuteScalarAsync(
                "SELECT COUNT(*) FROM CollectionMediaItems WHERE CollectionId = @CollectionId AND MediaItemId = @MediaItemId",
                new Dictionary<string, object?>
                {
                    { "@CollectionId", collectionId },
                    { "@MediaItemId", mediaItemId }
                }
            ) ?? 0);

            if (exists == 0)
            {
                string insertLinkQuery = "INSERT INTO CollectionMediaItems (CollectionId, MediaItemId) VALUES (@CollectionId, @MediaItemId)";
                await _db.ExecuteNonQueryAsync(insertLinkQuery, new Dictionary<string, object?>
                {
                    { "@CollectionId", collectionId },
                    { "@MediaItemId", mediaItemId }
                });
                _logger.LogInformation($"Linked movie ID={mediaItemId} to collection '{collectionName}'");
            }
        }

        private async Task<bool> ShouldAutoCreateCollectionsAsync()
        {
            try
            {
                var rows = await _db.ExecuteQueryAsync("SELECT SettingValue FROM SystemSettings WHERE SettingKey = 'AutoCreateCollections'");
                if (rows.Count > 0)
                {
                    return rows[0]["SettingValue"]?.ToString() == "true";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not load AutoCreateCollections setting: {ex.Message}. Defaulting to true.");
            }
            return true;
        }

        private async Task EnsureSpecsAndSubtitlesAsync(int mediaItemId, string filePath)
        {
            var itemRows = await _db.ExecuteQueryAsync("SELECT DurationInSeconds FROM MediaItems WHERE Id = @Id",
                new Dictionary<string, object?> { { "@Id", mediaItemId } });
            int duration = 0;
            if (itemRows.Count > 0 && itemRows[0]["DurationInSeconds"] != null && !(itemRows[0]["DurationInSeconds"] is DBNull))
            {
                duration = Convert.ToInt32(itemRows[0]["DurationInSeconds"]);
            }

            var specs = await _db.ExecuteQueryAsync("SELECT MediaItemId FROM MediaSpecs WHERE MediaItemId = @MediaItemId",
                new Dictionary<string, object?> { { "@MediaItemId", mediaItemId } });
            if (specs.Count == 0 || duration == 0)
            {
                await ScanAndSaveSpecsAsync(mediaItemId, filePath);
            }

            var subs = await _db.ExecuteQueryAsync("SELECT Id FROM MediaSubtitles WHERE MediaItemId = @MediaItemId",
                new Dictionary<string, object?> { { "@MediaItemId", mediaItemId } });
            if (subs.Count == 0)
            {
                await DiscoverAndSaveSubtitlesAsync(mediaItemId, filePath);
            }
        }

        private async Task ScanAndSaveSpecsAsync(int mediaItemId, string filePath)
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_format -show_streams -of json \"{filePath}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = new System.Diagnostics.Process { StartInfo = startInfo })
                {
                    process.Start();
                    var readTask = process.StandardOutput.ReadToEndAsync();
                    var delayTask = Task.Delay(3000);

                    var completedTask = await Task.WhenAny(readTask, delayTask);
                    if (completedTask == delayTask)
                    {
                        try { process.Kill(); } catch { }
                        return;
                    }

                    string json = await readTask;
                    await process.WaitForExitAsync();

                    if (string.IsNullOrWhiteSpace(json)) return;

                    using (var doc = JsonDocument.Parse(json))
                    {
                        string container = "";
                        string videoCodec = "";
                        string videoResolution = "";
                        int videoBitrate = 0;
                        string videoFrameRate = "";
                        string audioCodec = "";
                        int audioChannels = 0;
                        long fileSize = 0;

                        double durationSeconds = 0;
                        if (doc.RootElement.TryGetProperty("format", out var formatEl))
                        {
                            if (formatEl.TryGetProperty("format_name", out var formName))
                                container = formName.GetString() ?? "";
                            if (formatEl.TryGetProperty("size", out var sizeProp))
                                long.TryParse(sizeProp.GetString(), out fileSize);
                            if (formatEl.TryGetProperty("bit_rate", out var brProp))
                                int.TryParse(brProp.GetString(), out videoBitrate);
                            if (formatEl.TryGetProperty("duration", out var durProp))
                                double.TryParse(durProp.GetString(), out durationSeconds);
                        }

                        if (doc.RootElement.TryGetProperty("streams", out var streamsArr) && streamsArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var stream in streamsArr.EnumerateArray())
                            {
                                string codecType = "";
                                if (stream.TryGetProperty("codec_type", out var typeProp))
                                    codecType = typeProp.GetString() ?? "";

                                if (codecType == "video" && string.IsNullOrEmpty(videoCodec))
                                {
                                    if (stream.TryGetProperty("codec_name", out var vcProp))
                                        videoCodec = vcProp.GetString() ?? "";
                                    int w = 0, h = 0;
                                    if (stream.TryGetProperty("width", out var wProp)) w = wProp.GetInt32();
                                    if (stream.TryGetProperty("height", out var hProp)) h = hProp.GetInt32();
                                    if (w > 0 && h > 0)
                                        videoResolution = $"{w}x{h}";
                                    if (stream.TryGetProperty("avg_frame_rate", out var frProp))
                                        videoFrameRate = frProp.GetString() ?? "";
                                }
                                else if (codecType == "audio" && string.IsNullOrEmpty(audioCodec))
                                {
                                    if (stream.TryGetProperty("codec_name", out var acProp))
                                        audioCodec = acProp.GetString() ?? "";
                                    if (stream.TryGetProperty("channels", out var chProp))
                                        audioChannels = chProp.GetInt32();
                                }
                            }
                        }

                        // Save duration to MediaItems if extracted
                        if (durationSeconds > 0)
                        {
                            await _db.ExecuteNonQueryAsync(
                                "UPDATE MediaItems SET DurationInSeconds = @Duration WHERE Id = @Id",
                                new Dictionary<string, object?>
                                {
                                    { "@Duration", (int)durationSeconds },
                                    { "@Id", mediaItemId }
                                }
                            );
                        }

                        // Insert or Update MediaSpecs
                        var specsExists = await _db.ExecuteQueryAsync("SELECT MediaItemId FROM MediaSpecs WHERE MediaItemId = @MediaItemId",
                            new Dictionary<string, object?> { { "@MediaItemId", mediaItemId } });

                        if (specsExists.Count == 0)
                        {
                            string insertSpecsQuery = @"
                                INSERT INTO MediaSpecs (MediaItemId, Container, VideoCodec, VideoResolution, VideoBitrate, VideoFrameRate, AudioCodec, AudioChannels, FileSize)
                                VALUES (@MediaItemId, @Container, @VideoCodec, @VideoResolution, @VideoBitrate, @VideoFrameRate, @AudioCodec, @AudioChannels, @FileSize)";
                            
                            await _db.ExecuteNonQueryAsync(insertSpecsQuery, new Dictionary<string, object?>
                            {
                                { "@MediaItemId", mediaItemId },
                                { "@Container", container },
                                { "@VideoCodec", videoCodec },
                                { "@VideoResolution", videoResolution },
                                { "@VideoBitrate", videoBitrate },
                                { "@VideoFrameRate", videoFrameRate },
                                { "@AudioCodec", audioCodec },
                                { "@AudioChannels", audioChannels },
                                { "@FileSize", fileSize.ToString() }
                            });
                        }
                        else
                        {
                            string updateSpecsQuery = @"
                                UPDATE MediaSpecs 
                                SET Container = @Container, VideoCodec = @VideoCodec, VideoResolution = @VideoResolution, 
                                    VideoBitrate = @VideoBitrate, VideoFrameRate = @VideoFrameRate, AudioCodec = @AudioCodec, 
                                    AudioChannels = @AudioChannels, FileSize = @FileSize 
                                WHERE MediaItemId = @MediaItemId";
                            
                            await _db.ExecuteNonQueryAsync(updateSpecsQuery, new Dictionary<string, object?>
                            {
                                { "@MediaItemId", mediaItemId },
                                { "@Container", container },
                                { "@VideoCodec", videoCodec },
                                { "@VideoResolution", videoResolution },
                                { "@VideoBitrate", videoBitrate },
                                { "@VideoFrameRate", videoFrameRate },
                                { "@AudioCodec", audioCodec },
                                { "@AudioChannels", audioChannels },
                                { "@FileSize", fileSize.ToString() }
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error scanning media specs for ID {mediaItemId}: {ex.Message}");
            }
        }

        private async Task DiscoverAndSaveSubtitlesAsync(int mediaItemId, string filePath)
        {
            try
            {
                // 1. Scan for internal subtitle streams
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_entries stream=index,codec_type,codec_name:stream_tags=language,title -of json \"{filePath}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = new System.Diagnostics.Process { StartInfo = startInfo })
                {
                    process.Start();
                    var readTask = process.StandardOutput.ReadToEndAsync();
                    var delayTask = Task.Delay(3000);

                    var completedTask = await Task.WhenAny(readTask, delayTask);
                    if (completedTask != delayTask)
                    {
                        string json = await readTask;
                        await process.WaitForExitAsync();

                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            using (var doc = JsonDocument.Parse(json))
                            {
                                if (doc.RootElement.TryGetProperty("streams", out var streamsArr) && streamsArr.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var stream in streamsArr.EnumerateArray())
                                    {
                                        string codecType = "";
                                        if (stream.TryGetProperty("codec_type", out var typeProp))
                                            codecType = typeProp.GetString() ?? "";

                                        if (codecType == "subtitle")
                                        {
                                            int streamIndex = 0;
                                            if (stream.TryGetProperty("index", out var idxProp))
                                                streamIndex = idxProp.GetInt32();

                                            string codecName = "";
                                            if (stream.TryGetProperty("codec_name", out var cProp))
                                                codecName = cProp.GetString() ?? "";

                                            string language = "eng";
                                            string title = $"Track {streamIndex}";

                                            if (stream.TryGetProperty("tags", out var tags))
                                            {
                                                if (tags.TryGetProperty("language", out var langProp))
                                                    language = langProp.GetString() ?? "eng";
                                                if (tags.TryGetProperty("title", out var titleProp))
                                                    title = titleProp.GetString() ?? title;
                                            }

                                            int subId = await _db.GetNextIdAsync("MediaSubtitles");
                                            string insertSubQuery = @"
                                                INSERT INTO MediaSubtitles (Id, MediaItemId, SubtitleType, Language, Title, Format, StreamIndex, FilePath)
                                                VALUES (@Id, @MediaItemId, 'Internal', @Language, @Title, @Format, @StreamIndex, NULL)";

                                            await _db.ExecuteNonQueryAsync(insertSubQuery, new Dictionary<string, object?>
                                            {
                                                { "@Id", subId },
                                                { "@MediaItemId", mediaItemId },
                                                { "@Language", language },
                                                { "@Title", title },
                                                { "@Format", codecName },
                                                { "@StreamIndex", streamIndex }
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        try { process.Kill(); } catch { }
                    }
                }

                // 2. Scan for external subtitle files in same directory
                var fileInfo = new FileInfo(filePath);
                var directory = fileInfo.Directory;
                if (directory != null && directory.Exists)
                {
                    string baseName = Path.GetFileNameWithoutExtension(filePath);
                    var candidateFiles = directory.GetFiles($"{baseName}*");
                    foreach (var cand in candidateFiles)
                    {
                        string ext = cand.Extension.ToLowerInvariant();
                        if (ext == ".srt" || ext == ".vtt")
                        {
                            if (cand.FullName.Equals(filePath, StringComparison.OrdinalIgnoreCase)) continue;

                            string candName = Path.GetFileNameWithoutExtension(cand.Name);
                            string language = "eng";
                            string title = "External Subtitle";

                            if (candName.Length > baseName.Length)
                            {
                                string suffix = candName.Substring(baseName.Length).TrimStart('.');
                                if (suffix.Length >= 2)
                                {
                                    language = suffix.Split('.')[0];
                                    title = $"External ({language.ToUpper()})";
                                }
                            }

                            int subId = await _db.GetNextIdAsync("MediaSubtitles");
                            string insertSubQuery = @"
                                INSERT INTO MediaSubtitles (Id, MediaItemId, SubtitleType, Language, Title, Format, StreamIndex, FilePath)
                                VALUES (@Id, @MediaItemId, 'External', @Language, @Title, @Format, NULL, @FilePath)";

                            await _db.ExecuteNonQueryAsync(insertSubQuery, new Dictionary<string, object?>
                            {
                                { "@Id", subId },
                                { "@MediaItemId", mediaItemId },
                                { "@Language", language },
                                { "@Title", title },
                                { "@Format", ext.TrimStart('.') },
                                { "@FilePath", cand.FullName }
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error discovering subtitles for ID {mediaItemId}: {ex.Message}");
            }
        }
    }

    public record ScanFolder(int Id, string FolderPath, string MediaType);
}

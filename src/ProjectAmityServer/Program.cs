using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ProjectAmityServer
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                });
            });

            // Register background scanner, database service and scraper as singletons
            builder.Services.AddSingleton<GlacierDbService>();
            builder.Services.AddSingleton<MetadataScraperService>();
            builder.Services.AddSingleton<DirectoryScannerService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<DirectoryScannerService>());

            var app = builder.Build();

            // Enable CORS, static file serving, and default index.html redirection
            app.UseCors();
            app.UseDefaultFiles();
            app.UseStaticFiles();

            // Initialize the database and table structures on startup
            try
            {
                var dbService = app.Services.GetRequiredService<GlacierDbService>();
                await DbInitializer.InitializeAsync(dbService, app.Logger);
            }
            catch (Exception ex)
            {
                app.Logger.LogCritical(ex, "Failed to initialize the database.");
            }

            // ==================== MEDIA LIBRARY ENDPOINTS ====================

            // 1. GET /api/media - Get all media items (supports optional type filtering)
            app.MapGet("/api/media", async (string? mediaType, GlacierDbService db) =>
            {
                var list = new List<MediaItem>();
                try
                {
                    string query = @"
                        SELECT Id, Title, FilePath, MediaType, ReleaseYear, DurationInSeconds, ResumePositionInSeconds, DateAdded,
                               PosterPath, Overview, Genres, Director, CastJson, TvShowId, SeasonNumber, EpisodeNumber, Watched, LastWatched
                        FROM MediaItems";
                    
                    var parameters = new Dictionary<string, object?>();
                    if (!string.IsNullOrEmpty(mediaType))
                    {
                        query += " WHERE MediaType = @MediaType";
                        parameters.Add("@MediaType", mediaType);
                    }
                    query += " ORDER BY Title ASC";

                    var rows = await db.ExecuteQueryAsync(query, parameters);
                    foreach (var row in rows)
                    {
                        list.Add(new MediaItem(
                            Convert.ToInt32(row["Id"]),
                            row["Title"]?.ToString() ?? "",
                            row["FilePath"]?.ToString() ?? "",
                            row["MediaType"]?.ToString() ?? "",
                            row["ReleaseYear"] == null || row["ReleaseYear"] is DBNull ? null : Convert.ToInt32(row["ReleaseYear"]),
                            Convert.ToInt32(row["DurationInSeconds"]),
                            Convert.ToInt32(row["ResumePositionInSeconds"]),
                            Convert.ToDateTime(row["DateAdded"]),
                            row["PosterPath"] == null || row["PosterPath"] is DBNull ? null : row["PosterPath"]?.ToString(),
                            row["Overview"] == null || row["Overview"] is DBNull ? null : row["Overview"]?.ToString(),
                            row["Genres"] == null || row["Genres"] is DBNull ? null : row["Genres"]?.ToString(),
                            row["Director"] == null || row["Director"] is DBNull ? null : row["Director"]?.ToString(),
                            row["CastJson"] == null || row["CastJson"] is DBNull ? null : row["CastJson"]?.ToString(),
                            row["TvShowId"] == null || row["TvShowId"] is DBNull ? null : Convert.ToInt32(row["TvShowId"]),
                            row["SeasonNumber"] == null || row["SeasonNumber"] is DBNull ? null : Convert.ToInt32(row["SeasonNumber"]),
                            row["EpisodeNumber"] == null || row["EpisodeNumber"] is DBNull ? null : Convert.ToInt32(row["EpisodeNumber"]),
                            row["Watched"] == null || row["Watched"] is DBNull ? 0 : Convert.ToInt32(row["Watched"]),
                            row["LastWatched"] == null || row["LastWatched"] is DBNull ? null : (DateTime?)Convert.ToDateTime(row["LastWatched"])
                        ));
                    }
                    return Results.Ok(list);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Error getting media items");
                    return Results.Problem("Database query failed.");
                }
            });

            // 1b. GET /api/media/continue-watching - Get in-progress movies and episodes sorted by last watched
            app.MapGet("/api/media/continue-watching", async (GlacierDbService db) =>
            {
                var list = new List<ContinueWatchingItem>();
                try
                {
                    // Fetch all media items
                    string query = @"
                        SELECT Id, Title, FilePath, MediaType, ReleaseYear, DurationInSeconds, ResumePositionInSeconds, DateAdded, LastWatched, Watched,
                               PosterPath, Overview, Genres, Director, CastJson, TvShowId, SeasonNumber, EpisodeNumber
                        FROM MediaItems";
                    
                    var rows = await db.ExecuteQueryAsync(query);
                    
                    // Filter in C# memory
                    var filteredRows = rows.Where(row => {
                        int resumePos = Convert.ToInt32(row["ResumePositionInSeconds"]);
                        int duration = Convert.ToInt32(row["DurationInSeconds"]);
                        int watched = row["Watched"] == null || row["Watched"] is DBNull ? 0 : Convert.ToInt32(row["Watched"]);
                        return resumePos > 5 && resumePos < (duration - 15) && duration > 30 && watched != 1;
                    }).ToList();

                    if (filteredRows.Count > 0)
                    {
                        // Fetch TV Shows to resolve parent details
                        var tvRows = await db.ExecuteQueryAsync("SELECT Id, Title, PosterPath FROM TvShows");
                        var tvShows = tvRows.ToDictionary(
                            r => Convert.ToInt32(r["Id"]),
                            r => new {
                                Title = r["Title"]?.ToString() ?? "",
                                PosterPath = r["PosterPath"] == null || r["PosterPath"] is DBNull ? null : r["PosterPath"]?.ToString()
                            }
                        );

                        foreach (var row in filteredRows)
                        {
                            int? tvShowId = row["TvShowId"] == null || row["TvShowId"] is DBNull ? null : Convert.ToInt32(row["TvShowId"]);
                            string? posterPath = row["PosterPath"] == null || row["PosterPath"] is DBNull ? null : row["PosterPath"]?.ToString();
                            string? tvShowTitle = null;

                            if (tvShowId.HasValue && tvShows.TryGetValue(tvShowId.Value, out var show))
                            {
                                tvShowTitle = show.Title;
                                if (string.IsNullOrEmpty(posterPath))
                                {
                                    posterPath = show.PosterPath;
                                }
                            }

                            DateTime dateAdded = Convert.ToDateTime(row["DateAdded"]);
                            DateTime? lastWatched = null;
                            if (row["LastWatched"] != null && !(row["LastWatched"] is DBNull))
                            {
                                try
                                {
                                    lastWatched = Convert.ToDateTime(row["LastWatched"]);
                                }
                                catch
                                {
                                    // Handle timestamp conversion if stored as raw seconds
                                    if (long.TryParse(row["LastWatched"]?.ToString(), out long sec))
                                    {
                                        lastWatched = DateTimeOffset.FromUnixTimeSeconds(sec).DateTime;
                                    }
                                }
                            }
                            
                            list.Add(new ContinueWatchingItem(
                                Convert.ToInt32(row["Id"]),
                                row["Title"]?.ToString() ?? "",
                                row["FilePath"]?.ToString() ?? "",
                                row["MediaType"]?.ToString() ?? "",
                                row["ReleaseYear"] == null || row["ReleaseYear"] is DBNull ? null : Convert.ToInt32(row["ReleaseYear"]),
                                Convert.ToInt32(row["DurationInSeconds"]),
                                Convert.ToInt32(row["ResumePositionInSeconds"]),
                                dateAdded,
                                posterPath,
                                row["Overview"] == null || row["Overview"] is DBNull ? null : row["Overview"]?.ToString(),
                                row["Genres"] == null || row["Genres"] is DBNull ? null : row["Genres"]?.ToString(),
                                row["Director"] == null || row["Director"] is DBNull ? null : row["Director"]?.ToString(),
                                row["CastJson"] == null || row["CastJson"] is DBNull ? null : row["CastJson"]?.ToString(),
                                tvShowId,
                                row["SeasonNumber"] == null || row["SeasonNumber"] is DBNull ? null : Convert.ToInt32(row["SeasonNumber"]),
                                row["EpisodeNumber"] == null || row["EpisodeNumber"] is DBNull ? null : Convert.ToInt32(row["EpisodeNumber"]),
                                tvShowTitle,
                                lastWatched
                            ));
                        }

                        // Sort by lastWatched desc, then dateAdded desc
                        list = list.OrderByDescending(item => item.LastWatched ?? item.DateAdded).ToList();
                    }

                    return Results.Ok(list);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Error getting continue watching list");
                    return Results.Problem("Database query failed.");
                }
            });

            // 2. GET /api/media/{id} - Get metadata of a single media item
            app.MapGet("/api/media/{id}", async (int id, GlacierDbService db) =>
            {
                try
                {
                    string query = @"
                        SELECT Id, Title, FilePath, MediaType, ReleaseYear, DurationInSeconds, ResumePositionInSeconds, DateAdded,
                               PosterPath, Overview, Genres, Director, CastJson, TvShowId, SeasonNumber, EpisodeNumber, Watched, LastWatched
                        FROM MediaItems WHERE Id = @Id";
                    
                    var rows = await db.ExecuteQueryAsync(query, new Dictionary<string, object?> { { "@Id", id } });
                    if (rows.Count > 0)
                    {
                        var row = rows[0];
                        var item = new MediaItem(
                            Convert.ToInt32(row["Id"]),
                            row["Title"]?.ToString() ?? "",
                            row["FilePath"]?.ToString() ?? "",
                            row["MediaType"]?.ToString() ?? "",
                            row["ReleaseYear"] == null || row["ReleaseYear"] is DBNull ? null : Convert.ToInt32(row["ReleaseYear"]),
                            Convert.ToInt32(row["DurationInSeconds"]),
                            Convert.ToInt32(row["ResumePositionInSeconds"]),
                            Convert.ToDateTime(row["DateAdded"]),
                            row["PosterPath"] == null || row["PosterPath"] is DBNull ? null : row["PosterPath"]?.ToString(),
                            row["Overview"] == null || row["Overview"] is DBNull ? null : row["Overview"]?.ToString(),
                            row["Genres"] == null || row["Genres"] is DBNull ? null : row["Genres"]?.ToString(),
                            row["Director"] == null || row["Director"] is DBNull ? null : row["Director"]?.ToString(),
                            row["CastJson"] == null || row["CastJson"] is DBNull ? null : row["CastJson"]?.ToString(),
                            row["TvShowId"] == null || row["TvShowId"] is DBNull ? null : Convert.ToInt32(row["TvShowId"]),
                            row["SeasonNumber"] == null || row["SeasonNumber"] is DBNull ? null : Convert.ToInt32(row["SeasonNumber"]),
                            row["EpisodeNumber"] == null || row["EpisodeNumber"] is DBNull ? null : Convert.ToInt32(row["EpisodeNumber"]),
                            row["Watched"] == null || row["Watched"] is DBNull ? 0 : Convert.ToInt32(row["Watched"]),
                            row["LastWatched"] == null || row["LastWatched"] is DBNull ? null : (DateTime?)Convert.ToDateTime(row["LastWatched"])
                        );
                        return Results.Ok(item);
                    }
                    return Results.NotFound($"Media item with ID {id} not found.");
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error getting media item {id}");
                    return Results.Problem("Database query failed.");
                }
            });

            // 2b. GET /api/tvshows - Get all TV shows (Series summary list)
            app.MapGet("/api/tvshows", async (GlacierDbService db) =>
            {
                var list = new List<TvShowItem>();
                try
                {
                    string query = @"
                        SELECT Id, Title, Overview, PosterPath, Genres, ReleaseYear, CastJson, DateAdded
                        FROM TvShows
                        ORDER BY Title ASC";

                    var rows = await db.ExecuteQueryAsync(query);
                    foreach (var row in rows)
                    {
                        int showId = Convert.ToInt32(row["Id"]);
                        
                        // Query episode rows for this show to calculate counts and seasons in C#
                        var epRows = await db.ExecuteQueryAsync(
                            "SELECT SeasonNumber, LastWatched, Watched FROM MediaItems WHERE TvShowId = @ShowId AND MediaType = 'Episode'",
                            new Dictionary<string, object?> { { "@ShowId", showId } }
                        );

                        int epCount = epRows.Count;
                        int seasonCount = epRows
                            .Select(r => r["SeasonNumber"])
                            .Where(s => s != null && s is not DBNull)
                            .Select(s => Convert.ToInt32(s))
                            .Distinct()
                            .Count();

                        int unwatchedCount = epRows
                            .Select(r => r["Watched"])
                            .Where(w => w == null || w is DBNull || Convert.ToInt32(w) != 1)
                            .Count();

                        DateTime? showLastWatched = null;
                        foreach (var epRow in epRows)
                        {
                            if (epRow["LastWatched"] != null && !(epRow["LastWatched"] is DBNull))
                            {
                                var epLastWatched = Convert.ToDateTime(epRow["LastWatched"]);
                                if (showLastWatched == null || epLastWatched > showLastWatched)
                                {
                                    showLastWatched = epLastWatched;
                                }
                            }
                        }

                        list.Add(new TvShowItem(
                            showId,
                            row["Title"]?.ToString() ?? "",
                            row["Overview"] == null || row["Overview"] is DBNull ? null : row["Overview"]?.ToString(),
                            row["PosterPath"] == null || row["PosterPath"] is DBNull ? null : row["PosterPath"]?.ToString(),
                            row["Genres"] == null || row["Genres"] is DBNull ? null : row["Genres"]?.ToString(),
                            row["ReleaseYear"] == null || row["ReleaseYear"] is DBNull ? null : Convert.ToInt32(row["ReleaseYear"]),
                            row["CastJson"] == null || row["CastJson"] is DBNull ? null : row["CastJson"]?.ToString(),
                            Convert.ToDateTime(row["DateAdded"]),
                            epCount,
                            seasonCount,
                            showLastWatched,
                            unwatchedCount
                        ));
                    }
                    return Results.Ok(list);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Error getting TV shows list");
                    return Results.Problem("Database query failed.");
                }
            });

            // 2c. GET /api/tvshows/{id} - Get TV Show metadata and episodes grouped
            app.MapGet("/api/tvshows/{id}", async (int id, GlacierDbService db) =>
            {
                try
                {
                    // 1. Fetch TV Show info
                    object? showObj = null;
                    string showQuery = "SELECT Id, Title, Overview, PosterPath, Genres, ReleaseYear, CastJson, DateAdded FROM TvShows WHERE Id = @Id";
                    var showRows = await db.ExecuteQueryAsync(showQuery, new Dictionary<string, object?> { { "@Id", id } });
                    if (showRows.Count > 0)
                    {
                        var row = showRows[0];
                        showObj = new {
                            Id = Convert.ToInt32(row["Id"]),
                            Title = row["Title"]?.ToString() ?? "",
                            Overview = row["Overview"] == null || row["Overview"] is DBNull ? null : row["Overview"]?.ToString(),
                            PosterPath = row["PosterPath"] == null || row["PosterPath"] is DBNull ? null : row["PosterPath"]?.ToString(),
                            Genres = row["Genres"] == null || row["Genres"] is DBNull ? null : row["Genres"]?.ToString(),
                            ReleaseYear = row["ReleaseYear"] == null || row["ReleaseYear"] is DBNull ? null : (int?)Convert.ToInt32(row["ReleaseYear"]),
                            CastJson = row["CastJson"] == null || row["CastJson"] is DBNull ? null : row["CastJson"]?.ToString(),
                            DateAdded = Convert.ToDateTime(row["DateAdded"])
                        };
                    }

                    if (showObj == null)
                    {
                        return Results.NotFound($"TV Show with ID {id} not found.");
                    }

                    // 2. Fetch episodes grouped under this show
                    var episodes = new List<MediaItem>();
                    string epQuery = @"
                        SELECT Id, Title, FilePath, MediaType, ReleaseYear, DurationInSeconds, ResumePositionInSeconds, DateAdded, 
                               PosterPath, Overview, Genres, Director, CastJson, TvShowId, SeasonNumber, EpisodeNumber, Watched, LastWatched 
                        FROM MediaItems 
                        WHERE TvShowId = @TvShowId AND MediaType = 'Episode'
                        ORDER BY SeasonNumber ASC, EpisodeNumber ASC";
                    
                    var epRows = await db.ExecuteQueryAsync(epQuery, new Dictionary<string, object?> { { "@TvShowId", id } });
                    foreach (var row in epRows)
                    {
                        episodes.Add(new MediaItem(
                            Convert.ToInt32(row["Id"]),
                            row["Title"]?.ToString() ?? "",
                            row["FilePath"]?.ToString() ?? "",
                            row["MediaType"]?.ToString() ?? "",
                            row["ReleaseYear"] == null || row["ReleaseYear"] is DBNull ? null : Convert.ToInt32(row["ReleaseYear"]),
                            Convert.ToInt32(row["DurationInSeconds"]),
                            Convert.ToInt32(row["ResumePositionInSeconds"]),
                            Convert.ToDateTime(row["DateAdded"]),
                            row["PosterPath"] == null || row["PosterPath"] is DBNull ? null : row["PosterPath"]?.ToString(),
                            row["Overview"] == null || row["Overview"] is DBNull ? null : row["Overview"]?.ToString(),
                            row["Genres"] == null || row["Genres"] is DBNull ? null : row["Genres"]?.ToString(),
                            row["Director"] == null || row["Director"] is DBNull ? null : row["Director"]?.ToString(),
                            row["CastJson"] == null || row["CastJson"] is DBNull ? null : row["CastJson"]?.ToString(),
                            row["TvShowId"] == null || row["TvShowId"] is DBNull ? null : Convert.ToInt32(row["TvShowId"]),
                            row["SeasonNumber"] == null || row["SeasonNumber"] is DBNull ? null : Convert.ToInt32(row["SeasonNumber"]),
                            row["EpisodeNumber"] == null || row["EpisodeNumber"] is DBNull ? null : Convert.ToInt32(row["EpisodeNumber"]),
                            row["Watched"] == null || row["Watched"] is DBNull ? 0 : Convert.ToInt32(row["Watched"]),
                            row["LastWatched"] == null || row["LastWatched"] is DBNull ? null : (DateTime?)Convert.ToDateTime(row["LastWatched"])
                        ));
                    }

                    episodes = episodes.OrderBy(e => e.SeasonNumber ?? 1).ThenBy(e => e.EpisodeNumber ?? 1).ToList();
                    return Results.Ok(new { Show = showObj, Episodes = episodes });
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error getting TV show details for {id}");
                    return Results.Problem("Database query failed.");
                }
            });

            // 3. POST /api/media/{id}/resume - Update resume position (heartbeat)
            app.MapPost("/api/media/{id}/resume", async (int id, [FromBody] ResumeRequest request, GlacierDbService db) =>
            {
                if (request == null || request.Position < 0)
                {
                    return Results.BadRequest("Invalid position value.");
                }

                try
                {
                    int rows = await db.UpdateRowAsync("MediaItems", id, new Dictionary<string, object?>
                    {
                        { "ResumePositionInSeconds", request.Position },
                        { "LastWatched", DateTime.Now }
                    });
                    
                    if (rows > 0)
                    {
                        return Results.NoContent();
                    }
                    return Results.NotFound($"Media item with ID {id} not found.");
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error updating resume position for media item {id}");
                    return Results.Problem("Database update failed.");
                }
            });

            // 4. POST /api/media/{id}/duration - Update video duration (sent on initial video load)
            app.MapPost("/api/media/{id}/duration", async (int id, [FromBody] DurationRequest request, GlacierDbService db) =>
            {
                if (request == null || request.Duration <= 0)
                {
                    return Results.BadRequest("Invalid duration value.");
                }

                try
                {
                    int rows = await db.UpdateRowAsync("MediaItems", id, new Dictionary<string, object?>
                    {
                        { "DurationInSeconds", request.Duration }
                    });

                    if (rows > 0)
                    {
                        return Results.NoContent();
                    }
                    return Results.NotFound($"Media item with ID {id} not found.");
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error updating duration for media item {id}");
                    return Results.Problem("Database update failed.");
                }
            });

            // 5. DELETE /api/media/{id} - Remove media item from index
            app.MapDelete("/api/media/{id}", async (int id, GlacierDbService db) =>
            {
                try
                {
                    string query = "DELETE FROM MediaItems WHERE Id = @Id";
                    int rows = await db.ExecuteNonQueryAsync(query, new Dictionary<string, object?> { { "@Id", id } });
                    if (rows > 0)
                    {
                        return Results.NoContent();
                    }
                    return Results.NotFound($"Media item with ID {id} not found.");
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error deleting media item {id}");
                    return Results.Problem("Database delete failed.");
                }
            });

            // 6. POST /api/media/scan - Manually trigger recursive media directory scan
            app.MapPost("/api/media/scan", (DirectoryScannerService scanner) =>
            {
                _ = Task.Run(() => scanner.ScanNowAsync());
                return Results.Accepted(value: new { message = "Scan triggered." });
            });

            // 7. GET /api/media/stream/{id} - Stream video with HTTP range processing & quality scaling
            app.MapGet("/api/media/stream/{id}", async (int id, HttpContext context, GlacierDbService db) =>
            {
                string? filePath = null;
                try
                {
                    string query = "SELECT FilePath FROM MediaItems WHERE Id = @Id";
                    var rows = await db.ExecuteQueryAsync(query, new Dictionary<string, object?> { { "@Id", id } });
                    if (rows.Count > 0)
                    {
                        filePath = rows[0]["FilePath"]?.ToString();
                    }

                    if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                    {
                        return Results.NotFound("Media file not found on disk.");
                    }

                    string? quality = context.Request.Query["quality"];
                    string ext = Path.GetExtension(filePath).ToLowerInvariant();

                    // If quality is original/unspecified AND it's native browser container, do direct play
                    if ((string.IsNullOrEmpty(quality) || quality == "original") && 
                        (ext == ".mp4" || ext == ".m4v" || ext == ".webm"))
                    {
                        string contentType = ext == ".webm" ? "video/webm" : "video/mp4";
                        var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        return Results.File(fileStream, contentType, enableRangeProcessing: true);
                    }
                    else
                    {
                        // On-the-fly fragmented MP4 remuxing / scaling transcode
                        context.Response.ContentType = "video/mp4";
                        context.Response.Headers.Append("Connection", "keep-alive");

                        string videoCodec = "-c:v copy";
                        string audioCodec = "-c:a aac -b:a 192k";

                        if (!string.IsNullOrEmpty(quality) && quality != "original")
                        {
                            if (quality == "1080p")
                            {
                                videoCodec = "-c:v libx264 -vf scale=-2:1080 -preset fast -crf 22";
                                audioCodec = "-c:a aac -b:a 192k";
                            }
                            else if (quality == "720p")
                            {
                                videoCodec = "-c:v libx264 -vf scale=-2:720 -preset fast -crf 23";
                                audioCodec = "-c:a aac -b:a 128k";
                            }
                            else if (quality == "480p")
                            {
                                videoCodec = "-c:v libx264 -vf scale=-2:480 -preset fast -crf 24";
                                audioCodec = "-c:a aac -b:a 96k";
                            }
                        }

                        var processStartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            Arguments = $"-i \"{filePath}\" {videoCodec} {audioCodec} -map 0:v:0? -map 0:a? -f mp4 -movflags frag_keyframe+empty_moov+default_base_moof pipe:1",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        var process = new System.Diagnostics.Process { StartInfo = processStartInfo };
                        process.Start();

                        // Consume standard error in a separate task to prevent ffmpeg from blocking
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using (var errReader = process.StandardError)
                                {
                                    while (await errReader.ReadLineAsync() != null) { }
                                }
                            }
                            catch { }
                        });

                        var responseStream = context.Response.Body;
                        try
                        {
                            await process.StandardOutput.BaseStream.CopyToAsync(responseStream, context.RequestAborted);
                        }
                        catch (OperationCanceledException)
                        {
                            // Browser disconnected
                        }
                        finally
                        {
                            if (!process.HasExited)
                            {
                                try
                                {
                                    process.Kill();
                                }
                                catch { }
                            }
                        }

                        return Results.Empty;
                    }
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error streaming media item {id}");
                    return Results.Problem("Error occurred while opening the video stream.");
                }
            });

            // ==================== SETTINGS & DIRECTORY MANAGEMENT ENDPOINTS ====================

            // 8. GET /api/settings/folders - List all scan folders
            app.MapGet("/api/settings/folders", async (GlacierDbService db) =>
            {
                var list = new List<ScanFolder>();
                try
                {
                    string query = "SELECT Id, FolderPath, MediaType FROM ScanFolders ORDER BY MediaType, FolderPath";
                    var rows = await db.ExecuteQueryAsync(query);
                    foreach (var row in rows)
                    {
                        list.Add(new ScanFolder(
                            Convert.ToInt32(row["Id"]),
                            row["FolderPath"]?.ToString() ?? "",
                            row["MediaType"]?.ToString() ?? ""
                        ));
                    }
                    return Results.Ok(list);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Error getting scan folders");
                    return Results.Problem("Database query failed.");
                }
            });

            // 9. POST /api/settings/folders - Add a new scan folder
            app.MapPost("/api/settings/folders", async ([FromBody] FolderRequest request, GlacierDbService db) =>
            {
                if (request == null || string.IsNullOrWhiteSpace(request.FolderPath) || string.IsNullOrWhiteSpace(request.MediaType))
                {
                    return Results.BadRequest("Invalid folder path or media type settings.");
                }

                // Clean and normalize type
                string cleanType = request.MediaType.Equals("Movie", StringComparison.OrdinalIgnoreCase) ? "Movie" : "Episode";

                try
                {
                    // Create the directory on local disk if it doesn't exist
                    if (!Directory.Exists(request.FolderPath))
                    {
                        try
                        {
                            Directory.CreateDirectory(request.FolderPath);
                        }
                        catch (Exception ioEx)
                        {
                            return Results.BadRequest($"Could not create directory on local disk: {ioEx.Message}");
                        }
                    }

                    // Check for duplicates
                    string checkQuery = "SELECT COUNT(*) FROM ScanFolders WHERE FolderPath = @FolderPath AND MediaType = @MediaType";
                    int count = Convert.ToInt32(await db.ExecuteScalarAsync(checkQuery, new Dictionary<string, object?> {
                        { "@FolderPath", request.FolderPath },
                        { "@MediaType", cleanType }
                    }) ?? 0);

                    if (count > 0)
                    {
                        return Results.Conflict("Directory path is already configured for this media type.");
                    }

                    int newFolderId = await db.GetNextIdAsync("ScanFolders");

                    // Insert
                    string insertQuery = "INSERT INTO ScanFolders (Id, FolderPath, MediaType) VALUES (@Id, @FolderPath, @MediaType)";
                    await db.ExecuteNonQueryAsync(insertQuery, new Dictionary<string, object?> {
                        { "@Id", newFolderId },
                        { "@FolderPath", request.FolderPath },
                        { "@MediaType", cleanType }
                    });

                    return Results.Created($"/api/settings/folders", new { message = "Scan folder added successfully." });
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Error adding scan folder");
                    return Results.Problem("Database update failed.");
                }
            });

            // 10. DELETE /api/settings/folders/{id} - Remove a scan folder
            app.MapDelete("/api/settings/folders/{id}", async (int id, GlacierDbService db) =>
            {
                try
                {
                    string query = "DELETE FROM ScanFolders WHERE Id = @Id";
                    int rows = await db.ExecuteNonQueryAsync(query, new Dictionary<string, object?> { { "@Id", id } });
                    if (rows > 0)
                    {
                        return Results.NoContent();
                    }
                    return Results.NotFound($"Scan folder with ID {id} not found.");
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error deleting scan folder {id}");
                    return Results.Problem("Database delete failed.");
                }
            });

            // GET /api/settings - Get all system settings
            app.MapGet("/api/settings", async (GlacierDbService db) =>
            {
                try
                {
                    var rows = await db.ExecuteQueryAsync("SELECT SettingKey, SettingValue FROM SystemSettings");
                    var settingsDict = rows.ToDictionary(
                        r => r["SettingKey"]?.ToString() ?? "",
                        r => r["SettingValue"]?.ToString() ?? ""
                    );
                    return Results.Ok(settingsDict);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Error getting system settings");
                    return Results.Problem("Database query failed.");
                }
            });

            // POST /api/settings - Save system settings
            app.MapPost("/api/settings", async ([FromBody] Dictionary<string, string> request, GlacierDbService db) =>
            {
                if (request == null) return Results.BadRequest("Invalid settings data.");

                try
                {
                    foreach (var kvp in request)
                    {
                        // Clean values and save
                        string query = "UPDATE SystemSettings SET SettingValue = @Value WHERE SettingKey = @Key";
                        int updated = await db.ExecuteNonQueryAsync(query, new Dictionary<string, object?>
                        {
                            { "@Value", kvp.Value },
                            { "@Key", kvp.Key }
                        });
                        
                        // Fallback if row doesn't exist
                        if (updated == 0)
                        {
                            await db.ExecuteNonQueryAsync(
                                "INSERT INTO SystemSettings (SettingKey, SettingValue) VALUES (@Key, @Value)",
                                new Dictionary<string, object?> { { "@Key", kvp.Key }, { "@Value", kvp.Value } }
                            );
                        }
                    }
                    return Results.Ok(new { message = "Settings saved successfully." });
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Error saving system settings");
                    return Results.Problem("Database write failed.");
                }
            });

            // 11. GET /api/settings/status - Check background scanner status
            app.MapGet("/api/settings/status", (DirectoryScannerService scanner) =>
            {
                return Results.Ok(new { isScanning = scanner.IsScanning });
            });

            // 13. GET /api/settings/browse - Traverse server directory structures
            app.MapGet("/api/settings/browse", (string? path) =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        var drives = DriveInfo.GetDrives();
                        var driveDirs = new List<object>();
                        foreach (var drive in drives)
                        {
                            if (drive.IsReady)
                            {
                                driveDirs.Add(new { name = drive.Name, path = drive.Name });
                            }
                        }
                        return Results.Ok(new { currentPath = "", parentPath = "", directories = driveDirs });
                    }

                    if (!Directory.Exists(path))
                    {
                        return Results.NotFound("Target folder path does not exist on server.");
                    }

                    var parentInfo = Directory.GetParent(path);
                    string parentPath = parentInfo?.FullName ?? "";

                    var subdirs = new List<object>();
                    try
                    {
                        var directories = Directory.GetDirectories(path);
                        foreach (var dir in directories)
                        {
                            var dirInfo = new DirectoryInfo(dir);
                            if ((dirInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden &&
                                (dirInfo.Attributes & FileAttributes.System) != FileAttributes.System)
                            {
                                subdirs.Add(new { name = dirInfo.Name, path = dirInfo.FullName });
                            }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Ignore folders where access is denied
                    }

                    return Results.Ok(new { currentPath = path, parentPath = parentPath, directories = subdirs });
                }
                catch (Exception ex)
                {
                    return Results.Problem($"Failed to browse directory: {ex.Message}");
                }
            });

            // 12. POST /api/debug/log - Receive logs from Web UI client
            app.MapPost("/api/debug/log", ([FromBody] DebugLogRequest request, ILogger<Program> logger) =>
            {
                logger.LogInformation($"[CLIENT_LOG] {request.Level}: {request.Message}");
                return Results.Ok();
            });

            // GET /api/media/{id}/collections - Get collections linked to a media item
            app.MapGet("/api/media/{id}/collections", async (int id, GlacierDbService db) =>
            {
                var collections = new List<int>();
                try
                {
                    string query = "SELECT CollectionId FROM CollectionMediaItems WHERE MediaItemId = @MediaItemId";
                    var rows = await db.ExecuteQueryAsync(query, new Dictionary<string, object?> { { "@MediaItemId", id } });
                    foreach (var row in rows)
                    {
                        collections.Add(Convert.ToInt32(row["CollectionId"]));
                    }
                    return Results.Ok(collections);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error getting collection IDs for movie id={id}");
                    return Results.Problem("Database query failed.");
                }
            });

            // ==================== GENRES & COLLECTIONS ENDPOINTS ====================

            // GET /api/genres - Get unique list of genres
            app.MapGet("/api/genres", async (string? mediaType, GlacierDbService db) =>
            {
                var genres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string query;
                    if (mediaType == "Movie")
                    {
                        query = "SELECT Genres FROM MediaItems WHERE MediaType = 'Movie' AND Genres IS NOT NULL";
                    }
                    else
                    {
                        query = "SELECT Genres FROM TvShows WHERE Genres IS NOT NULL";
                    }

                    var rows = await db.ExecuteQueryAsync(query);
                    foreach (var row in rows)
                    {
                        string? genresStr = row["Genres"]?.ToString();
                        if (!string.IsNullOrEmpty(genresStr))
                        {
                            var split = genresStr.Split(',');
                            foreach (var g in split)
                            {
                                string trimmed = g.Trim();
                                if (!string.IsNullOrEmpty(trimmed))
                                {
                                    genres.Add(trimmed);
                                }
                            }
                        }
                    }
                    var sorted = new List<string>(genres);
                    sorted.Sort();
                    return Results.Ok(sorted);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Error getting genres");
                    return Results.Problem("Database query failed.");
                }
            });

            // GET /api/collections - Get all collections
            app.MapGet("/api/collections", async (GlacierDbService db) =>
            {
                var list = new List<CollectionItem>();
                try
                {
                    string query = @"
                        SELECT c.Id, c.Name, c.Overview, c.PosterPath
                        FROM Collections c
                        ORDER BY c.Name ASC";
                    
                    var rows = await db.ExecuteQueryAsync(query);
                    foreach (var row in rows)
                    {
                        int collId = Convert.ToInt32(row["Id"]);
                        // Query the item count manually since Glacier.Sql might have group limitations
                        int count = Convert.ToInt32(await db.ExecuteScalarAsync(
                            "SELECT COUNT(*) FROM CollectionMediaItems WHERE CollectionId = @CollectionId",
                            new Dictionary<string, object?> { { "@CollectionId", collId } }
                        ) ?? 0);

                        list.Add(new CollectionItem(
                            collId,
                            row["Name"]?.ToString() ?? "",
                            row["Overview"] == null || row["Overview"] is DBNull ? null : row["Overview"]?.ToString(),
                            row["PosterPath"] == null || row["PosterPath"] is DBNull ? null : row["PosterPath"]?.ToString(),
                            count
                        ));
                    }
                    return Results.Ok(list);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Error getting collections");
                    return Results.Problem("Database query failed.");
                }
            });

            // GET /api/collections/{id} - Get collection details & items
            app.MapGet("/api/collections/{id}", async (int id, GlacierDbService db) =>
            {
                try
                {
                    // Get collection info
                    string getCollQuery = "SELECT Id, Name, Overview, PosterPath FROM Collections WHERE Id = @Id";
                    string name = "";
                    string? overview = null;
                    string? posterPath = null;

                    var rows = await db.ExecuteQueryAsync(getCollQuery, new Dictionary<string, object?> { { "@Id", id } });
                    if (rows.Count > 0)
                    {
                        var row = rows[0];
                        name = row["Name"]?.ToString() ?? "";
                        overview = row["Overview"] == null || row["Overview"] is DBNull ? null : row["Overview"]?.ToString();
                        posterPath = row["PosterPath"] == null || row["PosterPath"] is DBNull ? null : row["PosterPath"]?.ToString();
                    }
                    else
                    {
                        return Results.NotFound();
                    }

                    // Get movies inside collection
                    var movies = new List<MediaItem>();
                    string getMoviesQuery = @"
                        SELECT m.Id, m.Title, m.FilePath, m.MediaType, m.ReleaseYear, m.DurationInSeconds, m.ResumePositionInSeconds, m.DateAdded,
                               m.PosterPath, m.Overview, m.Genres, m.Director, m.CastJson, m.TvShowId, m.SeasonNumber, m.EpisodeNumber, m.Watched, m.LastWatched
                        FROM MediaItems m
                        INNER JOIN CollectionMediaItems cm ON m.Id = cm.MediaItemId
                        WHERE cm.CollectionId = @CollectionId
                        ORDER BY m.Title ASC";
                    
                    var movieRows = await db.ExecuteQueryAsync(getMoviesQuery, new Dictionary<string, object?> { { "@CollectionId", id } });
                    foreach (var row in movieRows)
                    {
                        movies.Add(new MediaItem(
                            Convert.ToInt32(row["Id"]),
                            row["Title"]?.ToString() ?? "",
                            row["FilePath"]?.ToString() ?? "",
                            row["MediaType"]?.ToString() ?? "",
                            row["ReleaseYear"] == null || row["ReleaseYear"] is DBNull ? null : Convert.ToInt32(row["ReleaseYear"]),
                            Convert.ToInt32(row["DurationInSeconds"]),
                            Convert.ToInt32(row["ResumePositionInSeconds"]),
                            Convert.ToDateTime(row["DateAdded"]),
                            row["PosterPath"] == null || row["PosterPath"] is DBNull ? null : row["PosterPath"]?.ToString(),
                            row["Overview"] == null || row["Overview"] is DBNull ? null : row["Overview"]?.ToString(),
                            row["Genres"] == null || row["Genres"] is DBNull ? null : row["Genres"]?.ToString(),
                            row["Director"] == null || row["Director"] is DBNull ? null : row["Director"]?.ToString(),
                            row["CastJson"] == null || row["CastJson"] is DBNull ? null : row["CastJson"]?.ToString(),
                            row["TvShowId"] == null || row["TvShowId"] is DBNull ? null : Convert.ToInt32(row["TvShowId"]),
                            row["SeasonNumber"] == null || row["SeasonNumber"] is DBNull ? null : Convert.ToInt32(row["SeasonNumber"]),
                            row["EpisodeNumber"] == null || row["EpisodeNumber"] is DBNull ? null : Convert.ToInt32(row["EpisodeNumber"]),
                            row["Watched"] == null || row["Watched"] is DBNull ? 0 : Convert.ToInt32(row["Watched"]),
                            row["LastWatched"] == null || row["LastWatched"] is DBNull ? null : (DateTime?)Convert.ToDateTime(row["LastWatched"])
                        ));
                    }

                    return Results.Ok(new CollectionDetails(id, name, overview, posterPath, movies));
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error getting collection details for id={id}");
                    return Results.Problem("Database query failed.");
                }
            });

            // POST /api/collections - Create collection
            app.MapPost("/api/collections", async ([FromBody] CreateCollectionRequest request, GlacierDbService db) =>
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest("Collection Name cannot be empty.");
                }

                try
                {
                    // Check for duplicates
                    int exists = Convert.ToInt32(await db.ExecuteScalarAsync(
                        "SELECT COUNT(*) FROM Collections WHERE Name = @Name",
                        new Dictionary<string, object?> { { "@Name", request.Name.Trim() } }
                    ) ?? 0);

                    if (exists > 0)
                    {
                        return Results.Conflict("A collection with this name already exists.");
                    }

                    int newId = await db.GetNextIdAsync("Collections");
                    string query = @"
                        INSERT INTO Collections (Id, Name, Overview, PosterPath, DateCreated) 
                        VALUES (@Id, @Name, @Overview, @PosterPath, @DateCreated)";

                    await db.ExecuteNonQueryAsync(query, new Dictionary<string, object?>
                    {
                        { "@Id", newId },
                        { "@Name", request.Name.Trim() },
                        { "@Overview", string.IsNullOrWhiteSpace(request.Overview) ? DBNull.Value : request.Overview },
                        { "@PosterPath", string.IsNullOrWhiteSpace(request.PosterPath) ? DBNull.Value : request.PosterPath },
                        { "@DateCreated", DateTime.Now }
                    });

                    return Results.Created($"/api/collections/{newId}", new { id = newId });
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Error creating collection");
                    return Results.Problem("Database insert failed.");
                }
            });

            // DELETE /api/collections/{id} - Delete collection
            app.MapDelete("/api/collections/{id}", async (int id, GlacierDbService db) =>
            {
                try
                {
                    string query = "DELETE FROM Collections WHERE Id = @Id";
                    int rows = await db.ExecuteNonQueryAsync(query, new Dictionary<string, object?> { { "@Id", id } });
                    return rows > 0 ? Results.Ok() : Results.NotFound();
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error deleting collection id={id}");
                    return Results.Problem("Database operation failed.");
                }
            });

            // POST /api/collections/{id}/items - Add movie to collection
            app.MapPost("/api/collections/{id}/items", async (int id, [FromBody] AddCollectionItemRequest request, GlacierDbService db) =>
            {
                try
                {
                    // Check if collection exists
                    int collExists = Convert.ToInt32(await db.ExecuteScalarAsync(
                        "SELECT COUNT(*) FROM Collections WHERE Id = @Id",
                        new Dictionary<string, object?> { { "@Id", id } }
                    ) ?? 0);
                    if (collExists == 0) return Results.NotFound("Collection not found");

                    // Check if media item exists
                    int mediaExists = Convert.ToInt32(await db.ExecuteScalarAsync(
                        "SELECT COUNT(*) FROM MediaItems WHERE Id = @Id AND MediaType = 'Movie'",
                        new Dictionary<string, object?> { { "@Id", request.MediaItemId } }
                    ) ?? 0);
                    if (mediaExists == 0) return Results.NotFound("Movie not found");

                    // Check duplicate link
                    int linkExists = Convert.ToInt32(await db.ExecuteScalarAsync(
                        "SELECT COUNT(*) FROM CollectionMediaItems WHERE CollectionId = @CollectionId AND MediaItemId = @MediaItemId",
                        new Dictionary<string, object?>
                        {
                            { "@CollectionId", id },
                            { "@MediaItemId", request.MediaItemId }
                        }
                    ) ?? 0);

                    if (linkExists == 0)
                    {
                        string query = "INSERT INTO CollectionMediaItems (CollectionId, MediaItemId) VALUES (@CollectionId, @MediaItemId)";
                        await db.ExecuteNonQueryAsync(query, new Dictionary<string, object?>
                        {
                            { "@CollectionId", id },
                            { "@MediaItemId", request.MediaItemId }
                        });
                    }
                    
                    // Set collection poster path if collection currently does not have one
                    string updateCollPosterQuery = @"
                        UPDATE Collections 
                        SET PosterPath = (SELECT PosterPath FROM MediaItems WHERE Id = @MediaItemId)
                        WHERE Id = @CollectionId AND PosterPath IS NULL";
                    await db.ExecuteNonQueryAsync(updateCollPosterQuery, new Dictionary<string, object?>
                    {
                        { "@CollectionId", id },
                        { "@MediaItemId", request.MediaItemId }
                    });

                    return Results.Ok();
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error adding item to collection id={id}");
                    return Results.Problem("Database operation failed.");
                }
            });

            // DELETE /api/collections/{id}/items/{mediaItemId} - Remove movie from collection
            app.MapDelete("/api/collections/{id}/items/{mediaItemId}", async (int id, int mediaItemId, GlacierDbService db) =>
            {
                try
                {
                    string query = "DELETE FROM CollectionMediaItems WHERE CollectionId = @CollectionId AND MediaItemId = @MediaItemId";
                    int rows = await db.ExecuteNonQueryAsync(query, new Dictionary<string, object?>
                    {
                        { "@CollectionId", id },
                        { "@MediaItemId", mediaItemId }
                    });
                    return rows > 0 ? Results.Ok() : Results.NotFound();
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error removing item from collection id={id}");
                    return Results.Problem("Database operation failed.");
                }
            });

            // ==================== NEW ADVANCED MEDIA API ENDPOINTS ====================

            // 21. GET /api/media/{id}/chapters - Get skip chapters (intros / credits)
            app.MapGet("/api/media/{id}/chapters", async (int id, GlacierDbService db) =>
            {
                try
                {
                    var list = new List<object>();
                    var rows = await db.ExecuteQueryAsync(
                        "SELECT Id, Title, StartTime, EndTime FROM MediaChapters WHERE MediaItemId = @MediaItemId ORDER BY StartTime ASC",
                        new Dictionary<string, object?> { { "@MediaItemId", id } }
                    );

                    foreach (var row in rows)
                    {
                        list.Add(new
                        {
                            Id = Convert.ToInt32(row["Id"]),
                            Title = row["Title"]?.ToString() ?? "",
                            StartTime = Convert.ToDouble(row["StartTime"]),
                            EndTime = Convert.ToDouble(row["EndTime"])
                        });
                    }
                    return Results.Ok(list);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error getting chapters for media id={id}");
                    return Results.Problem("Database query failed.");
                }
            });

            // 22. PUT /api/media/{id} - Update details (Metadata Editor)
            app.MapPut("/api/media/{id}", async (int id, [FromBody] MediaEditRequest request, GlacierDbService db) =>
            {
                if (string.IsNullOrWhiteSpace(request.Title))
                {
                    return Results.BadRequest("Title cannot be empty.");
                }

                try
                {
                    int rows = await db.UpdateRowAsync("MediaItems", id, new Dictionary<string, object?>
                    {
                        { "Title", request.Title },
                        { "Overview", string.IsNullOrWhiteSpace(request.Overview) ? DBNull.Value : request.Overview },
                        { "ReleaseYear", request.ReleaseYear ?? (object)DBNull.Value },
                        { "Genres", string.IsNullOrWhiteSpace(request.Genres) ? DBNull.Value : request.Genres },
                        { "Director", string.IsNullOrWhiteSpace(request.Director) ? DBNull.Value : request.Director },
                        { "PosterPath", string.IsNullOrWhiteSpace(request.PosterPath) ? DBNull.Value : request.PosterPath }
                    });

                    return rows > 0 ? Results.Ok() : Results.NotFound();
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error updating metadata for media item {id}");
                    return Results.Problem("Database update failed.");
                }
            });

            // 23. PUT /api/tvshows/{id} - Update TV Show details
            app.MapPut("/api/tvshows/{id}", async (int id, [FromBody] MediaEditRequest request, GlacierDbService db) =>
            {
                if (string.IsNullOrWhiteSpace(request.Title))
                {
                    return Results.BadRequest("Title cannot be empty.");
                }

                try
                {
                    int rows = await db.UpdateRowAsync("TvShows", id, new Dictionary<string, object?>
                    {
                        { "Title", request.Title },
                        { "Overview", string.IsNullOrWhiteSpace(request.Overview) ? DBNull.Value : request.Overview },
                        { "ReleaseYear", request.ReleaseYear ?? (object)DBNull.Value },
                        { "Genres", string.IsNullOrWhiteSpace(request.Genres) ? DBNull.Value : request.Genres },
                        { "PosterPath", string.IsNullOrWhiteSpace(request.PosterPath) ? DBNull.Value : request.PosterPath }
                    });

                    return rows > 0 ? Results.Ok() : Results.NotFound();
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error updating metadata for TV show {id}");
                    return Results.Problem("Database update failed.");
                }
            });

            // 24. POST /api/media/{id}/watched - Mark movie or episode as watched
            app.MapPost("/api/media/{id}/watched", async (int id, GlacierDbService db) =>
            {
                try
                {
                    int rows = await db.UpdateRowAsync("MediaItems", id, new Dictionary<string, object?>
                    {
                        { "Watched", 1 },
                        { "ResumePositionInSeconds", 0 },
                        { "LastWatched", DateTime.Now }
                    });
                    return rows > 0 ? Results.Ok() : Results.NotFound();
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error marking media {id} as watched");
                    return Results.Problem("Database write failed.");
                }
            });

            // 25. POST /api/media/{id}/unwatched - Mark movie or episode as unwatched
            app.MapPost("/api/media/{id}/unwatched", async (int id, GlacierDbService db) =>
            {
                try
                {
                    int rows = await db.UpdateRowAsync("MediaItems", id, new Dictionary<string, object?>
                    {
                        { "Watched", 0 },
                        { "ResumePositionInSeconds", 0 },
                        { "LastWatched", DBNull.Value }
                    });
                    return rows > 0 ? Results.Ok() : Results.NotFound();
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error marking media {id} as unwatched");
                    return Results.Problem("Database write failed.");
                }
            });

            // 26. POST /api/tvshows/{id}/watched - Mark TV Show as watched (all episodes)
            app.MapPost("/api/tvshows/{id}/watched", async (int id, GlacierDbService db) =>
            {
                try
                {
                    var epRows = await db.ExecuteQueryAsync("SELECT Id FROM MediaItems WHERE TvShowId = @ShowId AND MediaType = 'Episode'",
                        new Dictionary<string, object?> { { "@ShowId", id } });
                    
                    foreach (var row in epRows)
                    {
                        int epId = Convert.ToInt32(row["Id"]);
                        await db.UpdateRowAsync("MediaItems", epId, new Dictionary<string, object?>
                        {
                            { "Watched", 1 },
                            { "ResumePositionInSeconds", 0 },
                            { "LastWatched", DateTime.Now }
                        });
                    }
                    return Results.Ok();
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error marking TV show {id} as watched");
                    return Results.Problem("Database write failed.");
                }
            });

            // 27. POST /api/tvshows/{id}/unwatched - Mark TV Show as unwatched (all episodes)
            app.MapPost("/api/tvshows/{id}/unwatched", async (int id, GlacierDbService db) =>
            {
                try
                {
                    var epRows = await db.ExecuteQueryAsync("SELECT Id FROM MediaItems WHERE TvShowId = @ShowId AND MediaType = 'Episode'",
                        new Dictionary<string, object?> { { "@ShowId", id } });
                    
                    foreach (var row in epRows)
                    {
                        int epId = Convert.ToInt32(row["Id"]);
                        await db.UpdateRowAsync("MediaItems", epId, new Dictionary<string, object?>
                        {
                            { "Watched", 0 },
                            { "ResumePositionInSeconds", 0 },
                            { "LastWatched", DBNull.Value }
                        });
                    }
                    return Results.Ok();
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error marking TV show {id} as unwatched");
                    return Results.Problem("Database write failed.");
                }
            });

            // 28. GET /api/media/{id}/specs - Get technical specs
            app.MapGet("/api/media/{id}/specs", async (int id, GlacierDbService db) =>
            {
                try
                {
                    var rows = await db.ExecuteQueryAsync("SELECT * FROM MediaSpecs WHERE MediaItemId = @Id",
                        new Dictionary<string, object?> { { "@Id", id } });
                    
                    if (rows.Count == 0) return Results.NotFound();
                    var row = rows[0];

                    return Results.Ok(new MediaSpecsDto(
                        Convert.ToInt32(row["MediaItemId"]),
                        row["Container"]?.ToString(),
                        row["VideoCodec"]?.ToString(),
                        row["VideoResolution"]?.ToString(),
                        row["VideoBitrate"] == null || row["VideoBitrate"] is DBNull ? 0 : Convert.ToInt32(row["VideoBitrate"]),
                        row["VideoFrameRate"]?.ToString(),
                        row["AudioCodec"]?.ToString(),
                        row["AudioChannels"] == null || row["AudioChannels"] is DBNull ? 0 : Convert.ToInt32(row["AudioChannels"]),
                        row["FileSize"] == null || row["FileSize"] is DBNull ? 0L : Convert.ToInt64(row["FileSize"])
                    ));
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error fetching specs for media {id}");
                    return Results.Problem("Database query failed.");
                }
            });

            // 29. GET /api/media/{id}/subtitles - Get list of subtitle tracks
            app.MapGet("/api/media/{id}/subtitles", async (int id, GlacierDbService db) =>
            {
                try
                {
                    var rows = await db.ExecuteQueryAsync("SELECT * FROM MediaSubtitles WHERE MediaItemId = @Id",
                        new Dictionary<string, object?> { { "@Id", id } });
                    
                    var list = new List<MediaSubtitleDto>();
                    foreach (var row in rows)
                    {
                        list.Add(new MediaSubtitleDto(
                            Convert.ToInt32(row["Id"]),
                            Convert.ToInt32(row["MediaItemId"]),
                            row["SubtitleType"]?.ToString() ?? "",
                            row["Language"]?.ToString() ?? "",
                            row["Title"]?.ToString() ?? "",
                            row["Format"]?.ToString() ?? "",
                            row["StreamIndex"] == null || row["StreamIndex"] is DBNull ? null : Convert.ToInt32(row["StreamIndex"])
                        ));
                    }
                    return Results.Ok(list);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error fetching subtitles list for media {id}");
                    return Results.Problem("Database query failed.");
                }
            });

            // 30. GET /api/media/{id}/subtitles/{subId}/stream - Stream subtitle track converted to WebVTT
            app.MapGet("/api/media/{id}/subtitles/{subId}/stream", async (int id, int subId, GlacierDbService db, HttpContext context) =>
            {
                try
                {
                    var subRows = await db.ExecuteQueryAsync("SELECT * FROM MediaSubtitles WHERE Id = @SubId",
                        new Dictionary<string, object?> { { "@SubId", subId } });
                    
                    if (subRows.Count == 0) return Results.NotFound();
                    var sub = subRows[0];

                    var mediaRows = await db.ExecuteQueryAsync("SELECT FilePath FROM MediaItems WHERE Id = @Id",
                        new Dictionary<string, object?> { { "@Id", id } });
                    
                    if (mediaRows.Count == 0) return Results.NotFound();
                    string filePath = mediaRows[0]["FilePath"]?.ToString() ?? "";

                    string subType = sub["SubtitleType"]?.ToString() ?? "";
                    if (subType == "External")
                    {
                        string extPath = sub["FilePath"]?.ToString() ?? "";
                        if (!File.Exists(extPath)) return Results.NotFound();

                        string rawContent = await File.ReadAllTextAsync(extPath);
                        string vttContent = ConvertSrtToVtt(rawContent);

                        context.Response.ContentType = "text/vtt; charset=utf-8";
                        await context.Response.WriteAsync(vttContent);
                        return Results.Empty;
                    }
                    else // Internal
                    {
                        int streamIndex = Convert.ToInt32(sub["StreamIndex"]);
                        
                        var startInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            Arguments = $"-i \"{filePath}\" -map 0:s:{streamIndex} -f webvtt -",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        var process = new System.Diagnostics.Process { StartInfo = startInfo };
                        process.Start();

                        context.Response.ContentType = "text/vtt; charset=utf-8";
                        await process.StandardOutput.BaseStream.CopyToAsync(context.Response.Body);
                        await process.WaitForExitAsync();
                        return Results.Empty;
                    }
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error streaming subtitle ID {subId} for media {id}");
                    return Results.Problem("Subtitle extraction failed.");
                }
            });

            // 31. GET /api/playlists - List all playlists
            app.MapGet("/api/playlists", async (GlacierDbService db) =>
            {
                try
                {
                    var rows = await db.ExecuteQueryAsync("SELECT * FROM Playlists ORDER BY Name ASC");
                    var list = new List<PlaylistDto>();
                    foreach (var row in rows)
                    {
                        int playlistId = Convert.ToInt32(row["Id"]);
                        var countRow = await db.ExecuteScalarAsync("SELECT COUNT(*) FROM PlaylistItems WHERE PlaylistId = @PlaylistId",
                            new Dictionary<string, object?> { { "@PlaylistId", playlistId } });
                        int itemCount = Convert.ToInt32(countRow ?? 0);

                        list.Add(new PlaylistDto(
                            playlistId,
                            row["Name"]?.ToString() ?? "",
                            row["Description"]?.ToString(),
                            Convert.ToDateTime(row["DateCreated"]),
                            Convert.ToDateTime(row["DateModified"]),
                            itemCount
                        ));
                    }
                    return Results.Ok(list);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Error listing playlists");
                    return Results.Problem("Database query failed.");
                }
            });

            // 32. POST /api/playlists - Create new playlist
            app.MapPost("/api/playlists", async ([FromBody] CreatePlaylistRequest request, GlacierDbService db) =>
            {
                if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest("Name cannot be empty.");
                try
                {
                    int playlistId = await db.GetNextIdAsync("Playlists");
                    string insertQuery = @"
                        INSERT INTO Playlists (Id, Name, Description, DateCreated, DateModified)
                        VALUES (@Id, @Name, @Description, @Now, @Now)";
                    
                    await db.ExecuteNonQueryAsync(insertQuery, new Dictionary<string, object?>
                    {
                        { "@Id", playlistId },
                        { "@Name", request.Name.Trim() },
                        { "@Description", string.IsNullOrWhiteSpace(request.Description) ? DBNull.Value : request.Description.Trim() },
                        { "@Now", DateTime.Now }
                    });

                    return Results.Ok(playlistId);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Error creating playlist");
                    return Results.Problem("Database write failed.");
                }
            });

            // 33. DELETE /api/playlists/{id} - Delete a playlist
            app.MapDelete("/api/playlists/{id}", async (int id, GlacierDbService db) =>
            {
                try
                {
                    await db.ExecuteNonQueryAsync("DELETE FROM PlaylistItems WHERE PlaylistId = @Id", new Dictionary<string, object?> { { "@Id", id } });
                    int rows = await db.ExecuteNonQueryAsync("DELETE FROM Playlists WHERE Id = @Id", new Dictionary<string, object?> { { "@Id", id } });
                    return rows > 0 ? Results.Ok() : Results.NotFound();
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error deleting playlist {id}");
                    return Results.Problem("Database delete failed.");
                }
            });

            // 34. GET /api/playlists/{id}/items - Get ordered media items in playlist
            app.MapGet("/api/playlists/{id}/items", async (int id, GlacierDbService db) =>
            {
                try
                {
                    string query = @"
                        SELECT m.Id, m.Title, m.FilePath, m.MediaType, m.ReleaseYear, m.DurationInSeconds, m.ResumePositionInSeconds, m.DateAdded,
                               m.PosterPath, m.Overview, m.Genres, m.Director, m.CastJson, m.TvShowId, m.SeasonNumber, m.EpisodeNumber, m.Watched, m.LastWatched
                        FROM MediaItems m
                        INNER JOIN PlaylistItems pi ON m.Id = pi.MediaItemId
                        WHERE pi.PlaylistId = @PlaylistId
                        ORDER BY pi.SortOrder ASC";

                    var rows = await db.ExecuteQueryAsync(query, new Dictionary<string, object?> { { "@PlaylistId", id } });
                    var list = new List<MediaItem>();
                    foreach (var row in rows)
                    {
                        list.Add(new MediaItem(
                            Convert.ToInt32(row["Id"]),
                            row["Title"]?.ToString() ?? "",
                            row["FilePath"]?.ToString() ?? "",
                            row["MediaType"]?.ToString() ?? "",
                            row["ReleaseYear"] == null || row["ReleaseYear"] is DBNull ? null : Convert.ToInt32(row["ReleaseYear"]),
                            Convert.ToInt32(row["DurationInSeconds"]),
                            Convert.ToInt32(row["ResumePositionInSeconds"]),
                            Convert.ToDateTime(row["DateAdded"]),
                            row["PosterPath"] == null || row["PosterPath"] is DBNull ? null : row["PosterPath"]?.ToString(),
                            row["Overview"] == null || row["Overview"] is DBNull ? null : row["Overview"]?.ToString(),
                            row["Genres"] == null || row["Genres"] is DBNull ? null : row["Genres"]?.ToString(),
                            row["Director"] == null || row["Director"] is DBNull ? null : row["Director"]?.ToString(),
                            row["CastJson"] == null || row["CastJson"] is DBNull ? null : row["CastJson"]?.ToString(),
                            row["TvShowId"] == null || row["TvShowId"] is DBNull ? null : Convert.ToInt32(row["TvShowId"]),
                            row["SeasonNumber"] == null || row["SeasonNumber"] is DBNull ? null : Convert.ToInt32(row["SeasonNumber"]),
                            row["EpisodeNumber"] == null || row["EpisodeNumber"] is DBNull ? null : Convert.ToInt32(row["EpisodeNumber"]),
                            row["Watched"] == null || row["Watched"] is DBNull ? 0 : Convert.ToInt32(row["Watched"]),
                            row["LastWatched"] == null || row["LastWatched"] is DBNull ? null : (DateTime?)Convert.ToDateTime(row["LastWatched"])
                        ));
                    }
                    return Results.Ok(list);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error listing playlist items for {id}");
                    return Results.Problem("Database query failed.");
                }
            });

            // 35. POST /api/playlists/{id}/items - Add item to playlist
            app.MapPost("/api/playlists/{id}/items", async (int id, [FromBody] AddPlaylistItemRequest request, GlacierDbService db) =>
            {
                try
                {
                    var maxRow = await db.ExecuteScalarAsync("SELECT MAX(SortOrder) FROM PlaylistItems WHERE PlaylistId = @PlaylistId",
                        new Dictionary<string, object?> { { "@PlaylistId", id } });
                    int nextSortOrder = maxRow == null || maxRow is DBNull ? 0 : Convert.ToInt32(maxRow) + 1;

                    int itemId = await db.GetNextIdAsync("PlaylistItems");
                    string insertQuery = @"
                        INSERT INTO PlaylistItems (Id, PlaylistId, MediaItemId, SortOrder)
                        VALUES (@Id, @PlaylistId, @MediaItemId, @SortOrder)";
                    
                    await db.ExecuteNonQueryAsync(insertQuery, new Dictionary<string, object?>
                    {
                        { "@Id", itemId },
                        { "@PlaylistId", id },
                        { "@MediaItemId", request.MediaItemId },
                        { "@SortOrder", nextSortOrder }
                    });

                    // Update DateModified on Playlist
                    await db.UpdateRowAsync("Playlists", id, new Dictionary<string, object?> { { "DateModified", DateTime.Now } });

                    return Results.Ok();
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error adding item to playlist {id}");
                    return Results.Problem("Database write failed.");
                }
            });

            // 36. DELETE /api/playlists/{id}/items/{itemId} - Remove item from playlist
            app.MapDelete("/api/playlists/{id}/items/{itemId}", async (int id, int itemId, GlacierDbService db) =>
            {
                try
                {
                    int rows = await db.ExecuteNonQueryAsync("DELETE FROM PlaylistItems WHERE PlaylistId = @PlaylistId AND MediaItemId = @MediaItemId",
                        new Dictionary<string, object?> { { "@PlaylistId", id }, { "@MediaItemId", itemId } });
                    
                    if (rows > 0)
                    {
                        await db.UpdateRowAsync("Playlists", id, new Dictionary<string, object?> { { "DateModified", DateTime.Now } });
                    }
                    return rows > 0 ? Results.Ok() : Results.NotFound();
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error deleting item {itemId} from playlist {id}");
                    return Results.Problem("Database delete failed.");
                }
            });

            // 37. PUT /api/playlists/{id}/items/reorder - Reorder playlist items
            app.MapPut("/api/playlists/{id}/items/reorder", async (int id, [FromBody] ReorderPlaylistRequest request, GlacierDbService db) =>
            {
                try
                {
                    for (int i = 0; i < request.ItemIds.Count; i++)
                    {
                        int mediaItemId = request.ItemIds[i];
                        
                        // Find matching row ID to bypass integer update limitation
                        var rows = await db.ExecuteQueryAsync("SELECT Id FROM PlaylistItems WHERE PlaylistId = @PlaylistId AND MediaItemId = @MediaItemId",
                            new Dictionary<string, object?> { { "@PlaylistId", id }, { "@MediaItemId", mediaItemId } });
                        
                        if (rows.Count > 0)
                        {
                            int rowId = Convert.ToInt32(rows[0]["Id"]);
                            await db.UpdateRowAsync("PlaylistItems", rowId, new Dictionary<string, object?> { { "SortOrder", i } });
                        }
                    }

                    await db.UpdateRowAsync("Playlists", id, new Dictionary<string, object?> { { "DateModified", DateTime.Now } });
                    return Results.Ok();
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, $"Error reordering playlist {id}");
                    return Results.Problem("Reordering failed.");
                }
            });

            await app.RunAsync();
        }

        private static string ConvertSrtToVtt(string srtContent)
        {
            string vtt = "WEBVTT\n\n" + srtContent;
            // Replace commas with periods in timestamps
            vtt = System.Text.RegularExpressions.Regex.Replace(vtt, @"(\d{2}:\d{2}:\d{2}),(\d{3})", "$1.$2");
            return vtt;
        }
    }

    // DTO records
    public record MediaItem(
        int Id,
        string Title,
        string FilePath,
        string MediaType,
        int? ReleaseYear,
        int DurationInSeconds,
        int ResumePositionInSeconds,
        DateTime DateAdded,
        string? PosterPath,
        string? Overview,
        string? Genres,
        string? Director,
        string? CastJson,
        int? TvShowId,
        int? SeasonNumber,
        int? EpisodeNumber,
        int? Watched,
        DateTime? LastWatched
    );

    public record ContinueWatchingItem(
        int Id,
        string Title,
        string FilePath,
        string MediaType,
        int? ReleaseYear,
        int DurationInSeconds,
        int ResumePositionInSeconds,
        DateTime DateAdded,
        string? PosterPath,
        string? Overview,
        string? Genres,
        string? Director,
        string? CastJson,
        int? TvShowId,
        int? SeasonNumber,
        int? EpisodeNumber,
        string? TvShowTitle,
        DateTime? LastWatched
    );

    public record TvShowItem(
        int Id,
        string Title,
        string? Overview,
        string? PosterPath,
        string? Genres,
        int? ReleaseYear,
        string? CastJson,
        DateTime DateAdded,
        int EpisodeCount,
        int SeasonCount,
        DateTime? LastWatched,
        int UnwatchedEpisodeCount
    );

    public record ResumeRequest(int Position);
    public record DurationRequest(int Duration);
    public record FolderRequest(string FolderPath, string MediaType);
    public record DebugLogRequest(string Level, string Message);

    public record CollectionItem(int Id, string Name, string? Overview, string? PosterPath, int MovieCount);
    public record CollectionDetails(int Id, string Name, string? Overview, string? PosterPath, List<MediaItem> Movies);
    public record CreateCollectionRequest(string Name, string? Overview, string? PosterPath);
    public record AddCollectionItemRequest(int MediaItemId);
    public record MediaEditRequest(string Title, string? Overview, int? ReleaseYear, string? Genres, string? Director, string? PosterPath);

    public record PlaylistDto(int Id, string Name, string? Description, DateTime DateCreated, DateTime DateModified, int ItemCount);
    public record CreatePlaylistRequest(string Name, string? Description);
    public record AddPlaylistItemRequest(int MediaItemId);
    public record ReorderPlaylistRequest(List<int> ItemIds);
    public record MediaSubtitleDto(int Id, int MediaItemId, string SubtitleType, string Language, string Title, string Format, int? StreamIndex);
    public record MediaSpecsDto(int MediaItemId, string? Container, string? VideoCodec, string? VideoResolution, int VideoBitrate, string? VideoFrameRate, string? AudioCodec, int AudioChannels, long FileSize);
}

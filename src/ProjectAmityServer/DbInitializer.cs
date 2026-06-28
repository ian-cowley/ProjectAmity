using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ProjectAmityServer
{
    public static class DbInitializer
    {
        public static async Task InitializeAsync(GlacierDbService db, ILogger logger)
        {
            logger.LogInformation("Initializing database tables via Glacier.Sql...");

            try
            {
                var catalog = db.Catalog;

                // 1. Ensure Table 'TvShows' exists
                if (!catalog.TableExists("TvShows"))
                {
                    logger.LogInformation("Creating table 'TvShows'...");
                    string createTvShows = @"
                        CREATE TABLE TvShows (
                            Id INT PRIMARY KEY,
                            Title VARCHAR NOT NULL,
                            Overview VARCHAR,
                            PosterPath VARCHAR,
                            Genres VARCHAR,
                            ReleaseYear INT,
                            CastJson VARCHAR,
                            DateAdded DATETIME,
                            TvmazeId INT
                        )";
                    await db.ExecuteNonQueryAsync(createTvShows);
                }

                // 2. Ensure Table 'MediaItems' exists
                if (!catalog.TableExists("MediaItems"))
                {
                    logger.LogInformation("Creating table 'MediaItems'...");
                    string createMediaItems = @"
                        CREATE TABLE MediaItems (
                            Id INT PRIMARY KEY,
                            Title VARCHAR NOT NULL,
                            FilePath VARCHAR NOT NULL,
                            MediaType VARCHAR NOT NULL,
                            ReleaseYear INT,
                            DurationInSeconds INT,
                            ResumePositionInSeconds INT,
                            DateAdded DATETIME,
                            TvShowId INT,
                            SeasonNumber INT,
                            EpisodeNumber INT,
                            PosterPath VARCHAR,
                            Overview VARCHAR,
                            Genres VARCHAR,
                            Director VARCHAR,
                            CastJson VARCHAR,
                            LastWatched DATETIME,
                            Watched INT
                        )";
                    await db.ExecuteNonQueryAsync(createMediaItems);
                }
                else
                {
                    // Check for migrations
                    var mediaTable = catalog.GetTable("MediaItems");
                    if (mediaTable != null)
                    {
                        if (!mediaTable.Columns.Exists(c => c.Name.Equals("LastWatched", StringComparison.OrdinalIgnoreCase)))
                        {
                            logger.LogInformation("Adding column 'LastWatched' to MediaItems...");
                            await db.ExecuteNonQueryAsync("ALTER TABLE MediaItems ADD LastWatched DATETIME");
                        }
                        if (!mediaTable.Columns.Exists(c => c.Name.Equals("Watched", StringComparison.OrdinalIgnoreCase)))
                        {
                            logger.LogInformation("Adding column 'Watched' to MediaItems...");
                            await db.ExecuteNonQueryAsync("ALTER TABLE MediaItems ADD Watched INT");
                        }
                    }
                }

                // 3. Ensure Table 'ScanFolders' exists
                if (!catalog.TableExists("ScanFolders"))
                {
                    logger.LogInformation("Creating table 'ScanFolders'...");
                    string createScanFolders = @"
                        CREATE TABLE ScanFolders (
                            Id INT PRIMARY KEY,
                            FolderPath VARCHAR NOT NULL,
                            MediaType VARCHAR NOT NULL
                        )";
                    await db.ExecuteNonQueryAsync(createScanFolders);
                }

                // 4. Ensure Table 'Collections' exists
                if (!catalog.TableExists("Collections"))
                {
                    logger.LogInformation("Creating table 'Collections'...");
                    string createCollections = @"
                        CREATE TABLE Collections (
                            Id INT PRIMARY KEY,
                            Name VARCHAR NOT NULL UNIQUE,
                            Overview VARCHAR,
                            PosterPath VARCHAR,
                            DateCreated DATETIME
                        )";
                    await db.ExecuteNonQueryAsync(createCollections);
                }

                // 5. Ensure Table 'CollectionMediaItems' exists
                if (!catalog.TableExists("CollectionMediaItems"))
                {
                    logger.LogInformation("Creating table 'CollectionMediaItems'...");
                    string createCollMedia = @"
                        CREATE TABLE CollectionMediaItems (
                            CollectionId INT NOT NULL,
                            MediaItemId INT NOT NULL
                        )";
                    await db.ExecuteNonQueryAsync(createCollMedia);
                }

                // 6. Ensure Table 'MediaChapters' exists (Skip Intro Feature)
                if (!catalog.TableExists("MediaChapters"))
                {
                    logger.LogInformation("Creating table 'MediaChapters'...");
                    string createChapters = @"
                        CREATE TABLE MediaChapters (
                            Id INT PRIMARY KEY,
                            MediaItemId INT NOT NULL,
                            Title VARCHAR NOT NULL,
                            StartTime FLOAT NOT NULL,
                            EndTime FLOAT NOT NULL
                        )";
                    await db.ExecuteNonQueryAsync(createChapters);
                }

                // 7. Ensure Table 'SystemSettings' exists
                if (!catalog.TableExists("SystemSettings"))
                {
                    logger.LogInformation("Creating table 'SystemSettings'...");
                    string createSettings = @"
                        CREATE TABLE SystemSettings (
                            SettingKey VARCHAR PRIMARY KEY,
                            SettingValue VARCHAR NOT NULL
                        )";
                    await db.ExecuteNonQueryAsync(createSettings);

                    // Seed default settings
                    await db.ExecuteNonQueryAsync("INSERT INTO SystemSettings (SettingKey, SettingValue) VALUES ('DefaultTranscodeQuality', 'original')");
                    await db.ExecuteNonQueryAsync("INSERT INTO SystemSettings (SettingKey, SettingValue) VALUES ('MetadataLanguage', 'en-US')");
                    await db.ExecuteNonQueryAsync("INSERT INTO SystemSettings (SettingKey, SettingValue) VALUES ('AutoCreateCollections', 'true')");
                    await db.ExecuteNonQueryAsync("INSERT INTO SystemSettings (SettingKey, SettingValue) VALUES ('UiTheme', 'amity-dark')");
                    await db.ExecuteNonQueryAsync("INSERT INTO SystemSettings (SettingKey, SettingValue) VALUES ('AutoScanOnStartup', 'true')");
                }

                // 8. Ensure Table 'MediaSubtitles' exists
                if (!catalog.TableExists("MediaSubtitles"))
                {
                    logger.LogInformation("Creating table 'MediaSubtitles'...");
                    string createSubs = @"
                        CREATE TABLE MediaSubtitles (
                            Id INT PRIMARY KEY,
                            MediaItemId INT NOT NULL,
                            SubtitleType VARCHAR NOT NULL,
                            Language VARCHAR,
                            Title VARCHAR,
                            Format VARCHAR NOT NULL,
                            StreamIndex INT,
                            FilePath VARCHAR
                        )";
                    await db.ExecuteNonQueryAsync(createSubs);
                }

                // 9. Ensure Table 'MediaSpecs' exists
                if (!catalog.TableExists("MediaSpecs"))
                {
                    logger.LogInformation("Creating table 'MediaSpecs'...");
                    string createSpecs = @"
                        CREATE TABLE MediaSpecs (
                            MediaItemId INT PRIMARY KEY,
                            Container VARCHAR,
                            VideoCodec VARCHAR,
                            VideoResolution VARCHAR,
                            VideoBitrate INT,
                            VideoFrameRate VARCHAR,
                            AudioCodec VARCHAR,
                            AudioChannels INT,
                            FileSize VARCHAR
                        )";
                    await db.ExecuteNonQueryAsync(createSpecs);
                }

                // 10. Ensure Table 'Playlists' exists
                if (!catalog.TableExists("Playlists"))
                {
                    logger.LogInformation("Creating table 'Playlists'...");
                    string createPlaylists = @"
                        CREATE TABLE Playlists (
                            Id INT PRIMARY KEY,
                            Name VARCHAR NOT NULL,
                            Description VARCHAR,
                            DateCreated DATETIME NOT NULL,
                            DateModified DATETIME NOT NULL
                        )";
                    await db.ExecuteNonQueryAsync(createPlaylists);
                }

                // 11. Ensure Table 'PlaylistItems' exists
                if (!catalog.TableExists("PlaylistItems"))
                {
                    logger.LogInformation("Creating table 'PlaylistItems'...");
                    string createPlaylistItems = @"
                        CREATE TABLE PlaylistItems (
                            Id INT PRIMARY KEY,
                            PlaylistId INT NOT NULL,
                            MediaItemId INT NOT NULL,
                            SortOrder INT NOT NULL
                        )";
                    await db.ExecuteNonQueryAsync(createPlaylistItems);
                }

                // Seed default folders if empty
                var folders = await db.ExecuteQueryAsync("SELECT * FROM ScanFolders");
                if (folders.Count == 0)
                {
                    logger.LogInformation("Seeding default media directories...");
                    
                    string defaultMovieDir = "D:\\Media\\Movies";
                    string defaultTvDir = "D:\\Media\\TVSeries";

                    if (!Directory.Exists(defaultMovieDir))
                    {
                        defaultMovieDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "Movies");
                    }
                    if (!Directory.Exists(defaultTvDir))
                    {
                        defaultTvDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "TV");
                    }

                    try
                    {
                        Directory.CreateDirectory(defaultMovieDir);
                        Directory.CreateDirectory(defaultTvDir);
                        logger.LogInformation($"Created physical directories: {defaultMovieDir} and {defaultTvDir}");
                    }
                    catch (Exception ioEx)
                    {
                        logger.LogWarning($"Could not create folders on disk: {ioEx.Message}");
                    }

                    int movieFolderId = await db.GetNextIdAsync("ScanFolders");
                    await db.ExecuteNonQueryAsync("INSERT INTO ScanFolders (Id, FolderPath, MediaType) VALUES (@Id, @Path, 'Movie')", new Dictionary<string, object?> {
                        { "@Id", movieFolderId },
                        { "@Path", defaultMovieDir }
                    });

                    int tvFolderId = await db.GetNextIdAsync("ScanFolders");
                    await db.ExecuteNonQueryAsync("INSERT INTO ScanFolders (Id, FolderPath, MediaType) VALUES (@Id, @Path, 'Episode')", new Dictionary<string, object?> {
                        { "@Id", tvFolderId },
                        { "@Path", defaultTvDir }
                    });
                    
                    logger.LogInformation("Default scan directories seeded in Glacier.Sql.");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while initializing the database.");
                throw;
            }
        }
    }
}

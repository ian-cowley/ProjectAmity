using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ProjectAmityServer
{
    public class MetadataScraperService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<MetadataScraperService> _logger;
        private readonly GlacierDbService _db;

        public MetadataScraperService(ILogger<MetadataScraperService> logger, GlacierDbService db)
        {
            _logger = logger;
            _db = db;
            
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        // ==================== MOVIES (TMDB HTML SCRAPER) ====================

        public async Task<MovieMetadata?> ScrapeMovieMetadataAsync(string title, int? year)
        {
            _logger.LogInformation($"Scraping TMDb for movie: '{title}'" + (year.HasValue ? $" ({year})" : ""));
            
            // 1. Try search with title + year
            var meta = await ScrapeMovieMetadataInternalAsync(title, year);
            if (meta != null) return meta;

            // 2. If that failed and we had a year, retry searching with ONLY title
            if (year.HasValue)
            {
                _logger.LogInformation($"Retrying search for '{title}' without year...");
                meta = await ScrapeMovieMetadataInternalAsync(title, null);
                if (meta != null) return meta;
            }

            return null;
        }

        private async Task<MovieMetadata?> ScrapeMovieMetadataInternalAsync(string title, int? year)
        {
            try
            {
                string searchQuery = title;
                if (year.HasValue)
                {
                    searchQuery += $" {year.Value}";
                }

                string lang = await GetMetadataLanguageAsync();
                string url = $"https://www.themoviedb.org/search/movie?query={Uri.EscapeDataString(searchQuery)}&language={lang}";
                
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"TMDb request failed with status: {response.StatusCode}");
                    return null;
                }

                string finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
                string movieHtml = await response.Content.ReadAsStringAsync();

                var directMovieMatch = Regex.Match(finalUrl, @"/movie/(?<id>\d+)", RegexOptions.IgnoreCase);
                if (directMovieMatch.Success)
                {
                    string movieId = directMovieMatch.Groups["id"].Value;
                    _logger.LogInformation($"TMDb redirected directly to Movie ID: {movieId}.");
                    return ParseMoviePage(movieHtml, finalUrl);
                }

                // Find first movie result link: /movie/(\d+)-slug
                var movieLinkRegex = new Regex(@"href=""/movie/(?<id>\d+)[^""]*""", RegexOptions.IgnoreCase);
                var linkMatch = movieLinkRegex.Match(movieHtml);
                if (!linkMatch.Success)
                {
                    return null;
                }

                string foundMovieId = linkMatch.Groups["id"].Value;
                string movieUrl = $"https://www.themoviedb.org/movie/{foundMovieId}?language={lang}";
                _logger.LogInformation($"Found TMDb Movie ID: {foundMovieId} in search results. Fetching details...");
                
                string detailsHtml = await _httpClient.GetStringAsync(movieUrl);
                return ParseMoviePage(detailsHtml, movieUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error scraping TMDb movie metadata internally for {title}");
                return null;
            }
        }

        private MovieMetadata ParseMoviePage(string html, string pageUrl)
        {
            var meta = new MovieMetadata();

            // 1. Title
            var titleRegex = new Regex(@"<h2[^>]*>\s*<a[^>]*>(?<title>[^<]+)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var titleMatch = titleRegex.Match(html);
            if (titleMatch.Success)
            {
                meta.Title = WebUtility.HtmlDecode(titleMatch.Groups["title"].Value.Trim());
            }

            // 2. Year
            var yearRegex = new Regex(@"<span class=""release_date"">[^<]*\((?<year>19\d{2}|20\d{2})\)[^<]*</span>", RegexOptions.IgnoreCase);
            var yearMatch = yearRegex.Match(html);
            if (yearMatch.Success && int.TryParse(yearMatch.Groups["year"].Value, out int year))
            {
                meta.ReleaseYear = year;
            }

            // 3. Poster Image
            var posterRegex = new Regex(@"<img[^>]*class=""[^""]*poster[^""]*""[^>]*src=""(?<src>[^""]+)""", RegexOptions.IgnoreCase);
            var posterMatch = posterRegex.Match(html);
            if (!posterMatch.Success)
            {
                posterRegex = new Regex(@"<img[^>]*class=""[^""]*poster[^""]*""[^>]*data-src=""(?<src>[^""]+)""", RegexOptions.IgnoreCase);
                posterMatch = posterRegex.Match(html);
            }
            if (!posterMatch.Success)
            {
                // Fallback to Schema.org JSON-LD
                var schemaRegex = new Regex(@"""@type""\s*:\s*""Movie"".*?""image""\s*:\s*""(?<src>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                posterMatch = schemaRegex.Match(html);
            }
            if (posterMatch.Success)
            {
                string src = posterMatch.Groups["src"].Value;
                meta.PosterPath = CleanTmdbImageUrl(src, "w500");
            }

            // 4. Genres
            var genresRegex = new Regex(@"<span class=""genres"">(?<content>.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var genresMatch = genresRegex.Match(html);
            if (genresMatch.Success)
            {
                var genreLinkRegex = new Regex(@"href=""/genre/[^""]+"">(?<name>[^<]+)</a>", RegexOptions.IgnoreCase);
                var matches = genreLinkRegex.Matches(genresMatch.Groups["content"].Value);
                var genresList = new List<string>();
                foreach (Match m in matches)
                {
                    genresList.Add(WebUtility.HtmlDecode(m.Groups["name"].Value.Trim()));
                }
                meta.Genres = string.Join(", ", genresList);
            }

            // 5. Overview
            var overviewRegex = new Regex(@"<div class=""overview""[^>]*>\s*<p>(?<overview>.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var overviewMatch = overviewRegex.Match(html);
            if (overviewMatch.Success)
            {
                meta.Overview = WebUtility.HtmlDecode(overviewMatch.Groups["overview"].Value.Trim());
            }

            // 6. Director
            var directorRegex = new Regex(@"<li class=""profile"">.*?<a href=""/person/[^""]+"">(?<name>[^<]+)</a>.*?<p class=""character"">[^<]*?Director[^<]*?</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var directorMatch = directorRegex.Match(html);
            if (directorMatch.Success)
            {
                meta.Director = WebUtility.HtmlDecode(directorMatch.Groups["name"].Value.Trim());
            }

            // 6.5. Collection
            var collectionRegex = new Regex(@"Part of the <a href=""/collection/[^""]+"">(?<name>[^<]+) Collection</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var collectionMatch = collectionRegex.Match(html);
            if (!collectionMatch.Success)
            {
                collectionRegex = new Regex(@"Part of the (?<name>[^<]+) Collection", RegexOptions.IgnoreCase);
                collectionMatch = collectionRegex.Match(html);
            }
            if (collectionMatch.Success)
            {
                meta.CollectionName = WebUtility.HtmlDecode(collectionMatch.Groups["name"].Value.Trim() + " Collection");
            }

            // 7. Cast & Crew (Top 10 circular actors)
            meta.Cast = ScrapeMovieCast(html);

            return meta;
        }

        private List<CastMember> ScrapeMovieCast(string html)
        {
            var castList = new List<CastMember>();
            
            // Extract the cast card block
            int castStart = html.IndexOf("<ol class=\"people scroller\">");
            if (castStart == -1) return castList;
            
            int castEnd = html.IndexOf("</ol>", castStart);
            if (castEnd == -1) return castList;

            string castBlock = html.Substring(castStart, castEnd - castStart);
            
            // Split by list items (cards)
            var cardSplit = castBlock.Split(new[] { "<li class=\"card\">" }, StringSplitOptions.RemoveEmptyEntries);
            
            // Skip first element as it's the opening tag text
            for (int i = 1; i < cardSplit.Length && castList.Count < 10; i++)
            {
                string card = cardSplit[i];

                // Regex for Name (supporting slugs)
                var nameMatch = Regex.Match(card, @"<p><a href=""/person/\d+[^""]*"">(?<name>[^<]+)</a></p>", RegexOptions.IgnoreCase);
                if (!nameMatch.Success) continue;

                string actorName = WebUtility.HtmlDecode(nameMatch.Groups["name"].Value.Trim());

                // Regex for Character
                var charMatch = Regex.Match(card, @"<p class=""character"">(?<char>[^<]+)</p>", RegexOptions.IgnoreCase);
                string character = charMatch.Success ? WebUtility.HtmlDecode(charMatch.Groups["char"].Value.Trim()) : "";

                // Regex for Image
                var imgMatch = Regex.Match(card, @"<img[^>]*src=""(?<src>[^""]+)""", RegexOptions.IgnoreCase);
                if (!imgMatch.Success)
                {
                    imgMatch = Regex.Match(card, @"<img[^>]*data-src=""(?<src>[^""]+)""", RegexOptions.IgnoreCase);
                }

                string imgUrl = "";
                if (imgMatch.Success)
                {
                    imgUrl = CleanTmdbImageUrl(imgMatch.Groups["src"].Value, "w185");
                }

                castList.Add(new CastMember
                {
                    Name = actorName,
                    Character = character,
                    ImageUrl = imgUrl
                });
            }

            return castList;
        }

        private string CleanTmdbImageUrl(string rawUrl, string targetSize)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return "";
            
            // If it's a relative URL, prepend TMDB media domain
            if (rawUrl.StartsWith("/"))
            {
                return $"https://image.tmdb.org/t/p/{targetSize}{rawUrl}";
            }

            // If it already contains size structure e.g. /t/p/w185_and_h278_face/...
            if (rawUrl.Contains("/t/p/"))
            {
                var filenameIndex = rawUrl.LastIndexOf('/');
                if (filenameIndex != -1)
                {
                    string filename = rawUrl.Substring(filenameIndex);
                    return $"https://image.tmdb.org/t/p/{targetSize}{filename}";
                }
            }

            return rawUrl;
        }

        // ==================== TV SHOWS (TVMAZE JSON API) ====================

        public async Task<TvShowMetadata?> FetchTvShowMetadataAsync(string showName)
        {
            _logger.LogInformation($"Querying TVmaze API for show: '{showName}'");
            try
            {
                string url = $"https://api.tvmaze.com/singlesearch/shows?q={Uri.EscapeDataString(showName)}&embed=cast";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"TVmaze returned status {response.StatusCode} for show: {showName}");
                    return null;
                }

                string jsonString = await response.Content.ReadAsStringAsync();
                using (var doc = JsonDocument.Parse(jsonString))
                {
                    var root = doc.RootElement;
                    var meta = new TvShowMetadata
                    {
                        TvmazeId = root.GetProperty("id").GetInt32(),
                        Title = root.GetProperty("name").GetString() ?? showName
                    };

                    // Summary
                    if (root.TryGetProperty("summary", out var summaryProp))
                    {
                        // Clean <p> and HTML tags if desired, but retaining tags is fine for HTML display
                        meta.Overview = summaryProp.GetString();
                    }

                    // Release Year
                    if (root.TryGetProperty("premiered", out var premProp) && premProp.ValueKind == JsonValueKind.String)
                    {
                        string premDate = premProp.GetString() ?? "";
                        if (premDate.Length >= 4 && int.TryParse(premDate.Substring(0, 4), out int year))
                        {
                            meta.ReleaseYear = year;
                        }
                    }

                    // Poster
                    if (root.TryGetProperty("image", out var imgProp) && imgProp.ValueKind == JsonValueKind.Object)
                    {
                        if (imgProp.TryGetProperty("original", out var origProp))
                        {
                            meta.PosterPath = origProp.GetString() ?? "";
                        }
                        else if (imgProp.TryGetProperty("medium", out var medProp))
                        {
                            meta.PosterPath = medProp.GetString() ?? "";
                        }
                    }

                    // Genres
                    if (root.TryGetProperty("genres", out var genresProp) && genresProp.ValueKind == JsonValueKind.Array)
                    {
                        var genresList = new List<string>();
                        foreach (var g in genresProp.EnumerateArray())
                        {
                            genresList.Add(g.GetString() ?? "");
                        }
                        meta.Genres = string.Join(", ", genresList);
                    }

                    // Cast List
                    var castList = new List<CastMember>();
                    if (root.TryGetProperty("_embedded", out var embedProp) && 
                        embedProp.TryGetProperty("cast", out var castProp) && 
                        castProp.ValueKind == JsonValueKind.Array)
                    {
                        int count = 0;
                        foreach (var member in castProp.EnumerateArray())
                        {
                            if (count >= 10) break;

                            string actorName = "";
                            string roleName = "";
                            string actorImg = "";

                            if (member.TryGetProperty("person", out var person) && person.TryGetProperty("name", out var pName))
                            {
                                actorName = pName.GetString() ?? "";
                            }
                            if (member.TryGetProperty("character", out var character) && character.TryGetProperty("name", out var cName))
                            {
                                roleName = cName.GetString() ?? "";
                            }
                            if (member.TryGetProperty("person", out var p) && p.TryGetProperty("image", out var pImg) && pImg.ValueKind == JsonValueKind.Object)
                            {
                                if (pImg.TryGetProperty("medium", out var medImg))
                                {
                                    actorImg = medImg.GetString() ?? "";
                                }
                            }

                            if (!string.IsNullOrEmpty(actorName))
                            {
                                castList.Add(new CastMember
                                {
                                    Name = actorName,
                                    Character = roleName,
                                    ImageUrl = actorImg
                                });
                                count++;
                            }
                        }
                    }
                    meta.Cast = castList;

                    return meta;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error fetching TVmaze show metadata for {showName}");
                return null;
            }
        }

        public async Task<List<TvmazeEpisode>?> FetchEpisodesListAsync(int showId)
        {
            _logger.LogInformation($"Querying TVmaze API for episodes list: showId={showId}");
            try
            {
                string url = $"https://api.tvmaze.com/shows/{showId}/episodes";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                string jsonString = await response.Content.ReadAsStringAsync();
                var episodes = JsonSerializer.Deserialize<List<TvmazeEpisode>>(jsonString, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return episodes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error fetching TVmaze episodes list for showId={showId}");
                return null;
            }
        }

        private async Task<string> GetMetadataLanguageAsync()
        {
            try
            {
                var rows = await _db.ExecuteQueryAsync("SELECT SettingValue FROM SystemSettings WHERE SettingKey = 'MetadataLanguage'");
                if (rows.Count > 0)
                {
                    return rows[0]["SettingValue"]?.ToString() ?? "en-US";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not load MetadataLanguage setting: {ex.Message}. Defaulting to en-US.");
            }
            return "en-US";
        }
    }

    // ==================== DTO ENTITIES ====================

    public class MovieMetadata
    {
        public string Title { get; set; } = "";
        public string Overview { get; set; } = "";
        public string PosterPath { get; set; } = "";
        public string Genres { get; set; } = "";
        public int? ReleaseYear { get; set; }
        public string Director { get; set; } = "";
        public string CollectionName { get; set; } = "";
        public List<CastMember> Cast { get; set; } = new List<CastMember>();
    }

    public class TvShowMetadata
    {
        public int TvmazeId { get; set; }
        public string Title { get; set; } = "";
        public string Overview { get; set; } = "";
        public string PosterPath { get; set; } = "";
        public string Genres { get; set; } = "";
        public int? ReleaseYear { get; set; }
        public List<CastMember> Cast { get; set; } = new List<CastMember>();
    }

    public class CastMember
    {
        public string Name { get; set; } = "";
        public string Character { get; set; } = "";
        public string ImageUrl { get; set; } = "";
    }

    public class TvmazeEpisode
    {
        public string Name { get; set; } = "";
        public int Season { get; set; }
        public int Number { get; set; }
        public string Summary { get; set; } = "";
        public TvmazeImage? Image { get; set; }
    }

    public class TvmazeImage
    {
        public string Medium { get; set; } = "";
        public string Original { get; set; } = "";
    }
}

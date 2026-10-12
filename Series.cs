using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;

namespace AniDownloaderTerminal
{
    /// <summary>
    /// A configured series: title, library path, episode offset, and a filter that can exclude or include releases.
    /// </summary>
    [Serializable]
    public partial class Series : ISerializable
    {
        /// <summary>
        /// Series title used for the Nyaa search and for illegal-character cleanup.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Local directory that already contains, or will contain, this series.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Added to a parsed episode number before the episode is stored.
        /// </summary>
        public int Offset { get; }

        /// <summary>
        /// Regular expression applied to the release title. Empty means no extra filtering.
        /// When <see cref="FilterIsInclusive"/> is false (default), a matching title is excluded.
        /// When true, only a matching title is kept; a non-matching title is excluded.
        /// </summary>
        public string Filter { get; }

        /// <summary>
        /// When true, the filter is inclusive: only releases whose title matches the regular expression are kept.
        /// When false, the filter is exclusive: releases whose title matches are excluded. Empty filter ignores this flag.
        /// </summary>
        public bool FilterIsInclusive { get; }

        private const string NyaaRssSearchUrlFormat = "https://nyaa.si/?page=rss&q={0}&c=1_0&f=0";
        private const string IllegalPathCharacters = "/\\?%*:|<>";

        // Episode files are named with a 2 or 3 digit episode number immediately before the extension.
        [GeneratedRegex(@"(\d{2,3})\.(?:mp4|mkv)", RegexOptions.IgnoreCase, "en-001")]
        private static partial Regex DownloadedEpisodeNumberRegex();

        /// <summary>
        /// Creates a series after checking the name, path, filter, and that the directory exists.
        /// </summary>
        public Series(string name, string path, int offset, string filter, bool filterIsInclusive = false)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (filter == null) filter = string.Empty; // null filter is treated as empty
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);

            Name = ReplaceIllegalCharacters(name).Trim();
            Path = path;
            Offset = offset;
            Filter = filter;
            FilterIsInclusive = filterIsInclusive;
        }

        /// <summary>
        /// Restores a series from the serialization contract written by <see cref="GetObjectData"/>.
        /// </summary>
        /// <remarks>
        /// How it works: Name, Path, and Filter are strings. Offset is an int. FilterIsInclusive is a bool that defaults to false when the value is absent.
        /// Filter must be read as a string; reading it as an int fails the round trip.
        /// </remarks>
        public Series(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));

            string? name = (string?)info.GetValue("Name", typeof(string));
            string? path = (string?)info.GetValue("Path", typeof(string));
            int? offset = (int?)info.GetValue("Offset", typeof(int));
            string? filter = (string?)info.GetValue("Filter", typeof(string));
            bool filterIsInclusive = false;
            try
            {
                filterIsInclusive = info.GetBoolean("FilterIsInclusive");
            }
            catch (SerializationException)
            {
                // Older serialized data has no FilterIsInclusive; keep the exclusive default.
            }
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (offset == null) throw new ArgumentNullException(nameof(offset));
            if (filter == null) filter = string.Empty; // null filter is treated as empty

            Name = ReplaceIllegalCharacters(name);
            Path = path;
            Offset = (int)offset;
            Filter = filter;
            FilterIsInclusive = filterIsInclusive;
        }

        /// <summary>
        /// Returns mkv and mp4 files in the series directory. An unreadable directory is logged and yields no files.
        /// </summary>
        public string[] GetAllEpisodeFiles()
        {
            HashSet<string> episodes = [];
            HashSet<string> validExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4" };
            foreach (string fileName in EnumerateSeriesFiles("GetAllEpisodeFiles"))
            {
                string extension = System.IO.Path.GetExtension(fileName);
                if (validExtensions.Contains(extension))
                {
                    episodes.Add(fileName);
                }
            }
            return [.. episodes];
        }

        /// <summary>
        /// Returns episode numbers parsed from downloaded mkv and mp4 file names. Unparseable names are logged and skipped.
        /// </summary>
        public int[] GetEpisodesDownloaded()
        {
            HashSet<int> episodes = [];
            foreach (string fileName in EnumerateSeriesFiles("GetEpisodesDownloaded"))
            {
                Match match = DownloadedEpisodeNumberRegex().Match(fileName);
                if (!match.Success)
                {
                    continue;
                }

                string episodeText = match.Groups[1].Value;
                if (!int.TryParse(episodeText, out int episodeNumber))
                {
                    Global.TaskAdmin.Logger.EX_Log(
                        $"Could not parse episode number '{episodeText}' from '{fileName}' for series '{Name}'.",
                        "GetEpisodesDownloaded");
                    continue;
                }
                episodes.Add(episodeNumber);
            }
            return [.. episodes];
        }

        /// <summary>
        /// Returns episode numbers for subtitle files whose name ends with the language code and .ass or .srt.
        /// </summary>
        /// <param name="shortLanguage">Language suffix used in the subtitle file name, such as eng.</param>
        public int[] GetSubsDownloaded(string shortLanguage)
        {
            if (shortLanguage == null) throw new ArgumentNullException(nameof(shortLanguage));

            HashSet<int> subtitles = [];
            Regex subtitleRegex = CreateSubtitleEpisodeRegex(shortLanguage);
            foreach (string fileName in EnumerateSeriesFiles("GetSubsDownloaded"))
            {
                Match match = subtitleRegex.Match(fileName);
                if (!match.Success)
                {
                    continue;
                }

                string episodeText = match.Groups[1].Value;
                if (!int.TryParse(episodeText, out int episodeNumber))
                {
                    Global.TaskAdmin.Logger.EX_Log(
                        $"Could not parse subtitle episode number '{episodeText}' from '{fileName}' for series '{Name}'.",
                        "GetSubsDownloaded");
                    continue;
                }
                subtitles.Add(episodeNumber);
            }
            return [.. subtitles];
        }

        /// <summary>
        /// Writes Name, Path, Offset, Filter, and FilterIsInclusive using the same types the constructor reads back.
        /// </summary>
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));

            info.AddValue("Name", Name, typeof(string));
            info.AddValue("Path", Path, typeof(string));
            info.AddValue("Offset", Offset, typeof(int));
            info.AddValue("Filter", Filter, typeof(string));
            info.AddValue("FilterIsInclusive", FilterIsInclusive, typeof(bool));
        }

        /// <summary>
        /// Replaces characters that are illegal in a series folder name with spaces, and removes quotes.
        /// </summary>
        public static string ReplaceIllegalCharacters(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            StringBuilder cleaned = new(text.Length);
            foreach (char character in text)
            {
                if (character == '"')
                {
                    continue;
                }
                if (IllegalPathCharacters.Contains(character))
                {
                    cleaned.Append(' ');
                    continue;
                }
                cleaned.Append(character);
            }
            
            return cleaned.ToString().Trim();
        }

        /// <summary>
        /// Searches Nyaa for this series and returns releases that pass age, seeder, size, batch, and filter checks.
        /// </summary>
        /// <remarks>
        /// How it works: one RSS request is parsed into episode elements. A malformed item is logged and skipped. The filter excludes a matching title when exclusive, or a non-matching title when inclusive. An invalid filter fails this search instead of downloading every release.
        /// </remarks>
        public async Task<OnlineEpisodeElement[]> GetAvailableSeriesEpisodes()
        {
            List<OnlineEpisodeElement> episodes = [];
            string encodedName = System.Web.HttpUtility.UrlEncode(Name);
            string searchUrl = string.Format(NyaaRssSearchUrlFormat, encodedName);
            string content = await Global.GetWebStringFromUrl(searchUrl);
            if (string.IsNullOrWhiteSpace(content))
            {
                Global.TaskAdmin.Logger.EX_Log(
                    $"Nyaa search returned no content for series '{Name}'.",
                    "GetAvailableSeriesEpisodes");
                return [];
            }

            if (!string.IsNullOrWhiteSpace(Filter) && !IsExcludeFilterValid())
            {
                return [];
            }

            string[] list = OnlineEpisodeElement.GetOnlineEpisodesListFromContent(content);
            int episodesFound = 0;
            foreach (string item in list)
            {
                if (!TryParseRelease(item, out OnlineEpisodeElement? element) || element == null) continue;
                if (element.ProbableEpNumber == null) continue;
                if (!element.IsAnime) continue;
                if (element.IsTooOld) continue;
                if (element.IsTooNew) continue;
                if (element.TooFewSeeders) continue;
                if (ReleaseShouldBeExcludedByFilter(element)) continue;
                if (Settings.ExcludeBatchReleases && element.Name.Contains("BATCH", StringComparison.InvariantCultureIgnoreCase)) continue; //Exclude batch releases if the setting is enabled
                if (element.SizeMiB > Settings.MaxFileSizeMb)
                {
                    Global.TaskAdmin.Logger.Log(
                        $"{element.Name} discarded due to big size (over {Settings.MaxFileSizeMb} MB).",
                        "GetAvailableSeriesEpisodes");
                    continue;
                }

                element.AddEpisodeNumberOffset(Offset);
                episodesFound += 1;
                episodes.Add(element);
            }

            Global.CurrentOpsQueue.Enqueue($"Found {episodesFound} episode candidate{(episodesFound == 1 ? string.Empty : "s")} online.");
            return [.. episodes];
        }

        /// <summary>
        /// Returns true when the filter should exclude this release.
        /// Exclusive mode (default) excludes a matching title. Inclusive mode excludes a non-matching title. An empty filter never excludes.
        /// </summary>
        private bool ReleaseShouldBeExcludedByFilter(OnlineEpisodeElement element)
        {
            if (string.IsNullOrWhiteSpace(Filter))
            {
                return false;
            }
            bool matches = Regex.IsMatch(element.Name, Filter);
            if (FilterIsInclusive)
            {
                return !matches;
            }
            return matches;
        }

        /// <summary>
        /// Checks the exclude filter once. An invalid expression is logged and rejects the search.
        /// </summary>
        private bool IsExcludeFilterValid()
        {
            try
            {
                _ = new Regex(Filter);
                return true;
            }
            catch (ArgumentException exception)
            {
                Global.TaskAdmin.Logger.EX_Log(
                    $"Exclude filter for series '{Name}' is not a valid regular expression: {exception.Message}",
                    "GetAvailableSeriesEpisodes");
                return false;
            }
        }

        /// <summary>
        /// Parses one Nyaa item. A malformed item is logged and skipped so the rest of the search can continue.
        /// </summary>
        private bool TryParseRelease(string item, out OnlineEpisodeElement? element)
        {
            try
            {
                element = new OnlineEpisodeElement(item);
                return true;
            }
            catch (InvalidDataException exception)
            {
                Global.TaskAdmin.Logger.EX_Log(
                    $"Skipped an unparseable Nyaa item for series '{Name}': {exception.Message}",
                    "GetAvailableSeriesEpisodes");
                element = null;
                return false;
            }
        }

        /// <summary>
        /// Lists files in the series directory. An IO failure is logged and yields no files.
        /// </summary>
        private IEnumerable<string> EnumerateSeriesFiles(string methodName)
        {
            try
            {
                return Directory.GetFiles(Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                Global.TaskAdmin.Logger.EX_Log(
                    $"Could not list files for series '{Name}' in '{Path}': {exception.Message}",
                    methodName);
                return [];
            }
        }

        /// <summary>
        /// Builds the subtitle file pattern for a language suffix. The language is escaped so it is matched literally.
        /// </summary>
        /// <remarks>
        /// How it works: episode number, a dot, the language suffix, then .ass or .srt.
        /// </remarks>
        private static Regex CreateSubtitleEpisodeRegex(string shortLanguage)
        {
            string escapedLanguage = Regex.Escape(shortLanguage);
            return new Regex(
                @"(\d{2,3})\." + escapedLanguage + @"\.(?:ass|srt)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}

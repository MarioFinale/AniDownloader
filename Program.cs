using DataTablePrettyPrinter;
using MonoTorrent.Client;
using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static AniDownloaderTerminal.SeriesDownloader;
using static AniDownloaderTerminal.SeriesDownloader.EpisodeToDownload;


namespace AniDownloaderTerminal
{
    public partial class Program
    {
        private string CurrentlyScanningSeries = string.Empty;
        private readonly SeriesDownloader CurrentSeriesDownloader = new();

        private int PreviousLineCount = 0;
        private int PreviousWindowHeight = 0;
        private int PreviousWindowWidth = 0;

        // Cached remote worker claims so the 200ms UI refresh does not walk the
        // filesystem every tick. Refreshed at most every WorkerClaimRefreshMs.
        private List<EncodingClaim> _cachedRemoteClaims = [];
        private DateTime _lastRemoteClaimScanUtc = DateTime.MinValue;
        private readonly object _remoteClaimsLock = new();

        public static readonly Settings settings = new();
        public static readonly Webserver webserver = new();

        [GeneratedRegex(@"\d{1,3}$")]
        private static partial Regex TempFolderEpisodeNumberRegex();

        private int _stoppingTorrents = 0;

        public static void Main(string[] args)
        {
            // Remote encoder mode: dotnet run -- --worker
            // (or pass -worker / worker). Uses the same settings + media paths;
            // coordinates via worker.claim files inside each episode .temp folder.
            if (args.Any(a => string.Equals(a, "--worker", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "-worker", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "worker", StringComparison.OrdinalIgnoreCase)))
            {
                EncoderWorker.Run();
                return;
            }

            settings.Init();
            Program program = new();
            webserver.Init();            
            Task task = Task.Run(async () => { await program.Start(); });
            task.Wait();
        }

        public Program()
        {
            LoadSeriesTable();
            Console.CursorVisible = false;
        }

        public async Task Start()
        {
            Console.Clear();
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Console.CancelKeyPress += OnCancelKeyPress;

            await CurrentSeriesDownloader.ResumeExistingTorrentsAsync();

            bool CleanFinishedSeriesTask()
            {
                CleanFinishedSeries();
                return true;
            }

            Global.TaskAdmin.NewTask("CleanFinishedSeries", "Downloader", CleanFinishedSeriesTask, 300000, true);


            bool StartDownloadsTask()
            {
                Thread.Sleep(5000);//Delay so it doesn't crash with CleanFinishedSeriesTask at the beggining.
                CurrentSeriesDownloader.StartDownloads();
                return true;
            }

            Global.TaskAdmin.NewTask("StartDownloads", "Downloader", StartDownloadsTask, 1000, true);


            bool StartConversionsTask()
            {
                CurrentSeriesDownloader.StartConversions();
                return true;
            }

            Global.TaskAdmin.NewTask("StartConvertions", "Downloader", StartConversionsTask, 2000, true);


            bool CleanEncodedFilesTask()
            {
                CurrentSeriesDownloader.CleanEncodedFiles();
                return true;
            };

            Global.TaskAdmin.NewTask("CleanEncodedFiles", "Downloader", CleanEncodedFilesTask, 10000, true);

            bool UpdateSeriesDataTableTask()
            {
                UpdateSeriesDataTable();
                return true;
            };

            Global.TaskAdmin.NewTask("UpdateSeriesDataTable", "Downloader", UpdateSeriesDataTableTask, 200, true);


            bool PrintUpdateTableTask()
            {
                PrintUpdateTable();
                return true;
            };
            Global.TaskAdmin.NewTask("PrintUpdateTable", "Downloader", PrintUpdateTableTask, 100, true);

            bool SearchForUncompletedAndNeedConvertSeriesTask()
            {
                SearchForUncompletedAndNeedConvertSeries();
                return true;
            }
            Global.TaskAdmin.NewTask("SearchForNeedConvertSeriesTask", "Downloader", SearchForUncompletedAndNeedConvertSeriesTask, 300000, true);

            await UpdateSeries();
        }

        /// <summary>
        /// Flushes torrent fast-resume data when the process is exiting.
        /// </summary>
        private void OnProcessExit(object? sender, EventArgs e)
        {
            StopTorrentsOnExit();
        }

        /// <summary>
        /// Flushes torrent fast-resume data on Ctrl+C before the process terminates.
        /// </summary>
        private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            StopTorrentsOnExit();
        }

        /// <summary>
        /// Stops every torrent once so a second exit signal does not start another flush.
        /// </summary>
        private void StopTorrentsOnExit()
        {
            if (Interlocked.Exchange(ref _stoppingTorrents, 1) == 1) return;
            try
            {
                CurrentSeriesDownloader.StopAllTorrentsAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.EX_Log(ex.Message, "StopTorrentsOnExit");
            }
        }

        /// 
        /// Cleans finished series from the SeriesTable by checking if the series is marked as finished on Anilist
        /// and if the local directory contains at least as many video files as the total episodes reported.
        /// Removes matching rows from the DataTable and persists changes to XML if any deletions occur.
        ///
        public void CleanFinishedSeries()
        {
            HashSet<(string name, string path)> seriesRows = [];
            HashSet<(string name, string path)> rowsToDelete = [];
            lock (Global.SeriesTable)
            {
                foreach (DataRow row in Global.SeriesTable.Rows)
                {
                    string? seriesName = row["Name"]?.ToString();
                    string? seriesPath = row["Path"]?.ToString();
                    if (string.IsNullOrWhiteSpace(seriesName) || string.IsNullOrWhiteSpace(seriesPath))
                    {
                        continue;
                    }
                    seriesRows.Add((seriesName, seriesPath));
                }
            }

            foreach ((string name, string path) row in seriesRows)
            {
                Global.CurrentOpsQueue.Enqueue($"Checking if {row.name} is finished.");
                try
                {
                    if (!Directory.Exists(row.path)) continue; // Directory cannot be accessed, skip.

                    // This program stores the episode names as 'SeriesName {episode}.ext' although the path can be specified by the user. We enforce flat directories.
                    // We use MKV as video container but there is the possibility the user manually downloaded some episodes so we should look for valid extensions specified in the settings.
                    int fileCount = Directory.EnumerateFiles(row.path, row.name + "*.*")
                                             .Where(f => Settings.ValidExtensions.Select(x => x.ToLowerInvariant()).ToHashSet().Contains(Path.GetExtension(f).ToLowerInvariant()))
                                             .Count();

                    if (fileCount == 0) continue; // New Series, don't bother to waste resources on querying.

                    // Since the user can specify the path (but not the file names) we try to check if the directory is following a Plex-style folder structure
                    // where it usually follows the '/Series Name/Season/Episode.ext' structure. We use '$' to make sure we match the end of the path.
                    // Matches 's00' 's-00' 's_00' 's.00' 'season00' 'season 00' 'season-00' 'season_00' 'season.00' '00' at the end of the path. Case insensitive.
                    Match isSeasonMatchInPath = Regex.Match(row.path.Trim(), @"(?:.+?)[/\\](?:[Ss](?:eason)?[ \-_\\.]*)*(\d{1,3})(?:$|[/\\]$)", RegexOptions.IgnoreCase);
                    Match isSeasonMatchInName = Regex.Match(row.name.Trim(), @"(?:(?:s|season)[ .\-_]*| )(\d{1,3})(?:nd season$|$)", RegexOptions.IgnoreCase); //We check the name too, if it matches we clean it.

                    string searchName = row.name; // Series name that we will use to query anilist.
                    
                    if (isSeasonMatchInPath.Success)
                    {
                        if (isSeasonMatchInName.Success)
                        {
                            searchName = searchName.Replace(isSeasonMatchInName.Value, string.Empty).Trim(); // We remove the season from the name, as it will be added later.
                            searchName = Regex.Replace(searchName, "[ -\\._]+$", "");
                        }

                        bool success = int.TryParse(isSeasonMatchInPath.Groups[1].Value, out int season);
                        if (!success) continue; // Shouldn't fail since the group 1 in the regex only matches digits but we check just in case the regex is updated in the future.
                                                // 'Series name + season {number}' should return the correct season in anilist for directories following the plex-style structure.
                        searchName = searchName.Trim() + " season " + season;
                    }

                    AnilistMetadataProvider.SeriesStatus? status = Global.MetadataProvider.QuerySeriesByName(searchName);
                    if (status == null) continue; // Can be null if an exception occurs inside the 'QuerySeriesByName' function or Anilist cannot find it by name. If so, we skip.
                    if (status.Episodes <= 0) continue; // No episodes yet, skip.

                    if (status.Finished)
                    {
                        if (fileCount >= status.Episodes)
                        {
                            rowsToDelete.Add(row);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Global.TaskAdmin.Logger.EX_Log($"Failed to process series '{row.name}' at '{row.path}'.", "CleanFinishedSeries");
                    Global.TaskAdmin.Logger.Debug_Log(ex.Message, "CleanFinishedSeries"); // Only prints if Debug == true
                    Global.TaskAdmin.Logger.Debug_Log(ex.StackTrace, "CleanFinishedSeries");
                }
            }
            // Lock the table and clean finished series.
            if (rowsToDelete.Count > 0)
            {
                Global.TaskAdmin.Logger.Log($"{rowsToDelete.Count} series finished and with all episodes downloaded are going to be renoved from the series list.", "CleanFinishedSeries");
                lock (Global.SeriesTable)
                {
                    foreach ((string name, string path) row in rowsToDelete)
                    {
                        // Use Select for efficient lookup; escapes apostrophes in queries.
                        DataRow[]? matchingRows = Global.SeriesTable.Select($"Name = '{row.name.Replace("'", "''")}' AND Path = '{row.path.Replace("'", "''")}'");
                        foreach (DataRow r in matchingRows)
                        {
                            r.Delete();
                        }
                    }
                    Global.SeriesTable.AcceptChanges();
                    Global.SeriesTable.WriteXml(Global.SeriesTableFilePath, XmlWriteMode.WriteSchema);
                }
                foreach (string name in rowsToDelete.Select(x=> x.name))
                {
                    Global.TaskAdmin.Logger.Log($"Removed '{name}' from the SeriesList.", "CleanFinishedSeries");
                }              
            }
            Global.CurrentOpsQueue.Enqueue($"Check for finished series Done.");
        }

        private static bool IsSeasonFolder(string folderName)
        {
            // Matches common Plex-style season folders: S01, Season 1, s.01, season_02, 1, etc.
            // (Simplified version of the regex already used in CleanFinishedSeries)
            return Regex.IsMatch(folderName.Trim(), @"^(?:[Ss](?:eason)?[ \-_\\.]*)*\d{1,3}$", RegexOptions.IgnoreCase);
        }

        private async Task UpdateSeries()
        {
            while (true)
            {
                try
                {
                    List<Series> seriesInTable = [];
                    lock (Global.SeriesTable)
                    {
                        foreach (DataRow row in Global.SeriesTable.Rows)
                        {
                            string? sName = row["Name"].ToString();
                            string? sPath = row["Path"].ToString();
                            string? sFilter = row["Filter"].ToString();
                            bool filterIsInclusive = false;
                            if (row.Table.Columns.Contains("InclusiveFilter"))
                            {
                                string? inclusiveText = row["InclusiveFilter"]?.ToString();
                                filterIsInclusive = string.Equals(inclusiveText, "True", StringComparison.OrdinalIgnoreCase);
                            }
                            _ = int.TryParse(row["Offset"].ToString(), out int sOffset);

                            if (sName == null) { continue; }
                            if (sPath == null) { continue; }
                            if (sFilter == null) { continue; }
                            if (!Directory.Exists(sPath)) Directory.CreateDirectory(sPath);
                            Series series = new(sName, sPath, sOffset, sFilter, filterIsInclusive);
                            seriesInTable.Add(series);
                        }
                    }

                    foreach (Series series in seriesInTable)
                    {
                        CurrentlyScanningSeries = "Scanning : " + series.Name;
                        OnlineEpisodeElement[] filteredEpisodes = FilterFoundEpisodes(await series.GetAvailableSeriesEpisodes(), series);

                        foreach (OnlineEpisodeElement episodeToDownload in filteredEpisodes)
                        {
                            if (episodeToDownload.ProbableEpNumber == null) continue;
                            int episodeNumber = (int)episodeToDownload.ProbableEpNumber;
                            string episodeName = series.Name + " " + episodeNumber.ToString("00");
                            if (CurrentSeriesDownloader.Episodes.ContainsKey(episodeName)) continue;
                            CurrentSeriesDownloader.AddTorrentToDictionary(episodeToDownload.TorrentUrl, series.Path, episodeName, episodeNumber);
                        }
                    }                      
                    seriesInTable.Clear();

                    await Task.Run(() =>
                    {
                        CurrentlyScanningSeries = "Done scanning!";
                        Thread.Sleep(1800000); //Task.Delay() causes a memory leak. It's only one thread so blocking it it's okay.
                    });
                    LoadSeriesTable();
                }
                catch (Exception ex)
                {
                    Global.TaskAdmin.Logger.EX_Log(ex.Message, "UpdateSeries");
                }
            }
        }

        /// <summary>
        /// Searches for uncompleted episodes and series that need conversion by collecting series information from the global series table and search paths,
        /// then processes each series for markers requiring conversion and uncompleted temporary folders.
        /// This method replaces the original SearchForUncompletedEpisodes and SearchForNeedConvertSeries methods.
        /// </summary>
        public void SearchForUncompletedAndNeedConvertSeries()
        {
            // Use HashSet to avoid duplicate series if paths overlap between table and search subdirs
            HashSet<(string name, string path)> seriesRows = [];

            // Load existing entries from Global.SeriesTable (unchanged)
            lock (Global.SeriesTable)
            {
                foreach (DataRow row in Global.SeriesTable.Rows)
                {
                    string? seriesName = row["Name"]?.ToString();
                    string? seriesPath = row["Path"]?.ToString();
                    if (string.IsNullOrWhiteSpace(seriesName) || string.IsNullOrWhiteSpace(seriesPath))
                    {
                        continue;
                    }
                    seriesRows.Add((seriesName, seriesPath));
                }
            }

            // Discover from SearchPaths
            foreach (string searchPath in Settings.SearchPaths)
            {
                if (!Directory.Exists(searchPath)) continue;
                if (File.Exists(Path.Combine(searchPath, ".ADIngnore"))) continue;


                foreach (string potentialSeriesDir in Directory.EnumerateDirectories(searchPath))
                {
                    if (!Directory.Exists(potentialSeriesDir)) continue;

                    string basePath = Path.GetFullPath(potentialSeriesDir);
                    string baseName = new DirectoryInfo(potentialSeriesDir).Name;

                    // Always add the top-level series folder (flat structure support)
                    seriesRows.Add((baseName, basePath));

                    // NEW: Look one level deeper for season subfolders (S01, Season 02, etc.)
                    foreach (string seasonDir in Directory.EnumerateDirectories(basePath))
                    {
                        string seasonFolderName = new DirectoryInfo(seasonDir).Name;
                        if (IsSeasonFolder(seasonFolderName))
                        {
                            string fullSeasonPath = Path.GetFullPath(seasonDir);
                            string seriesNameWithSeason = baseName.Trim() + " " + seasonFolderName;
                            seriesRows.Add((seriesNameWithSeason, fullSeasonPath));
                        }
                    }
                }
            }

            // Process every discovered series (including seasons)
            foreach ((string name, string path) row in seriesRows)
            {
                Global.CurrentOpsQueue.Enqueue($"Searching unconverted files and marked series for {row.name}");
                if (!Directory.Exists(row.path))
                {
                    continue;
                }
                SearchAndProcessDirectoryMarker(row.name, row.path);
                SearchAndProcessNonH264Videos(row.name, row.path);
                SearchAndProcessUncompleted(row.path);
            }

            UpdateSeriesDataTable();
            Global.CurrentOpsQueue.Enqueue("Search for unconverted files and marked series done.");
        }

        /// <summary>
        /// Processes a series directory if it contains a marker file indicating the need for conversion.
        /// This includes probing the directory for writability, moving valid video files into per-episode temporary folders,
        /// adding them as downloaded episodes, and deleting the marker file upon success.
        /// </summary>
        /// <param name="seriesName">The name of the series.</param>
        /// <param name="seriesPath">The full path to the series directory.</param>
        public void SearchAndProcessDirectoryMarker(string seriesName, string seriesPath)
        {
            string markerPath = Path.Combine(seriesPath, Settings.NeedsConvertFileName);
            if (!File.Exists(markerPath)) return;
            if (String.IsNullOrWhiteSpace(seriesName))
            {
                Global.TaskAdmin.Logger.Log($"Skipped '{seriesPath}'. DirectoryInfo for the path returned null or an empty string.", "SearchAndProcessDirectoryMarker");
                return;
            }
            try
            {
                // Probe the directory by creating and deleting a test file to ensure writability
                string probeFile = Path.Combine(seriesPath, "probe");
                bool probeOk = true;
                if (File.Exists(probeFile))
                {
                    probeOk &= DeleteFileWithRetries(probeFile, 3);
                }
                File.Create(probeFile).Close();
                probeOk = probeOk && DeleteFileWithRetries(probeFile, 3);
                if (!probeOk) throw new Exception("DeleteFileWithRetries failed.");
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.EX_Log($"Probing for '{seriesPath}' failed. Skipping Subdirectory. Exception: {ex.Message}.", "SearchAndProcessDirectoryMarker");
                return;
            }
            try
            {
                // Get all files once
                string[] videoFiles = [.. Settings.ValidExtensions.SelectMany(extension => Directory.EnumerateFiles(seriesPath, "*" + extension))]; //Settings.ValidExtensions <- lowercase hashset of extensions with dot.
                if (videoFiles.Length == 0)
                {
                    Global.TaskAdmin.Logger.Log($"Deleting marker '{markerPath}' for empty series '{seriesName}' in path '{seriesPath}'. No valid video files found!", "SearchAndProcessDirectoryMarker");
                    if (!DeleteFileWithRetries(markerPath, 3))
                    {
                        Global.TaskAdmin.Logger.EX_Log($"Failed to delete marker '{markerPath}'", "SearchAndProcessDirectoryMarker");
                    }
                    return;
                }
                int queuedCount = 0;
                foreach (string file in videoFiles)
                {
                    string episodeName = Path.GetFileNameWithoutExtension(file).Trim();
                    int? episodeNumber = OnlineEpisodeElement.GetEpNumberFromString(episodeName); //Method catches exceptions, always returns a number or null.
                    if (episodeNumber == null)
                    {
                        Global.TaskAdmin.Logger.Log($"Skipped '{file}'. No episode number found.", "SearchAndProcessDirectoryMarker");
                        continue;
                    }
                    // Format episode number as 00 or 000 based on total video files to ensure consistent naming (e.g., for sorting)
                    string epNumberString = String.Format("{0:00}", episodeNumber);
                    if (videoFiles.Length > 99)
                    {
                        epNumberString = String.Format("{0:000}", episodeNumber);
                    }
                    string destEpisodeExtension = Path.GetExtension(file).ToLowerInvariant();
                    string destEpisodeName = seriesName + " " + epNumberString;
                    string tempFolder = Path.Combine(seriesPath, destEpisodeName) + ".temp"; //Temp folder is per-episode, not per series. When episode processing is completed the folder is deleted.
                    string destEpisodePath = Path.Combine(tempFolder, destEpisodeName + destEpisodeExtension);
                    try
                    {
                        if (Directory.Exists(tempFolder))
                        {
                            Directory.Delete(tempFolder, true);
                            Global.TaskAdmin.Logger.Log($"Pre-existent temporary folder '{tempFolder}' was deleted.", "SearchAndProcessDirectoryMarker");
                        }
                        Directory.CreateDirectory(tempFolder);
                    }
                    catch (Exception ex)
                    {
                        Global.TaskAdmin.Logger.EX_Log($"Failed to delete temp dir '{tempFolder}'. Exception: {ex.Message}", "SearchAndProcessDirectoryMarker");
                        continue;
                    }
                    if (!MoveFileWithRetries(file, destEpisodePath, 3))
                    {
                        Global.TaskAdmin.Logger.Log($"Skipped '{file}'. Failed to move to temp folder.", "SearchAndProcessDirectoryMarker");
                        continue;
                    }
                    AddEpisode(destEpisodeName, seriesPath, (int)episodeNumber, State.DownloadedFound, "Downloaded-Found"); //DownloadedFound = DownloadedSeeding. Added recently.
                    queuedCount++;
                }
                if (queuedCount > 0)
                {
                    Global.TaskAdmin.Logger.Log($"Queued {queuedCount} files of {videoFiles.Length} in '{seriesPath}' for Series '{seriesName}'.", "SearchAndProcessDirectoryMarker");
                    if (queuedCount != videoFiles.Length)
                    {
                        Global.TaskAdmin.Logger.Log($"Unprocessed files in '{seriesPath}' may not satisfy requirements, check filenames, extensions and Logs.", "SearchAndProcessDirectoryMarker");
                        
                    }
                }
                if (!DeleteFileWithRetries(markerPath, 3))
                {
                    Global.TaskAdmin.Logger.EX_Log($"Failed to delete marker '{markerPath}'", "SearchAndProcessDirectoryMarker");
                    return;
                }
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.EX_Log($"Error processing '{seriesPath}': {ex.Message}", "SearchAndProcessDirectoryMarker");
                Global.TaskAdmin.Logger.EX_Log($"Stack trace: {ex.StackTrace}", "SearchAndProcessDirectoryMarker");
            }
        }

        /// <summary>
        /// Scans a series directory for video files that are not H.264 (or that cannot be probed)
        /// and queues them for conversion using the same rename-to-canonical-name logic as the
        /// .needsconvert marker routine. This keeps final filenames consistent with the rest of
        /// the downloader (SeriesName 01.ext, SeriesName 02.ext, ...).
        /// Files already inside a .temp folder or already tracked are skipped.
        /// </summary>
        public void SearchAndProcessNonH264Videos(string seriesName, string seriesPath)
        {
            if (string.IsNullOrWhiteSpace(seriesName) || !Directory.Exists(seriesPath))
                return;

            try
            {
                // Only top-level files (same as the marker path). .temp folders are handled separately.
                string[] videoFiles = [.. Settings.ValidExtensions
                    .SelectMany(extension => Directory.EnumerateFiles(seriesPath, "*" + extension))];

                if (videoFiles.Length == 0)
                    return;

                int queuedCount = 0;
                foreach (string file in videoFiles)
                {
                    // Skip anything that is already being tracked or that lives under a temp dir
                    string episodeNameCandidate = Path.GetFileNameWithoutExtension(file).Trim();
                    if (CurrentSeriesDownloader.Episodes.ContainsKey(episodeNameCandidate))
                        continue;

                    // Fast path: a valid .h264ok marker means we already probed this exact
                    // file (same size + mtime) and confirmed it is H.264. Skip the expensive
                    // ffprobe call on every 5-minute scan cycle.
                    if (SeriesDownloader.IsFileVerifiedH264(file))
                        continue;

                    // Probe codec – only process when it is NOT h264 (or probe failed).
                    // On first successful confirmation we write the marker so later cycles
                    // can take the fast path above.
                    if (SeriesDownloader.IsH264Video(file))
                    {
                        SeriesDownloader.MarkFileAsVerifiedH264(file);
                        continue;
                    }

                    int? episodeNumber = OnlineEpisodeElement.GetEpNumberFromString(episodeNameCandidate);
                    if (episodeNumber == null)
                    {
                        Global.TaskAdmin.Logger.Log($"Skipped non-H264 '{file}'. No episode number found.", "SearchAndProcessNonH264Videos");
                        continue;
                    }

                    // Same naming convention used by the .needsconvert path so the rest of the
                    // pipeline (final file name, GetEpisodesDownloaded regex, Plex, etc.) stays consistent.
                    string epNumberString = String.Format("{0:00}", episodeNumber);
                    if (videoFiles.Length > 99)
                    {
                        epNumberString = String.Format("{0:000}", episodeNumber);
                    }
                    string destEpisodeExtension = Path.GetExtension(file).ToLowerInvariant();
                    string destEpisodeName = seriesName + " " + epNumberString;
                    string tempFolder = Path.Combine(seriesPath, destEpisodeName) + ".temp";
                    string destEpisodePath = Path.Combine(tempFolder, destEpisodeName + destEpisodeExtension);

                    // If a final file with the canonical name already exists and is itself H.264 we
                    // leave the original alone; otherwise we still move & re-encode.
                    string possibleFinal = Path.Combine(seriesPath, destEpisodeName + destEpisodeExtension);
                    if (File.Exists(possibleFinal))
                    {
                        if (SeriesDownloader.IsFileVerifiedH264(possibleFinal) || SeriesDownloader.IsH264Video(possibleFinal))
                        {
                            if (!SeriesDownloader.IsFileVerifiedH264(possibleFinal))
                                SeriesDownloader.MarkFileAsVerifiedH264(possibleFinal);
                            // There is already a good final file; skip this source to avoid unnecessary work.
                            continue;
                        }
                    }

                    try
                    {
                        if (Directory.Exists(tempFolder))
                        {
                            Directory.Delete(tempFolder, true);
                            Global.TaskAdmin.Logger.Log($"Pre-existent temporary folder '{tempFolder}' was deleted.", "SearchAndProcessNonH264Videos");
                        }
                        Directory.CreateDirectory(tempFolder);
                    }
                    catch (Exception ex)
                    {
                        Global.TaskAdmin.Logger.EX_Log($"Failed to prepare temp dir '{tempFolder}'. Exception: {ex.Message}", "SearchAndProcessNonH264Videos");
                        continue;
                    }

                    if (!MoveFileWithRetries(file, destEpisodePath, 3))
                    {
                        Global.TaskAdmin.Logger.Log($"Skipped non-H264 '{file}'. Failed to move to temp folder.", "SearchAndProcessNonH264Videos");
                        continue;
                    }

                    AddEpisode(destEpisodeName, seriesPath, (int)episodeNumber, State.DownloadedFound, "Downloaded-Found (non-H264)");
                    queuedCount++;
                }

                if (queuedCount > 0)
                {
                    Global.TaskAdmin.Logger.Log($"Queued {queuedCount} non-H264 file(s) in '{seriesPath}' for Series '{seriesName}'.", "SearchAndProcessNonH264Videos");
                }
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.EX_Log($"Error processing non-H264 videos in '{seriesPath}': {ex.Message}", "SearchAndProcessNonH264Videos");
                Global.TaskAdmin.Logger.Debug_Log(ex.StackTrace, "SearchAndProcessNonH264Videos");
            }
        }

        /// <summary>
        /// Scans a series directory for temporary (.temp) episode folders and adds uncompleted episodes to the downloader
        /// based on their state files, skipping those already in the episodes dictionary.
        /// </summary>
        /// <param name="seriesPath">The full path to the series directory.</param>
        public void SearchAndProcessUncompleted(string seriesPath)
        {
            foreach (string tempDirPath in Directory.GetDirectories(seriesPath, "*.temp"))
            {
                string episodeName = Path.GetFileNameWithoutExtension(tempDirPath).Trim();
                Match tempFolderMatch = TempFolderEpisodeNumberRegex().Match(episodeName);
                if (!tempFolderMatch.Success) continue;
                if (!int.TryParse(tempFolderMatch.Value, out int episodeNumber)) continue; //Value can not be null because we guarded against match success.
                // Skip if episode is already being tracked in the current downloader
                if (CurrentSeriesDownloader.Episodes.ContainsKey(episodeName)) continue;
                // Check for episode states to determine if it's downloaded or encoded
                if (File.Exists(Path.Combine(tempDirPath, "state.DownloadedSeeding")) ||
                    File.Exists(Path.Combine(tempDirPath, "state.ReEncoding")))
                {
                    AddEpisode(episodeName, seriesPath, episodeNumber, State.DownloadedFound, "Downloaded-found");
                }
                else if (File.Exists(Path.Combine(tempDirPath, "state.EncodedSeeding")) ||
                         File.Exists(Path.Combine(tempDirPath, "state.EncodedFound")))
                {
                    AddEpisode(episodeName, seriesPath, episodeNumber, State.EncodedFound, "Encoded-found");
                }
            }
        }


        public static bool MoveFileWithRetries(String source, String destination, int tries)
        {

            for (int retry = 0; retry < tries; retry++)  // Retry on lock
            {
                try
                {
                    File.Move(source, destination);
                    return true;
                }
                catch (IOException ex) when (retry < tries - 1)
                {
                    Global.TaskAdmin.Logger.EX_Log($"Retrying move for '{source}': {ex.Message}", "MoveFileWithRetries");
                    Thread.Sleep(1000);  // 1s delay
                }
                catch (IOException ex)
                {
                    Global.TaskAdmin.Logger.EX_Log($"Failed to move '{source}': {ex.Message}", "MoveFileWithRetries");
                }
            }

            return false;
        }

        public static bool DeleteFileWithRetries(String file, int tries)
        {

            for (int retry = 0; retry < tries; retry++)  // Retry on lock
            {
                try
                {
                    File.Delete(file);
                    return true;
                }
                catch (IOException ex) when (retry < tries - 1)
                {
                    Global.TaskAdmin.Logger.EX_Log($"Retrying delete for '{file}': {ex.Message}", "DeleteFileWithRetries");
                    Thread.Sleep(1000);  // 1s delay
                }
                catch (IOException ex)
                {
                    Global.TaskAdmin.Logger.EX_Log($"Failed to delete '{file}': {ex.Message}", "DeleteFileWithRetries");
                }
            }

            return false;
        }

        private void AddEpisode(string episodeName, string seriesPath, int episodeNumber, State state, string status)
        {
            EpisodeToDownload episode = new("", episodeName, seriesPath, episodeNumber);
            episode.SetState(state);
            episode.StatusDescription = status;
            CurrentSeriesDownloader.AddFoundEpisodeToDictionary(episode);
        }

        private static OnlineEpisodeElement[] FilterFoundEpisodes(OnlineEpisodeElement[] episodes, Series series)
        {
            Dictionary<int, OnlineEpisodeElement> bestEpisodes = [];
            List<OnlineEpisodeElement> preFilteredEpisodes = [];

            int[] downloadedEpisodes = series.GetEpisodesDownloaded();

            foreach (OnlineEpisodeElement episode in episodes)
            {
                if (episode.ProbableEpNumber == null || downloadedEpisodes.Contains((int)episode.ProbableEpNumber))
                {
                    continue;
                }
                preFilteredEpisodes.Add(episode);
            }

            foreach (OnlineEpisodeElement episode in preFilteredEpisodes)
            {
                if (episode.ProbableEpNumber == null || episode.ProbableLang == Lang.RAW) continue;
                episode.ProbableLang = episode.ProbableLang == Lang.Undefined ? episode.GetProbableLanguage() : episode.ProbableLang;
                if (Settings.UseCustomLanguage)
                {
                    if (episode.ProbableLang != Lang.Custom && episode.ProbableLang != Lang.CustomAndEng) continue;
                }
                else
                {
                    if (episode.ProbableLang != Lang.Eng) continue;
                }               

                int epNum = (int)episode.ProbableEpNumber;

                if (!bestEpisodes.TryAdd(epNum, episode))
                {
                    if (episode.SizeMiB > bestEpisodes[epNum].SizeMiB || Global.TrySelectUncensoredEpisode(episode, bestEpisodes[epNum]) == episode)
                    {
                        bestEpisodes[epNum] = episode;
                    }
                }
            }

            return [.. bestEpisodes.Values];
        }

        public static void SetUpdateEpisodesStatusTable(SeriesDownloader.EpisodeToDownload episodeElement)
        {
            DataRow? row;
            lock (Global.CurrentStatusTable)
            {
                row = Global.CurrentStatusTable.AsEnumerable().Where(dr => dr.Field<string>("Episode") == episodeElement.Name).FirstOrDefault();
            }

            if (!(row == null))
            {
                lock (Global.CurrentStatusTable)
                {
                    lock (row)
                    {
                        row[1] = episodeElement.Name;
                        row[2] = episodeElement.StatusDescription;
                        if (episodeElement.GetState == State.EncodedSeeding)
                        {                          
                            row[3] = "R:" + episodeElement.GetTorrentRatio().ToString("0.00");
                        }
                        else
                        {
                            row[3] = episodeElement.StatusPercentage;
                        }                        
                    }
                }
            }
            else
            {
                lock (Global.CurrentStatusTable)
                {
                    Global.CurrentStatusTable.Rows.Add(episodeElement.TorrentName, episodeElement.Name, episodeElement.StatusDescription, episodeElement.StatusPercentage);
                }
            }
        }

        /// <summary>
        /// Re-reads worker.claim files from disk at most every WorkerClaimRefreshMs.
        /// Results are cached for the frequent 200ms status-table rebuild.
        /// </summary>
        private void RefreshRemoteClaimsCacheIfDue()
        {
            int refreshMs = Math.Max(500, Settings.WorkerClaimRefreshMs);
            if ((DateTime.UtcNow - _lastRemoteClaimScanUtc).TotalMilliseconds < refreshMs)
                return;

            List<string> seriesPaths = [];
            lock (Global.SeriesTable)
            {
                foreach (DataRow row in Global.SeriesTable.Rows)
                {
                    string? path = row["Path"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(path))
                        seriesPaths.Add(path);
                }
            }
            foreach (string searchPath in Settings.SearchPaths)
            {
                if (!string.IsNullOrWhiteSpace(searchPath) && Directory.Exists(searchPath))
                    seriesPaths.Add(searchPath);
            }

            List<EncodingClaim> fresh = EncodingClaim.FindActiveRemoteClaims(seriesPaths.Distinct());
            lock (_remoteClaimsLock)
            {
                _cachedRemoteClaims = fresh;
                _lastRemoteClaimScanUtc = DateTime.UtcNow;
            }
        }

        private void UpdateSeriesDataTable()
        {
            List<string> episodes = [];
            
            lock (CurrentSeriesDownloader.Episodes)
            {
                foreach (KeyValuePair<string, SeriesDownloader.EpisodeToDownload> pair in CurrentSeriesDownloader.Episodes)
                {
                    SeriesDownloader.EpisodeToDownload episode = pair.Value;
                    SetUpdateEpisodesStatusTable(episode);
                    episodes.Add(episode.Name);
                }
            }

            // Merge remote worker progress. The status table is rebuilt every 200ms for a
            // responsive console, but the filesystem scan of worker.claim files is
            // throttled (WorkerClaimRefreshMs, default 2s) so we do not walk every
            // series directory on each tick.
            RefreshRemoteClaimsCacheIfDue();

            List<EncodingClaim> remoteClaims;
            lock (_remoteClaimsLock)
            {
                remoteClaims = [.. _cachedRemoteClaims];
            }

            foreach (EncodingClaim claim in remoteClaims)
            {
                string episodeName = string.IsNullOrWhiteSpace(claim.EpisodeName)
                    ? claim.Source
                    : claim.EpisodeName;
                if (string.IsNullOrWhiteSpace(episodeName))
                    continue;
                if (episodes.Contains(episodeName))
                    continue; // already tracked locally

                string status = FormatWorkerStatus(claim.WorkerId, claim.Status);
                string progress = claim.Progress.ToString("0.##");
                lock (Global.CurrentStatusTable)
                {
                    DataRow? existing = Global.CurrentStatusTable.AsEnumerable()
                        .FirstOrDefault(dr => dr.Field<string>("Episode") == episodeName);
                    if (existing != null)
                    {
                        existing[0] = claim.WorkerId;
                        existing[1] = episodeName;
                        existing[2] = status;
                        existing[3] = progress;
                    }
                    else
                    {
                        Global.CurrentStatusTable.Rows.Add(claim.WorkerId, episodeName, status, progress);
                    }
                }
                episodes.Add(episodeName);
            }
            
            lock (Global.CurrentStatusTable)
            {
                using DataTable filteredDataTable = Global.CurrentStatusTable.Clone();
                foreach (DataRow row in Global.CurrentStatusTable.AsEnumerable())
                {
                    if (row == null) continue;
                    string? value = row.Field<string>("Episode");
                    if (value == null) continue;
                    if (episodes.Contains(value))
                    {
                        filteredDataTable.ImportRow(row);
                    }
                }
                Global.CurrentStatusTable.Rows.Clear();
                foreach (DataRow row in filteredDataTable.Rows)
                {
                    Global.CurrentStatusTable.Rows.Add(row.ItemArray);
                }
                
            }

            OverrideStatusTableWithWorkersData();

            string html = Webserver.ConvertStatusDataTableToHTML(Global.CurrentStatusTable);
            if (!string.IsNullOrEmpty(html)) Global.PublishStatusHtmlSnapshot(html);

        }

        /// <summary>
        /// Formats a worker status for the status table. When the encoder matches the main
        /// instance (this process's worker id), only the status is shown. Remote workers are
        /// prefixed with Remote [workerId].
        /// </summary>
        private static string FormatWorkerStatus(string workerId, string status)
        {
            string localId = EncodingClaim.GetLocalWorkerId();
            if (string.Equals(workerId, localId, StringComparison.OrdinalIgnoreCase))
                return status;
            return $"Remote [{workerId}] {status}";
        }

        private static void OverrideStatusTableWithWorkersData()
        {
            List<string> workerFiles = [];
            foreach (string dir in Settings.SearchPaths)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (string workerFile in Directory.GetFiles(dir, "*.worker"))
                        workerFiles.Add(workerFile);
                }
                catch { /* ignore inaccessible paths */ }
            }

            foreach (string workerFile in workerFiles)
            {
                try
                {
                    string[] content = File.ReadAllLines(workerFile);
                    if (content.Length < 4) continue;

                    string workerID = content[0];
                    string tempFolder = content[1];
                    if (!double.TryParse(content[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double progress))
                        continue;
                    string status = content[3];

                    // Leftover *.worker files used to keep "Encoding" on the series list after
                    // the claim was released. Only a fresh claim without a finished state counts.
                    if (!EncodingClaim.IsWorkerProgressCurrent(tempFolder))
                    {
                        EncodingClaim.ClearWorkerProgressFiles(tempFolder);
                        continue;
                    }

                    // Platform-agnostic last path component (handles \ and /)
                    string normalized = tempFolder.Replace('\\', '/').TrimEnd('/');
                    string episodeName = normalized.Contains('/')
                        ? normalized[(normalized.LastIndexOf('/') + 1)..]
                        : normalized;

                    if (episodeName.EndsWith(".temp", StringComparison.OrdinalIgnoreCase))
                        episodeName = episodeName[..^5];

                    if (string.IsNullOrWhiteSpace(episodeName)) continue;

                    lock (Global.CurrentStatusTable)
                    {
                        foreach (DataRow row in Global.CurrentStatusTable.AsEnumerable())
                        {
                            if (row == null) continue;
                            string? value = row.Field<string>("Episode");
                            if (value == null) continue;

                            if (string.Equals(value.Trim(), episodeName.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                row.SetField("Status", FormatWorkerStatus(workerID, status));
                                row.SetField("Progress", progress);
                            }
                        }
                        Global.CurrentStatusTable.DefaultView.Sort = "Progress DESC";
                    }
                }
                catch (Exception ex)
                {
                    Global.TaskAdmin.Logger.Debug_Log($"OverrideStatusTableWithWorkersData failed for '{workerFile}': {ex.Message}", "Program");
                }
            }
        }

        private void PrintUpdateTable()
        {
            string consoleText = GetConsoleText();
            string currentSeries = PrepareCurrentSeries(consoleText);
            string currentOpsString = GetCurrentOpsString();

            currentOpsString = MatchStringLenghtWithSpaces(currentOpsString, consoleText.Split('\n')[0]);
            consoleText += '\n' + currentOpsString;

            if (ShouldUpdateConsole(consoleText))
            {
                consoleText = UpdateConsole(consoleText);
            }

            consoleText += string.Join('\n', Enumerable.Repeat("", 2).Select(s => MatchStringLenghtWithSpaces(s, consoleText.Split('\n')[0])));

            DisplayConsoleText(consoleText);
        }

        private static string GetConsoleText()
        {
            string consoleText = string.Empty;
            lock (Global.CurrentStatusTable)
            {
                consoleText += Global.CurrentStatusTable.ToPrettyPrintedString();
            }
            return consoleText;
        }

        private string PrepareCurrentSeries(string consoleText)
        {
            return MatchStringLenghtWithSpaces(CurrentlyScanningSeries, consoleText.Split('\n')[0]);
        }

        private static string GetCurrentOpsString()
        {
            ConcurrentQueue<string> queue = Global.CurrentOpsQueue;
            if (queue.Count > 5)
            {
                while (queue.Count > 1)
                {
                    queue.TryDequeue(out _);
                }
                queue.TryPeek(out string? result);
                return result ?? String.Empty;
            }
            else
            {
                if (queue.Count < 2)
                {
                    queue.TryPeek(out string? result);
                    return result ?? String.Empty;
                }
                else
                {
                    queue.TryDequeue(out string? result);
                    return result ?? String.Empty;
                }
            }
        }

        private bool ShouldUpdateConsole(string consoleText)
        {
            int currentLineCount = consoleText.Split('\n').Length;
            return currentLineCount != PreviousLineCount ||
                   Console.BufferWidth != PreviousWindowWidth ||
                   Console.BufferHeight != PreviousWindowHeight;
        }

        private string UpdateConsole(string consoleText)
        {
            PreviousLineCount = consoleText.Split('\n').Length;
            PreviousWindowHeight = Console.BufferHeight;
            PreviousWindowWidth = Console.BufferWidth;
            try
            {
                Console.Clear();
            }
            catch (IOException ex)
            {
                consoleText += "\n[CLS EX:]" + ex.Message;
                Global.TaskAdmin.Logger.EX_Log(ex.Message, "UpdateConsole");
            }
            return consoleText;
        }

        private static void DisplayConsoleText(string consoleText)
        {
            Console.SetCursorPosition(0, 0);
            Console.CursorVisible = false;
            Console.Write(consoleText);
        }

        public static string MatchStringLenghtWithSpaces(string stringToMatch, string stringToGetLenght)
        {
            if (stringToMatch.Length > stringToGetLenght.Length) return stringToMatch;
            while (stringToMatch.Length < stringToGetLenght.Length)
            {
                stringToMatch += " ";
            }
            return stringToMatch;
        }

        public static void LoadSeriesTable()
        {            
            Global.SeriesTable.Clear();

            if (File.Exists(Global.SeriesTableFilePath))
            {
                lock (Global.SeriesTable)
                {
                    try
                    {
                        Global.SeriesTable.ReadXml(Global.SeriesTableFilePath);
                    }
                    catch (Exception ex)
                    {
                        Global.TaskAdmin.Logger.EX_Log(ex.Message, "LoadSeriesTable");
                    }
                }               
            }
            else
            {
                lock (Global.SeriesTable) {
                    if (!Global.SeriesTable.Columns.Contains("Name"))
                    {
                        DataColumn[] keys = new DataColumn[1];
                        DataColumn SeriesColumn = new("Name", typeof(string));
                        Global.SeriesTable.Columns.Add(SeriesColumn);
                        keys[0] = SeriesColumn;
                        Global.SeriesTable.PrimaryKey = keys;
                    }

                    if (!Global.SeriesTable.Columns.Contains("Path"))
                    {
                        DataColumn SeriesPath = new("Path", typeof(string));
                        Global.SeriesTable.Columns.Add(SeriesPath);
                    }

                    if (!Global.SeriesTable.Columns.Contains("Offset"))
                    {
                        DataColumn Offset = new("Offset", typeof(string));
                        Global.SeriesTable.Columns.Add(Offset);
                    }

                    if (!Global.SeriesTable.Columns.Contains("Filter"))
                    {
                        DataColumn Filter = new("Filter", typeof(string));
                        Global.SeriesTable.Columns.Add(Filter);
                    }

                    if (!Global.SeriesTable.Columns.Contains("InclusiveFilter"))
                    {
                        DataColumn InclusiveFilter = new("InclusiveFilter", typeof(string));
                        Global.SeriesTable.Columns.Add(InclusiveFilter);
                    }
               }                         
            }
            lock (Global.SeriesTable)
            {
                if (!Global.SeriesTable.Columns.Contains("InclusiveFilter"))
                {
                    DataColumn InclusiveFilter = new("InclusiveFilter", typeof(string));
                    Global.SeriesTable.Columns.Add(InclusiveFilter);
                }
            }
            lock (Global.CurrentStatusTable)
            {
                Global.CurrentStatusTable.Clear();

                if (!Global.CurrentStatusTable.Columns.Contains("Torrent ID")){
                    Global.CurrentStatusTable.Columns.Add(new DataColumn("Torrent ID", typeof(string)));
                }

                if (!Global.CurrentStatusTable.Columns.Contains("Episode")){
                    Global.CurrentStatusTable.Columns.Add(new DataColumn("Episode", typeof(string)));
                }

                if (!Global.CurrentStatusTable.Columns.Contains("Status")){
                    Global.CurrentStatusTable.Columns.Add(new DataColumn("Status", typeof(string)));
                }

                if (!Global.CurrentStatusTable.Columns.Contains("Progress")){
                    Global.CurrentStatusTable.Columns.Add(new DataColumn("Progress", typeof(string)));
                }

                Global.CurrentStatusTable.Columns[0].SetWidth(20);
                Global.CurrentStatusTable.Columns[1].SetWidth(55);
                Global.CurrentStatusTable.Columns[2].SetWidth(30);
                Global.CurrentStatusTable.Columns[3].SetWidth(10);
            }
            string html = Webserver.ConvertStatusDataTableToHTML(Global.CurrentStatusTable);
            if (!string.IsNullOrEmpty(html)) Global.PublishStatusHtmlSnapshot(html);
        }
    }
}



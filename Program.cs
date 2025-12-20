using DataTablePrettyPrinter;
using System.Collections.Concurrent;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;
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

        public static readonly Settings settings = new();
        public static readonly Webserver webserver = new();

        [GeneratedRegex(@"\d{1,3}$")]
        private static partial Regex TempFolderEpisodeNumberRegex();

        public static void Main()
        {
            settings.Init();
            Program program = new();
            webserver.Init();            
            var task = Task.Run(async () => { await program.Start(); });
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

            bool StartDownloadsTask()
            {
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
                            _ = int.TryParse(row["Offset"].ToString(), out int sOffset);

                            if (sName == null) { continue; }
                            if (sPath == null) { continue; }
                            if (sFilter == null) { continue; }
                            Global.CurrentOpsQueue.Enqueue("Checking " + sName);
                            if (!Directory.Exists(sPath)) Directory.CreateDirectory(sPath);
                            Series series = new(sName, sPath, sOffset, sFilter);
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
            foreach (String path in Settings.SearchPaths)
            {
                foreach (String subDir in Directory.EnumerateDirectories(path))
                {
                    if (String.IsNullOrWhiteSpace(subDir)) continue;
                    if (!Directory.Exists(subDir)) continue;
                    string seriesName = new DirectoryInfo(subDir).Name;
                    string? seriesPath = Path.GetFullPath(subDir);
                    if (seriesName == null || seriesPath == null) continue;
                    seriesRows.Add((seriesName, seriesPath));
                }
            }
            foreach ((string name, string path) row in seriesRows)
            {
                string seriesName = row.name;
                string seriesPath = row.path;
                Global.CurrentOpsQueue.Enqueue($"Searching unconverted files and marked series for {seriesName}");
                if (!Directory.Exists(seriesPath))
                {
                    continue;
                }
                SearchAndProcessDirectoryMarker(seriesName, seriesPath);
                SearchAndProcessUncompleted(seriesPath);
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
                    int? episodeNumber = OnlineEpisodeElement.GetEpNumberFromString(episodeName); //Method catches exceptions, always returns a number of null.
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
                    string tempFolder = Path.Combine(seriesPath, destEpisodeName) + ".temp"; //Temp folder is per-episode, not per series. When episode processing is complete the folder is deleted.
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
            // Clean up existing file if necessary
            string episodePath = Path.Combine(seriesPath, episodeName);
            string mkvFile = episodePath + ".mkv";
            string mp4File = episodePath + ".mp4";
            if (File.Exists(mkvFile))
            {
                if (!DeleteFileWithRetries(mkvFile, 3)) {
                    Global.TaskAdmin.Logger.EX_Log($"Could not Add '{episodeName}' because another file already exists and could not be deleted. ", "AddEpisode");
                    return;
                }
                
            }
            if (File.Exists(mp4File))
            {
                if (!DeleteFileWithRetries(mp4File, 3))
                {
                    Global.TaskAdmin.Logger.EX_Log($"Could not Add '{episodeName}' because another file already exists and could not be deleted. ", "AddEpisode");
                    return;
                }
            }

            var episode = new SeriesDownloader.EpisodeToDownload("", episodeName, seriesPath, episodeNumber);
            episode.SetState(state);
            episode.StatusDescription = status;
            CurrentSeriesDownloader.AddFoundEpisodeToDictionary(episode); //AddFoundEpisodeToDictionary has lock for Episodes dictionary inside.
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
            var queue = Global.CurrentOpsQueue;
            if (queue.Count > 5)
            {
                while (queue.Count > 1)
                {
                    queue.TryDequeue(out _);
                }
                queue.TryPeek(out var result);
                return result ?? String.Empty;
            }
            else
            {
                if (queue.Count < 2)
                {
                    queue.TryPeek(out var result);
                    return result ?? String.Empty;
                }
                else
                {
                    queue.TryDequeue(out var result);
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
        }
    }
}


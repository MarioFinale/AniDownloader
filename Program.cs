using DataTablePrettyPrinter;
using System.Data;
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
            Program program = new();
            settings.Init();
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

            bool SearchForUncompletedEpisodesTask()
            {
                SearchForUncompletedEpisodes();
                return true;
            };

            Global.TaskAdmin.NewTask("SearchForUncompletedEpisodes", "Downloader", SearchForUncompletedEpisodesTask, 60000, true);

            bool PrintUpdateTableTask()
            {
                PrintUpdateTable();
                return true;
            };
            Global.TaskAdmin.NewTask("PrintUpdateTable", "Downloader", PrintUpdateTableTask, 100, true);

            bool SearchForNeedConvertSeriesTask()
            {
                SearchForNeedConvertSeries();
                return true;
            }
            Global.TaskAdmin.NewTask("SearchForNeedConvertSeriesTask", "Downloader", SearchForNeedConvertSeriesTask, 60000, true);

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

        public void SearchForUncompletedEpisodes()
        {
            List<Tuple<String, String>> seriesRows = [];
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
                    Tuple<String, String> tup = Tuple.Create(seriesName, seriesPath);
                    seriesRows.Add(tup);
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
                    Tuple<String, String> tup = Tuple.Create(seriesName, seriesPath);
                    seriesRows.Add(tup);
                }              
            }

            foreach (Tuple<String, String> row in seriesRows)
            {
                string seriesName = row.Item1;
                string seriesPath = row.Item2;

                Global.CurrentOpsQueue.Enqueue($"Searching unconverted files for {seriesName}");

                if (!Directory.Exists(seriesPath))
                {
                    continue;
                }

                foreach (string tempDirPath in Directory.GetDirectories(seriesPath, "*.temp"))
                {
                    string episodeName = Path.GetFileNameWithoutExtension(tempDirPath).Trim();

                    if (!int.TryParse(TempFolderEpisodeNumberRegex().Match(episodeName).Value, out int episodeNumber)) continue;
                    if (CurrentSeriesDownloader.Episodes.ContainsKey(episodeName)) continue;

                    // Check for episode states
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

            Global.CurrentOpsQueue.Enqueue("Unconverted files search done.");
        }

        private void SearchForNeedConvertSeries()
        {
            var validExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4" };  // Align with Series.cs

            foreach (string rootPath in Settings.SearchPaths)
            {
                if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) continue;

                foreach (string subDir in Directory.EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly))  // Lazy, non-recursive
                {
                    Global.CurrentOpsQueue.Enqueue($"Searching unconverted files in {subDir}");
                    string markerPath = Path.Combine(subDir, Settings.NeedsConvertFileName);
                    if (!File.Exists(markerPath)) continue;

                    string seriesName = new DirectoryInfo(subDir).Name;
                    if (String.IsNullOrWhiteSpace(seriesName))
                    {
                        Global.TaskAdmin.Logger.Log($"Skipped '{subDir}'. DirectoryInfo for the path returned null or an empty string.", "SearchForNeedConvertSeries");
                        continue;
                    }

                    try
                    {
                        string probeFile = Path.Combine(subDir, "probe");
                        bool probeOk = true;
                        if (File.Exists(probeFile))
                        {
                           probeOk = probeOk && DeleteFileWithRetries(probeFile, 3);
                        }
                        File.Create(probeFile).Close();
                        probeOk = probeOk && DeleteFileWithRetries(probeFile, 3);
                        if (!probeOk) throw new Exception("DeleteFileWithRetries failed.");
                    }
                    catch (Exception ex)
                    {
                        Global.TaskAdmin.Logger.EX_Log($"Probing for '{subDir}' failed. Skipping Subdirectory. Exception: {ex.Message}.", "SearchForNeedConvertSeries");
                        continue;
                    }

                    try
                    {
                        // Get all files once
                        string[] videoFiles = [.. Directory.GetFiles(subDir, "*.mkv"), .. Directory.GetFiles(subDir, "*.mp4")];
                        if (videoFiles.Length == 0) continue;  // No files; skip (but could delete marker if empty dir desired)


                        int queuedCount = 0;
                        foreach (string file in videoFiles)
                        {

                            string extension = Path.GetExtension(file).ToLowerInvariant();
                            if (!validExtensions.Contains(extension)) continue;

                            string episodeName = Path.GetFileNameWithoutExtension(file).Trim();

                            int? episodeNumber =  OnlineEpisodeElement.GetEpNumberFromString(episodeName);

                            if (episodeNumber == null) {
                                Global.TaskAdmin.Logger.Log($"Skipped '{file}'. No episode number found.", "SearchForNeedConvertSeries");
                                continue;
                            }
                            string epNumberString = String.Format("{0:00}", episodeNumber);
                            if (videoFiles.Length > 99)
                            {
                                epNumberString = String.Format("{0:000}", episodeNumber);
                            }
                            string destEpisodeExtension = Path.GetExtension(file).ToLowerInvariant();
                            string destEpisodeName = seriesName + " " + epNumberString;                            
                            string tempFolder = Path.Combine(subDir, destEpisodeName)  + ".temp"; //Temp folder is per-episode, not per series. When episode processing is complete the folder is deleted.
                            string destEpisodePath = Path.Combine(tempFolder, destEpisodeName + destEpisodeExtension);
                            try
                            {
                                if (Directory.Exists(tempFolder))
                                {
                                    Directory.Delete(tempFolder, true);
                                    Global.TaskAdmin.Logger.Log($"Pre-existent temporary folder '{tempFolder}' was deleted.", "SearchForNeedConvertSeries");
                                }
                                Directory.CreateDirectory(tempFolder);
                            }
                            catch (Exception ex)
                            {
                                Global.TaskAdmin.Logger.EX_Log($"Failed to delete temp dir '{tempFolder}'. Exception: {ex.Message}", "SearchForNeedConvertSeries");
                                continue;
                            }                            


                            if (!MoveFileWithRetries(file, destEpisodePath, 3))
                            {
                                Global.TaskAdmin.Logger.Log($"Skipped '{file}'. Failed to move to temp folder.", "SearchForNeedConvertSeries");
                                continue;
                            }

                            AddEpisode(destEpisodeName, subDir, (int) episodeNumber, State.DownloadedFound, "Downloaded-Found"); //DownloadedFound = DownloadedSeeding. Added recently.
                            queuedCount++;
                        }

                        if (queuedCount > 0)
                        {
                            Global.TaskAdmin.Logger.Log($"Queued {queuedCount} files of {videoFiles.Length} in '{subDir}' for Series '{seriesName}'. Missing files may not satisfy requirements, check filenames and Logs.", "SearchForNeedConvertSeries");                                                      
                        }
                        if (!DeleteFileWithRetries(markerPath, 3))
                        {
                            Global.TaskAdmin.Logger.EX_Log($"Failed to delete marker '{markerPath}'", "SearchForNeedConvertSeries");
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Global.TaskAdmin.Logger.EX_Log($"Error processing '{subDir}': {ex.Message}", "SearchForNeedConvertSeries");
                    }

                }
            }
            UpdateSeriesDataTable();
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
            return Global.CurrentOpsQueue.Count < 2
                ? Global.CurrentOpsQueue.Peek()
                : Global.CurrentOpsQueue.Dequeue();
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


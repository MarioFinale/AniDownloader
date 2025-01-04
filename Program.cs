using System.Data;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using DataTablePrettyPrinter;

using static AniDownloaderTerminal.SeriesDownloader.EpisodeToDownload;


namespace AniDownloaderTerminal
{
    public class Program
    {
        private string CurrentlyScanningSeries = string.Empty;
        private readonly SeriesDownloader CurrentSeriesDownloader = new();

        private int PreviousLineCount = 0;
        private int PreviousWindowHeight = 0;
        private int PreviousWindowWidth = 0;

        public static readonly Settings settings = new();
        public static readonly Webserver webserver = new();

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


            bool StartConvertionsTask()
            {
                CurrentSeriesDownloader.StartConvertions();
                return true;
            }

            Global.TaskAdmin.NewTask("StartConvertions", "Downloader", StartConvertionsTask, 2000, true);


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

            await UpdateSeries();
        }

        private async Task UpdateSeries()
        {
            while (true)
            {
                try
                {
                    foreach (DataRow row in Global.SeriesTable.Rows)
                    {
                        string? sName = row["Name"].ToString();
                        string? sPath = row["Path"].ToString();
                        string? sFilter = row["Filter"].ToString();
                        _ = int.TryParse(row["Offset"].ToString(), out int sOffset);
                        if (sName == null) { continue; }
                        if (sPath == null) { continue; }
                        if (sPath == null) { continue; }
                        if (sFilter == null) { continue; }
                        Global.CurrentOpsQueue.Enqueue("Checking " + sName);
                        if (!Directory.Exists(sPath)) Directory.CreateDirectory(sPath);
                        Series series = new(sName, sPath, sOffset, sFilter);
                        CurrentlyScanningSeries = "Scanning : " + sName;
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

                    await Task.Run(() =>
                    {
                        CurrentlyScanningSeries = "Done scanning!";
                        Thread.Sleep(1800000);
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
            foreach (DataRow row in Global.SeriesTable.Rows)
            {
                // Extract and validate series name and path
                string? seriesName = row["Name"]?.ToString();
                string? seriesPath = row["Path"]?.ToString();

                if (string.IsNullOrWhiteSpace(seriesName) || string.IsNullOrWhiteSpace(seriesPath))
                {
                    continue;
                }

                Global.CurrentOpsQueue.Enqueue($"Searching unconverted files for {seriesName}");

                if (!Directory.Exists(seriesPath))
                {
                    continue;
                }

                foreach (string tempDirPath in Directory.GetDirectories(seriesPath, "*.temp"))
                {
                    string episodeName = Path.GetFileNameWithoutExtension(tempDirPath).Trim();

                    if (!int.TryParse(Regex.Match(episodeName, @"\d{1,3}$").Value, out int episodeNumber)) continue;
                    if (CurrentSeriesDownloader.Episodes.ContainsKey(episodeName)) continue;

                    // Check for episode states
                    if (File.Exists(Path.Combine(tempDirPath, "state.DownloadedSeeding")) ||
                        File.Exists(Path.Combine(tempDirPath, "state.ReEncoding")))
                    {
                        AddEpisode(episodeName, seriesPath, episodeNumber, State.DownloadedSeeding, "Downloaded-found");
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

        private void AddEpisode(string episodeName, string seriesPath, int episodeNumber, State state, string status)
        {
            // Clean up existing file if necessary
            string episodePath = Path.Combine(seriesPath, episodeName);
            if (File.Exists(episodePath)) File.Delete(episodePath);
            var episode = new SeriesDownloader.EpisodeToDownload("", episodeName, seriesPath, episodeNumber);
            episode.SetState(state);
            episode.StatusDescription = status;
            CurrentSeriesDownloader.AddFoundEpisodeToDictionary(episode);
        }

        private static OnlineEpisodeElement[] FilterFoundEpisodes(OnlineEpisodeElement[] episodes, Series series)
        {
            Dictionary<int, OnlineEpisodeElement> bestEpisodes = new();
            List<OnlineEpisodeElement> preFilteredEpisodes = new();

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

                if (!bestEpisodes.ContainsKey(epNum))
                {
                    bestEpisodes.Add(epNum, episode);
                }
                else
                {
                    if (episode.SizeMiB > bestEpisodes[epNum].SizeMiB || Global.TrySelectUncensoredEpisode(episode, bestEpisodes[epNum]) == episode)
                    {
                        bestEpisodes[epNum] = episode;
                    }
                }
            }

            return bestEpisodes.Values.ToArray();
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
                            row[3] = "R:" + episodeElement.GetTorrentRatio();
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
            List<string> episodes = new();

            foreach (KeyValuePair<string, SeriesDownloader.EpisodeToDownload> pair in CurrentSeriesDownloader.Episodes)
            {
                SeriesDownloader.EpisodeToDownload episode = pair.Value;
                SetUpdateEpisodesStatusTable(episode);
                episodes.Add(episode.Name);
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
                try
                {
                    Global.SeriesTable.ReadXml(Global.SeriesTableFilePath);
                }
                catch (Exception ex)
                {
                    Global.TaskAdmin.Logger.EX_Log(ex.Message, "LoadSeriesTable");
                }
            }
            else
            {               
                
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
                    Global.CurrentStatusTable.Columns.Add(new DataColumn("Progress", typeof(int)));
                }

                Global.CurrentStatusTable.Columns[0].SetWidth(20);
                Global.CurrentStatusTable.Columns[1].SetWidth(55);
                Global.CurrentStatusTable.Columns[2].SetWidth(30);
                Global.CurrentStatusTable.Columns[3].SetWidth(10);
            }
        }
    }


}


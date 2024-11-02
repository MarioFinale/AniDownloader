using System.Data;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DataTablePrettyPrinter;
using Mono.Nat.Logging;
using TaskAdmin.Utility;
using static AniDownloaderTerminal.SeriesDownloader.EpisodeToDownload;


namespace AniDownloaderTerminal
{
    public class Program
    {
        private string CurrentlyScanningSeries = string.Empty;
        private readonly SeriesDownloader CurrentSeriesDownloader = new();
        private DataTable SeriesTable;
        private DataTable CurrentStatusTable = new("Torrent Status");

        private int PreviousLineCount = 0;
        private int PreviousWindowHeight = 0;
        private int PreviousWindowWidth = 0;

        public static void Main()
        {
            Program program = new();
            var task = Task.Run(async () => { await program.Start(); });
            task.Wait();
        }

        public Program()
        {
            SeriesTable = new("Series");
            LoadSeriesTable();
            Console.CursorVisible = false;
        }

        public async Task Start()
        {
            Console.Clear();

            Func<bool> StartDownloadsTask = () =>
            {
                CurrentSeriesDownloader.StartDownloads();
                return true;
            };

            Global.TaskAdmin.NewTask("StartDownloads", "Downloader", StartDownloadsTask, 1000, true);


            Func<bool> StartConvertionsTask = () =>
            {
                CurrentSeriesDownloader.StartConvertions();
                return true;
            };

            Global.TaskAdmin.NewTask("StartConvertions", "Downloader", StartConvertionsTask, 2000, true);


            Func<bool> CleanEncodedFilesTask = () =>
            {
                CurrentSeriesDownloader.CleanEncodedFiles();
                return true;
            };

            Global.TaskAdmin.NewTask("CleanEncodedFiles", "Downloader", CleanEncodedFilesTask, 10000, true);

            Func<bool> UpdateSeriesDataTableTask = () =>
            {
                UpdateSeriesDataTable();
                return true;
            };

            Global.TaskAdmin.NewTask("UpdateSeriesDataTable", "Downloader", UpdateSeriesDataTableTask, 200, true);

            Func<bool> SearchForUncompletedEpisodesTask = () =>
            {
                SearchForUncompletedEpisodes();
                return true;
            };

            Global.TaskAdmin.NewTask("SearchForUncompletedEpisodes", "Downloader", SearchForUncompletedEpisodesTask, 60000, true);

            Func<bool> PrintUpdateTableTask = () =>
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
                foreach (DataRow row in SeriesTable.Rows)
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
        }

        public void SearchForUncompletedEpisodes()
        {
            foreach (DataRow row in SeriesTable.Rows)
            {
                string? sName = row["Name"].ToString();
                string? sPath = row["Path"].ToString();
                if (sName == null) { continue; }
                if (sPath == null) { continue; }
                Global.CurrentOpsQueue.Enqueue("Searching unconverted files for " + sName);
                if (!Directory.Exists(sPath)) continue;
                foreach (string directoryPath in Directory.GetDirectories(sPath))
                {
                    if (!directoryPath.ToLowerInvariant().EndsWith(".temp")) continue;
                    string folderName = new DirectoryInfo(directoryPath).Name;
                    string episodename = folderName.Replace(".temp", "").Trim();
                    Match episodeNumberMatch = Regex.Match(episodename, "\\d{1,3}$");
                    if (!episodeNumberMatch.Success) continue;
                    int episodeNumber = int.Parse(episodeNumberMatch.Value);
                    if (CurrentSeriesDownloader.Episodes.ContainsKey(episodename)) continue;
                    if (File.Exists(directoryPath + "/" + "state.DownloadedSeeding") | File.Exists(directoryPath + "/" + "state.ReEncoding"))
                    {
                        SeriesDownloader.EpisodeToDownload episode = new("", episodename, sPath, episodeNumber);
                        if (File.Exists(sPath + "/" + episodename))
                        {
                            File.Delete(sPath + "/" + episodename);
                        }
                        episode.SetState(SeriesDownloader.EpisodeToDownload.State.DownloadedSeeding);
                        episode.StatusDescription = "Downloaded-found";
                        CurrentSeriesDownloader.AddFoundEpisodeToDictionary(episode);
                    }
                    if (File.Exists(directoryPath + "/" + "state.EncodedSeeding") | File.Exists(directoryPath + "/" + "state.EncodedFound"))
                    {
                        SeriesDownloader.EpisodeToDownload episode = new("", episodename, sPath, episodeNumber);
                        episode.SetState(SeriesDownloader.EpisodeToDownload.State.EncodedFound);
                        episode.StatusDescription = "Encoded-found";
                        CurrentSeriesDownloader.AddFoundEpisodeToDictionary(episode);
                    }
                }
            }
            Global.CurrentOpsQueue.Enqueue("Unconverted files search done.");
        }

        private static OnlineEpisodeElement[] FilterFoundEpisodes(OnlineEpisodeElement[] episodes, Series series)
        {
            Dictionary<int, OnlineEpisodeElement> bestEpisodes = new Dictionary<int, OnlineEpisodeElement>();
            List<OnlineEpisodeElement> preFilteredEpisodes = new List<OnlineEpisodeElement>();

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
                if (episode.ProbableEpNumber == null || episode.ProbableLang == Lang.RAW || episode.ProbableLang == Lang.Undefined) continue;
                episode.ProbableLang = episode.ProbableLang == Lang.Undefined ? episode.GetProbableLanguage() : episode.ProbableLang;
                if (episode.ProbableLang != Lang.Custom && episode.ProbableLang != Lang.CustomAndEng) continue;

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

        public void SetUpdateEpisodesStatusTable(string torrentName, string episode, string torrentStatus, int torrentProgress)
        {
            DataRow? row;
            lock (CurrentStatusTable)
            {
                row = CurrentStatusTable.AsEnumerable().Where(dr => dr.Field<string>("Episode") == episode).FirstOrDefault();
            }

            if (!(row == null))
            {
                lock (CurrentStatusTable)
                {
                    lock (row)
                    {
                        row[1] = episode;
                        row[2] = torrentStatus;
                        row[3] = torrentProgress;
                    }
                }

            }
            else
            {
                lock (CurrentStatusTable)
                {
                    CurrentStatusTable.Rows.Add(torrentName, episode, torrentStatus, torrentProgress);
                }
            }
        }

        private void UpdateSeriesDataTable()
        {
            List<string> episodes = new();

            foreach (KeyValuePair<string, SeriesDownloader.EpisodeToDownload> pair in CurrentSeriesDownloader.Episodes)
            {
                SeriesDownloader.EpisodeToDownload episode = pair.Value;
                SetUpdateEpisodesStatusTable(episode.TorrentName, episode.Name, episode.StatusDescription, episode.StatusPercentage);
                episodes.Add(episode.Name);
            }
            DataTable filteredDataTable;
            lock (CurrentStatusTable)
            {
                filteredDataTable = CurrentStatusTable.Clone();
                foreach (DataRow row in CurrentStatusTable.AsEnumerable())
                {
                    if (row == null) continue;
                    string? value = row.Field<string>("Episode");
                    if (value == null) continue;
                    if (episodes.Contains(value))
                    {
                        filteredDataTable.ImportRow(row);
                    }
                }
                CurrentStatusTable = filteredDataTable;
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

        private string GetConsoleText()
        {
            string consoleText = string.Empty;
            lock (CurrentStatusTable)
            {
                consoleText += CurrentStatusTable.ToPrettyPrintedString();
            }
            return consoleText;
        }

        private string PrepareCurrentSeries(string consoleText)
        {
            return MatchStringLenghtWithSpaces(CurrentlyScanningSeries, consoleText.Split('\n')[0]);
        }

        private string GetCurrentOpsString()
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

        private void DisplayConsoleText(string consoleText)
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

        public void LoadSeriesTable()
        {
            SeriesTable = new("Series");

            if (File.Exists(Global.SeriesTableFilePath))
            {
                try
                {
                    SeriesTable.ReadXml(Global.SeriesTableFilePath);
                }
                catch (Exception ex)
                {
                    Global.TaskAdmin.Logger.EX_Log(ex.Message, "LoadSeriesTable");
                }
            }
            else
            {
                DataColumn[] keys = new DataColumn[1];
                DataColumn SeriesColumn = new("Name", typeof(string));
                SeriesTable.Columns.Add(SeriesColumn);
                keys[0] = SeriesColumn;

                DataColumn SeriesPath = new("Path", typeof(string));
                SeriesTable.Columns.Add(SeriesPath);

                DataColumn Offset = new("Offset", typeof(string));
                SeriesTable.Columns.Add(Offset);

                DataColumn Filter = new("Filter", typeof(string));
                SeriesTable.Columns.Add(Filter);

                SeriesTable.PrimaryKey = keys;
            }
            lock (CurrentStatusTable)
            {
                CurrentStatusTable = new("Torrent Status");
                CurrentStatusTable.Columns.Add(new DataColumn("Torrent Name", typeof(string)));
                CurrentStatusTable.Columns.Add(new DataColumn("Episode", typeof(string)));
                CurrentStatusTable.Columns.Add(new DataColumn("Status", typeof(string)));
                CurrentStatusTable.Columns.Add(new DataColumn("Progress", typeof(int)));

                CurrentStatusTable.Columns[0].SetWidth(20);
                CurrentStatusTable.Columns[1].SetWidth(55);
                CurrentStatusTable.Columns[2].SetWidth(30);
                CurrentStatusTable.Columns[3].SetWidth(10);
            }
        }
    }


}


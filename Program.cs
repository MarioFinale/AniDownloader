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
        private readonly SeriesDownloader downloader = new();
        private DataTable SeriesTable;
        private DataTable CurrentStatusTable = new("Torrent Status");
        private readonly static string Exepath = AppDomain.CurrentDomain.BaseDirectory;
        private readonly static string SeriesTableFilePath = Exepath + "/SeriesData.xml";
        private DateTime LastNyaaRequest = DateTime.UtcNow;
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
                downloader.StartDownloads();
                return true;
            };

            Global.TaskAdmin.NewTask("StartDownloads", "Downloader", StartDownloadsTask, 1000, true);


            Func<bool> StartConvertionsTask = () =>
            {
                downloader.StartConvertions();
                return true;
            };

            Global.TaskAdmin.NewTask("StartConvertions", "Downloader", StartConvertionsTask, 2000, true);


            Func<bool> CleanEncodedFilesTask = () =>
            {
                downloader.CleanEncodedFiles();
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
                    Global.currentOpsQueue.Enqueue("Checking " + sName);
                    if (!Directory.Exists(sPath)) Directory.CreateDirectory(sPath);
                    Series series = new(sName, sPath, sOffset, sFilter);
                    CurrentlyScanningSeries = "Scanning : " + sName;
                    OnlineEpisodeElement[] filteredEpisodes = FilterEpisodes(await GetAvailableSeriesEpisodes(series), series);

                    foreach (OnlineEpisodeElement episodeToDownload in filteredEpisodes)
                    {
                        if (episodeToDownload.ProbableEpNumber == null) continue;
                        int episodeNumber = (int)episodeToDownload.ProbableEpNumber;
                        string episodeName = series.Name + " " + episodeNumber.ToString("00");
                        if (downloader.Episodes.ContainsKey(episodeName)) continue;
                        downloader.AddTorrentToDictionary(episodeToDownload.TorrentUrl, series.Path, episodeName, episodeNumber);
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
                Global.currentOpsQueue.Enqueue("Searching unconverted files for " + sName);
                if (!Directory.Exists(sPath)) continue;
                foreach (string directoryPath in Directory.GetDirectories(sPath))
                {
                    if (!directoryPath.ToLowerInvariant().EndsWith(".temp")) continue;
                    string folderName = new DirectoryInfo(directoryPath).Name;
                    string episodename = folderName.Replace(".temp", "").Trim();
                    Match episodeNumberMatch = Regex.Match(episodename, "\\d{1,3}$");
                    if (!episodeNumberMatch.Success) continue;
                    int episodeNumber = int.Parse(episodeNumberMatch.Value);
                    if (downloader.Episodes.ContainsKey(episodename)) continue;
                    if (File.Exists(directoryPath + "/" + "state.DownloadedSeeding") | File.Exists(directoryPath + "/" + "state.ReEncoding"))
                    {
                        SeriesDownloader.EpisodeToDownload episode = new("", episodename, sPath, episodeNumber);
                        if (File.Exists(sPath + "/" + episodename))
                        {
                            File.Delete(sPath + "/" + episodename);
                        }
                        episode.SetState(SeriesDownloader.EpisodeToDownload.State.DownloadedSeeding);
                        episode.StatusDescription = "Downloaded-found";
                        downloader.AddFoundEpisodeToDictionary(episode);
                    }
                    if (File.Exists(directoryPath + "/" + "state.EncodedSeeding") | File.Exists(directoryPath + "/" + "state.EncodedFound"))
                    {
                        SeriesDownloader.EpisodeToDownload episode = new("", episodename, sPath, episodeNumber);
                        episode.SetState(SeriesDownloader.EpisodeToDownload.State.EncodedFound);
                        episode.StatusDescription = "Encoded-found";
                        downloader.AddFoundEpisodeToDictionary(episode);
                    }
                }
            }
            Global.currentOpsQueue.Enqueue("Unconverted files search done.");
        }

        private async Task<string> GetWebDataFromUrl(string url)
        {
            TimeSpan time = DateTime.UtcNow - LastNyaaRequest;
            while (time.TotalSeconds < 2)
            {
                await Task.Run(() =>
                {
                    Thread.Sleep(100);
                });
                time = DateTime.UtcNow - LastNyaaRequest;
            }

            Global.currentOpsQueue.Enqueue("Loading web resource: " + url);
            try
            {
                using HttpResponseMessage responseMessage = await Global.httpClient.GetAsync(url);
                using HttpContent content = responseMessage.Content;
                Thread.Sleep(1500); //Prevent server-side throttling
                return await content.ReadAsStringAsync();

            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.EX_Log(ex.Message, "GetWebDataFromUrl");
                return string.Empty;
            }
        }

        private async Task<OnlineEpisodeElement[]> GetAvailableSeriesEpisodes(Series series)
        {
            List<OnlineEpisodeElement> episodes = new();
            string seriesUrlEncoded = System.Web.HttpUtility.UrlEncode(series.Name);
            string content = await GetWebDataFromUrl("https://nyaa.si/?page=rss&q=" + seriesUrlEncoded + "&c=1_0&f=0");
            string[] list = OnlineEpisodeElement.GetOnlineEpisodesListFromContent(content);

            foreach (string item in list)
            {
                OnlineEpisodeElement element = new(item);
                if (element == null) continue;
                if (element.ProbableEpNumber == null) continue;
                if (!element.IsAnime) continue;
                if (element.IsTooOld) continue;
                if (element.IsTooNew) continue;
                if (!String.IsNullOrWhiteSpace(series.Filter))
                {
                    if (Regex.Match(element.Name, series.Filter).Success) continue;
                }
                if (element.Name.ToUpperInvariant().Contains("BATCH")) continue;
                if (element.SizeMiB > 4000) { 
                    Global.TaskAdmin.Logger.Log(element.Name + " discarded due to big size (over 4GiB).", "GetAvailableSeriesEpisodes");
                    continue;
                }
                element.AddEpisodeNumberOffset(series.Offset);
                episodes.Add(element);
            }
            return episodes.ToArray();
        }

        private static OnlineEpisodeElement[] FilterEpisodes(OnlineEpisodeElement[] episodes, Series series)
        {
            Dictionary<int, OnlineEpisodeElement> epsQ = new();
            List<OnlineEpisodeElement> filteredEpisodes_pre = new();
            List<OnlineEpisodeElement> filteredEpisodes = new();



            int[] downloadedEpisodes = series.GetEpisodesDownloaded();

            foreach (OnlineEpisodeElement episode in episodes)
            {
                if (episode.ProbableEpNumber == null) continue;
                if (!downloadedEpisodes.Contains((int)episode.ProbableEpNumber))
                {
                    filteredEpisodes_pre.Add(episode);
                }
            }

            foreach (OnlineEpisodeElement episode in filteredEpisodes_pre)
            {
                if (episode.ProbableEpNumber == null || episode.ProbableLang == Lang.RAW) continue;

                if (episode.ProbableLang == Lang.Undefined)
                {
                    Lang epLang = episode.GetProbableLanguage();
                    episode.ProbableLang = epLang;
                }

                if (episode.ProbableLang != Lang.Spa && episode.ProbableLang != Lang.SpaEng) continue;

                int epnum = (int)episode.ProbableEpNumber;
                if (!epsQ.ContainsKey(epnum))
                {
                    epsQ.Add(epnum, episode);
                }

                if (epsQ[epnum].SizeMiB < episode.SizeMiB) epsQ[epnum] = episode;


                bool isCurrentEpisodeUncensored = epsQ[epnum].Name.Contains("uncensored", StringComparison.OrdinalIgnoreCase)
                                                || epsQ[epnum].Name.Contains("sin censura", StringComparison.OrdinalIgnoreCase);
                bool isCandidateEpisodeUncensored = episode.Name.Contains("uncensored", StringComparison.OrdinalIgnoreCase)
                                                || episode.Name.Contains("sin censura", StringComparison.OrdinalIgnoreCase);
                if (isCandidateEpisodeUncensored && !isCurrentEpisodeUncensored) epsQ[epnum] = episode;
            }
            filteredEpisodes.AddRange(epsQ.Values);
            return filteredEpisodes.ToArray();
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

            foreach (KeyValuePair<string, SeriesDownloader.EpisodeToDownload> pair in downloader.Episodes)
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
            string consoleText = string.Empty;
            string currentSeries = CurrentlyScanningSeries;
            string currentOpsString = string.Empty;

            lock (CurrentStatusTable)
            {
                consoleText += CurrentStatusTable.ToPrettyPrintedString();
            }
            currentSeries = MatchStringLenghtWithSpaces(currentSeries, consoleText.Split(Environment.NewLine)[0]);
            consoleText += currentSeries;

            if (Global.currentOpsQueue.Count < 2)
            {                
                currentOpsString += Global.currentOpsQueue.Peek();
            }
            else
            {
                currentOpsString += Global.currentOpsQueue.Dequeue();               
            }

            currentOpsString = MatchStringLenghtWithSpaces(currentOpsString, consoleText.Split(Environment.NewLine)[0]);
            consoleText += Environment.NewLine + currentOpsString;

            int currentLineCount = consoleText.Split(Environment.NewLine).Count();
            if (currentLineCount != PreviousLineCount || Console.BufferWidth != PreviousWindowWidth || Console.BufferHeight != PreviousWindowHeight)
            {
                PreviousLineCount = currentLineCount;
                PreviousWindowHeight = Console.BufferHeight;
                PreviousWindowWidth = Console.BufferWidth;
                try
                {
                    Console.Clear();
                }
                catch (IOException ex)
                {
                    consoleText += Environment.NewLine + "[CLS EX:]" + ex.Message;
                }
            }
            consoleText += MatchStringLenghtWithSpaces(String.Empty, consoleText.Split(Environment.NewLine)[0]);
            consoleText += MatchStringLenghtWithSpaces(String.Empty, consoleText.Split(Environment.NewLine)[0]);

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

            if (File.Exists(SeriesTableFilePath))
            {
                try
                {
                    SeriesTable.ReadXml(SeriesTableFilePath);
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


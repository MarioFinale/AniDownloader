using MonoTorrent.Client;
using MonoTorrent;
using System.Diagnostics;
using MonoTorrent.Client.Tracker;
using System.Text.RegularExpressions;
using static AniDownloaderTerminal.SeriesDownloader.EpisodeToDownload;

namespace AniDownloaderTerminal
{
    public class SeriesDownloader
    {
        public Dictionary<string, EpisodeToDownload> Episodes = new();
        private readonly ClientEngine engine;    
        private bool CurrentlyEncodingVideoDurationFound = false;
        private TimeSpan CurrentlyEncodingVideoDuration = new();
        private TimeSpan CurrentlyEncodingVideoPosition = new();

        public SeriesDownloader()
        {
            Random rand = new();
            int randPort = rand.Next(50200, 55000);
            EngineSettingsBuilder settingBuilder = new()
            {
                AllowPortForwarding = true,
                AutoSaveLoadDhtCache = true,
                AutoSaveLoadFastResume = true,
                AutoSaveLoadMagnetLinkMetadata = true,
                ListenPort = 58123,
                DhtPort = randPort,
                MaximumUploadSpeed = 13107200,
                MaximumOpenFiles = 30,
                MaximumConnections = 400,
                AllowHaveSuppression = true,
                MaximumHalfOpenConnections = 15,
            };
            engine = new ClientEngine(settingBuilder.ToSettings());
        }


        public class EpisodeToDownload
        {
            public string TorrentURL { get; }
            public string TorrentName { get; }
            public string Name { get; }
            /// <summary>
            /// Folder where the episode will be stored.
            /// </summary>
            public string DownloadPath { get; }
            public TorrentManager? TorrentManager { get; set; }
            public int Number { get; }
            public int StatusPercentage { get; set; }
            public string StatusDescription { get; set; }

            private DateTime _stateTime;
            public State GetState { get => _episodeState; }
            private State _episodeState;

            public DateTime StateTime { get { return _stateTime; } }
            public State EpisodeState { get { return _episodeState; } }

            //

            /// <summary>
            /// Class holding episode to download data.
            /// </summary>
            /// <param name="URL">Url of the torrent file.</param>
            /// <param name="epName">Name of the episode without extension</param>
            /// <param name="episodePath">episodePath is only the folder, not the complete file path, this class calculates the file path in the method getEpisodeFilePathWitoutExtension()</param>
            /// <param name="number">Number of the episode, unused but exposed</param>
            public EpisodeToDownload(string URL, string epName, string episodePath, int number)
            {

                TorrentURL = URL;
                Name = epName;
                DownloadPath = episodePath;
                Number = number;
                _stateTime = DateTime.Now;
                _episodeState = State.NotStarted;
                TorrentName = Path.GetFileNameWithoutExtension(URL);
                StatusDescription = "Queued for Download";
            }

            public void SetState(State state)
            {
                _stateTime = DateTime.Now;
                _episodeState = state;

                foreach (string file in Directory.EnumerateFiles(GetTempDownloadPath()))
                {
                    string filename = Path.GetFileName(file);
                    if (filename.StartsWith("state."))
                    {
                        File.Delete(file);
                    }
                }
                string filepath = GetTempDownloadPath() + "/state." + state.ToString();
                File.WriteAllText(filepath, state.ToString());
            }

            public string GetTempDownloadPath()
            {
                return DownloadPath + "/" + Name + ".temp";
            }

            public string GetTorrentFilePath()
            {
                return GetTempDownloadPath() + "/" + Name + ".torrent";
            }

            public string GetEpisodeFilePathWitoutExtension()
            {
                return DownloadPath + "/" + Name;
            }

            public double GetTorrentRatio()
            {
                if (TorrentManager != null)
                {
                    double ratio = TorrentManager.Monitor.DataBytesUploaded / TorrentManager.Monitor.DataBytesDownloaded;
                    return ratio;
                }
                return 0;
            }


            public enum State
            {
                NotStarted,
                Downloading,
                DownloadedSeeding,
                ReEncoding,
                EncodedSeeding,
                EncodedFound
            }

        }
       

        public void AddTorrentToDictionary(string downloadurl, string downloadPath, string episodeName, int episodeNumber)
        {
            EpisodeToDownload episode = new(downloadurl, episodeName, downloadPath, episodeNumber);
            lock (Episodes)
            {
                Episodes.Add(episode.Name, episode);
            }
        }

        public void AddFoundEpisodeToDictionary(EpisodeToDownload episode)
        {
            lock (Episodes)
            {
                Episodes.Add(episode.Name, episode);
            }
        }

        public void StartDownloads()
        {
            try
            {
                foreach (KeyValuePair<string, EpisodeToDownload> pair in Episodes)
                {
                    if (!pair.Value.EpisodeState.Equals(EpisodeToDownload.State.NotStarted)) continue;
                    EpisodeToDownload episode = pair.Value;

                    string tempDownloadDirPath = episode.GetTempDownloadPath();
                    string torrentFilePath = episode.GetTorrentFilePath();

                    if (!Directory.Exists(tempDownloadDirPath)) Directory.CreateDirectory(tempDownloadDirPath);
                    if (File.Exists(torrentFilePath)) File.Delete(torrentFilePath);

                    if (!Global.DownloadFileToPath(episode.TorrentURL, torrentFilePath)) continue;
                    Torrent torrent = Torrent.Load(torrentFilePath);
                    TorrentManager manager = Task.Run(() => engine.AddAsync(torrentFilePath, tempDownloadDirPath)).Result;

                    lock (episode)
                    {
                        episode.TorrentManager = manager;
                        episode.TorrentManager.TorrentStateChanged += (o, e) => TorrentStateChangedDelegate(e, episode);
                        episode.TorrentManager.PeerConnected += (o, e) => TorrentConnectionSuccessfulDelegate(e, episode);
                        episode.TorrentManager.ConnectionAttemptFailed += (o, e) => TorrentConnectionAttemptFailedDelegate(e, episode);
                        episode.TorrentManager.PieceHashed += (o, e) => TorrentPieceHashedDelegate(e, episode);
                        episode.TorrentManager.TrackerManager.AnnounceComplete += (o, e) => TorrentTrackerManagerAnnounceCompleteDelegate(e, episode);
                        _ = Task.Run(() =>
                        {
                            episode.TorrentManager.StartAsync();
                        });
                        episode.SetState(EpisodeToDownload.State.Downloading);
                        episode.StatusDescription = "Downloading";
                    }
                }
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.EX_Log(ex.Message, "StartDownloads");
            }
        }

        public void StartConvertions()
        {
            try
            {
                foreach (KeyValuePair<string, EpisodeToDownload> pair in Episodes)
                {
                    if (!pair.Value.EpisodeState.Equals(EpisodeToDownload.State.DownloadedSeeding)) continue;
                    EpisodeToDownload episode = pair.Value;
                    string fileToConvertPath = string.Empty;

                    foreach (string file in Directory.EnumerateFiles(episode.GetTempDownloadPath()))
                    {
                        if (Path.GetExtension(file).ToLowerInvariant().Equals(".mp4") | Path.GetExtension(file).ToLowerInvariant().Equals(".mkv"))
                        {
                            fileToConvertPath = file;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(fileToConvertPath)) continue;
                    ConvertFile(episode, fileToConvertPath);
                    lock (episode)
                    {
                        episode.SetState(EpisodeToDownload.State.EncodedSeeding);
                        episode.StatusPercentage = 100;
                        episode.StatusDescription = "Encoded-Seeding";
                    }
                }
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.EX_Log(ex.Message, "StartConvertions");
            }
            
        }

        public void CleanEncodedFiles()
        {
            try
            {
                foreach (KeyValuePair<string, EpisodeToDownload> pair in Episodes)
                {
                    if (!pair.Value.EpisodeState.Equals(EpisodeToDownload.State.EncodedSeeding) && !pair.Value.EpisodeState.Equals(EpisodeToDownload.State.EncodedFound)) continue;
                    EpisodeToDownload episode = pair.Value;
                    TimeSpan spannedTime = DateTime.Now - episode.StateTime;
                    if (string.IsNullOrWhiteSpace(episode.TorrentName))
                    {
                        episode.SetState(State.EncodedFound);
                    }
                    if (episode.TorrentManager != null)
                    {
                        double ratio = episode.TorrentManager.Monitor.DataBytesUploaded / episode.TorrentManager.Monitor.DataBytesDownloaded;

                        if (Settings.UseRatio)
                        {
                            if (ratio < Settings.SeedingRatio)
                            {
                                if (spannedTime.TotalHours < Settings.SeedingTimeHours) continue;
                            }
                        }
                        else
                        {
                            if (spannedTime.TotalHours < Settings.SeedingTimeHours) continue;
                        }

                        try
                        {
                            _ = episode.TorrentManager.StopAsync();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex.Message);
                        }

                    }
                    int tries = 1;
                    DirectoryInfo torrentTempDir = new(episode.GetTempDownloadPath());
                    while (torrentTempDir.Exists)
                    {
                        Thread.Sleep(2000);
                        try
                        {
                            lock (episode)
                            {
                                episode.StatusDescription = "Cleaning - Try " + tries.ToString();
                            }

                            torrentTempDir.Delete(true);
                            tries++;
                        }
                        catch (Exception ex)
                        {
                            Global.TaskAdmin.Logger.EX_Log($"Failed to clean file: {ex.Message}", "CleanEncodedFiles");
                        }
                    }
                    Episodes.Remove(episode.Name);
                    return;
                }
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.EX_Log(ex.Message, "CleanEncodedFiles");
            }           
        }


        public bool ConvertFile(EpisodeToDownload episode, string filetoConvert)
        {
            string extensionToUse = Path.GetExtension(filetoConvert);

            if (!File.Exists(filetoConvert))
                return false;
            string finalEpisodeName = episode.GetEpisodeFilePathWitoutExtension() + extensionToUse;
            if (File.Exists(finalEpisodeName))
            {
                File.Delete(finalEpisodeName);
            }
            string args;
            if (Settings.UseTranscodingHWAccel)
            {
                args = "-hwaccel auto ";
            }
             args = "-i \"" + filetoConvert + "\" -y " + Settings.OutputTranscodeCommandLineArguments + " \"" + finalEpisodeName + "\"";

            lock (episode)
            {
                episode.SetState(EpisodeToDownload.State.ReEncoding);
                episode.StatusPercentage = 0;
                episode.StatusDescription = "Re-Encoding";
            }

            using Process p = new();
            {
                var withBlock = p;
                withBlock.StartInfo.UseShellExecute = false;
                withBlock.StartInfo.FileName = "ffmpeg";
                withBlock.StartInfo.Arguments = args;
                withBlock.StartInfo.RedirectStandardError = true;
                withBlock.StartInfo.RedirectStandardOutput = true;
                withBlock.StartInfo.CreateNoWindow = true;
                withBlock.OutputDataReceived += (o, e) => EncoderDataRecievedEventHandlerDelegate(e, episode);
                withBlock.ErrorDataReceived += (o, e) => EncoderDataRecievedEventHandlerDelegate(e, episode);
                withBlock.Start();
                withBlock.BeginOutputReadLine();
                withBlock.BeginErrorReadLine();
                withBlock.WaitForExit();
                CurrentlyEncodingVideoDurationFound = false;
                CurrentlyEncodingVideoDuration = new TimeSpan(0);
                CurrentlyEncodingVideoPosition = new TimeSpan(0);
            }
            return true;
        }

        private void EncoderDataRecievedEventHandlerDelegate(DataReceivedEventArgs e, EpisodeToDownload episode)
        {
            if (e.Data == null) return;
            string ttext = e.Data;
            if (ttext == null)
                return;

            if (!CurrentlyEncodingVideoDurationFound)
            {
                if (ttext.Contains("Duration"))
                {
                    CurrentlyEncodingVideoDuration = GetDurationFromStderr(ttext);
                    if (!(CurrentlyEncodingVideoDuration.Ticks == 0))
                        CurrentlyEncodingVideoDurationFound = true;
                }
            }

            if (ttext.Contains("frame="))
            {
                Tuple<TimeSpan, decimal> durationAndSpeed = GetCurrentTimeAndSpeed(ttext);
                if ((durationAndSpeed.Item1.Ticks.Equals(0) | durationAndSpeed.Item2.Equals(0M)))
                    return;
                CurrentlyEncodingVideoPosition = durationAndSpeed.Item1;
            }
            double dpercent = (((CurrentlyEncodingVideoPosition.TotalSeconds + 1) * 100D) / (CurrentlyEncodingVideoDuration.TotalSeconds + 1));
            int percentage = Convert.ToInt32(Math.Abs(dpercent));
            if (percentage > 100) percentage = 100; //cap to 100

            lock (episode)
            {
                episode.StatusPercentage = percentage;
            }

        }

        public static Tuple<TimeSpan, decimal> GetCurrentTimeAndSpeed(string line)
        {
            Match m = Regex.Match(line, @"time=(\d{2}:\d{2}:\d{2}\.\d{2}).+?speed=(\d{1,20}\.*\d{1,20})x");
            if (!m.Success)
                return new Tuple<TimeSpan, decimal>(new TimeSpan(0), 0M);
            string t = m.Groups[1].Value;
            string[] tim = t.Split(new[] { '.', ':' });
            TimeSpan span = new(0, int.Parse(tim[0]), int.Parse(tim[1]), int.Parse(tim[2]), int.Parse(tim[3]) * 10);
            string s = m.Groups[2].Value;
            decimal spd = decimal.Parse(s);
            return new Tuple<TimeSpan, decimal>(span, spd);
        }

        public static TimeSpan GetDurationFromStderr(string line)
        {
            Match m = Regex.Match(line, @"(Duration: )([0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{2})");
            if (m.Success)
            {
                string t = m.Groups[2].Value;
                string[] tim = t.Split(new[] { '.', ':' });
                TimeSpan span = new(0, int.Parse(tim[0]), int.Parse(tim[1]), int.Parse(tim[2]), int.Parse(tim[3]) * 10);
                return span;
            }
            else
                return new TimeSpan(0);
        }

        private static void TorrentStateChangedDelegate(TorrentStateChangedEventArgs e, EpisodeToDownload episode)
        {
            if (e.NewState == TorrentState.Seeding)
            {
                lock (episode)
                {
                    episode.SetState(EpisodeToDownload.State.DownloadedSeeding);
                    episode.StatusDescription = "Downloaded-Seeding";
                    episode.StatusPercentage = 100;
                }
            }
            else
            {
                lock (episode)
                {
                    episode.StatusDescription = e.NewState.ToString();
                }
            }
        }

        private static void TorrentConnectionSuccessfulDelegate(PeerConnectedEventArgs e, EpisodeToDownload episode)
        {
            Debug.WriteLine($"Episode {episode.Name} Torrent {e.TorrentManager.Torrent.Name} Connection to peer successful: {e.Peer.Uri}");
        }
        private static void TorrentConnectionAttemptFailedDelegate(ConnectionAttemptFailedEventArgs e, EpisodeToDownload episode)
        {
            Debug.WriteLine($"Episode {episode.Name} Torrent {e.TorrentManager.Torrent.Name} Connection failed: {e.Peer.ConnectionUri} - {e.Reason}");
        }
        private static void TorrentPieceHashedDelegate(PieceHashedEventArgs e, EpisodeToDownload episode)
        {
            Debug.WriteLine($"Torrent {e.TorrentManager.Torrent.Name} Piece Hashed: {e.PieceIndex} - {(e.HashPassed ? "Pass" : "Fail")}");
            if (!e.TorrentManager.State.Equals(TorrentState.Seeding))
            {
                lock (episode)
                {
                    episode.StatusPercentage = Convert.ToInt32(e.TorrentManager.Progress);
                }
            }

        }
        private static void TorrentTrackerManagerAnnounceCompleteDelegate(AnnounceResponseEventArgs e, EpisodeToDownload episode)
        {
            Debug.WriteLine($"Episode {episode.Name} Tracker announce: {e.Successful}: {e.Tracker}");
        }




    }
}

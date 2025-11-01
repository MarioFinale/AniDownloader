using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Client.Tracker;
using System.Diagnostics;
using System.Text.RegularExpressions;
using static AniDownloaderTerminal.SeriesDownloader.EpisodeToDownload;

namespace AniDownloaderTerminal
{
    public partial class SeriesDownloader
    {
        public Dictionary<string, EpisodeToDownload> Episodes = [];
        private readonly ClientEngine engine;
        private bool CurrentlyEncodingVideoDurationFound = false;
        private TimeSpan CurrentlyEncodingVideoDuration = new();
        private TimeSpan CurrentlyEncodingVideoPosition = new();
        private bool CurrentlyEncodingFrameCountFound = false;
        private bool CurrentlyEncodingVideoStreamFound = false;
        private ulong CurrentlyEncodingVideoTotalFrames = 0;
        private static readonly string[] sourceArray = [".mp4", ".mkv"];

        [GeneratedRegex("Stream #0:0:(\\w{1,20})* Video:")]
        private static partial Regex FFmpegStreamZeroRegex();
        [GeneratedRegex("frame=\\s*(\\d+)")]
        private static partial Regex FFmpegFrameRegex();
        [GeneratedRegex(@"(Duration: )([0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{2})")]
        private static partial Regex FFmpegDurationRegex();
        [GeneratedRegex(@"time=(\d{2}:\d{2}:\d{2}\.\d{2}).+?speed=(\d{1,20}\.*\d{1,20})x")]
        private static partial Regex FFmpegTimeSpeedRegex();

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


        /// <summary>
        /// Class holding episode to download data.
        /// </summary>
        /// <param name="URL">Url of the torrent file.</param>
        /// <param name="epName">Name of the episode without extension</param>
        /// <param name="episodePath">episodePath is only the folder, not the complete file path, this class calculates the file path in the method getEpisodeFilePathWitoutExtension()</param>
        /// <param name="number">Number of the episode, unused but exposed</param>
        public class EpisodeToDownload(string URL, string epName, string episodePath, int number)
        {
            public string TorrentURL { get; } = URL;
            public string TorrentName { get; } = Path.GetFileNameWithoutExtension(URL);
            public string Name { get; } = epName;
            /// <summary>
            /// Folder where the episode will be stored.
            /// </summary>
            public string DownloadPath { get; } = episodePath;
            public TorrentManager? TorrentManager { get; set; }
            public int Number { get; } = number;
            public Double StatusPercentage { get; set; }
            public string StatusDescription { get; set; } = "Queued for Download";

            private DateTime _stateTime = DateTime.Now;
            public State GetState { get => _episodeState; }
            private State _episodeState = State.NotStarted;

            public DateTime StateTime { get { return _stateTime; } }
            public State EpisodeState { get { return _episodeState; } }

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
                return Path.Combine(DownloadPath, Name + ".temp");
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
                    double ratio = (double)TorrentManager.Monitor.DataBytesUploaded / (double)TorrentManager.Monitor.DataBytesDownloaded;
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
                EncodedFound,
                DownloadedFound = DownloadedSeeding
            }

        }


        public void AddTorrentToDictionary(string downloadurl, string downloadPath, string episodeName, int episodeNumber)
        {
            EpisodeToDownload episode = new(downloadurl, episodeName, downloadPath, episodeNumber);
            lock (Episodes)
            {
                foreach (EpisodeToDownload ep in Episodes.Values)
                {
                    if (ep.TorrentURL.Equals(episode.TorrentURL))
                    {
                        Global.TaskAdmin.Logger.Log("Duplicate torrent URL detected for '" + episode.Name + "' Skipping...", "AddTorrentToDictionary");
                        return;
                    }

                }
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

                    TorrentManager? manager = GetEngineTorrentManagerByPath(torrentFilePath, tempDownloadDirPath);
                    manager ??= Task.Run(() => engine.AddAsync(torrentFilePath, tempDownloadDirPath)).Result;

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

        public TorrentManager? GetEngineTorrentManagerByPath(string metadataPath, string saveDirectory)
        {
            foreach (TorrentManager torrentManager in engine.Torrents)
            {
                string dir = torrentManager.SavePath;
                string met = torrentManager.MetadataPath;
                if (metadataPath.Equals(met) && saveDirectory.Equals(dir)) return torrentManager;
            }
            return null;

        }

        public void StartConversions()
        {
            try
            {
                foreach (var pair in Episodes)
                {
                    if (pair.Value.EpisodeState != EpisodeToDownload.State.DownloadedSeeding) continue;

                    var episode = pair.Value;
                    int episodeNumber = episode.Number;

                    // Regex with word boundary for better accuracy
                    Regex episodeNumberRegex = new($@"\b{episodeNumber}\d{{0,2}}\b", RegexOptions.Compiled);

                    var a = Directory.GetFiles(episode.GetTempDownloadPath(), "*", SearchOption.AllDirectories);

                    // Fetch all candidate files
                    var candidateFiles = Directory
                        .GetFiles(episode.GetTempDownloadPath(), "*", SearchOption.AllDirectories)
                        .Where(file => sourceArray.Contains(Path.GetExtension(file)?.ToLowerInvariant()))
                        .ToList();

                    // Sort based on regex match and closest number
                    var sortedFiles = candidateFiles
                        .OrderByDescending(file => episodeNumberRegex.IsMatch(Path.GetFileNameWithoutExtension(file)))
                        .ThenBy(file => Math.Abs(GetLastNumber(Path.GetFileNameWithoutExtension(file)) - episodeNumber))
                        .ToList();

                    if (sortedFiles.Count == 0) continue;

                    ConvertFile(episode, sortedFiles[0]);

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
                Global.TaskAdmin.Logger.EX_Log(ex.Message, "StartConversions");
            }
        }

        private static int GetLastNumber(string fileName)
        {
            var parts = fileName.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Reverse().FirstOrDefault(part => int.TryParse(part, out _)) is string numStr && int.TryParse(numStr, out int num) ? num : int.MaxValue;
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
            string args = string.Empty;
            if (Settings.UseTranscodingHWAccel)
            {
                args = "-hwaccel auto ";
            }
            args += "-i \"" + filetoConvert + "\" -y " + Settings.OutputTranscodeCommandLineArguments + " \"" + finalEpisodeName + "\"";
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
                CurrentlyEncodingVideoDurationFound = false;
                CurrentlyEncodingVideoDuration = new TimeSpan(0);
                CurrentlyEncodingVideoPosition = new TimeSpan(0);
                CurrentlyEncodingFrameCountFound = false;
                CurrentlyEncodingVideoTotalFrames = 0;
                withBlock.Start();
                withBlock.BeginOutputReadLine();
                withBlock.BeginErrorReadLine();
                withBlock.WaitForExit();

                if (withBlock.ExitCode != 0)
                {
                    Global.TaskAdmin.Logger.EX_Log("Exit code '" + withBlock.ExitCode + "' on ffmpeg for '" + finalEpisodeName + "'. Conversion failed.", "ConvertFile");
                    try
                    {
                        if (File.Exists(finalEpisodeName)) File.Delete(finalEpisodeName);
                        Global.TaskAdmin.Logger.EX_Log("'" + finalEpisodeName + "'. Was deleted successfully.", "ConvertFile");
                    }
                    catch (IOException ex)
                    {
                        Global.TaskAdmin.Logger.EX_Log("Error cleaning errored output file for '" + finalEpisodeName + "'. Manual cleaning is neccesary.", "ConvertFile");
                        Global.TaskAdmin.Logger.EX_Log("EX message:" + ex.Message, "ConvertFile");
                    }
                    return false;
                }
            }
            CurrentlyEncodingVideoDurationFound = false;
            CurrentlyEncodingFrameCountFound = false;
            CurrentlyEncodingVideoDuration = TimeSpan.Zero;
            CurrentlyEncodingVideoPosition = TimeSpan.Zero;
            CurrentlyEncodingVideoTotalFrames = 0;

            if (!extensionToUse.TrimStart('.').ToLowerInvariant().Equals("mkv")) return true;

            // Optimize with mkvmerge
            string tempOptimizedName = finalEpisodeName + ".temp.mkv";
            if (File.Exists(tempOptimizedName))
            {
                File.Delete(tempOptimizedName);
            }
            lock (episode)
            {
                episode.StatusDescription = "Optimizing MKV";
                episode.StatusPercentage = 0;
            }
            using Process mkvProcess = new();
            {
                var withBlock = mkvProcess;
                withBlock.StartInfo.UseShellExecute = false;
                withBlock.StartInfo.FileName = "mkvmerge";
                withBlock.StartInfo.Arguments = "-o \"" + tempOptimizedName + "\" --clusters-in-meta-seek \"" + finalEpisodeName + "\"";
                withBlock.StartInfo.RedirectStandardError = true;
                withBlock.StartInfo.RedirectStandardOutput = true;
                withBlock.StartInfo.CreateNoWindow = true;
                withBlock.OutputDataReceived += (o, e) => EncoderDataRecievedEventHandlerDelegate(e, episode);
                withBlock.ErrorDataReceived += (o, e) => EncoderDataRecievedEventHandlerDelegate(e, episode);
                withBlock.Start();
                withBlock.BeginOutputReadLine();
                withBlock.BeginErrorReadLine();
                withBlock.WaitForExit();
                lock (episode)
                {
                    episode.StatusDescription = "Finished Optimizing MKV";
                    episode.StatusPercentage = 100;
                }
                if (withBlock.ExitCode != 0)
                {
                    Global.TaskAdmin.Logger.EX_Log("Exit code '" + withBlock.ExitCode + "' on mkvmerge for '" + tempOptimizedName + "'. Using unoptimized version instead...", "ConvertFile");
                    try
                    {
                        if (File.Exists(tempOptimizedName)) File.Delete(tempOptimizedName);
                        Global.TaskAdmin.Logger.EX_Log("'" + tempOptimizedName + "'. Was deleted successfully.", "ConvertFile");
                    }
                    catch (IOException ex)
                    {
                        Global.TaskAdmin.Logger.EX_Log("Error cleaning temp file for '" + tempOptimizedName + "'. Manual cleaning is neccesary.", "ConvertFile");
                        Global.TaskAdmin.Logger.EX_Log("EX message:" + ex.Message, "ConvertFile");
                    }

                    return true;
                }
            }
            File.Delete(finalEpisodeName);
            File.Move(tempOptimizedName, finalEpisodeName);
            return true;
        }

        private void EncoderDataRecievedEventHandlerDelegate(DataReceivedEventArgs e, EpisodeToDownload episode)
        {
            if (e.Data == null) return;
            string ttext = e.Data.Trim();
            if (string.IsNullOrEmpty(ttext)) return;

            if (!CurrentlyEncodingVideoDurationFound)
            {
                if (ttext.Contains("Duration", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentlyEncodingVideoDuration = GetDurationFromStderr(ttext);
                    if (CurrentlyEncodingVideoDuration.Ticks != 0) CurrentlyEncodingVideoDurationFound = true;
                }
            }

            if (ttext.Contains("frame=", StringComparison.OrdinalIgnoreCase))
            {
                Tuple<TimeSpan, decimal> durationAndSpeed = GetCurrentTimeAndSpeed(ttext);
                ulong currentFrame = GetCurrentFrameFromLine(ttext);
                if (durationAndSpeed.Item1.Ticks != 0 && durationAndSpeed.Item2 != 0M) // Time-based progress (primary)
                {
                    CurrentlyEncodingVideoPosition = durationAndSpeed.Item1;
                    double dpercent = ((CurrentlyEncodingVideoPosition.TotalSeconds + 1) * 100D) / (CurrentlyEncodingVideoDuration.TotalSeconds + 1);
                    double percentage = Math.Truncate(dpercent * 100) / 100;
                    if (percentage > 100) percentage = 100;
                    lock (episode)
                    {
                        episode.StatusPercentage = percentage;
                        episode.StatusDescription = $"Encoding";
                    }
                }
                else if (currentFrame > 0 && CurrentlyEncodingVideoTotalFrames > 0) // Frame-based fallback if time is N/A
                {
                    double dpercent = ((currentFrame + 1) * 100D) / (CurrentlyEncodingVideoTotalFrames + 1);
                    double percentage = Math.Truncate(dpercent * 100) / 100;
                    if (percentage > 100) percentage = 100;
                    lock (episode)
                    {
                        episode.StatusPercentage = percentage;
                        episode.StatusDescription = $"Encoding";
                    }
                }
                return;
            }

            if (ttext.StartsWith("Progress:", StringComparison.OrdinalIgnoreCase))
            {
                string percentStr = ttext["Progress: ".Length..].TrimEnd('%');
                if (double.TryParse(percentStr, out double percentage))
                {
                    if (percentage > 100) percentage = 100;
                    lock (episode)
                    {
                        episode.StatusPercentage = percentage;
                        episode.StatusDescription = $"Optimizing MKV"; //Percentage is shown in the web ui table and console app table.
                    }
                }
            }
            else if (ttext.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                Global.TaskAdmin.Logger.EX_Log("mkvmerge error: " + ttext, "EncoderDataRecievedEventHandlerDelegate");
            }

            if (FFmpegStreamZeroRegex().IsMatch(ttext))
            {
                CurrentlyEncodingVideoStreamFound = true;
            }
            if (CurrentlyEncodingVideoStreamFound && !CurrentlyEncodingFrameCountFound)
            {
                if (ttext.Contains("NUMBER_OF_FRAMES"))
                {
                    CurrentlyEncodingVideoTotalFrames = GetNumberOfFramesFromLine(ttext);
                    CurrentlyEncodingFrameCountFound = true;
                }
            }
        }

        private static ulong GetCurrentFrameFromLine(string line)
        {
            Match match = FFmpegFrameRegex().Match(line);
            if (match.Success && ulong.TryParse(match.Groups[1].Value, out ulong frame))
            {
                return frame;            
            }
            return 0;
        }

        private static ulong GetNumberOfFramesFromLine(string line)
        {
            string[] parts = line.Split(' ');
            if (ulong.TryParse(parts[^1].Trim(), out ulong frames))
            {
                return frames;
            }
            return 0;
        }

        public static Tuple<TimeSpan, decimal> GetCurrentTimeAndSpeed(string line)
        {
            Match m = FFmpegTimeSpeedRegex().Match(line);
            if (!m.Success)
                return new Tuple<TimeSpan, decimal>(new TimeSpan(0), 0M);
            string t = m.Groups[1].Value;
            string[] tim = t.Split(['.', ':']);
            TimeSpan span = new(0, int.Parse(tim[0]), int.Parse(tim[1]), int.Parse(tim[2]), int.Parse(tim[3]) * 10);
            string s = m.Groups[2].Value;
            decimal spd = decimal.Parse(s);
            return new Tuple<TimeSpan, decimal>(span, spd);
        }

        public static TimeSpan GetDurationFromStderr(string line)
        {
            Match m = FFmpegDurationRegex().Match(line);
            if (m.Success)
            {
                string t = m.Groups[2].Value;
                string[] tim = t.Split(['.', ':']);
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
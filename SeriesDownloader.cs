using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Client.Tracker;
using System.Data;
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
        private DateTime _lastClaimProgressWriteUtc = DateTime.MinValue;
        private static readonly string[] sourceArray = [".mp4", ".mkv"];

        [GeneratedRegex("Stream #0:0:(\\w{1,20})* Video:")]
        private static partial Regex FFmpegStreamZeroRegex();
        [GeneratedRegex("frame=\\s*(\\d+)")]
        private static partial Regex FFmpegFrameRegex();
        [GeneratedRegex(@"(Duration: )([0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{2})")]
        private static partial Regex FFmpegDurationRegex();
        [GeneratedRegex(@"time=(\d{2}:\d{2}:\d{2}\.\d{2}).+?speed=(\d{1,20}\.*\d{1,20})x")]
        private static partial Regex FFmpegTimeSpeedRegex();
        // Trailing 1-3 digits on a temp folder name, matching Program.TempFolderEpisodeNumberRegex.
        [GeneratedRegex(@"\d{1,3}$")]
        private static partial Regex TempFolderEpisodeNumberRegex();

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
        /// Stops every loaded torrent so MonoTorrent flushes fast-resume data before the process exits.
        /// </summary>
        public async Task StopAllTorrentsAsync()
        {
            try
            {
                await engine.StopAllAsync();
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.EX_Log(ex.Message, "StopAllTorrentsAsync");
            }
        }

        /// <summary>
        /// Re-attaches torrents left in a temporary folder by a previous run so download or seeding can continue.
        /// </summary>
        /// <remarks>
        /// How it works: only folders that still contain a .torrent file and a Downloading,
        /// DownloadedSeeding, or EncodedSeeding state file are considered. The torrent is added
        /// from the local file (no re-download) and started. Incomplete torrents stay Downloading.
        /// A live Seeding state restores DownloadedSeeding, or EncodedSeeding when the episode
        /// was already encoded, so CleanEncodedFiles can keep seeding until the configured ratio.
        /// A previous state file is never treated as proof of completion by itself.
        /// </remarks>
        public async Task ResumeExistingTorrentsAsync()
        {
            List<string> seriesPaths = [];
            lock (Global.SeriesTable)
            {
                foreach (DataRow row in Global.SeriesTable.Rows)
                {
                    string? seriesPath = row["Path"]?.ToString();
                    if (string.IsNullOrWhiteSpace(seriesPath)) continue;
                    if (!Directory.Exists(seriesPath)) continue;
                    seriesPaths.Add(seriesPath);
                }
            }

            foreach (string seriesPath in seriesPaths)
            {
                string[] tempDirs;
                try
                {
                    tempDirs = Directory.GetDirectories(seriesPath, "*.temp");
                }
                catch (Exception ex)
                {
                    Global.TaskAdmin.Logger.EX_Log($"Failed to list temp folders in '{seriesPath}': {ex.Message}", "ResumeExistingTorrentsAsync");
                    continue;
                }

                foreach (string tempDirPath in tempDirs)
                {
                    await ResumeOneTempFolderAsync(seriesPath, tempDirPath);
                }
            }
        }

        /// <summary>
        /// Resumes a single temporary episode folder when it still has a torrent file and a download, seed, or encoded-seed state.
        /// </summary>
        private async Task ResumeOneTempFolderAsync(string seriesPath, string tempDirPath)
        {
            string episodeName = Path.GetFileNameWithoutExtension(tempDirPath).Trim();
            if (string.IsNullOrWhiteSpace(episodeName)) return;

            Match numberMatch = TempFolderEpisodeNumberRegex().Match(episodeName);
            if (!numberMatch.Success) return;
            if (!int.TryParse(numberMatch.Value, out int episodeNumber)) return;

            lock (Episodes)
            {
                if (Episodes.ContainsKey(episodeName)) return;
            }

            string torrentFilePath = Path.Combine(tempDirPath, episodeName + ".torrent");
            if (!File.Exists(torrentFilePath)) return;

            bool wasDownloading = File.Exists(Path.Combine(tempDirPath, "state.Downloading"));
            bool wasSeeding = File.Exists(Path.Combine(tempDirPath, "state.DownloadedSeeding"));
            bool wasEncodedSeeding = File.Exists(Path.Combine(tempDirPath, "state.EncodedSeeding"));
            if (!wasDownloading && !wasSeeding && !wasEncodedSeeding) return;

            // Local path keeps TorrentName non-empty so ratio/time cleanup still applies.
            EpisodeToDownload episode = new(torrentFilePath, episodeName, seriesPath, episodeNumber);
            lock (Episodes)
            {
                if (Episodes.ContainsKey(episodeName)) return;
                Episodes.Add(episodeName, episode);
            }

            try
            {
                TorrentManager? manager = GetEngineTorrentManagerByPath(torrentFilePath, tempDirPath);
                manager ??= await engine.AddAsync(torrentFilePath, tempDirPath);
                AttachTorrentEventHandlers(episode, manager);
                await manager.StartAsync();

                lock (episode)
                {
                    episode.TorrentManager = manager;
                    // Never promote from the old state file alone. Only a live Seeding state is complete.
                    // EncodedSeeding is restored only when the torrent is actually seeding, so the
                    // ratio/time rules in CleanEncodedFiles keep running instead of re-encoding.
                    if (manager.State == TorrentState.Seeding)
                    {
                        if (wasEncodedSeeding)
                        {
                            episode.SetState(State.EncodedSeeding);
                            episode.StatusDescription = "Encoded-Seeding";
                        }
                        else
                        {
                            episode.SetState(State.DownloadedSeeding);
                            episode.StatusDescription = "Downloaded-Seeding";
                        }
                        episode.StatusPercentage = 100;
                    }
                    else
                    {
                        episode.SetState(State.Downloading);
                        episode.StatusDescription = "Downloading";
                        episode.StatusPercentage = Convert.ToInt32(manager.Progress);
                    }
                }

                Global.TaskAdmin.Logger.Log($"Resumed torrent for '{episodeName}' from '{torrentFilePath}'.", "ResumeExistingTorrentsAsync");
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.EX_Log($"Failed to resume '{episodeName}': {ex.Message}", "ResumeExistingTorrentsAsync");
                lock (episode)
                {
                    episode.StatusDescription = "Resume failed";
                }
            }
        }

        /// <summary>
        /// Attaches the torrent event handlers used for status and state transitions.
        /// </summary>
        private static void AttachTorrentEventHandlers(EpisodeToDownload episode, TorrentManager manager)
        {
            manager.TorrentStateChanged += (o, e) => TorrentStateChangedDelegate(e, episode);
            manager.PeerConnected += (o, e) => TorrentConnectionSuccessfulDelegate(e, episode);
            manager.ConnectionAttemptFailed += (o, e) => TorrentConnectionAttemptFailedDelegate(e, episode);
            manager.PieceHashed += (o, e) => TorrentPieceHashedDelegate(e, episode);
            manager.TrackerManager.AnnounceComplete += (o, e) => TorrentTrackerManagerAnnounceCompleteDelegate(e, episode);
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
                        AttachTorrentEventHandlers(episode, episode.TorrentManager);
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
                // Collect candidates first so we can prioritize real downloads (with TorrentURL)
                // over "found" episodes (empty TorrentURL / DownloadedFound). This places
                // newly downloaded torrents at the front of the conversion "queue".
                // Also re-queue ReEncoding episodes whose claim is gone/stale (crash, killed
                // ffmpeg, missing tools, etc.) so they are not stuck forever.
                List<EpisodeToDownload> candidates = [];
                foreach (var pair in Episodes)
                {
                    var ep = pair.Value;
                    if (ep.EpisodeState == EpisodeToDownload.State.DownloadedSeeding)
                    {
                        candidates.Add(ep);
                    }
                    else if (ep.EpisodeState == EpisodeToDownload.State.ReEncoding)
                    {
                        string tp = ep.GetTempDownloadPath();
                        if (!EncodingClaim.IsActivelyClaimed(tp))
                            candidates.Add(ep);
                    }
                }

                // Prioritize: episodes that came from a torrent download (have TorrentURL) first,
                // then series that were discovered on disk (found / needs-convert / non-h264).
                candidates = [.. candidates.OrderBy(e => string.IsNullOrWhiteSpace(e.TorrentURL) ? 1 : 0)];

                foreach (var episode in candidates)
                {
                    // Re-check state in case another thread changed it while we were collecting
                    if (episode.EpisodeState != EpisodeToDownload.State.DownloadedSeeding &&
                        episode.EpisodeState != EpisodeToDownload.State.ReEncoding)
                        continue;

                    string tempPath = episode.GetTempDownloadPath();

                    // Worker already finished this episode on disk → adopt the result and
                    // let CleanEncodedFiles remove the temp folder (no re-encode, no 0% stall).
                    if (TryAdoptWorkerCompletedEncode(episode, tempPath))
                        continue;

                    // Skip if a remote worker already holds a fresh claim on this episode.
                    if (EncodingClaim.IsActivelyClaimed(tempPath, byOtherThanWorkerId: EncodingClaim.GetLocalWorkerId()))
                    {
                        var remote = EncodingClaim.TryRead(tempPath);
                        lock (episode)
                        {
                            episode.StatusDescription = remote != null
                                ? $"Remote: {remote.WorkerId} ({remote.Progress:0}%)"
                                : "Remote encoding";
                            episode.StatusPercentage = remote?.Progress ?? episode.StatusPercentage;
                        }
                        continue;
                    }

                    int episodeNumber = episode.Number;

                    // Regex with word boundary for better accuracy
                    Regex episodeNumberRegex = new($@"\b{episodeNumber}\d{{0,2}}\b", RegexOptions.Compiled);

                    // Fetch all candidate files
                    var candidateFiles = Directory
                        .GetFiles(tempPath, "*", SearchOption.AllDirectories)
                        .Where(file => sourceArray.Contains(Path.GetExtension(file)?.ToLowerInvariant()))
                        .ToList();

                    // Sort based on regex match and closest number
                    var sortedFiles = candidateFiles
                        .OrderByDescending(file => episodeNumberRegex.IsMatch(Path.GetFileNameWithoutExtension(file)))
                        .ThenBy(file => Math.Abs(GetLastNumber(Path.GetFileNameWithoutExtension(file)) - episodeNumber))
                        .ToList();

                    if (sortedFiles.Count == 0) continue;

                    if (!ConvertFile(episode, sortedFiles[0]))
                    {
                        Global.TaskAdmin.Logger.EX_Log($"ConvertFile returned False for: {episode.Name}. File: {sortedFiles[0]}", "StartConversions");
                        continue;
                    }

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

        /// <summary>
        /// If a remote worker already wrote state.EncodedFound / EncodedSeeding or a verified
        /// (or probe-confirmed H.264) final file exists, promote the in-memory episode so it
        /// shows 100% and is eligible for CleanEncodedFiles instead of sitting at 0% as
        /// DownloadedSeeding forever. Also re-marks the file if the .h264ok sidecar was lost.
        /// </summary>
        private static bool TryAdoptWorkerCompletedEncode(EpisodeToDownload episode, string tempPath)
        {
            try
            {
                bool diskEncoded =
                    File.Exists(Path.Combine(tempPath, "state.EncodedFound")) ||
                    File.Exists(Path.Combine(tempPath, "state.EncodedSeeding"));

                bool goodFinal = false;
                foreach (string ext in sourceArray)
                {
                    string finalPath = episode.GetEpisodeFilePathWitoutExtension() + ext;
                    if (!File.Exists(finalPath)) continue;

                    if (IsFileVerifiedH264(finalPath))
                    {
                        goodFinal = true;
                        break;
                    }
                    // Marker missing or stale (mtime changed by media scanners, NFS, etc.)
                    // — probe once and re-create the marker if it really is H.264.
                    if (IsH264Video(finalPath))
                    {
                        MarkFileAsVerifiedH264(finalPath);
                        goodFinal = true;
                        break;
                    }
                }

                if (!diskEncoded && !goodFinal)
                    return false;

                lock (episode)
                {
                    // No torrent on this process → EncodedFound (cleanup immediately).
                    // Otherwise EncodedSeeding so ratio/time rules still apply.
                    if (episode.TorrentManager == null || string.IsNullOrWhiteSpace(episode.TorrentURL))
                    {
                        episode.SetState(State.EncodedFound);
                        episode.StatusDescription = "Encoded-Found (worker)";
                    }
                    else
                    {
                        episode.SetState(State.EncodedSeeding);
                        episode.StatusDescription = "Encoded-Seeding (worker)";
                    }
                    episode.StatusPercentage = 100;
                }
                return true;
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.Debug_Log(
                    $"TryAdoptWorkerCompletedEncode failed for '{episode.Name}': {ex.Message}",
                    "StartConversions");
                return false;
            }
        }

        private static int GetLastNumber(string fileName)
        {
            var parts = fileName.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Reverse().FirstOrDefault(part => int.TryParse(part, out _)) is string numStr && int.TryParse(numStr, out int num) ? num : int.MaxValue;
        }

        /// <summary>
        /// Probes a video file with ffprobe to determine if the primary video stream is H.264.
        /// Returns true only when the codec is clearly "h264"; any error, missing stream or
        /// other codec is treated as "needs conversion".
        /// </summary>
        public static bool IsH264Video(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                using Process p = new();
                p.StartInfo.FileName = "ffprobe";
                p.StartInfo.Arguments = $"-v error -select_streams v:0 -show_entries stream=codec_name -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"";
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                string output = p.StandardOutput.ReadToEnd().Trim();
                // Drain stderr to avoid deadlocks on some platforms
                _ = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);

                if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                    return false;

                return output.Equals("h264", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.Debug_Log($"IsH264Video probe failed for '{filePath}': {ex.Message}", "IsH264Video");
                return false;
            }
        }

        /// <summary>
        /// Returns true when a matching verification marker exists for <paramref name="videoPath"/>
        /// and the recorded size + UTC mtime still match the current file. This lets the
        /// periodic scanner skip already-confirmed H.264 files without calling ffprobe again.
        /// </summary>
        public static bool IsFileVerifiedH264(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                return false;

            string markerPath = videoPath + Settings.VerifiedH264MarkerSuffix;
            if (!File.Exists(markerPath))
                return false;

            try
            {
                var fi = new FileInfo(videoPath);
                long expectedSize = -1;
                long expectedMtimeTicks = -1;

                foreach (string line in File.ReadLines(markerPath))
                {
                    if (line.StartsWith("size=", StringComparison.Ordinal))
                        _ = long.TryParse(line.AsSpan(5), out expectedSize);
                    else if (line.StartsWith("mtime=", StringComparison.Ordinal))
                        _ = long.TryParse(line.AsSpan(6), out expectedMtimeTicks);
                }

                return expectedSize == fi.Length && expectedMtimeTicks == fi.LastWriteTimeUtc.Ticks;
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.Debug_Log($"IsFileVerifiedH264 failed for '{videoPath}': {ex.Message}", "IsFileVerifiedH264");
                return false;
            }
        }

        /// <summary>
        /// Writes (or overwrites) a small sidecar marker next to the video that records
        /// its current size and UTC last-write time. Subsequent scans can then skip the
        /// expensive ffprobe call as long as the file has not been replaced or modified.
        /// Best-effort: failures are logged but never throw.
        /// </summary>
        public static void MarkFileAsVerifiedH264(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                return;

            try
            {
                var fi = new FileInfo(videoPath);
                string markerPath = videoPath + Settings.VerifiedH264MarkerSuffix;
                string content =
                    $"size={fi.Length}{Environment.NewLine}" +
                    $"mtime={fi.LastWriteTimeUtc.Ticks}{Environment.NewLine}" +
                    $"codec=h264{Environment.NewLine}";
                File.WriteAllText(markerPath, content);
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.Debug_Log($"MarkFileAsVerifiedH264 failed for '{videoPath}': {ex.Message}", "MarkFileAsVerifiedH264");
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

                    // Worker-finished (or found-on-disk) encodes have no torrent to seed.
                    // Promote to EncodedFound and clean immediately — do not wait for ratio/time.
                    bool noTorrent = episode.TorrentManager == null || string.IsNullOrWhiteSpace(episode.TorrentName);
                    if (noTorrent)
                    {
                        lock (episode)
                        {
                            if (episode.EpisodeState != State.EncodedFound)
                                episode.SetState(State.EncodedFound);
                            episode.StatusPercentage = 100;
                            episode.StatusDescription = "Encoded-Found";
                        }
                    }
                    else if (episode.TorrentManager != null)
                    {
                        double ratio = 0;
                        if (episode.TorrentManager.Monitor.DataBytesDownloaded > 0)
                        {
                            ratio = episode.TorrentManager.Monitor.DataBytesUploaded / episode.TorrentManager.Monitor.DataBytesDownloaded;
                        }

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
                    const int maxTries = 5;
                    DirectoryInfo torrentTempDir = new(episode.GetTempDownloadPath());
                    while (torrentTempDir.Exists && tries <= maxTries)
                    {
                        try
                        {
                            lock (episode)
                            {
                                episode.StatusDescription = "Cleaning - Try " + tries.ToString();
                                episode.StatusPercentage = 100;
                            }

                            torrentTempDir.Delete(true);
                        }
                        catch (Exception ex)
                        {
                            Global.TaskAdmin.Logger.EX_Log($"Failed to clean file: {ex.Message}", "CleanEncodedFiles");
                            Thread.Sleep(2000);
                        }
                        tries++;
                        torrentTempDir.Refresh();
                    }

                    // Always drop from the live list once cleanup was attempted so worker
                    // results do not stick at 0% in the status/web table forever.
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
            string tempFolder = episode.GetTempDownloadPath();

            if (File.Exists(finalEpisodeName))
            {
                // Another instance (or a previous run) already produced a verified final.
                // Skip re-encoding and let the caller advance the episode state.
                if (IsFileVerifiedH264(finalEpisodeName) || IsH264Video(finalEpisodeName))
                {
                    if (!IsFileVerifiedH264(finalEpisodeName))
                        MarkFileAsVerifiedH264(finalEpisodeName);
                    return true;
                }
                // Incomplete / wrong-codec leftover from a previous crash — remove it so we can
                // write a clean result. The original source inside the .temp folder is left intact.
                try { File.Delete(finalEpisodeName); } catch { /* best-effort */ }
            }

            // Claim this episode so remote workers skip it. If someone else already holds
            // a fresh claim, abort and let StartConversions show remote progress instead.
            if (EncodingClaim.TryAcquire(tempFolder, episode.Name, filetoConvert, "Re-Encoding") == null)
            {
                Global.TaskAdmin.Logger.Log($"ConvertFile skipped '{episode.Name}': claimed by another worker.", "ConvertFile");
                return false;
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

            bool ffmpegOk = false;
            try
            {
                using Process p = new();
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.FileName = "ffmpeg";
                p.StartInfo.Arguments = args;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.CreateNoWindow = true;
                p.OutputDataReceived += (o, e) => EncoderDataRecievedEventHandlerDelegate(e, episode);
                p.ErrorDataReceived += (o, e) => EncoderDataRecievedEventHandlerDelegate(e, episode);
                CurrentlyEncodingVideoDurationFound = false;
                CurrentlyEncodingVideoDuration = new TimeSpan(0);
                CurrentlyEncodingVideoPosition = new TimeSpan(0);
                CurrentlyEncodingFrameCountFound = false;
                CurrentlyEncodingVideoTotalFrames = 0;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();

                if (p.ExitCode != 0)
                {
                    Global.TaskAdmin.Logger.EX_Log("Exit code '" + p.ExitCode + "' on ffmpeg for '" + finalEpisodeName + "'. Conversion failed.", "ConvertFile");
                    Global.TaskAdmin.Logger.EX_Log($"Full command line: ffmpeg {args}", "ConvertFile");
                }
                else
                {
                    ffmpegOk = true;
                }
            }
            catch (Exception ex)
            {
                // Most common: ffmpeg not installed / not on PATH
                Global.TaskAdmin.Logger.EX_Log($"ffmpeg failed to start or crashed for '{finalEpisodeName}': {ex.Message}", "ConvertFile");
                Global.TaskAdmin.Logger.EX_Log($"Full command line: ffmpeg {args}", "ConvertFile");
            }

            if (!ffmpegOk)
            {
                try
                {
                    if (File.Exists(finalEpisodeName)) File.Delete(finalEpisodeName);
                }
                catch (IOException ex)
                {
                    Global.TaskAdmin.Logger.EX_Log("Error cleaning errored output file for '" + finalEpisodeName + "'. Manual cleaning is neccesary.", "ConvertFile");
                    Global.TaskAdmin.Logger.EX_Log("EX message:" + ex.Message, "ConvertFile");
                }
                // Reset state so the episode can be retried later instead of staying stuck in ReEncoding.
                lock (episode)
                {
                    episode.SetState(EpisodeToDownload.State.DownloadedSeeding);
                    episode.StatusDescription = "Downloaded-Seeding (encode failed)";
                    episode.StatusPercentage = 100;
                }
                EncodingClaim.Release(tempFolder);
                return false;
            }

            CurrentlyEncodingVideoDurationFound = false;
            CurrentlyEncodingFrameCountFound = false;
            CurrentlyEncodingVideoDuration = TimeSpan.Zero;
            CurrentlyEncodingVideoPosition = TimeSpan.Zero;
            CurrentlyEncodingVideoTotalFrames = 0;

            if (!extensionToUse.TrimStart('.').ToLowerInvariant().Equals("mkv"))
            {
                // Non-MKV output is already the final file; mark it so future scans skip the probe.
                MarkFileAsVerifiedH264(finalEpisodeName);
                EncodingClaim.Release(tempFolder);
                return true;
            }

            // Optimize with mkvmerge (optional — if missing or fails we keep the unoptimized H.264 file)
            string tempOptimizedName = finalEpisodeName + ".temp.mkv";
            try { if (File.Exists(tempOptimizedName)) File.Delete(tempOptimizedName); } catch { }

            lock (episode)
            {
                episode.StatusDescription = "Optimizing MKV";
                episode.StatusPercentage = 0;
            }
            EncodingClaim.UpdateProgress(tempFolder, 0, "Optimizing MKV");

            bool mkvOk = false;
            try
            {
                using Process mkvProcess = new();
                mkvProcess.StartInfo.UseShellExecute = false;
                mkvProcess.StartInfo.FileName = "mkvmerge";
                mkvProcess.StartInfo.Arguments = "-o \"" + tempOptimizedName + "\" --clusters-in-meta-seek \"" + finalEpisodeName + "\"";
                mkvProcess.StartInfo.RedirectStandardError = true;
                mkvProcess.StartInfo.RedirectStandardOutput = true;
                mkvProcess.StartInfo.CreateNoWindow = true;
                mkvProcess.OutputDataReceived += (o, e) => EncoderDataRecievedEventHandlerDelegate(e, episode);
                mkvProcess.ErrorDataReceived += (o, e) => EncoderDataRecievedEventHandlerDelegate(e, episode);
                mkvProcess.Start();
                mkvProcess.BeginOutputReadLine();
                mkvProcess.BeginErrorReadLine();
                mkvProcess.WaitForExit();

                lock (episode)
                {
                    episode.StatusDescription = "Finished Optimizing MKV";
                    episode.StatusPercentage = 100;
                }

                if (mkvProcess.ExitCode == 0 && File.Exists(tempOptimizedName))
                {
                    mkvOk = true;
                }
                else
                {
                    Global.TaskAdmin.Logger.EX_Log("Exit code '" + mkvProcess.ExitCode + "' on mkvmerge for '" + tempOptimizedName + "'. Using unoptimized version instead...", "ConvertFile");
                }
            }
            catch (Exception ex)
            {
                // mkvmerge missing from PATH or failed to start — keep the ffmpeg output.
                Global.TaskAdmin.Logger.EX_Log($"mkvmerge unavailable or failed for '{finalEpisodeName}': {ex.Message}. Keeping unoptimized file.", "ConvertFile");
            }

            if (mkvOk)
            {
                try
                {
                    File.Delete(finalEpisodeName);
                    File.Move(tempOptimizedName, finalEpisodeName);
                }
                catch (Exception ex)
                {
                    Global.TaskAdmin.Logger.EX_Log($"Failed to replace with optimized MKV: {ex.Message}. Keeping original encode.", "ConvertFile");
                    try { if (File.Exists(tempOptimizedName)) File.Delete(tempOptimizedName); } catch { }
                }
            }
            else
            {
                try { if (File.Exists(tempOptimizedName)) File.Delete(tempOptimizedName); } catch { }
            }

            // Whether optimized or not, the file is valid H.264 at this point.
            MarkFileAsVerifiedH264(finalEpisodeName);
            EncodingClaim.Release(tempFolder);
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
                    MaybeUpdateClaimProgress(episode, percentage, "Encoding");
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
                    MaybeUpdateClaimProgress(episode, percentage, "Encoding");
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
                    MaybeUpdateClaimProgress(episode, percentage, "Optimizing MKV");
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

        /// <summary>
        /// Writes progress into the episode's worker.claim at most every 2 seconds so remote
        /// machines can see encoding progress without hammering the filesystem.
        /// </summary>
        private void MaybeUpdateClaimProgress(EpisodeToDownload episode, double percentage, string status)
        {
            if ((DateTime.UtcNow - _lastClaimProgressWriteUtc).TotalSeconds < 2)
                return;
            _lastClaimProgressWriteUtc = DateTime.UtcNow;
            EncodingClaim.UpdateProgress(episode.GetTempDownloadPath(), percentage, status);
        }

        public static ulong GetCurrentFrameFromLine(string line)
        {
            Match match = FFmpegFrameRegex().Match(line);
            if (match.Success && ulong.TryParse(match.Groups[1].Value, out ulong frame))
            {
                //Console.WriteLine("Parsed current frame:" + frame);
                return frame;

            }
            //Console.WriteLine("Could not parse current frame, line: " + line);
            return 0;
        }

        public static ulong GetNumberOfFramesFromLine(string line)
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
                    // Do not demote an episode that is already encoded and seeding.
                    if (episode.EpisodeState == State.EncodedSeeding || episode.EpisodeState == State.EncodedFound)
                    {
                        episode.StatusPercentage = 100;
                        return;
                    }
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
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using static AniDownloaderTerminal.SeriesDownloader.EpisodeToDownload;

namespace AniDownloaderTerminal
{
    /// <summary>
    /// Lightweight console worker that picks up pending encode work from shared series
    /// folders. Designed to run on additional machines (Linux / macOS / Windows) that
    /// can see the same media paths (NFS, SMB, Syncthing, etc.).
    ///
    /// Coordination is file-based:
    ///   - Work is discovered from *.temp folders that contain state.DownloadedSeeding
    ///     or state.DownloadedFound (created by the main AniDownloader instance).
    ///   - A worker.claim file inside the .temp folder prevents double-encoding.
    ///   - Progress / heartbeat is written into the same claim so the main instance
    ///     can show remote status in its console table.
    ///
    /// Usage (from the project directory):
    ///   dotnet run --project AniDownloaderTerminal.csproj -- --worker
    /// or compile a second executable that calls EncoderWorker.Run().
    /// </summary>
    public static class EncoderWorker
    {
        private static readonly string[] VideoExtensions = [".mp4", ".mkv"];
        private static readonly object ConsoleLock = new();
        private static volatile bool _running = true;

        public static void Run()
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                _running = false;
                WriteLine("Shutting down worker...");
            };

            Settings settings = new();
            settings.Init();

            // Fail fast if the required encoder is missing. mkvmerge is optional.
            if (!ToolAvailable("ffmpeg", "-version"))
            {
                WriteLine("FATAL: ffmpeg not found on PATH. Encoding worker cannot run.");
                WriteLine("Install ffmpeg and ensure it is available in the same environment as this process.");
                return;
            }
            if (!ToolAvailable("mkvmerge", "--version"))
            {
                WriteLine("WARNING: mkvmerge not found on PATH. MKV files will be left unoptimized (still valid H.264).");
            }

            string workerId = EncodingClaim.GetLocalWorkerId();
            WriteLine($"Encoder worker started. Id={workerId}");
            WriteLine($"SearchPaths: {string.Join(" ; ", Settings.SearchPaths)}");
            WriteLine($"Claim stale timeout: {Settings.WorkerClaimStaleSeconds}s");
            WriteLine($"Scan interval: {Settings.WorkerScanIntervalMs}ms");
            WriteLine("Press Ctrl+C to stop.");
            WriteLine("");

            while (_running)
            {
                try
                {
                    int claimed = ScanAndEncodeOnce();
                    if (claimed == 0)
                    {
                        Thread.Sleep(Settings.WorkerScanIntervalMs);
                    }
                    // If we just finished work, loop immediately to pick up more.
                }
                catch (Exception ex)
                {
                    WriteLine($"[ERROR] {ex.Message}");
                    Thread.Sleep(5000);
                }
            }

            foreach (string dir in Settings.SearchPaths)
            {
                if (File.Exists(workerId + ".worker")) File.Delete(workerId + ".worker");
            }

            WriteLine("Worker stopped.");
        }

        private static int ScanAndEncodeOnce()
        {
            int encoded = 0;
            HashSet<string> seriesPaths = CollectSeriesPaths();

            foreach (string seriesPath in seriesPaths)
            {
                if (!_running) break;
                if (!Directory.Exists(seriesPath)) continue;

                string[] tempDirs;
                try
                {
                    tempDirs = Directory.GetDirectories(seriesPath, "*.temp");
                }
                catch
                {
                    continue;
                }

                foreach (string tempDir in tempDirs)
                {
                    if (!_running) break;

                    if (!IsPendingEncodeWork(tempDir))
                        continue;

                    // Skip if someone else already holds a fresh claim
                    if (EncodingClaim.IsActivelyClaimed(tempDir, byOtherThanWorkerId: EncodingClaim.GetLocalWorkerId()))
                        continue;

                    string episodeName = Path.GetFileNameWithoutExtension(tempDir).Trim();
                    string? sourceFile = FindBestSourceFile(tempDir, episodeName);
                    if (sourceFile == null)
                        continue;

                    string extension = Path.GetExtension(sourceFile).ToLowerInvariant();
                    string finalPath = Path.Combine(seriesPath, episodeName + extension);

                    // Already done by another machine (or by a previous run whose .h264ok
                    // marker was lost / mtime changed by a media scanner). Probe if needed.
                    if (File.Exists(finalPath))
                    {
                        bool good = SeriesDownloader.IsFileVerifiedH264(finalPath);
                        if (!good && SeriesDownloader.IsH264Video(finalPath))
                        {
                            SeriesDownloader.MarkFileAsVerifiedH264(finalPath);
                            good = true;
                        }
                        if (good)
                        {
                            WriteState(tempDir, "EncodedFound");
                            EncodingClaim.Release(tempDir);
                            continue;
                        }
                    }

                    var claim = EncodingClaim.TryAcquire(tempDir, episodeName, sourceFile, "Worker-Encoding");
                    if (claim == null)
                        continue;

                    WriteLine($"Claimed '{episodeName}'  source={Path.GetFileName(sourceFile)}");
                    WriteState(tempDir, "ReEncoding");

                    bool ok = EncodeFile(episodeName, seriesPath, tempDir, sourceFile, finalPath);
                    if (ok)
                    {
                        SeriesDownloader.MarkFileAsVerifiedH264(finalPath);
                        WriteState(tempDir, "EncodedFound");
                        EncodingClaim.Release(tempDir);
                        WriteLine($"Finished '{episodeName}'");
                        encoded++;
                    }
                    else
                    {
                        // Release so another worker (or main) can retry
                        WriteState(tempDir, "DownloadedSeeding");
                        EncodingClaim.Release(tempDir);
                        WriteLine($"Failed '{episodeName}' — claim released");
                    }
                }
            }

            return encoded;
        }

        private static HashSet<string> CollectSeriesPaths()
        {
            HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

            // Same discovery logic as the main program's SearchForUncompletedAndNeedConvertSeries
            foreach (string searchPath in Settings.SearchPaths)
            {
                if (!Directory.Exists(searchPath)) continue;
                if (File.Exists(Path.Combine(searchPath, ".ADIngnore"))) continue;

                paths.Add(Path.GetFullPath(searchPath));

                try
                {
                    foreach (string dir in Directory.EnumerateDirectories(searchPath))
                    {
                        paths.Add(Path.GetFullPath(dir));

                        // One level of season folders (S01, Season 02, ...)
                        try
                        {
                            foreach (string seasonDir in Directory.EnumerateDirectories(dir))
                            {
                                string name = new DirectoryInfo(seasonDir).Name;
                                if (Regex.IsMatch(name.Trim(), @"^(?:[Ss](?:eason)?[ \-_\\.]*)*\d{1,3}$", RegexOptions.IgnoreCase))
                                    paths.Add(Path.GetFullPath(seasonDir));
                            }
                        }
                        catch { /* ignore */ }
                    }
                }
                catch { /* ignore */ }
            }

            // Also honour SeriesData.xml if present next to the executable
            string seriesTablePath = Global.SeriesTableFilePath;
            if (File.Exists(seriesTablePath))
            {
                try
                {
                    var table = new System.Data.DataTable();
                    table.ReadXml(seriesTablePath);
                    foreach (System.Data.DataRow row in table.Rows)
                    {
                        string? path = row["Path"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                            paths.Add(Path.GetFullPath(path));
                    }
                }
                catch (Exception ex)
                {
                    WriteLine($"[WARN] Could not read SeriesData.xml: {ex.Message}");
                }
            }

            return paths;
        }

        private static bool IsPendingEncodeWork(string tempDir)
        {
            // Main (or a previous worker) left the episode ready for conversion
            bool downloaded =
                File.Exists(Path.Combine(tempDir, "state.DownloadedSeeding")) ||
                File.Exists(Path.Combine(tempDir, "state.DownloadedFound"));

            // Already finished — nothing to do
            bool encoded =
                File.Exists(Path.Combine(tempDir, "state.EncodedSeeding")) ||
                File.Exists(Path.Combine(tempDir, "state.EncodedFound"));

            // Main is mid-encode (state file) even without a claim
            bool mainEncoding = File.Exists(Path.Combine(tempDir, "state.ReEncoding"));

            return downloaded && !encoded && !mainEncoding;
        }

        private static string? FindBestSourceFile(string tempDir, string episodeName)
        {
            // Prefer a file whose name contains the episode number / canonical name
            var files = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f)?.ToLowerInvariant()))
                .ToList();

            if (files.Count == 0)
                return null;

            // Extract trailing number from episode name for ranking
            int epNum = 0;
            var numMatch = Regex.Match(episodeName, @"(\d{1,3})$");
            if (numMatch.Success)
                _ = int.TryParse(numMatch.Groups[1].Value, out epNum);

            return files
                .OrderByDescending(f => Path.GetFileNameWithoutExtension(f)
                    .Contains(episodeName, StringComparison.OrdinalIgnoreCase))
                .ThenBy(f =>
                {
                    var parts = Path.GetFileNameWithoutExtension(f)
                        .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
                    string? last = parts.Reverse().FirstOrDefault(p => int.TryParse(p, out _));
                    return last != null && int.TryParse(last, out int n)
                        ? Math.Abs(n - epNum)
                        : int.MaxValue;
                })
                .First();
        }

        private static bool EncodeFile(string episodeName, string seriesPath, string tempFolder,
            string sourceFile, string finalPath)
        {
            try
            {
                if (File.Exists(finalPath))
                {
                    if (SeriesDownloader.IsFileVerifiedH264(finalPath) || SeriesDownloader.IsH264Video(finalPath))
                    {
                        if (!SeriesDownloader.IsFileVerifiedH264(finalPath))
                            SeriesDownloader.MarkFileAsVerifiedH264(finalPath);
                        return true;
                    }
                    // Incomplete leftover only — never touch the source inside .temp
                    try { File.Delete(finalPath); } catch { }
                }

                // Same command shape as the main instance, plus -progress pipe:1 so we also
                // get newline-delimited key=value progress on stdout when supported.
                string args = string.Empty;
                if (Settings.UseTranscodingHWAccel)
                    args = "-hwaccel auto ";
                args += $"-progress pipe:1 -i \"{sourceFile}\" -y {Settings.OutputTranscodeCommandLineArguments} \"{finalPath}\"";

                // Same inputs the main encoder uses for % calculation.
                double durationSeconds = ProbeDurationSeconds(sourceFile);
                ulong totalFrames = ProbeTotalFrames(sourceFile, durationSeconds);
                double lastPercent = 0;
                object progressLock = new();
                bool frameCountFound = false;

                void HandleFfmpegLine(string line)
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    line = line.Trim();

                    // Duration banner (stderr) — same as main
                    if (line.Contains("Duration", StringComparison.OrdinalIgnoreCase))
                    {
                        TimeSpan d = SeriesDownloader.GetDurationFromStderr(line);
                        if (d.TotalSeconds > 1)
                            durationSeconds = d.TotalSeconds;
                    }

                    // Matroska/other tags sometimes expose frame count mid-stream — same as main
                    if (!frameCountFound && line.Contains("NUMBER_OF_FRAMES", StringComparison.OrdinalIgnoreCase))
                    {
                        ulong n = SeriesDownloader.GetNumberOfFramesFromLine(line);
                        if (n > 0)
                        {
                            totalFrames = n;
                            frameCountFound = true;
                            //Console.WriteLine("Found number of frames:" + totalFrames);
                        }
                           
                    }

                    // Primary progress line: frame=... time=... speed=...
                    // Mirrors EncoderDataRecievedEventHandlerDelegate:
                    //   1) time-based when time/speed are real numbers
                    //   2) frame-based when time=N/A (common for some mkv/sources)
                    if (line.Contains("frame=", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("time=", StringComparison.OrdinalIgnoreCase))
                    {
                        var (pos, speed) = SeriesDownloader.GetCurrentTimeAndSpeed(line);
                        ulong currentFrame = SeriesDownloader.GetCurrentFrameFromLine(line);

                        if (pos.Ticks != 0 && speed != 0M && durationSeconds > 1)
                        {
                            // Time-based (primary) — same formula as main
                           
                            double dpercent = ((pos.TotalSeconds + 1) * 100D) / (durationSeconds + 1);
                            double pct = Math.Truncate(dpercent * 100) / 100;
                            if (pct > 100) pct = 100;
                            //Console.WriteLine($"Using time based': totalseconds:{pos.TotalSeconds} durationseconds:{durationSeconds} percent: {lastPercent}");
                            lock (progressLock) { lastPercent = pct; }
                        }
                        else if (currentFrame > 0 && totalFrames > 0)
                        {
                            // Frame-based fallback when time=N/A / speed=N/A
                            double dpercent = ((currentFrame + 1) * 100D) / (totalFrames + 1);
                            double pct = Math.Truncate(dpercent * 100) / 100;
                            //Console.WriteLine($"Using frame based': currentFrame:{currentFrame} totalFrames:{totalFrames} percent: {pct}");
                            if (pct > 100) pct = 100;
                            lock (progressLock) { lastPercent = pct; }
                        }
                    }

                    // -progress pipe:1 key=value (stdout) — extra path when available
                    if (line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                    {
                        if (long.TryParse(line.AsSpan("out_time_ms=".Length), out long ms) && ms >= 0 && durationSeconds > 1)
                        {
                            double pct = Math.Min(100, Math.Truncate((ms / 1000.0) * 10000.0 / durationSeconds) / 100.0);
                            lock (progressLock) { lastPercent = pct; }
                        }
                    }
                    else if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
                    {
                        if (long.TryParse(line.AsSpan("out_time_us=".Length), out long us) && us >= 0 && durationSeconds > 1)
                        {
                            double pct = Math.Min(100, Math.Truncate((us / 1_000_000.0) * 10000.0 / durationSeconds) / 100.0);
                            lock (progressLock) { lastPercent = pct; }
                        }
                    }
                    else if (line.StartsWith("out_time=", StringComparison.Ordinal))
                    {
                        if (TryParseOutTime(line.AsSpan("out_time=".Length), out double seconds) && durationSeconds > 1)
                        {
                            double pct = Math.Min(100, Math.Truncate(seconds * 10000.0 / durationSeconds) / 100.0);
                            lock (progressLock) { lastPercent = pct; }
                        }
                    }
                    else if (line.Equals("progress=end", StringComparison.Ordinal))
                    {
                        lock (progressLock) { lastPercent = 100; }
                    }
                }

                using Process p = new();
                p.StartInfo.FileName = "ffmpeg";
                p.StartInfo.Arguments = args;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.CreateNoWindow = true;
                // Critical: ffmpeg status lines use \r; leave the streams in binary-ish mode
                // and parse both CR and LF ourselves (Begin*ReadLine only splits on \n).
                p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                p.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                p.Start();

                // Drain stdout + stderr on background threads, splitting on \r and \n
                // so we see the same progress lines the main instance sees on ErrorDataReceived.
                var stdoutThread = new Thread(() => ReadFfmpegStream(p.StandardOutput.BaseStream, HandleFfmpegLine))
                { IsBackground = true, Name = "ffmpeg-stdout" };
                var stderrThread = new Thread(() => ReadFfmpegStream(p.StandardError.BaseStream, HandleFfmpegLine))
                { IsBackground = true, Name = "ffmpeg-stderr" };
                stdoutThread.Start();
                stderrThread.Start();

                // Heartbeat: write claim immediately, then every 2s (progress + heartbeat_utc).
                using var heartbeatDone = new ManualResetEventSlim(false);
                var heartbeat = new Thread(() =>
                {
                    double reported = -1;
                    do
                    {
                        double pct;
                        lock (progressLock) { pct = lastPercent; }
                        EncodingClaim.UpdateProgress(tempFolder, pct, "Worker-Encoding");
                        if (Math.Abs(pct - reported) >= 0.5)
                        {
                            reported = pct;
                            WriteLine($"  {episodeName}: {pct:0.#}%");
                        }
                    } while (!heartbeatDone.Wait(2000));
                })
                {
                    IsBackground = true,
                    Name = "claim-heartbeat"
                };
                heartbeat.Start();

                p.WaitForExit();
                heartbeatDone.Set();
                stdoutThread.Join(3000);
                stderrThread.Join(3000);
                heartbeat.Join(3000);

                if (p.ExitCode != 0)
                {
                    WriteLine($"  ffmpeg exit {p.ExitCode} for '{episodeName}'");
                    try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
                    return false;
                }

                // Optional mkvmerge optimize (same as main). Missing mkvmerge or any failure
                // must NOT discard the already-successful ffmpeg output.
                if (Path.GetExtension(finalPath).Equals(".mkv", StringComparison.OrdinalIgnoreCase))
                {
                    string tempOptimized = finalPath + ".temp.mkv";
                    try { if (File.Exists(tempOptimized)) File.Delete(tempOptimized); } catch { }

                    EncodingClaim.UpdateProgress(tempFolder, 0, "Worker-Optimizing");
                    try
                    {
                        using Process mkv = new();
                        mkv.StartInfo.FileName = "mkvmerge";
                        mkv.StartInfo.Arguments = $"-o \"{tempOptimized}\" --clusters-in-meta-seek \"{finalPath}\"";
                        mkv.StartInfo.UseShellExecute = false;
                        mkv.StartInfo.RedirectStandardError = true;
                        mkv.StartInfo.RedirectStandardOutput = true;
                        mkv.StartInfo.CreateNoWindow = true;
                        mkv.Start();
                        _ = mkv.StandardOutput.ReadToEnd();
                        _ = mkv.StandardError.ReadToEnd();
                        mkv.WaitForExit();

                        if (mkv.ExitCode == 0 && File.Exists(tempOptimized))
                        {
                            File.Delete(finalPath);
                            File.Move(tempOptimized, finalPath);
                        }
                        else
                        {
                            WriteLine($"  mkvmerge exit {mkv.ExitCode} — keeping unoptimized file");
                            try { if (File.Exists(tempOptimized)) File.Delete(tempOptimized); } catch { }
                        }
                    }
                    catch (Exception mkvEx)
                    {
                        WriteLine($"  mkvmerge unavailable or failed ({mkvEx.Message}) — keeping unoptimized file");
                        try { if (File.Exists(tempOptimized)) File.Delete(tempOptimized); } catch { }
                    }
                }

                EncodingClaim.UpdateProgress(tempFolder, 100, "Worker-Done");
                EncodingClaim.Release(tempFolder);
                return true;
            }
            catch (Exception ex)
            {
                WriteLine($"  Encode exception for '{episodeName}': {ex.Message}");
                try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
                EncodingClaim.Release(tempFolder);
                return false;
            }
        }

        /// <summary>
        /// Reads an ffmpeg redirected stream byte-by-byte and emits lines split on both
        /// '\n' and '\r'. ffmpeg rewrites its status line with CR, which Begin*ReadLine
        /// never delivers until the process exits.
        /// </summary>
        private static void ReadFfmpegStream(Stream stream, Action<string> onLine)
        {
            try
            {
                var buffer = new System.Text.StringBuilder();
                var byteBuf = new byte[1];
                while (true)
                {
                    int n = stream.Read(byteBuf, 0, 1);
                    if (n <= 0)
                    {
                        if (buffer.Length > 0)
                        {
                            onLine(buffer.ToString());
                            buffer.Clear();
                        }
                        break;
                    }

                    char c = (char)byteBuf[0];
                    if (c == '\r' || c == '\n')
                    {
                        if (buffer.Length > 0)
                        {
                            onLine(buffer.ToString());
                            buffer.Clear();
                        }
                    }
                    else
                    {
                        buffer.Append(c);
                    }
                }
            }
            catch
            {
                // Stream closed when process exits — expected.
            }
        }

        /// <summary>
        /// Uses ffprobe to get duration in seconds. Returns 0 on failure.
        /// </summary>
        private static double ProbeDurationSeconds(string filePath)
        {
            try
            {
                using Process p = new();
                p.StartInfo.FileName = "ffprobe";
                p.StartInfo.Arguments =
                    $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                string output = p.StandardOutput.ReadToEnd().Trim();
                _ = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                if (p.ExitCode == 0 &&
                    double.TryParse(output, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double seconds) &&
                    seconds > 0)
                {
                    return seconds;
                }
            }
            catch (Exception ex)
            {
                WriteLine($"[WARN] ffprobe duration failed: {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// Estimates total video frames for frame-based progress when ffmpeg reports time=N/A.
        /// Tries nb_frames first, then duration * avg_frame_rate.
        /// </summary>
        private static ulong ProbeTotalFrames(string filePath, double durationSeconds)
        {
            try
            {
                using Process p = new();
                p.StartInfo.FileName = "ffprobe";
                p.StartInfo.Arguments =
                    $"-v error -select_streams v:0 -show_entries stream=nb_frames,avg_frame_rate -of default=noprint_wrappers=1 \"{filePath}\"";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                _ = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);

                ulong nbFrames = 0;
                double fps = 0;
                foreach (string raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("nb_frames=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line["nb_frames=".Length..];
                        if (v != "N/A")
                            _ = ulong.TryParse(v, out nbFrames);
                    }
                    else if (line.StartsWith("avg_frame_rate=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line["avg_frame_rate=".Length..];
                        // e.g. 24000/1001 or 25/1
                        string[] parts = v.Split('/');
                        if (parts.Length == 2 &&
                            double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double num) &&
                            double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double den) &&
                            den != 0)
                        {
                            fps = num / den;
                        }
                        else if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out double flat))
                        {
                            fps = flat;
                        }
                    }
                }

                if (nbFrames > 0)
                    return nbFrames;

                if (fps > 0 && durationSeconds > 1)
                    return (ulong)Math.Max(1, Math.Round(durationSeconds * fps));
            }
            catch (Exception ex)
            {
                WriteLine($"[WARN] ffprobe frame count failed: {ex.Message}");
            }
            return 0;
        }

        private static bool TryParseOutTime(ReadOnlySpan<char> value, out double seconds)
        {
            seconds = 0;
            // formats: HH:MM:SS.microseconds
            string s = value.ToString();
            string[] parts = s.Split(':');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], out int h)) return false;
            if (!int.TryParse(parts[1], out int m)) return false;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double sec)) return false;
            seconds = h * 3600 + m * 60 + sec;
            return true;
        }

        private static void WriteState(string tempDir, string stateName)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(tempDir, "state.*"))
                {
                    try { File.Delete(file); } catch { }
                }
                File.WriteAllText(Path.Combine(tempDir, "state." + stateName), stateName);
            }
            catch (Exception ex)
            {
                WriteLine($"[WARN] Could not write state.{stateName}: {ex.Message}");
            }
        }

        private static void WriteLine(string message)
        {
            lock (ConsoleLock)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            }
        }

        /// <summary>
        /// Returns true if the named executable can be started (exists on PATH).
        /// </summary>
        private static bool ToolAvailable(string name, string versionArg)
        {
            try
            {
                using Process p = new();
                p.StartInfo.FileName = name;
                p.StartInfo.Arguments = versionArg;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                _ = p.StandardOutput.ReadToEnd();
                _ = p.StandardError.ReadToEnd();
                p.WaitForExit(8000);
                return true; // started successfully; exit code of --version is irrelevant
            }
            catch
            {
                return false;
            }
        }

    }
}

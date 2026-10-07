using System.Globalization;
using System.Text;

namespace AniDownloaderTerminal
{
    /// <summary>
    /// File-based claim used so multiple machines can cooperate on encoding without a central
    /// orchestrator. Each episode's .temp folder may contain a worker.claim file that records
    /// who is encoding it, progress, and a heartbeat. Stale claims (no heartbeat within the
    /// configured timeout) are treated as free and may be taken by another worker.
    /// </summary>
    public sealed class EncodingClaim
    {
        public string WorkerId { get; set; } = string.Empty;
        public string Hostname { get; set; } = string.Empty;
        public int Pid { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime HeartbeatUtc { get; set; }
        public double Progress { get; set; }
        public string Status { get; set; } = "Claimed";
        public string Source { get; set; } = string.Empty;
        public string EpisodeName { get; set; } = string.Empty;

        public static string ClaimFileName => Settings.WorkerClaimFileName;

        public static string GetClaimPath(string tempFolder) =>
            Path.Combine(tempFolder, ClaimFileName);

        /// <summary>
        /// Builds a stable worker id for this process (hostname-pid, or the configured WorkerId).
        /// </summary>
        public static string GetLocalWorkerId()
        {
            if (!string.IsNullOrWhiteSpace(Settings.WorkerId))
                return Settings.WorkerId.Trim();
            string host = Environment.MachineName;
            if (string.IsNullOrWhiteSpace(host))
                host = "unknown";
            return $"{host}-{Environment.ProcessId}";
        }

        public static EncodingClaim? TryRead(string tempFolder)
        {
            string path = GetClaimPath(tempFolder);
            if (!File.Exists(path))
                return null;

            try
            {
                var claim = new EncodingClaim();
                foreach (string raw in File.ReadLines(path))
                {
                    string line = raw.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                        continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string key = line[..eq].Trim();
                    string value = line[(eq + 1)..].Trim();
                    switch (key.ToLowerInvariant())
                    {
                        case "id":
                            claim.WorkerId = value;
                            break;
                        case "hostname":
                            claim.Hostname = value;
                            break;
                        case "pid":
                            if (int.TryParse(value, out int pid))
                                claim.Pid = pid;
                            break;
                        case "started_utc":
                            if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started))
                                claim.StartedUtc = started;
                            break;
                        case "heartbeat_utc":
                            if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var hb))
                                claim.HeartbeatUtc = hb;
                            break;
                        case "progress":
                            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double prog))
                                claim.Progress = prog;
                            break;
                        case "status":
                            claim.Status = value;
                            break;
                        case "source":
                            claim.Source = value;
                            break;
                        case "episode":
                            claim.EpisodeName = value;
                            break;
                    }
                }

                if (string.IsNullOrWhiteSpace(claim.WorkerId))
                    return null;
                return claim;
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.Debug_Log($"TryRead claim failed for '{tempFolder}': {ex.Message}", "EncodingClaim");
                return null;
            }
        }

        /// <summary>
        /// True when a claim exists and its heartbeat is still within the stale timeout.
        /// When <paramref name="byOtherThanWorkerId"/> is set, claims owned by that id are ignored
        /// (useful for "is someone else holding this?").
        /// </summary>
        public static bool IsActivelyClaimed(string tempFolder, string? byOtherThanWorkerId = null)
        {
            var claim = TryRead(tempFolder);
            if (claim == null)
                return false;
            if (!IsFresh(claim))
                return false;
            if (byOtherThanWorkerId != null &&
                string.Equals(claim.WorkerId, byOtherThanWorkerId, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        public static bool IsFresh(EncodingClaim claim)
        {
            double ageSeconds = (DateTime.UtcNow - claim.HeartbeatUtc).TotalSeconds;
            return ageSeconds >= 0 && ageSeconds <= Settings.WorkerClaimStaleSeconds;
        }

        /// <summary>
        /// Attempts to create or refresh a claim owned by this worker.
        /// Returns the claim on success, null if another worker holds a fresh claim.
        /// </summary>
        public static EncodingClaim? TryAcquire(string tempFolder, string episodeName, string sourcePath, string? status = null)
        {
            string localId = GetLocalWorkerId();
            var existing = TryRead(tempFolder);

            if (existing != null && IsFresh(existing) &&
                !string.Equals(existing.WorkerId, localId, StringComparison.OrdinalIgnoreCase))
            {
                return null; // held by someone else
            }

            DateTime started = DateTime.UtcNow;
            double progress = 0;
            if (existing != null &&
                string.Equals(existing.WorkerId, localId, StringComparison.OrdinalIgnoreCase))
            {
                started = existing.StartedUtc;
                progress = existing.Progress;
            }

            var claim = new EncodingClaim
            {
                WorkerId = localId,
                Hostname = Environment.MachineName,
                Pid = Environment.ProcessId,
                StartedUtc = started,
                HeartbeatUtc = DateTime.UtcNow,
                Progress = progress,
                Status = status ?? "Claimed",
                Source = sourcePath,
                EpisodeName = episodeName
            };

            if (!Write(tempFolder, claim))
                return null;
            return claim;
        }

        public static bool Write(string tempFolder, EncodingClaim claim)
        {
            try
            {
                if (!Directory.Exists(tempFolder))
                    Directory.CreateDirectory(tempFolder);

                var sb = new StringBuilder();
                sb.AppendLine($"id={claim.WorkerId}");
                sb.AppendLine($"hostname={claim.Hostname}");
                sb.AppendLine($"pid={claim.Pid}");
                sb.AppendLine($"started_utc={claim.StartedUtc.ToUniversalTime():o}");
                sb.AppendLine($"heartbeat_utc={claim.HeartbeatUtc.ToUniversalTime():o}");
                sb.AppendLine($"progress={claim.Progress.ToString("0.##", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"status={claim.Status}");
                sb.AppendLine($"source={claim.Source}");
                sb.AppendLine($"episode={claim.EpisodeName}");

                string path = GetClaimPath(tempFolder);
                string content = sb.ToString();

                // Prefer direct write — File.Move(overwrite) is unreliable on some
                // SMB/AFP/NFS mounts (e.g. /Volumes/... on macOS).
                try
                {
                    File.WriteAllText(path, content);
                    return true;
                }
                catch
                {
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, content);
                    if (File.Exists(path))
                        File.Delete(path);
                    File.Move(tmp, path);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.Debug_Log($"Write claim failed for '{tempFolder}': {ex.Message}", "EncodingClaim");
                return false;
            }
        }

        private static void UpdateWorkerFile(string tempFolder, double progress, string status)
        {
            try
            {
                string workerId = GetLocalWorkerId();
                string workerFileName = workerId + ".worker";

                // Prefer the SearchPath that is a prefix of the temp folder
                string? baseDir = Settings.SearchPaths
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Select(d => d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    .FirstOrDefault(d => tempFolder.StartsWith(d, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(baseDir)) return;

                string workerFilePath = Path.Combine(baseDir, workerFileName);
                string content =
                    workerId + "\n" +
                    tempFolder + "\n" +
                    progress.ToString("0.##", CultureInfo.InvariantCulture) + "\n" +
                    status;

                File.WriteAllText(workerFilePath, content);
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.Debug_Log($"UpdateWorkerFile failed: {ex.Message}", "EncodingClaim");
            }
        }


        public static void UpdateProgress(string tempFolder, double progress, string status)
        {
            string localId = GetLocalWorkerId();
            var claim = TryRead(tempFolder);

            // A late ffmpeg/mkv callback can arrive after Release. Do not recreate the
            // claim or the *.worker progress file once the episode is already encoded.
            if (HasFinishedEncodeState(tempFolder))
            {
                ClearWorkerProgressFiles(tempFolder);
                return;
            }

            if (claim != null &&
                !string.Equals(claim.WorkerId, localId, StringComparison.OrdinalIgnoreCase) &&
                IsFresh(claim))
            {
                return; // someone else holds a fresh claim
            }

            if (claim == null)
            {
                // Claim file missing (network glitch, manual delete) — recreate so
                // progress reporting and main-instance UI keep working.
                string name = Path.GetFileName(tempFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase))
                    name = name[..^5];

                claim = new EncodingClaim
                {
                    WorkerId = localId,
                    Hostname = Environment.MachineName,
                    Pid = Environment.ProcessId,
                    StartedUtc = DateTime.UtcNow,
                    EpisodeName = name
                };
            }

            claim.Progress = Math.Clamp(progress, 0, 100);
            claim.Status = status;
            claim.HeartbeatUtc = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(claim.WorkerId))
                claim.WorkerId = localId;
            Write(tempFolder, claim);
            UpdateWorkerFile(tempFolder, progress, status);
        }

        public static void Release(string tempFolder)
        {
            try
            {
                string path = GetClaimPath(tempFolder);
                if (File.Exists(path))
                {
                    EncodingClaim? claim = TryRead(tempFolder);

                    // Only refuse to delete if a *different* worker still holds a fresh claim.
                    if (claim != null &&
                        IsFresh(claim) &&
                        !string.Equals(claim.WorkerId, GetLocalWorkerId(), StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    // Retry a few times — on NFS/SMB the heartbeat thread may still be writing.
                    // Do not return on success: the *.worker progress file still has to be removed.
                    for (int i = 0; i < 5; i++)
                    {
                        try
                        {
                            if (!File.Exists(path))
                                break;
                            File.Delete(path);
                            break;
                        }
                        catch (IOException) when (i < 4)
                        {
                            Thread.Sleep(150 + i * 100);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.Debug_Log($"Release claim failed for '{tempFolder}': {ex.Message}", "EncodingClaim");
            }

            ClearWorkerProgressFiles(tempFolder);
        }

        /// <summary>
        /// True when <paramref name="tempFolder"/> already has a finished encode state file.
        /// </summary>
        public static bool HasFinishedEncodeState(string tempFolder)
        {
            if (string.IsNullOrWhiteSpace(tempFolder))
                return false;
            return File.Exists(Path.Combine(tempFolder, "state.EncodedFound"))
                || File.Exists(Path.Combine(tempFolder, "state.EncodedSeeding"));
        }

        /// <summary>
        /// True when a *.worker progress file still describes an encode that has not finished.
        /// A missing folder, a finished state marker, or a missing/stale claim means the file is leftover.
        /// </summary>
        public static bool IsWorkerProgressCurrent(string tempFolder)
        {
            if (string.IsNullOrWhiteSpace(tempFolder) || !Directory.Exists(tempFolder))
                return false;
            if (HasFinishedEncodeState(tempFolder))
                return false;
            EncodingClaim? claim = TryRead(tempFolder);
            return claim != null && IsFresh(claim);
        }

        /// <summary>
        /// Deletes *.worker progress files that point at <paramref name="tempFolder"/>.
        /// </summary>
        /// <remarks>
        /// How it works: scans each search path for *.worker files and removes those whose episode path matches, plus this process's own worker file. Release used to return before this cleanup, so finished encodes stayed on the series list.
        /// </remarks>
        public static void ClearWorkerProgressFiles(string tempFolder)
        {
            try
            {
                string workerId = GetLocalWorkerId();
                foreach (string dir in Settings.SearchPaths)
                {
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                        continue;

                    string ownFile = Path.Combine(dir, workerId + ".worker");
                    DeleteWorkerFileIfMatches(ownFile, tempFolder);

                    foreach (string workerFile in Directory.GetFiles(dir, "*.worker"))
                        DeleteWorkerFileIfMatches(workerFile, tempFolder);
                }
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.Debug_Log($"ClearWorkerProgressFiles failed for '{tempFolder}': {ex.Message}", "EncodingClaim");
            }
        }

        /// <summary>
        /// Deletes a worker progress file when its episode path matches <paramref name="tempFolder"/>.
        /// </summary>
        private static void DeleteWorkerFileIfMatches(string workerFile, string tempFolder)
        {
            try
            {
                if (!File.Exists(workerFile))
                    return;

                string[] content = File.ReadAllLines(workerFile);
                if (content.Length < 2)
                    return;
                if (PathsReferToSameFolder(content[1], tempFolder))
                    File.Delete(workerFile);
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.Debug_Log($"DeleteWorkerFileIfMatches failed for '{workerFile}': {ex.Message}", "EncodingClaim");
            }
        }

        /// <summary>
        /// Compares episode temp paths without caring about slash style or a trailing separator.
        /// </summary>
        private static bool PathsReferToSameFolder(string left, string right)
        {
            string normalizedLeft = left.Replace('\\', '/').Trim().TrimEnd('/');
            string normalizedRight = right.Replace('\\', '/').Trim().TrimEnd('/');
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Scans series roots for active remote claims so the main UI can show worker progress.
        /// </summary>
        public static List<EncodingClaim> FindActiveRemoteClaims(IEnumerable<string> seriesPaths)
        {
            var results = new List<EncodingClaim>();
            string localId = GetLocalWorkerId();

            foreach (string seriesPath in seriesPaths)
            {
                if (string.IsNullOrWhiteSpace(seriesPath) || !Directory.Exists(seriesPath))
                    continue;

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
                    EncodingClaim? claim = TryRead(tempDir);
                    if (claim == null || !IsFresh(claim) || HasFinishedEncodeState(tempDir))
                        continue;
                    if (string.Equals(claim.WorkerId, localId, StringComparison.OrdinalIgnoreCase))
                        continue; // local work is already tracked via Episodes dict

                    if (string.IsNullOrWhiteSpace(claim.EpisodeName))
                        claim.EpisodeName = Path.GetFileNameWithoutExtension(tempDir).Trim();

                    results.Add(claim);
                }
            }

            return results;
        }
    }
}
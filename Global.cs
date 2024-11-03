using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AniDownloaderTerminal
{
    public static class Global
    {
        public static readonly TaskAdmin.Utility.TaskAdmin TaskAdmin = new();
        public static readonly Queue<string> CurrentOpsQueue = new();

        public readonly static string Exepath = AppDomain.CurrentDomain.BaseDirectory;
        public readonly static string SeriesTableFilePath = Path.Combine(Exepath,"SeriesData.xml");
        public readonly static string SettingsPath = Path.Combine(Global.Exepath, "AniDownloader.cfg");

        public static readonly DataTable SeriesTable = new("Series");
        public static readonly DataTable CurrentStatusTable = new("Torrent Status");

        private static DateTime LastRequestTime;
        private static readonly HttpClient httpClient = new();


        private static async Task DelayAsync()
        {
            TimeSpan timeElapsed = DateTime.UtcNow - LastRequestTime;
            while (timeElapsed.TotalMilliseconds < Settings.RPSDelayMs)
            {
                await Task.Delay(100);
                timeElapsed = DateTime.UtcNow - LastRequestTime;
            }
            LastRequestTime = DateTime.UtcNow;
        }

        private static async Task<T?> PerformHttpOperationAsync<T>(Func<HttpResponseMessage, Task<T>> operation, string url)
        {
            CurrentOpsQueue.Enqueue($"Loading: {url}");
            try
            {
                await DelayAsync();
                using HttpResponseMessage response = await httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                return await operation(response);
            }
            catch (Exception ex)
            {
                TaskAdmin.Logger.EX_Log(ex.Message, "PerformHttpOperationAsync");
                return default;
            }
        }

        public static async Task<string> GetWebStringFromUrl(string url)
        {
            string? result = await PerformHttpOperationAsync<string>(async response => await response.Content.ReadAsStringAsync(), url);
            return result ?? string.Empty;
        }

        public static async Task<Stream?> DownloadFileTask(string url)
        {
            return await PerformHttpOperationAsync(async response =>
            {
                var memoryStream = new MemoryStream();
                await response.Content.CopyToAsync(memoryStream);
                memoryStream.Position = 0;
                return memoryStream;
            }, url);
        }

        public static string GetWebStringFromUrlNonAsync(string url)
        {
            string? result = Task.Run(() => GetWebStringFromUrl(url)).GetAwaiter().GetResult();
            return result ?? string.Empty;
        }

        public static bool DownloadFileToPath(string url, string filePath)
        {
            try
            {
                using FileStream fileStream = new(filePath, FileMode.CreateNew);
                using Stream? stream = Task.Run(() => DownloadFileTask(url)).GetAwaiter().GetResult();

                if (stream == null)
                {
                    TaskAdmin.Logger.EX_Log("Failed to download file: Stream is null", "DownloadFileToPath");
                    return false;
                }

                stream.CopyTo(fileStream);
                return true;
            }
            catch (Exception ex)
            {
                TaskAdmin.Logger.EX_Log(ex.Message, "DownloadFileToPath");
                return false;
            }
        }

        public static decimal ParseFileSize(string sizeStr)
        {
            var sizeMultiplier = new Dictionary<string, decimal> { { "KIB", 1m / 1000 }, { "MIB", 1m }, { "GIB", 1000m } };
            var cleanedSize = sizeStr.ToUpper().Replace("KIB", "").Replace("MIB", "").Replace("GIB", "").Trim();
            var multiplier = sizeMultiplier[sizeStr.ToUpper().Split(' ').FirstOrDefault(s => sizeMultiplier.ContainsKey(s)) ?? "MIB"];
            return decimal.Parse(cleanedSize, CultureInfo.InvariantCulture) * multiplier;
        }

        public static OnlineEpisodeElement TrySelectUncensoredEpisode(OnlineEpisodeElement candidateEpisode, OnlineEpisodeElement currentEpisode)
        {
            bool isCurrentEpisodeUncensored = Regex.Match(currentEpisode.Name, Settings.UncensoredEpisodeRegex).Success;
            bool isCandidateEpisodeUncensored = Regex.Match(candidateEpisode.Name, Settings.UncensoredEpisodeRegex).Success;
            if (isCandidateEpisodeUncensored && !isCurrentEpisodeUncensored) currentEpisode = candidateEpisode;
            return currentEpisode;
        }

    }
}

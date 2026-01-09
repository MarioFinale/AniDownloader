using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
namespace AniDownloaderTerminal
{
    public static class Global
    {

        public readonly static string Exepath = AppDomain.CurrentDomain.BaseDirectory;
        public readonly static string SeriesTableFilePath = Path.Combine(Exepath, "SeriesData.xml");
        public readonly static string SettingsPath = Path.Combine(Global.Exepath, "AniDownloader.cfg");
        public readonly static string MetadataDBPath = Path.Combine(Global.Exepath, "SeriesMetadata.db");

        public static readonly DataTable SeriesTable = new("Series");
        public static readonly DataTable CurrentStatusTable = new("Torrent Status");
        private static DateTime LastRequestTime;

        private static readonly HttpClient httpClient = CreateHttpClient();


        public static readonly TaskAdmin.Utility.TaskAdmin TaskAdmin = new();
        public static readonly ConcurrentQueue<string> CurrentOpsQueue = new();
        public static AnilistMetadataProvider MetadataProvider = new();

        private static HttpClient CreateHttpClient()
        {
            HttpClient client = (new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
            client.DefaultRequestHeaders.Add(HttpRequestHeader.UserAgent.ToString(), "Anidownloader/1.2.0" );
            return client;
        }

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
        private static async Task<T?> PerformHttpGetOperationAsync<T>(Func<HttpResponseMessage, Task<T>> operation, string url)
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
        private static async Task<T?> PerformHttpPostOperationAsync<T>(Func<HttpResponseMessage, Task<T>> operation, string url, HttpContent content)
        {
            CurrentOpsQueue.Enqueue($"Posting to: {url}");
            try
            {
                await DelayAsync();
                using HttpResponseMessage response = await httpClient.PostAsync(url, content);
                response.EnsureSuccessStatusCode();
                return await operation(response);
            }
            catch (Exception ex)
            {
                TaskAdmin.Logger.EX_Log(ex.Message, "PerformHttpPostOperationAsync");
                return default;
            }
        }
        public static async Task<string> GetWebStringFromUrl(string url)
        {
            string? result = await PerformHttpGetOperationAsync<string>(async response => await response.Content.ReadAsStringAsync(), url);
            return result ?? string.Empty;
        }
        public static async Task<Stream?> DownloadFileTask(string url)
        {
            return await PerformHttpGetOperationAsync(async response =>
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
        public static async Task<string> PostWebStringToUrl(string url, HttpContent content)
        {
            string? result = await PerformHttpPostOperationAsync<string>(async response => await response.Content.ReadAsStringAsync(), url, content);
            return result ?? string.Empty;
        }
        public static async Task<Stream?> PostForStreamResponse(string url, HttpContent content)
        {
            return await PerformHttpPostOperationAsync(async response =>
            {
                var memoryStream = new MemoryStream();
                await response.Content.CopyToAsync(memoryStream);
                memoryStream.Position = 0;
                return memoryStream;
            }, url, content);
        }
        public static string PostWebStringToUrlNonAsync(string url, HttpContent content)
        {
            string? result = Task.Run(() => PostWebStringToUrl(url, content)).GetAwaiter().GetResult();
            return result ?? string.Empty;
        }
        public static bool PostAndSaveResponseToPath(string url, HttpContent content, string filePath)
        {
            try
            {
                using FileStream fileStream = new(filePath, FileMode.CreateNew);
                using Stream? stream = Task.Run(() => PostForStreamResponse(url, content)).GetAwaiter().GetResult();
                if (stream == null)
                {
                    TaskAdmin.Logger.EX_Log("Failed to post and get response: Stream is null", "PostAndSaveResponseToPath");
                    return false;
                }
                stream.CopyTo(fileStream);
                return true;
            }
            catch (Exception ex)
            {
                TaskAdmin.Logger.EX_Log(ex.Message, "PostAndSaveResponseToPath");
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

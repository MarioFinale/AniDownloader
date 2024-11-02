using Mono.Nat.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AniDownloaderTerminal
{
    public static class Global
    {
        public static TaskAdmin.Utility.TaskAdmin TaskAdmin = new();
        public static readonly Queue<string> CurrentOpsQueue = new();

        public readonly static string Exepath = AppDomain.CurrentDomain.BaseDirectory;
        public readonly static string SeriesTableFilePath = Path.Combine(Exepath,"SeriesData.xml");

        private static DateTime LastRequestTime;
        private static HttpClient httpClient = new();


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
            Global.CurrentOpsQueue.Enqueue($"Performing HTTP operation on: {url}");
            try
            {
                await DelayAsync();
                using (HttpResponseMessage response = await httpClient.GetAsync(url))
                {
                    response.EnsureSuccessStatusCode();
                    return await operation(response);
                }
            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.EX_Log(ex.Message, "PerformHttpOperationAsync");
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
            return await PerformHttpOperationAsync<Stream>(response => response.Content.ReadAsStreamAsync(), url);
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
                using (FileStream fileStream = new FileStream(filePath, FileMode.CreateNew))
                {
                    using (Stream? stream = Task.Run(() => DownloadFileTask(url)).GetAwaiter().GetResult())
                    {
                        if (stream == null)
                        {
                            TaskAdmin.Logger.EX_Log("Failed to download file: Stream is null", "DownloadFileToPath");
                            return false;
                        }
                        stream.CopyTo(fileStream);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                TaskAdmin.Logger.EX_Log(ex.Message, "DownloadFileToPath");
                return false;
            }
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

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
        public static HttpClient httpClient = new();

        public readonly static string Exepath = AppDomain.CurrentDomain.BaseDirectory;
        public readonly static string SeriesTableFilePath = Exepath + "/SeriesData.xml";

        private static DateTime LastRequestTime;

        public static async Task<string> GetWebDataFromUrl(string url)
        {
            TimeSpan time = DateTime.UtcNow - LastRequestTime;
            while (time.TotalSeconds < 2)
            {
                await Task.Run(() =>
                {
                    Thread.Sleep(100);
                });
                time = DateTime.UtcNow - LastRequestTime;
            }

            Global.CurrentOpsQueue.Enqueue("Loading web resource: " + url);
            try
            {
                using HttpResponseMessage responseMessage = await Global.httpClient.GetAsync(url);
                using HttpContent content = responseMessage.Content;
                Thread.Sleep(1500); //Prevent server-side throttling
                LastRequestTime = DateTime.UtcNow;
                return await content.ReadAsStringAsync();

            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.EX_Log(ex.Message, "GetWebDataFromUrl");
                return string.Empty;
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

using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace AniDownloaderTerminal
{
    [Serializable]
    public class Series : ISerializable
    {
        public string Name { get; }
        public string Path { get; }
        public int Offset { get; }

        public string Filter { get; }

        public Series(string name, string path, int offset, string filter) 
        {
            if (name == null) throw new ArgumentNullException(name);
            if (path == null) throw new ArgumentNullException(path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            Name = ReplaceIllegalCharacters(name);
            Path = path;
            Offset = offset;
            Filter = filter;
        }

        public Series(SerializationInfo info, StreamingContext context) 
        {
            string? name = (string?)info.GetValue("Name", typeof(string));
            string? path = (string?)info.GetValue("Path", typeof(string));
            int? offset = (int?)info.GetValue("Offset", typeof(int));
            string? filter = (string?)info.GetValue("Filter", typeof(int));
            if (name == null) throw new ArgumentNullException(name);
            if (path == null) throw new ArgumentNullException(path);
            if (offset == null) throw new ArgumentNullException(offset.ToString());
            if (filter == null) throw new ArgumentNullException(filter);
            Name = ReplaceIllegalCharacters(name);
            Path = path;
            Offset = (int)offset;
            Filter = filter;
        }

        public int[] GetEpisodesDownloaded()
        {
           HashSet<int> episodes = new();
           foreach (string fileName in Directory.GetFiles(Path))
            {
                Match match = Regex.Match(fileName, @"(\d{2,3})\.(?:mp4|mkv)");
                if (match.Success)
                {
                    string epNumStr = match.Groups[1].Value;
                    int epNum = int.Parse(epNumStr);
                    episodes.Add(epNum);
                }
            }
           return episodes.ToArray();
        }

        public int[] GetSubsDownloaded(string shortLang)
        {
            if (shortLang == null) throw new ArgumentNullException(shortLang);
            HashSet<int> subs = new();
            foreach (string fileName in System.IO.Directory.GetFiles(Path))
            {
                Match match = Regex.Match(fileName, @"(\d{2,3})\." + shortLang + "(.ass|.srt)");
                if (match.Success)
                {
                    string subNumSrt = match.Groups[1].Value;
                    int subNum = int.Parse(subNumSrt);
                    subs.Add(subNum);
                }
            }
            return subs.ToArray();
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("Name", Name, typeof(string));
            info.AddValue("Path", Path, typeof(string));
            info.AddValue("Offset", Offset, typeof(int));
            info.AddValue("Filter", Filter, typeof(string));
        }

        public static string ReplaceIllegalCharacters(string text)
        {
            return text.Replace('/', ' ').Replace('\\', ' ').Replace('?', ' ').Replace('%', ' ').Replace('*', ' ').Replace(':', ' ').Replace('|', ' ').Replace('\\', ' ').Replace('<', ' ').Replace('>', ' ').Replace("\"",string.Empty);
        }


        public async Task<OnlineEpisodeElement[]> GetAvailableSeriesEpisodes()
        {
            List<OnlineEpisodeElement> episodes = new();
            string seriesUrlEncoded = System.Web.HttpUtility.UrlEncode(Name);
            string content = await Global.GetWebStringFromUrl("https://nyaa.si/?page=rss&q=" + seriesUrlEncoded + "&c=1_0&f=0");
            string[] list = OnlineEpisodeElement.GetOnlineEpisodesListFromContent(content);

            foreach (string item in list)
            {
                OnlineEpisodeElement element = new(item);
                if (element == null) continue;
                if (element.ProbableEpNumber == null) continue;
                if (!element.IsAnime) continue;
                if (element.IsTooOld) continue;
                if (element.IsTooNew) continue;
                if (!String.IsNullOrWhiteSpace(Filter))
                {
                    if (Regex.Match(element.Name, Filter).Success) continue;
                }
                if (Settings.ExcludeBatchReleases && element.Name.ToUpperInvariant().Contains("BATCH")) continue;
                if (element.SizeMiB > Settings.MaxFileSizeMb)
                {
                    Global.TaskAdmin.Logger.Log(element.Name + " discarded due to big size (over " + Settings.MaxFileSizeMb.ToString() + "MB).", "GetAvailableSeriesEpisodes");
                    continue;
                }
                element.AddEpisodeNumberOffset(Offset);
                episodes.Add(element);
            }
            return episodes.ToArray();
        }


    }



    


}

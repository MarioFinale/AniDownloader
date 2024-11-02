using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace AniDownloaderTerminal
{
    public class OnlineEpisodeElement
    {
        public string ViewUrl { get; }
        public string TorrentUrl { get; }
        public string Hash { get; }
        public int Id { get; }
        public string Name { get; }
        public string Category { get; }
        public decimal SizeMiB { get; }
        public string ProbableRes { get; }
        private int _ProbableEpNumber;
        public int? ProbableEpNumber { get { return _ProbableEpNumber; } }
        public Lang ProbableLang { get; set; }
        public bool IsAnime { get; }

        public bool IsTooOld { get; }
        public bool IsTooNew { get; }

        public OnlineEpisodeElement(string webCode)
        {
            Match textMatch = Regex.Match(webCode, "<pubDate>(.+)<\\/pubDate>[\\s\\S]+?CDATA.+?#(\\d{1,8}) \\| (.+?)<\\/a\\> \\| (.+?) \\| (.+?) \\| (.+?)\\]");
            if (!textMatch.Success) throw new InvalidDataException("Input string does not match nyaa.si cdata description format.");
            string idStr = textMatch.Groups[2].Value.Trim();
            Id = int.Parse(idStr);
            Name = textMatch.Groups[3].Value.Trim();
            string sizeStr = textMatch.Groups[4].Value.Trim();
            decimal multiplier = 1;
            if (sizeStr.ToUpperInvariant().Contains("GIB")) multiplier *= 1000;
            if (sizeStr.ToUpperInvariant().Contains("KIB")) multiplier /= 1000;
            sizeStr = sizeStr.Replace("GiB", "").Replace("MiB", "").Replace("KiB", "");
            sizeStr = sizeStr.Trim();
            SizeMiB = decimal.Parse(sizeStr, CultureInfo.InvariantCulture) * multiplier;
            Category = textMatch.Groups[5].Value.Trim();
            Hash = textMatch.Groups[6].Value.Trim();
            ViewUrl = "https://nyaa.si/view/" + idStr;
            TorrentUrl = "https://nyaa.si/download/" + idStr + ".torrent";
            ProbableLang = Lang.Undefined;

            Match resolutionMatch = Regex.Match(Name, "\\d{3,4}[pi]");
            if (resolutionMatch.Success)
            {
                ProbableRes = resolutionMatch.Value.Trim();
            }
            else
            {
                if (SizeMiB > 500)
                {
                    ProbableRes = "1080p";
                }
                else
                {
                    ProbableRes = "720p";
                }
            }

            IsTooOld = false;

            string pubdate = textMatch.Groups[1].Value.Trim();
            Match dateMatch = Regex.Match(pubdate, ", (\\d{1,2}) (\\w{3,4}) (\\d{4})");

            if (dateMatch.Success) {
                string day = dateMatch.Groups[1].Value.Trim();
                string month = dateMatch.Groups[2].Value.Trim().ToLowerInvariant()[..3];
                string year = dateMatch.Groups[3].Value.Trim();

                int monthInt = 0;
                _ = int.TryParse(year, out int yearInt);
                _ = int.TryParse(day, out int dayInt);

                switch (month)
                {
                    case "jan":
                        monthInt = 1; break;
                    case "feb":
                        monthInt = 2; break;
                    case "mar":
                        monthInt = 3; break;
                    case "apr":
                        monthInt = 4; break;
                    case "may":
                        monthInt = 5; break;
                    case "jun":
                        monthInt = 6; break;
                    case "jul":
                        monthInt = 7; break;
                    case "aug":
                        monthInt = 8; break;
                    case "sep":
                        monthInt = 9; break;
                    case "oct":
                        monthInt = 10; break;
                    case "nov":
                        monthInt = 11; break;
                    case "dec":
                        monthInt = 12; break;
                    default:
                        break;
                }

                DateTime dateTime = new DateTime(yearInt, monthInt, dayInt);
                TimeSpan span = DateTime.Now.Subtract(dateTime);
                if (span > TimeSpan.FromDays(60))
                {
                    IsTooOld = true;
                }

                if (span < TimeSpan.FromMinutes(30)) //Let's wait at least 30min
                {
                    IsTooNew = true;
                }

            }





            Match epMatch = Regex.Match(Name, "(?:(?:s(?:eason)*\\d{1,2} *ep*| - )(\\d{1,2})|(\\d{1,2}) of \\d{1,2})", RegexOptions.IgnoreCase);
            if (epMatch.Success)
            {
                string epStr = epMatch.Groups[1].Value.Trim();
                if (!String.IsNullOrEmpty(epMatch.Groups[2].Value.Trim())) epStr = epMatch.Groups[2].Value.Trim();
                _ProbableEpNumber = int.Parse(epStr, CultureInfo.InvariantCulture);
            }

            IsAnime = Category.Contains("Anime");

            if (IsAnime)
            {
                if (Category.Contains("Anime - Raw"))
                {
                    ProbableLang = Lang.RAW;
                }
                else
                {
                    bool isSpanish = Regex.Match(Name, "[^\\w]esp[^\\w]|spa[^\\w]|español|spanish", RegexOptions.IgnoreCase).Success;
                    bool isEnglish = Regex.Match(Name, "[^\\w]eng[^\\w]|english", RegexOptions.IgnoreCase).Success;

                    if (isSpanish && isEnglish)
                    {
                        ProbableLang = Lang.CustomAndEng;
                    }
                    else if (isEnglish)
                    {
                        ProbableLang = Lang.Eng;
                    }
                    else if (isSpanish)
                    {
                        ProbableLang = Lang.Custom;
                    }
                }
            }
        }

        public void AddEpisodeNumberOffset(int Offset) {

            _ProbableEpNumber += Offset; 
        }

        public static string[] GetOnlineEpisodesListFromContent(string content)
        {
            MatchCollection matches = Regex.Matches(content, "<pubDate>(.+)<\\/pubDate>[\\s\\S]+?CDATA.+?#(\\d{1,8}) \\| (.+?)<\\/a\\> \\| (.+?) \\| (.+?) \\| (.+?)\\]");
            return matches.Select(i => i.Value).ToArray();
        }

        public Lang GetProbableLanguage()
        {
            string pageText = Global.GetWebStringFromUrlNonAsync(ViewUrl);
            Match descriptionMatch = Regex.Match(pageText, "id=\"torrent-description\">(.*?)<\\/div>", RegexOptions.Singleline);
            Lang resultLang = Lang.Undefined;

            if (descriptionMatch.Success)
            {
                string description = Name + " " + descriptionMatch.Groups[1].Value;

                bool isSpanish = Regex.Match(description, "[^\\w]esp[^\\w]|spa[^\\w]| es[^\\w]|español|spanish", RegexOptions.IgnoreCase).Success;
                bool isEnglish = Regex.Match(description, "[^\\w]eng[^\\w]|english", RegexOptions.IgnoreCase).Success;

                if (isSpanish && isEnglish)
                {
                    resultLang = Lang.CustomAndEng;
                }
                else if (isEnglish)
                {
                    resultLang = Lang.Eng;
                }
                else if (isSpanish)
                {
                    resultLang = Lang.Custom;
                }
            }

            return resultLang;
        }

        






    }

}


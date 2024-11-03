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
        private bool _IsAnime;
        public bool IsAnime { get { return _IsAnime; } }
        private bool _IsTooOld;
        public bool IsTooOld { get { return _IsTooOld; } }
        private bool _IsTooNew;
        public bool IsTooNew { get { return _IsTooNew; } }


        public OnlineEpisodeElement(string webCode)
        {
            var match = Regex.Match(webCode, "<pubDate>(.+)<\\/pubDate>[\\s\\S]+?CDATA.+?#(\\d{1,8}) \\| (.+?)<\\/a\\> \\| (.+?) \\| (.+?) \\| (.+?)\\]");

            if (!match.Success)
                throw new InvalidDataException("Input string does not match nyaa.si cdata description format.");

            Id = int.Parse(match.Groups[2].Value.Trim());
            Name = match.Groups[3].Value.Trim();
            string sizeStr = match.Groups[4].Value.Trim();
            SizeMiB = Global.ParseFileSize(sizeStr);
            Category = match.Groups[5].Value.Trim();
            Hash = match.Groups[6].Value.Trim();

            ViewUrl = $"https://nyaa.si/view/{Id}";
            TorrentUrl = $"https://nyaa.si/download/{Id}.torrent";
            ProbableLang = Lang.Undefined;
            ProbableRes = DetermineResolution();
            _IsTooOld = _IsTooNew = false;
            SetAgeFromPubDate(match.Groups[1].Value.Trim());
            SetEpisodeNumber();
            DetermineLanguage();
        }

        private string DetermineResolution()
        {
            var resMatch = Regex.Match(Name, @"(\d{3,4})[pi]");
            return resMatch.Success ? resMatch.Value.Trim() : SizeMiB > 500 ? "1080p" : "720p";
        }

        private void SetAgeFromPubDate(string pubdate)
        {
            var dateMatch = Regex.Match(pubdate, ", (\\d{1,2}) (\\w{3,4}) (\\d{4})");
            if (dateMatch.Success)
            {
                var day = int.Parse(dateMatch.Groups[1].Value);
                var month = MonthToNumber(dateMatch.Groups[2].Value.ToLowerInvariant());
                var year = int.Parse(dateMatch.Groups[3].Value);
                var episodeDate = new DateTime(year, month, day);
                var ageSpan = DateTime.Now - episodeDate;
                _IsTooOld = ageSpan > TimeSpan.FromDays(Settings.TooOldDays);
                _IsTooNew = ageSpan < TimeSpan.FromMinutes(Settings.TooNewMinutes);
            }
        }

        private int MonthToNumber(string month)
        => month switch
        {
            "jan" => 1,
            "feb" => 2,
            "mar" => 3,
            "apr" => 4,
            "may" => 5,
            "jun" => 6,
            "jul" => 7,
            "aug" => 8,
            "sep" => 9,
            "oct" => 10,
            "nov" => 11,
            "dec" => 12,
            _ => 1 
        };

        private void SetEpisodeNumber()
        {
            var epMatch = Regex.Match(Name, @"(?:s\d{1,2} *ep|ep| - |s\d{1,2}e)(\d{1,2})|(\d{1,2}) of \d{1,2}", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (epMatch.Success)
            {
                _ProbableEpNumber = int.Parse(epMatch.Groups[1].Success ? epMatch.Groups[1].Value : epMatch.Groups[2].Value);
            }
        }

        private void DetermineLanguage()
        {
            _IsAnime = Category.Contains("Anime");
            if (_IsAnime && !Category.Contains("Anime - Raw"))
            {
                var nameLower = Name.ToLowerInvariant();
                var isCustom = Regex.IsMatch(nameLower, Settings.CustomLanguageNameRegex);
                var isEnglish = Regex.IsMatch(nameLower, @"[^\w]eng\w*|english");

                if (isCustom && isEnglish) ProbableLang = Lang.CustomAndEng;
                else if (isCustom) ProbableLang = Lang.Custom;
                else if (isEnglish) ProbableLang = Lang.Eng;
            }
            else if (_IsAnime) ProbableLang = Lang.RAW;
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

                bool isCustom = Regex.Match(description, Settings.CustomLanguageDescriptionRegex, RegexOptions.IgnoreCase).Success;
                bool isEnglish = Regex.Match(description, "[^\\w]eng[^\\w]|english", RegexOptions.IgnoreCase).Success;

                if (isCustom && isEnglish)
                {
                    resultLang = Lang.CustomAndEng;
                }
                else if (isEnglish)
                {
                    resultLang = Lang.Eng;
                }
                else if (isCustom)
                {
                    resultLang = Lang.Custom;
                }
            }

            return resultLang;
        }


    }

}


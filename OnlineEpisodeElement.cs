using System.Text.RegularExpressions;

namespace AniDownloaderTerminal
{
    public partial class OnlineEpisodeElement
    {
        public string ViewUrl { get; }
        public string TorrentUrl { get; }
        public string Hash { get; }
        public int Id { get; }
        public string Name { get; }
        public string Category { get; }
        public decimal SizeMiB { get; }
        public string ProbableRes { get; }
        public int Seeders { get; }

        private int? _ProbableEpNumber;
        public int? ProbableEpNumber { get { return _ProbableEpNumber; } }
        public Lang ProbableLang { get; set; }
        private bool _IsAnime;
        public bool IsAnime { get { return _IsAnime; } }
        private bool _IsTooOld;
        public bool IsTooOld { get { return _IsTooOld; } }
        private bool _IsTooNew;
        public bool IsTooNew { get { return _IsTooNew; } }
        public bool TooFewSeeders { get { return _TooFewSeeders; } }
        private readonly bool _TooFewSeeders;

        [GeneratedRegex("<pubDate>(.+)<\\/pubDate>[\\s\\S]+?CDATA.+?#(\\d{1,8}) \\| (.+?)<\\/a\\> \\| (.+?) \\| (.+?) \\| (.+?)\\]")]
        private static partial Regex NyaaCDATARegex();
        [GeneratedRegex("\\<nyaa\\:seeders\\>(\\d+)\\<\\/nyaa\\:seeders\\>")]
        private static partial Regex NyaaSeedersRegex();
        [GeneratedRegex(@"(\d{3,4})[pi]")]
        private static partial Regex ResolutionRegex();
        [GeneratedRegex(", (\\d{1,2}) (\\w{3,4}) (\\d{4})")]
        private static partial Regex DateRegex();
        [GeneratedRegex(@"(?:s\d{1,2} *ep|ep| - |s\d{1,2}e|^\w+ )(\d{1,2})|(\d{1,2}) of \d{1,2}", RegexOptions.IgnoreCase | RegexOptions.Singleline, "en-001")]
        private static partial Regex EpisodeNumberRegex();
        [GeneratedRegex(@"[^\w]eng\w*|english")]
        private static partial Regex EngLangRegex();
        [GeneratedRegex("<pubDate>(.+)<\\/pubDate>[\\s\\S]+?CDATA.+?#(\\d{1,8}) \\| (.+?)<\\/a\\> \\| (.+?) \\| (.+?) \\| (.+?)\\]")]
        private static partial Regex OnlineEpisodesRegex();
        [GeneratedRegex("id=\"torrent-description\">(.*?)<\\/div>", RegexOptions.Singleline)]
        private static partial Regex NyaaDescriptionRegex();
        [GeneratedRegex("[^\\w]eng[^\\w]|english", RegexOptions.IgnoreCase, "en-001")]
        private static partial Regex NyaaEnglishLangDescriptionRegex();


        public OnlineEpisodeElement(string webCode)
        {
            Match match = NyaaCDATARegex().Match(webCode);

            if (!match.Success)
                throw new InvalidDataException("Input string does not match nyaa.si cdata description format.");

            Id = int.Parse(match.Groups[2].Value.Trim());
            Name = match.Groups[3].Value.Trim();
            string sizeStr = match.Groups[4].Value.Trim();
            SizeMiB = Global.ParseFileSize(sizeStr);
            Seeders = ParseSeeders(webCode);
            Category = match.Groups[5].Value.Trim();
            Hash = match.Groups[6].Value.Trim();

            ViewUrl = $"https://nyaa.si/view/{Id}";
            TorrentUrl = $"https://nyaa.si/download/{Id}.torrent";
            ProbableLang = Lang.Undefined;
            ProbableRes = DetermineResolution(Name);
            _IsTooOld = _TooFewSeeders = false;
            _TooFewSeeders = Settings.TooFewSeeders >= Seeders;
            SetAgeFromPubDate(match.Groups[1].Value.Trim());
            SetEpisodeNumber(Name);
            DetermineLanguage();
        }

        private static int ParseSeeders(string webcode) {
            Match match = NyaaSeedersRegex().Match(webcode);
            if (match.Success) { 
                string seedersString = match.Groups[1].Value.Trim();
                return int.Parse(seedersString);
            }
            return 0;
        }

        private string DetermineResolution(String content)
        {
            Match resMatch = ResolutionRegex().Match(content);
            return resMatch.Success ? resMatch.Value.Trim() : SizeMiB > 500 ? "1080p" : "720p";
        }


        private void SetAgeFromPubDate(string pubdate)
        {
            var dateMatch = DateRegex().Match(pubdate);
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

        private static int MonthToNumber(string month)
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

        private void SetEpisodeNumber(String content)
        {

            _ProbableEpNumber = GetEpNumberFromString(content);
           
        }

        public static int? GetEpNumberFromString(String str)
        {
            var epMatch = EpisodeNumberRegex().Match(str);
            if (epMatch.Success) return int.Parse(epMatch.Groups[1].Success ? epMatch.Groups[1].Value : epMatch.Groups[2].Value);
            return null;
        }

        private void DetermineLanguage()
        {
            _IsAnime = Category.Contains("Anime");
            if (_IsAnime && !Category.Contains("Anime - Raw"))
            {
                var nameLower = Name.ToLowerInvariant();
                var isCustom = Regex.IsMatch(nameLower, Settings.CustomLanguageNameRegex);
                var isEnglish = EngLangRegex().IsMatch(nameLower);

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
            MatchCollection matches = OnlineEpisodesRegex().Matches(content);
            return [.. matches.Select(i => i.Value)];
        }

        public Lang GetProbableLanguage()
        {
            string pageText = Global.GetWebStringFromUrlNonAsync(ViewUrl);
            Match descriptionMatch = NyaaDescriptionRegex().Match(pageText);
            Lang resultLang = Lang.Undefined;

            if (descriptionMatch.Success)
            {
                string description = Name + " " + descriptionMatch.Groups[1].Value;

                bool isCustom = Regex.Match(description, Settings.CustomLanguageDescriptionRegex, RegexOptions.IgnoreCase).Success;
                bool isEnglish = NyaaEnglishLangDescriptionRegex().Match(description).Success;

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


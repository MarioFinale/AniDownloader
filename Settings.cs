namespace AniDownloaderTerminal
{
    public class Settings
    {
        public static ulong MaxFileSizeMb { get => _MaxFileSizeMb; set => _MaxFileSizeMb = value; }
        static ulong _MaxFileSizeMb = 4000;

        public static ulong RPSDelayMs { get => _RPSDelayMs; set => _RPSDelayMs = value; }
        static ulong _RPSDelayMs = 1500;
        public static int TooFewSeeders { get => _TooFewSeeders; set => _TooFewSeeders = value; }
        static int _TooFewSeeders = 1;

        public static ulong TooOldDays { get => _TooOldDays; set => _TooOldDays = value; }
        static ulong _TooOldDays = 60;

        public static ulong TooNewMinutes { get => _TooNewMinutes; set => _TooNewMinutes = value; }
        static ulong _TooNewMinutes = 120;

        public static ulong SeedingTimeHours { get => _SeedingTimeHours; set => _SeedingTimeHours = value;}
        static ulong _SeedingTimeHours = 12;

        public static int WebserverPort { get => _WebserverPort; set => _WebserverPort = value; }
        static int _WebserverPort = 8080;

        public static double SeedingRatio { get => _SeedingRatio; set => _SeedingRatio = value; }
        static double _SeedingRatio = 1;

        public static bool UseRatio { get => _UseRatio; set => _UseRatio = value; }
        static bool _UseRatio = true;

        public static bool ExcludeBatchReleases { get => _ExcludeBatchReleases; set => _ExcludeBatchReleases = value; }
        static bool _ExcludeBatchReleases = true;

        public static bool EnableWebServer { get => _EnableWebServer; set => _EnableWebServer = value; }
        static bool _EnableWebServer = true;

        public static bool UseTranscodingHWAccel { get => _UseTranscodingHWAccel; set => _UseTranscodingHWAccel = value; }
        static bool _UseTranscodingHWAccel = true;

        public static bool UseCustomLanguage { get => _UseCustomLanguage; set => _UseCustomLanguage = value; }
        static bool _UseCustomLanguage = true;

        public static string ListeningIP { get => _ListeningIP; set => _ListeningIP = value; }
        static string _ListeningIP = "127.0.0.1";

        public static string DefaultPath { get => _DefaultPath; set => _DefaultPath = value; }      
        static string _DefaultPath = "/";

        public static string NeedsConvertFileName { get => _NeedsConvertFileName; set => _NeedsConvertFileName = value; }
        static string _NeedsConvertFileName = ".needsConvert";

        public static string[] SearchPaths { get => _SearchPaths; set => _SearchPaths = value; }
        static string[] _SearchPaths = ["/"];

        public static string UncensoredEpisodeRegex { get => _UncensoredEpisodeRegex; set => _UncensoredEpisodeRegex = value; }
        static string _UncensoredEpisodeRegex = "[Uu]ncensored|[Ss]in *[Cc]ensura";

        public static string CustomLanguageNameRegex { get => _CustomLanguageNameRegex; set => _CustomLanguageNameRegex = value; }
        static string _CustomLanguageNameRegex = @"[^\w]esp[^\w]|spa[^\w]| es[^\w]|[Ee]spañol|[Ss]panish";

        public static string CustomLanguageDescriptionRegex { get => _CustomLanguageDescriptionRegex; set => _CustomLanguageDescriptionRegex = value; }
        static string _CustomLanguageDescriptionRegex = @"[^\w]esp[^\w]|spa[^\w]| es[^\w]|[Ee]spañol|[Ss]panish";

        public static string OutputTranscodeCommandLineArguments { get => _OutputTranscodeCommandLineArguments; set => _OutputTranscodeCommandLineArguments = value; }
        static string _OutputTranscodeCommandLineArguments = "-map 0 -map -0:d? -disposition:s:0 default -scodec copy -c:a aac -ac 2 -b:a 320k -vcodec libx264 -crf 25 -preset slow -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -movflags faststart -tune fastdecode -pix_fmt yuv420p -vf \"crop=trunc(iw/2)*2:trunc(ih/2)*2\"";
        public static string UserName { get => _UserName; set => _UserName = value; }
        static string _UserName = "admin";
        public static string Password { get => _Password; set => _Password = value; }
        static string _Password = "changeme";




        private bool InvalidSettings = false;

        private readonly Dictionary<string, DateTime> LastWriteTimes = [];

        public void Init() {
            LoadAndValidateSettingsFile();

            bool SettingsWatcher()
            {
                if (HasFileBeenModified(Global.SettingsPath))
                {
                    LoadAndValidateSettingsFile();
                    Global.CurrentOpsQueue.Enqueue("Loaded new settings.");
                }
                return true;
            };
            Global.TaskAdmin.NewTask("SettingsWatcher", "Settings", SettingsWatcher, 5000, true);
        }

        public void LoadAndValidateSettingsFile()
        {
            try
            {
                if (!File.Exists(Global.SettingsPath)) File.Create(Global.SettingsPath).Close();
                Dictionary<string, string> settingValues = File.ReadLines(Global.SettingsPath)
                           .Where(x => !string.IsNullOrWhiteSpace(x))
                           .Select(line => line.Split('=', 2, StringSplitOptions.None).Select(part => part.Trim()).ToArray())
                           .Where(parts => parts.Length == 2)
                           .ToDictionary(parts => parts[0], parts => parts[1]);

                TryUpdateSetting(settingValues, "MaxFileSizeMb", ref _MaxFileSizeMb);
                TryUpdateSetting(settingValues, "RPSDelayMs", ref _RPSDelayMs);
                TryUpdateSetting(settingValues, "TooFewSeeders", ref _TooFewSeeders);
                TryUpdateSetting(settingValues, "TooOldDays", ref _TooOldDays);
                TryUpdateSetting(settingValues, "TooNewMinutes", ref _TooNewMinutes);
                TryUpdateSetting(settingValues, "SeedingTimeHours", ref _SeedingTimeHours);
                TryUpdateSetting(settingValues, "WebserverPort", ref _WebserverPort);
                TryUpdateSetting(settingValues, "SeedingRatio", ref _SeedingRatio);
                TryUpdateSetting(settingValues, "UseRatio", ref _UseRatio);
                TryUpdateSetting(settingValues, "ExcludeBatchReleases", ref _ExcludeBatchReleases);
                TryUpdateSetting(settingValues, "EnableWebServer", ref _EnableWebServer);
                TryUpdateSetting(settingValues, "ListeningIP", ref _ListeningIP);
                TryUpdateSetting(settingValues, "DefaultPath", ref _DefaultPath);
                TryUpdateSetting(settingValues, "SearchPaths", ref _SearchPaths);
                TryUpdateSetting(settingValues, "_NeedsConvertFileName", ref _NeedsConvertFileName);
                TryUpdateSetting(settingValues, "UncensoredEpisodeRegex", ref _UncensoredEpisodeRegex);
                TryUpdateSetting(settingValues, "CustomLanguageNameRegex", ref _CustomLanguageNameRegex);
                TryUpdateSetting(settingValues, "CustomLanguageDescriptionRegex", ref _CustomLanguageDescriptionRegex);
                TryUpdateSetting(settingValues, "UseCustomLanguage", ref _UseCustomLanguage);
                TryUpdateSetting(settingValues, "OutputTranscodeCommandLineArguments", ref _OutputTranscodeCommandLineArguments);
                TryUpdateSetting(settingValues, "UseTranscodingHWAccel", ref _UseTranscodingHWAccel);
                TryUpdateSetting(settingValues, "UserName", ref _UserName);
                TryUpdateSetting(settingValues, "Password", ref _Password);

                if (InvalidSettings)
                {
                    InvalidSettings = false;
                    List<string> newSettings = [];
                    foreach(KeyValuePair<string,string> setting in settingValues)
                    {
                        newSettings.Add($"{setting.Key} = {setting.Value}");
                    }
                    File.WriteAllLines(Global.SettingsPath, newSettings);
                }

            }
            catch (Exception ex)
            {
                Global.TaskAdmin.Logger.EX_Log($"Failed to load settings file: {ex.Message}", "LoadSettings");
            }            
        }


        private void TryUpdateSetting<T>(Dictionary<string, string> settingValues, string key, ref T variable)
        {
            if (settingValues.TryGetValue(key, out string? value))
            {
                try
                {
                    if (typeof(T) == typeof(string[]))
                    {
                        // Parse as semicolon-separated (safer for paths with commas); replace embedded ';' to prevent mis-splits.
                        string[] arrayValue = [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                              .Select(p =>
                                              {
                                                  if (p.Contains(';'))
                                                  {
                                                      Global.TaskAdmin.Logger.Log($"Warning: Path for '{key}' '{p}' contains semicolon - replacing with '_'.", "LoadSettings");
                                                      return p.Replace(";", "_");
                                                  }
                                                  return p;
                                              })
                                              .Where(p => !string.IsNullOrWhiteSpace(p))];

                        // Log if empty after filtering
                        if (arrayValue.Length == 0)
                        {
                            Global.TaskAdmin.Logger.Log($"Warning: All paths for '{key}' were empty or invalid after filtering.", "LoadSettings");
                        }

                        foreach (var path in arrayValue)
                        {
                            if (!Directory.Exists(path))
                            {
                                Global.TaskAdmin.Logger.Log($"Warning: Search path for '{key}' '{path}' does not exist. Entry will be added but consider creating the directory or removing the entry.", "LoadSettings");
                            }
                        }
                        variable = (T)(object)arrayValue;
                    }
                    else
                    {
                        variable = (T)Convert.ChangeType(value, typeof(T));
                    }
                }
                catch (Exception ex)
                {
                    Global.TaskAdmin.Logger.EX_Log($"Failed to parse {key}: {ex.Message}. Using default.", "LoadSettings");
                    if (typeof(T) == typeof(string[]))
                    {
                        if (variable != null)
                        {
                            settingValues[key] = string.Join(";", (string[])(object)variable);
                        }
                        else
                        {
                            Global.TaskAdmin.Logger.EX_Log($"Failed to use default in {key}. Setting will be set to empty.", "LoadSettings");
                            Global.TaskAdmin.Logger.EX_Log($"This should not be null. If this is a fork, nag the Authors.", "LoadSettings");
                            settingValues[key] = string.Empty; // For safety, but should not happen because defaults are not null.
                        }
                    }
                    else
                    {
                        settingValues[key] = (variable?.ToString()) ?? string.Empty;
                    }
                    InvalidSettings = true;
                }
            }
            else
            {
                if (typeof(T) == typeof(string[]))
                {
                    if (variable != null)
                    {
                        settingValues.Add(key, string.Join(";", (string[])(object)variable));
                    }
                    else
                    {
                        Global.TaskAdmin.Logger.EX_Log($"Failed to set value in {key}. Setting will be set to empty.", "LoadSettings");
                        settingValues.Add(key, string.Empty); // For safety, but again, should not happen because defaults are not null.
                    }
                }
                else
                {
                    settingValues.Add(key, (variable?.ToString()) ?? string.Empty);
                }
                InvalidSettings = true;
            }
        }

        public bool HasFileBeenModified(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Global.TaskAdmin.Logger.EX_Log($"Failed to load settings file: File does not Exist!", "LoadSettings");
                return false;
            }

            DateTime currentWriteTime = File.GetLastWriteTime(filePath);
            if (LastWriteTimes.TryGetValue(filePath, out DateTime lastCheckWriteTime))
            {
                bool hasBeenModified = currentWriteTime > lastCheckWriteTime;

                if (hasBeenModified)
                {
                    LastWriteTimes[filePath] = currentWriteTime;
                    return true;
                }
                return false;
            }
            else
            {
                LastWriteTimes.Add(filePath, currentWriteTime);
                return false;
            }
        }


    }
}

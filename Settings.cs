using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

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

        public static string UncensoredEpisodeRegex { get => _UncensoredEpisodeRegex; set => _UncensoredEpisodeRegex = value; }
        static string _UncensoredEpisodeRegex = "[Uu]ncensored|[Ss]in *[Cc]ensura";

        public static string CustomLanguageNameRegex { get => _CustomLanguageNameRegex; set => _CustomLanguageNameRegex = value; }
        static string _CustomLanguageNameRegex = @"[^\w]esp\w*|spanish|español";

        public static string CustomLanguageDescriptionRegex { get => _CustomLanguageDescriptionRegex; set => _CustomLanguageDescriptionRegex = value; }
        static string _CustomLanguageDescriptionRegex = @"[^\w]esp[^\w]|spa[^\w]| es[^\w]|español|spanish";

        public static string OutputTranscodeCommandLineArguments { get => _OutputTranscodeCommandLineArguments; set => _OutputTranscodeCommandLineArguments = value; }
        static string _OutputTranscodeCommandLineArguments = "-map 0 -map -0:d -disposition:s:0 default -scodec copy -c:a aac -ac 2 -b:a 320k -vcodec libx264 -crf 25 -preset slow -movflags faststart -tune film -pix_fmt yuv420p -x264opts opencl -vf \"crop=trunc(iw/2)*2:trunc(ih/2)*2\"";
        
        private bool InvalidSettings = false;

        private readonly Dictionary<string, DateTime> LastWriteTimes = new();

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
                TryUpdateSetting(settingValues, "UncensoredEpisodeRegex", ref _UncensoredEpisodeRegex);
                TryUpdateSetting(settingValues, "CustomLanguageNameRegex", ref _CustomLanguageNameRegex);
                TryUpdateSetting(settingValues, "CustomLanguageDescriptionRegex", ref _CustomLanguageDescriptionRegex);
                TryUpdateSetting(settingValues, "UseCustomLanguage", ref _UseCustomLanguage);
                TryUpdateSetting(settingValues, "OutputTranscodeCommandLineArguments", ref _OutputTranscodeCommandLineArguments);
                TryUpdateSetting(settingValues, "UseTranscodingHWAccel", ref _UseTranscodingHWAccel);

                if (InvalidSettings)
                {
                    InvalidSettings = false;
                    List<string> newSettings = new();
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
            if (settingValues.ContainsKey(key))
            {
                string value = settingValues[key];
                try
                {
                    variable = (T)Convert.ChangeType(value, typeof(T));
                }
                catch
                {
                    settingValues[key] = (variable?.ToString()) ?? string.Empty;
                    InvalidSettings = true;
                }
            }
            else
            {
                settingValues.Add(key, (variable?.ToString()) ?? string.Empty);
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
            if (LastWriteTimes.ContainsKey(filePath))
            {
                DateTime lastCheckWriteTime = LastWriteTimes[filePath];
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

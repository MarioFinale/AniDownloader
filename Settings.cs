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
        public static ulong MaxFileSizeMb = 4000;
        public static ulong RPSDelayMs = 1500;
        public static ulong TooOldDays = 60;
        public static ulong TooNewMinutes = 120;
        public static ulong SeedingTimeHours = 12;
        public static int WebserverPort = 8080;
        public static double SeedingRatio = 1;
        public static bool UseRatio = true;
        public static bool ExcludeBatchReleases = true;
        public static bool EnableWebServer = true;
        public static string ListeningIP = "127.0.0.1";
        public static string DefaultPath = "/";        
        public static string UncensoredEpisodeRegex = "[Uu]ncensored|[Ss]in *[Cc]ensura";
        public static string CustomLanguageNameRegex = @"[^\w]esp\w*|spanish|español";
        public static string CustomLanguageDescriptionRegex = @"[^\\w]esp[^\\w]|spa[^\\w]| es[^\\w]|español|spanish";
        private bool InvalidSettings = false;
        private Dictionary<string, DateTime> _lastWriteTimes = new Dictionary<string, DateTime>();

        public void Init() {


            LoadAndValidateSettingsFile();

            Func<bool> SettingsWatcher = () =>
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
                           .Select(line => line.Split('=', 2).Select(part => part.Trim()).ToArray())
                           .Where(parts => parts.Length == 2)
                           .ToDictionary(parts => parts[0], parts => parts[1]);

                TryUpdateSetting(settingValues, "MaxFileSizeMb", ref MaxFileSizeMb);
                TryUpdateSetting(settingValues, "RPSDelayMs", ref RPSDelayMs);
                TryUpdateSetting(settingValues, "TooOldDays", ref TooOldDays);
                TryUpdateSetting(settingValues, "TooNewMinutes", ref TooNewMinutes);
                TryUpdateSetting(settingValues, "SeedingTimeHours", ref SeedingTimeHours);
                TryUpdateSetting(settingValues, "WebserverPort", ref WebserverPort);
                TryUpdateSetting(settingValues, "SeedingRatio", ref SeedingRatio);
                TryUpdateSetting(settingValues, "UseRatio", ref UseRatio);
                TryUpdateSetting(settingValues, "ExcludeBatchReleases", ref ExcludeBatchReleases);
                TryUpdateSetting(settingValues, "EnableWebServer", ref EnableWebServer);
                TryUpdateSetting(settingValues, "ListeningIP", ref ListeningIP);
                TryUpdateSetting(settingValues, "DefaultPath", ref DefaultPath);
                TryUpdateSetting(settingValues, "UncensoredEpisodeRegex", ref UncensoredEpisodeRegex);
                TryUpdateSetting(settingValues, "CustomLanguageNameRegex", ref CustomLanguageNameRegex);
                TryUpdateSetting(settingValues, "CustomLanguageDescriptionRegex", ref CustomLanguageDescriptionRegex);

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
            if (_lastWriteTimes.ContainsKey(filePath))
            {
                DateTime lastCheckWriteTime = _lastWriteTimes[filePath];
                bool hasBeenModified = currentWriteTime > lastCheckWriteTime;

                if (hasBeenModified)
                {
                    _lastWriteTimes[filePath] = currentWriteTime;
                    return true;
                }
                return false;
            }
            else
            {
                _lastWriteTimes.Add(filePath, currentWriteTime);
                return false;
            }
        }


    }
}

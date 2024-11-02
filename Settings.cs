using System;
using System.Collections.Generic;
using System.Linq;
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
        public static bool ExcludeBatchReleases = true;
        public static string DefaultPath = "";
        public static string SettingsPath = Path.Combine(Global.Exepath, "AniDownloader.cfg");
        public static string UncensoredEpisodeRegex = "[Uu]ncensored|[Ss]in *[Cc]ensura";


        public void Init() {


            Func<bool> SettingsWatcher = () =>
            {
               
                return true;
            };

            Global.TaskAdmin.NewTask("StartDownloads", "Downloader", SettingsWatcher, 1000, true);



        }

    }
}

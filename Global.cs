using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AniDownloaderTerminal
{
    public static class Global
    {
        public static TaskAdmin.Utility.TaskAdmin TaskAdmin = new();
        public static readonly Queue<string> currentOpsQueue = new();
        public static HttpClient httpClient = new();
    }
}

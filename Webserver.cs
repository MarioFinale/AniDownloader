using System.Data;
using System.Net;
using System.Text;

namespace AniDownloaderTerminal
{
    public class Webserver
    {
        private string url = $"http://{Settings.ListeningIP}:{Settings.WebserverPort}/";
        private string pageData = string.Empty;

        private Dictionary<string, DateTime> lastReAuthTimes = new Dictionary<string, DateTime>();
        private Dictionary<string, bool> forcingReAuth = new Dictionary<string, bool>();

        public async Task HandleIncomingConnections(HttpListener listener)
        {
            bool runServer = true;

            Global.TaskAdmin.Logger.EX_Log($"Web server started.", "HandleIncomingConnections");
            while (runServer)
            {
                if (listener is null)
                {
                    runServer = false;
                    return;
                }
                string disableSubmit = !runServer ? "disabled" : "";

                HttpListenerContext ctx = await listener.GetContextAsync();
                HttpListenerRequest req = ctx.Request;
                HttpListenerResponse resp = ctx.Response;

                if (req is null) return;


                /* WIP: BASIC SECURITY 
                // IP Restriction: Get remote IP
                IPAddress remoteIp = ctx.Request.RemoteEndPoint.Address;
                if (remoteIp.IsIPv4MappedToIPv6) remoteIp = remoteIp.MapToIPv4();  // Handle IPv6-mapped IPv4

                // Allow loopback
                if (!remoteIp.Equals(IPAddress.Loopback) || !remoteIp.Equals(IPAddress.IPv6Loopback))
                {
                    // Get local IPv4 subnet prefix (e.g., "192.168.1.")
                    var localAddresses = Dns.GetHostEntry(Dns.GetHostName()).AddressList
                        .Where(addr => addr.AddressFamily == AddressFamily.InterNetwork)  // IPv4 only
                        .ToArray();

                    if (localAddresses.Length == 0)
                    {
                        // No local IPv4 found; log and deny
                        resp.StatusCode = 403;  // Forbidden
                        resp.Close();
                        continue;
                    }

                    // Assume first local IPv4 and /24 subnet
                    string localIp = localAddresses[0].ToString();
                    string subnetPrefix = string.Join(".", localIp.Split('.').Take(3)) + ".";

                    string remoteIpStr = remoteIp.ToString();
                    if (!remoteIpStr.StartsWith(subnetPrefix))
                    {
                        Global.TaskAdmin.Logger.EX_Log($"Access denied from IP: {remoteIpStr}", "HandleIncomingConnections");
                        resp.StatusCode = 403;  // Forbidden
                        resp.Close();
                        continue;
                    }
                }

               
                */

                // Validate Basic Auth credentials
                if (ctx.User == null || ctx.User.Identity == null || !ctx.User.Identity.IsAuthenticated)
                {
                    resp.StatusCode = 401; // Unauthorized
                    resp.Headers.Add("WWW-Authenticate", "Basic realm=\"Secure Area\"");
                    resp.Close();
                    continue;
                }
                HttpListenerBasicIdentity identity = (HttpListenerBasicIdentity)ctx.User.Identity;
                string username = identity.Name; // From settings
                string password = identity.Password; // From settings
                if (username != Settings.UserName || password != Settings.Password)
                {
                    resp.StatusCode = 401; // Unauthorized
                    resp.Headers.Add("WWW-Authenticate", "Basic realm=\"Secure Area\"");
                    resp.Close();
                    continue;
                }

                IPAddress remoteIp = ctx.Request.RemoteEndPoint.Address;
                if (remoteIp.IsIPv4MappedToIPv6) remoteIp = remoteIp.MapToIPv4();

                // NEW: Force re-auth logic (credentials are valid at this point)
                string ipKey = remoteIp.ToString();
                if (!lastReAuthTimes.TryGetValue(ipKey, out DateTime lastTime) || DateTime.Now - lastTime > TimeSpan.FromHours(24))
                {
                    if (!forcingReAuth.TryGetValue(ipKey, out bool isForcing) || !isForcing)
                    {
                        forcingReAuth[ipKey] = true;
                        resp.StatusCode = 401;
                        resp.Headers.Add("WWW-Authenticate", "Basic realm=\"Secure Area\"");
                        resp.Close();
                        continue;
                    }
                    else
                    {
                        forcingReAuth.Remove(ipKey); // Clean up
                        lastReAuthTimes[ipKey] = DateTime.Now;
                        // Proceed to handle the request
                    }
                }
                else
                {
                    // Proceed to handle the request
                }

                string responseData = string.Empty;

                if ((req.HttpMethod == "POST") && (req.Url != null) && (req.Url.AbsolutePath == "/update"))
                {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                    string text = reader.ReadToEnd();
                    string[] webParams = [.. text.Split("&").Select(x => System.Web.HttpUtility.UrlDecode(x))];

                    Dictionary<int, WebTable> paramsDic = [];
                    try
                    {
                        foreach (string postParam in webParams)
                        {
                            string[] subparams = postParam.Split("=", 2, StringSplitOptions.None);
                            string paramVal = subparams[1];
                            int paramID = int.Parse(subparams[0].Split("-")[1]);
                            string paramName = subparams[0].Split("-")[0].Trim();

                            if (!paramsDic.TryGetValue(paramID, out WebTable? value))
                            {                             
                                if (value == null) paramsDic.Add(paramID, new WebTable());
                            }

                            switch (paramName)
                            {
                                case "Name":
                                    paramsDic[paramID].Name = paramVal;
                                    break;
                                case "Path":
                                    paramsDic[paramID].Path = paramVal;
                                    break;
                                case "Offset":
                                    paramsDic[paramID].Offset = paramVal;
                                    break;
                                case "Filter":
                                    paramsDic[paramID].Filter = paramVal;
                                    break;
                                default:
                                    break;
                            }
                        }


                        lock (Global.SeriesTable)
                        {
                            Global.SeriesTable.Rows.Clear();
                            foreach (KeyValuePair<int, WebTable> pair in paramsDic)
                            {
                                Global.SeriesTable.Rows.Add(pair.Value.Name, pair.Value.Path, pair.Value.Offset, pair.Value.Filter);
                            }
                            Global.SeriesTable.WriteXml(Global.SeriesTableFilePath, XmlWriteMode.WriteSchema);
                        }

                    }
                    catch (Exception ex)
                    {
                        Global.TaskAdmin.Logger.EX_Log($"Error handling POST data.", "HandleIncomingConnections");
                        Global.TaskAdmin.Logger.EX_Log($"Ex: {ex.Message}", "HandleIncomingConnections");
                    }


                }

                if ((req.HttpMethod == "POST") && (req.Url != null) && (req.Url.AbsolutePath == "/settings"))
                {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                    string text = reader.ReadToEnd();
                    string[] webParams = [.. text.Split("&").Select(x => System.Web.HttpUtility.UrlDecode(x))];
                    File.WriteAllLines(Global.SettingsPath, webParams);
                    Program.settings.LoadAndValidateSettingsFile();
                }



                pageData = File.ReadAllText(Path.Join(Global.Exepath, "SettingsPage.htm")).Replace("==========TABLE HERE===========", ConvertSeriesDataTableToHTML(Global.SeriesTable));
                pageData = pageData.Replace("MaxFileSizeMb-replace", Settings.MaxFileSizeMb.ToString());
                pageData = pageData.Replace("RPSDelayMs-replace", Settings.RPSDelayMs.ToString());
                pageData = pageData.Replace("TooFewSeeders-replace", Settings.TooFewSeeders.ToString());
                pageData = pageData.Replace("TooOldDays-replace", Settings.TooOldDays.ToString());
                pageData = pageData.Replace("TooNewMinutes-replace", Settings.TooNewMinutes.ToString());
                pageData = pageData.Replace("SeedingTimeHours-replace", Settings.SeedingTimeHours.ToString());
                pageData = pageData.Replace("WebserverPort-replace", Settings.WebserverPort.ToString().Replace("\"", "\\\""));
                pageData = pageData.Replace("SeedingRatio-replace", Settings.SeedingRatio.ToString().Replace("\"", "\\\""));
                if (Settings.UseRatio)
                {
                    pageData = pageData.Replace("<!--UseRatioTrueSelected-->", "selected");
                    pageData = pageData.Replace("<!--UseRatioFalseSelected-->", "");
                }
                else
                {
                    pageData = pageData.Replace("<!--UseRatioTrueSelected-->", "");
                    pageData = pageData.Replace("<!--UseRatioFalseSelected-->", "selected");
                }
                if (Settings.ExcludeBatchReleases)
                {
                    pageData = pageData.Replace("<!--ExcludeBatchReleasesTrueSelected-->", "selected");
                    pageData = pageData.Replace("<!--ExcludeBatchReleasesFalseSelected-->", "");
                }
                else
                {
                    pageData = pageData.Replace("<!--ExcludeBatchReleasesTrueSelected-->", "");
                    pageData = pageData.Replace("<!--ExcludeBatchReleasesFalseSelected-->", "selected");
                }
                if (Settings.EnableWebServer)
                {
                    pageData = pageData.Replace("<!--EnableWebServerTrueSelected-->", "selected");
                    pageData = pageData.Replace("<!--EnableWebServerFalseSelected-->", "");
                }
                else
                {
                    pageData = pageData.Replace("<!--EnableWebServerTrueSelected-->", "");
                    pageData = pageData.Replace("<!--EnableWebServerFalseSelected-->", "selected");
                }
                if (Settings.UseTranscodingHWAccel)
                {
                    pageData = pageData.Replace("<!--UseTranscodingHWAccelTrueSelected-->", "selected");
                    pageData = pageData.Replace("<!--UseTranscodingHWAccelFalseSelected-->", "");
                }
                else
                {
                    pageData = pageData.Replace("<!--UseTranscodingHWAccelTrueSelected-->", "");
                    pageData = pageData.Replace("<!--UseTranscodingHWAccelFalseSelected-->", "selected");
                }
                if (Settings.UseCustomLanguage)
                {
                    pageData = pageData.Replace("<!--UseCustomLanguageTrueSelected-->", "selected");
                    pageData = pageData.Replace("<!--UseCustomLanguageFalseSelected-->", "");
                }
                else
                {
                    pageData = pageData.Replace("<!--UseUseCustomLanguageTrueSelected-->", "");
                    pageData = pageData.Replace("<!--UseCustomLanguageFalseSelected-->", "selected");
                }

                pageData = pageData.Replace("ListeningIP-replace", Settings.ListeningIP.ToString());
                pageData = pageData.Replace("DefaultPath-replace", Settings.DefaultPath.ToString().Replace("\"", "&quot;"));
                pageData = pageData.Replace("NeedsConvertFileName-replace", Settings.NeedsConvertFileName.ToString().Replace("\"", "&quot;"));
                pageData = pageData.Replace("SearchPaths-replace", String.Join(";", Settings.SearchPaths).Replace("\"", "&quot;"));
                pageData = pageData.Replace("UncensoredEpisodeRegex-replace", Settings.UncensoredEpisodeRegex.ToString().Replace("\"", "&quot;"));
                pageData = pageData.Replace("CustomLanguageNameRegex-replace", Settings.CustomLanguageNameRegex.ToString().Replace("\"", "&quot;"));
                pageData = pageData.Replace("CustomLanguageDescriptionRegex-replace", Settings.CustomLanguageDescriptionRegex.ToString().Replace("\"", "&quot;"));
                pageData = pageData.Replace("OutputTranscodeCommandLineArguments-replace", Settings.OutputTranscodeCommandLineArguments.ToString().Replace("\"", "&quot;"));

                responseData = pageData;
                resp.ContentType = "text/html";

                if (!Settings.EnableWebServer)
                {
                    runServer = false;
                }

                if ($"http://{Settings.ListeningIP}:{Settings.WebserverPort}/" != url)
                {
                    runServer = false;
                }


                if (req.Url != null && req.Url.AbsolutePath == "/style.css")
                {
                    responseData = File.ReadAllText(Path.Join(Global.Exepath, "style.css"));
                    resp.ContentType = "text/css";
                }

                if (req.Url != null && req.Url.AbsolutePath == "/status")
                {
                    responseData = ConvertStatusDataTableToHTML(Global.CurrentStatusTable);
                    resp.ContentType = "text/html";
                }

                if (req.Url != null && req.Url.AbsolutePath == "/currentoperation")
                {
                    try
                    {
                        Global.CurrentOpsQueue.TryPeek(out var peek);
                        if (peek != null)
                        {
                            responseData = peek;
                        }                        
                    }
                    catch (Exception ex)
                    {
                        Global.TaskAdmin.Logger.EX_Log($"currentoperation Error: {ex.Message}", "HandleIncomingConnections");
                    }

                    resp.ContentType = "text/html";
                }

                byte[] data = Encoding.UTF8.GetBytes(responseData);
                resp.ContentEncoding = Encoding.UTF8;
                resp.ContentLength64 = data.LongLength;
                await resp.OutputStream.WriteAsync(data);
                resp.Close();

            }
            Global.TaskAdmin.Logger.EX_Log($"Web server closed.", "HandleIncomingConnections");
        }

        private class WebTable
        {
            public string Name = string.Empty;
            public string Path = string.Empty;
            public string Offset = string.Empty;
            public string Filter = string.Empty;
        }

        public void Init()
        {

            var timer = new System.Timers.Timer(TimeSpan.FromHours(24).TotalMilliseconds);
            timer.Elapsed += (sender, e) => { lastReAuthTimes.Clear(); forcingReAuth.Clear(); };
            timer.AutoReset = true;
            timer.Start();

            bool WebServer()
            {
                if (!Settings.EnableWebServer) return true;
                url = $"http://{Settings.ListeningIP}:{Settings.WebserverPort}/";
                using HttpListener listener = new();
                try
                {
                    listener.Prefixes.Add(url);
                    listener.AuthenticationSchemes = AuthenticationSchemes.Basic;
                    listener.Start();
                    // Handle requests
                    Task listenTask = HandleIncomingConnections(listener);
                    listenTask.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Global.TaskAdmin.Logger.EX_Log($"Web server cannot start: {ex.Message}", "GetAvailableSeriesEpisodes");
                    listener.Close();
                    return false;
                }
                listener.Close();
                return true;
            }
            Global.TaskAdmin.NewTask("WebServer", "WebServer", WebServer, 1000, true, true);
        }

        private static string ConvertSeriesDataTableToHTML(DataTable table)
        {
            DataTable dt;
            lock (table)
            {
                dt = table.Copy(); //Copy the dataTable to avoid collection modified exceptions.
            }

            string html = $"<table id=\"{dt.TableName}\" class=\"table\">";
            html += "<thead>";
            html += "<tr>";
            for (int i = 0; i < dt.Columns.Count; i++)
            {
                html += "<th scope=\"col\">" + dt.Columns[i].ColumnName + "</th>";
            }
            html += "</tr>";
            html += "</thead>";

            html += "<tbody id=\"Series_Body\">";
            for (int i = 0; i < dt.Rows.Count; i++)
            {
                html += $"<tr id=\"sr_{i}\">";
                for (int j = 0; j < dt.Columns.Count; j++)
                {
                    html += $"<td><input style=\"width:105%; padding:inherit; box-sizing:border-box;\" type=\"text\" name=\"{dt.Columns[j].ColumnName}-{i}\" value=\"{dt.Rows[i][j]}\"/></td>";
                }

                html += $"<td><button id=\"del-{i}\" type=\"button\" onclick=\"deleteRow({i})\">x</button></td>";
                html += "</tr>";
            }
            html += "</tbody>";
            html += "</table>";
            return html;
        }

        private static string ConvertStatusDataTableToHTML(DataTable table)
        {
            DataTable dt;
            lock (table)
            {
                dt = table.Copy(); //Copy the dataTable to avoid collection modified exceptions.
            }
            string html = "";
            for (int i = 0; i < dt.Rows.Count; i++)
            {
                html += $"<tr id=\"sr_{i}\">";
                for (int j = 0; j < dt.Columns.Count; j++)
                {
                    html += $"<td style=\"width:inherit; padding:inherit; box-sizing:border-box; align:center;\">{dt.Rows[i][j]}</td>";
                }

                html += "</tr>";
            }
            return html;
        }
    }

}


using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;

namespace AniDownloaderTerminal
{
    public class Webserver
    {
        public static HttpListener listener;
        public static string url = $"http://{Settings.ListeningIP}:{Settings.WebserverPort}/";
        public static int pageViews = 0;
        public static int requestCount = 0;
        public static string pageData = string.Empty;



        public async Task HandleIncomingConnections()
        {
            bool runServer = true;

            Global.TaskAdmin.Logger.EX_Log($"Web server started.", "HandleIncomingConnections");
            while (runServer)
            {
                string disableSubmit = !runServer ? "disabled" : "";

                // Will wait here until we hear from a connection
                HttpListenerContext ctx = await listener.GetContextAsync();
                // Peel out the requests and response objects
                HttpListenerRequest req = ctx.Request;
                HttpListenerResponse resp = ctx.Response;

                string responseData = string.Empty;

                // If `shutdown` url requested w/ POST, then shutdown the server after serving the page
                if ((req.HttpMethod == "POST") && (req.Url.AbsolutePath == "/update"))
                {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                    string text = reader.ReadToEnd();
                    string[] webParams = text.Split("&").Select(x => System.Web.HttpUtility.UrlDecode(x)).ToArray();
                  
                    Dictionary<int,WebTable> paramsDic = new();
                    try
                    {
                        foreach (string postParam in webParams)
                        {
                            string[] subparams = postParam.Split("=", 2, StringSplitOptions.None);
                            string paramVal = subparams[1];
                            int paramID = int.Parse(subparams[0].Split("-")[1]);
                            string paramName = subparams[0].Split("-")[0].Trim();

                            if (!paramsDic.ContainsKey(paramID))
                            {
                                WebTable tab = new WebTable();
                                paramsDic.Add(paramID, tab);
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

                        Global.SeriesTable.Rows.Clear();

                        foreach (KeyValuePair<int, WebTable> pair in paramsDic)
                        {
                            Global.SeriesTable.Rows.Add(pair.Value.Name, pair.Value.Path, pair.Value.Offset, pair.Value.Filter);
                        }
                        Global.SeriesTable.WriteXml(Global.SeriesTableFilePath, XmlWriteMode.WriteSchema);

                    }
                    catch (Exception)
                    {
                        Global.TaskAdmin.Logger.EX_Log($"Error handling POST data.", "HandleIncomingConnections");
                    }
                   

                }

                if ((req.HttpMethod == "POST") && (req.Url.AbsolutePath == "/settings"))
                {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                    string text = reader.ReadToEnd();
                    string[] webParams = text.Split("&").Select(x => System.Web.HttpUtility.UrlDecode(x)).ToArray();
                    File.WriteAllLines(Global.SettingsPath, webParams);
                    Program.settings.LoadAndValidateSettingsFile();
                }


                pageData = File.ReadAllText(Path.Join(Global.Exepath, "SettingsPage.htm")).Replace("==========TABLE HERE===========", ConvertDataTableToHTML(Global.SeriesTable));
                pageData = pageData.Replace("MaxFileSizeMb-replace", Settings.MaxFileSizeMb.ToString());
                pageData = pageData.Replace("RPSDelayMs-replace", Settings.RPSDelayMs.ToString());
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

                pageData = pageData.Replace("ListeningIP-replace", Settings.ListeningIP.ToString());
                pageData = pageData.Replace("DefaultPath-replace", Settings.DefaultPath.ToString().Replace("\"", "&quot;"));
                pageData = pageData.Replace("UncensoredEpisodeRegex-replace", Settings.UncensoredEpisodeRegex.ToString().Replace("\"", "&quot;"));
                pageData = pageData.Replace("CustomLanguageNameRegex-replace", Settings.CustomLanguageNameRegex.ToString().Replace("\"", "&quot;"));
                pageData = pageData.Replace("CustomLanguageDescriptionRegex-replace", Settings.CustomLanguageDescriptionRegex.ToString().Replace("\"", "&quot;"));
                pageData = pageData.Replace("OutputTranscodeCommandLineArguments-replace", Settings.OutputTranscodeCommandLineArguments.ToString().Replace("\"", "&quot;"));


                if (!Settings.EnableWebServer)
                {
                    runServer = false;
                }

                if ($"http://{Settings.ListeningIP}:{Settings.WebserverPort}/" != url)
                {
                    runServer = false;
                }

                // Make sure we don't increment the page views counter if `favicon.ico` is requested
                if (req.Url.AbsolutePath != "/favicon.ico") pageViews += 1;

                if (req.Url.AbsolutePath == "/style.css")
                {
                    responseData = File.ReadAllText(Path.Join(Global.Exepath, "style.css"));
                    resp.ContentType = "text/css";
                }
                else
                {
                    responseData = pageData;
                    resp.ContentType = "text/html";
                }

                // Write the response info
                byte[] data = Encoding.UTF8.GetBytes(responseData);
                
                resp.ContentEncoding = Encoding.UTF8;
                resp.ContentLength64 = data.LongLength;

                // Write out to the response stream (asynchronously), then close it
                await resp.OutputStream.WriteAsync(data, 0, data.Length);
                resp.Close();

            }
            Global.TaskAdmin.Logger.EX_Log($"Web server closed.", "HandleIncomingConnections");
        }

        private class WebTable
        {
            public string Name;
            public string Path;
            public string Offset;
            public string Filter;
        }

        public void Init()
        {
            Func<bool> WebServer = () =>
            {
                if (!Settings.EnableWebServer) return true;
                // Create a Http server and start listening for incoming connections
                url = $"http://{Settings.ListeningIP}:{Settings.WebserverPort}/";
                listener = new HttpListener();
                try
                {
                    listener.Prefixes.Add(url);
                    listener.Start();
                }
                catch (Exception ex)
                {
                    Global.TaskAdmin.Logger.EX_Log($"Web server cannot start: {ex.Message}", "GetAvailableSeriesEpisodes");
                    return false;
                }              

                // Handle requests
                Task listenTask = HandleIncomingConnections();
                listenTask.GetAwaiter().GetResult();

                // Close the listener
                listener.Close();
                return true;
            };
            Global.TaskAdmin.NewTask("WebServer", "WebServer", WebServer, 1000, true, true);          
        }

        private string ConvertDataTableToHTML(DataTable dt)
        {
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
                    html += $"<td><input style=\"width:105%; padding:inherit; box-sizing:border-box;\" type=\"text\" name=\"{dt.Columns[j].ColumnName}-{i}\" value=\"{dt.Rows[i][j].ToString()}\"/></td>";                 
                }

                html += $"<td><button id=\"del-{i}\" type=\"button\" onclick=\"deleteRow({i})\">x</button></td>";
                html += "</tr>";
            }
            html += "</tbody>";
            html += "</table>";
            return html;
        }


    }

}


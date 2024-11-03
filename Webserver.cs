using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AniDownloaderTerminal
{
    internal class Webserver
    {
        public static HttpListener listener;
        public static string url = $"http://{Settings.ListeningIP}:{Settings.WebserverPort}/";
        public static int pageViews = 0;
        public static int requestCount = 0;
        public static string pageData =
            "<!DOCTYPE>" +
            "<html>" +
            "  <head>" +
            "    <title>HttpListener Example</title>" +
            "  </head>" +
            "  <body>" +
            "    <p>Page Views: {0}</p>" +
            "    <form method=\"post\" action=\"shutdown\">" +
            "      <input type=\"submit\" value=\"Shutdown\" {1}>" +
            "    </form>" +
            "  </body>" +
            "</html>";


        public static async Task HandleIncomingConnections()
        {
            bool runServer = true;

            Global.TaskAdmin.Logger.EX_Log($"Web server started.", "HandleIncomingConnections");
            while (runServer)
            {
                // Will wait here until we hear from a connection
                HttpListenerContext ctx = await listener.GetContextAsync();

                // Peel out the requests and response objects
                HttpListenerRequest req = ctx.Request;
                HttpListenerResponse resp = ctx.Response;

                // If `shutdown` url requested w/ POST, then shutdown the server after serving the page
                if ((req.HttpMethod == "POST") && (req.Url.AbsolutePath == "/shutdown"))
                {
                    runServer = false;
                }

                if (!Settings.EnableWebServer)
                {
                    runServer = false;
                }

                if ($"http://{Settings.ListeningIP}:{Settings.WebserverPort}/" != url)
                {
                    runServer = false;
                }

                // Make sure we don't increment the page views counter if `favicon.ico` is requested
                if (req.Url.AbsolutePath != "/favicon.ico")
                    pageViews += 1;

                // Write the response info
                string disableSubmit = !runServer ? "disabled" : "";
                byte[] data = Encoding.UTF8.GetBytes(String.Format(pageData, pageViews, disableSubmit));
                resp.ContentType = "text/html";
                resp.ContentEncoding = Encoding.UTF8;
                resp.ContentLength64 = data.LongLength;

                // Write out to the response stream (asynchronously), then close it
                await resp.OutputStream.WriteAsync(data, 0, data.Length);
                resp.Close();
            }
            Global.TaskAdmin.Logger.EX_Log($"Web server closed.", "HandleIncomingConnections");
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
    }

}


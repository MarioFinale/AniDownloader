using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace AniDownloaderTerminal
{
    public class AnilistMetadataProvider
    {
        private readonly string APIEndpointURI;
        private readonly string DbPath;

        public AnilistMetadataProvider()
        {
            APIEndpointURI = Settings.AnilistEndPointUri;
            DbPath = Global.MetadataDBPath;
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var conn = GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS SeriesMetadata (
                    Name TEXT PRIMARY KEY,
                    Id INTEGER,
                    Episodes INTEGER,
                    Finished INTEGER,
                    Timestamp DATETIME
                )";
            cmd.ExecuteNonQuery();
        }

        private SqliteConnection GetDbConnection()
        {
            return new SqliteConnection($"Data Source={DbPath};");
        }

        public SeriesStatus? QuerySeriesByName(string seriesName)
        {
            Global.CurrentOpsQueue.Enqueue($"Queryng db for '{seriesName}'.");
            if (string.IsNullOrWhiteSpace(seriesName)) return null;
            SeriesStatus? status = QuerySeriesByNameFromCache(seriesName);
            if (status == null)
            {
                status = QuerySeriesByNameFromApi(seriesName);
                if (status != null)
                {
                    CacheSeriesStatus(seriesName, status);
                }
            }
            return status;
        }

        private SeriesStatus? QuerySeriesByNameFromCache(string seriesName)
        {
            using var conn = GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Episodes, Finished, Timestamp FROM SeriesMetadata WHERE Name = @Name";
            cmd.Parameters.AddWithValue("@Name", seriesName);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                DateTime timestamp = reader.GetDateTime(3);
                if (DateTime.UtcNow - timestamp > TimeSpan.FromDays(7))
                {
                    // Cache too old; delete and return null to refetch
                    DeleteCacheEntry(seriesName);
                    return null;
                }
                return new SeriesStatus
                {
                    Id = reader.GetInt64(0),
                    Episodes = reader.IsDBNull(1) ? 0 : reader.GetInt32(1), // Handle null episodes (ongoing series)
                    Finished = reader.GetInt32(2) == 1
                };
            }
            return null;
        }

        private void DeleteCacheEntry(string seriesName)
        {
            using var conn = GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM SeriesMetadata WHERE Name = @Name";
            cmd.Parameters.AddWithValue("@Name", seriesName);
            cmd.ExecuteNonQuery();
        }

        private SeriesStatus? QuerySeriesByNameFromApi(string seriesName)
        {
            Global.CurrentOpsQueue.Enqueue($"Queryng Anilist API for '{seriesName}'.");
            string query = "{ \"query\": \"query ($search: String) { Media(search: $search, type: ANIME) { id episodes status } }\", \"variables\": { \"search\": \"" + seriesName + "\" } }";
            HttpContent content = new StringContent(query, Encoding.UTF8, "application/json");
            string response = Global.PostWebStringToUrlNonAsync(APIEndpointURI, content);
            if (string.IsNullOrWhiteSpace(response)) return null;

            using JsonDocument document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data) ||
                !data.TryGetProperty("Media", out JsonElement media))
            {
                Global.TaskAdmin.Logger.EX_Log("Error parsing Anilist API response.", "QuerySeriesByNameFromApi");
                Global.TaskAdmin.Logger.Debug_Log("Json response:" + response, "QuerySeriesByNameFromApi");
                return null;
            }

            media.TryGetProperty("id", out JsonElement _id);
            media.TryGetProperty("episodes", out JsonElement _episodes);
            media.TryGetProperty("status", out JsonElement _status);

            string? statusStr = _status.GetString()?.ToLowerInvariant().Trim();
            bool finished = statusStr?.Contains("finished") ?? false;

            return new SeriesStatus
            {
                Id = _id.GetInt64(),
                Episodes = _episodes.ValueKind == JsonValueKind.Null ? 0 : _episodes.GetInt32(), // 0 for ongoing/null
                Finished = finished
            };
        }

        private void CacheSeriesStatus(string seriesName, SeriesStatus status)
        {
            using var conn = GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO SeriesMetadata (Name, Id, Episodes, Finished, Timestamp)
                VALUES (@Name, @Id, @Episodes, @Finished, @Timestamp)";
            cmd.Parameters.AddWithValue("@Name", seriesName);
            cmd.Parameters.AddWithValue("@Id", status.Id);
            cmd.Parameters.AddWithValue("@Episodes", status.Episodes == 0 ? DBNull.Value : status.Episodes); // Allow null for ongoing
            cmd.Parameters.AddWithValue("@Finished", status.Finished ? 1 : 0);
            cmd.Parameters.AddWithValue("@Timestamp", DateTime.UtcNow); // Use UTC
            cmd.ExecuteNonQuery();
        }

        public class SeriesStatus
        {
            public long Id { get; set; }
            public int Episodes { get; set; }
            public bool Finished { get; set; }
        }
    }
}
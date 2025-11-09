using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Dapper;

namespace GarminTempApi.Services
{
    public class GarminDbReader
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public GarminDbReader(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
            _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        }

        public Task<bool> DbExistsAsync()
        {
            return Task.FromResult(System.IO.File.Exists(_dbPath));
        }

        public async Task<IEnumerable<ActivitySummary>> GetActivitiesAsync(int limit = 100)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var sql = @"
                SELECT id AS ActivityId,
                       name AS ActivityName,
                       start_time_utc AS StartTime,
                       distance_meters AS DistanceMeters,
                       duration_seconds AS DurationSeconds,
                       sport AS Sport
                FROM activities
                ORDER BY start_time_utc DESC
                LIMIT @Limit;
            ";

            var res = await conn.QueryAsync<ActivitySummary>(sql, new { Limit = limit });
            return res;
        }

        public class ActivitySummary
        {
            public long ActivityId { get; set; }
            public string ActivityName { get; set; }
            public DateTime StartTime { get; set; }
            public double DistanceMeters { get; set; }
            public double DurationSeconds { get; set; }
            public string Sport { get; set; }
        }
    }
}

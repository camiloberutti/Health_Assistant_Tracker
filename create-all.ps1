<#
create-all.ps1
Creates a new ASP.NET Core project scaffold + Docker + GarminDB files inside:
C:\Users\camil\Desktop\App_Garmin
#>

$root = "C:\Users\camil\Desktop\App_Garmin"
if (-not (Test-Path $root)) { New-Item -ItemType Directory -Path $root | Out-Null }

Write-Host "Creating project scaffold in $root"

Push-Location $root

# 1) Create ASP.NET Core Web API project and solution
Write-Host "Creating ASP.NET Core WebAPI project..."
dotnet new webapi -n GarminTempApi | Out-Null

# create solution and add project
dotnet new sln -n App_Garmin | Out-Null
dotnet sln App_Garmin.sln add .\GarminTempApi\GarminTempApi.csproj | Out-Null

# create folders for our code
$folders = @(
    ".\GarminTempApi\Controllers",
    ".\GarminTempApi\Data",
    ".\GarminTempApi\Models",
    ".\GarminTempApi\Services",
    ".\GarminTempApi\Background",
    ".\garmindb",
    ".\web"
)
foreach ($f in $folders) { if (-not (Test-Path $f)) { New-Item -ItemType Directory -Path $f | Out-Null } }

# 2) Create Program.cs (replace)
$program = @'
using System;
using System.IO;
using GarminTempApi.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Garmin DB path from env var or config
var dbPath = Environment.GetEnvironmentVariable("GARMIN_DB_PATH")
             ?? builder.Configuration["GarminDb:Path"]
             ?? "/data/garmin.sqlite";

// Register GarminDbReader (singleton)
builder.Services.AddSingleton(new GarminTempApi.Services.GarminDbReader(dbPath));
// Wait for DB presence on startup
builder.Services.AddHostedService<GarminTempApi.Services.DbWaiterHostedService>();

// Register EF and other services for file upload flow (SQLite)
builder.Services.AddDbContext<GarminTempApi.Data.AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=garmin_temp.db"));
builder.Services.AddScoped<GarminTempApi.Services.ActivityService>();
builder.Services.AddScoped<GarminTempApi.Services.IActivityParser, GarminTempApi.Services.GpxTcxParser>();
builder.Services.AddScoped<GarminTempApi.Services.FitActivityParser>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();
'@
Set-Content -Path ".\GarminTempApi\Program.cs" -Value $program -Encoding UTF8

# 3) appsettings.json
$appsettings = @'
{
  "ConnectionStrings": {
    "Default": "Data Source=garmin_temp.db"
  },
  "GarminDb": {
    "Path": "C:\\Users\\camil\\.GarminDb\\databases\\garmin.sqlite"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  }
}
'@
Set-Content -Path ".\GarminTempApi\appsettings.json" -Value $appsettings -Encoding UTF8

# 4) Models/Activity.cs
$activity = @'
using System;
using System.Collections.Generic;

namespace GarminTempApi.Models
{
    public class Activity
    {
        public int Id { get; set; }
        public string Source { get; set; }         // "upload" or "garmin-api"
        public string FileName { get; set; }
        public DateTime StartTime { get; set; }
        public double DistanceMeters { get; set; }
        public TimeSpan Duration { get; set; }
        public string ActivityType { get; set; }   // Run, Ride, etc.
        public List<ActivityPoint> Points { get; set; } = new();
    }

    public class ActivityPoint
    {
        public int Id { get; set; }
        public int ActivityId { get; set; }
        public Activity Activity { get; set; }
        public DateTime Timestamp { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double? HeartRate { get; set; }
        public double? Altitude { get; set; }
    }
}
'@
Set-Content -Path ".\GarminTempApi\Models\Activity.cs" -Value $activity -Encoding UTF8

# 5) Data/AppDbContext.cs
$appdb = @'
using Microsoft.EntityFrameworkCore;
using GarminTempApi.Models;

namespace GarminTempApi.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> opts) : base(opts) { }

        public DbSet<Activity> Activities { get; set; }
        public DbSet<ActivityPoint> ActivityPoints { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Activity>()
                .HasMany(a => a.Points)
                .WithOne(p => p.Activity)
                .HasForeignKey(p => p.ActivityId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
'@
Set-Content -Path ".\GarminTempApi\Data\AppDbContext.cs" -Value $appdb -Encoding UTF8

# 6) Services/IActivityParser.cs
$iact = @'
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using GarminTempApi.Models;

namespace GarminTempApi.Services
{
    public interface IActivityParser
    {
        Task<List<Activity>> ParseAsync(Stream fileStream, string fileName);
    }
}
'@
Set-Content -Path ".\GarminTempApi\Services\IActivityParser.cs" -Value $iact -Encoding UTF8

# 7) Services/GpxTcxParser.cs
$gpx = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using GarminTempApi.Models;

namespace GarminTempApi.Services
{
    public class GpxTcxParser : IActivityParser
    {
        public async Task<List<Activity>> ParseAsync(Stream fileStream, string fileName)
        {
            fileStream.Position = 0;
            using var sr = new StreamReader(fileStream);
            var xml = await sr.ReadToEndAsync();
            var doc = XDocument.Parse(xml);

            var rootName = doc.Root?.Name.LocalName?.ToLowerInvariant() ?? string.Empty;
            if (rootName.Contains("gpx"))
                return ParseGpx(doc, fileName);
            if (rootName.Contains("tcx") || doc.Descendants().Any(x => x.Name.LocalName == "TrainingCenterDatabase"))
                return ParseTcx(doc, fileName);

            throw new InvalidOperationException("Unsupported XML file (not GPX or TCX)");
        }

        private List<Activity> ParseGpx(XDocument doc, string fileName)
        {
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var trkpts = doc.Descendants(ns + "trkpt").ToList();

            if (!trkpts.Any()) return new List<Activity>();

            var activity = new Activity
            {
                FileName = fileName,
                Source = "upload",
                ActivityType = "Unknown"
            };

            foreach (var pt in trkpts)
            {
                var lat = double.Parse(pt.Attribute("lat").Value, System.Globalization.CultureInfo.InvariantCulture);
                var lon = double.Parse(pt.Attribute("lon").Value, System.Globalization.CultureInfo.InvariantCulture);
                var timeNode = pt.Element(ns + "time");
                DateTime ts = timeNode != null ? DateTime.Parse(timeNode.Value, null, System.Globalization.DateTimeStyles.AssumeUniversal) : DateTime.UtcNow;

                var eleNode = pt.Element(ns + "ele");
                double? ele = eleNode != null ? double.Parse(eleNode.Value, System.Globalization.CultureInfo.InvariantCulture) : (double?)null;

                var hrNode = pt.Descendants().FirstOrDefault(e => e.Name.LocalName == "hr" || e.Name.LocalName == "heart_rate");
                double? hr = null;
                if (hrNode != null && double.TryParse(hrNode.Value, out var hrv)) hr = hrv;

                activity.Points.Add(new ActivityPoint
                {
                    Timestamp = ts,
                    Latitude = lat,
                    Longitude = lon,
                    Altitude = ele,
                    HeartRate = hr
                });
            }

            activity.StartTime = activity.Points.First().Timestamp;
            activity.Duration = activity.Points.Last().Timestamp - activity.Points.First().Timestamp;
            return new List<Activity> { activity };
        }

        private List<Activity> ParseTcx(XDocument doc, string fileName)
        {
            var trackpoints = doc.Descendants().Where(x => x.Name.LocalName == "Trackpoint").ToList();
            if (!trackpoints.Any()) return new List<Activity>();

            var activity = new Activity
            {
                FileName = fileName,
                Source = "upload",
                ActivityType = "Unknown"
            };

            foreach (var tp in trackpoints)
            {
                var timeNode = tp.Elements().FirstOrDefault(e => e.Name.LocalName == "Time");
                DateTime ts = timeNode != null ? DateTime.Parse(timeNode.Value, null, System.Globalization.DateTimeStyles.AssumeUniversal) : DateTime.UtcNow;

                var pos = tp.Elements().FirstOrDefault(e => e.Name.LocalName == "Position");
                double lat = 0, lon = 0;
                if (pos != null)
                {
                    var latNode = pos.Elements().FirstOrDefault(e => e.Name.LocalName == "LatitudeDegrees");
                    var lonNode = pos.Elements().FirstOrDefault(e => e.Name.LocalName == "LongitudeDegrees");
                    if (latNode != null) double.TryParse(latNode.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lat);
                    if (lonNode != null) double.TryParse(lonNode.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lon);
                }

                var eleNode = tp.Elements().FirstOrDefault(e => e.Name.LocalName == "AltitudeMeters");
                double? ele = null;
                if (eleNode != null && double.TryParse(eleNode.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ev)) ele = ev;

                var hrNode = tp.Descendants().FirstOrDefault(e => e.Name.LocalName == "Value" && e.Parent?.Name.LocalName == "HeartRateBpm");
                double? hr = null;
                if (hrNode != null && double.TryParse(hrNode.Value, out var hv)) hr = hv;

                activity.Points.Add(new ActivityPoint
                {
                    Timestamp = ts,
                    Latitude = lat,
                    Longitude = lon,
                    Altitude = ele,
                    HeartRate = hr
                });
            }

            activity.StartTime = activity.Points.First().Timestamp;
            activity.Duration = activity.Points.Last().Timestamp - activity.Points.First().Timestamp;
            return new List<Activity> { activity };
        }
    }
}
'@
Set-Content -Path ".\GarminTempApi\Services\GpxTcxParser.cs" -Value $gpx -Encoding UTF8

# 8) Services/FitActivityParser.cs (scaffold)
$fit = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GarminTempApi.Models;

namespace GarminTempApi.Services
{
    public class FitActivityParser
    {
        public FitActivityParser()
        {
        }

        public async Task<List<Activity>> ParseAsync(Stream fileStream, string fileName)
        {
            // TODO: Implement using Garmin FIT SDK Decode API.
            throw new NotImplementedException("FIT parser scaffold - implement using Garmin FIT SDK.");
        }
    }
}
'@
Set-Content -Path ".\GarminTempApi\Services\FitActivityParser.cs" -Value $fit -Encoding UTF8

# 9) Services/ActivityService.cs
$svc = @'
using System.Threading.Tasks;
using GarminTempApi.Data;
using GarminTempApi.Models;

namespace GarminTempApi.Services
{
    public class ActivityService
    {
        private readonly AppDbContext _db;
        public ActivityService(AppDbContext db) => _db = db;

        public async Task SaveActivityAsync(Activity activity)
        {
            _db.Activities.Add(activity);
            await _db.SaveChangesAsync();
        }
    }
}
'@
Set-Content -Path ".\GarminTempApi\Services\ActivityService.cs" -Value $svc -Encoding UTF8

# 10) Controllers/FilesController.cs
$filesCtrl = @'
using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Threading.Tasks;
using GarminTempApi.Services;
using System.Linq;

namespace GarminTempApi.Controllers
{
    [ApiController]
    [Route("api/files")]
    public class FilesController : ControllerBase
    {
        private readonly IActivityParser _xmlParser;
        private readonly FitActivityParser _fitParser;
        private readonly ActivityService _svc;

        public FilesController(IActivityParser xmlParser, FitActivityParser fitParser, ActivityService svc)
        {
            _xmlParser = xmlParser;
            _fitParser = fitParser;
            _svc = svc;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> Upload([FromForm] Microsoft.AspNetCore.Http.IFormFile file)
        {
            if (file == null) return BadRequest("No file uploaded.");

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            ms.Position = 0;

            var activities = ext switch
            {
                ".gpx" or ".tcx" or ".xml" => await _xmlParser.ParseAsync(ms, file.FileName),
                ".fit" => await _fitParser.ParseAsync(ms, file.FileName),
                _ => new System.Collections.Generic.List<Models.Activity>()
            };

            if (activities == null || !activities.Any())
                return BadRequest("No activities parsed from file.");

            foreach (var a in activities)
                await _svc.SaveActivityAsync(a);

            return Ok(new { imported = activities.Count, file = file.FileName });
        }
    }
}
'@
Set-Content -Path ".\GarminTempApi\Controllers\FilesController.cs" -Value $filesCtrl -Encoding UTF8

# 11) Controllers/ActivitiesController.cs
$actCtrl = @'
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using GarminTempApi.Data;
using Microsoft.EntityFrameworkCore;

namespace GarminTempApi.Controllers
{
    [ApiController]
    [Route("api/activities")]
    public class ActivitiesController : ControllerBase
    {
        private readonly AppDbContext _db;
        public ActivitiesController(AppDbContext db) => _db = db;

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var activities = await _db.Activities
                .Include(a => a.Points)
                .ToListAsync();

            return Ok(activities);
        }
    }
}
'@
Set-Content -Path ".\GarminTempApi\Controllers\ActivitiesController.cs" -Value $actCtrl -Encoding UTF8

# 12) Controllers/AuthController.cs
$auth = @'
using Microsoft.AspNetCore.Mvc;

namespace GarminTempApi.Controllers
{
    [ApiController]
    [Route("auth")]
    public class AuthController : ControllerBase
    {
        [HttpGet("garmin/login")]
        public IActionResult Login()
        {
            return Ok("Placeholder: implement Garmin OAuth redirect once you have client id & secret.");
        }

        [HttpGet("garmin/callback")]
        public IActionResult Callback([FromQuery] string code)
        {
            return Ok(new { code });
        }
    }
}
'@
Set-Content -Path ".\GarminTempApi\Controllers\AuthController.cs" -Value $auth -Encoding UTF8

# 13) Services/GarminDbReader.cs (reads GarminDB sqlite)
$gdb = @'
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
'@
Set-Content -Path ".\GarminTempApi\Services\GarminDbReader.cs" -Value $gdb -Encoding UTF8

# 14) Services/DbWaiterHostedService.cs
$dbwait = @'
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Services
{
    public class DbWaiterHostedService : IHostedService
    {
        private readonly ILogger<DbWaiterHostedService> _logger;
        private readonly GarminDbReader _reader;
        private readonly int _timeoutSeconds = 120;

        public DbWaiterHostedService(ILogger<DbWaiterHostedService> logger, GarminDbReader reader)
        {
            _logger = logger;
            _reader = reader;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("DbWaiterHostedService starting. Waiting for Garmin DB...");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!await _reader.DbExistsAsync())
            {
                if (sw.Elapsed.TotalSeconds > _timeoutSeconds)
                {
                    _logger.LogWarning("Timed out waiting for Garmin DB after {Timeout}s.", _timeoutSeconds);
                    return;
                }
                _logger.LogInformation("Garmin DB not found yet. Sleeping 3s...");
                await Task.Delay(3000, cancellationToken);
            }

            _logger.LogInformation("Garmin DB found at startup.");
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
'@
Set-Content -Path ".\GarminTempApi\Services\DbWaiterHostedService.cs" -Value $dbwait -Encoding UTF8

# 15) Controllers/GarminDbController.cs
$gdbctrl = @'
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace GarminTempApi.Controllers
{
    [ApiController]
    [Route("api/garmindb")]
    public class GarminDbController : ControllerBase
    {
        private readonly GarminTempApi.Services.GarminDbReader _reader;
        public GarminDbController(GarminTempApi.Services.GarminDbReader reader) => _reader = reader;

        [HttpGet("activities")]
        public async Task<IActionResult> GetActivities([FromQuery] int limit = 50)
        {
            if (!await _reader.DbExistsAsync())
                return Problem("Garmin DB not available yet.", statusCode: 503);

            var rows = await _reader.GetActivitiesAsync(limit);
            return Ok(rows);
        }
    }
}
'@
Set-Content -Path ".\GarminTempApi\Controllers\GarminDbController.cs" -Value $gdbctrl -Encoding UTF8

# 16) Background/GarminPollingService.cs (skeleton)
$bg = @'
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Background
{
    public class GarminPollingService : BackgroundService
    {
        private readonly ILogger<GarminPollingService> _logger;

        public GarminPollingService(ILogger<GarminPollingService> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("GarminPollingService started (skeleton).");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // TODO: use GarminDbReader or API tokens to import new activities
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
                catch (TaskCanceledException) { /* shutting down */ }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in GarminPollingService loop.");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }
    }
}
'@
Set-Content -Path ".\GarminTempApi\Background\GarminPollingService.cs" -Value $bg -Encoding UTF8

# 17) Docker: docker-compose.yml
$dc = @'
services:
  garmindb:
    build:
      context: ./garmindb
      dockerfile: Dockerfile
    container_name: garmindb
    restart: always
    environment:
      - GARMIN_USERNAME=${GARMIN_USERNAME}
      - GARMIN_PASSWORD=${GARMIN_PASSWORD}
      - GARMIN_START_DATE=${GARMIN_START_DATE}
      - GARMIN_END_DATE=${GARMIN_END_DATE}
      - GARMIN_OUTPUT_DB=/data/garmin.sqlite
      - GARMIN_FLAGS=--activities --import --analyze --latest
      - GARMIN_REFRESH_MINUTES=${GARMIN_REFRESH_MINUTES}
    volumes:
      - garmin-data:/data

  web:
    build:
      context: ./web
      dockerfile: Dockerfile
    container_name: garmin-web
    restart: always
    ports:
      - "5000:80"
    depends_on:
      - garmindb
    environment:
      - GARMIN_DB_PATH=/data/garmin.sqlite
    volumes:
      - garmin-data:/data
    healthcheck:
      test: ["CMD-SHELL", "test -f /data/garmin.sqlite || exit 1"]
      interval: 30s
      timeout: 10s
      retries: 5

volumes:
  garmin-data:
'@
Set-Content -Path ".\docker-compose.yml" -Value $dc -Encoding UTF8

# 18) garmindb/Dockerfile and entrypoint (create run_garmindb.sh safely)
$garmindb_docker = @'
FROM python:3.11-slim
ENV DEBIAN_FRONTEND=noninteractive
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends git sqlite3 ca-certificates && apt-get clean && rm -rf /var/lib/apt/lists/*
# The build will copy a local GarminDB directory (if provided) or try to git clone during build.
# We'll attempt to clone during build (if network available)
RUN git clone https://github.com/tcgoetz/GarminDB.git /app/GarminDB
WORKDIR /app/GarminDB
RUN python -m venv .venv
ENV PATH="/app/GarminDB/.venv/bin:$PATH"
RUN pip install --upgrade pip
RUN pip install -r requirements.txt
COPY run_garmindb.sh /usr/local/bin/run_garmindb.sh
RUN chmod +x /usr/local/bin/run_garmindb.sh
VOLUME ["/data"]
ENV GARMIN_USERNAME=""
ENV GARMIN_PASSWORD=""
ENV GARMIN_OUTPUT_DB="/data/garmin.sqlite"
ENV GARMIN_FLAGS="--activities --import --analyze --latest"
CMD ["/usr/local/bin/run_garmindb.sh"]
'@
Set-Content -Path ".\garmindb\Dockerfile" -Value $garmindb_docker -Encoding UTF8

# create run_garmindb.sh (literal content, avoids PowerShell expansion)
$runScript = @'
#!/usr/bin/env bash
set -euo pipefail

CONFIG_DIR="${HOME}/.GarminDb"
mkdir -p "$CONFIG_DIR"

CONFIG_FILE="$CONFIG_DIR/GarminConnectConfig.json"

cat > "$CONFIG_FILE" <<EOF
{
  "Username": "${GARMIN_USERNAME:-}",
  "Password": "${GARMIN_PASSWORD:-}",
  "UseOAUTH": false,
  "BaseFolder": "/data",
  "DatabaseLocation": "${GARMIN_OUTPUT_DB:-/data/garmin.sqlite}"
}
EOF

SLEEP_MINUTES=${GARMIN_REFRESH_MINUTES:-60}

while true; do
  echo "Starting GarminDB run at $(date --iso-8601=seconds)"
  python garmindb_cli.py ${GARMIN_FLAGS} --db ${GARMIN_OUTPUT_DB} || echo "garmindb run failed"
  echo "Sleeping ${SLEEP_MINUTES} minutes..."
  sleep $((SLEEP_MINUTES * 60))
done
'@
Set-Content -Path ".\garmindb\run_garmindb.sh" -Value $runScript -Encoding UTF8
# convert line endings to LF
(Get-Content .\garmindb\run_garmindb.sh -Raw) -replace "`r`n","`n" | Set-Content .\garmindb\run_garmindb.sh -Encoding UTF8

# 19) web/Dockerfile (builds the ASP.NET app)
$webdocker = @'
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY GarminTempApi/*.csproj ./GarminTempApi/
WORKDIR /src/GarminTempApi
RUN dotnet restore
COPY GarminTempApi/. ./GarminTempApi
WORKDIR /src/GarminTempApi
RUN dotnet publish -c Release -o /app/publish
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80
ENTRYPOINT ["dotnet","GarminTempApi.dll"]
'@
Set-Content -Path ".\web\Dockerfile" -Value $webdocker -Encoding UTF8

# 20) Print next steps for the user
Write-Host "Scaffold created in $root"
Write-Host ""
Write-Host "NEXT STEPS (run these commands manually in PowerShell from $root):"
Write-Host ""
Write-Host "1) Go into project folder:"
Write-Host "   cd $root"
Write-Host ""
Write-Host "2) Restore & add NuGet packages (in GarminTempApi folder):"
Write-Host "   cd GarminTempApi"
Write-Host "   dotnet restore"
Write-Host "   dotnet add package Microsoft.EntityFrameworkCore.Sqlite"
Write-Host "   dotnet add package Microsoft.EntityFrameworkCore.Design"
Write-Host "   dotnet add package Swashbuckle.AspNetCore"
Write-Host "   dotnet add package Microsoft.Data.Sqlite"
Write-Host "   dotnet add package Dapper"
Write-Host ""
Write-Host "3) (Optional) Install Garmin FIT SDK if you plan to parse .fit files:"
Write-Host "   dotnet add package Garmin.FIT.Sdk"
Write-Host ""
Write-Host "4) Create EF migrations and DB (optional - only for uploaded file flow):"
Write-Host "   dotnet tool install --global dotnet-ef   # if not installed"
Write-Host "   dotnet ef migrations add Init"
Write-Host "   dotnet ef database update"
Write-Host ""
Write-Host "5) To run locally without Docker (quick test):"
Write-Host "   dotnet run   # from GarminTempApi folder"
Write-Host ""
Write-Host "6) To run with Docker Compose (recommended for GarminDB automated import):"
Write-Host "   # from $root (one level above GarminTempApi)"
Write-Host "   # Set env vars for Garmin credentials (do NOT commit them)"
Write-Host "   $env:GARMIN_USERNAME = 'you@example.com'"
Write-Host "   $env:GARMIN_PASSWORD = 'YourPassword'"
Write-Host "   docker compose up --build"
Write-Host ""
Write-Host "If you prefer I can also provide:"
Write-Host "- a .env template (for local dev),"
Write-Host "- instructions to run GarminDB locally (Python venv) instead of Docker,"
Write-Host "- or a ready FIT parser implementation using Garmin.FIT.Sdk."
Write-Host ""
Write-Host "Scaffold finished. Files created:"
Get-ChildItem -Recurse -Depth 2 | Where-Object { $_.PSIsContainer -eq $false } | Select-Object FullName -First 200

Pop-Location

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Configuration;
using GarminTempApi.Models;
using GarminTempApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

LoadDotEnvIfPresent();

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddOpenApi();

// Determine application data directory for the local SQLite database
var dataDirectory = Environment.GetEnvironmentVariable("APP_DATA_DIR")
                    ?? builder.Configuration["Data:Directory"]
                    ?? Path.Combine(builder.Environment.ContentRootPath, "data");

Directory.CreateDirectory(dataDirectory);
var sqlitePath = Path.Combine(dataDirectory, "garmin_app.db");

// Register Garmin Connect importer and synchronization services
builder.Services.AddSingleton<GarminConnectImporter>();
builder.Services.AddSingleton<GarminDataSyncService>();
builder.Services.AddHostedService<GarminSyncHostedService>();

// Register EF Core sqlite (local app DB)
builder.Services.AddDbContext<GarminTempApi.Data.AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? $"Data Source={sqlitePath}"));

// Register other services used by project
builder.Services.AddScoped<GarminTempApi.Services.ActivityService>();
builder.Services.AddScoped<GarminTempApi.Services.IActivityParser, GarminTempApi.Services.GpxTcxParser>();
builder.Services.AddScoped<GarminTempApi.Services.FitActivityParser>();
builder.Services.AddScoped<InsightDataBuilder>();
builder.Services.AddScoped<DataStatusService>();

builder.Services.AddOptions<OpenAiOptions>()
    .Bind(builder.Configuration.GetSection(OpenAiOptions.SectionName))
    .PostConfigure(options =>
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            options.ApiKey = builder.Configuration["OpenAI:ApiKey"]
                ?? builder.Configuration["CHATGPT_API_KEY"]
                ?? options.ApiKey;
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            options.BaseUrl = builder.Configuration["OpenAI:BaseUrl"]
                ?? builder.Configuration["OPENAI_BASE_URL"]
                ?? options.BaseUrl;
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            options.Model = builder.Configuration["OpenAI:Model"]
                ?? builder.Configuration["OPENAI_MODEL"]
                ?? options.Model;
        }

        if (builder.Configuration["OPENAI_TEMPERATURE"] is string tempLiteral &&
            double.TryParse(tempLiteral, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
        {
            options.Temperature = temperature;
        }

        if (builder.Configuration["OPENAI_MAX_TOKENS"] is string tokenLiteral &&
            int.TryParse(tokenLiteral, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxTokens))
        {
            options.MaxTokens = maxTokens;
        }
    });

builder.Services.AddHttpClient<IOpenAiInsightService, OpenAiInsightService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.Accept.Clear();
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

var seedSampleSteps = ShouldSeedSampleSteps(builder.Configuration);
var syncOnce = ShouldRunSyncOnce();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GarminTempApi.Data.AppDbContext>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ActivityDetailSnapshots (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ActivityId INTEGER NOT NULL UNIQUE,
            DetailJson TEXT NOT NULL,
            LastUpdatedUtc TEXT NOT NULL,
            FOREIGN KEY(ActivityId) REFERENCES Activities(Id) ON DELETE CASCADE
        );");
    db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS SleepDetailSnapshots (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Date TEXT NOT NULL UNIQUE,
            DetailJson TEXT NOT NULL,
            LastUpdatedUtc TEXT NOT NULL
        );");
    EnsureSleepSummaryColumns(db.Database.GetDbConnection());

    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    var logger = loggerFactory.CreateLogger("SampleDataSeeder");

    if (seedSampleSteps)
    {
        var importer = scope.ServiceProvider.GetRequiredService<GarminConnectImporter>();
        var seededSteps = await EnsureSampleStepDataAsync(db, CancellationToken.None);
        if (seededSteps > 0)
        {
            logger.LogInformation(
                importer.HasCredentials()
                    ? "Seeded {Count} sample step summaries. Real Garmin sync will replace them once it succeeds."
                    : "Seeded {Count} sample step summaries for demo mode.",
                seededSteps);
        }
    }
    else
    {
        logger.LogInformation("Sample step seeding disabled. Expecting Garmin sync to populate StepSummaries.");
    }
}

if (syncOnce)
{
    await RunSyncOnceAsync(app.Services, CancellationToken.None);
    return;
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.MapOpenApi();
}

app.UseStaticFiles();
app.UseRouting();
app.UseHttpsRedirection();
app.UseAuthorization();

app.MapGet("/Sleep/Today", context =>
{
    context.Response.Redirect("/Sleep", permanent: false);
    return Task.CompletedTask;
});

app.MapControllers();
app.MapRazorPages();

app.Run();

static bool ShouldRunSyncOnce()
{
    var value = Environment.GetEnvironmentVariable("GARMIN_SYNC_ONCE");
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    return value.Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}

static bool ShouldSeedSampleSteps(IConfiguration configuration)
{
    var value = Environment.GetEnvironmentVariable("GARMIN_SEED_SAMPLE_STEPS")
        ?? configuration["Garmin:SeedSampleSteps"]
        ?? configuration["SeedSampleSteps"];

    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    return value.Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}

static void EnsureSleepSummaryColumns(DbConnection connection)
{
    var requiredColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "SleepStartLocal", "TEXT" },
        { "SleepEndLocal", "TEXT" },
        { "SleepStartGmt", "TEXT" },
        { "SleepEndGmt", "TEXT" },
        { "RestingHeartRate", "REAL" },
        { "BodyBatteryChange", "REAL" },
        { "AverageRespirationValue", "REAL" },
        { "LowestSpO2Value", "REAL" },
        { "SleepGoalSeconds", "REAL" }
    };

    if (connection.State != System.Data.ConnectionState.Open)
    {
        connection.Open();
    }

    try
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(SleepSummaries);";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                existingColumns.Add(name);
            }
        }

        foreach (var (column, type) in requiredColumns)
        {
            if (existingColumns.Contains(column))
            {
                continue;
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE SleepSummaries ADD COLUMN {column} {type};";
            alter.ExecuteNonQuery();
        }
    }
    finally
    {
        connection.Close();
    }
}

static async Task<int> EnsureSampleStepDataAsync(GarminTempApi.Data.AppDbContext db, CancellationToken cancellationToken)
{
    if (await db.StepSummaries.AnyAsync(cancellationToken))
    {
        return 0;
    }

    var today = DateTime.Today;
    var start = today.AddDays(-119);
    var random = new Random(4321);
    var entries = new List<StepSummary>(capacity: 120);

    for (var i = 0; i < 120; i++)
    {
        var date = start.AddDays(i).Date;
        var seasonal = Math.Sin(i / 5.5d) * 1400d;
        var fatigue = Math.Cos(i / 2.8d) * 500d;
        var weekendBoost = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 900d : 0d;
        var randomVariance = (random.NextDouble() - 0.5d) * 900d;

        var baseGoal = 10000d + Math.Sin(i / 3d) * 600d;
        var rawSteps = 9500d + seasonal + fatigue + weekendBoost + randomVariance;
        rawSteps = Math.Clamp(rawSteps, 3800d, 16500d);

        var goalSteps = Math.Clamp(baseGoal + (random.NextDouble() - 0.5d) * 500d, 7000d, 14000d);
        var distanceMeters = Math.Round(rawSteps * 0.78d, 1);
        var activeCalories = Math.Round(rawSteps * 0.045d, 0);
        var totalCalories = Math.Round(1550d + activeCalories + (random.NextDouble() - 0.5d) * 120d, 0);

        entries.Add(new StepSummary
        {
            Date = date,
            TotalSteps = Math.Round(rawSteps, 0),
            GoalSteps = Math.Round(goalSteps, 0),
            TotalCalories = totalCalories,
            ActiveCalories = activeCalories,
            TotalDistanceMeters = distanceMeters
        });
    }

    await db.StepSummaries.AddRangeAsync(entries, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);
    return entries.Count;
}

static async Task RunSyncOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
{
    using var scope = services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("GarminSyncOnce");
    var importer = scope.ServiceProvider.GetRequiredService<GarminConnectImporter>();
    var syncService = scope.ServiceProvider.GetRequiredService<GarminDataSyncService>();

    if (!importer.HasCredentials())
    {
        logger.LogWarning("Garmin credentials are not configured; skipping synchronization.");
        return;
    }

    logger.LogInformation("Running one-off Garmin synchronization...");
    var result = await syncService.SynchronizeAsync(cancellationToken);
    logger.LogInformation("Sync complete. Inserted {ActivitiesInserted} of {ActivitiesFetched} activities, upserted {SleepUpserted} sleep and {StepsUpserted} steps.",
        result.ActivitiesInserted,
        result.ActivitiesFetched,
        result.SleepUpserted,
        result.StepsUpserted);
}

static void LoadDotEnvIfPresent()
{
    var candidates = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    void AddCandidate(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var fullPath = Path.GetFullPath(directory);
        if (seen.Add(fullPath))
        {
            candidates.Add(fullPath);
        }
    }

    var currentDirectory = Directory.GetCurrentDirectory();
    AddCandidate(currentDirectory);
    var parent = Directory.GetParent(currentDirectory);
    if (parent is not null)
    {
        AddCandidate(parent.FullName);
    }
    AddCandidate(AppContext.BaseDirectory);

    foreach (var root in candidates)
    {
        var envPath = Path.Combine(root, ".env");
        if (!File.Exists(envPath))
        {
            continue;
        }

        foreach (var line in File.ReadLines(envPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = trimmed[..separatorIndex].Trim();
            var value = trimmed[(separatorIndex + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            if (key.Length == 0)
            {
                continue;
            }

            Environment.SetEnvironmentVariable(key, value);
        }

        break;
    }
}

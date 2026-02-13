using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Data;
using GarminTempApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Services;

/// <summary>
/// Bridges the .NET backend to the R analytics script (scripts/deep_analytics.R).
/// Extracts filtered data from the database, writes it as JSON, invokes Rscript,
/// and deserialises the JSON result.
/// </summary>
public class RAnalyticsService
{
    private readonly AppDbContext _db;
    private readonly ILogger<RAnalyticsService> _logger;

    // Column aliases → map UI-friendly names to internal property paths
    private static readonly Dictionary<string, string> ColumnAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Steps"] = "steps",
        ["GoalSteps"] = "goal_steps",
        ["TotalCalories"] = "total_calories",
        ["ActiveCalories"] = "active_calories",
        ["Distance"] = "distance_meters",
        ["SleepScore"] = "sleep_score",
        ["TotalSleep"] = "total_sleep_secs",
        ["DeepSleep"] = "deep_sleep_secs",
        ["LightSleep"] = "light_sleep_secs",
        ["RemSleep"] = "rem_sleep_secs",
        ["AwakeSecs"] = "awake_secs",
        ["RestingHR"] = "resting_hr",
        ["BodyBatteryChange"] = "bb_change",
        ["AvgRespiration"] = "avg_respiration",
        ["LowestSpO2"] = "lowest_spo2",
        ["ActivityDuration"] = "activity_duration_secs",
        ["ActivityDistance"] = "activity_distance_m",
    };

    public static IReadOnlyDictionary<string, string> AvailableVariables => ColumnAliases;

    public RAnalyticsService(AppDbContext db, ILogger<RAnalyticsService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Variable grouping for density scan ──────────────────────────

    private static readonly Dictionary<string, string> VariableGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Steps"] = "Steps", ["GoalSteps"] = "Steps", ["TotalCalories"] = "Steps",
        ["ActiveCalories"] = "Steps", ["Distance"] = "Steps",
        ["SleepScore"] = "Sleep", ["TotalSleep"] = "Sleep", ["DeepSleep"] = "Sleep",
        ["LightSleep"] = "Sleep", ["RemSleep"] = "Sleep", ["AwakeSecs"] = "Sleep",
        ["RestingHR"] = "Sleep", ["BodyBatteryChange"] = "Sleep",
        ["AvgRespiration"] = "Sleep", ["LowestSpO2"] = "Sleep",
        ["ActivityDuration"] = "Activity", ["ActivityDistance"] = "Activity",
    };

    /// <summary>
    /// Scans the database for the given date range and returns per-variable
    /// density information (how many days have non-null data).
    /// Variables below <paramref name="minDensity"/> are marked unavailable.
    /// </summary>
    public async Task<DataDensityResult> GetDataDensityAsync(
        DateTime start, DateTime end, double minDensity, CancellationToken ct)
    {
        var startDate = start.Date;
        var endDate = end.Date;
        var endExclusive = endDate.AddDays(1);
        var totalDays = (int)(endDate - startDate).TotalDays + 1;

        if (totalDays <= 0)
            return new DataDensityResult { TotalDaysInRange = 0, MinDensityThreshold = minDensity };

        // ── Steps group counts ──────────────────────────────────
        var stepRows = await _db.StepSummaries
            .Where(s => s.Date >= startDate && s.Date < endExclusive)
            .ToListAsync(ct);

        var stepDensities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Steps"] = stepRows.Count(r => r.TotalSteps > 0),
            ["GoalSteps"] = stepRows.Count(r => r.GoalSteps > 0),
            ["TotalCalories"] = stepRows.Count(r => r.TotalCalories > 0),
            ["ActiveCalories"] = stepRows.Count(r => r.ActiveCalories > 0),
            ["Distance"] = stepRows.Count(r => r.TotalDistanceMeters > 0),
        };

        // ── Sleep group counts ──────────────────────────────────
        var sleepRows = await _db.SleepSummaries
            .Where(s => s.Date >= startDate && s.Date < endExclusive)
            .ToListAsync(ct);

        var sleepDensities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["SleepScore"] = sleepRows.Count(r => r.SleepScore.HasValue && r.SleepScore.Value > 0),
            ["TotalSleep"] = sleepRows.Count(r => r.TotalSleepSeconds > 0),
            ["DeepSleep"] = sleepRows.Count(r => r.DeepSleepSeconds > 0),
            ["LightSleep"] = sleepRows.Count(r => r.LightSleepSeconds > 0),
            ["RemSleep"] = sleepRows.Count(r => r.RemSleepSeconds > 0),
            ["AwakeSecs"] = sleepRows.Count(r => r.AwakeSeconds > 0),
            ["RestingHR"] = sleepRows.Count(r => r.RestingHeartRate.HasValue && r.RestingHeartRate.Value > 0),
            ["BodyBatteryChange"] = sleepRows.Count(r => r.BodyBatteryChange.HasValue),
            ["AvgRespiration"] = sleepRows.Count(r => r.AverageRespirationValue.HasValue && r.AverageRespirationValue.Value > 0),
            ["LowestSpO2"] = sleepRows.Count(r => r.LowestSpO2Value.HasValue && r.LowestSpO2Value.Value > 0),
        };

        // ── Activity group counts ───────────────────────────────
        var activityDays = await _db.Activities
            .Where(a => a.StartTime >= startDate && a.StartTime < endExclusive)
            .Select(a => a.StartTime.Date)
            .Distinct()
            .CountAsync(ct);

        var actDensities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ActivityDuration"] = activityDays,
            ["ActivityDistance"] = activityDays,
        };

        // ── Build result ────────────────────────────────────────
        var allCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in stepDensities) allCounts[kv.Key] = kv.Value;
        foreach (var kv in sleepDensities) allCounts[kv.Key] = kv.Value;
        foreach (var kv in actDensities) allCounts[kv.Key] = kv.Value;

        var variables = new List<VariableDensityInfo>();
        foreach (var alias in ColumnAliases)
        {
            var populated = allCounts.TryGetValue(alias.Key, out var c) ? c : 0;
            var density = totalDays > 0 ? (double)populated / totalDays : 0;
            variables.Add(new VariableDensityInfo
            {
                Key = alias.Key,
                Column = alias.Value,
                Group = VariableGroups.TryGetValue(alias.Key, out var g) ? g : "Other",
                TotalDays = totalDays,
                PopulatedDays = populated,
                Density = Math.Round(density, 4),
                Available = density >= minDensity,
            });
        }

        return new DataDensityResult
        {
            TotalDaysInRange = totalDays,
            MinDensityThreshold = minDensity,
            Variables = variables,
        };
    }

    /// <summary>
    /// Run the requested deep analytics method.
    /// </summary>
    public async Task<DeepAnalyticsResult> RunAsync(DeepAnalyticsRequest request, CancellationToken ct)
    {
        // 1. Build combined daily dataframe from DB
        var rows = await BuildDailyDataFrameAsync(request.StartDate, request.EndDate, request.Variables, ct);
        if (rows.Count < 5)
        {
            return new DeepAnalyticsResult
            {
                Error = $"Not enough data rows ({rows.Count}) in the selected range. Need at least 5."
            };
        }

        // 2. Write to temporary JSON file
        var tempDir = Path.Combine(Path.GetTempPath(), "garmin_analytics");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, $"input_{Guid.NewGuid():N}.json");

        try
        {
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false
            };
            var json = JsonSerializer.Serialize(rows, jsonOptions);
            await File.WriteAllTextAsync(inputPath, json, ct);

            // 3. Build Rscript arguments
            var rScriptPath = FindRScript();
            var args = BuildRArguments(inputPath, request);

            _logger.LogInformation("Running R analytics: method={Method}, rows={Rows}, vars={Vars}",
                request.Method, rows.Count, request.Variables.Count);

            // 4. Execute Rscript
            var (exitCode, stdout, stderr) = await RunProcessAsync(rScriptPath, args, ct);

            if (exitCode != 0)
            {
                _logger.LogWarning("Rscript exited with code {Code}. stderr: {Stderr}", exitCode, stderr);

                // Try to parse error from stdout (R script outputs JSON errors)
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(stdout);
                        if (doc.RootElement.TryGetProperty("error", out var errProp))
                        {
                            return new DeepAnalyticsResult { Error = errProp.GetString() };
                        }
                    }
                    catch { /* not JSON */ }
                }

                return new DeepAnalyticsResult
                {
                    Error = $"R script failed (exit {exitCode}): {TruncateForDisplay(stderr, 500)}"
                };
            }

            // 5. Parse result JSON
            return ParseResult(stdout, request.Method);
        }
        finally
        {
            // Clean up temp file
            try { File.Delete(inputPath); } catch { /* best effort */ }
        }
    }

    // ── Data extraction ──────────────────────────────────────────────

    private async Task<List<Dictionary<string, object?>>> BuildDailyDataFrameAsync(
        DateTime start, DateTime end, List<string> selectedVars, CancellationToken ct)
    {
        var startDate = start.Date;
        var endDate = end.Date;
        var endExclusive = endDate.AddDays(1);

        // Determine which variable groups we need
        bool needSteps = selectedVars.Count == 0; // if empty = all
        bool needSleep = selectedVars.Count == 0;
        bool needActivities = selectedVars.Count == 0;

        var stepVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Steps", "GoalSteps", "TotalCalories", "ActiveCalories", "Distance" };
        var sleepVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SleepScore", "TotalSleep", "DeepSleep", "LightSleep", "RemSleep", "AwakeSecs",
              "RestingHR", "BodyBatteryChange", "AvgRespiration", "LowestSpO2" };
        var actVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ActivityDuration", "ActivityDistance" };

        if (selectedVars.Count > 0)
        {
            needSteps = selectedVars.Any(v => stepVars.Contains(v));
            needSleep = selectedVars.Any(v => sleepVars.Contains(v));
            needActivities = selectedVars.Any(v => actVars.Contains(v));
        }

        // Indexed by date
        var data = new SortedDictionary<DateTime, Dictionary<string, object?>>();

        void EnsureRow(DateTime date)
        {
            if (!data.ContainsKey(date))
                data[date] = new Dictionary<string, object?> { ["date"] = date.ToString("yyyy-MM-dd") };
        }

        if (needSteps)
        {
            var steps = await _db.StepSummaries
                .Where(s => s.Date >= startDate && s.Date < endExclusive)
                .ToListAsync(ct);
            foreach (var s in steps)
            {
                var d = s.Date.Date;
                EnsureRow(d);
                var row = data[d];
                row["steps"] = s.TotalSteps;
                row["goal_steps"] = s.GoalSteps;
                row["total_calories"] = s.TotalCalories;
                row["active_calories"] = s.ActiveCalories;
                row["distance_meters"] = s.TotalDistanceMeters;
            }
        }

        if (needSleep)
        {
            var sleeps = await _db.SleepSummaries
                .Where(s => s.Date >= startDate && s.Date < endExclusive)
                .ToListAsync(ct);
            foreach (var s in sleeps)
            {
                var d = s.Date.Date;
                EnsureRow(d);
                var row = data[d];
                row["sleep_score"] = s.SleepScore;
                row["total_sleep_secs"] = s.TotalSleepSeconds;
                row["deep_sleep_secs"] = s.DeepSleepSeconds;
                row["light_sleep_secs"] = s.LightSleepSeconds;
                row["rem_sleep_secs"] = s.RemSleepSeconds;
                row["awake_secs"] = s.AwakeSeconds;
                row["resting_hr"] = s.RestingHeartRate;
                row["bb_change"] = s.BodyBatteryChange;
                row["avg_respiration"] = s.AverageRespirationValue;
                row["lowest_spo2"] = s.LowestSpO2Value;
            }
        }

        if (needActivities)
        {
            var activities = await _db.Activities
                .Where(a => a.StartTime >= startDate && a.StartTime < endExclusive)
                .GroupBy(a => a.StartTime.Date)
                .Select(g => new
                {
                    Date = g.Key,
                    TotalDurationSecs = g.Sum(a => a.Duration.TotalSeconds),
                    TotalDistanceM = g.Sum(a => a.DistanceMeters)
                })
                .ToListAsync(ct);

            foreach (var a in activities)
            {
                EnsureRow(a.Date);
                var row = data[a.Date];
                row["activity_duration_secs"] = a.TotalDurationSecs;
                row["activity_distance_m"] = a.TotalDistanceM;
            }
        }

        // Filter columns to only the selected variables (if user chose a subset)
        var result = new List<Dictionary<string, object?>>();
        HashSet<string>? keepCols = null;
        if (selectedVars.Count > 0)
        {
            keepCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "date" };
            foreach (var v in selectedVars)
            {
                if (ColumnAliases.TryGetValue(v, out var colName))
                    keepCols.Add(colName);
            }
        }

        foreach (var kv in data)
        {
            if (keepCols != null)
            {
                var filtered = new Dictionary<string, object?>();
                foreach (var col in kv.Value)
                {
                    if (keepCols.Contains(col.Key))
                        filtered[col.Key] = col.Value;
                }
                result.Add(filtered);
            }
            else
            {
                result.Add(kv.Value);
            }
        }

        return result;
    }

    // ── R process management ────────────────────────────────────────

    private string FindRScript()
    {
        // Look for the deep_analytics.R script relative to content root
        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "deep_analytics.R"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "scripts", "deep_analytics.R"),
            Path.Combine(Directory.GetCurrentDirectory(), "scripts", "deep_analytics.R"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "scripts", "deep_analytics.R"),
        };

        foreach (var path in candidates)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
                return full;
        }

        throw new FileNotFoundException(
            "Cannot find scripts/deep_analytics.R. Searched: " +
            string.Join(", ", candidates.Select(Path.GetFullPath)));
    }

    private static string BuildRArguments(string inputPath, DeepAnalyticsRequest request)
    {
        var method = request.Method.ToUpperInvariant() switch
        {
            "PCA" => "pca",
            "EFA" => "efa",
            "CCA" => "cca",
            _ => "pca"
        };

        var args = $"--input \"{inputPath}\" --method {method}";

        if (method == "efa")
            args += $" --nfactors {request.NumberOfFactors}";

        if (method == "cca")
        {
            if (request.CcaSet1.Count > 0)
            {
                var set1 = string.Join(",", request.CcaSet1.Select(v =>
                    ColumnAliases.TryGetValue(v, out var c) ? c : v));
                args += $" --set1 \"{set1}\"";
            }
            if (request.CcaSet2.Count > 0)
            {
                var set2 = string.Join(",", request.CcaSet2.Select(v =>
                    ColumnAliases.TryGetValue(v, out var c) ? c : v));
                args += $" --set2 \"{set2}\"";
            }
        }

        return args;
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunProcessAsync(
        string rScriptPath, string args, CancellationToken ct)
    {
        // Detect Rscript executable
        var rscriptExe = FindRscriptExecutable();

        var psi = new ProcessStartInfo
        {
            FileName = rscriptExe,
            Arguments = $"\"{rScriptPath}\" {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogDebug("Executing: {Exe} {Args}", psi.FileName, psi.Arguments);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        // Timeout after 120 seconds
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "R script timed out after 120 seconds");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }

    private static string FindRscriptExecutable()
    {
        // Check environment variable first
        var envPath = Environment.GetEnvironmentVariable("RSCRIPT_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        // On Windows, try common R installation paths
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var rBase = Path.Combine(programFiles, "R");

            if (Directory.Exists(rBase))
            {
                // Find latest R version
                var versions = Directory.GetDirectories(rBase)
                    .OrderByDescending(d => d)
                    .ToList();

                foreach (var vDir in versions)
                {
                    var rscript = Path.Combine(vDir, "bin", "Rscript.exe");
                    if (File.Exists(rscript))
                        return rscript;
                }
            }
        }

        // Fallback: assume Rscript is on PATH
        return "Rscript";
    }

    // ── Result parsing ──────────────────────────────────────────────

    private DeepAnalyticsResult ParseResult(string json, string method)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                return new DeepAnalyticsResult { Error = err.GetString() };
            }

            var result = new DeepAnalyticsResult
            {
                Method = method.ToUpperInvariant(),
                Observations = root.TryGetProperty("n_observations", out var nObs) ? nObs.GetInt32() : 0,
                VariableCount = root.TryGetProperty("n_variables", out var nVar) ? nVar.GetInt32() : 0,
                NaReport = ParseNaReport(root),
            };

            switch (method.ToUpperInvariant())
            {
                case "PCA":
                    result.Components = ParsePcaComponents(root);
                    result.Loadings = ParseGenericArray(root, "loadings");
                    result.Scores = ParseGenericArray(root, "scores");
                    break;

                case "EFA":
                    result.NumberOfFactors = root.TryGetProperty("n_factors", out var nf) ? nf.GetInt32() : null;
                    result.BartlettChiSq = root.TryGetProperty("bartlett_chisq", out var bc) ? bc.GetDouble() : null;
                    result.BartlettPValue = root.TryGetProperty("bartlett_pvalue", out var bp) ? bp.GetDouble() : null;
                    result.KmoOverall = root.TryGetProperty("kmo_overall", out var kmo) ? kmo.GetDouble() : null;
                    result.Eigenvalues = ParseDoubleArray(root, "eigenvalues");
                    result.Factors = ParseEfaFactors(root);
                    result.Loadings = ParseGenericArray(root, "loadings");
                    result.Communalities = ParseCommunalities(root);
                    break;

                case "CCA":
                    result.Set1Variables = ParseStringArray(root, "set1_variables");
                    result.Set2Variables = ParseStringArray(root, "set2_variables");
                    result.Dimensions = ParseCcaDimensions(root);
                    result.XCoefficients = ParseGenericArray(root, "x_coefficients");
                    result.YCoefficients = ParseGenericArray(root, "y_coefficients");
                    break;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse R output");
            return new DeepAnalyticsResult
            {
                Error = $"Failed to parse R output: {ex.Message}. Raw output: {TruncateForDisplay(json, 300)}"
            };
        }
    }

    private static List<PcaComponent>? ParsePcaComponents(JsonElement root)
    {
        if (!root.TryGetProperty("components", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        return arr.EnumerateArray().Select(el => new PcaComponent
        {
            Name = el.GetProperty("name").GetString() ?? "",
            Eigenvalue = el.GetProperty("eigenvalue").GetDouble(),
            VariancePct = el.GetProperty("variance_pct").GetDouble(),
            CumulativePct = el.GetProperty("cumulative_pct").GetDouble(),
        }).ToList();
    }

    private static List<EfaFactor>? ParseEfaFactors(JsonElement root)
    {
        if (!root.TryGetProperty("factors", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        return arr.EnumerateArray().Select(el => new EfaFactor
        {
            Factor = el.GetProperty("factor").GetString() ?? "",
            SsLoading = el.GetProperty("ss_loading").GetDouble(),
            VariancePct = el.GetProperty("variance_pct").GetDouble(),
        }).ToList();
    }

    private static List<EfaCommunality>? ParseCommunalities(JsonElement root)
    {
        if (!root.TryGetProperty("communalities", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        return arr.EnumerateArray().Select(el => new EfaCommunality
        {
            Variable = el.GetProperty("variable").GetString() ?? "",
            Communality = el.GetProperty("communality").GetDouble(),
            Uniqueness = el.GetProperty("uniqueness").GetDouble(),
        }).ToList();
    }

    private static List<CcaDimension>? ParseCcaDimensions(JsonElement root)
    {
        if (!root.TryGetProperty("dimensions", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        return arr.EnumerateArray().Select(el => new CcaDimension
        {
            Dimension = el.GetProperty("dimension").GetInt32(),
            CanonicalCorr = el.GetProperty("canonical_corr").GetDouble(),
            CanonicalCorrSq = el.GetProperty("canonical_corr_sq").GetDouble(),
        }).ToList();
    }

    private static List<Dictionary<string, object>>? ParseGenericArray(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<Dictionary<string, object>>();
        foreach (var el in arr.EnumerateArray())
        {
            var dict = new Dictionary<string, object>();
            foreach (var p in el.EnumerateObject())
            {
                dict[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.Number => p.Value.GetDouble(),
                    JsonValueKind.String => p.Value.GetString()!,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => p.Value.GetRawText()
                };
            }
            result.Add(dict);
        }
        return result;
    }

    private static List<double>? ParseDoubleArray(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        return arr.EnumerateArray().Select(e => e.GetDouble()).ToList();
    }

    private static List<string>? ParseStringArray(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        return arr.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
    }

    private static NaReport? ParseNaReport(JsonElement root)
    {
        if (!root.TryGetProperty("na_report", out var nr) || nr.ValueKind != JsonValueKind.Object)
            return null;

        return new NaReport
        {
            TotalCells = nr.TryGetProperty("total_cells", out var tc) ? tc.GetInt32() : 0,
            NaCellsBefore = nr.TryGetProperty("na_cells_before", out var nb) ? nb.GetInt32() : 0,
            NaCellsAfter = nr.TryGetProperty("na_cells_after", out var na2) ? na2.GetInt32() : 0,
            RowsBefore = nr.TryGetProperty("rows_before", out var rb) ? rb.GetInt32() : 0,
            RowsAfter = nr.TryGetProperty("rows_after", out var ra) ? ra.GetInt32() : 0,
            ColumnsUsed = nr.TryGetProperty("columns_used", out var cu) && cu.ValueKind == JsonValueKind.Array
                ? cu.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : new List<string>()
        };
    }

    private static string TruncateForDisplay(string? s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        return s.Length <= maxLen ? s : s[..maxLen] + "...";
    }
}

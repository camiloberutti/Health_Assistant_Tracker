using System;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Models;
using GarminTempApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DeepAnalyticsController : ControllerBase
{
    private readonly RAnalyticsService _analytics;
    private readonly ILogger<DeepAnalyticsController> _logger;

    public DeepAnalyticsController(RAnalyticsService analytics, ILogger<DeepAnalyticsController> logger)
    {
        _analytics = analytics;
        _logger = logger;
    }

    /// <summary>
    /// Returns the list of selectable variable keys the UI can offer.
    /// </summary>
    [HttpGet("variables")]
    public IActionResult GetAvailableVariables()
    {
        return Ok(RAnalyticsService.AvailableVariables);
    }

    /// <summary>
    /// Scans the database for the given date range and returns which variables
    /// have enough data to be usable (above the density threshold).
    /// </summary>
    [HttpPost("density")]
    public async Task<IActionResult> GetDataDensity([FromBody] DataDensityRequest request, CancellationToken ct)
    {
        if (request.EndDate < request.StartDate)
        {
            return BadRequest(new { error = "EndDate must be >= StartDate" });
        }

        var density = Math.Clamp(request.MinDensity, 0.0, 1.0);

        try
        {
            var result = await _analytics.GetDataDensityAsync(
                request.StartDate, request.EndDate, density, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Data density check failed");
            return StatusCode(500, new { error = "Density check failed: " + ex.Message });
        }
    }

    /// <summary>
    /// Runs PCA, EFA, or CCA on the selected date range and variables.
    /// </summary>
    [HttpPost("run")]
    public async Task<IActionResult> Run([FromBody] DeepAnalyticsRequest request, CancellationToken ct)
    {
        if (request.EndDate < request.StartDate)
        {
            return BadRequest(new { error = "EndDate must be >= StartDate" });
        }

        try
        {
            var result = await _analytics.RunAsync(request, ct);

            if (!string.IsNullOrEmpty(result.Error))
            {
                return UnprocessableEntity(result);
            }

            return Ok(result);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogError(ex, "R script not found");
            return StatusCode(500, new { error = "R environment not configured. " + ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deep analytics failed");
            return StatusCode(500, new { error = "Analysis failed: " + ex.Message });
        }
    }
}

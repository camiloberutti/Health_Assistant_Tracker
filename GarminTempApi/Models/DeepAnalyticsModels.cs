using System;
using System.Collections.Generic;

namespace GarminTempApi.Models;

// ── Request DTOs ──────────────────────────────────────────────────

public class DeepAnalyticsRequest
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    /// <summary>PCA | EFA | CCA | CLUSTER | REGRESSION | MDS</summary>
    public string Method { get; set; } = "PCA";

    /// <summary>Subset of variable keys the user selected (e.g. "Steps", "SleepScore", "RestingHR").</summary>
    public List<string> Variables { get; set; } = new();

    /// <summary>Number of factors for EFA (default 5).</summary>
    public int NumberOfFactors { get; set; } = 5;

    /// <summary>CCA set 1 variable keys.</summary>
    public List<string> CcaSet1 { get; set; } = new();

    /// <summary>CCA set 2 variable keys.</summary>
    public List<string> CcaSet2 { get; set; } = new();

    // ── Clustering options ──
    /// <summary>Number of clusters for k-means (default 3).</summary>
    public int NumberOfClusters { get; set; } = 3;

    /// <summary>Hierarchical clustering linkage method: single | complete | average (default complete).</summary>
    public string LinkageMethod { get; set; } = "complete";

    // ── Regression options ──
    /// <summary>The dependent (response) variable key for regression.</summary>
    public string? RegressionTarget { get; set; }

    // ── MDS options ──
    /// <summary>Number of dimensions for MDS output (2 or 3). Default 2.</summary>
    public int MdsDimensions { get; set; } = 2;
}

public class DataDensityRequest
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    /// <summary>Minimum population ratio (0.0–1.0) for a variable to be considered available. Default 0.10 (10%).</summary>
    public double MinDensity { get; set; } = 0.10;
}

/// <summary>Per-variable density info returned by the density-check endpoint.</summary>
public class VariableDensityInfo
{
    public string Key { get; set; } = string.Empty;
    public string Column { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public int TotalDays { get; set; }
    public int PopulatedDays { get; set; }
    public double Density { get; set; }
    public bool Available { get; set; }
}

public class DataDensityResult
{
    public int TotalDaysInRange { get; set; }
    public double MinDensityThreshold { get; set; }
    public List<VariableDensityInfo> Variables { get; set; } = new();
}

// ── Response DTOs ──────────────────────────────────────────────────

public class DeepAnalyticsResult
{
    public string Method { get; set; } = string.Empty;
    public int Observations { get; set; }
    public int VariableCount { get; set; }

    // PCA fields
    public List<PcaComponent>? Components { get; set; }
    public List<Dictionary<string, object>>? Loadings { get; set; }
    public List<Dictionary<string, object>>? Scores { get; set; }

    // EFA fields
    public int? NumberOfFactors { get; set; }
    public double? BartlettChiSq { get; set; }
    public double? BartlettPValue { get; set; }
    public double? KmoOverall { get; set; }
    public List<double>? Eigenvalues { get; set; }
    public List<EfaFactor>? Factors { get; set; }
    public List<EfaCommunality>? Communalities { get; set; }

    // CCA fields
    public List<string>? Set1Variables { get; set; }
    public List<string>? Set2Variables { get; set; }
    public List<CcaDimension>? Dimensions { get; set; }
    public List<Dictionary<string, object>>? XCoefficients { get; set; }
    public List<Dictionary<string, object>>? YCoefficients { get; set; }

    // Clustering fields
    public ClusteringResult? Clustering { get; set; }

    // Regression fields
    public RegressionResult? Regression { get; set; }

    // MDS fields
    public MdsResult? Mds { get; set; }

    // Shared
    public NaReport? NaReport { get; set; }
    public string? Error { get; set; }
}

public class PcaComponent
{
    public string Name { get; set; } = string.Empty;
    public double Eigenvalue { get; set; }
    public double VariancePct { get; set; }
    public double CumulativePct { get; set; }
}

public class EfaFactor
{
    public string Factor { get; set; } = string.Empty;
    public double SsLoading { get; set; }
    public double VariancePct { get; set; }
}

public class EfaCommunality
{
    public string Variable { get; set; } = string.Empty;
    public double Communality { get; set; }
    public double Uniqueness { get; set; }
}

public class CcaDimension
{
    public int Dimension { get; set; }
    public double CanonicalCorr { get; set; }
    public double CanonicalCorrSq { get; set; }
}

// ── Clustering DTOs ─────────────────────────────────────────────

public class ClusteringResult
{
    public double HopkinsStatistic { get; set; }
    public double HopkinsPValue { get; set; }
    public bool ClusteringTendency { get; set; }

    // K-Means
    public int K { get; set; }
    public double TotalWithinSS { get; set; }
    public double BetweenSS { get; set; }
    public double TotalSS { get; set; }
    public List<double>? WithinSS { get; set; }
    public List<int>? ClusterSizes { get; set; }
    public List<Dictionary<string, object>>? ClusterCenters { get; set; }
    public double SilhouetteAvg { get; set; }
    public List<Dictionary<string, object>>? SilhouettePerPoint { get; set; }

    // Hierarchical
    public string LinkageMethod { get; set; } = string.Empty;
    public List<Dictionary<string, object>>? DendrogramMerge { get; set; }
    public List<double>? DendrogramHeight { get; set; }
    public List<string>? DendrogramLabels { get; set; }

    // Elbow data (WCSS for k=1..10)
    public List<double>? ElbowWcss { get; set; }

    // Per-point assignments
    public List<int>? Assignments { get; set; }
    public List<Dictionary<string, object>>? Points { get; set; }
}

// ── Regression DTOs ─────────────────────────────────────────────

public class RegressionResult
{
    public string TargetVariable { get; set; } = string.Empty;
    public List<string> Predictors { get; set; } = new();
    public double RSquared { get; set; }
    public double AdjustedRSquared { get; set; }
    public double FStatistic { get; set; }
    public double FPValue { get; set; }
    public List<RegressionCoefficient>? Coefficients { get; set; }
    public List<double>? Residuals { get; set; }
    public List<double>? FittedValues { get; set; }
    public double ShapiroPValue { get; set; }
    public bool ResidualsNormal { get; set; }
}

public class RegressionCoefficient
{
    public string Variable { get; set; } = string.Empty;
    public double Estimate { get; set; }
    public double StdError { get; set; }
    public double TValue { get; set; }
    public double PValue { get; set; }
    public string Significance { get; set; } = string.Empty;
}

// ── MDS DTOs ────────────────────────────────────────────────────

public class MdsResult
{
    public int Dimensions { get; set; }
    public double GoF { get; set; }
    public List<double>? Eigenvalues { get; set; }
    public List<Dictionary<string, object>>? Points { get; set; }
    public double Stress { get; set; }
}

public class NaReport
{
    public int TotalCells { get; set; }
    public int NaCellsBefore { get; set; }
    public int NaCellsAfter { get; set; }
    public int RowsBefore { get; set; }
    public int RowsAfter { get; set; }
    public List<string> ColumnsUsed { get; set; } = new();
}

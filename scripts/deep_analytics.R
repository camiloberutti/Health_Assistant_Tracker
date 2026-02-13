#!/usr/bin/env Rscript
# deep_analytics.R
# -------------------------------------------------------------------
# R wrapper that accepts a JSON dataframe from the .NET backend,
# runs PCA, EFA, CCA, Clustering, Linear Regression, or MDS,
# and returns results as JSON to stdout.
#
# Usage:
#   Rscript deep_analytics.R --input data.json --method pca
#   Rscript deep_analytics.R --input data.json --method efa --nfactors 5
#   Rscript deep_analytics.R --input data.json --method cca --set1 "hr_avg,hrv" --set2 "sleep_score,deep_sleep"
#   Rscript deep_analytics.R --input data.json --method cluster --nclusters 3 --linkage complete
#   Rscript deep_analytics.R --input data.json --method regression --target sleep_score
#   Rscript deep_analytics.R --input data.json --method mds --mdsdims 2
#
# The input JSON should be an array of objects (rows), e.g.:
#   [{"date":"2025-01-01","steps":8000,"sleep_score":72,...}, ...]
#
# Output: a single JSON object written to stdout.
# -------------------------------------------------------------------

suppressPackageStartupMessages({
  library(jsonlite)
  library(psych)
})

# ── Argument parsing ─────────────────────────────────────────────────
args <- commandArgs(trailingOnly = TRUE)

parse_args <- function(args) {
  opts <- list(
    input     = NULL,
    method    = "pca",
    nfactors  = 5,
    set1      = NULL,
    set2      = NULL,
    nclusters = 3,
    linkage   = "complete",
    target    = NULL,
    mdsdims   = 2
  )
  i <- 1
  while (i <= length(args)) {
    key <- args[i]
    if (key == "--input"     && i < length(args)) { opts$input     <- args[i + 1]; i <- i + 2; next }
    if (key == "--method"    && i < length(args)) { opts$method    <- tolower(args[i + 1]); i <- i + 2; next }
    if (key == "--nfactors"  && i < length(args)) { opts$nfactors  <- as.integer(args[i + 1]); i <- i + 2; next }
    if (key == "--set1"      && i < length(args)) { opts$set1      <- args[i + 1]; i <- i + 2; next }
    if (key == "--set2"      && i < length(args)) { opts$set2      <- args[i + 1]; i <- i + 2; next }
    if (key == "--nclusters" && i < length(args)) { opts$nclusters <- as.integer(args[i + 1]); i <- i + 2; next }
    if (key == "--linkage"   && i < length(args)) { opts$linkage   <- tolower(args[i + 1]); i <- i + 2; next }
    if (key == "--target"    && i < length(args)) { opts$target    <- args[i + 1]; i <- i + 2; next }
    if (key == "--mdsdims"   && i < length(args)) { opts$mdsdims   <- as.integer(args[i + 1]); i <- i + 2; next }
    i <- i + 1
  }
  return(opts)
}

opts <- parse_args(args)

if (is.null(opts$input)) {
  cat(toJSON(list(error = "Missing --input argument"), auto_unbox = TRUE))
  quit(status = 1)
}

# ── Load data ────────────────────────────────────────────────────────
raw_json <- readLines(opts$input, warn = FALSE)
raw_json <- paste(raw_json, collapse = "\n")
df <- fromJSON(raw_json)

# Keep only numeric columns for analysis
numeric_cols <- names(df)[sapply(df, is.numeric)]
if (length(numeric_cols) < 2) {
  cat(toJSON(list(error = paste0("Need at least 2 numeric variables for analysis, got ",
                                  length(numeric_cols), ": ",
                                  paste(numeric_cols, collapse = ", ")),
                  received_columns = names(df)),
             auto_unbox = TRUE))
  quit(status = 1)
}

df_num <- df[, numeric_cols, drop = FALSE]

# ── Handle missing data (NAs) ───────────────────────────────────────
# Strategy: column-median imputation for sporadic NAs, then drop rows
# that are still all-NA. This preserves maximum data while being robust
# to the irregular gaps common in health tracking data.
impute_median <- function(x) {
  med <- median(x, na.rm = TRUE)
  if (is.na(med)) med <- 0
  x[is.na(x)] <- med
  return(x)
}

na_report <- list(
  total_cells      = prod(dim(df_num)),
  na_cells_before  = sum(is.na(df_num)),
  rows_before      = nrow(df_num)
)

# Remove rows that are entirely NA
all_na_rows <- apply(df_num, 1, function(r) all(is.na(r)))
df_num <- df_num[!all_na_rows, , drop = FALSE]

# Remove columns with zero variance or all NA
col_ok <- sapply(df_num, function(x) {
  vals <- na.omit(x)
  length(vals) >= 3 && sd(vals) > 0
})
df_num <- df_num[, col_ok, drop = FALSE]

# Median imputation for remaining NAs
df_num <- as.data.frame(lapply(df_num, impute_median))

na_report$na_cells_after <- sum(is.na(df_num))
na_report$rows_after     <- nrow(df_num)
na_report$columns_used   <- names(df_num)
na_report$columns_received <- numeric_cols
na_report$columns_dropped  <- setdiff(numeric_cols, names(df_num))

if (nrow(df_num) < 5) {
  cat(toJSON(list(error = "Too few complete observations after cleaning (need >= 5)",
                  na_report = na_report), auto_unbox = TRUE))
  quit(status = 1)
}

# ── Scale ────────────────────────────────────────────────────────────
df_scaled <- scale(df_num)

# ══════════════════════════════════════════════════════════════════════
# Method: PCA
# ══════════════════════════════════════════════════════════════════════
run_pca <- function(df_scaled) {
  n_vars <- ncol(df_scaled)
  n_obs  <- nrow(df_scaled)

  # ── Guard: PCA needs >= 2 variables ──
  if (n_vars < 2) {
    cat(toJSON(list(error = paste0("PCA requires at least 2 variables, got ", n_vars, "."),
                    na_report = na_report), auto_unbox = TRUE))
    quit(status = 1)
  }

  pca <- prcomp(df_scaled, center = TRUE, scale. = TRUE)

  importance <- summary(pca)$importance
  n_pcs <- ncol(importance)

  # Eigenvalues = sdev^2
  eigenvalues <- pca$sdev^2

  # Build per-component info
  components <- lapply(seq_len(n_pcs), function(k) {
    list(
      name              = paste0("PC", k),
      eigenvalue        = round(eigenvalues[k], 4),
      variance_pct      = round(importance["Proportion of Variance", k] * 100, 2),
      cumulative_pct    = round(importance["Cumulative Proportion", k] * 100, 2)
    )
  })

  # Loadings matrix (rotation)
  loadings_df <- as.data.frame(pca$rotation)
  loadings_df$variable <- rownames(loadings_df)

  # Scores (for cluster viz) – first 3 PCs
  max_pc <- min(3, n_pcs)
  scores_df <- as.data.frame(pca$x[, 1:max_pc, drop = FALSE])
  scores_df$row_index <- seq_len(nrow(scores_df))

  # Include dates if available
  if ("date" %in% tolower(names(df))) {
    date_col <- names(df)[tolower(names(df)) == "date"][1]
    valid_dates <- df[[date_col]][!all_na_rows]
    if (length(valid_dates) == nrow(scores_df)) {
      scores_df$date <- valid_dates
    }
  }

  list(
    method         = "PCA",
    n_observations = nrow(df_scaled),
    n_variables    = ncol(df_scaled),
    components     = components,
    loadings       = loadings_df,
    scores         = scores_df,
    na_report      = na_report
  )
}

# ══════════════════════════════════════════════════════════════════════
# Method: EFA
# ══════════════════════════════════════════════════════════════════════
run_efa <- function(df_scaled, nfactors) {
  n_vars <- ncol(df_scaled)
  n_obs  <- nrow(df_scaled)

  # ── Guard: EFA needs >= 3 variables ──
  if (n_vars < 3) {
    cat(toJSON(list(error = paste0("EFA requires at least 3 variables, got ", n_vars,
                                    ". Select more variables or use PCA instead."),
                    na_report = na_report), auto_unbox = TRUE))
    quit(status = 1)
  }

  # Auto-cap nfactors: can't exceed p-1, and the Heywood case limit
  max_factors <- max(1, floor((n_vars - 1) / 2))
  max_factors <- min(max_factors, n_vars - 1)
  if (nfactors > max_factors) {
    nfactors <- max_factors
  }
  # Also ensure n_obs >> nfactors
  if (n_obs < nfactors * 3) {
    nfactors <- max(1, floor(n_obs / 3))
  }

  cor_matrix <- cor(df_scaled, use = "pairwise.complete.obs")

  # Bartlett test
  bartlett <- tryCatch(
    psych::cortest.bartlett(cor_matrix, n = nrow(df_scaled)),
    error = function(e) list(chisq = NA, p.value = NA)
  )

  # KMO
  kmo <- tryCatch(
    psych::KMO(cor_matrix),
    error = function(e) list(MSA = NA, MSAi = NA)
  )

  # Factor analysis (Principal Axis, varimax rotation)
  fa_result <- tryCatch(
    psych::fa(df_scaled, fm = "pa", nfactors = nfactors, rotate = "varimax"),
    error = function(e) {
      cat(toJSON(list(error = paste("EFA failed:", e$message),
                      na_report = na_report), auto_unbox = TRUE))
      quit(status = 1)
    }
  )

  # Loadings
  loads <- unclass(fa_result$loadings)
  loadings_df <- as.data.frame(loads)
  loadings_df$variable <- rownames(loads)

  # Communalities & uniquenesses
  communalities <- data.frame(
    variable     = names(fa_result$communality),
    communality  = round(as.numeric(fa_result$communality), 4),
    uniqueness   = round(as.numeric(fa_result$uniquenesses), 4)
  )

  # Eigenvalues from the factor result
  eigenvalues <- fa_result$values

  # Variance explained per factor
  var_explained <- lapply(seq_len(nfactors), function(k) {
    ss_loading <- sum(loads[, k]^2)
    list(
      factor           = paste0("Factor", k),
      ss_loading       = round(ss_loading, 4),
      variance_pct     = round(ss_loading / n_vars * 100, 2)
    )
  })

  list(
    method          = "EFA",
    n_observations  = nrow(df_scaled),
    n_variables     = ncol(df_scaled),
    n_factors       = nfactors,
    bartlett_chisq  = round(bartlett$chisq, 2),
    bartlett_pvalue = bartlett$p.value,
    kmo_overall     = round(kmo$MSA, 3),
    eigenvalues     = round(eigenvalues, 4),
    factors         = var_explained,
    loadings        = loadings_df,
    communalities   = communalities,
    na_report       = na_report
  )
}

# ══════════════════════════════════════════════════════════════════════
# Method: CCA (Canonical Correlation Analysis)
# ══════════════════════════════════════════════════════════════════════
run_cca <- function(df_num, set1_names, set2_names) {
  if (is.null(set1_names) || is.null(set2_names)) {
    cat(toJSON(list(error = "CCA requires --set1 and --set2 comma-separated variable lists"),
               auto_unbox = TRUE))
    quit(status = 1)
  }

  set1_vars <- trimws(unlist(strsplit(set1_names, ",")))
  set2_vars <- trimws(unlist(strsplit(set2_names, ",")))

  missing1 <- setdiff(set1_vars, names(df_num))
  missing2 <- setdiff(set2_vars, names(df_num))
  if (length(missing1) > 0 || length(missing2) > 0) {
    cat(toJSON(list(error = paste("Variables not found. Missing from set1:",
                                  paste(missing1, collapse = ", "),
                                  "| Missing from set2:",
                                  paste(missing2, collapse = ", "))),
               auto_unbox = TRUE))
    quit(status = 1)
  }

  X <- as.matrix(df_num[, set1_vars, drop = FALSE])
  Y <- as.matrix(df_num[, set2_vars, drop = FALSE])

  # Scale
  X <- scale(X)
  Y <- scale(Y)

  cca_result <- tryCatch(
    cancor(X, Y),
    error = function(e) {
      cat(toJSON(list(error = paste("CCA failed:", e$message)), auto_unbox = TRUE))
      quit(status = 1)
    }
  )

  n_dims <- length(cca_result$cor)
  dimensions <- lapply(seq_len(n_dims), function(k) {
    list(
      dimension         = k,
      canonical_corr    = round(cca_result$cor[k], 4),
      canonical_corr_sq = round(cca_result$cor[k]^2, 4)
    )
  })

  # X and Y coefficients
  xcoef_df <- as.data.frame(cca_result$xcoef)
  xcoef_df$variable <- rownames(cca_result$xcoef)
  ycoef_df <- as.data.frame(cca_result$ycoef)
  ycoef_df$variable <- rownames(cca_result$ycoef)

  list(
    method          = "CCA",
    n_observations  = nrow(df_num),
    set1_variables  = set1_vars,
    set2_variables  = set2_vars,
    dimensions      = dimensions,
    x_coefficients  = xcoef_df,
    y_coefficients  = ycoef_df,
    na_report       = na_report
  )
}

# ══════════════════════════════════════════════════════════════════════
# Method: CLUSTER (K-Means + Hierarchical Clustering)
# Based on Lab 8: kmeans(), hclust(), silhouette(), hopkins()
# ══════════════════════════════════════════════════════════════════════
run_cluster <- function(df_scaled, df_num, nclusters, linkage) {
  n_vars <- ncol(df_scaled)
  n_obs  <- nrow(df_scaled)

  if (n_vars < 2) {
    cat(toJSON(list(error = paste0("Clustering requires at least 2 variables, got ", n_vars, "."),
                    na_report = na_report), auto_unbox = TRUE))
    quit(status = 1)
  }
  if (n_obs < nclusters) {
    nclusters <- n_obs
  }
  if (nclusters < 2) nclusters <- 2

  # ── Hopkins test for clustering tendency ──
  hopkins_stat <- NA
  hopkins_pval <- NA
  clustering_tendency <- TRUE
  tryCatch({
    # Simple Hopkins statistic implementation (avoids extra package)
    set.seed(42)
    m <- min(n_obs - 1, max(10, floor(n_obs * 0.1)))
    sample_idx <- sample(1:n_obs, m)
    random_points <- matrix(runif(m * n_vars,
                                  min = apply(df_scaled, 2, min),
                                  max = apply(df_scaled, 2, max)),
                            nrow = m, ncol = n_vars)
    
    # Nearest neighbor distances for sampled points
    nn_sample <- sapply(sample_idx, function(i) {
      dists <- apply(df_scaled[-i, , drop = FALSE], 1, function(row) sum((row - df_scaled[i, ])^2))
      sqrt(min(dists))
    })
    
    # Nearest neighbor distances for random points
    nn_random <- sapply(1:m, function(i) {
      dists <- apply(df_scaled, 1, function(row) sum((row - random_points[i, ])^2))
      sqrt(min(dists))
    })
    
    hopkins_stat <- sum(nn_random) / (sum(nn_random) + sum(nn_sample))
    # H close to 0.5 = random, close to 1 = clusterable
    clustering_tendency <- hopkins_stat > 0.5
  }, error = function(e) {
    hopkins_stat <<- NA
    clustering_tendency <<- TRUE
  })

  # ── Elbow method: WCSS for k = 1..10 ──
  max_k <- min(10, n_obs - 1)
  elbow_wcss <- sapply(1:max_k, function(k) {
    km <- kmeans(df_scaled, centers = k, nstart = 10, iter.max = 50)
    km$tot.withinss
  })

  # ── K-Means clustering ──
  km_result <- kmeans(df_scaled, centers = nclusters, nstart = 25, iter.max = 100)

  # ── Silhouette scores ──
  sil_avg <- 0
  sil_per_point <- list()
  tryCatch({
    if (requireNamespace("cluster", quietly = TRUE)) {
      dist_matrix <- dist(df_scaled)
      sil <- cluster::silhouette(km_result$cluster, dist_matrix)
      sil_avg <- mean(sil[, "sil_width"])
      sil_per_point <- data.frame(
        row_index = 1:nrow(sil),
        cluster = as.integer(sil[, "cluster"]),
        neighbor = as.integer(sil[, "neighbor"]),
        sil_width = round(as.numeric(sil[, "sil_width"]), 4)
      )
    }
  }, error = function(e) {
    sil_avg <<- 0
  })

  # ── Hierarchical clustering ──
  valid_linkage <- if (linkage %in% c("single", "complete", "average", "ward.D2")) linkage else "complete"
  dist_matrix <- dist(df_scaled)
  hc <- hclust(dist_matrix, method = valid_linkage)
  hc_clusters <- cutree(hc, k = nclusters)

  # Build merge info for dendrogram
  merge_df <- as.data.frame(hc$merge)
  names(merge_df) <- c("a", "b")

  # ── Cluster centers (in original scale) ──
  centers_df <- as.data.frame(km_result$centers)
  centers_df$cluster <- 1:nrow(centers_df)

  # ── Points with assignments ──
  points_df <- as.data.frame(df_num)
  points_df$km_cluster <- km_result$cluster
  points_df$hc_cluster <- hc_clusters

  # Include dates if available
  if ("date" %in% tolower(names(df))) {
    date_col <- names(df)[tolower(names(df)) == "date"][1]
    valid_dates <- df[[date_col]][!all_na_rows]
    if (length(valid_dates) == nrow(points_df)) {
      points_df$date <- valid_dates
    }
  }

  list(
    method             = "CLUSTER",
    n_observations     = n_obs,
    n_variables        = n_vars,
    hopkins_statistic  = round(hopkins_stat, 4),
    hopkins_pvalue     = round(hopkins_pval, 4),
    clustering_tendency = clustering_tendency,
    k                  = nclusters,
    total_withinss     = round(km_result$tot.withinss, 4),
    betweenss          = round(km_result$betweenss, 4),
    totalss            = round(km_result$totss, 4),
    withinss           = round(km_result$withinss, 4),
    cluster_sizes      = as.integer(km_result$size),
    cluster_centers    = centers_df,
    silhouette_avg     = round(sil_avg, 4),
    silhouette_per_point = sil_per_point,
    linkage_method     = valid_linkage,
    dendrogram_merge   = merge_df,
    dendrogram_height  = round(hc$height, 4),
    dendrogram_labels  = hc$labels,
    elbow_wcss         = round(elbow_wcss, 4),
    assignments        = as.integer(km_result$cluster),
    points             = points_df,
    na_report          = na_report
  )
}

# ══════════════════════════════════════════════════════════════════════
# Method: REGRESSION (Multiple Linear Regression)
# Based on Lab 1 & Lab 7: lm(), shapiro.test()
# ══════════════════════════════════════════════════════════════════════
run_regression <- function(df_num, target) {
  if (is.null(target) || !(target %in% names(df_num))) {
    cat(toJSON(list(error = paste0("Regression requires --target to be a valid numeric column. ",
                                    "Available: ", paste(names(df_num), collapse = ", ")),
                    na_report = na_report), auto_unbox = TRUE))
    quit(status = 1)
  }

  predictors <- setdiff(names(df_num), target)
  if (length(predictors) < 1) {
    cat(toJSON(list(error = "Regression requires at least 1 predictor variable (select ≥2 total variables).",
                    na_report = na_report), auto_unbox = TRUE))
    quit(status = 1)
  }

  # Build formula: target ~ pred1 + pred2 + ...
  formula_str <- paste(target, "~", paste(predictors, collapse = " + "))
  model <- lm(as.formula(formula_str), data = df_num)
  s <- summary(model)

  # Coefficients table
  coef_df <- as.data.frame(s$coefficients)
  coef_df$variable <- rownames(coef_df)
  names(coef_df) <- c("estimate", "std_error", "t_value", "p_value", "variable")
  coef_df$significance <- ifelse(coef_df$p_value < 0.001, "***",
                           ifelse(coef_df$p_value < 0.01, "**",
                           ifelse(coef_df$p_value < 0.05, "*",
                           ifelse(coef_df$p_value < 0.1, ".", ""))))
  coef_df <- coef_df[, c("variable", "estimate", "std_error", "t_value", "p_value", "significance")]

  # Residual normality check
  residuals_vec <- as.numeric(residuals(model))
  shapiro_p <- NA
  residuals_normal <- FALSE
  tryCatch({
    if (length(residuals_vec) >= 3 && length(residuals_vec) <= 5000) {
      st <- shapiro.test(residuals_vec)
      shapiro_p <- st$p.value
      residuals_normal <- shapiro_p > 0.05
    }
  }, error = function(e) {})

  # F-statistic
  f_stat <- if (!is.null(s$fstatistic)) s$fstatistic[1] else NA
  f_pval <- if (!is.null(s$fstatistic)) {
    pf(s$fstatistic[1], s$fstatistic[2], s$fstatistic[3], lower.tail = FALSE)
  } else NA

  list(
    method            = "REGRESSION",
    n_observations    = nrow(df_num),
    n_variables       = ncol(df_num),
    target_variable   = target,
    predictors        = predictors,
    r_squared         = round(s$r.squared, 6),
    adjusted_r_squared = round(s$adj.r.squared, 6),
    f_statistic       = round(f_stat, 4),
    f_pvalue          = f_pval,
    coefficients      = coef_df,
    residuals         = round(residuals_vec, 6),
    fitted_values     = round(as.numeric(fitted(model)), 6),
    shapiro_pvalue    = round(shapiro_p, 6),
    residuals_normal  = residuals_normal,
    na_report         = na_report
  )
}

# ══════════════════════════════════════════════════════════════════════
# Method: MDS (Classical Multidimensional Scaling)
# Based on Lab 9 & MultiDimensionalScaling.qmd: cmdscale()
# ══════════════════════════════════════════════════════════════════════
run_mds <- function(df_scaled, ndims) {
  n_vars <- ncol(df_scaled)
  n_obs  <- nrow(df_scaled)

  if (n_vars < 2) {
    cat(toJSON(list(error = paste0("MDS requires at least 2 variables, got ", n_vars, "."),
                    na_report = na_report), auto_unbox = TRUE))
    quit(status = 1)
  }

  ndims <- min(ndims, n_vars, n_obs - 1)
  if (ndims < 2) ndims <- 2

  dist_matrix <- dist(df_scaled)
  mds_result <- cmdscale(dist_matrix, k = ndims, eig = TRUE)

  # Goodness of fit
  eig_vals <- mds_result$eig
  positive_eigs <- eig_vals[eig_vals > 0]
  gof <- sum(positive_eigs[1:min(ndims, length(positive_eigs))]) / sum(positive_eigs)

  # Stress (Kruskal's stress-1)
  coords <- mds_result$points
  fit_dist <- as.matrix(dist(coords))
  orig_dist <- as.matrix(dist_matrix)
  stress <- sqrt(sum((orig_dist - fit_dist)^2) / sum(orig_dist^2))

  # Build points dataframe
  points_df <- as.data.frame(coords)
  col_names <- paste0("Dim", 1:ndims)
  names(points_df) <- col_names
  points_df$row_index <- 1:nrow(points_df)

  # Include dates if available
  if ("date" %in% tolower(names(df))) {
    date_col <- names(df)[tolower(names(df)) == "date"][1]
    valid_dates <- df[[date_col]][!all_na_rows]
    if (length(valid_dates) == nrow(points_df)) {
      points_df$date <- valid_dates
    }
  }

  list(
    method         = "MDS",
    n_observations = n_obs,
    n_variables    = n_vars,
    dimensions     = ndims,
    gof            = round(gof, 6),
    eigenvalues    = round(eig_vals[1:min(10, length(eig_vals))], 4),
    points         = points_df,
    stress         = round(stress, 6),
    na_report      = na_report
  )
}

# ── Dispatch ─────────────────────────────────────────────────────────
result <- switch(opts$method,
  "pca"        = run_pca(df_scaled),
  "efa"        = run_efa(df_scaled, opts$nfactors),
  "cca"        = run_cca(df_num, opts$set1, opts$set2),
  "cluster"    = run_cluster(df_scaled, df_num, opts$nclusters, opts$linkage),
  "regression" = run_regression(df_num, opts$target),
  "mds"        = run_mds(df_scaled, opts$mdsdims),
  {
    cat(toJSON(list(error = paste("Unknown method:", opts$method)), auto_unbox = TRUE))
    quit(status = 1)
  }
)

# ── Output ───────────────────────────────────────────────────────────
cat(toJSON(result, auto_unbox = TRUE, digits = 6, pretty = FALSE))

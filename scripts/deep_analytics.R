#!/usr/bin/env Rscript
# deep_analytics.R
# -------------------------------------------------------------------
# R wrapper that accepts a JSON dataframe from the .NET backend,
# runs PCA, EFA, and/or CCA, and returns results as JSON to stdout.
#
# Usage:
#   Rscript deep_analytics.R --input data.json --method pca
#   Rscript deep_analytics.R --input data.json --method efa --nfactors 5
#   Rscript deep_analytics.R --input data.json --method cca --set1 "hr_avg,hrv" --set2 "sleep_score,deep_sleep"
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
    input    = NULL,
    method   = "pca",
    nfactors = 5,
    set1     = NULL,
    set2     = NULL
  )
  i <- 1
  while (i <= length(args)) {
    key <- args[i]
    if (key == "--input"    && i < length(args)) { opts$input    <- args[i + 1]; i <- i + 2; next }
    if (key == "--method"   && i < length(args)) { opts$method   <- tolower(args[i + 1]); i <- i + 2; next }
    if (key == "--nfactors" && i < length(args)) { opts$nfactors <- as.integer(args[i + 1]); i <- i + 2; next }
    if (key == "--set1"     && i < length(args)) { opts$set1     <- args[i + 1]; i <- i + 2; next }
    if (key == "--set2"     && i < length(args)) { opts$set2     <- args[i + 1]; i <- i + 2; next }
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

# ── Dispatch ─────────────────────────────────────────────────────────
result <- switch(opts$method,
  "pca" = run_pca(df_scaled),
  "efa" = run_efa(df_scaled, opts$nfactors),
  "cca" = run_cca(df_num, opts$set1, opts$set2),
  {
    cat(toJSON(list(error = paste("Unknown method:", opts$method)), auto_unbox = TRUE))
    quit(status = 1)
  }
)

# ── Output ───────────────────────────────────────────────────────────
cat(toJSON(result, auto_unbox = TRUE, digits = 6, pretty = FALSE))

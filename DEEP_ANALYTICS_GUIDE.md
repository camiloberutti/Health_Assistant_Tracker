# Deep Analytics – User Guide

This page lets you run **six multivariate statistical methods** on your Garmin health data, directly from the browser. The analysis is powered by R scripts executed server-side; results are returned as interactive Plotly charts.

---

## Getting Started

1. Open the app and click **Deep Analytics** in the sidebar (or navigate to `/Analytics`).
2. Pick a **date range** — the last 90 days is pre-filled.
3. Click **Scan** (or wait — it auto-scans when dates change). This queries the database and shows which variables have enough data.
4. Select the variables you want to include.
5. Choose a **Method**, configure its options, and press **Run Analysis**.

---

## Data Density System

Before any analysis runs, the app checks how much data actually exists per variable in your selected date range.

| Badge colour     | Meaning                                      |
| ---------------- | -------------------------------------------- |
| 🟢 Green (≥ 70%)  | Strong coverage — safe to include            |
| 🟡 Yellow (< 70%) | Partial data — usable but may weaken results |
| 🔴 Red / Disabled | Below the slider threshold — excluded        |

**Density slider** — drag it to set the minimum population threshold (default 10%). Variables below this % are greyed out and cannot be selected.

> **Rule of thumb:** for reliable multivariate analysis, aim for variables with ≥ 70% density over at least 60 days.

---

## Available Methods

### 1. PCA – Principal Component Analysis

**What it does:** Reduces your many health variables into a few uncorrelated "principal components" that capture the most variance.

**When to use it:** You want to understand the main patterns in your data — e.g., "Is there a single dimension that separates hard training days from rest days?"

**Options:** None (fully automatic).

**Minimum:** 2 variables.

**Output:**
- **Eigenvalue table** — each PC with its eigenvalue, % variance, and cumulative %. PCs with eigenvalue ≥ 1 are highlighted (Kaiser rule).
- **Scree plot** — bar chart of eigenvalues + cumulative variance line. The "elbow" suggests how many PCs to keep.
- **Loadings heatmap** — shows how each original variable contributes to each PC. Red = positive loading, blue = negative.
- **3D score scatter** — every day plotted on PC1 × PC2 × PC3, coloured by k-means cluster.
- **2D cluster scatter** — PC1 vs PC2 with cluster colours.

**Interpretation example:**
> If PC1 has high positive loadings on Steps, ActiveCalories, Distance and high negative loading on TotalSleep, it represents an "Activity vs Rest" axis. Days with high PC1 scores were active; low PC1 scores were rest/recovery days.

---

### 2. EFA – Exploratory Factor Analysis

**What it does:** Discovers latent (hidden) factors behind your observed variables. Unlike PCA, EFA assumes that your variables are caused by underlying factors plus noise.

**When to use it:** You suspect your health metrics group into meaningful clusters — e.g., a "recovery" factor (sleep + HRV) vs a "training load" factor (calories + duration).

**Options:**
- **# Factors** — how many factors to extract (default 5). The script auto-caps this if it's too high for your data.

**Minimum:** 3 variables.

**Output:**
- **Suitability tests:**
  - *Bartlett's χ²* — tests if the correlation matrix is significantly different from identity. p < 0.05 = good.
  - *KMO* — Kaiser-Meyer-Olkin measure. > 0.6 = acceptable, > 0.8 = good.
- **Factor variance bar chart** — how much variance each factor explains.
- **Loadings heatmap** — which variables load onto which factors.
- **Communalities table** — h² (communality) = proportion of each variable's variance explained by the factors. Low h² means that variable is mostly noise.

**Interpretation example:**
> Factor 1 loads heavily on DeepSleep, TotalSleep, and RemSleep → label it "Sleep Quality". Factor 2 loads on Steps, Distance, ActiveCalories → label it "Daily Activity".

---

### 3. CCA – Canonical Correlation Analysis

**What it does:** Finds the maximum correlation between two groups of variables. You define Set 1 (e.g., activity metrics) and Set 2 (e.g., sleep/recovery metrics), and CCA finds the linear combinations of each set that are most correlated.

**When to use it:** You want to answer: "How strongly is my set of activity metrics related to my set of recovery metrics?"

**Options:**
- **Set 1 (Predictors)** — check the variables for the first group.
- **Set 2 (Outcomes)** — check the variables for the second group.

**Minimum:** 2+ variables in each set. Sets must not overlap.

**Output:**
- **Canonical correlations table** — each canonical dimension with its r and r².
- **X/Y coefficient bar charts** — which variables contribute most to each canonical variate.

**Interpretation example:**
> Canonical dimension 1 has r = 0.72. The X side weights ActivityDuration highly; the Y side weights TotalSleep and DeepSleep. This means longer training sessions are strongly associated with more (and deeper) sleep.

---

### 4. Clustering – K-Means & Hierarchical

**What it does:** Groups your training days into clusters of similar days. Runs both k-means and hierarchical clustering simultaneously.

**When to use it:** You want to discover distinct "day types" — e.g., hard training days, easy days, rest days, travel days.

**Options:**
- **# Clusters (K)** — how many groups (default 3). Use the elbow plot to find the best K.
- **Linkage** — hierarchical clustering method: Complete (default), Single, Average, or Ward's.

**Minimum:** 2 variables.

**Output:**
- **Quality metrics:**
  - *Hopkins statistic* — tests if data has clustering tendency at all. < 0.5 = clusterable.
  - *Silhouette average* — how well-separated clusters are. > 0.5 = good, > 0.7 = strong structure.
  - *Between SS / Total SS* — % of variance explained by clusters. Higher = better separation.
- **Elbow plot** — total within-cluster SS for K=1 to 10. The "elbow" suggests optimal K. Your chosen K is marked with a red dashed line.
- **Cluster scatter** — all days projected to 2D via PCA, coloured by cluster assignment.
- **Silhouette plot** — per-point silhouette width. Negative bars = possibly misclassified points.
- **Cluster centers table** — average (scaled) value of each variable per cluster.

**Interpretation example:**
> Cluster 1 (blue): high Steps + high ActiveCalories + moderate Sleep → "Active Recovery Days"
> Cluster 2 (red): very high ActivityDuration + low TotalSleep → "Hard Training Days"
> Cluster 3 (green): low everything → "Rest Days"

---

### 5. Regression – Multiple Linear

**What it does:** Models one variable (target) as a linear function of all others. Tells you which predictors significantly affect the target and by how much.

**When to use it:** You want to answer: "What predicts my sleep duration?" or "Do more steps lead to more calories burned, controlling for activity duration?"

**Options:**
- **Target (Y)** — the dependent variable to predict. The dropdown populates from your selected variables.

**Minimum:** 2 variables (1 target + at least 1 predictor).

**Output:**
- **Model summary:**
  - *R²* — proportion of target variance explained (0–1). > 0.3 = moderate, > 0.6 = strong.
  - *Adjusted R²* — R² penalized for number of predictors.
  - *F-statistic / p-value* — tests if the overall model is significant.
  - *Shapiro p-value* — tests if residuals are normally distributed (p > 0.05 = yes).
- **Coefficient bar chart** — β estimates coloured by significance (green = p < 0.05, orange = p < 0.1, red = not significant).
- **Coefficients table** — full detail: estimate, std error, t-value, p-value, significance stars.
- **Actual vs Fitted plot** — dots should fall near the red diagonal for a good model.
- **Residual histogram** — should look roughly bell-shaped for valid inference.

**Significance stars:**
| Symbol | Meaning         |
| ------ | --------------- |
| `***`  | p < 0.001       |
| `**`   | p < 0.01        |
| `*`    | p < 0.05        |
| `.`    | p < 0.1         |
| ` `    | Not significant |

**Interpretation example:**
> Target = TotalSleep. Coefficient for Steps = -0.42 (p = 0.003 **). This means each unit increase in (scaled) steps is associated with 0.42 units less (scaled) sleep, holding other variables constant.

---

### 6. MDS – Multidimensional Scaling

**What it does:** Computes pairwise distances between all your training days, then embeds them in 2D (or 3D) so that similar days appear close together on the map.

**When to use it:** You want a visual overview of how your training days relate to each other — without imposing any variable structure. Useful for spotting seasonal patterns, outlier days, or training phase transitions.

**Options:**
- **Dimensions** — 2D (default) or 3D.

**Minimum:** 3 variables (with enough observations).

**Output:**
- **Quality metrics:**
  - *Goodness of Fit (GoF)* — % of distance information preserved. > 80% = good.
  - *Stress (Kruskal)* — measures distortion. < 0.05 = excellent, < 0.10 = good, < 0.20 = fair.
- **Eigenvalue scree** — coloured bars showing which dimensions carry information.
- **Configuration map** — each dot is one day. Hover for the date. Nearby points had similar health metrics that day.

**Interpretation example:**
> A tight cluster in the bottom-left contains mostly weekdays in January with consistent step counts and sleep. A spread-out region on the right contains race weekends and holidays with unusual patterns.

---

## Tips

- **Start broad, then narrow.** Run PCA first with all available variables to see the big picture. Then use EFA or Clustering to dig deeper.
- **Check density before running.** If a key variable has < 50% data, results involving it will be unreliable.
- **Date range matters.** A 30-day window might not have enough variation. 90–365 days works best for most methods.
- **Method minimums.** PCA/Clustering/Regression/MDS need ≥ 2 variables. EFA needs ≥ 3. CCA needs ≥ 2 per set.
- **Imputation.** The R script fills sporadic missing values with the column median. Rows that are entirely empty are dropped. The "Missing Data Report" banner tells you exactly what happened.
- **Variables are scaled.** All methods operate on standardized (z-scored) data, so units don't matter — Steps (thousands) and SleepScore (0–100) are treated equally.

## Requirements

- **R 4.x** installed (detected automatically from `C:\Program Files\R\`)
- R packages: `jsonlite`, `psych`, `cluster` (installed once)
- Data in the Garmin database (synced via the app's import pipeline)

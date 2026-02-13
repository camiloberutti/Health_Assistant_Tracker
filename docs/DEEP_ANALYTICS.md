# Deep Analytics Module

The Deep Analytics module provides advanced statistical tools to explore longitudinal health and activity data. Unlike the standard dashboard which focuses on daily summaries and trends, this module applies multivariate statistical methods to uncover hidden patterns, relationships, and structures in the data.

This analysis is powered by **R (version 4.5.1)** running as a subprocess, utilizing libraries such as `jsonlite`, `psych`, and `cluster`.

## Scope

The scope of this module is **exploratory data analysis (EDA)** for N=1 (single-user) datasets. It allows the user to:
- Reduce data dimensionality to see global patterns.
- Identify latent constructs (underlying factors) driving daily metrics.
- Correlate sets of variables (e.g., Activity vs. Recovery).
- Group days into distinct "types" or clusters.
- Model relationships between a target variable and potential predictors.
- Visualize the similarity between different days.

## Methods Detail

The module supports six specific statistical methods, each tailored to a different analytical question:

### 1. Principal Component Analysis (PCA)
*   **Goal**: Dimensionality reduction. To condense many correlated variables (steps, calories, distance) into a few "Principal Components" that capture the most variance.
*   **Implementation**: Uses the standard `prcomp` function (SVD-based) on scaled data.
*   **Key Outputs**:
    *   **Eigenvalues/Scree Plot**: How much information each component retains.
    *   **Loadings Heatmap**: Which original variables contribute to each component.
    *   **Score Scatter Plot**: A visualization of days in the new component space (e.g., "Active Days" vs. "Rest Days").

### 2. Exploratory Factor Analysis (EFA)
*   **Goal**: Construct discovery. To identify latent (unobserved) variables that explain the correlation between observed variables.
*   **Implementation**: Uses `psych::fa` with varimax rotation (orthogonal) to produce interpretable factors.
*   **Key Outputs**:
    *   **Loadings**: The strength of the relationship between variables and factors.
    *   **Communality**: The proportion of each variable's variance explained by the factors.
    *   **KMO & Bartlett's Test**: Statistical tests to verify if the data is suitable for factor analysis.

### 3. Canonical Correlation Analysis (CCA)
*   **Goal**: Multi-set correlation. To find the relationship between two *sets* of variables (e.g., "Activity Metrics" vs. "Sleep Metrics"). It answers: "How does my physical activity profile relate to my sleep quality profile?"
*   **Implementation**: Uses standard Canonical Correlation techniques to find linear combinations of X and Y that maximize correlation.
*   **Key Outputs**:
    *   **Canonical Correlation Coefficients**: The strength of the relationship between the variates.
    *   **XY Plots**: Visualizing how the two sets relate in the optimized space.

### 4. Clustering (K-Means)
*   **Goal**: Classification. To group days into distinct clusters based on their similarity across all metrics.
*   **Implementation**: Uses `kmeans` algorithm. The optimal number of clusters is explored using the Elbow Method (Within-Cluster Sum of Squares).
*   **Key Outputs**:
    *   **Cluster Centers**: The "archetype" or average profile of each cluster (e.g., "High Stress/Low Sleep", "High Activity/Good Sleep").
    *   **Silhouette Plot**: A metric of how well-separated the clusters are.

### 5. Linear Regression
*   **Goal**: Prediction and Explanation. To model a single "Target" variable (e.g., Sleep Score) as a linear function of other "Predictor" variables.
*   **Implementation**: Uses `lm` (Linear Models). Includes automatic detection of "tautological" models (where predictors mathematically equal the target, e.g., Total Sleep ~ Deep + Light + REM).
*   **Key Outputs**:
    *   **Coefficients**: The estimated impact of each predictor on the target.
    *   **R-Squared**: How much of the variation in the target is explained by the model.
    *   **Residual Analysis**: Checking if the model's errors are random (normal).

### 6. Multidimensional Scaling (MDS)
*   **Goal**: Similarity visualization. To map days onto a 2D plane such that the distance between points represents their statistical dissimilarity.
*   **Implementation**: Uses Metric MDS (`cmdscale`) based on Euclidean distances between scaled daily vectors.
*   **Key Outputs**:
    *   **Configuration Map**: A scatter plot where days with similar stats are close together.
    *   **Stress**: A measure of how well the 2D map preserves the high-dimensional distances.

---

## Reflection: Validating N=1 Analytics

Performing these sophisticated statistical analyses on single-user data raises important methodological questions.

### 1. The "Quantified Self" vs. Population Statistics
Most statistical methods (like Regression and Factor Analysis) were developed for **nomothetic** science: finding general laws across large populations (N subjects). This project applies them in an **idiographic** context: understanding the unique laws of a single individual (N=1, T timepoints).
*   *Validation*: While the sample size is "1 person", the sample size for the model is "T days". With T > 100 days, we have sufficient statistical power to detect internal patterns, even if they don't generalize to other people.

### 2. The Independence Assumption (Autocorrelation)
Standard regression and PCA assume observations are independent. In longitudinal self-tracking data, today's values often depend on yesterday's (autocorrelation).
*   *Limitation*: This module currently treats days as independent independent observations (i.i.d.).
*   *Implication*: P-values and confidence intervals may be overly optimistic (too narrow). A "significant" relationship might partly be a byproduct of time trends (e.g., seasonality in running habits). **Users should interpret "significance" as a heuristic for importance, not a rigorous proof of causality.**

### 3. Causality vs. Correlation
Finding that "Activity Calories" correlates with "Sleep Score" (CCA/Regression) does not prove that running makes you sleep better. It could be that on days you feel well-rested, you run more (reverse causality), or a third factor (e.g., work stress) influences both.
*   *Usage*: These insights are hypothesis generators. They suggest *potential* levers for behavior change, which the user can then test experimentally (e.g., "I will try running more for 2 weeks and see if my sleep score actually improves").

### 4. Data Quality and Completeness
Real-world wearable data is noisy. Devices fall off, batteries die, and sensors have error margins.
*   *Handling*: The module includes robust data cleaning (filtering out missing values or low-quality days) before analysis. The "Data Density" indicator helps the user know if they have enough valid days for a reliable model.

### **Conclusion**
Deep Analytics in this context is a **mirror, not a microscope**. It doesn't find biological truths down to the molecule; it reflects back the user's own behavioral patterns in a structured way, allowing them to see the "shape" of their lifestyle that is invisible in day-to-day living.

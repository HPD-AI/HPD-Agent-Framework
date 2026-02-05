using System;
using System.Collections.Generic;

/// <summary>
/// Financial Analysis Skills - Strategic guides for balance sheet analysis
/// Skills teach Claude how to approach financial analysis tasks optimally
/// Each skill recommends best approaches while providing tactical alternatives
/// </summary>
public class FinancialAnalysisSkills
{
    /// <summary>
    /// Financial Health Dashboard Skill
    /// PRIMARY SKILL - Comprehensive balance sheet analysis
    /// </summary>
    [Skill]
    public Skill FinancialHealthDashboard(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "FinancialHealthDashboard",
            description: "Comprehensive balance sheet analysis covering liquidity, solvency, structure, and trends",
            functionResult: "Financial Health Dashboard activated. Ready to analyze balance sheets.",
            systemPrompt: @"
RECOMMENDED APPROACH:
For comprehensive multi-period balance sheet analysis, use a single call:

→ FinancialAnalysisToolkit.ComprehensiveBalanceSheetAnalysis(
    y1CurrentAssets, y1TotalAssets, y1CurrentLiabilities, y1TotalLiabilities, y1Equity,
    y2CurrentAssets, y2TotalAssets, y2CurrentLiabilities, y2TotalLiabilities, y2Equity)

Returns: Common-size analysis, period-over-period changes, top 3 changes by magnitude.

WHEN COMPREHENSIVE FUNCTION ISN'T ENOUGH:
If you need additional metrics not in ComprehensiveBalanceSheetAnalysis, supplement with:

Validate first (if needed):
→ ValidateBalanceSheetEquation(totalAssets, totalLiabilities, stockholdersEquity)

Additional liquidity metrics:
→ CalculateCurrentRatio(currentAssets, currentLiabilities)
→ CalculateQuickRatio(currentAssets, currentLiabilities, inventory)
→ CalculateWorkingCapital(currentAssets, currentLiabilities)

Additional leverage metrics:
→ CalculateDebtToEquityRatio(totalLiabilities, stockholdersEquity)
→ CalculateDebtToAssetsRatio(totalLiabilities, totalAssets)
→ CalculateEquityMultiplier(totalAssets, stockholdersEquity)

DECISION FRAMEWORK:
- User says 'analyze this balance sheet' → Use ComprehensiveBalanceSheetAnalysis + supplement if needed
- User requests specific metrics only → Use individual functions
- User needs validation → Start with ValidateBalanceSheetEquation

For interpretation guidance: read_skill_document('05-financialhealthdashboard-sop')",
            options: new SkillOptions()
                .AddDocumentFromFile(
                    "./Skills/SOPs/05-FinancialHealthDashboard-SOP.md",
                    "Interpretation framework and benchmarks"),
            "FinancialAnalysisToolkit.ComprehensiveBalanceSheetAnalysis",
            "FinancialAnalysisToolkit.ValidateBalanceSheetEquation",
            "FinancialAnalysisToolkit.CalculateCurrentRatio",
            "FinancialAnalysisToolkit.CalculateQuickRatio",
            "FinancialAnalysisToolkit.CalculateWorkingCapital",
            "FinancialAnalysisToolkit.CalculateDebtToEquityRatio",
            "FinancialAnalysisToolkit.CalculateDebtToAssetsRatio",
            "FinancialAnalysisToolkit.CalculateEquityMultiplier",
            "FinancialAnalysisToolkit.CommonSizeBalanceSheetAssets",
            "FinancialAnalysisToolkit.CommonSizeBalanceSheetLiabilities",
            "FinancialAnalysisToolkit.EquityToTotalAssetsPercentage",
            "FinancialAnalysisToolkit.CalculateAbsoluteChange",
            "FinancialAnalysisToolkit.CalculatePercentageChange",
            "FinancialAnalysisToolkit.CalculatePercentagePointChange"
        );
    }

    /// <summary>
    /// Quick Liquidity Analysis Skill
    /// Use when the task specifically requires ONLY liquidity assessment
    /// </summary>
    [Skill]
    public Skill QuickLiquidityAnalysis(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "QuickLiquidityAnalysis",
            description: "Focused liquidity assessment - current ratio, quick ratio, working capital",
            functionResult: "Quick Liquidity Analysis activated.",
            systemPrompt: @"
WHEN TO USE THIS SKILL:
Use when the user specifically requests liquidity analysis, current ratio, quick ratio, or working capital.
For comprehensive analysis, use FinancialHealthDashboard instead.

APPROACH:
Execute these functions (can be parallel):

→ FinancialAnalysisToolkit.CalculateCurrentRatio(currentAssets, currentLiabilities)
  Healthy benchmark: >1.5 for most industries

→ FinancialAnalysisToolkit.CalculateQuickRatio(currentAssets, currentLiabilities, inventory)
  Conservative benchmark: >1.0

→ FinancialAnalysisToolkit.CalculateWorkingCapital(currentAssets, currentLiabilities)
  Positive = liquidity cushion; Negative may indicate efficiency or stress

SYNTHESIS:
After calculations, assess: Can the company meet short-term obligations? Compare to industry norms.

For benchmarks: read_skill_document('01-quickliquidityanalysis-sop')",
            options: new SkillOptions()
                .AddDocumentFromFile(
                    "./Skills/SOPs/01-QuickLiquidityAnalysis-SOP.md",
                    "Liquidity benchmarks and interpretation"),
            "FinancialAnalysisToolkit.CalculateCurrentRatio",
            "FinancialAnalysisToolkit.CalculateQuickRatio",
            "FinancialAnalysisToolkit.CalculateWorkingCapital"
        );
    }

    /// <summary>
    /// Capital Structure Analysis Skill
    /// Use when the task specifically requires ONLY leverage/capital structure assessment
    /// </summary>
    [Skill]
    public Skill CapitalStructureAnalysis(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "CapitalStructureAnalysis",
            description: "Focused capital structure assessment - debt ratios and financial leverage",
            functionResult: "Capital Structure Analysis activated.",
            systemPrompt: @"
WHEN TO USE THIS SKILL:
Use when the user specifically requests leverage analysis, debt ratios, or capital structure.
For comprehensive analysis, use FinancialHealthDashboard instead.

APPROACH:
Execute these functions (can be parallel):

→ FinancialAnalysisToolkit.CalculateDebtToEquityRatio(totalLiabilities, stockholdersEquity)
  >1.0 = more debt than equity (varies by industry)

→ FinancialAnalysisToolkit.CalculateDebtToAssetsRatio(totalLiabilities, totalAssets)
  Shows % of assets financed by debt

→ FinancialAnalysisToolkit.CalculateEquityMultiplier(totalAssets, stockholdersEquity)
  Used in DuPont analysis; measures leverage

→ FinancialAnalysisToolkit.EquityToTotalAssetsPercentage(stockholdersEquity, totalAssets)
  Higher % = more conservative capital structure

SYNTHESIS:
After calculations, assess: Is leverage appropriate for the industry? What's the financial risk?

For benchmarks: read_skill_document('02-capitalstructureanalysis-sop')",
            options: new SkillOptions()
                .AddDocumentFromFile(
                    "./Skills/SOPs/02-CapitalStructureAnalysis-SOP.md",
                    "Leverage benchmarks and risk assessment"),
            "FinancialAnalysisToolkit.CalculateDebtToEquityRatio",
            "FinancialAnalysisToolkit.CalculateDebtToAssetsRatio",
            "FinancialAnalysisToolkit.CalculateEquityMultiplier",
            "FinancialAnalysisToolkit.EquityToTotalAssetsPercentage"
        );
    }

    /// <summary>
    /// Period Change Analysis Skill
    /// Use when analyzing period-over-period changes in financial metrics
    /// </summary>
    [Skill]
    public Skill PeriodChangeAnalysis(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "PeriodChangeAnalysis",
            description: "Period-over-period change analysis using absolute and relative measures",
            functionResult: "Period Change Analysis activated.",
            systemPrompt: @"
WHEN TO USE THIS SKILL:
Use when the user asks about changes, trends, or period-over-period analysis.
For comprehensive analysis, use FinancialHealthDashboard instead.

APPROACH:
For each line item you're analyzing, use these functions (can be parallel):

→ FinancialAnalysisToolkit.CalculateAbsoluteChange(currentPeriodValue, priorPeriodValue)
  Shows raw dollar impact

→ FinancialAnalysisToolkit.CalculatePercentageChange(currentPeriodValue, priorPeriodValue)
  Shows growth rate (relative magnitude)

→ FinancialAnalysisToolkit.CalculatePercentagePointChange(currentPercent, priorPercent)
  Shows change in composition (use with common-size percentages)

WHEN TO USE EACH:
- Absolute Change: Understanding total dollar impact
- Percentage Change: Comparing growth rates of different-sized items
- Percentage Point Change: Analyzing structural shifts in balance sheet

SYNTHESIS:
After calculations, explain: What changed? Favorable or concerning? Business drivers?

For examples: read_skill_document('03-periodchangeanalysis-sop')",
            options: new SkillOptions()
                .AddDocumentFromFile(
                    "./Skills/SOPs/03-PeriodChangeAnalysis-SOP.md",
                    "Change analysis examples and interpretation"),
            "FinancialAnalysisToolkit.CalculateAbsoluteChange",
            "FinancialAnalysisToolkit.CalculatePercentageChange",
            "FinancialAnalysisToolkit.CalculatePercentagePointChange"
        );
    }

    /// <summary>
    /// Common-Size Balance Sheet Skill
    /// Use for normalizing balance sheets to percentages for comparison
    /// </summary>
    [Skill]
    public Skill CommonSizeBalanceSheet(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "CommonSizeBalanceSheet",
            description: "Common-size balance sheet - express items as percentages for comparison",
            functionResult: "Common-Size Balance Sheet analysis activated.",
            systemPrompt: @"
WHEN TO USE THIS SKILL:
Use when the user requests common-size analysis, percentage breakdowns, or structural composition.
For comprehensive analysis, use FinancialHealthDashboard instead.

APPROACH:
Execute these functions (can be parallel):

→ FinancialAnalysisToolkit.CommonSizeBalanceSheetAssets(currentAssets, totalAssets)
  Returns: Current Assets % | Non-Current Assets % | Total: 100%

→ FinancialAnalysisToolkit.CommonSizeBalanceSheetLiabilities(currentLiabilities, totalLiabilities)
  Returns: Current Liabilities % | Non-Current Liabilities % | Total: 100%

→ FinancialAnalysisToolkit.EquityToTotalAssetsPercentage(stockholdersEquity, totalAssets)
  Returns: Equity as % of total assets

BENEFITS:
- Compare companies of different sizes
- Identify structural changes over time
- Spot unusual concentrations

SYNTHESIS:
After calculations: Verify percentages sum to 100%, identify unusual patterns, compare to benchmarks.

For interpretation: read_skill_document('04-commonsizebalancesheet-sop')",
            options: new SkillOptions()
                .AddDocumentFromFile(
                    "./Skills/SOPs/04-CommonSizeBalanceSheet-SOP.md",
                    "Common-size interpretation and comparisons"),
            "FinancialAnalysisToolkit.CalculateCommonSizePercentage",
            "FinancialAnalysisToolkit.CommonSizeBalanceSheetAssets",
            "FinancialAnalysisToolkit.CommonSizeBalanceSheetLiabilities",
            "FinancialAnalysisToolkit.EquityToTotalAssetsPercentage"
        );
    }

    /// <summary>
    /// URL Document Test Skill - Tests URL document support
    /// </summary>
    [Skill]
    public Skill UrlDocumentTest(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "UrlDocumentTest",
            description: "Test skill demonstrating URL document support",
            functionResult: "URL Document Test skill activated.",
            systemPrompt: @"
This skill demonstrates the new URL document feature.
The documentation is loaded from a URL instead of a local file.

Check the available documents to see URL-based documents marked with 🔗 URL indicator.",
            options: new SkillOptions()
                .AddDocumentFromUrl(
                    "https://raw.githubusercontent.com/microsoft/semantic-kernel/main/README.md",
                    "Semantic Kernel README for reference",
                    documentId: "semantic-kernel-readme"),
            "FinancialAnalysisToolkit.CalculateCurrentRatio"
        );
    }
}

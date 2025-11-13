using HPD_Agent.Skills;
using System;
using System.Collections.Generic;

namespace AgentConsoleTest.Skills;

/// <summary>
/// Financial Analysis Skills - Orchestrates FinancialAnalysisPlugin functions into semantic workflows
/// Each skill groups related functions that are consistently used together
/// SOPs are documented in Skills/SOPs/ directory
/// </summary>

public class FinancialAnalysisSkills
{
    /// <summary>
    /// Quick Liquidity Analysis Skill
    /// Analyzes a company's ability to meet short-term obligations
    /// </summary>
    [Skill(Category = "Liquidity Analysis", Priority = 10)]
    public Skill QuickLiquidityAnalysis(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "QuickLiquidityAnalysis",
            description: "Analyze company's short-term liquidity position using current ratio, quick ratio, and working capital",
            instructions: @"
Use this skill to assess whether a company can pay its short-term obligations.

Steps:
1. Calculate Current Ratio (Current Assets / Current Liabilities)
2. Calculate Quick Ratio (Quick Assets / Current Liabilities)  
3. Calculate Working Capital (Current Assets - Current Liabilities)

Interpretation:
- Current Ratio: >1.5 is generally healthy
- Quick Ratio: >1.0 is conservative
- Working Capital: Positive indicates liquidity cushion

See SOP documentation for detailed step-by-step instructions.",
            options: new SkillOptions()
                .AddDocumentFromFile(
                    "./Skills/SOPs/01-QuickLiquidityAnalysis-SOP.md",
                    "Step-by-step procedure for analyzing liquidity ratios"),
            "FinancialAnalysisPlugin.CalculateCurrentRatio",
            "FinancialAnalysisPlugin.CalculateQuickRatio",
            "FinancialAnalysisPlugin.CalculateWorkingCapital"
        );
    }

    /// <summary>
    /// Capital Structure Analysis Skill
    /// Analyzes how the company finances itself (debt vs equity mix)
    /// </summary>
    [Skill(Category = "Leverage Analysis", Priority = 11)]
    public Skill CapitalStructureAnalysis(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "CapitalStructureAnalysis",
            description: "Analyze company's capital structure and financial leverage through debt and equity ratios",
            instructions: @"
Use this skill to understand how the company is financed and assess financial risk.

Steps:
1. Calculate Debt-to-Equity Ratio (Total Debt / Total Equity)
2. Calculate Debt-to-Assets Ratio (Total Debt / Total Assets)
3. Calculate Equity Multiplier (Total Assets / Total Equity)
4. Calculate Equity to Total Assets % (Equity / Assets × 100)

Interpretation:
- D/E: >1.0 means more debt than equity (higher leverage)
- D/A: Shows what % of assets are financed by debt
- Equity Multiplier: Part of DuPont analysis
- Equity %: Conservative companies have higher equity %

See SOP documentation for detailed analysis framework.",
            options: new SkillOptions()
                .AddDocumentFromFile(
                    "./Skills/SOPs/02-CapitalStructureAnalysis-SOP.md",
                    "Step-by-step procedure for analyzing capital structure and leverage"),
            "FinancialAnalysisPlugin.CalculateDebtToEquityRatio",
            "FinancialAnalysisPlugin.CalculateDebtToAssetsRatio",
            "FinancialAnalysisPlugin.CalculateEquityMultiplier",
            "FinancialAnalysisPlugin.EquityToTotalAssetsPercentage"
        );
    }

    /// <summary>
    /// Period Change Analysis Skill
    /// Analyzes how financial metrics changed from one period to another
    /// </summary>
    [Skill(Category = "Trend Analysis", Priority = 12)]
    public Skill PeriodChangeAnalysis(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "PeriodChangeAnalysis",
            description: "Analyze period-over-period changes in financial metrics using absolute and relative measures",
            instructions: @"
Use this skill to understand how financial items changed between periods.

Steps:
1. Calculate Absolute Change (Current Period Value - Prior Period Value)
   - Shows raw dollar impact
   
2. Calculate Percentage Change ((Change / Prior Value) × 100)
   - Shows relative magnitude of change
   
3. Calculate Percentage Point Change (Current % - Prior %)
   - Shows change in common-size percentages

When to use each:
- Absolute Change: Total dollar impact on balance sheet
- Percentage Change: Growth rate and relative magnitude
- Percentage Point Change: How much share of total changed

See SOP documentation for examples and interpretation guidelines.",
            options: new SkillOptions()
                .AddDocumentFromFile(
                    "./Skills/SOPs/03-PeriodChangeAnalysis-SOP.md",
                    "Step-by-step procedure for analyzing period-over-period changes"),
            "FinancialAnalysisPlugin.CalculateAbsoluteChange",
            "FinancialAnalysisPlugin.CalculatePercentageChange",
            "FinancialAnalysisPlugin.CalculatePercentagePointChange"
        );
    }

    /// <summary>
    /// Common-Size Balance Sheet Skill
    /// Normalizes balance sheet items to percentages for comparison
    /// </summary>
    [Skill(Category = "Comparative Analysis", Priority = 13)]
    public Skill CommonSizeBalanceSheet(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "CommonSizeBalanceSheet",
            description: "Create a common-size balance sheet showing each item as a percentage of total assets",
            instructions: @"
Use this skill to build a common-size balance sheet for comparison across periods or companies.

Steps:
1. Calculate asset percentages (Each Asset / Total Assets × 100)
   - Use CommonSizeBalanceSheetAssets() for breakdown
   
2. Calculate liability percentages (Each Liability / Total Liabilities × 100)
   - Use CommonSizeBalanceSheetLiabilities() for breakdown
   
3. Calculate equity percentage (Total Equity / Total Assets × 100)
   - Shows capital structure
   
4. Verify: All percentages should sum to 100%

Benefits:
- Compare companies of different sizes
- Identify structural changes over time
- Spot unusual asset/liability distributions

See SOP documentation for interpretation and examples.",
            options: new SkillOptions()
                .AddDocumentFromFile(
                    "./Skills/SOPs/04-CommonSizeBalanceSheet-SOP.md",
                    "Step-by-step procedure for creating common-size financial statements"),
            "FinancialAnalysisPlugin.CalculateCommonSizePercentage",
            "FinancialAnalysisPlugin.CommonSizeBalanceSheetAssets",
            "FinancialAnalysisPlugin.CommonSizeBalanceSheetLiabilities",
            "FinancialAnalysisPlugin.EquityToTotalAssetsPercentage"
        );
    }

    /// <summary>
    /// Financial Health Dashboard Skill
    /// Comprehensive financial analysis combining all analysis techniques
    /// </summary>
    [Skill(Category = "Executive Summary", Priority = 1)]
    public Skill FinancialHealthDashboard(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "FinancialHealthDashboard",
            description: "Comprehensive financial health assessment combining liquidity, leverage, common-size, and change analysis",
            instructions: @"
Use this skill for a complete financial health assessment.

Process:
1. VALIDATE: Verify Balance Sheet Equation (Assets = Liabilities + Equity)
   - If invalid, investigate data issues first
   
2. LIQUIDITY: Calculate current ratio, quick ratio, and working capital
   - Can company meet short-term obligations?
   
3. LEVERAGE: Calculate debt-to-equity, debt-to-assets, and equity multiplier
   - What's the debt/equity mix? Is it sustainable?
   
4. STRUCTURE: Create common-size balance sheet analysis
   - How is the balance sheet distributed?
   - Are there unusual concentrations?
   
5. TRENDS: Calculate absolute and percentage changes
   - How did metrics change from prior period?
   - Are changes favorable or concerning?

Output: Complete financial health snapshot
- Liquidity assessment
- Solvency assessment  
- Structural analysis
- Trend analysis
- Overall risk rating

See SOP documentation for complete analysis framework and interpretation.",
            options: new SkillOptions()
                .AddDocumentFromFile(
                    "./Skills/SOPs/05-FinancialHealthDashboard-SOP.md",
                    "Complete framework for comprehensive financial analysis")
                .AddDocumentFromFile(
                    "./Skills/SOPs/00-AnalysisFramework-Overview.md",
                    "Overview of all financial analysis skills and when to use them"),
            "FinancialAnalysisPlugin.ValidateBalanceSheetEquation",
            "FinancialAnalysisPlugin.CalculateCurrentRatio",
            "FinancialAnalysisPlugin.CalculateQuickRatio",
            "FinancialAnalysisPlugin.CalculateWorkingCapital",
            "FinancialAnalysisPlugin.CalculateDebtToEquityRatio",
            "FinancialAnalysisPlugin.CalculateDebtToAssetsRatio",
            "FinancialAnalysisPlugin.CalculateEquityMultiplier",
            "FinancialAnalysisPlugin.CommonSizeBalanceSheetAssets",
            "FinancialAnalysisPlugin.CommonSizeBalanceSheetLiabilities",
            "FinancialAnalysisPlugin.CalculateAbsoluteChange",
            "FinancialAnalysisPlugin.CalculatePercentageChange",
            "FinancialAnalysisPlugin.CalculatePercentagePointChange"
        );
    }
}

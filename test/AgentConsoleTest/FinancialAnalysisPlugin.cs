using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;

/// <summary>
/// Advanced financial analysis plugin for balance sheet analysis, common-sizing, and ratio calculations.
/// Designed to solve textbook problems and real-world financial statement analysis.
/// </summary>

public class FinancialAnalysisPluginMetadataContext : IPluginMetadata
{
    private readonly Dictionary<string, object> _properties = new();

    public FinancialAnalysisPluginMetadataContext(
        string currency = "USD",
        int decimalPlaces = 2,
        bool allowNegativeEquity = true,
        bool includePercentageSign = true)
    {
        _properties["currency"] = currency;
        _properties["decimalPlaces"] = decimalPlaces;
        _properties["allowNegativeEquity"] = allowNegativeEquity;
        _properties["includePercentageSign"] = includePercentageSign;
        
        Currency = currency;
        DecimalPlaces = decimalPlaces;
        AllowNegativeEquity = allowNegativeEquity;
        IncludePercentageSign = includePercentageSign;
    }

    public string Currency { get; }
    public int DecimalPlaces { get; }
    public bool AllowNegativeEquity { get; }
    public bool IncludePercentageSign { get; }

    public T? GetProperty<T>(string propertyName, T? defaultValue = default)
    {
        if (_properties.TryGetValue(propertyName, out var value))
        {
            if (value is T typedValue)
                return typedValue;
            if (typeof(T) == typeof(string))
                return (T)(object)value.ToString()!;
        }
        return defaultValue;
    }

    public bool HasProperty(string propertyName) => _properties.ContainsKey(propertyName);
    public IEnumerable<string> GetPropertyNames() => _properties.Keys;
}


[Collapse(
    description: "Financial Analysis Plugin",
    FunctionResult: @"Financial Analysis Plugin activated.

Available capabilities:
• Common-size analysis (balance sheet ratios)
• Liquidity ratios (current, quick, working capital)
• Leverage ratios (debt-to-equity, debt-to-assets, equity multiplier)
• Period-over-period change analysis
• Balance sheet equation validation
• Comprehensive multi-period analysis",
   SystemPrompt: @"# FINANCIAL ANALYSIS RULES

## Core Principles
- ALWAYS validate the accounting equation: Assets = Liabilities + Equity
- ALWAYS use consistent decimal places (default: 2 for ratios, 0 for dollar amounts)
- ALWAYS show calculations step-by-step for transparency
- NEVER mix different reporting periods in the same calculation

## Common-Size Analysis Protocol
1. Calculate each line item as % of base amount (Total Assets or Total Liabilities)
2. Verify percentages sum to 100% within category
3. Compare period-over-period using percentage POINT changes (not percentage changes)
4. Identify top 3 changes by magnitude for executive summary

## Ratio Interpretation Guidelines
- Current Ratio > 2.0: Strong liquidity
- Current Ratio 1.0-2.0: Adequate liquidity
- Current Ratio < 1.0: Liquidity concerns
- Quick Ratio > 1.0: Good short-term solvency
- Debt-to-Equity > 2.0: High financial leverage/risk

## Error Prevention
- Check for zero denominators BEFORE calculation
- Round final results only (maintain precision in intermediate steps)
- Express percentage point changes with sign (+/-)
- Format dollar amounts with millions suffix when appropriate")]
public class FinancialAnalysisPlugin
{
    // ==================== COMMON-SIZE ANALYSIS ====================

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Calculates common-size percentage: (line item / base amount) * 100. Essential for Question 11 type analysis.")]
    public string CalculateCommonSizePercentage(
        [AIDescription("The line item amount (e.g., Current Assets = 12313)")] decimal lineItemAmount,
        [AIDescription("The base amount (e.g., Total Assets = 16778)")] decimal baseAmount,
        [AIDescription("Number of decimal places (default: 2)")] int decimalPlaces = 2)
    {
        if (baseAmount == 0)
            return "ERROR: Base amount cannot be zero";

        decimal percentage = (lineItemAmount / baseAmount) * 100;
        return $"{Math.Round(percentage, decimalPlaces)}%";
    }

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Calculates the percentage point change between two periods (Period2% - Period1%). Perfect for analyzing trends.")]
    public string CalculatePercentagePointChange(
        [AIDescription("Percentage in first period (e.g., 73.39 for 73.39%)")] decimal period1Percentage,
        [AIDescription("Percentage in second period (e.g., 68.67 for 68.67%)")] decimal period2Percentage,
        [AIDescription("Number of decimal places (default: 2)")] int decimalPlaces = 2)
    {
        decimal change = period2Percentage - period1Percentage;
        string sign = change >= 0 ? "+" : "";
        return $"{sign}{Math.Round(change, decimalPlaces)} percentage points";
    }

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Performs complete common-size balance sheet analysis for assets. Returns formatted table data.")]
    public string CommonSizeBalanceSheetAssets(
        [AIDescription("Current assets amount")] decimal currentAssets,
        [AIDescription("Total assets amount")] decimal totalAssets,
        [AIDescription("Decimal places (default: 2)")] int decimalPlaces = 2)
    {
        if (totalAssets == 0)
            return "ERROR: Total assets cannot be zero";

        decimal currentAssetsPct = (currentAssets / totalAssets) * 100;
        decimal nonCurrentAssets = totalAssets - currentAssets;
        decimal nonCurrentAssetsPct = (nonCurrentAssets / totalAssets) * 100;

        return $"Current Assets: {Math.Round(currentAssetsPct, decimalPlaces)}% | " +
               $"Non-Current Assets: {Math.Round(nonCurrentAssetsPct, decimalPlaces)}% | " +
               $"Total: 100.00%";
    }

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Performs complete common-size balance sheet analysis for liabilities. Returns formatted table data.")]
    public string CommonSizeBalanceSheetLiabilities(
        [AIDescription("Current liabilities amount")] decimal currentLiabilities,
        [AIDescription("Total liabilities amount")] decimal totalLiabilities,
        [AIDescription("Decimal places (default: 2)")] int decimalPlaces = 2)
    {
        if (totalLiabilities == 0)
            return "ERROR: Total liabilities cannot be zero";

        decimal currentLiabilitiesPct = (currentLiabilities / totalLiabilities) * 100;
        decimal nonCurrentLiabilities = totalLiabilities - currentLiabilities;
        decimal nonCurrentLiabilitiesPct = (nonCurrentLiabilities / totalLiabilities) * 100;

        return $"Current Liabilities: {Math.Round(currentLiabilitiesPct, decimalPlaces)}% | " +
               $"Non-Current Liabilities: {Math.Round(nonCurrentLiabilitiesPct, decimalPlaces)}% | " +
               $"Total: 100.00%";
    }

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Calculates stockholders' equity as percentage of total assets (capital structure analysis).")]
    public string EquityToTotalAssetsPercentage(
        [AIDescription("Stockholders' equity amount")] decimal stockholdersEquity,
        [AIDescription("Total assets amount")] decimal totalAssets,
        [AIDescription("Decimal places (default: 2)")] int decimalPlaces = 2)
    {
        if (totalAssets == 0)
            return "ERROR: Total assets cannot be zero";

        decimal percentage = (stockholdersEquity / totalAssets) * 100;
        return $"{Math.Round(percentage, decimalPlaces)}%";
    }

    // ==================== LIQUIDITY RATIOS ====================

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Calculates current ratio: Current Assets / Current Liabilities. Measures short-term liquidity.")]
    public string CalculateCurrentRatio(
        [AIDescription("Current assets amount")] decimal currentAssets,
        [AIDescription("Current liabilities amount")] decimal currentLiabilities,
        [AIDescription("Decimal places (default: 2)")] int decimalPlaces = 2)
    {
        if (currentLiabilities == 0)
            return "ERROR: Current liabilities cannot be zero";

        decimal ratio = currentAssets / currentLiabilities;
        return $"{Math.Round(ratio, decimalPlaces):F2}";
    }

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Calculates quick ratio (acid-test): (Current Assets - Inventory) / Current Liabilities.")]
    public string CalculateQuickRatio(
        [AIDescription("Current assets amount")] decimal currentAssets,
        [AIDescription("Inventory amount")] decimal inventory,
        [AIDescription("Current liabilities amount")] decimal currentLiabilities,
        [AIDescription("Decimal places (default: 2)")] int decimalPlaces = 2)
    {
        if (currentLiabilities == 0)
            return "ERROR: Current liabilities cannot be zero";

        decimal quickAssets = currentAssets - inventory;
        decimal ratio = quickAssets / currentLiabilities;
        return $"{Math.Round(ratio, decimalPlaces):F2}";
    }

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Calculates working capital: Current Assets - Current Liabilities. Returns amount in millions.")]
    public string CalculateWorkingCapital(
        [AIDescription("Current assets amount (in millions)")] decimal currentAssets,
        [AIDescription("Current liabilities amount (in millions)")] decimal currentLiabilities,
        [AIDescription("Decimal places (default: 0)")] int decimalPlaces = 0)
    {
        decimal workingCapital = currentAssets - currentLiabilities;
        string sign = workingCapital >= 0 ? "$" : "-$";
        return $"{sign}{Math.Abs(Math.Round(workingCapital, decimalPlaces))} million";
    }

    // ==================== LEVERAGE RATIOS ====================

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Calculates debt-to-equity ratio: Total Liabilities / Stockholders' Equity. Measures financial leverage.")]
    public string CalculateDebtToEquityRatio(
        [AIDescription("Total liabilities amount")] decimal totalLiabilities,
        [AIDescription("Stockholders' equity amount")] decimal stockholdersEquity,
        [AIDescription("Decimal places (default: 2)")] int decimalPlaces = 2)
    {
        if (stockholdersEquity == 0)
            return "ERROR: Stockholders' equity cannot be zero";

        decimal ratio = totalLiabilities / stockholdersEquity;
        return $"{Math.Round(ratio, decimalPlaces):F2}";
    }

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Calculates debt-to-assets ratio: Total Liabilities / Total Assets. Shows proportion of assets financed by debt.")]
    public string CalculateDebtToAssetsRatio(
        [AIDescription("Total liabilities amount")] decimal totalLiabilities,
        [AIDescription("Total assets amount")] decimal totalAssets,
        [AIDescription("Decimal places (default: 2)")] int decimalPlaces = 2)
    {
        if (totalAssets == 0)
            return "ERROR: Total assets cannot be zero";

        decimal ratio = totalLiabilities / totalAssets;
        return $"{Math.Round(ratio, decimalPlaces):F2} or {Math.Round(ratio * 100, decimalPlaces)}%";
    }

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Calculates equity multiplier: Total Assets / Stockholders' Equity. Part of DuPont analysis.")]
    public string CalculateEquityMultiplier(
        [AIDescription("Total assets amount")] decimal totalAssets,
        [AIDescription("Stockholders' equity amount")] decimal stockholdersEquity,
        [AIDescription("Decimal places (default: 2)")] int decimalPlaces = 2)
    {
        if (stockholdersEquity == 0)
            return "ERROR: Stockholders' equity cannot be zero";

        decimal ratio = totalAssets / stockholdersEquity;
        return $"{Math.Round(ratio, decimalPlaces):F2}";
    }

    // ==================== CHANGE ANALYSIS ====================

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Calculates absolute dollar change between two periods: Period2 - Period1.")]
    public string CalculateAbsoluteChange(
        [AIDescription("Amount in first period")] decimal period1Amount,
        [AIDescription("Amount in second period")] decimal period2Amount,
        [AIDescription("Decimal places (default: 0)")] int decimalPlaces = 0)
    {
        decimal change = period2Amount - period1Amount;
        string sign = change >= 0 ? "+$" : "-$";
        return $"{sign}{Math.Abs(Math.Round(change, decimalPlaces))} million";
    }

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Calculates percentage change between two periods: ((Period2 - Period1) / Period1) * 100.")]
    public string CalculatePercentageChange(
        [AIDescription("Amount in first period")] decimal period1Amount,
        [AIDescription("Amount in second period")] decimal period2Amount,
        [AIDescription("Decimal places (default: 2)")] int decimalPlaces = 2)
    {
        if (period1Amount == 0)
            return "ERROR: Period 1 amount cannot be zero";

        decimal percentChange = ((period2Amount - period1Amount) / Math.Abs(period1Amount)) * 100;
        string sign = percentChange >= 0 ? "+" : "";
        return $"{sign}{Math.Round(percentChange, decimalPlaces)}%";
    }

    // ==================== BALANCE SHEET EQUATION VALIDATION ====================

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Validates the fundamental accounting equation: Assets = Liabilities + Equity. Returns true/false with details.")]
    public string ValidateBalanceSheetEquation(
        [AIDescription("Total assets amount")] decimal totalAssets,
        [AIDescription("Total liabilities amount")] decimal totalLiabilities,
        [AIDescription("Stockholders' equity amount")] decimal stockholdersEquity,
        [AIDescription("Acceptable tolerance (default: 0.01)")] decimal tolerance = 0.01m)
    {
        decimal liabilitiesPlusEquity = totalLiabilities + stockholdersEquity;
        decimal difference = Math.Abs(totalAssets - liabilitiesPlusEquity);

        if (difference <= tolerance)
            return $"✅ BALANCED: Assets ({totalAssets:F2}) = Liabilities ({totalLiabilities:F2}) + Equity ({stockholdersEquity:F2})";
        else
            return $"❌ UNBALANCED: Assets ({totalAssets:F2}) ≠ Liabilities + Equity ({liabilitiesPlusEquity:F2}). Difference: {difference:F2}";
    }

    // ==================== COMPREHENSIVE ANALYSIS ====================

    [AIFunction<FinancialAnalysisPluginMetadataContext>]
    [AIDescription("Performs comprehensive balance sheet analysis for Question 11 & 12 style problems. Returns all key metrics.")]
    public string ComprehensiveBalanceSheetAnalysis(
        [AIDescription("Year 1 Current Assets")] decimal y1CurrentAssets,
        [AIDescription("Year 1 Total Assets")] decimal y1TotalAssets,
        [AIDescription("Year 1 Current Liabilities")] decimal y1CurrentLiabilities,
        [AIDescription("Year 1 Total Liabilities")] decimal y1TotalLiabilities,
        [AIDescription("Year 1 Stockholders' Equity")] decimal y1Equity,
        [AIDescription("Year 2 Current Assets")] decimal y2CurrentAssets,
        [AIDescription("Year 2 Total Assets")] decimal y2TotalAssets,
        [AIDescription("Year 2 Current Liabilities")] decimal y2CurrentLiabilities,
        [AIDescription("Year 2 Total Liabilities")] decimal y2TotalLiabilities,
        [AIDescription("Year 2 Stockholders' Equity")] decimal y2Equity)
    {
        // Common-size calculations
        decimal y1CurrentAssetsPct = (y1CurrentAssets / y1TotalAssets) * 100;
        decimal y2CurrentAssetsPct = (y2CurrentAssets / y2TotalAssets) * 100;
        decimal currentAssetsChange = y2CurrentAssetsPct - y1CurrentAssetsPct;

        decimal y1NonCurrentAssetsPct = ((y1TotalAssets - y1CurrentAssets) / y1TotalAssets) * 100;
        decimal y2NonCurrentAssetsPct = ((y2TotalAssets - y2CurrentAssets) / y2TotalAssets) * 100;
        decimal nonCurrentAssetsChange = y2NonCurrentAssetsPct - y1NonCurrentAssetsPct;

        decimal y1CurrentLiabPct = (y1CurrentLiabilities / y1TotalLiabilities) * 100;
        decimal y2CurrentLiabPct = (y2CurrentLiabilities / y2TotalLiabilities) * 100;
        decimal currentLiabChange = y2CurrentLiabPct - y1CurrentLiabPct;

        decimal y1NonCurrentLiabPct = ((y1TotalLiabilities - y1CurrentLiabilities) / y1TotalLiabilities) * 100;
        decimal y2NonCurrentLiabPct = ((y2TotalLiabilities - y2CurrentLiabilities) / y2TotalLiabilities) * 100;
        decimal nonCurrentLiabChange = y2NonCurrentLiabPct - y1NonCurrentLiabPct;

        decimal y1EquityPct = (y1Equity / y1TotalAssets) * 100;
        decimal y2EquityPct = (y2Equity / y2TotalAssets) * 100;
        decimal equityChange = y2EquityPct - y1EquityPct;

        return $"COMPREHENSIVE BALANCE SHEET ANALYSIS\n" +
               $"=====================================\n" +
               $"COMMON-SIZE ANALYSIS:\n" +
               $"Current Assets: Y1={y1CurrentAssetsPct:F2}% | Y2={y2CurrentAssetsPct:F2}% | Change={currentAssetsChange:+0.00;-0.00}pp\n" +
               $"Non-Current Assets: Y1={y1NonCurrentAssetsPct:F2}% | Y2={y2NonCurrentAssetsPct:F2}% | Change={nonCurrentAssetsChange:+0.00;-0.00}pp\n" +
               $"Current Liabilities: Y1={y1CurrentLiabPct:F2}% | Y2={y2CurrentLiabPct:F2}% | Change={currentLiabChange:+0.00;-0.00}pp\n" +
               $"Non-Current Liabilities: Y1={y1NonCurrentLiabPct:F2}% | Y2={y2NonCurrentLiabPct:F2}% | Change={nonCurrentLiabChange:+0.00;-0.00}pp\n" +
               $"Stockholders' Equity: Y1={y1EquityPct:F2}% | Y2={y2EquityPct:F2}% | Change={equityChange:+0.00;-0.00}pp\n" +
               $"\nTOP 3 CHANGES (by magnitude):\n" +
               $"1. Equity/Assets: {Math.Abs(equityChange):F2}pp change\n" +
               $"2. Non-Current Liab: {Math.Abs(nonCurrentLiabChange):F2}pp change\n" +
               $"3. Current Assets: {Math.Abs(currentAssetsChange):F2}pp change";
    }
}

# Financial Analysis Skills - Complete Framework Overview

## Purpose
This document provides an overview of all financial analysis skills and when to use them.

---

## Skill Hierarchy

### Level 1: Foundation Skills
These skills address specific financial analysis questions:

1. **Quick Liquidity Analysis** (Priority 10)
   - *Question:* Can the company pay its short-term obligations?
   - *Functions:* Current Ratio, Quick Ratio, Working Capital
   - *Time to run:* ~1 minute
   - *Audience:* Credit managers, short-term lenders

2. **Capital Structure Analysis** (Priority 11)
   - *Question:* How is the company financed? What's the financial risk?
   - *Functions:* D/E Ratio, D/A Ratio, Equity Multiplier, Equity %
   - *Time to run:* ~1 minute
   - *Audience:* Equity investors, credit analysts

3. **Period Change Analysis** (Priority 12)
   - *Question:* How did financial metrics change from last period?
   - *Functions:* Absolute Change, % Change, Percentage Point Change
   - *Time to run:* ~2 minutes (varies by # of line items)
   - *Audience:* Managers, internal auditors, trend analysts

4. **Common-Size Balance Sheet** (Priority 13)
   - *Question:* What's the composition of the balance sheet?
   - *Functions:* Common-Size %, Asset breakdown, Liability breakdown, Equity %
   - *Time to run:* ~1 minute
   - *Audience:* Comparative analysis, benchmarking

### Level 2: Executive Summary
This skill orchestrates all foundation skills:

5. **Financial Health Dashboard** (Priority 1)
   - *Question:* Complete financial health assessment
   - *Includes:* All Level 1 skills + balance sheet validation
   - *Time to run:* ~5-10 minutes
   - *Audience:* C-suite, board members, external auditors

---

## Decision Tree: Which Skill to Use?

```
Start here: What question are you trying to answer?

├─ "Can we pay our bills?"
│  └─ Use: Quick Liquidity Analysis
│     Returns: Current Ratio, Quick Ratio, Working Capital
│
├─ "What's our financial leverage?"
│  └─ Use: Capital Structure Analysis
│     Returns: D/E Ratio, D/A Ratio, Equity Multiplier, Equity %
│
├─ "How did we change from last year?"
│  └─ Use: Period Change Analysis
│     Returns: Absolute/% changes for all line items
│
├─ "How is our balance sheet structured?"
│  └─ Use: Common-Size Balance Sheet
│     Returns: Common-size percentages for all items
│
└─ "Give me the full picture"
   └─ Use: Financial Health Dashboard
      Returns: All of the above + validation + synthesis
```

---

## Analysis Sequence (Standard Workflow)

For most financial analysis projects, follow this sequence:

### Phase 1: Data Validation
- Run `FinancialAnalysisPlugin.ValidateBalanceSheetEquation()` first
- If invalid, stop and investigate data quality
- If valid, proceed to Phase 2

### Phase 2: Quick Assessment
- If time is short, run ONE skill based on your question
- If time permits, run multiple skills

### Phase 3: Detailed Analysis
- For comprehensive analysis, use **Financial Health Dashboard**
- It runs everything in logical order

### Phase 4: Deeper Investigation
- Each dashboard finding triggers deeper dives
- Use individual skills to drill down

---

## Quick Reference: Functions by Skill

| Skill | Functions |
|-------|-----------|
| **Quick Liquidity** | CalculateCurrentRatio, CalculateQuickRatio, CalculateWorkingCapital |
| **Capital Structure** | CalculateDebtToEquityRatio, CalculateDebtToAssetsRatio, CalculateEquityMultiplier, EquityToTotalAssetsPercentage |
| **Period Change** | CalculateAbsoluteChange, CalculatePercentageChange, CalculatePercentagePointChange |
| **Common-Size** | CalculateCommonSizePercentage, CommonSizeBalanceSheetAssets, CommonSizeBalanceSheetLiabilities, EquityToTotalAssetsPercentage |
| **Health Dashboard** | ALL of the above + ValidateBalanceSheetEquation |

---

## Best Practices

### 1. Always Validate First
```
BEFORE running any analysis:
  1. Validate balance sheet equation
  2. Check for negative values where unusual
  3. Verify decimal places (currency, decimals set correctly)
```

### 2. Run Skills in Context
- Don't run metrics in isolation
- Always compare to:
  - Prior periods (trends)
  - Industry benchmarks
  - Company's own targets

### 3. Interpretation Rules
- **No single ratio is conclusive**
- **Look for patterns** across multiple ratios
- **Investigate anomalies** - they often reveal problems
- **Consider context** - industry, economic conditions, company strategy

### 4. Documentation
- Document your findings with all three change types:
  - Absolute ($ impact)
  - Percentage (relative magnitude)
  - Percentage Point (share of total)

---

## Common Scenarios

### Scenario 1: Quick Credit Decision (5 minutes)
```
1. Run: Quick Liquidity Analysis
2. Look at: Current Ratio and Quick Ratio
3. Decision: Approve/decline based on thresholds
```

### Scenario 2: Investor Due Diligence (30 minutes)
```
1. Run: Financial Health Dashboard
2. Analyze each component:
   - Liquidity: Can company survive downturn?
   - Leverage: Is debt sustainable?
   - Structure: Any red flags?
   - Trends: Getting better or worse?
3. Synthesize: Overall risk assessment
```

### Scenario 3: Internal Financial Review (60 minutes)
```
1. Run: Financial Health Dashboard for current year
2. Run: Period Change Analysis (current vs. prior year)
3. Drill down: Investigate significant changes
4. Document: Key findings and action items
```

### Scenario 4: Textbook Problem (varies)
```
1. Identify: Which questions being asked
2. Map: To the relevant skill
3. Run: Skill and present results
4. Example questions:
   - "Analyze liquidity" → Quick Liquidity Analysis
   - "How is the company financed?" → Capital Structure Analysis
   - "Common-size the balance sheet" → Common-Size Balance Sheet
```

---

## Next Steps

For detailed SOPs on each skill, see:
- `01-QuickLiquidityAnalysis-SOP.md`
- `02-CapitalStructureAnalysis-SOP.md`
- `03-PeriodChangeAnalysis-SOP.md`
- `04-CommonSizeBalanceSheet-SOP.md`
- `05-FinancialHealthDashboard-SOP.md`

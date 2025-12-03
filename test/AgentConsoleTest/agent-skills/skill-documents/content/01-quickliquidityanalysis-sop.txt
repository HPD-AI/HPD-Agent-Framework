# Quick Liquidity Analysis - SOP

## Overview
Analyze a company's ability to pay short-term obligations with liquid assets.

---

## When to Use This Skill

- **Credit managers** evaluating loan applications
- **Short-term lenders** assessing repayment capacity  
- **Treasurers** monitoring cash position
- **Internal auditors** testing cash management controls
- **Quick decision** needed (5-10 minutes)

---

## Prerequisites

- Balance Sheet (current assets, current liabilities)
- Inventory data (if calculating quick ratio)
- Current period's financial data

---

## Step-by-Step Procedure

### Step 1: Gather Balance Sheet Data

| Item | Value | Note |
|------|-------|------|
| Current Assets | [auto-calculated] | Sum of all current assets |
| Current Liabilities | [auto-calculated] | Sum of all current liabilities |
| Inventory | [from balance sheet] | Usually in Current Assets |
| Prepaid Expenses | [from balance sheet] | Usually in Current Assets |

**Example Values (Acme Corp, 12/31/2024):**
- Current Assets: $250,000
- Current Liabilities: $140,000
- Inventory: $80,000

---

### Step 2: Calculate Current Ratio

**Function Call:** `FinancialAnalysisPlugin.CalculateCurrentRatio(currentAssets, currentLiabilities)`

**Calculation:**
```
Current Ratio = Current Assets ÷ Current Liabilities
```

**Example:**
```
Current Ratio = $250,000 ÷ $140,000 = 1.79
```

**Interpretation:**
| Ratio | Meaning |
|-------|---------|
| < 0.5 | ⚠️ Critical - Cannot cover liabilities |
| 0.5 - 1.0 | ⚠️ At risk - Likely liquidity problems |
| 1.0 - 1.5 | ⚠️ Tight - Limited cushion |
| 1.5 - 3.0 | ✅ Healthy - Normal range |
| > 3.0 | ℹ️ Conservative - Excess cash (possible opportunity) |

**Acme Example:** 1.79 = **Healthy** liquidity position ✅

---

### Step 3: Calculate Quick Ratio

**Function Call:** `FinancialAnalysisPlugin.CalculateQuickRatio(currentAssets, inventory, prepaidExpenses, currentLiabilities)`

**Calculation:**
```
Quick Ratio = (Current Assets - Inventory - Prepaid Expenses) ÷ Current Liabilities
```

**Rationale:** Removes inventory (may take time to sell) and prepaid expenses (not cash)

**Example:**
```
Quick Assets = $250,000 - $80,000 - $10,000 = $160,000
Quick Ratio = $160,000 ÷ $140,000 = 1.14
```

**Interpretation:**
| Ratio | Meaning |
|-------|---------|
| < 0.5 | ⚠️ Very tight - Heavy inventory dependence |
| 0.5 - 1.0 | ⚠️ Tight - Moderate inventory dependence |
| 1.0 - 1.5 | ✅ Healthy - Can pay without selling inventory |
| > 1.5 | ✅ Strong - Excess liquid resources |

**Acme Example:** 1.14 = **Healthy** immediate liquidity ✅

---

### Step 4: Calculate Working Capital

**Function Call:** `FinancialAnalysisPlugin.CalculateWorkingCapital(currentAssets, currentLiabilities)`

**Calculation:**
```
Working Capital = Current Assets - Current Liabilities
```

**Example:**
```
Working Capital = $250,000 - $140,000 = $110,000
```

**Interpretation:**
| Result | Meaning |
|--------|---------|
| Negative | ⚠️ Deficit - Company owes more than it owns in current assets |
| $0 - $50k | ⚠️ Tight - Little buffer |
| $50k - $200k | ✅ Healthy - Normal operating cushion |
| > $200k | ℹ️ High - May indicate inefficient asset use |

**Acme Example:** $110,000 = **Healthy** cushion ✅

---

## Synthesis: Reading the Liquidity Story

| Metric | Acme Corp | Assessment |
|--------|-----------|------------|
| **Current Ratio** | 1.79 | ✅ Can cover obligations 1.8x over |
| **Quick Ratio** | 1.14 | ✅ Even without inventory, has cushion |
| **Working Capital** | $110,000 | ✅ Good operating buffer |
| **Overall** | | ✅ **Strong short-term liquidity** |

### What the Numbers Tell Us:
- Acme can comfortably meet short-term obligations
- Not dependent on inventory turnover (quick ratio still healthy)
- Has $110k cushion for operations
- **Recommendation:** Approve short-term credit if other factors favorable

---

## Red Flags to Investigate

If any metric fails, investigate:

1. **Low Current Ratio + Declining Trend**
   - Investigate: Is it seasonal? Intentional cash deployment?
   - Risk: Company approaching cash crisis

2. **Current Ratio ✅ but Quick Ratio ⚠️**
   - Investigate: What percentage is inventory?
   - Risk: Liquidity depends on sales; vulnerable to inventory slowdown

3. **Negative or Barely Positive Working Capital**
   - Investigate: Is company in distress or managing efficiently? (e.g., retailers often operate with low WC)
   - Risk: Zero margin for error; any downturn = crisis

4. **Extremely High Current Ratio (>3.0)**
   - Investigate: Why so much cash?
   - Opportunity: Company could invest in growth, return capital, or pay down debt

---

## Common Pitfalls

### Pitfall 1: Seasonal Interpretation
- **Problem:** Using end-of-year ratios when company is seasonal
- **Solution:** Compare to prior-year same quarter; use average of quarters
- **Example:** Retail company has highest inventory before Christmas; compare Q4-to-Q4, not Q4-to-Q3

### Pitfall 2: Industry Variation
- **Problem:** Using manufacturing standards for retail (which operates with low WC)
- **Solution:** Benchmark against industry peers
- **Example:** Walmart typically has current ratio ~0.8 (by design); manufacturer target ~1.5

### Pitfall 3: Ignoring Receivables Quality
- **Problem:** Current assets include receivables; if collectible?
- **Solution:** Investigate accounts receivable aging
- **Example:** If $80k of $250k current assets is 90+ days past due, real liquidity is worse

### Pitfall 4: Off-Balance-Sheet Liabilities
- **Problem:** Current liabilities don't include all obligations (lease commitments, guarantees)
- **Solution:** Read footnotes for future commitments
- **Example:** Company shows $100k current liabilities, but has $50k lease payments due

---

## Comparison to Industry Benchmarks

### Typical Industry Ratios:

| Industry | Current Ratio | Quick Ratio | WC/Sales |
|----------|---------------|-------------|----------|
| Manufacturing | 1.5 - 2.0 | 1.0 - 1.5 | 10-15% |
| Retail | 0.8 - 1.2 | 0.3 - 0.6 | 2-5% |
| Technology | 1.5 - 2.5 | 1.2 - 2.0 | 15-25% |
| Financial Services | 0.5 - 1.0 | 0.3 - 0.8 | Variable |
| Utilities | 0.8 - 1.2 | 0.6 - 1.0 | 5-10% |

**Acme Comparison (if Manufacturing):**
- Current: 1.79 vs Industry: 1.5-2.0 ✅ **In range**
- Quick: 1.14 vs Industry: 1.0-1.5 ✅ **In range**

---

## Next Steps

### If Liquidity is Strong ✅
- Proceed to other analyses:
  - Capital Structure Analysis (financial risk)
  - Period Change Analysis (trends)
  - Use Financial Health Dashboard for full picture

### If Liquidity is Weak ⚠️
- **Immediate Actions:**
  1. Investigate root cause (seasonal? distress? intentional?)
  2. Analyze cash flow statement (are operations generating cash?)
  3. Review 5-year trends (is it improving or worsening?)
  4. Meet with management (what's the plan?)

- **Deep Dive Analysis:**
  - Analyze accounts receivable (are they collectible?)
  - Inventory turnover (is inventory obsolete?)
  - Accounts payable terms (can we extend?)
  - Historical working capital patterns

---

## Related Skills

- **Capital Structure Analysis:** How is cash funded?
- **Period Change Analysis:** Is liquidity improving or declining?
- **Financial Health Dashboard:** Complete picture

# Common-Size Balance Sheet - SOP

## Overview
Analyze balance sheet composition by converting all line items to percentages of total assets.

---

## When to Use This Skill

- **Balance sheet benchmarking** across companies
- **Industry comparison** analysis
- **Structural analysis** of financial position
- **Detecting** dramatic shifts in asset/liability mix
- **Identifying** unusual balance sheet compositions
- **Teaching** financial analysis basics
- **Quick assessment** (5-10 minutes)

---

## Prerequisites

- Complete balance sheet (Assets, Liabilities, Equity)
- Breakdown of asset categories (current, fixed, other)
- Breakdown of liability categories (current, long-term)
- Equity components

---

## Why Common-Size Analysis Works

### The Power of Normalization

**Problem:** Comparing two companies directly
```
Company A:  Assets $100M,  Current Assets $40M  (40%)
Company B:  Assets $1B,    Current Assets $300M (30%)

Raw numbers: B has more current assets
Percentage: A has higher liquidity (proportion)
```

**Solution:** Express everything as % of total
- **Removes size differences** (can compare small to large)
- **Shows composition** (what's the mix?)
- **Reveals structure** (asset-heavy vs. equity-light?)
- **Enables benchmarking** (compare to peers)

---

## Step-by-Step Procedure

### Step 1: Gather Balance Sheet Data

**Standard Balance Sheet Format:**

| Item | Amount | Note |
|------|--------|------|
| **ASSETS** | | |
| Current Assets | [total] | Sum of current items |
| Fixed Assets | [total] | Property, Plant, Equipment |
| Intangibles | [total] | Goodwill, patents, etc. |
| Other Assets | [total] | Investments, receivables, etc. |
| **TOTAL ASSETS** | [total] | Sum of all assets |
| **LIABILITIES** | | |
| Current Liabilities | [total] | Due within 1 year |
| Long-Term Liabilities | [total] | Due after 1 year |
| Other Liabilities | [total] | Deferred taxes, etc. |
| **TOTAL LIABILITIES** | [total] | Sum of all liabilities |
| **EQUITY** | | |
| Common Stock | [amount] | |
| Retained Earnings | [amount] | |
| Other Equity | [amount] | Accummulated other CI, etc. |
| **TOTAL EQUITY** | [total] | Sum of all equity |
| **TOTAL LIAB + EQUITY** | [total] | Should = Total Assets |

**Example (Acme Manufacturing, 12/31/2024):**

| Item | Amount |
|------|--------|
| Current Assets | $300,000 |
| Fixed Assets | $500,000 |
| Intangibles | $50,000 |
| Other Assets | $150,000 |
| **TOTAL ASSETS** | **$1,000,000** |
| Current Liabilities | $200,000 |
| Long-Term Debt | $300,000 |
| Other Liabilities | $50,000 |
| **TOTAL LIABILITIES** | **$550,000** |
| Stockholders' Equity | $450,000 |
| **TOTAL EQUITY** | **$450,000** |
| **TOTAL LIAB + EQUITY** | **$1,000,000** |

---

### Step 2: Calculate Common-Size Percentages - Assets

**Function Call:** `FinancialAnalysisPlugin.CalculateCommonSizePercentage(itemValue, totalAssets)`

**Calculation:**
```
Common-Size % = (Line Item ÷ Total Assets) × 100
```

**Acme Example - Asset Composition:**

| Asset Item | Amount | Common-Size % | Meaning |
|-----------|--------|---------------|---------|
| Current Assets | $300,000 | 30.0% | 30¢ of every $1 is liquid |
| Fixed Assets | $500,000 | 50.0% | 50¢ of every $1 is fixed |
| Intangibles | $50,000 | 5.0% | 5¢ of every $1 is intangible |
| Other Assets | $150,000 | 15.0% | 15¢ of every $1 is other |
| **TOTAL ASSETS** | **$1,000,000** | **100.0%** | Everything |

**Interpretation:**

| % | Meaning | Assessment |
|---|---------|-----------|
| Current Assets 30% | 30% liquid within year | Moderate liquidity |
| Fixed Assets 50% | 50% capital-intensive | Asset-heavy business |
| Intangibles 5% | Low intangible assets | Not tech/software heavy |
| Other 15% | Some investments/receivables | Moderate other holdings |

---

### Step 3: Drill Down - Current Assets Breakdown

**Function Call:** `FinancialAnalysisPlugin.CommonSizeBalanceSheetAssets(currentAssets, totalAssets)`

**Purpose:** Understand what makes up the 30% current assets

**Typical Breakdown:**

| Current Asset | Amount | % of Current | % of Total |
|--------------|--------|--------------|-----------|
| Cash & Equivalents | $60,000 | 20% | 6.0% |
| Accounts Receivable | $120,000 | 40% | 12.0% |
| Inventory | $100,000 | 33% | 10.0% |
| Prepaid Expenses | $20,000 | 7% | 2.0% |
| **Total Current** | **$300,000** | **100%** | **30.0%** |

**Interpretation:**
- Cash is only 6% of total assets (low cash holdings)
- A/R is 12% (customers owe significant amount)
- Inventory is 10% (working capital tied up in stock)
- Prepaid is 2% (low deferred costs)

---

### Step 4: Drill Down - Fixed Assets Breakdown

**Purpose:** Understand capital structure (operational assets vs. real estate)

**Typical Breakdown:**

| Fixed Asset | Amount | % of Fixed | % of Total |
|-------------|--------|-----------|-----------|
| Real Estate | $250,000 | 50% | 25.0% |
| Equipment | $200,000 | 40% | 20.0% |
| Vehicles | $30,000 | 6% | 3.0% |
| Accumulated Depreciation | ($0,000) | 0% | 0.0% |
| **Net Fixed** | **$500,000** | **100%** | **50.0%** |

**Interpretation:**
- 25% in real estate (significant property holdings)
- 20% in operational equipment
- 3% in vehicles (minimal fleet)
- Company is property-rich (may be real estate play)

---

### Step 5: Calculate Common-Size Percentages - Liabilities

**Function Call:** `FinancialAnalysisPlugin.CommonSizeBalanceSheetLiabilities(totalLiabilities, totalAssets)`

**Calculation:**
```
Liability Common-Size % = (Liability ÷ Total Assets) × 100
```

**Acme Example - Liability Composition:**

| Liability Item | Amount | Common-Size % | Meaning |
|---------------|--------|----------------|---------|
| Current Liabilities | $200,000 | 20.0% | 20¢ of every $1 is due in 1 year |
| Long-Term Debt | $300,000 | 30.0% | 30¢ of every $1 is long-term debt |
| Other Liabilities | $50,000 | 5.0% | 5¢ of every $1 is other |
| **TOTAL LIABILITIES** | **$550,000** | **55.0%** | Everything owed |

**Interpretation:**

| % | Meaning | Assessment |
|---|---------|-----------|
| Current Liab 20% | 20% due within year | Moderate near-term obligations |
| Long-Term Debt 30% | 30% long-term debt | Significant leverage |
| Other 5% | Deferred items, etc. | Minor obligations |
| **Total 55%** | 55% of assets financed by debt | 45% by equity |

---

### Step 6: Calculate Common-Size Percentages - Equity

**Function Call:** `FinancialAnalysisPlugin.EquityToTotalAssetsPercentage(totalEquity, totalAssets)`

**Calculation:**
```
Equity % of Assets = (Total Equity ÷ Total Assets) × 100
```

**Acme Example - Equity Composition:**

| Equity Item | Amount | Common-Size % | Meaning |
|------------|--------|----------------|---------|
| Stockholders' Equity | $450,000 | 45.0% | 45¢ of every $1 is owned by shareholders |
| **TOTAL EQUITY** | **$450,000** | **45.0%** | Everything owned |

**Balance Check:**
```
Total Liabilities 55% + Total Equity 45% = 100% ✅

(55% ÷ 45%) = 1.22, which matches D/E ratio of 1.22
```

---

## The Complete Common-Size Balance Sheet

### Acme Manufacturing - Full Common-Size Analysis

| Item | Amount | % of Total |
|-----|--------|-----------|
| **ASSETS** | | |
| Current Assets | $300,000 | 30.0% |
| Fixed Assets | $500,000 | 50.0% |
| Intangibles | $50,000 | 5.0% |
| Other Assets | $150,000 | 15.0% |
| **TOTAL ASSETS** | **$1,000,000** | **100.0%** |
| | | |
| **LIABILITIES** | | |
| Current Liabilities | $200,000 | 20.0% |
| Long-Term Debt | $300,000 | 30.0% |
| Other Liabilities | $50,000 | 5.0% |
| **TOTAL LIABILITIES** | **$550,000** | **55.0%** |
| | | |
| **EQUITY** | | |
| Stockholders' Equity | $450,000 | 45.0% |
| **TOTAL EQUITY** | **$450,000** | **45.0%** |
| | | |
| **TOTAL LIAB + EQUITY** | **$1,000,000** | **100.0%** |

### What This Tells Us About Acme:

**Asset Structure:**
- 30% Current (moderate liquidity, not a bank)
- 50% Fixed (manufacturing company with factories/equipment)
- 5% Intangibles (not tech/software)
- 15% Other (some investments or other holdings)

**Financing Structure:**
- 55% Debt (meaningful leverage, but not extreme)
- 45% Equity (solid equity cushion)
- Debt/Equity: 1.22 (balanced, slightly debt-heavy)

**Business Model Implications:**
- Capital-intensive (50% fixed assets)
- Traditional manufacturing (low intangibles)
- Moderate leverage (typical for manufacturing)
- Reasonable liquidity position

---

## Benchmarking Against Industry Standards

### Typical Industry Common-Size Structures:

| Industry | Current % | Fixed % | Current Liab % | LT Debt % | Equity % |
|----------|-----------|---------|-----------------|-----------|----------|
| **Retail** | 25-35% | 30-40% | 15-25% | 15-25% | 50-70% |
| **Manufacturing** | 25-35% | 50-65% | 15-25% | 20-35% | 45-65% |
| **Technology** | 40-60% | 10-20% | 10-15% | 5-15% | 75-85% |
| **Utilities** | 15-25% | 70-80% | 20-30% | 30-50% | 40-60% |
| **Real Estate** | 5-15% | 80-90% | 15-25% | 40-60% | 20-45% |

### Acme Comparison (Manufacturing)

| Metric | Acme | Mfg Average | Assessment |
|--------|------|-------------|-----------|
| Current Assets | 30% | 25-35% | ✅ In range |
| Fixed Assets | 50% | 50-65% | ✅ In range |
| Current Liab | 20% | 15-25% | ✅ In range |
| LT Debt | 30% | 20-35% | ✅ In range |
| Equity | 45% | 45-65% | ✅ In range |

**Conclusion:** Acme is structured like a typical manufacturer - no red flags ✅

---

## Trend Analysis: Common-Size Over Time

### Tracking Composition Changes (3-Year Trend)

| Item | 2022 | 2023 | 2024 | Trend |
|-----|------|------|------|-------|
| Current Assets | 28% | 29% | 30% | ↗ Slightly more liquid |
| Fixed Assets | 52% | 51% | 50% | ↘ Slight divestment/depreciation |
| Equity % | 42% | 43% | 45% | ↗ Improving capital structure |
| LT Debt % | 32% | 31% | 30% | ↘ Paying down debt |

**Story:** Company gradually improving financial position - less debt, more equity, slight shift to liquidity

---

## Real-World Scenarios

### Scenario 1: Asset-Heavy Company (Real Estate/Utilities)

```
Assets:
  Current:  10% (very low)
  Fixed:    85% (very high)
  Other:    5%

Liabilities:
  Current:  15%
  LT Debt:  50%
  Equity:   35%

Meaning:
- Highly capital-intensive business
- Most value in long-term fixed assets
- Financed largely by debt (typical for utilities)
- High leverage (50% LT debt) but sustainable due to stable cash flows
```

### Scenario 2: Liquid Cash-Heavy Company (Tech/Holding Company)

```
Assets:
  Current:  55% (very high)
  Fixed:    15% (low)
  Intangibles: 20% (significant)
  Other:    10%

Liabilities:
  Current:  8%
  LT Debt:  2%
  Equity:   90%

Meaning:
- Asset-light model (tech or service company)
- Large cash position (acquisition target or waiting for investment)
- Minimal debt (strong balance sheet)
- High equity cushion
- Business model doesn't require heavy fixed assets
```

### Scenario 3: Red Flag - Deteriorating Equity Position

```
2022:  Equity 60%, Debt 40%
2023:  Equity 50%, Debt 50%
2024:  Equity 40%, Debt 60%

Warning Signs:
- Equity declining rapidly (-5 pp per year)
- Debt rising proportionally
- Trajectory: Company burning equity
- Investigation needed: What's causing losses/dilution?
```

### Scenario 4: Unusual - Very High Intangibles

```
Assets:
  Current:  20%
  Fixed:    20%
  Intangibles: 55%
  Other:    5%

Meaning:
- Acquisition was expensive (paid for goodwill/brands)
- Post-acquisition integration critical
- Risk: If business underperforms, goodwill impairment likely
- Must monitor profitability closely
- Compare: Intangibles per balance sheet vs. realistic value
```

---

## Key Relationships to Watch

### 1. Asset Composition Stability

**Question:** Is the asset mix changing dramatically?

**If % changes > 5pp year-over-year:**
- Company strategy shifting (e.g., investing in new asset class)
- Large acquisition (added intangibles)
- Asset sale/divestment
- Depreciation (fixed assets declining as %)

---

### 2. Debt Maturity Structure

**Question:** Is company shifting debt profile?

**Example:**
```
2023:  Current Liab 25%, LT Debt 25% (equal maturity)
2024:  Current Liab 15%, LT Debt 35% (more long-term)

Meaning: Company extending maturity (good - less near-term refinancing risk)
```

---

### 3. Equity Cushion

**Question:** What's the equity buffer?

```
High Equity % (70%+):     Conservative capital structure
Medium Equity % (40-70%): Balanced approach
Low Equity % (<40%):      Aggressive/leveraged structure
```

---

## Common Pitfalls

### Pitfall 1: Comparing Across Industries Directly
- **Problem:** Tech company (80% equity) vs. Utility (40% equity)
- **Solution:** Compare within industry peer group
- **Example:** Tech company looks weak at 80%? No - industry normal

### Pitfall 2: Ignoring Scale Changes
- **Problem:** Current assets 30% → 25%, but actually grew in dollars
- **Solution:** Look at both % AND absolute $ changes
- **Example:** Total assets tripled; current assets doubled (% down but $ up)

### Pitfall 3: Not Investigating "Other" Categories
- **Problem:** "Other Assets" is 20% - what is it?
- **Solution:** Drill into footnotes to understand
- **Example:** "Other" could be long-term receivables, investments, or hidden problems

### Pitfall 4: Forgetting Accumulated Depreciation Impact
- **Problem:** Fixed assets seem to be declining % - is it intentional?
- **Solution:** Separate net fixed assets from depreciation
- **Example:** Capex same, but depreciation > capex = net fixed declining

### Pitfall 5: Intangibles Impairment Risk
- **Problem:** Intangibles are 40% of assets
- **Solution:** Monitor for goodwill write-downs
- **Example:** If acquisition underperforms, large impairment coming

---

## Analysis Workflow

### Standard Common-Size Analysis Process:

```
1. Calculate Common-Size %
   ├─ Express all items as % of total assets
   └─ Creates normalized view

2. Identify Unusual Items
   ├─ What's higher/lower than industry?
   └─ What's unusual in composition?

3. Compare to Prior Years
   ├─ Are % changing dramatically?
   └─ Is composition stable or shifting?

4. Benchmark to Industry
   ├─ Is structure typical?
   └─ Outliers above/below peer group?

5. Drill Down on Surprises
   ├─ Investigate unusual line items
   └─ Read footnotes for details

6. Connect to Other Analyses
   ├─ Does this explain liquidity/leverage findings?
   └─ Update overall assessment
```

---

## Next Steps

### If Structure is Typical ✅
- Compare to prior years (is it stable?)
- Use findings to inform liquidity analysis
- Proceed to profitability analysis

### If Structure is Unusual 🟡
- Understand the why (strategy, market conditions, acquisition?)
- Compare to peers (is it normal for industry?)
- Investigate further if concerning

### If Structure is Deteriorating ⚠️
- Track trend over time (is it accelerating?)
- Investigate root causes
- Assess sustainability

---

## Related Skills

- **Quick Liquidity Analysis:** How much of current assets are liquid?
- **Capital Structure Analysis:** Is financing structure healthy?
- **Period Change Analysis:** How did composition change from prior year?
- **Financial Health Dashboard:** Full picture including structure

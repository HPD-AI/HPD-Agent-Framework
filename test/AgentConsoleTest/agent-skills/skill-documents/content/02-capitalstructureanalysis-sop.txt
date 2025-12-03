# Capital Structure Analysis - SOP

## Overview
Analyze how a company is financed (debt vs. equity) and assess financial leverage/risk.

---

## When to Use This Skill

- **Equity investors** assessing financial risk
- **Credit analysts** evaluating default probability
- **Company management** deciding financing strategy
- **Debt holders** assessing business model sustainability
- **Valuation analysts** adjusting discount rates for risk
- **Medium-term** assessment (10-15 minutes)

---

## Prerequisites

- Balance Sheet (total assets, total liabilities, total equity)
- Breakdown of liabilities (current + long-term)
- Understanding of company's industry and debt covenants

---

## Step-by-Step Procedure

### Step 1: Gather Balance Sheet Data

| Item | Value | Note |
|------|-------|------|
| Total Assets | [auto-calculated] | From balance sheet |
| Total Liabilities | [auto-calculated] | Current + Long-term |
| Total Equity | [auto-calculated] | Assets - Liabilities |
| Total Debt | [from balance sheet] | Long-term debt + current portion |

**Example Values (Beta Manufacturing, 12/31/2024):**
- Total Assets: $1,000,000
- Total Liabilities: $600,000 (Debt: $400,000)
- Total Equity: $400,000

---

### Step 2: Calculate Debt-to-Equity Ratio

**Function Call:** `FinancialAnalysisPlugin.CalculateDebtToEquityRatio(totalLiabilities, totalEquity)`

**Calculation:**
```
D/E Ratio = Total Debt ÷ Total Equity
```

**Example:**
```
D/E Ratio = $400,000 ÷ $400,000 = 1.0
```

**Interpretation:**

| Ratio | Meaning | Risk Level |
|-------|---------|-----------|
| 0.0 - 0.3 | Very low debt | 🟢 Conservative |
| 0.3 - 0.7 | Moderate debt | 🟢 Healthy |
| 0.7 - 1.5 | Meaningful leverage | 🟡 Elevated |
| 1.5 - 2.5 | High leverage | 🔴 Aggressive |
| > 2.5 | Very high leverage | 🔴 Risky |

**Beta Example:** 1.0 = **Balanced** - equally financed by debt and equity 🟡

**Rationale:**
- D/E of 1.0 = $1 debt for every $1 equity
- Higher ratio = More financial risk (more debt obligations)
- Lower ratio = More financial stability

---

### Step 3: Calculate Debt-to-Assets Ratio

**Function Call:** `FinancialAnalysisPlugin.CalculateDebtToAssetsRatio(totalLiabilities, totalAssets)`

**Calculation:**
```
D/A Ratio = Total Debt ÷ Total Assets
```

**Example:**
```
D/A Ratio = $400,000 ÷ $1,000,000 = 0.40 or 40%
```

**Interpretation:**

| Ratio | Meaning | Risk Level |
|-------|---------|-----------|
| 0% - 20% | Minimal leverage | 🟢 Very conservative |
| 20% - 40% | Moderate leverage | 🟢 Healthy |
| 40% - 60% | Meaningful leverage | 🟡 Elevated |
| 60% - 80% | High leverage | 🔴 Aggressive |
| > 80% | Very high leverage | 🔴 Risky |

**Beta Example:** 40% = **Moderate** - 40¢ of every asset $1 is financed by debt 🟡

**Rationale:**
- Easier to interpret than D/E (shows % of assets financed by debt)
- D/A of 40% = Creditors finance 40%, equity holders finance 60%
- Complements D/E ratio

---

### Step 4: Calculate Equity Multiplier

**Function Call:** `FinancialAnalysisPlugin.CalculateEquityMultiplier(totalAssets, totalEquity)`

**Calculation:**
```
Equity Multiplier = Total Assets ÷ Total Equity
```

**Example:**
```
Equity Multiplier = $1,000,000 ÷ $400,000 = 2.5x
```

**Interpretation:**

| Multiplier | Meaning | Leverage |
|-----------|---------|----------|
| 1.0 - 1.5x | Minimal | 🟢 No debt used |
| 1.5 - 2.0x | Moderate | 🟢 Some leverage |
| 2.0 - 3.0x | Meaningful | 🟡 Elevated leverage |
| 3.0 - 5.0x | High | 🔴 Aggressive leverage |
| > 5.0x | Very high | 🔴 Risky leverage |

**Beta Example:** 2.5x = **Elevated** - Every $1 of equity is controlling $2.50 of assets 🟡

**Rationale:**
- Equity Multiplier = 1 + D/E ratio
- Shows financial leverage directly
- Used in ROE decomposition (DuPont analysis)

---

### Step 5: Calculate Equity-to-Assets Percentage

**Function Call:** `FinancialAnalysisPlugin.EquityToTotalAssetsPercentage(totalEquity, totalAssets)`

**Calculation:**
```
Equity % = Total Equity ÷ Total Assets × 100
```

**Example:**
```
Equity % = $400,000 ÷ $1,000,000 × 100 = 40%
```

**Interpretation:**

| Percentage | Meaning | Risk Level |
|-----------|---------|-----------|
| 0% - 20% | Minimal equity cushion | 🔴 High risk |
| 20% - 40% | Moderate equity | 🟡 Balanced |
| 40% - 60% | Healthy equity | 🟢 Conservative |
| 60% - 80% | Strong equity | 🟢 Very conservative |
| > 80% | Minimal leverage | 🟢 Extremely conservative |

**Beta Example:** 40% = **Balanced** - Equity finances 40% of assets, debt 60% 🟡

**Rationale:**
- Complements D/A (D/A + Equity % = 100%)
- Shows equity cushion for creditors
- Higher = safer for creditors, lower ROE potential for equity

---

## Synthesis: Reading the Capital Structure Story

| Metric | Beta Manufacturing | Benchmark | Assessment |
|--------|-------------------|-----------|------------|
| **D/E Ratio** | 1.0 | 0.5-0.8 (industry) | 🟡 Above peers |
| **D/A Ratio** | 40% | 30-35% (industry) | 🟡 Above peers |
| **Equity Multiplier** | 2.5x | 1.8-2.0x (industry) | 🟡 Elevated |
| **Equity %** | 40% | 55-60% (industry) | 🟡 Below peers |
| **Overall** | | | 🟡 **More leveraged than peers** |

### What the Numbers Tell Us:

**Capital Structure Characteristics:**
- Company uses more debt than industry average
- For every $1 of equity, $1 of debt
- Each $1 of equity controls $2.50 of assets
- Creditors finance 40% of asset base

**Financial Risk Assessment:**
- ✅ Not in distress (not overleveraged)
- 🟡 Above-average leverage for industry
- **Implication:** More sensitive to economic downturns; less cushion than peers

**Debt Capacity:**
- Could likely take on more debt (Equity Multiplier < 3.0x is typical for healthy companies)
- Any significant downturn would stress cash flow coverage

---

## Red Flags to Investigate

### Red Flag 1: D/E Ratio Rising Trend
- **Example:** 0.5x → 0.7x → 1.0x → 1.3x over 3 years
- **Investigation:** Why is company taking on more debt?
  - Growth capex? (positive)
  - Debt restructuring? (neutral)
  - Funding losses? (negative) ⚠️
- **Risk:** If funding losses, deteriorating business quality

### Red Flag 2: D/E Much Higher Than Industry
- **Example:** Industry average 0.6x, company 1.8x
- **Investigation:** Why? Different business model or financial distress?
  - Leveraged buyout? (LBO companies intentionally high D/E)
  - Acquisition funding? (temporary)
  - Weak profitability? (concern) ⚠️
- **Risk:** Limited flexibility; vulnerable to rate increases or downturns

### Red Flag 3: High D/A But Equity Still Declining
- **Example:** D/A 60% and rising
- **Investigation:** Company building reserves? Or burning cash?
  - If equity declining: burning cash ⚠️
  - If equity stable: company is profitable ✅
- **Risk:** If equity declining, business is not generating adequate returns

### Red Flag 4: Equity Multiplier > 5.0x
- **Example:** Equity Multiplier of 6.0x
- **Investigation:** Extreme leverage - why?
  - Financial institution? (normal)
  - LBO? (may be intentional)
  - In distress? ⚠️
- **Risk:** Any downturn could wipe out equity; bankruptcy risk elevated

---

## Industry Variations

Capital structure varies significantly by industry:

| Industry | Typical D/E | Typical D/A | Typical Equity % |
|----------|----------|---------|---------|
| Utilities | 1.0 - 1.5 | 40% - 60% | 40% - 60% |
| Real Estate | 1.5 - 3.0 | 60% - 75% | 25% - 40% |
| Manufacturing | 0.5 - 1.0 | 25% - 40% | 60% - 75% |
| Technology | 0.2 - 0.5 | 15% - 30% | 70% - 85% |
| Financial Services | 4.0 - 10.0 | 80% - 90% | 10% - 20% |
| Retail | 0.8 - 1.5 | 30% - 50% | 50% - 70% |

**Beta Manufacturing (Assuming Manufacturing):**
- Actual: D/E 1.0 vs Industry: 0.5-1.0 = **At top of range** 🟡
- Actual: D/A 40% vs Industry: 25-40% = **At top of range** 🟡
- Implication: More aggressive than typical manufacturer

---

## Leverage Implications

### Positive Aspects of Debt Financing:
- ✅ **Tax benefit:** Interest is tax-deductible (equity dividends are not)
- ✅ **Leverage:** Amplify ROE (if generating returns > cost of debt)
- ✅ **Discipline:** Debt covenants can improve management quality
- ✅ **Cost:** Debt often cheaper than equity

### Negative Aspects of Debt Financing:
- ⚠️ **Fixed obligations:** Debt must be paid regardless of profitability
- ⚠️ **Bankruptcy risk:** Too much debt = insolvency risk
- ⚠️ **Opportunity cost:** Debt limits financial flexibility
- ⚠️ **Refinancing risk:** If debt comes due, need to refinance at potentially higher rates

---

## Connection to Other Metrics

### D/E Ratio × Profit Margin = ROE Leverage Effect
```
Example:
- D/E Ratio: 1.0
- ROA (Return on Assets): 10%
- ROE (Return on Equity) = ROA × (1 + D/E) = 10% × 2.0 = 20%

If ROA > Cost of Debt (say 4%), leverage benefits equity holders:
  - Without leverage: ROE = 10%
  - With 1.0 D/E: ROE = 20%
```

### Connection to Interest Coverage Ratio:
```
Interest Coverage = EBIT ÷ Interest Expense

Higher debt → Higher interest expense → Lower interest coverage
If Interest Coverage < 1.5x, significant risk
```

---

## Next Steps

### If Capital Structure is Healthy 🟢
- **Proceed to:**
  - Liquidity Analysis (can company pay debt?)
  - Period Change Analysis (is leverage increasing/decreasing?)
  - Profitability Analysis (can company afford the debt?)

### If Capital Structure is Aggressive 🟡
- **Deep Dive Analysis:**
  1. Interest Coverage Ratio - Can company afford debt service?
  2. Debt maturity schedule - When does debt come due?
  3. Cash flow statement - Is company generating cash?
  4. Debt covenants - Any restrictions or concerns?

### If Capital Structure is Risky 🔴
- **Immediate Action:**
  1. Assess bankruptcy probability
  2. Review management's refinancing plans
  3. Analyze downside scenarios (recession impact)
  4. Determine recovery value for creditors

---

## Related Skills

- **Quick Liquidity Analysis:** Can company pay debt obligations?
- **Period Change Analysis:** Is leverage increasing or improving?
- **Financial Health Dashboard:** Full risk assessment including leverage

# Period Change Analysis - SOP

## Overview
Analyze how financial metrics changed from one period to another using multiple measurement approaches.

---

## When to Use This Skill

- **Financial analysts** assessing business trends
- **Management** monitoring year-over-year performance
- **Internal auditors** investigating significant changes
- **Investors** understanding business trajectory
- **Budget vs. actual** comparisons
- **Variance analysis** in financial reviews
- **Medium-term** assessment (15-30 minutes depending on line items)

---

## Prerequisites

- Balance sheets or P&L statements from two consecutive periods
- Corresponding values for all line items being analyzed
- Understanding of what "normal" changes are for the industry
- Context for any known one-time items

---

## Three Change Metrics Explained

### Understanding When to Use Each Metric

| Metric | Use When | Shows | Example |
|--------|----------|-------|---------|
| **Absolute Change** | You need dollar impact | How much $ changed | Revenue up $10M |
| **Percentage Change** | You need relative magnitude | % growth rate | Revenue up 15% |
| **Percentage Point Change** | You need composition shift | Share of total changed | Revenue % of sales up 5 pp |

---

## Step-by-Step Procedure

### Step 1: Gather Period Data

| Item | Prior Period | Current Period | Note |
|------|--------------|-----------------|------|
| [Line Item 1] | [Prior Value] | [Current Value] | e.g., Revenue |
| [Line Item 2] | [Prior Value] | [Current Value] | e.g., COGS |
| [Line Item ...] | [Prior Value] | [Current Value] | All items to analyze |

**Example Dataset (Acme Corp, Full Year Comparison):**

| Line Item | FY 2023 | FY 2024 | Type |
|-----------|---------|---------|------|
| Revenue | $500,000 | $575,000 | Income Statement |
| COGS | $300,000 | $345,000 | Income Statement |
| Gross Profit | $200,000 | $230,000 | Income Statement |
| SG&A | $100,000 | $115,000 | Income Statement |
| Net Income | $60,000 | $69,000 | Income Statement |

---

### Step 2: Calculate Absolute Changes

**Function Call:** `FinancialAnalysisPlugin.CalculateAbsoluteChange(currentValue, priorValue)`

**Calculation:**
```
Absolute Change = Current Value - Prior Value
```

**For Each Line Item:**

| Line Item | FY 2023 | FY 2024 | $ Change | Interpretation |
|-----------|---------|---------|----------|-----------------|
| Revenue | $500,000 | $575,000 | +$75,000 | ✅ Revenue grew |
| COGS | $300,000 | $345,000 | +$45,000 | ⚠️ COGS grew (check margin) |
| Gross Profit | $200,000 | $230,000 | +$30,000 | ✅ Margin increased |
| SG&A | $100,000 | $115,000 | +$15,000 | ⚠️ Expenses grew (check efficiency) |
| Net Income | $60,000 | $69,000 | +$9,000 | ✅ Profitability grew |

**Interpretation Guidelines:**

| Result | Meaning | Context Needed |
|--------|---------|-----------------|
| Positive | Increase from prior period | Is it good? Compare to revenue growth |
| Negative | Decrease from prior period | Is it good? Depends on line item |
| Zero | No change | Unusual - investigate why |

**Acme Analysis:**
- Revenue: +$75,000 (growing top line) ✅
- COGS: +$45,000 (cost growing slower than revenue) ✅
- Gross Profit: +$30,000 (margin expanding) ✅
- SG&A: +$15,000 (overhead controlled) ✅
- Net Income: +$9,000 (profitability improved) ✅

---

### Step 3: Calculate Percentage Changes

**Function Call:** `FinancialAnalysisPlugin.CalculatePercentageChange(currentValue, priorValue)`

**Calculation:**
```
% Change = (Current Value - Prior Value) ÷ Prior Value × 100
```

**For Each Line Item:**

| Line Item | FY 2023 | FY 2024 | % Change | Interpretation |
|-----------|---------|---------|----------|-----------------|
| Revenue | $500,000 | $575,000 | +15.0% | ✅ Strong growth |
| COGS | $300,000 | $345,000 | +15.0% | ℹ️ Growing in line with revenue |
| Gross Profit | $200,000 | $230,000 | +15.0% | ✅ Margin stable (same %) |
| SG&A | $100,000 | $115,000 | +15.0% | ⚠️ Growing in line with revenue |
| Net Income | $60,000 | $69,000 | +15.0% | ✅ Growing in line with revenue |

**Interpretation Guidelines:**

| % Change | Assessment | Follow-up |
|----------|-----------|-----------|
| < -20% | Significant decline | Investigate cause |
| -20% to -5% | Meaningful decline | Understand reason |
| -5% to +5% | Relatively flat | Compare to peers/trends |
| +5% to +20% | Good growth | Assess sustainability |
| > +20% | Strong growth | Can company sustain? |

**Acme Analysis:**
- All metrics: +15.0% growth
- Implication: Balanced growth across all dimensions (no red flags) ✅
- Growth rate: 15% is healthy (need to compare to industry average)

---

### Step 4: Calculate Percentage Point Changes (Common-Size)

**Function Call:** `FinancialAnalysisPlugin.CalculatePercentagePointChange(currentPercent, priorPercent)`

**Purpose:** Understand changes in composition/proportions

**First, Calculate Common-Size Percentages:**

| Line Item | FY 2023 $ | FY 2023 % | FY 2024 $ | FY 2024 % |
|-----------|-----------|-----------|-----------|-----------|
| Revenue | $500,000 | 100.0% | $575,000 | 100.0% |
| COGS | $300,000 | 60.0% | $345,000 | 60.0% |
| Gross Profit | $200,000 | 40.0% | $230,000 | 40.0% |
| SG&A | $100,000 | 20.0% | $115,000 | 20.0% |
| Net Income | $60,000 | 12.0% | $69,000 | 12.0% |

**Calculate Percentage Point Changes:**

| Line Item | FY 2023 % | FY 2024 % | pp Change | Interpretation |
|-----------|-----------|-----------|-----------|-----------------|
| COGS % of Revenue | 60.0% | 60.0% | 0.0 pp | No change - stable |
| Gross Margin % | 40.0% | 40.0% | 0.0 pp | No change - stable |
| SG&A % of Revenue | 20.0% | 20.0% | 0.0 pp | No change - well controlled |
| Net Margin % | 12.0% | 12.0% | 0.0 pp | No change - consistent |

**Interpretation Guidelines:**

| pp Change | Meaning | Assessment |
|-----------|---------|-----------|
| < -2.0 pp | Negative margin compression | ⚠️ Investigate |
| -2.0 to -0.5 pp | Slight deterioration | ℹ️ Monitor |
| -0.5 to +0.5 pp | Essentially flat | ✅ Stable |
| +0.5 to +2.0 pp | Slight improvement | ✅ Good |
| > +2.0 pp | Meaningful improvement | ✅ Excellent |

**Acme Analysis:**
- All margins: 0.0 pp change
- Implication: Profitability structure unchanged (perfectly proportional growth) ✅

---

## Three-Part Synthesis: Complete Change Story

### The Revenue Growth Story (Three Perspectives)

**Acme Corp Revenue Analysis:**

1. **Absolute Change Perspective:**
   - Revenue increased by **$75,000**
   - Impact on cash: Company has additional $75k revenue to work with
   - Question: Was this organic or from acquisition?

2. **Percentage Change Perspective:**
   - Revenue grew **15%** year-over-year
   - Growth rate: Solid (need to compare to industry)
   - Question: Is 15% sustainable?

3. **Percentage Point Change Perspective:**
   - Revenue % of total: Still 100% (by definition)
   - Implication: All growth came from top line (no price/volume issues)
   - Question: At what price points?

### Understanding Metric Relationships

```
These metrics are related:

Absolute Change = $ amount changed
Percentage Change = Absolute Change ÷ Prior Value
Percentage Point Change = Change in proportion (composition metric)

Example: If revenue goes from $100 to $115:
  - Absolute Change: +$15
  - Percentage Change: +15%
  - Percentage Point Change: N/A (revenue is total, stays at 100%)

Example: If Gross Margin goes from 35% to 38%:
  - Absolute Change: +3 percentage points (or +$3k on $100k base)
  - Percentage Change: +8.6% (3÷35)
  - Percentage Point Change: +3.0 pp ← Use this for margins!
```

---

## Real-World Scenarios

### Scenario 1: Mixed Changes (Requires Investigation)

**Data:**
| Line Item | FY 2023 | FY 2024 | $ Change | % Change | pp Change (% of revenue) |
|-----------|---------|---------|----------|----------|----------------------|
| Revenue | $500,000 | $550,000 | +$50,000 | +10% | 100% → 100% |
| COGS | $300,000 | $345,000 | +$45,000 | +15% | 60.0% → 62.7% |
| Gross Profit | $200,000 | $205,000 | +$5,000 | +2.5% | 40.0% → 37.3% |
| SG&A | $100,000 | $105,000 | +$5,000 | +5% | 20.0% → 19.1% |
| Net Income | $60,000 | $55,000 | -$5,000 | -8.3% | 12.0% → 10.0% |

**Three-Metric Analysis:**
1. **Absolute:** Revenue +$50k, but income -$5k (profit declined!)
2. **Percentage:** Revenue +10%, but COGS +15% (costs growing faster!)
3. **Percentage Point:** Gross margin -2.7 pp, Net margin -2.0 pp (profitability eroding!)

**Investigation Questions:**
- Why is COGS growing faster (15%) than revenue (10%)?
- Are there supply chain cost increases?
- Is the product mix shifting to lower-margin items?
- Is there manufacturing inefficiency?

---

### Scenario 2: Strong Growth with Margin Expansion

**Data:**
| Line Item | FY 2023 | FY 2024 | % Change | pp Change |
|-----------|---------|---------|----------|-----------|
| Revenue | $500,000 | $650,000 | +30% | 100% → 100% |
| COGS | $300,000 | $358,000 | +19.3% | 60.0% → 55.1% |
| Gross Profit | $200,000 | $292,000 | +46% | 40.0% → 44.9% |
| Net Income | $60,000 | $87,000 | +45% | 12.0% → 13.4% |

**Three-Metric Analysis:**
1. **Absolute:** Revenue +$150k, profit +$27k
2. **Percentage:** Revenue +30%, COGS only +19.3%, profit +45%
3. **Percentage Point:** Gross margin +4.9 pp, Net margin +1.4 pp

**Story:** Excellent execution. Revenue growing, costs under control, operating leverage kicking in. ✅

---

### Scenario 3: Red Flag - Revenue Down but Expenses Up

**Data:**
| Line Item | FY 2023 | FY 2024 | % Change | pp Change |
|-----------|---------|---------|----------|-----------|
| Revenue | $500,000 | $475,000 | -5% | 100% → 100% |
| COGS | $300,000 | $315,000 | +5% | 60.0% → 66.3% |
| Gross Profit | $200,000 | $160,000 | -20% | 40.0% → 33.7% |
| SG&A | $100,000 | $110,000 | +10% | 20.0% → 23.2% |
| Net Income | $60,000 | $35,000 | -42% | 12.0% → 7.4% |

**Red Flags:**
- Revenue declining (-5%) but COGS up (+5%) → Double negative on margins
- Operating leverage working in reverse: Expense base not flexible
- Net income down -42% on only -5% revenue decline = High operating leverage downside
- SG&A up +10% despite lower revenue = Cost control issues

**Investigation:** Urgent - need management explanation

---

## Common Pitfalls

### Pitfall 1: Focusing on Only Percentage Change
- **Problem:** 15% revenue growth sounds great, but margins collapsed
- **Solution:** Always look at absolute $ impact AND percentage point impact
- **Example:** Revenue +$10M (15%) but Gross Margin -5 pp = Problem

### Pitfall 2: Ignoring Scale Differences
- **Problem:** "Revenue grew 50%" sounds better than "Revenue grew 15%"
- **Solution:** Adjust for base (growing from $100k to $150k is easier than $1B to $1.15B)
- **Example:** Small company: +50% growth (hard); Large company: +5% growth (respectable)

### Pitfall 3: Not Considering One-Time Items
- **Problem:** Big % changes that won't repeat
- **Solution:** Identify one-time gains/losses, remove for trend analysis
- **Example:** One-time sale of building creates +30% net income; adjust for recurring earnings

### Pitfall 4: Seasonal Misinterpretation
- **Problem:** Q4 revenue up 40% vs Q3, but Q4 is always peak season
- **Solution:** Compare to prior-year same quarter (Q4 vs Q4), not sequential
- **Example:** Retail: Q4 2024 vs Q4 2023 shows real growth; Q4 vs Q3 is seasonal

### Pitfall 5: Missing Composition Shifts
- **Problem:** Absolute change +$5M but percentage points show negative shift
- **Solution:** Use all three metrics to catch composition issues
- **Example:** Product A revenue +$10M but as % of total down (other products grew more)

---

## Connecting to Business Drivers

### Understanding What Changed the Metrics

**Use the three metrics to diagnose:**

| Metric | Improvement | Deterioration | Investigation |
|--------|------------|---------------|-----------------|
| **% Change** | Revenue +10% but COGS +15% | Costs rising faster than revenue | Supply chain issues? Product mix? Efficiency losses? |
| **pp Change** | Gross Margin +2 pp | Gross Margin -2 pp | Price increases? Cost management? Sales mix? |
| **$** | COGS +$5k on revenue +$50k | COGS +$50k on revenue +$10k | Structural cost issues? Volume inefficiencies? |

---

## Period-to-Period Tracking Framework

### Recommended Analysis Structure:

```
1. Calculate Absolute Change first
   ├─ Identify biggest $ winners and losers
   └─ Focus on material items only

2. Calculate Percentage Change second
   ├─ See which % metrics changed most
   └─ Compare to revenue % change

3. Calculate Percentage Point Change third
   ├─ Understand composition shifts
   └─ Identify margin compression/expansion

4. Synthesize the story
   ├─ Is performance healthy?
   ├─ Are changes sustainable?
   └─ What actions needed?
```

---

## Multi-Year Trending (Advanced)

### Analyze Trend Over 3-5 Years:

| Metric | FY21 | FY22 | FY23 | FY24 | Trend |
|--------|------|------|------|------|-------|
| Revenue $ | 400k | 450k | 500k | 550k | ↗ Consistent growth |
| Revenue % chg | — | +12.5% | +11.1% | +10% | ↘ Growth slowing |
| Gross Margin % | 38% | 38% | 40% | 40% | ↗ Improving, stable |
| Net Margin % | 10% | 10.5% | 12% | 12% | ↗ Improving, stable |

**Interpretation:** Growth slowing but profitability stable = Mature, well-managed business

---

## Next Steps

### If Changes are Positive ✅
- Compare to industry benchmarks
- Analyze sustainability (is it repeatable?)
- Use period data to project forward

### If Changes are Mixed 🟡
- Drill down on each line item
- Understand the story (growth? efficiency? mix?)
- Plan next actions

### If Changes are Negative ⚠️
- Investigate root causes
- Review management commentary
- Analyze deeper with other skills

---

## Related Skills

- **Quick Liquidity Analysis:** Did liquid position improve/decline?
- **Capital Structure Analysis:** Is leverage trending up/down?
- **Common-Size Balance Sheet:** Understand composition changes better
- **Financial Health Dashboard:** Full picture of all changes

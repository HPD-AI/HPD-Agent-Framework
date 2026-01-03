# Financial Health Dashboard - SOP

## Overview
Comprehensive financial analysis orchestrating all foundation skills plus balance sheet validation for complete health assessment.

---

## When to Use This Skill

- **Board reporting** - Complete picture for executives
- **Investment committee** - Major funding decisions
- **Credit assessment** - Bank loan evaluation
- **Annual financial review** - Full audit of health
- **Investor due diligence** - Equity/debt investment analysis
- **Stakeholder reporting** - Complete transparency
- **Complex assessment** (30-45 minutes depending on depth)

---

## Prerequisites

- Complete financial statements (Balance Sheet + P&L)
- All balance sheet details (asset/liability/equity breakdown)
- Multi-period data (current + prior year for trends)
- Knowledge of business model and industry
- Context on any one-time items

---

## Five-Skill Orchestration Framework

### Dashboard Structure: Layered Analysis

```
Level 1: Data Validation
  ↓ ValidateBalanceSheetEquation
  ↓ (If invalid, STOP and investigate)
  ↓

Level 2: Foundation Skills (Run All Four)
  ├─ Quick Liquidity Analysis
  ├─ Capital Structure Analysis
  ├─ Period Change Analysis
  └─ Common-Size Balance Sheet
  ↓

Level 3: Synthesis
  ├─ Integrate findings
  ├─ Identify connections
  └─ Build overall assessment
  ↓

Level 4: Health Score & Recommendations
  └─ Overall financial health rating
  └─ Risk assessment
  └─ Action items
```

---

## Step-by-Step Procedure

### Step 0: Data Validation (CRITICAL)

**Function Call:** `FinancialAnalysisToolkit.ValidateBalanceSheetEquation(totalAssets, totalLiabilities, totalEquity)`

**Validation:**
```
Check: Total Assets = Total Liabilities + Total Equity

If invalid:
  STOP ANALYSIS
  Investigate data quality
  Fix data source
  Restart analysis
```

**Example (Acme Corp):**
```
Assets:              $1,000,000
Liabilities:         $  550,000
Equity:              $  450,000
Sum (L + E):         $1,000,000

Check: $1,000,000 = $1,000,000  VALID

Proceed to foundation skills
```

**Why This Matters:**
- Invalid = Accounting error (likely data quality issue)
- Invalid = Can't trust the numbers
- Must fix before proceeding
- Common causes:
  - Manual data entry errors
  - System export issues
  - Adjustments not posted
  - Consolidated accounting issues

---

### Step 1: Run Liquidity Skill

**Function Call:** `FinancialAnalysisToolkit.CalculateCurrentRatio(...), CalculateQuickRatio(...), CalculateWorkingCapital(...)`

**Input:** Current Assets, Current Liabilities, Inventory

**Output: Liquidity Profile**

| Metric | Value | Assessment |
|--------|-------|-----------|
| Current Ratio | 1.79 |  Healthy |
| Quick Ratio | 1.14 |  Healthy |
| Working Capital | $110,000 |  Good cushion |
| **Liquidity Rating** | | **STRONG  ** |

**Interpretation in Dashboard Context:**
- Can company meet short-term obligations? **YES**
- Is there immediate risk? **NO**
- Proceed with confidence to longer-term analysis

**Red Flags at This Stage:**
- Current Ratio < 1.0 = Can't cover obligations
- Quick Ratio < 0.5 = Inventory dependency critical
- Working Capital negative = Deficit position
→ If any red flag: Investigate immediately before proceeding

---

### Step 2: Run Capital Structure Skill

**Function Call:** `FinancialAnalysisToolkit.CalculateDebtToEquityRatio(...), CalculateDebtToAssetsRatio(...), CalculateEquityMultiplier(...), EquityToTotalAssetsPercentage(...)`

**Input:** Total Assets, Total Liabilities, Total Equity, Debt

**Output: Leverage Profile**

| Metric | Value | Benchmark | Assessment |
|--------|-------|-----------|-----------|
| D/E Ratio | 1.0 | 0.5-0.8 | 🟡 Above peers |
| D/A Ratio | 40% | 30-35% | 🟡 Above peers |
| Equity Multiplier | 2.5x | 1.8-2.0x | 🟡 Elevated |
| Equity % | 45% | 55-60% | 🟡 Below peers |
| **Leverage Rating** | | | **BALANCED 🟡** |

**Interpretation in Dashboard Context:**
- Is company at financial risk? **Not yet, but elevated**
- How much borrowing capacity? **Limited**
- Sensitivity to downturn? **Moderate - more affected than peers**

**Connection to Liquidity:**
- Strong liquidity + Elevated leverage = Can service debt currently
- But elevated leverage + weak liquidity = Problem
- In this case: Strong position despite leverage  

**Red Flags at This Stage:**
- D/E > 2.5 = High leverage
- D/A > 60% = Creditor-heavy structure
- Equity < 30% = Thin equity cushion
- Rising trend = Deteriorating position

---

### Step 3: Run Period Change Skill

**Function Call:** `FinancialAnalysisToolkit.CalculateAbsoluteChange(...), CalculatePercentageChange(...), CalculatePercentagePointChange(...)`

**Input:** Current Period Values, Prior Period Values (all line items)

**Output: Trend Profile**

| Metric | Absolute | % Change | pp Change | Trend |
|--------|----------|----------|-----------|-------|
| Revenue | +$75,000 | +15% | N/A | ↗ Strong |
| COGS | +$45,000 | +15% | 0 pp | ↗ Proportional |
| Net Income | +$9,000 | +15% | 0 pp | ↗ Proportional |
| D/E Ratio | 0.0 | 0% | N/A | → Stable |
| **Trend Rating** | | | | **POSITIVE  ** |

**Interpretation in Dashboard Context:**
- Is business growing? **YES - solidly**
- Sustainable growth? **YES - balanced**
- Improving or deteriorating? **Improving**
- Direction for future? **Positive**

**Connection to Other Metrics:**
- Growth + stable leverage = Financial position strengthening
- Growth + rising leverage = Need to monitor
- Decline + rising leverage = Major warning
- In this case: Positive trajectory  

**Red Flags at This Stage:**
- Revenue declining but expenses rising
- Margins compressing (pp changes negative)
- Leverage rising while profitability declining
- Cash position worsening despite revenue growth

---

### Step 4: Run Common-Size Skill

**Function Call:** `FinancialAnalysisToolkit.CalculateCommonSizePercentage(...), CommonSizeBalanceSheetAssets(...), CommonSizeBalanceSheetLiabilities(...), EquityToTotalAssetsPercentage(...)`

**Input:** All Balance Sheet items

**Output: Composition Profile**

| Item | % of Total | Benchmark | Assessment |
|-----|-----------|-----------|-----------|
| Current Assets | 30% | 25-35% |  In range |
| Fixed Assets | 50% | 50-65% |  In range |
| Current Liab | 20% | 15-25% |  In range |
| LT Debt | 30% | 20-35% |  In range |
| Equity | 45% | 45-65% |  In range |
| **Structure Rating** | | | **TYPICAL  ** |

**Interpretation in Dashboard Context:**
- Is company structured like peers? **YES**
- Any unusual composition? **NO**
- Red flags in structure? **NONE**

**Connection to Other Metrics:**
- Typical structure + strong liquidity = Good financial position
- Typical structure + weak liquidity = Liquidity is the issue (not structure)
- Unusual structure + weakness = Structure and composition issues
- In this case: Typical structure, strong fundamentals  

**Red Flags at This Stage:**
- 40%+ in "Other" assets (what are they?)
- Intangibles > 30% (goodwill impairment risk)
- Current Liab > 50% (maturity mismatch)
- Extreme deviation from peer structure

---

### Step 5: Synthesis and Health Dashboard

**Integrate All Findings:**

```
FINANCIAL HEALTH DASHBOARD - ACME MANUFACTURING
Assessment Date: 12/31/2024
Industry Comparison: Manufacturing Peers

     

SECTION 1: DATA QUALITY
───────────────────────
Balance Sheet Equation Check:   VALID
Assets = Liabilities + Equity: $1,000,000 = $1,000,000
Data Quality Rating:            CLEAN


SECTION 2: LIQUIDITY ASSESSMENT
────────────────────────────────
Current Ratio:                 1.79  (Healthy; peer avg: 1.5)
Quick Ratio:                   1.14  (Healthy; peer avg: 1.0)
Working Capital:               $110k (Good cushion)

Liquidity Rating:               STRONG
Meaning:                       Can meet short-term obligations comfortably
Risk Level:                    LOW
Trend:                         Stable/Improving


SECTION 3: SOLVENCY ASSESSMENT
───────────────────────────────
D/E Ratio:                     1.0   (Elevated; peer avg: 0.6)
D/A Ratio:                     40%   (Elevated; peer avg: 30%)
Equity Multiplier:             2.5x  (Above peer average: 2.0x)
Equity % of Assets:            45%   (Below peer avg: 55%)

Solvency Rating:               🟡 BALANCED
Meaning:                       More leveraged than peers but manageable
Risk Level:                    MODERATE
Capacity for More Debt:        Limited - already above peer leverage


SECTION 4: PROFITABILITY & GROWTH
──────────────────────────────────
Revenue Growth:                +15% YoY
Gross Margin:                  40%  (Stable from prior year)
Net Margin:                    12%  (Stable from prior year)

Growth Rating:                  STRONG
Meaning:                       Healthy organic growth with stable margins
Sustainability:                Favorable trajectory
Direction:                     Improving


SECTION 5: BALANCE SHEET STRUCTURE
───────────────────────────────────
Asset Composition (% of Total):
  Current Assets:              30%   (vs. peer avg: 28% - similar)
  Fixed Assets:                50%   (vs. peer avg: 55% - slightly lower)
  Intangibles:                 5%    (vs. peer avg: 4% - similar)
  Other:                       15%   (vs. peer avg: 13% - similar)

Liability Composition (% of Total):
  Current Liab:                20%   (vs. peer avg: 18% - similar)
  LT Debt:                     30%   (vs. peer avg: 25% - elevated)
  Other Liab:                  5%    (vs. peer avg: 7% - similar)

Structure Rating:               TYPICAL
Meaning:                       Aligned with peer structure
Red Flags:                     NONE
Composition Changes from Prior: Minimal (stable structure)


     

OVERALL FINANCIAL HEALTH SCORE
───────────────────────────────
Liquidity:                      STRONG    (85/100)
Solvency:                      🟡 BALANCED  (70/100)
Growth:                         STRONG    (80/100)
Structure:                      HEALTHY   (85/100)

COMPOSITE SCORE:               78/100   GOOD FINANCIAL HEALTH

     

RISK ASSESSMENT
───────────────

Financial Distress Probability:
  Immediate (0-12 months):     LOW
  Medium-term (1-3 years):     LOW-MODERATE
  Long-term (3+ years):        MODERATE

Stress Test - Recession Scenario:
  If revenue declined 20%:
    ├─ Current Ratio would decline to: 1.35 (still healthy)
    ├─ D/E would increase to: 1.25 (manageable)
    └─ Risk: Would need to reduce costs or seek financing

Stress Test - Rising Rates Scenario:
  If interest rates up 2%:
    ├─ Additional interest expense: ~$8,000 annually
    ├─ Impact on net income: -12% (minor)
    └─ Risk: Moderate - limited impact


KEY STRENGTHS
─────────────
1.  Strong liquidity position - can weather short-term challenges
2.  Stable and improving profitability
3.  Balanced growth with margin stability
4.  Typical industry structure - aligned with peers
5.  Valid balance sheet - clean data


AREAS OF CONCERN
────────────────
1. 🟡 Above-average leverage vs. peers (D/E 1.0 vs 0.6)
2. 🟡 Limited debt capacity - already leveraged
3. 🟡 Sensitivity to economic downturn elevated
4. 🟡 Interest rate exposure (30% LT Debt)


RECOMMENDED ACTIONS
───────────────────

Short-term (0-3 months):
  □ Continue monitoring liquidity (current position healthy)
  □ Track receivables and inventory (working capital management)
  □ Monitor revenue/margin trends (continue positive trajectory)

Medium-term (3-12 months):
  □ Develop debt reduction plan (bring D/E to peer average 0.7)
  □ Evaluate capital allocation:
    ├─ Reduce debt? (strengthen balance sheet)
    ├─ Invest in growth? (use strong cash generation)
    └─ Return capital? (unlikely given elevation targets)
  □ Assess interest rate hedging (if material debt exposure)

Long-term (12+ months):
  □ Strategic financing review (target D/E of 0.7)
  □ Capital structure optimization
  □ Industry monitoring (are peers changing leverage?)


STAKEHOLDER RECOMMENDATIONS
────────────────────────────

For Lenders:
   Approve credit within normal parameters
   Monitor leverage ratio as covenant
     Note above-average leverage vs. peers
  Recommendation: APPROVE with elevated leverage covenant


For Equity Investors:
   Solid financial position
   Strong growth trajectory
   Reasonable risk/reward
     Limited debt capacity (constrains growth capex)
  Recommendation: BUY with moderate conviction


For Company Management:
   Strong operational performance
   Continue revenue growth strategy
     Prioritize debt reduction when opportunities arise
  Recommendation: Maintain course, target debt reduction


     
```

---

## Dashboard Components Deep-Dive

### Component 1: Liquidity Waterfall

**Visual Representation:**

```
Cash Position Analysis:
  Operating Cash:              $60,000
  Receivables (liquid):        $120,000
  Inventory (less liquid):     $100,000
  Prepaid (illiquid):          $20,000
                              ──────────
  Total Current Assets:        $300,000

  Less: Current Liabilities:   $200,000
                              ──────────
  Net Working Capital:         $100,000

Visual:
  ████████░░  Current Assets (80% of buffer available)
  ████░░░░░░  Current Liabilities (covered 1.5x)
```

---

### Component 2: Leverage Comparison

**Peer Benchmarking Chart:**

```
Company vs. Industry

D/E Ratio:
  Acme:           ████████░  (1.0)
  Industry Avg:   ████░░░░░  (0.6)
  High Performers:████░░░░░░ (0.4)
  High Fliers:    ████████████████ (1.5)

Position: Above average, but acceptable
```

---

### Component 3: Growth Trajectory

**Trend Analysis:**

```
Revenue Trend (3-Year):
  FY22: $400k
  FY23: $500k  (+25%)
  FY24: $575k  (+15%)

Interpretation:
  • Growth decelerating (25% → 15%)
  • But still strong (15% well above inflation)
  • Margins stable (profitability keeping pace)
  • Trajectory: Sustainable  
```

---

### Component 4: Stress Test Matrix

**Scenario Analysis:**

```
                    Base Case    -20% Revenue   +2% Rates    Combined
Current Ratio         1.79          1.35          1.79        1.35
D/E Ratio            1.0           1.25          1.0         1.25
Net Margin           12%            8%           11%          7%
Distress Risk        LOW           MODERATE      LOW         MODERATE

Conclusion: Company resilient to single shocks, stressed by combined
```

---

## Common Dashboard Interpretations

### Scenario A: Healthy Company  

```
Liquidity:         STRONG (CR > 1.5)
Leverage:          STRONG (D/E < 0.7)
Growth:            STRONG (Revenue + and stable margins)
Structure:         TYPICAL (aligned with peers)

Result:            LOW RISK - Good lending/investment candidate
Action:           GREEN LIGHT for credit/investment
```

### Scenario B: Growing Company Stretching 🟡

```
Liquidity:         STRONG (but declining trend)
Leverage:         🟡 RISING (D/E increasing)
Growth:            STRONG (but capex heavy)
Structure:        🟡 CHANGING (shifting to growth mode)

Result:           🟡 MODERATE RISK - Monitor closely
Action:           PROCEED WITH CAUTION - Monitor quarterly
```

### Scenario C: Troubled Company  

```
Liquidity:           WEAK (CR < 1.0)
Leverage:            HIGH (D/E > 2.0)
Growth:              NEGATIVE (Revenue declining)
Structure:           DETERIORATING (Equity shrinking)

Result:              HIGH RISK - Serious concerns
Action:           RED LIGHT - Decline credit/divest
```

---

## Multi-Year Dashboard Tracking

### Year-over-Year Dashboard Comparison

| Metric | FY22 | FY23 | FY24 | Trend | Status |
|--------|------|------|------|-------|--------|
| **Liquidity** | | | | | |
| Current Ratio | 1.8 | 1.8 | 1.79 | → Stable |  |
| **Solvency** | | | | | |
| D/E Ratio | 0.85 | 0.92 | 1.00 | ↗ Rising | 🟡 |
| **Growth** | | | | | |
| Revenue Growth | — | +25% | +15% | ↘ Slowing |  |
| **Health Score** | 75/100 | 76/100 | 78/100 | ↗ Improving |  |

**Multi-Year Interpretation:**
- Liquidity stable (consistent financial strength)
- Leverage rising (need to monitor - D/E approaching 1.0)
- Growth healthy but moderating (maturing business)
- Overall health improving (strong operational execution)

---

## Dashboard Presentation

### Executive Summary Version (1 page)

```
FINANCIAL HEALTH - EXECUTIVE SUMMARY
====================================
Company:     Acme Manufacturing
Period:      FY 2024 Ending 12/31/2024
Audience:    Board of Directors

HEADLINE:     STRONG FINANCIAL POSITION
             Healthy liquidity, manageable leverage, solid growth

KEY METRICS
───────────
Liquidity:       1.79 Current Ratio (Strong)
Leverage:        1.0 D/E Ratio (Balanced)
Profitability:   12% Net Margin (Stable)
Growth:          15% Revenue Growth (Healthy)

OVERALL SCORE:   78/100 - GOOD FINANCIAL HEALTH

RECOMMENDATION:  CONTINUE CURRENT STRATEGY
                 Monitor leverage development
                 Focus on debt reduction opportunity
```

### Detailed Version (3+ pages)

See full dashboard above with all components

---

## Related Analysis

### What to Do After Dashboard Assessment

**If Financial Health is Strong  **
- Approve credit/investment with confidence
- Consider increased financing capacity
- Monitor annually for changes

**If Financial Health is Mixed 🟡**
- Understand the specific concerns
- Set monitoring frequency (quarterly)
- Develop improvement plan with management
- Reassess in 6-12 months

**If Financial Health is Weak  **
- Conduct deeper investigation
- Assess turnaround probability
- Determine recovery strategy
- Consider position reduction

---

## Next Steps After Dashboard

1. **Board Reporting** - Present findings to governance
2. **Management Discussion** - Discuss recommendations
3. **Action Planning** - Develop implementation plan
4. **Monitoring Framework** - Set review cadence
5. **Drill-Down Analysis** - Investigate specific concerns if needed

---

## Related Skills

- **Quick Liquidity Analysis:** Detailed liquidity deep-dive
- **Capital Structure Analysis:** Detailed leverage analysis
- **Period Change Analysis:** Detailed growth analysis
- **Common-Size Balance Sheet:** Detailed structure analysis

Use this dashboard for initial assessment, then use individual skills for deep dives on areas of concern.

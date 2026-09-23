# KODEX200 Band-Trading Strategy: Redesigning a System from 6.6 Years of Live Data

**A performance ceiling investigation — hypothesis testing, rejection, root-cause analysis, and redesign**

---

## 1. The problem

A band-based automated trading system for the KODEX200 ETF (tracks the KOSPI 200 index) had been
running live since 2018. Across good years, its return consistently topped out around **+7–8%** —
a ceiling the system never broke through, regardless of market conditions. The first question was
simple: *what's causing the ceiling?*

To answer it, I pulled the full live-trading database — **943 trading days, January 2018 to August
2024** — rather than relying on assumptions about the strategy's design.

**What the 6.6-year record actually showed:**

![Legacy system vs benchmark](assets/fig1_legacy_vs_benchmark.png)

| Metric | Legacy system | Buy & hold (KODEX200) |
|---|---|---|
| Total return (6.6 yrs) | **−3.9%** | **+9.7%** |
| Max drawdown | **−30.6%** | — |
| Best year | +7.7% | — |
| Worst year (2022) | −18.2% | — |
| Worst year (2023) | −13.4% | (KODEX200 itself: +13.2% that year) |

The "ceiling" was real, but it was only half the story: the system had an **upside cap and no
downside floor**. Good years capped near 8%; bad years had no equivalent limit. Something asymmetric
was going on, and it needed a real explanation before any redesign could be justified.

---

## 2. Hypothesis 1 — "Chain trading" (rejected)

The first idea I proposed: when the lowest held band hits a full liquidation point, instead of
liquidating, force-sell a *higher* band to fund an extension of the lower boundary — keeping the
system in the market instead of fully exiting.

Before writing any code, I worked through the breakeven math:

- If band 1 is force-sold at price *P* to fund the extension, and the market later recovers, band 1
  can only be "made whole" by buying back at or below *P* plus whatever profit the extension itself
  generated.
- That condition **cannot be satisfied on the way back up** — by the time the recovery is confirmed,
  price has already moved past the point where repurchase would be profitable.
- Two variations on the idea (buy back immediately regardless of price; abandon recovery and treat
  the reallocation as permanent) were checked the same way and were **both structurally loss-making**
  for the same reason: neither could avoid selling low and buying back higher, or giving up the
  position outright.

**Conclusion: rejected.** All three variants forced a loss under normal recovery scenarios. This was
caught analytically, before any implementation time was spent on it — the reasoning itself is the
deliverable here, not a piece of code.

**What replaced it:** instead of force-selling other positions to fund expansion, the system
accumulates a **reserve** from realized profits over time, and only expands the lower boundary when
the reserve can cover it. If the reserve is insufficient, the system simply skips the expansion —
a missed opportunity, never a forced loss.

---

## 3. Finding the real cause

With the flawed fix rejected, the 6.6-year dataset was used to test what was *actually* driving the
ceiling. The asymmetry between good and bad years pointed at capital, not the band-width parameter
that had originally been suspected:

- In the 2022 decline, the system ran out of capital to keep buying as price fell through its bands —
  it simply couldn't average down any further.
- In the 2023 rebound (KODEX200 +13.2%), it had no inventory left to sell, having been forced into a
  starved position the year before.

**The ceiling was a capital-allocation problem, not a strategy-design problem.** This reframed the
entire redesign: the fix wasn't a smarter band, it was making sure the system could never run out of
capital across the full price range it might see.

The redesign target: **₩100M capital, deployed in full, with a band width wide enough that even a
decline to ₩0 wouldn't exhaust it** (a deliberately extreme lower bound, chosen so 2022-style capital
exhaustion becomes structurally impossible).

---

## 4. Validating a sell-timing improvement on real minute-level data

Separately from the capital fix, I tested whether the *sell-timing rule itself* could be improved.
The original design sold one unit at each band as price rose. The alternative: **hold through
multiple bands and sell the accumulated position only when price reverses** ("breakthrough–reversal
sell") — capturing more of a sustained rally instead of trimming it away band by band.

To test this properly, I collected **91,247 rows of real 1-minute price data** (241 trading days,
zero missing bars) rather than relying on daily-bar approximations. This mattered: the actual
intraday-to-daily-range ratio measured out to **9.4×**, more than 3× larger than the 3.0× assumption
used in an earlier synthetic backtest — meaning prior return estimates for any sell-timing comparison
had been running on the wrong volatility assumption entirely.

**Result, same one-year test window:**

![Breakthrough-reversal vs band-by-band sell](assets/fig2_breakthrough_vs_bandsell.png)

The breakthrough–reversal rule outperformed the best band-by-band configuration across *every* band
width and reversal-threshold combination tested — not just on average, with roughly **30% fewer
trades** as well.

One constraint had to be derived before the reversal threshold (*R*) could be set: if a position is
bought at band *B* and sold on a reversal of size *R* after rising only one band width *W*, the trade
only nets a profit if *R < W* — otherwise a single-band round trip is a loss by construction. Reversal
threshold and band width can't be chosen independently; they have to be set together.

---

## 5. Setting the final band-width rule

With the real root cause identified (§3) and the sell-timing improvement validated (§4), the last
piece was the band-width parameter itself. Three approaches were compared head-to-head under
identical conditions:

| Approach | Result |
|---|---|
| Fixed ₩ amount (e.g. ₩300) | Only performed well in 2003–2009 back-data, where that amount happened to represent 2.65% of price — the same ₩300 is 0.28% of the current price, ten times thinner. Rejected as an artifact of the backtest period. |
| ATR(10)-linked width | Statistically identical performance to price-ratio width, but requires daily recalculation for no measurable benefit. Dropped for simplicity. |
| **Price-ratio width (final)** | Same performance as ATR-linked, far simpler: **band width = current price × 0.5–1.0%** |

Guardrails on the ratio were set from execution safety, not backtest curve-fitting: a **0.3% floor**
(any narrower and cost erodes too much of the edge — an earlier 0.15% floor was tightened after an
independent cross-check flagged the cost/profit ratio as too thin) and a **2.0% ceiling** (beyond
that, trade frequency drops below ~100/year, into overfitting territory).

I also tested one more idea — dynamically widening the grid further as price fell deeper below −20%
("deep zone" expansion). It improved CAGR slightly (3.61% → 3.85%) but **worsened max drawdown**
(−22.6% → −27.7%), because funding it thinned out the primary defensive allocation. **Rejected** —
the same discipline as §2: test the idea, and let the data say no if it says no.

---

## 6. Cross-validation

Before finalizing, I had the statistical conclusions above independently reviewed by a second AI
system (Gemini) against six specific questions — including whether the price-ratio band-width
approach was justified, whether the 0.15% floor was safe, and how reliable a 24-year *daily-bar*
synthetic backtest actually is for this purpose. The review agreed with the overall approach but
flagged the 0.15% floor as too thin on a cost/profit basis; re-testing at 0.3% showed almost no
performance cost (3.44% → 3.52% CAGR), so the floor was raised.

---

## Summary

| Stage | Outcome |
|---|---|
| Hypothesis 1: chain trading | Rejected via breakeven math, before implementation |
| Root cause | Capital exhaustion in downturns — not band width (found via 6.6-yr live data) |
| Sell-timing rule | Breakthrough-reversal validated on real 1-min data — ~3× vs best band-sell config |
| Band-width rule | Price-ratio (0.5–1.0%, floor 0.3%, ceiling 2.0%) — matches ATR performance, simpler |
| Deep-zone expansion | Tested and rejected — CAGR gain not worth the drawdown cost |
| External check | Cross-validated against an independent AI review; one parameter revised as a result |

Code and live-system logic: [`kodex200-trading`](https://github.com/1908hanjm/kodex200-trading)

---

*Note on scope: return figures above come from historical backtests and a live-trading record on a
specific ETF and time window. They describe what happened in that data, not a forecast or investment
recommendation.*

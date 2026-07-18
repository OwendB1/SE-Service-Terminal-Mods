---
type: "query"
date: "2026-07-18T12:23:54.834378+00:00"
question: "Track ramming, self grinding, faction-member grinding, faction-owned block damage, and other insurance damage types as separate pricing inputs"
contributor: "graphify"
outcome: "useful"
source_nodes: ["InsuranceRuntime", "DamageAttribution", "ClaimQuote"]
---

# Q: Track ramming, self grinding, faction-member grinding, faction-owned block damage, and other insurance damage types as separate pricing inputs

## Answer

Expanded from original query via graph vocabulary: damage, damaged, attacker, claim, grid, insurance. Current InsuranceRuntime records damage incidents and latest per-block attribution, while BuildQuote prices all covered loss through one ClaimValueFraction. Recommended persistent orthogonal classification: damage cause plus actor relationship; weight each repair item's component-value loss by its attribution. Key engine caveats: Deformation with a grid attacker indicates ramming; Grind is explicit; hand grinders report tool entity IDs, so ResolveAttacker must also resolve handheld tool ownership; unknown removal needs a configured fallback.

## Outcome

- Signal: useful

## Source Nodes

- InsuranceRuntime
- DamageAttribution
- ClaimQuote
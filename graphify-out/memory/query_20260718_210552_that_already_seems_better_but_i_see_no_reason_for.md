---
type: "query"
date: "2026-07-18T21:05:52.399772+00:00"
question: "That already seems better but I see no reason for us to modify the controls. Why are we modifying the controls specifically?"
contributor: "graphify"
outcome: "useful"
source_nodes: ["InsuranceTerminalControls", "InsuranceRichHudWindow"]
---

# Q: That already seems better but I see no reason for us to modify the controls. Why are we modifying the controls specifically?

## Answer

Expanded from original query via graph vocab: [control, controls, terminal, visible, remove, register, enabled, toggle, custom, button, action, create]. InsuranceTerminalControls modifies Keen's terminal registry only to provide a secondary fallback UI. InsuranceRichHud still needs the class as a mediator for selection, networking, and actions, but does not need Register(). Since ServicesTerminal is FunctionalBlock/ServicesTerminal, type-based registration targets the shared IMyFunctionalBlock registry and filters by subtype afterward. The minimal fix is to stop registering/removing vanilla terminal controls while retaining the mediator.

## Outcome

- Signal: useful

## Source Nodes

- InsuranceTerminalControls
- InsuranceRichHudWindow
# Pilot feasibility and business model

**Decision.** Offer an on-premises, read-only production rescheduling pilot for a single plant that already records orders, processing times, material receipts and machine availability. The buyer hypothesis is an operations manager with a planner who can explain past disruptions. Start with one planning horizon, one site, synthetic data followed by authorized historical data. The delivered product is a proposed schedule, a comparison to the current plan and a trace for human review. No PLC actuation is part of the pilot.

| Candidate | Financial measure | Pilot data burden | Main confounder | Decision |
|---|---|---|---|---|
| Finite-capacity production | Observed lateness cost, setup/overtime if supplied | Orders, routing, materials, calendars | Unrecorded setups and machine flexibility | First, cost links to actual operations |
| Workforce planning | Shift premiums and coverage shortfall | Shifts, skills, availability, synthetic staff identifiers | Local policies, personal data | Second proof of generality on synthetic inputs |
| Inventory replenishment | Stockout and holding cost | Demand history, lead times, inventory movements | Estimation error and seasonality | Later after uncertainty backtesting |
| Transport/logistics | Travel cost and late deliveries | Locations, time windows and travel matrix | Travel-time quality | Later, no paid routing dependency |
| Financial/cash or clinical decision support | Cash exposures or patient outcomes | Sensitive data and high governance burden | Attribution and approval risk | Defer |

**Measured pilot.** Freeze a chronological holdout of production days including outages. Have a planner identify material units, calendars, hard constraints and costs before evaluation. Compare planner history, deterministic earliest due date with stable ID ties and exact/limited solve under equal input knowledge. Report verified feasibility, weighted tardiness, schedule changes, planner preparation minutes, time to first feasible, certificate/gap where available and number of rejected imports. Report timeouts and missing costs separately. Reconstructed counterfactual savings are estimates, not cash realized. Run in shadow mode before approval. Decline unsupported safety or material rules.

| Stage | Evidence to pass | Explicitly deferred |
|---|---|---|
| Research prototype | Independent small oracle and falsification cases repeat on command | Customer savings, general solver performance, commercial readiness |
| Sellable MVP | Import/export, one-site scheduling, operator-reviewed alternative, evidence package, Windows/Linux CLI and Studio qualification, data contract and a pilot buyer | Automatic writeback and broad connector catalog |
| Enterprise | Tenant isolation, OIDC, approval scope, backup/restore, operations, support, SLO and audit review with customer | Unvalidated scale or new industries |
| Mature platform | Independent sector uses same core, measured portfolio/decomposition benefits, versioned third-party extensions and capacity evidence | Generality outside the supported languages and adapters |

**Illustrative cost worksheet (planning assumptions, not market quotes).** Three input mappings × 1–3 engineer-days, calendar/material reconciliation 3–8 days, historical replay and acceptance 5–12 days: 11–29 engineer-days of pilot integration after a runnable product. At a hypothetical internal cost of USD 200/day this is USD 2,200–5,800, excluding core engineering, customer labor, hardware, tax and ongoing support. Example core development budget: 2 engineers × 60 working days × USD 200 = USD 24,000; discovery and scope can change it materially. Track actual delivery time per mapping and per support event to replace every assumption.

SaaS monthly cost = reserved hosting + storage/egress + observed core-hours × effective compute rate + monitoring + support time. Example variable solve cost at 400 runs × 2 core-minutes / 60 × USD 0.10 per core-hour = USD 1.33 before overhead. This illustrates units only; raw solve cost does not predict total price. On-prem costs include customer hardware and operations plus vendor packaging, supported OS testing, updates and offline support. SaaS hypothesis: per site subscription with a pooled compute allowance. On-prem hypothesis: annual site license plus onboarding/support. Validate willingness to pay separately from estimated savings. Break-even sites = ceiling(fixed monthly cost / (monthly fee − measured variable cost per site)), valid only for positive contribution margin.

**Decision risks.** Missing shop rules → capture planner overrides and extend the schema only with reference fixtures. No improvement against dispatch → sell no savings claim, stop or narrow scope. License/ABI gaps → disable the adapter. Dense coupling → revert to full solve. Integration overrun → test one CSV snapshot before ERP access. Governance risk → require evidence-linked approval and no external writes in pilot. Discovery gates require a willing customer; no interview, quotation or willingness to pay has been verified.

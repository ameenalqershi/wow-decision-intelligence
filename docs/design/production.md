# Pilot mathematical specification

Let each mandatory operation i have a fixed machine mᵢ, integer duration pᵢ>0, release rᵢ, due date dᵢ, material consumption aᵢₖ at start and tardiness weight wᵢ ≥ 0. Horizon H is finite and calendars define immutable machine downtime. Decision sᵢ is an integer start minute, eᵢ=sᵢ+pᵢ. Closed ticks label starts; actual occupancy is half-open [sᵢ,eᵢ). Input order identifies records but cannot confer priority.

- Require rᵢ ≤ sᵢ and eᵢ ≤ H. Precedence i→j requires eᵢ ≤ sⱼ. Machine intervals cannot intersect each other or downtime intervals.
- For every material k and event tick t, Σᵢ aᵢₖ 1[sᵢ≤t] ≤ initialₖ + Σ receiptsₖ up to and including t. Receipts at t precede starts at t. A horizon-total constraint cannot prevent consumption before receipt.
- For each due date Tᵢ=max(0,eᵢ−dᵢ). Minimize Σ wᵢTᵢ, with units declared per weight and time. Customer-promised hard deadlines, where applicable, are separate hard constraints. Additional setup costs, alternate machines, split batches and changeovers require separate data and qualification.
- Optional second objective minimizes movement from an approved plan only after first-stage optimum is bounded or certified under declared policy. A stability preference never overrides hard safety and capacity.

The C# research fixture has one machine, two independent jobs (durations 3 and 2, dues 4 and 2, weights 3 and 1), one unit of initial stock and one more at tick 3. Each job consumes one unit. H=10. With no outage, A starts at 0 and B at 3, costing 3. During outage [1,3), A starts at 3 and B at 6, costing 12. A separate tick-by-tick oracle enumerates all start pairs and checks the reported bound. Costs are synthetic weighted ticks, not money. The broader model with precedence, multiple materials, calendars and operations is a delivery specification.

First commercial data contract: orders(operationId, jobId, machineId, release, duration, due, weight, materialId, quantity, unit); precedence(predecessor,successor); receipts(materialId,qty,unit,availableAt); calendars(machineId,start,end,timezone); metadata(horizon,clock,objectiveUnit,revision). Store demand, material and costs as source facts separate from model policy. Reject overlapping or missing calendars with actionable row references. Exports: schedule and occupancy, material prefix trace, late operations, objective contributions and evidence.

Second-sector research fixture: buy continuous hours x₀,x₁ ∈ [0,2] at rates 1 and 3 subject to x₀+x₁≥1.5. Objective x₀+3x₁≥x₀+x₁≥1.5; allocation (1.5,0) attains the bound. The demo demonstrates a separate .NET LP backend. Sharing UDIR, implementing actual skills/shifts, and proving cross-sector reuse remain issues V06 and V07.

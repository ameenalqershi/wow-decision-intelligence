# Paired comparison protocol · بروتوكول المقارنة

Each trial is keyed by dataset digest, scenario family, held-out horizon, information cutoff, strategy, seed, host and budget. Freeze training, validation and test by family and time before tuning. Give each arm the same information available at decision time. A retrospective planner schedule can serve as a baseline only if its input availability is documented.

| Arm | Training and execution | Comparison or failure control |
|---|---|---|
| A Human planning | Recorded planner schedule and effort; collect with permission | Missing schedule is a missing arm, never zero cost. |
| B Enterprise-trained model | Fixed training split, architecture and checkpoint; record training and inference compute separately | Evaluate the raw output using the same feasibility checker; report any repair as a separate policy. |
| C Conventional optimization | Cold qualified backend with fixed selection and approved settings | Match formulation semantics, input snapshot, wall budget and allocated cores. |
| D Adaptive engine | Capability-first policy, decomposition, reuse and controlled fallback | Also run ablations disabling each mechanism; charge preprocessing, cache and verification costs. |
| E Adaptive + prediction | Freeze forecast version and prediction interval before the decision cutoff | Compare D and E both with and without distribution shift; isolate the forecast effect. |

Exercise demand shocks, machine outages, budget cuts and resource shortfalls with a held-out perturbation sequence. Verify candidates using original constraints and objectives. Retain timeouts, invalid outputs, missing arms and unsupported instances. Report feasibility, violations, incumbent, objective and gap separately. Financial measures require compatible monetary units and a documented cost model. Never infer actual profit from a reconstructed counterfactual.

Report decision latency, end-to-end CPU, peak memory, training and inference cost, resource utilization, operating cost, profit where defined, adaptation cost, robustness and reproducibility. Score explanation quality against a predeclared rubric for source rows, evidence identity and supported counterfactuals. Use paired family bootstrap intervals; publish raw samples and exclusions. A false certificate fails correctness regardless of speed. Register the promotion threshold before evaluation. This protocol has no five-arm results yet.

لا يجوز تسريب بيانات المستقبل لأي طريقة. تنشر النتائج السلبية والإيجابية والحالات غير القابلة للمقارنة مع سببها.

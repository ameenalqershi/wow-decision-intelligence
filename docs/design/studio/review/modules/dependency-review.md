# Feature integration and acceptance gate review

The official claim graph is unchanged. The following additional relationships are reviewed as integration/acceptance capability gates, not new mandatory global prerequisites. Some are already transitive. Enforcing every edge as a hard claim dependency would prevent independent shell and offline work. The feature owner must identify the supported mode and obtain actual provider evidence before claiming it. No proposed relationship creates a cycle in the current ledger. Send any changes to the audit owner; do not bypass or silently rewrite the issue graph.

| Consumer | Provider | Kind | Graph status | Reason |
|---|---|---|---|---|
| #90 UI01 | #129 RT01 | integration | Additional capability gate for owner review | Authoritative workspace/revision switching; shell-local navigation can be developed independently but real workspace acceptance needs RT01. |
| #90 UI01 | #71 U02 | native acceptance | Additional capability gate for owner review | Native disconnected monitor restoration and window lifecycle are host-owned; browser test alone is insufficient. |
| #90 UI01 | #127 DS03 | native acceptance | Additional capability gate for owner review | Native placement and mixed-scale focus proof; shell layout development must not bypass DS03 qualification. |
| #91 UI02 | #133 RT05 | remote capability | Additional capability gate for owner review | Remote connection and credential lifecycle must be supported before remote acceptance; offline workspace does not depend on remote availability. |
| #92 UI03 | #130 RT02 | enterprise capability | Additional capability gate for owner review | Enterprise job health and live status require authorized host observations; local panels may work separately. |
| #92 UI03 | #58 I04 | integration capability | Additional capability gate for owner review | Connector synchronization health is owned by I04; do not imply healthy from missing data. |
| #93 UI04 | #18 M05 | comparison acceptance | Additional capability gate for owner review | Batch DSL equivalence requires an approved grammar and fixture, not a Studio-invented syntax. |
| #93 UI04 | #20 M07 | comparison acceptance | Additional capability gate for owner review | Normalized equivalent output must come from semantic analysis; structural UDIR alone is insufficient. |
| #94 UI05 | #128 DS04 | presentation reuse | Additional capability gate for owner review | Reuse existing graph projection, never substitute static image. Already closed. |
| #96 UI07 | #29 S04 | evidence capability | Additional capability gate for owner review | Original constraint checks are verifier-owned; conflict proof must be evidence-backed, not inferred from a red form. |
| #99 UI10 | #129 RT01 | local execution | Additional capability gate for owner review | A usable local submit flow needs application runtime admission, not direct solver invocation from UI. |
| #100 UI11 | #129 RT01 | local execution | Additional capability gate for owner review | Local job inspection and cancellation use application runtime. S07/S10 own execution/evidence. |
| #100 UI11 | #128 DS04 | presentation reuse | Additional capability gate for owner review | Convergence primitive is merged; metric projections preserve supplied event sequence. |
| #106 UI17 | #129 RT01 | local execution | Additional capability gate for owner review | Read/export evidence through application runtime, not raw registry or filesystem bypass. |
| #109 UI20 | #58 I04 | integration capability | Additional capability gate for owner review | Full synchronization health/checkpoint capability remains I04-owned; I01/I02 cover declaration and snapshot import. |
| #110 UI21 | #37 S12 | enterprise capability | Additional capability gate for owner review | Actual server route/action admission belongs to S12; UI cannot define administrative authorization independently. |
| #110 UI21 | #76 E02 | enterprise capability | Additional capability gate for owner review | Durable membership and administrative persistence are server-owned; configured identity seam is not a production directory. |
| #110 UI21 | #138 EXT01 | extension capability | Additional capability gate for owner review | Extension management requires approved registry and activation policy; manifest syntax alone does not authorize activation. |
| #110 UI21 | #132 RT04 | SDK capability | Additional capability gate for owner review | Versioned SDK examples must match actual embedded SDK distribution. |
| #111 UI22 | #113 CLI01 | shared command capability | Additional capability gate for owner review | Registered invocation semantics should agree with common command registry; do not execute arbitrary shell. |
| #111 UI22 | #129 RT01 | local capability | Additional capability gate for owner review | Local workspace context and host capabilities are runtime-owned. |
| #112 UI23 | #129 RT01 | local capability | Additional capability gate for owner review | Effective engine/workspace settings and runtime health need actual runtime provider, not mock config. |
| #112 UI23 | #127 DS03 | presentation acceptance | Additional capability gate for owner review | Runtime language and native layout qualification are DS03-owned. |
| #144 QA02 | #71 U02 | native acceptance | Additional capability gate for owner review | Release-level desktop qualification requires host proof; UI shell does not replace native packaging/startup. |

Direct SP02 consumers: 23 Studio issues. Transitive closure also includes QA02, SL01/SL02, Q02/Q03 and G02–G06. The matrix contains their current ledger prerequisites. A specification review is not a release gate pass.

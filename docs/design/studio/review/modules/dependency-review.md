# Accepted Studio integration and acceptance gates

Merged #190 is authoritative: seven hard, one transitive, sixteen capability gates. Capability gates apply only when that mode is claimed. No direct or transitive cycle was found in the accepted graph.

| Consumer | Provider | Disposition | Reason |
|---|---|---|---|
| #90 UI01 | #129 RT01 | capability | Authoritative workspace/revision switching; shell-local navigation can be developed independently but real workspace acceptance needs RT01. |
| #90 UI01 | #71 U02 | capability | Native disconnected monitor restoration and window lifecycle are host-owned; browser test alone is insufficient. |
| #90 UI01 | #127 DS03 | capability | Native placement and mixed-scale focus proof; shell layout development must not bypass DS03 qualification. |
| #91 UI02 | #133 RT05 | capability | Remote connection and credential lifecycle must be supported before remote acceptance; offline workspace does not depend on remote availability. |
| #92 UI03 | #130 RT02 | capability | Enterprise job health and live status require authorized host observations; local panels may work separately. |
| #92 UI03 | #58 I04 | capability | Connector synchronization health is owned by I04; do not imply healthy from missing data. |
| #93 UI04 | #18 M05 | transitive | Batch DSL equivalence requires an approved grammar and fixture, not a Studio-invented syntax. |
| #93 UI04 | #20 M07 | hard | Normalized equivalent output must come from semantic analysis; structural UDIR alone is insufficient. |
| #94 UI05 | #128 DS04 | hard | Reuse existing graph projection, never substitute static image. Already closed. |
| #96 UI07 | #29 S04 | capability | Original constraint checks are verifier-owned; conflict proof must be evidence-backed, not inferred from a red form. |
| #99 UI10 | #129 RT01 | capability | A usable local submit flow needs application runtime admission, not direct solver invocation from UI. |
| #100 UI11 | #129 RT01 | capability | Local job inspection and cancellation use application runtime. S07/S10 own execution/evidence. |
| #100 UI11 | #128 DS04 | hard | Convergence primitive is merged; metric projections preserve supplied event sequence. |
| #106 UI17 | #129 RT01 | capability | Read/export evidence through application runtime, not raw registry or filesystem bypass. |
| #109 UI20 | #58 I04 | capability | Full synchronization health/checkpoint capability remains I04-owned; I01/I02 cover declaration and snapshot import. |
| #110 UI21 | #37 S12 | capability | Actual server route/action admission belongs to S12; UI cannot define administrative authorization independently. |
| #110 UI21 | #76 E02 | capability | Durable membership and administrative persistence are server-owned; configured identity seam is not a production directory. |
| #110 UI21 | #138 EXT01 | capability | Extension management requires approved registry and activation policy; manifest syntax alone does not authorize activation. |
| #110 UI21 | #132 RT04 | hard | Versioned SDK examples must match actual embedded SDK distribution. |
| #111 UI22 | #113 CLI01 | hard | Registered invocation semantics should agree with common command registry; do not execute arbitrary shell. |
| #111 UI22 | #129 RT01 | capability | Local workspace context and host capabilities are runtime-owned. |
| #112 UI23 | #129 RT01 | capability | Effective engine/workspace settings and runtime health need actual runtime provider, not mock config. |
| #112 UI23 | #127 DS03 | hard | Runtime language and native layout qualification are DS03-owned. |
| #144 QA02 | #71 U02 | hard | Release-level desktop qualification requires host proof; UI shell does not replace native packaging/startup. |

[Authoritative dispositions](../../../../../planning/studio-integration-gates.json).

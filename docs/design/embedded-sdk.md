# RT04 — local package consumer

Current scope: managed packaging and consumer conformance, not full embedded optimization. The tool constructs seven internal NuGet packages at the existing default assembly/package version 1.0.0, a local test identity only. It restores the SDK project graph in locked mode, packs the exact project dependencies, and verifies all four externally signed package content hashes with dotnet nuget verify against the committed SDK lock. The SHA-256 manifest records raw archive identities separately from NuGet signed-content SHA-512 hashes. Internal packages are not signed; the generated manifest is a local integrity record, not independent publisher authentication. No package is uploaded or published. Repository licensing/release metadata, package-specific readmes, signing and redistributable native artifacts require separate release review. No public release is implied by version 1.0.0.

The consumer source in samples/EmbeddedSdk uses an exact version PackageReference to WowDi.ModelingSdk, with no ProjectReference and no server or UI. The tool copies it to a fresh output directory outside the checkout in the conformance run, selects only the local feed, restores to an empty package cache, verifies resolved package identities and content hashes, re-restores in locked mode, builds and invokes the resulting executable twice. The consumer's deterministic Batch model uses actual typed SDK APIs, exact frozen facts, the shared normalizer and the schema importer. It proves identical canonical output, incompatible-major contract.major rejection and cancellation. It does not invoke a solver, infer feasibility or certify optimality. The test-only consumer disables online vulnerability auditing to keep the offline restore self-contained; this is not a vulnerability assessment. The online preparation validates signatures with the platform's NuGet trust configuration.

## Reproduce

```sh
dotnet restore tools/EmbeddedSdk --locked-mode
dotnet build tools/EmbeddedSdk -c Release --no-restore
out="$(mktemp -d)/package-run"
tool="$PWD/tools/EmbeddedSdk/bin/Release/net10.0/WowDi.EmbeddedSdk.Pack.dll"
dotnet "$tool" prepare "$PWD" "$out"
sudo unshare -n dotnet "$tool" verify "$PWD" "$out"
```

Use a new output directory each time. Preserve the emitted package-sha256.json and consumer/packages.lock.json together with the local feed for the tested build. The CI workflow runs on Windows and Linux with a fresh cache and deliberately modifies one package to require rejection before restoration. The Linux network-isolated verification runs with no enabled network interface; Windows CI currently validates the local feed, not OS network isolation.

Observed Linux: 11 packages (7 internal, 4 external), signature/content lock matches, fresh restore and locked restore, consumer build zero warnings/errors, two identical results. UDIR SHA-256 8b221bf81fa4942ba33db7265c6a6641d932a151176720a3449ae435997e47ff; normalized SHA-256 056420cdd3539722f14b0a847caa37682bd5a213abb91a6f43e643af4a336aa9. An altered archive failed before creating the package cache. Initial naive raw SHA-512 comparison of signed packages failed; corrected by checking the authoritative signed-content hash through NuGet and separately hashing archive bytes.

## Outstanding acceptance

TC-RT04 requires a package-only consumer to execute a real qualified native solver and independently verify Batch against its original model. Actual embedded cancellation/diagnostics, native artifact distribution and the shared verification/runtime package closure remain outstanding. R07 and S02/S03 are the native qualification gates. Keep #132 active/draft; successful managed package admission is not a solve or release qualification.

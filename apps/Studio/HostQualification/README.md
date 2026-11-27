# U02 — native host qualification (candidate, not production Studio)

Photino.Blazor 4.0.13 is the official upstream package candidate. Pin the
Blazor WebView and Microsoft logging packages to the observed .NET 10
servicing version (10.0.15) rather than retaining the candidate's old
9.0.1 defaults. The package lock resolves Photino.NET 4.0.16 and
Photino.Native 4.0.22. Compatibility must be demonstrated at runtime.

This isolated Razor fixture loads a bundled local JSON record, displays its
revision, switches LTR/RTL and records actual native keyboard events and
DOM direction. It never calls the engine, remote API or a paid service.
The JS file is a test-only DOM inspection bridge. Evidence is produced
only after rendering; the package build is not evidence of native startup.

## Local reproduction

```sh
dotnet restore apps/Studio/HostQualification --locked-mode -p:SelfContained=true
dotnet publish apps/Studio/HostQualification -c Release -r linux-x64 --self-contained true --no-restore -o artifacts/host-linux
dotnet publish apps/Studio/HostQualification -c Release -r win-x64 --self-contained true --no-restore -o artifacts/host-windows
```

Use a disposable runner with a desktop or Xvfb, not the shared development
host, to install the native dependencies. The checked Linux package needs
WebKitGTK 4.1, JavaScriptCoreGTK 4.1, GTK 3 and libnotify. Windows requires
an installed WebView2 runtime. The dedicated CI workflow tests extraction
of both portable packages, a native window, real Tab/Enter events, RTL
and normal window closure. Linux uses a network namespace; Windows uses
scoped outbound firewall rules for the application and installed WebView2
binaries, not a machine-wide offline guarantee. The scripts are not yet
qualified until observed CI results are available.

## Evidence and limits

- Local .NET 10.0.115 Release build: 0 errors, 0 warnings.
- Locked-mode self-contained publish succeeded for linux-x64 and win-x64.
- Local publish outputs: approximately 82 MiB and 81 MiB respectively.
- NuGet audit on the candidate graph reported no known vulnerabilities at
  the time of the local check; this is not a security certification.
- Local ldd found missing libwebkit2gtk-4.1.so.0, libgtk-3.so.0,
  libgdk-3.so.0, libjavascriptcoregtk-4.1.so.0 and libnotify.so.4. No
  system-wide dependency installation was made on the shared server.
- Native CI results, end-user installer qualification, full offline model
  inspection and production accessibility evidence remain outstanding.
- Upstream source inspection found a documented origin-trust limitation in
  PhotinoWebViewManager (messages assume the local application origin).
  This test fixture contains only trusted local assets and no external
  navigation. Production adoption requires review of navigation/origin
  controls and the security boundary; do not expose untrusted external
  content to this bridge.

References: https://github.com/tryphotino/photino.Blazor and
https://www.nuget.org/packages/Photino.Blazor/4.0.13.

U02 owns the host/packaging proof only. UI01 owns the application shell,
DS02 the Fluent component integration, and RT01 the actual workspace and
local runtime. No production claims or issue closure are justified by
compilation and portable extraction alone.

First CI run 46035411702: Linux native window/offline namespace/keyboard/RTL/normal closure passed (host.json and window.png retained under evidence/). Windows timed out at the first render predicate and did not produce a host report. No cause is established. Added durable startup logging and timeout diagnostics for the next run; Windows remains unqualified.

Run 46039614781 established Windows native startup and Razor DOM interop
in both Connected and Blocked modes (evidence/windows-startup/*.json).
Both modes failed at first keyboard focus acquisition. The new probe
gives initial focus explicitly, then requires actual forward/backward
keyboard traversal and Enter activation while verifying the native
foreground window. Initial programmatic focus is not counted as a
keyboard success. WebView profile data is kept out of uploaded evidence.

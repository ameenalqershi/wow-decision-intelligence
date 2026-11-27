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

Run 46041393009: both Windows modes rendered and had initial DOM focus, but real Tab did not reach the second button; Win32 GUI thread focus stayed on the top-level window. The observed reports and focus logs are retained in evidence/windows-focus. Pinned upstream v4.0.22 source (Photino.Windows.cpp SHA-256 6cfec62b0c0f6296175f986efa8bd0bbcb748394bf1c03da5009a9d466d995f7) calls WebView2 controller MoveFocus on WM_ACTIVATE. The revised probe forces a genuine minimize/restore activation of only its own native window, then injects actual OS keyboard input using Win32 SendInput and checks its accepted count. This is a hypothesis under test, not a claimed fix. Both connected and process-network-blocked runs must still complete the original Tab, Shift+Tab, Enter and RTL assertions.

Run 46068618236 failed the same strict first Tab in both modes despite
accepted SendInput and genuine native activation. The failure artifacts are
retained in evidence/windows-sendinput. Root cause is not established:
GUI thread focus remains the top-level window, while DOM focus names the
first button. The next run enumerates native descendant classes and thread
focus; on strict keyboard failure only, it probes a real click in blank
fixture space followed by two real Tabs. That secondary diagnostic cannot
satisfy the original keyboard-only acceptance; it distinguishes focus
acquisition from keyboard dispatch. No external window is clicked.

Run 46071873692 isolated the focus acquisition problem: after a blank
native-area click, GUI-thread focus moved from the top-level HWND to
Chrome_WidgetWin_1 and two real Tabs reached confirm in both network
modes. The diagnostic run still failed the original startup criterion.
The new WindowsFocusBridge is a candidate, host-owned compatibility
adapter that transfers focus only inside the foreground fixture on its
own UI thread after renderer readiness and on activation. It preserves
existing child focus and checks the exact visible descendant hierarchy.
No queue attachment, external activation, keyboard injection or DOM
mutation is performed by the bridge. This uses an observed private
WebView2 HWND hierarchy, not a documented controller API: it is not
approved for product adoption without independent host review and
version conformance. Original strict keyboard tests remain mandatory.

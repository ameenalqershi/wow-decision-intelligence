using System.Runtime.InteropServices;
using System.Text;
using Photino.NET;

namespace WowDi.Studio.HostQualification;

// Candidate host adapter, scoped to the pinned Photino/WebView2 hierarchy
// observed in native CI. No queue attachment, injected input, foreground
// activation or DOM mutation. Unknown/ambiguous hierarchy fails closed.
// An upstream supported controller MoveFocus surface is preferred before
// adopting this compatibility adapter in production.
public sealed class WindowsFocusBridge
{
    private PhotinoWindow? window;
    private bool rendererReady;
    public void Bind(PhotinoWindow mainWindow)
    {
        if (!OperatingSystem.IsWindows()) return;
        window = mainWindow;
        mainWindow.WindowFocusIn += (_, _) => AcquireIfReady();
    }
    public void RendererReady()
    {
        if (!OperatingSystem.IsWindows() || window is null) return;
        rendererReady = true;
        AcquireIfReady();
    }
    private void AcquireIfReady()
    {
        if (!rendererReady || window is not { } mainWindow) return;
        mainWindow.Invoke(() =>
        {
            var parent = mainWindow.WindowHandle;
            if (parent == IntPtr.Zero || GetForegroundWindow() != parent) return;
            if (GetWindowThreadProcessId(parent, out var owner) != GetCurrentThreadId() ||
                owner != (uint)Environment.ProcessId) return;
            var current = GetFocus();
            if (current != parent && current != IntPtr.Zero)
            {
                Console.WriteLine($"Native focus preserved: {current}");
                return;
            }
            var candidates = new List<IntPtr>();
            EnumChildWindows(parent, (child, _) =>
            {
                var container = GetParent(child);
                if (ClassName(child) == "Chrome_WidgetWin_1" &&
                    ClassName(container) == "Chrome_WidgetWin_0" &&
                    GetWindowThreadProcessId(container, out var containerOwner) != 0 &&
                    containerOwner == owner &&
                    IsChild(parent, child) && IsWindowVisible(child) && IsWindowEnabled(child))
                    candidates.Add(child);
                return true;
            }, IntPtr.Zero);
            if (candidates.Count != 1)
            {
                Console.WriteLine($"Native focus bridge unavailable: {candidates.Count} eligible descendants");
                return;
            }
            var target = candidates[0];
            _ = SetFocus(target);
            var actual = GetFocus();
            Console.WriteLine($"Native focus bridge target={target}; actual={actual}; acquired={actual == target || IsChild(target, actual)}");
        });
    }
    private static string ClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }
    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}

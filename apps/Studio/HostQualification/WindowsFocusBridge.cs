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
    // All native state is confined to the Photino UI thread.
    private bool rendererReady;
    private IntPtr parent;
    private uint recoverMessage;
    private SubclassProc? callback;
    private const uint WM_SETFOCUS = 0x0007, WM_ACTIVATE = 0x0006, WM_NCDESTROY = 0x0082;
    private static readonly UIntPtr SubclassId = new(1);
    public void Bind(PhotinoWindow mainWindow)
    {
        if (!OperatingSystem.IsWindows()) return;
        window = mainWindow;

    }
    public void RendererReady()
    {
        if (!OperatingSystem.IsWindows() || window is null) return;
        window.Invoke(() =>
        {
            if (rendererReady) return;
            parent = window.WindowHandle;
            if (parent == IntPtr.Zero ||
                GetWindowThreadProcessId(parent, out var owner) != GetCurrentThreadId() ||
                owner != (uint)Environment.ProcessId) return;
            recoverMessage = RegisterWindowMessage("WowDi.HostQualification.FocusRecovery.v1");
            if (recoverMessage == 0) throw new InvalidOperationException("Cannot register focus recovery message.");
            callback = Dispatch;
            if (!SetWindowSubclass(parent, callback, SubclassId, UIntPtr.Zero))
                throw new InvalidOperationException("Cannot observe native focus transitions.");
            rendererReady = true;
            QueueRecovery();
        });
    }
    private void QueueRecovery()
    {
        if (rendererReady && !PostMessage(parent, recoverMessage, UIntPtr.Zero, IntPtr.Zero))
            Console.Error.WriteLine("Could not queue native focus recovery.");
    }
    private IntPtr Dispatch(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        // Never set focus reentrantly inside activation/WM_SETFOCUS. The
        // native parent can overwrite it while processing the activation.
        if (message == WM_NCDESTROY)
        {
            rendererReady = false;
            if (callback is not null) RemoveWindowSubclass(hwnd, callback, SubclassId);
            parent = IntPtr.Zero;
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }
        if (message == recoverMessage)
        {
            try { AcquireIfReady(); }
            catch (Exception ex) { Console.Error.WriteLine($"Native focus recovery failed: {ex.GetType().Name}"); }
            return IntPtr.Zero;
        }
        var result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == WM_SETFOCUS || (message == WM_ACTIVATE && (wParam.ToUInt64() & 0xffff) != 0))
        {
            Console.WriteLine($"Native focus transition {message:x}; current={GetFocus()}");
            QueueRecovery();
        }
        return result;
    }
    private void AcquireIfReady()
    {
            if (!rendererReady) return;
            if (parent == IntPtr.Zero || GetForegroundWindow() != parent) return;
            if (GetWindowThreadProcessId(parent, out var owner) != GetCurrentThreadId() ||
                owner != (uint)Environment.ProcessId) return;
            var current = GetFocus();
            if (current != parent && IsChild(parent, current))
            {
                Console.WriteLine($"Native focus preserved: {current}");
                return;
            }
            if (current != parent) return; // do not steal focus from other windows
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
    }
    private static string ClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam);
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

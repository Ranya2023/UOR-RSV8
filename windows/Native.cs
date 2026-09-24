using System.Runtime.InteropServices;

namespace Remco;

/// <summary>Win32 calls for mouse, keyboard, windows and COM.</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    public const uint KEYEVENTF_KEYUP = 0x0002;

    public const byte VK_CONTROL = 0x11;
    public const byte VK_PRIOR = 0x21;   // Page Up
    public const byte VK_NEXT = 0x22;    // Page Down
    public const byte VK_ADD = 0x6B;     // numpad +
    public const byte VK_SUBTRACT = 0x6D;// numpad -
    public const byte VK_B = 0x42;
    public const byte VK_W = 0x57;
    public const byte VK_I = 0x49;
    public const byte VK_E = 0x45;
    public const byte VK_ESCAPE = 0x1B;
    public const byte VK_F5 = 0x74;
    public const byte VK_LWIN = 0x5B;
    public const byte VK_D = 0x44;
    public const byte VK_MENU = 0x12;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    // ── layered overlay window ─────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public int biSize; public int biWidth; public int biHeight; public short biPlanes; public short biBitCount;
        public int biCompression; public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public int biClrUsed; public int biClrImportant;
        public int bmiColors; // one RGBQUAD, unused for 32-bpp
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppunk);

    /// <summary>Marshal.GetActiveObject replacement (it does not exist in .NET 5+).</summary>
    public static object? GetRunningComObject(string progId)
    {
        if (CLSIDFromProgID(progId, out Guid clsid) < 0) return null;
        if (GetActiveObject(ref clsid, IntPtr.Zero, out object? obj) < 0) return null;
        return obj;
    }

    // ── mouse ──────────────────────────────────────────────────────────
    public static void MoveRelative(int dx, int dy)
    {
        if (dx != 0 || dy != 0) mouse_event(MOUSEEVENTF_MOVE, dx, dy, 0, UIntPtr.Zero);
    }

    /// <summary>Moves the cursor to an absolute screen pixel and generates a real mouse-move.</summary>
    public static void MoveAbsolute(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        int nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny, 0, UIntPtr.Zero);
    }

    public static void Button(string button, string action)
    {
        bool right = button == "right";
        uint down = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        uint up = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
        if (action == "dblclick")
        {
            // two clicks at the SAME spot, fast — Windows sees a real double-click
            mouse_event(down, 0, 0, 0, UIntPtr.Zero); mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            mouse_event(down, 0, 0, 0, UIntPtr.Zero); mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            return;
        }
        if (action == "down" || action == "click") mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        if (action == "up" || action == "click") mouse_event(up, 0, 0, 0, UIntPtr.Zero);
    }

    public static void Wheel(int notches) =>
        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, notches * 120, UIntPtr.Zero);

    // ── keyboard ───────────────────────────────────────────────────────
    public static void Key(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void CtrlKey(byte vk)
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        Key(vk);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // ── SendInput (unicode typing from the phone keyboard) ─────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    /// <summary>Types any text (Kurdish, Arabic, English…) into the focused app.</summary>
    public static void TypeText(string text)
    {
        var list = new List<INPUT>();
        foreach (char ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n') { Key(0x0D); continue; }
            var down = new INPUT { type = 1 };
            down.U.ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = 0x0004 /*KEYEVENTF_UNICODE*/ };
            var up = down;
            up.U.ki.dwFlags = 0x0004 | 0x0002;
            list.Add(down); list.Add(up);
            if (list.Count >= 64) { SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>()); list.Clear(); }
        }
        if (list.Count > 0) SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static readonly Dictionary<string, (byte vk, bool ext)> NamedKeys = new()
    {
        ["enter"] = (0x0D, false), ["backspace"] = (0x08, false), ["tab"] = (0x09, false), ["esc"] = (0x1B, false),
        ["left"] = (0x25, true), ["up"] = (0x26, true), ["right"] = (0x27, true), ["down"] = (0x28, true),
        ["delete"] = (0x2E, true), ["home"] = (0x24, true), ["end"] = (0x23, true),
        ["pageup"] = (0x21, true), ["pagedown"] = (0x22, true), ["space"] = (0x20, false),
    };

    /// <summary>Presses a named key (or a letter) with optional Ctrl / Shift / Alt / Win.</summary>
    public static void KeyCombo(string name, bool ctrl, bool shift, bool alt, bool win, int repeat = 1)
    {
        byte vk; bool ext = false;
        if (NamedKeys.TryGetValue(name, out var nk)) { vk = nk.vk; ext = nk.ext; }
        else if (name.Length == 1 && char.IsLetterOrDigit(name[0])) vk = (byte)char.ToUpperInvariant(name[0]);
        else return;
        if (ctrl) keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        if (shift) keybd_event(0x10, 0, 0, UIntPtr.Zero);
        if (alt) keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        if (win) keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        uint ef = ext ? 0x0001u : 0u;
        for (int i = 0; i < Math.Clamp(repeat, 1, 200); i++)
        {
            keybd_event(vk, 0, ef, UIntPtr.Zero);
            keybd_event(vk, 0, ef | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        if (win) keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (alt) keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (shift) keybd_event(0x10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (ctrl) keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // ── is a text cursor blinking somewhere? (to pop up the phone keyboard) ──
    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize; public uint flags; public IntPtr hwndActive; public IntPtr hwndFocus; public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner; public IntPtr hwndMoveSize; public IntPtr hwndCaret; public RECT rcCaret;
    }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    /// <summary>Set by the controller: the slide show is minimised (desktop showing).</summary>
    public static bool IsIconicShow { get; set; }

    public static bool TextCaretVisible()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            uint tid = GetWindowThreadProcessId(fg, out _);
            var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(tid, ref gi)) return false;
            return gi.hwndCaret != IntPtr.Zero || (gi.flags & 0x1 /*GUI_CARETBLINKING*/) != 0;
        }
        catch { return false; }
    }

    /// <summary>Win+D: show the desktop (minimises everything, including the slide show).</summary>
    public static void ShowDesktop()
    {
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        Key(VK_D);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>Brings a window to the front even though Remco isn't the active app.</summary>
    public static void ForceForeground(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9 /*SW_RESTORE*/);
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);          // a tap of Alt lifts Windows' focus-stealing lock
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        SetForegroundWindow(h);
    }

    /// <summary>Client area of a window in screen pixels.</summary>
    public static Rectangle? ClientScreenRect(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return null;
        if (!GetClientRect(hWnd, out RECT r)) return null;
        var p = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hWnd, ref p)) return null;
        return new Rectangle(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
    }
}

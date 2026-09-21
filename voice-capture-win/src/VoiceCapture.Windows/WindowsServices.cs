using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace VoiceCapture.Windows;

public sealed class SecretStore(string directory)
{
    private string PathName => Path.Combine(directory, "openai.key");
    public string Read()
    {
        if (!File.Exists(PathName)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(PathName), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
    public void Save(string key)
    {
        Directory.CreateDirectory(directory);
        if (string.IsNullOrWhiteSpace(key)) { if (File.Exists(PathName)) File.Delete(PathName); return; }
        byte[] bytes = Encoding.UTF8.GetBytes(key.Trim());
        try
        {
            File.WriteAllBytes(PathName + ".tmp", ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            File.Move(PathName + ".tmp", PathName, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

public static class StartupRegistration
{
    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("VoiceCapture", "\"" + Environment.ProcessPath + "\" --tray");
        else key.DeleteValue("VoiceCapture", false);
    }
}

internal static class Native
{
    internal delegate nint HookCallback(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, HookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBOARD { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint Type; public INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    internal static INPUT Key(ushort key, bool up) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0u } } };
    internal static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    internal static bool ModifiersDown() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(Down);
}

public sealed class HotkeyMonitor : IDisposable
{
    private readonly Native.HookCallback callback;
    private nint hook;
    private readonly HashSet<int> keys = [];
    private AppSettings settings;
    private bool active, suppressedKey, escapeSuppressed;
    public bool Enabled { get; set; } = true;
    public bool SessionActive { get; set; }
    public event Action? Pressed;
    public event Action? Released;
    public event Action? Cancelled;
    public HotkeyMonitor(AppSettings settings)
    {
        this.settings = settings;
        callback = OnKey;
        hook = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
        if (hook == 0) throw new InvalidOperationException("Не удалось включить глобальный хоткей.");
    }
    public void Update(AppSettings value) { settings = value; Reset(); }
    public void Reset() { keys.Clear(); active = false; suppressedKey = false; escapeSuppressed = false; }
    private nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code < 0) return Native.CallNextHookEx(hook, code, wParam, lParam);
        var data = Marshal.PtrToStructure<Native.KEYBOARD>(lParam);
        if ((data.Flags & 0x10) != 0) return Native.CallNextHookEx(hook, code, wParam, lParam);
        bool down = wParam == 0x100 || wParam == 0x104;
        bool up = wParam == 0x101 || wParam == 0x105;
        if (!down && !up) return Native.CallNextHookEx(hook, code, wParam, lParam);
        int key = (int)data.Key;
        if (down) keys.Add(key); else keys.Remove(key);
        if (key == 0x1B && ((down && SessionActive) || escapeSuppressed))
        {
            if (down && !escapeSuppressed) Cancelled?.Invoke();
            escapeSuppressed = down;
            return 1;
        }
        bool consume = key == settings.HotkeyVirtualKey && suppressedKey;
        bool satisfied = Enabled && keys.Contains(settings.HotkeyVirtualKey) &&
            (!settings.HotkeyControl || keys.Contains(0xA2)) &&
            (!settings.HotkeyAlt || keys.Contains(0xA4)) &&
            (!settings.HotkeyShift || keys.Contains(0xA0)) && !keys.Contains(0xA5) && !keys.Contains(0x5B) && !keys.Contains(0x5C);
        // Trigger key must be pressed last, so its key-down can be suppressed consistently.
        if (!active && satisfied && down && key == settings.HotkeyVirtualKey && !consume)
        {
            active = true; suppressedKey = true; consume = true;
            Pressed?.Invoke();
        }
        if (active && !satisfied) { active = false; Released?.Invoke(); }
        if (up && key == settings.HotkeyVirtualKey) suppressedKey = false;
        return consume ? 1 : Native.CallNextHookEx(hook, code, wParam, lParam);
    }
    public void Dispose() { if (hook != 0) Native.UnhookWindowsHookEx(hook); hook = 0; }
}

public readonly record struct PasteTarget(nint Window, uint Process)
{
    public static PasteTarget Capture()
    {
        nint window = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(window, out uint process);
        return new(window, process);
    }
    public bool IsCurrent()
    {
        if (Window == 0 || Native.GetForegroundWindow() != Window) return false;
        Native.GetWindowThreadProcessId(Window, out uint process);
        return process == Process;
    }
}

public static class ClipboardService
{
    public static async Task<string> DeliverAsync(string text, bool autoPaste, PasteTarget target, CancellationToken token)
    {
        bool copied = false;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, text);
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
                Clipboard.SetDataObject(data, true);
                copied = true; break;
            }
            catch (COMException) { await Task.Delay(50, token); }
        }
        if (!copied) throw new RecognitionException("Буфер занят. Не удалось скопировать результат.");
        uint clipboardVersion = Native.GetClipboardSequenceNumber();
        if (!autoPaste) return "Текст скопирован";
        for (int i = 0; i < 30 && Native.ModifiersDown(); i++) await Task.Delay(50, token);
        token.ThrowIfCancellationRequested();
        if (Native.ModifiersDown()) return "Скопировано: клавиши ещё удерживаются";
        if (!target.IsCurrent()) return "Скопировано: активное окно изменилось";
        if (clipboardVersion != Native.GetClipboardSequenceNumber()) return "Вставка отменена: буфер изменился";
        Native.INPUT[] input = [Native.Key(0x11, false), Native.Key(0x56, false), Native.Key(0x56, true), Native.Key(0x11, true)];
        uint sent = Native.SendInput((uint)input.Length, input, Marshal.SizeOf<Native.INPUT>());
        if (sent != input.Length)
        {
            Native.SendInput(2, [Native.Key(0x56, true), Native.Key(0x11, true)], Marshal.SizeOf<Native.INPUT>());
            return "Скопировано: автоматическая вставка недоступна";
        }
        return "Отправлено в активное окно";
    }
}

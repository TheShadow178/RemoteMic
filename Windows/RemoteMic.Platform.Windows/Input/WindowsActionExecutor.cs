using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace RemoteMic.Platform.Windows.Input;

public enum WindowsKey : ushort
{
    Backspace = 0x08,
    Tab = 0x09,
    Enter = 0x0D,
    Shift = 0x10,
    LeftControl = 0xA2,
    RightControl = 0xA3,
    LeftAlt = 0xA4,
    RightAlt = 0xA5,
    Escape = 0x1B,
    Space = 0x20,
    PageUp = 0x21,
    PageDown = 0x22,
    End = 0x23,
    Home = 0x24,
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,
    Delete = 0x2E,
    A = 0x41,
    C = 0x43,
    F = 0x46,
    L = 0x4C,
    V = 0x56,
    X = 0x58,
    Y = 0x59,
    Z = 0x5A,
    LeftWindows = 0x5B,
    VolumeMute = 0xAD,
    VolumeDown = 0xAE,
    VolumeUp = 0xAF,
    MediaNext = 0xB0,
    MediaPrevious = 0xB1,
    MediaStop = 0xB2,
    MediaPlayPause = 0xB3,
}

/// <summary>
/// Executes mapping actions with Win32 SendInput. Clipboard-based fixed text
/// mirrors v1.5.0: paste atomically, wait 300 ms, then restore only when the
/// user or another app has not changed the clipboard in the meantime.
/// </summary>
public sealed class WindowsActionExecutor
{
    private readonly SynchronizationContext _uiContext;

    public WindowsActionExecutor(SynchronizationContext? uiContext = null)
    {
        _uiContext = uiContext ?? SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                "WindowsActionExecutor must be created on the WPF UI thread.");
    }

    public async Task SendKeyAsync(
        WindowsKey key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var virtualKeyboard = VirtualHidKeyboard.TryOpen();
        var usedVirtualKeyboard = virtualKeyboard?.TrySubmit(key, keyUp: false) == true;
        if (!usedVirtualKeyboard)
        {
            Send([KeyboardInput(key, keyUp: false)]);
        }
        try
        {
            // Some global-shortcut handlers debounce the near-simultaneous
            // down/up pair produced by one SendInput call. A short human-like
            // hold is still a tap, while remaining visible to those handlers.
            await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
        }
        finally
        {
            // Never leave a modifier logically held when shutdown cancels the
            // delay between key-down and key-up.
            if (usedVirtualKeyboard)
            {
                if (virtualKeyboard?.TrySubmit(key, keyUp: true) != true)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "RemoteMic 虚拟 HID 键盘无法提交按键释放报告。");
                }
            }
            else
            {
                Send([KeyboardInput(key, keyUp: true)]);
            }
        }
    }

    public Task SendShortcutAsync(
        IReadOnlyList<WindowsKey> keys,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (keys.Count == 0)
        {
            return Task.CompletedTask;
        }

        var inputs = new INPUT[keys.Count * 2];
        for (var index = 0; index < keys.Count; index++)
        {
            inputs[index] = KeyboardInput(keys[index], keyUp: false);
            inputs[keys.Count + index] = KeyboardInput(keys[keys.Count - index - 1], keyUp: true);
        }

        Send(inputs);
        return Task.CompletedTask;
    }

    public async Task PasteFixedTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await OnUiAsync(CaptureClipboard);
        await OnUiAsync(() =>
        {
            Clipboard.SetText(text ?? string.Empty, TextDataFormat.UnicodeText);
            Clipboard.Flush();
            return true;
        });
        var injectedSequence = GetClipboardSequenceNumber();

        await SendShortcutAsync([WindowsKey.LeftControl, WindowsKey.V], cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);

        if (snapshot.Captured && GetClipboardSequenceNumber() == injectedSequence)
        {
            await OnUiAsync(() =>
            {
                RestoreClipboard(snapshot);
                return true;
            });
        }
    }

    public Task OpenAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task FocusInputAsync(CancellationToken cancellationToken = default) =>
        // Browsers, editors and most desktop chat clients expose their primary
        // input/search surface through Ctrl+L. Multi-step actions may override
        // this with an explicitly recorded shortcut for app-specific behavior.
        SendShortcutAsync([WindowsKey.LeftControl, WindowsKey.L], cancellationToken);

    public Task ShowDesktopAsync(CancellationToken cancellationToken = default) =>
        SendShortcutAsync([WindowsKey.LeftWindows, (WindowsKey)0x44], cancellationToken);

    public Task SearchAsync(CancellationToken cancellationToken = default) =>
        SendShortcutAsync([WindowsKey.LeftWindows, (WindowsKey)0x53], cancellationToken);

    public Task ScrollAsync(int wheelDelta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = new INPUT
        {
            type = InputMouse,
            union = new INPUTUNION
            {
                mouse = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)wheelDelta),
                    flags = MouseEventWheel,
                },
            },
        };
        Send([input]);
        return Task.CompletedTask;
    }

    private static ClipboardSnapshot CaptureClipboard()
    {
        try
        {
            var source = Clipboard.GetDataObject();
            if (source is null)
            {
                return new ClipboardSnapshot(true, []);
            }

            var entries = new List<ClipboardEntry>();
            foreach (var format in source.GetFormats(autoConvert: false).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    var data = CloneClipboardValue(source.GetData(format, autoConvert: false));
                    if (data is not null)
                    {
                        entries.Add(new ClipboardEntry(format, data));
                    }
                }
                catch (Exception exception) when (
                    exception is ExternalException or IOException or NotSupportedException)
                {
                    // Preserve every format that can be materialized safely;
                    // one owner-specific format must not discard the rest.
                }
            }

            return new ClipboardSnapshot(true, entries);
        }
        catch (ExternalException)
        {
            return new ClipboardSnapshot(false, []);
        }
    }

    private static void RestoreClipboard(ClipboardSnapshot snapshot)
    {
        try
        {
            if (snapshot.Entries.Count > 0)
            {
                var restored = new DataObject();
                foreach (var entry in snapshot.Entries)
                {
                    restored.SetData(entry.Format, entry.Value);
                }
                Clipboard.SetDataObject(restored, true);
            }
            else
            {
                Clipboard.Clear();
            }
        }
        catch (ExternalException)
        {
            // Clipboard ownership can change between the sequence check and
            // SetDataObject. In that race, preserving the newer owner is safer.
        }
    }

    private static object? CloneClipboardValue(object? value)
    {
        return value switch
        {
            null => null,
            byte[] bytes => bytes.ToArray(),
            MemoryStream memory => new MemoryStream(memory.ToArray(), writable: false),
            Stream stream => CopyStream(stream),
            ICloneable cloneable => cloneable.Clone(),
            _ => value,
        };
    }

    private static MemoryStream CopyStream(Stream source)
    {
        var position = source.CanSeek ? source.Position : 0;
        if (source.CanSeek)
        {
            source.Position = 0;
        }

        var copy = new MemoryStream();
        try
        {
            source.CopyTo(copy);
            copy.Position = 0;
            return copy;
        }
        finally
        {
            if (source.CanSeek)
            {
                source.Position = position;
            }
        }
    }

    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(_ =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, null);
        return completion.Task;
    }

    private static INPUT KeyboardInput(WindowsKey key, bool keyUp)
    {
        var scanCode = key switch
        {
            WindowsKey.RightControl => (ushort)0x1D,
            WindowsKey.RightAlt => (ushort)0x38,
            _ => (ushort)0,
        };
        var useExtendedScanCode = scanCode != 0;
        var flags = useExtendedScanCode
            ? KeyEventExtendedKey | KeyEventScanCode
            : 0;
        if (keyUp)
        {
            flags |= KeyEventKeyUp;
        }

        return new INPUT
        {
            type = InputKeyboard,
            union = new INPUTUNION
            {
                keyboard = new KEYBDINPUT
                {
                    virtualKey = useExtendedScanCode ? (ushort)0 : (ushort)key,
                    scanCode = scanCode,
                    flags = flags,
                },
            },
        };
    }

    private static void Send(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput 只提交了 {sent}/{inputs.Length} 个事件（Win32 {Marshal.GetLastWin32Error()}）。");
        }
    }

    private sealed record ClipboardEntry(string Format, object Value);
    private sealed record ClipboardSnapshot(bool Captured, IReadOnlyList<ClipboardEntry> Entries);

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;
    private const uint MouseEventWheel = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT keyboard;
        [FieldOffset(0)] public MOUSEINPUT mouse;
        [FieldOffset(0)] public HARDWAREINPUT hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort virtualKey;
        public ushort scanCode;
        public uint flags;
        public uint time;
        public UIntPtr extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint message;
        public ushort parameterLow;
        public ushort parameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint numberOfInputs,
        [In] INPUT[] inputs,
        int sizeOfInput);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}

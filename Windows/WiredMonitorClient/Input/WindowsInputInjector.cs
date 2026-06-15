using System.Runtime.InteropServices;
using WiredMonitorClient.Protocol;

namespace WiredMonitorClient.Input;

public sealed class WindowsInputInjector
{
    private const int MonitorDefaultToNearest = 2;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int WheelDelta = 120;

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseeventfMove = 0x0001;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightdown = 0x0008;
    private const uint MouseeventfRightup = 0x0010;
    private const uint MouseeventfMiddledown = 0x0020;
    private const uint MouseeventfMiddleup = 0x0040;
    private const uint MouseeventfWheel = 0x0800;
    private const uint MouseeventfHwheel = 0x1000;
    private const uint MouseeventfXdown = 0x0080;
    private const uint MouseeventfXup = 0x0100;
    private const uint MouseeventfAbsolute = 0x8000;
    private const uint MouseeventfVirtualdesk = 0x4000;

    private const uint KeyeventfKeyup = 0x0002;

    private MonitorBounds _targetBounds;
    private int _sourceWidth = 1;
    private int _sourceHeight = 1;

    public void SetTargetWindow(IntPtr windowHandle, int sourceWidth, int sourceHeight)
    {
        _targetBounds = GetMonitorBoundsForWindow(windowHandle);
        _sourceWidth = Math.Max(1, sourceWidth);
        _sourceHeight = Math.Max(1, sourceHeight);
    }

    public void Inject(WindowsInputEventPayload input)
    {
        switch (input.EventType)
        {
            case WindowsInputEventType.MouseMove:
                MoveMouse(input.X, input.Y);
                break;
            case WindowsInputEventType.MouseDown:
                MoveMouse(input.X, input.Y);
                SendMouseButton(input.Button, isDown: true);
                break;
            case WindowsInputEventType.MouseUp:
                MoveMouse(input.X, input.Y);
                SendMouseButton(input.Button, isDown: false);
                break;
            case WindowsInputEventType.KeyDown:
                SendKey(input.VirtualKey, isDown: true);
                break;
            case WindowsInputEventType.KeyUp:
                SendKey(input.VirtualKey, isDown: false);
                break;
            case WindowsInputEventType.Scroll:
                SendScroll(input.DeltaX, input.DeltaY);
                break;
        }
    }

    public void ReleaseCommonModifiers()
    {
        foreach (var virtualKey in new ushort[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
            SendKey(virtualKey, isDown: false);
    }

    private void MoveMouse(int sourceX, int sourceY)
    {
        var bounds = _targetBounds.IsEmpty ? GetVirtualScreenBounds() : _targetBounds;
        var x = bounds.Left + ScaleCoordinate(sourceX, _sourceWidth, bounds.Width);
        var y = bounds.Top + ScaleCoordinate(sourceY, _sourceHeight, bounds.Height);
        SendMouse(MouseeventfMove | MouseeventfAbsolute | MouseeventfVirtualdesk, x, y);
    }

    private static int ScaleCoordinate(int value, int sourceSize, int targetSize)
    {
        var clamped = Math.Clamp(value, 0, Math.Max(0, sourceSize - 1));
        if (sourceSize <= 1 || targetSize <= 1)
            return 0;

        return Math.Clamp((int)Math.Round(clamped * (targetSize - 1.0) / (sourceSize - 1)), 0, targetSize - 1);
    }

    private static void SendMouseButton(byte button, bool isDown)
    {
        var flags = button switch
        {
            1 => isDown ? MouseeventfLeftdown : MouseeventfLeftup,
            2 => isDown ? MouseeventfRightdown : MouseeventfRightup,
            3 => isDown ? MouseeventfMiddledown : MouseeventfMiddleup,
            4 or 5 => isDown ? MouseeventfXdown : MouseeventfXup,
            _ => 0u,
        };

        if (flags == 0)
            return;

        var mouseData = button switch
        {
            4 => 0x0001u,
            5 => 0x0002u,
            _ => 0u,
        };
        SendInputOrThrow(CreateMouseInput(flags, 0, 0, mouseData));
    }

    private static void SendScroll(int deltaX, int deltaY)
    {
        if (deltaY != 0)
            SendInputOrThrow(CreateMouseInput(MouseeventfWheel, 0, 0, unchecked((uint)(deltaY * WheelDelta))));

        if (deltaX != 0)
            SendInputOrThrow(CreateMouseInput(MouseeventfHwheel, 0, 0, unchecked((uint)(deltaX * WheelDelta))));
    }

    private static void SendKey(ushort virtualKey, bool isDown)
    {
        if (virtualKey == 0)
            return;

        var input = new Input
        {
            Type = InputKeyboard,
            U = new InputUnion
            {
                Ki = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = 0,
                    Flags = isDown ? 0 : KeyeventfKeyup,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };
        SendInputOrThrow(input);
    }

    private static void SendMouse(uint flags, int screenX, int screenY)
    {
        var virtualBounds = GetVirtualScreenBounds();
        var normalizedX = NormalizeAbsolute(screenX, virtualBounds.Left, virtualBounds.Width);
        var normalizedY = NormalizeAbsolute(screenY, virtualBounds.Top, virtualBounds.Height);
        SendInputOrThrow(CreateMouseInput(flags, normalizedX, normalizedY, 0));
    }

    private static int NormalizeAbsolute(int pixel, int origin, int size)
    {
        if (size <= 1)
            return 0;

        return Math.Clamp((int)Math.Round((pixel - origin) * 65535.0 / (size - 1)), 0, 65535);
    }

    private static Input CreateMouseInput(uint flags, int dx, int dy, uint mouseData)
    {
        return new Input
        {
            Type = InputMouse,
            U = new InputUnion
            {
                Mi = new MouseInputData
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = mouseData,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };
    }

    private static void SendInputOrThrow(Input input)
    {
        var inputs = new[] { input };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput failed: {Marshal.GetLastWin32Error()}");
    }

    private static MonitorBounds GetMonitorBoundsForWindow(IntPtr windowHandle)
    {
        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return GetVirtualScreenBounds();

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info)
            ? MonitorBounds.FromRect(info.Monitor)
            : GetVirtualScreenBounds();
    }

    private static MonitorBounds GetVirtualScreenBounds()
    {
        return new MonitorBounds(
            GetSystemMetrics(SmXVirtualScreen),
            GetSystemMetrics(SmYVirtualScreen),
            GetSystemMetrics(SmCxVirtualScreen),
            GetSystemMetrics(SmCyVirtualScreen));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private readonly record struct MonitorBounds(int Left, int Top, int Width, int Height)
    {
        public bool IsEmpty => Width <= 0 || Height <= 0;

        public static MonitorBounds FromRect(Rect rect)
        {
            return new MonitorBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInputData Mi;

        [FieldOffset(0)]
        public KeyboardInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

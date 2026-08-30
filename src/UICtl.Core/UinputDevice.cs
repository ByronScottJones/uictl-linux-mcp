using System.ComponentModel;
using System.Runtime.InteropServices;
using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// One long-lived virtual keyboard+mouse device over `/dev/uinput`, created
/// once and cached for the daemon's lifetime - same rationale as
/// Accessibility's lazy AT-SPI Connection (see AsyncBridge.cs and
/// ENGINEERING.md's "Why a daemon at all"). Absolute pointer (EV_ABS), not
/// relative deltas: see the input-synthesis plan's coordinate-space
/// discussion - a relative device would need a cheap "where is the pointer
/// right now" query, which doesn't exist on Wayland (same gap as `pixel`).
/// ABS_X/ABS_Y are ranged to the real X11/XWayland screen pixel size
/// (XlibScreenInterop), verified live to line up with AT-SPI's own
/// screen-coordinate frames.
/// </summary>
internal static class UinputDevice
{
    private static readonly Lazy<Device> LazyDevice = new(Create);

    static UinputDevice()
    {
        // The daemon's own stop handling calls Environment.Exit directly
        // (see DaemonServer.cs) - process teardown would reclaim the fd
        // and implicitly destroy the kernel-side device either way, but
        // disposing explicitly here (ProcessExit still fires on
        // Environment.Exit) is cheap, correct hygiene rather than relying
        // on that implicit cleanup - PR #2 review comment.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (LazyDevice.IsValueCreated) LazyDevice.Value.Dispose();
        };
    }

    public static void MoveTo(int x, int y)
    {
        var d = LazyDevice.Value;
        d.Write(UinputInterop.EvAbs, UinputInterop.AbsX, Clamp(x, d.ScreenWidth));
        d.Write(UinputInterop.EvAbs, UinputInterop.AbsY, Clamp(y, d.ScreenHeight));
        d.Sync();
    }

    public static void Click(int x, int y, MouseButton button, int count)
    {
        MoveTo(x, y);
        int btn = ButtonCode(button);
        var d = LazyDevice.Value;
        for (int i = 0; i < count; i++)
        {
            d.Write(UinputInterop.EvKey, (ushort)btn, 1);
            d.Sync();
            Thread.Sleep(30);
            d.Write(UinputInterop.EvKey, (ushort)btn, 0);
            d.Sync();
            if (i + 1 < count) Thread.Sleep(60);
        }
    }

    public static void Scroll(int x, int y, int dx, int dy)
    {
        MoveTo(x, y);
        var d = LazyDevice.Value;
        // Kernel REL_WHEEL convention is inverted from the "positive dy
        // scrolls content down" contract MCP_INTERFACE.md documents for
        // this tool - see the input-synthesis plan's sign-convention note.
        int wheelTicks = -dy;
        int step = Math.Sign(wheelTicks);
        for (int i = 0; i < Math.Abs(wheelTicks); i++)
        {
            d.Write(UinputInterop.EvRel, UinputInterop.RelWheel, step);
            d.Sync();
        }
        int hStep = Math.Sign(dx);
        for (int i = 0; i < Math.Abs(dx); i++)
        {
            d.Write(UinputInterop.EvRel, UinputInterop.RelHWheel, hStep);
            d.Sync();
        }
    }

    public static void KeyTap(int keycode)
    {
        KeyDown(keycode);
        KeyUp(keycode);
    }

    public static void KeyDown(int keycode)
    {
        var d = LazyDevice.Value;
        d.Write(UinputInterop.EvKey, (ushort)keycode, 1);
        d.Sync();
    }

    public static void KeyUp(int keycode)
    {
        var d = LazyDevice.Value;
        d.Write(UinputInterop.EvKey, (ushort)keycode, 0);
        d.Sync();
    }

    /// <summary>Throwaway open+close (not the persistent device) - used by Permissions.GetStatus() so a status check doesn't itself create a virtual input device.</summary>
    public static bool ProbeWritable()
    {
        int fd = UinputInterop.OpenWriteOnlyForProbe();
        if (fd < 0) return false;
        UinputInterop.close(fd);
        return true;
    }

    private static int Clamp(int value, int max) => Math.Clamp(value, 0, Math.Max(0, max - 1));

    private static int ButtonCode(MouseButton button) => button switch
    {
        MouseButton.Left => UinputInterop.BtnLeft,
        MouseButton.Right => UinputInterop.BtnRight,
        MouseButton.Center => UinputInterop.BtnMiddle,
        _ => UinputInterop.BtnLeft,
    };

    private static Device Create()
    {
        NativeAbi.EnsureSupported("uinput device/event struct layout and ioctl encoding");

        int fd = UinputInterop.OpenWriteNonBlock();
        if (fd < 0)
            throw new UiCtlException($"could not open {UinputInterop.UinputPath} for writing ({ErrnoMessage()}) - is $USER in the 'input' group? See `uictl permissions`.");

        (int width, int height) = XlibScreenInterop.GetScreenSize();

        SetBit(fd, UinputInterop.UiSetEvBit, UinputInterop.EvKey);
        SetBit(fd, UinputInterop.UiSetEvBit, UinputInterop.EvRel);
        SetBit(fd, UinputInterop.UiSetEvBit, UinputInterop.EvAbs);

        // Full standard keyboard range (KEY_ESC=1 .. KEY_KPDOT=83, plus the
        // extended block up to KEY_COMPOSE=127) so both KeyCodes' text and
        // combo tables have every code they need registered on the device -
        // an EV_KEY code that isn't UI_SET_KEYBIT-registered is silently
        // dropped by the kernel at write time, not rejected loudly.
        for (int key = 1; key <= 127; key++)
            SetBit(fd, UinputInterop.UiSetKeyBit, (uint)key);
        SetBit(fd, UinputInterop.UiSetKeyBit, (uint)UinputInterop.BtnLeft);
        SetBit(fd, UinputInterop.UiSetKeyBit, (uint)UinputInterop.BtnRight);
        SetBit(fd, UinputInterop.UiSetKeyBit, (uint)UinputInterop.BtnMiddle);

        SetBit(fd, UinputInterop.UiSetRelBit, UinputInterop.RelWheel);
        SetBit(fd, UinputInterop.UiSetRelBit, UinputInterop.RelHWheel);

        SetBit(fd, UinputInterop.UiSetAbsBit, UinputInterop.AbsX);
        SetBit(fd, UinputInterop.UiSetAbsBit, UinputInterop.AbsY);

        SetBit(fd, UinputInterop.UiSetPropBit, UinputInterop.InputPropPointer);

        byte[] userDev = UinputInterop.BuildUserDev("uictl-virtual-input", width - 1, height - 1);
        nint written = UinputInterop.write(fd, userDev, (nuint)userDev.Length);
        if (written != userDev.Length)
            throw new UiCtlException($"failed writing uinput_user_dev to {UinputInterop.UinputPath} ({ErrnoMessage()}, wrote {written} of {userDev.Length} bytes)");

        int created = UinputInterop.ioctl_noarg(fd, UinputInterop.UiDevCreate);
        if (created < 0)
            throw new UiCtlException($"UI_DEV_CREATE failed on {UinputInterop.UinputPath} ({ErrnoMessage()})");

        // Give the kernel/compositor a moment to enumerate the new device
        // before the first event - observed necessary in practice for
        // libinput to pick up a freshly created uinput device.
        Thread.Sleep(150);

        return new Device(fd, width, height);
    }

    private static void SetBit(int fd, nuint request, uint bit)
    {
        int result = UinputInterop.ioctl_int(fd, request, (int)bit);
        if (result < 0)
            throw new UiCtlException($"uinput ioctl setup failed (request=0x{request:X}, bit={bit}, {ErrnoMessage()})");
    }

    /// <summary>Human-readable errno text for the P/Invoke call that just failed - every UinputInterop entry point is declared SetLastError=true. Win32Exception's message maps an errno to strerror() text on Linux, despite the Windows-sounding name.</summary>
    private static string ErrnoMessage()
    {
        int errno = Marshal.GetLastPInvokeError();
        return $"errno {errno}: {new Win32Exception(errno).Message}";
    }

    private sealed class Device : IDisposable
    {
        private readonly int _fd;
        public int ScreenWidth { get; }
        public int ScreenHeight { get; }

        public Device(int fd, int width, int height)
        {
            _fd = fd;
            ScreenWidth = width;
            ScreenHeight = height;
        }

        public void Write(ushort type, ushort code, int value)
        {
            byte[] ev = UinputInterop.BuildEvent(type, code, value);
            UinputInterop.write(_fd, ev, (nuint)ev.Length);
        }

        public void Sync() => Write(UinputInterop.EvSyn, UinputInterop.SynReport, 0);

        public void Dispose()
        {
            UinputInterop.ioctl_noarg(_fd, UinputInterop.UiDevDestroy);
            UinputInterop.close(_fd);
        }
    }
}

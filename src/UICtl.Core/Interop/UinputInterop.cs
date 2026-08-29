using System.Runtime.InteropServices;

namespace UICtl.Core.Interop;

/// <summary>
/// Raw P/Invoke surface for `/dev/uinput` - the kernel-level virtual
/// keyboard+mouse device input synthesis is built on (see ENGINEERING.md's
/// input synthesis row: "kernel-level, identical under X11 and Wayland").
/// Structs are packed/unpacked as raw byte buffers rather than marshaled via
/// DllImport struct parameters, to avoid any ambiguity around how the CLR
/// would lay out nested fixed-size arrays - every offset/size here is
/// verified against this machine's /usr/include/linux/uinput.h and
/// input-event-codes.h (Ubuntu 26.04), not guessed from memory.
/// </summary>
internal static class UinputInterop
{
    private const int OWronly = 0x0001;
    private const int ONonblock = 0x0800;

    // struct uinput_user_dev (uinput.h): char name[80]; struct input_id id
    // (4x u16 = 8 bytes); u32 ff_effects_max; s32 absmax/absmin/absfuzz/absflat
    // each [ABS_CNT=64]. Total = 80 + 8 + 4 + 4*64*4 = 1116 bytes.
    private const int NameSize = 80;
    private const int AbsCnt = 64;
    public const int UinputUserDevSize = NameSize + 8 + 4 + 4 * AbsCnt * 4;

    // struct input_event (input.h, 64-bit): struct timeval time (2x long =
    // 16 bytes on x86_64); u16 type; u16 code; s32 value. Total = 24 bytes.
    public const int InputEventSize = 16 + 2 + 2 + 4;

    public const ushort EvSyn = 0x00;
    public const ushort EvKey = 0x01;
    public const ushort EvRel = 0x02;
    public const ushort EvAbs = 0x03;

    public const ushort SynReport = 0;

    public const ushort AbsX = 0x00;
    public const ushort AbsY = 0x01;

    public const ushort RelX = 0x00;
    public const ushort RelY = 0x01;
    public const ushort RelHWheel = 0x06;
    public const ushort RelWheel = 0x08;

    public const int BtnLeft = 0x110;
    public const int BtnRight = 0x111;
    public const int BtnMiddle = 0x112;

    // asm-generic/ioctl.h direction/shift layout (standard on x86_64 Linux).
    private const uint IocWrite = 1;
    private const int IocNrShift = 0;
    private const int IocTypeShift = 8;
    private const int IocSizeShift = 16;
    private const int IocDirShift = 30;

    private static nuint Ioc(uint dir, uint type, uint nr, uint size) =>
        (nuint)((dir << IocDirShift) | (type << IocTypeShift) | (nr << IocNrShift) | (size << IocSizeShift));

    private static nuint IoW(uint type, uint nr, uint size) => Ioc(IocWrite, type, nr, size);
    private static nuint Io(uint type, uint nr) => Ioc(0, type, nr, 0);

    private const uint UinputIoctlBase = 'U';
    private const uint SizeofInt = 4;

    public static readonly nuint UiDevCreate = Io(UinputIoctlBase, 1);
    public static readonly nuint UiDevDestroy = Io(UinputIoctlBase, 2);
    public static readonly nuint UiSetEvBit = IoW(UinputIoctlBase, 100, SizeofInt);
    public static readonly nuint UiSetKeyBit = IoW(UinputIoctlBase, 101, SizeofInt);
    public static readonly nuint UiSetRelBit = IoW(UinputIoctlBase, 102, SizeofInt);
    public static readonly nuint UiSetAbsBit = IoW(UinputIoctlBase, 103, SizeofInt);
    public static readonly nuint UiSetPropBit = IoW(UinputIoctlBase, 110, SizeofInt);

    /// <summary>
    /// Without this property bit, libinput has no signal that an EV_ABS
    /// device with buttons is a *pointer* rather than e.g. a graphics
    /// tablet. Confirmed live: a device without it opened/wrote/ioctl'd
    /// without any error, but clicks/moves never actually reached any
    /// application - adding this bit fixed it (verified against
    /// gnome-text-editor: click + synthesized typing landed correctly).
    /// </summary>
    public const uint InputPropPointer = 0x00;

    public const string UinputPath = "/dev/uinput";

    public static int OpenWriteNonBlock() => open(UinputPath, OWronly | ONonblock);

    public static int OpenWriteOnlyForProbe() => open(UinputPath, OWronly);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    public static extern int close(int fd);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    public static extern nint write(int fd, byte[] buf, nuint count);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static extern int ioctl_int(int fd, nuint request, int arg);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static extern int ioctl_noarg(int fd, nuint request);

    /// <summary>Builds the raw 1116-byte `struct uinput_user_dev` payload written via `write()` to configure the device before <see cref="UiDevCreate"/>.</summary>
    public static byte[] BuildUserDev(string name, int absXMax, int absYMax)
    {
        var buf = new byte[UinputUserDevSize];
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        Array.Copy(nameBytes, buf, Math.Min(nameBytes.Length, NameSize - 1));

        int offset = NameSize;
        // input_id: bustype, vendor, product, version - all zero (BUS_VIRTUAL not required for uinput to accept the device).
        offset += 8;
        // ff_effects_max
        offset += 4;

        void WriteAbsSlot(int arrayIndex, ushort axis, int value)
        {
            int pos = offset + arrayIndex * AbsCnt * 4 + axis * 4;
            BitConverter.GetBytes(value).CopyTo(buf, pos);
        }

        WriteAbsSlot(0, AbsX, absXMax); // absmax[ABS_X]
        WriteAbsSlot(0, AbsY, absYMax); // absmax[ABS_Y]
        WriteAbsSlot(1, AbsX, 0);       // absmin[ABS_X]
        WriteAbsSlot(1, AbsY, 0);       // absmin[ABS_Y]
        return buf;
    }

    /// <summary>Builds one raw 24-byte `struct input_event`.</summary>
    public static byte[] BuildEvent(ushort type, ushort code, int value)
    {
        var buf = new byte[InputEventSize];
        // timeval sec/usec left zero - the kernel doesn't require a caller-supplied timestamp for uinput writes.
        BitConverter.GetBytes(type).CopyTo(buf, 16);
        BitConverter.GetBytes(code).CopyTo(buf, 18);
        BitConverter.GetBytes(value).CopyTo(buf, 20);
        return buf;
    }
}

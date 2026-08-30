using System.Runtime.InteropServices;

namespace UICtl.Core.Interop;

/// <summary>
/// Shared architecture guard for this project's hand-rolled native struct
/// offsets (X11ImageInterop's XImage, X11WindowInterop's
/// XClientMessageEvent, UinputInterop's input_event/uinput_user_dev/ioctl
/// encoding) - every one of them was derived assuming the standard 64-bit
/// Linux LP64 data model with natural (unpacked) struct alignment and
/// little-endian byte order: `int` is 4 bytes, `long`/pointer are 8 bytes
/// aligned to 8, no compiler-inserted packing.
///
/// x86_64's SysV ABI and AArch64's Linux ABI (AAPCS64) both fit this
/// description exactly - same primitive sizes, same natural-alignment
/// rules, both little-endian by default on every mainstream Linux
/// distribution's aarch64 port (Ubuntu, Debian, Raspberry Pi OS, Fedora,
/// Amazon Linux/Graviton). That means the offsets hand-verified on this
/// project's x86_64 dev machine (see X11ImageInterop's class doc comment)
/// are reasoned, not guessed, to hold on arm64 too - but **not
/// independently live-verified** there, since no arm64 machine has been
/// available to confirm against (same "flag the untested gap rather than
/// assume" precedent as X11ImageInterop's XWayland root-capture note - see
/// ENGINEERING.md's "Coordinate spaces" section).
///
/// Deliberately excludes every other architecture rather than trying to
/// reason about each: 32-bit targets (arm/x86) use a 4-byte `long`,
/// changing every offset after the first pointer/long field; and a
/// handful of Linux ports (alpha/mips/powerpc/sparc, all irrelevant to
/// this project's desktop-GNOME use case) use a different ioctl
/// direction-bit layout than the `asm-generic/ioctl.h` one x86_64/arm64
/// share, which `UinputInterop`'s ioctl encoding assumes.
/// </summary>
internal static class NativeAbi
{
    public static bool IsSupported =>
        BitConverter.IsLittleEndian &&
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    /// <summary>Throws a clear, actionable error instead of silently misreading a hand-rolled struct offset on an architecture this project's native interop hasn't reasoned about (or has reasoned about but not live-verified - see class doc comment).</summary>
    public static void EnsureSupported(string what)
    {
        if (!IsSupported)
        {
            string endian = BitConverter.IsLittleEndian ? "little-endian" : "big-endian";
            throw new UiCtlException($"{what} assumes little-endian x86_64/arm64 Linux struct layout, not {RuntimeInformation.ProcessArchitecture} ({endian}) - refusing to read/write possibly-wrong native offsets");
        }
    }
}

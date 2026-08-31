namespace UICtl.Core;

/// <summary>
/// A tiny file-based signal telling `uictl-gui`'s toast to stay hidden
/// while a Wayland screen capture is in flight - the toast is a floating,
/// undecorated window Wayland gives this project no API to position (no
/// `wlr-layer-shell` support confirmed on this GNOME/Mutter build, live-
/// verified via a raw Wayland registry dump - see ENGINEERING.md's
/// "Wayland screenshot" section), so a leftover toast from an earlier
/// command can end up sitting in the middle of a screenshot. Rather than
/// solve positioning (confirmed not possible here), this just keeps the
/// toast out of the frame for the moment that matters.
///
/// Deliberately a polled file, not a new daemon->uictl-gui push channel:
/// the daemon has no existing way to signal `uictl-gui` (only the reverse -
/// `uictl-gui` connects out to the daemon's socket) building a second,
/// bidirectional IPC path just for this cosmetic concern isn't
/// proportionate. `uictl-gui`'s Program.cs already polls every 300ms for
/// activity log entries; it checks this same file on that existing tick
/// rather than adding a second timer. A stale flag from a killed/crashed
/// capture process (Suppress() called, Unsuppress() never reached) auto-
/// expires after a few seconds rather than leaving the toast hidden
/// forever - see IsActive's own comment for the exact window.
/// </summary>
internal static class ToastSuppression
{
    private static readonly string FlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".uictl", "toast-suppress");

    /// <summary>Generous relative to a real capture (~0.6-2s typical, occasionally longer under contention - see ENGINEERING.md) so a slow-but-legitimate capture doesn't have its suppression expire mid-way, while still bounding a crashed/killed capturer's flag to a few seconds, not forever.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);

    public static void Suppress()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FlagPath)!);
            File.WriteAllText(FlagPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        }
        catch { /* best effort - worst case the toast stays visible during this one capture */ }
    }

    public static void Unsuppress()
    {
        try { File.Delete(FlagPath); }
        catch { /* best effort */ }
    }

    /// <summary>Checked by uictl-gui, not the daemon - true if a capture is (recently) in flight.</summary>
    public static bool IsActive()
    {
        try
        {
            if (!File.Exists(FlagPath)) return false;
            long ms = long.Parse(File.ReadAllText(FlagPath));
            return DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(ms) < MaxAge;
        }
        catch
        {
            return false;
        }
    }
}

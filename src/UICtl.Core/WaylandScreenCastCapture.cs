using System.Diagnostics;

namespace UICtl.Core;

/// <summary>
/// Screenshot.cs's Wayland entry point for the ScreenCast+PipeWire path -
/// spawns the isolated `uictl-screencapture` helper (see its own Program.cs
/// doc comment for why this is a separate process, not a direct in-process
/// call: the PipeWire native layer is genuinely risky, unproven interop
/// that must not be able to crash the daemon). Any failure here - helper
/// not found, non-zero exit, timeout, an actual crash - is treated as
/// "ScreenCast unavailable this time", not a hard error: Screenshot.cs
/// falls back to WaylandScreenshotBackend.cs's proven, always-works (but
/// per-call-dialog) portal path.
/// </summary>
internal static class WaylandScreenCastCapture
{
    /// <summary>
    /// Generous relative to the ~1-2s this genuinely takes once portal
    /// session negotiation and PipeWire format negotiation both succeed
    /// (confirmed live) - bounds the case where either hangs instead of
    /// failing cleanly, so a stuck ScreenCast attempt doesn't block the
    /// screenshot indefinitely before falling back.
    /// </summary>
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);

    /// <returns>Captured PNG bytes, or null if the ScreenCast path isn't available/failed this time (caller should fall back).</returns>
    public static byte[]? TryCapturePng()
    {
        string? exe = ResolveExecutable();
        if (exe is null)
        {
            Console.Error.WriteLine("uictl: could not find \"uictl-screencapture\" on $PATH or in the sibling dev build output - falling back to the per-call-dialog screenshot path");
            return null;
        }

        string tmpPath = Path.Combine(Path.GetTempPath(), $"uictl-screencast-{Guid.NewGuid():N}.png");
        try
        {
            // No stream redirection, matching GuiLauncher.cs's launch of
            // uictl-gui - avoids any risk of a pipe-buffer deadlock from
            // not actively draining a redirected stream. The child's
            // stdout/stderr inherit the daemon's own OS-level file
            // descriptors *as they existed at process start* - confirmed
            // live these do NOT end up in daemon.log: DaemonServer.cs's
            // RedirectConsoleToLogFile only reassigns .NET's own
            // Console.Out/Error properties (Console.SetOut/SetError), not
            // an OS-level dup2 of fd 1/2, so a spawned child inherits
            // whatever fd 1/2 pointed to before that redirection ran
            // (typically the terminal/session that started the daemon).
            // uictl-screencapture's diagnostics are still visible when
            // testing by hand from a foreground daemon, just not
            // persisted to daemon.log - acceptable for now since the
            // daemon-side caller here doesn't depend on reading them
            // (only exit code / output file matter for the fallback
            // decision); revisit if these diagnostics turn out to be
            // needed for debugging a live daemon that's normally
            // backgrounded, not run in the foreground.
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add(tmpPath);
            using var process = Process.Start(psi);
            if (process is null)
            {
                Console.Error.WriteLine("uictl: Process.Start for uictl-screencapture returned null - falling back");
                return null;
            }

            bool exited = process.WaitForExit((int)ProcessTimeout.TotalMilliseconds);
            if (!exited)
            {
                Console.Error.WriteLine($"uictl: uictl-screencapture didn't finish within {ProcessTimeout.TotalSeconds:F0}s - killing it and falling back");
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }
            if (process.ExitCode != 0 || !File.Exists(tmpPath))
            {
                Console.Error.WriteLine($"uictl: uictl-screencapture exited {process.ExitCode} (see daemon.log above for its own stderr) - falling back");
                return null;
            }

            return File.ReadAllBytes(tmpPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"uictl: failed to run uictl-screencapture ({ex.Message}) - falling back");
            return null;
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { /* best effort */ }
        }
    }

    /// <summary>Same PATH-first-then-sibling-dev-build-output resolution as UICtl.Ipc/GuiLauncher.cs's uictl-gui lookup - duplicated rather than shared since Core has no reference to Ipc (the dependency runs the other way).</summary>
    private static string? ResolveExecutable()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(dir, "uictl-screencapture");
            if (File.Exists(candidate)) return candidate;
        }

        string cliBinNet = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string? cliBinConfig = Path.GetDirectoryName(cliBinNet);
        string? cliBin = cliBinConfig is null ? null : Path.GetDirectoryName(cliBinConfig);
        string? cliProject = cliBin is null ? null : Path.GetDirectoryName(cliBin);
        string? srcDir = cliProject is null ? null : Path.GetDirectoryName(cliProject);
        if (srcDir is null || cliBinConfig is null) return null;

        string devCandidate = Path.Combine(srcDir, "UICtl.ScreenCapture", "bin", Path.GetFileName(cliBinConfig), "net10.0", "uictl-screencapture");
        return File.Exists(devCandidate) ? devCandidate : null;
    }
}

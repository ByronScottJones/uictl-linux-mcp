using UICtl.Core;
using UICtl.Core.Interop;

namespace UICtl.ScreenCapture;

/// <summary>
/// `uictl-screencapture &lt;output-png-path&gt;` - the ScreenCast+PipeWire
/// alternative to WaylandScreenshotBackend.cs's per-call-dialog Screenshot
/// portal, run as its own process rather than in-daemon (see
/// ENGINEERING.md's "Wayland screenshot" section for the full backstory).
///
/// **Why a separate process, not a class the daemon calls directly**: the
/// PipeWire half of this (PipeWireCapture.cs) is genuinely risky, unproven
/// native interop - hand-rolled P/Invoke, function-pointer callbacks, raw
/// unmanaged memory reads for pixel data, manually-encoded binary SPA POD
/// structures with no compiler/header to check them against on this
/// machine. A wrong struct offset or callback signature is a native ABI
/// mismatch - a segfault, not a catchable .NET exception - and would take
/// the *entire daemon* down with it if it ran in-process, since
/// DaemonServer.cs handles one connection at a time in the same process
/// space every other command runs in. Isolating it here means a crash
/// only fails *this* capture attempt: the daemon-side caller (Screenshot.cs)
/// treats any non-zero exit/timeout/crash as "ScreenCast unavailable this
/// time" and falls back to the proven WaylandScreenshotBackend.cs path
/// (a real click, but always works) rather than losing the daemon.
///
/// The portal session setup (WaylandScreenCastBackend.cs - CreateSession/
/// SelectSources/Start/OpenPipeWireRemote) is safe, ordinary Tmds.DBus code
/// with no crash risk of its own, and is reused as-is from UICtl.Core; only
/// the PipeWire native layer below it needed this isolation.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: uictl-screencapture <output-png-path>");
            return 2;
        }
        string outputPath = args[0];

        WaylandScreenCastBackend.Session? session = null;
        try
        {
            session = await WaylandScreenCastBackend.StartSessionAsync();
            var (rgba, width, height) = PipeWireCapture.CaptureFrame(session.PipeWireFd, session.NodeId, FrameTimeout);
            byte[] png = PngCodec.Encode(rgba, width, height);
            await File.WriteAllBytesAsync(outputPath, png);
            Console.Error.WriteLine($"uictl-screencapture: wrote {width}x{height} to {outputPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"uictl-screencapture: failed: {ex}");
            return 1;
        }
        finally
        {
            if (session is not null)
            {
                try { await WaylandScreenCastBackend.CloseSessionAsync(session); }
                catch { /* best effort - the portal will eventually clean up an orphaned session on its own */ }
            }
        }
    }
}

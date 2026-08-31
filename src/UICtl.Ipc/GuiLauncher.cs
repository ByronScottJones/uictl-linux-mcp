using System.ComponentModel;
using System.Diagnostics;

namespace UICtl.Ipc;

/// <summary>
/// Starts `uictl-gui` (UICtl.Gui's own executable, not the daemon) so the
/// toast/log-window feature has something to talk to. Fire-and-forget,
/// once per daemon process lifetime (`Lazy&lt;bool&gt;` - same one-time-
/// resource pattern as UinputDevice.cs/TesseractEngine.cs), *not* once
/// per command - GApplication's own single-instance handling (confirmed
/// live: a second launch registers, sees `IsRemote`, forwards, and exits
/// in ~0.3s) makes a redundant launch harmless, but still costs a process
/// spawn there's no reason to pay on every single call.
///
/// `uictl-gui` isn't expected to be co-located with `uictl` the way a
/// packaged/published deployment might place them (this repo's own
/// README has the user build and PATH just `uictl`, per-project bin
/// output otherwise) - resolved via PATH first (the expected real-world
/// case once someone also builds/installs `uictl-gui`), falling back to
/// this solution's own sibling dev-build output for a friction-free
/// build-from-source dev loop.
/// </summary>
internal static class GuiLauncher
{
    private static readonly Lazy<bool> Started = new(() => TryStart());

    public static void EnsureStarted() => _ = Started.Value;

    /// <summary>`log.show` - always spawns fresh rather than checking `Started` first, since GApplication's single-instance handling (see class doc comment) makes that redundant: if an instance is already running this is a ~0.3s handoff to it, and if not, this call itself becomes the primary, already showing the window `--show-log` asks for.</summary>
    public static void ShowLogWindow() => TryStart("--show-log");

    private static bool TryStart(string? extraArg = null)
    {
        string? exe = ResolveExecutable();
        if (exe is null)
        {
            Console.Error.WriteLine("uictl: could not find \"uictl-gui\" on $PATH or in the sibling dev build output - the toast/log-show window won't be available this session");
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (extraArg is not null) psi.ArgumentList.Add(extraArg);
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex)
        {
            Console.Error.WriteLine($"uictl: failed to start \"{exe}\" ({ex.Message}) - the toast/log-show window won't be available this session");
            return false;
        }
    }

    private static string? ResolveExecutable()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(dir, "uictl-gui");
            if (File.Exists(candidate)) return candidate;
        }

        // Dev fallback: .../src/UICtl.Cli/bin/<config>/net10.0 -> .../src/UICtl.Gui/bin/<config>/net10.0/uictl-gui
        string cliBinNet = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar); // .../src/UICtl.Cli/bin/<config>/net10.0
        string? cliBinConfig = Path.GetDirectoryName(cliBinNet);                          // .../src/UICtl.Cli/bin/<config>
        string? cliBin = cliBinConfig is null ? null : Path.GetDirectoryName(cliBinConfig); // .../src/UICtl.Cli/bin
        string? cliProject = cliBin is null ? null : Path.GetDirectoryName(cliBin);        // .../src/UICtl.Cli
        string? srcDir = cliProject is null ? null : Path.GetDirectoryName(cliProject);    // .../src
        if (srcDir is null || cliBinConfig is null) return null;

        string devCandidate = Path.Combine(srcDir, "UICtl.Gui", "bin", Path.GetFileName(cliBinConfig), "net10.0", "uictl-gui");
        return File.Exists(devCandidate) ? devCandidate : null;
    }
}

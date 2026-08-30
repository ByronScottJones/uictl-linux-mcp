using System.ComponentModel;
using System.Diagnostics;

namespace UICtl.Core;

/// <summary>
/// `clipboard.get`/`clipboard.set` - shells out to wl-copy/wl-paste
/// (Wayland) or xclip (X11), selected by the same session-type detection
/// as the window backend (SessionDetection.cs). No native protocol
/// implementation exists yet - ENGINEERING.md flags this as a possible
/// follow-up if shelling out proves fragile.
///
/// Live-verified on this machine:
/// - `wl-paste`'s default output always appends a trailing newline
///   regardless of what was actually on the clipboard (confirmed: set
///   text with no trailing newline via `wl-copy`, then plain `wl-paste`
///   still added one) - `--no-newline` is required for byte-exact
///   round-tripping. `xclip -o` needs no equivalent flag; its output
///   already matches exactly what was set.
/// - `wl-copy` and `xclip -selection clipboard` (no `-o`, reading the
///   text to set from stdin) both fork into the background to keep
///   serving the clipboard/selection after the invoked process exits
///   *only when they succeed* - a failing invocation (bad args, no
///   display) exits immediately without forking. The invoked process
///   itself always exits promptly either way (confirmed ~150-300ms).
/// - The forked child inherits this process's redirected stdout/stderr
///   pipes, so draining them via `ReadToEndAsync` after a *successful*
///   set waits for an EOF that never comes - confirmed live twice: first
///   as an outright hang (a `clipboard set` call never returned until
///   manually killed, leaving an orphaned `wl-copy` behind), then again
///   as a several-second stall per call even after bounding the read
///   with a timeout (draining two pipes at up to ~2s each, every single
///   successful call, just to throw the result away). The actual fix
///   isn't a bigger timeout - it's not reading output at all when a
///   call both (a) might fork and (b) succeeded, since neither `Set`
///   caller needs that output on success. `RunProcess`'s
///   `mayForkOnSuccess` flag opts a call out of the read in exactly
///   that case; `Get` (which never forks) always reads normally, and any
///   failing call (which never forks) is always safe to read too.
/// - An empty/non-text clipboard makes both tools exit non-zero with an
///   explanatory stderr message ("Nothing is copied" / "Error: target
///   STRING not available") - surfaced as a UiCtlException rather than
///   an empty string, since MCP_INTERFACE.md's uictl_clipboard_get
///   always returns a real string, never null/empty-on-failure.
/// </summary>
public static class Clipboard
{
    public static string Get()
    {
        var (exitCode, stdout, stderr) = SessionDetection.HasWayland
            ? RunProcess("wl-paste", new[] { "--no-newline" })
            : RunProcess("xclip", new[] { "-selection", "clipboard", "-o" });
        if (exitCode != 0)
            throw new UiCtlException($"clipboard is empty or holds no text ({stderr.Trim()})");
        return stdout;
    }

    public static void Set(string text)
    {
        var (exitCode, _, stderr) = SessionDetection.HasWayland
            ? RunProcess("wl-copy", Array.Empty<string>(), text, mayForkOnSuccess: true)
            : RunProcess("xclip", new[] { "-selection", "clipboard" }, text, mayForkOnSuccess: true);
        if (exitCode != 0)
            throw new UiCtlException($"failed to set clipboard ({stderr.Trim()})");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, string[] args, string? stdin = null, bool mayForkOnSuccess = false) =>
        AsyncBridge.RunSync(async () =>
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            Process process;
            try
            {
                process = Process.Start(psi) ?? throw new UiCtlException($"could not start \"{fileName}\"");
            }
            catch (Win32Exception ex)
            {
                throw new UiCtlException($"could not start \"{fileName}\" ({ex.Message}) - is wl-clipboard/xclip installed? See scripts/preflight.sh.");
            }

            using (process)
            {
                if (stdin is not null)
                {
                    await process.StandardInput.WriteAsync(stdin);
                    process.StandardInput.Close();
                }

                await process.WaitForExitAsync();

                if (mayForkOnSuccess && process.ExitCode == 0)
                    return (process.ExitCode, "", ""); // see class doc comment - draining output here would wait on the forked child's inherited pipe

                // Concurrent, not sequential - a large enough stdout+stderr
                // pair could otherwise deadlock (the child blocks writing to
                // whichever pipe fills up first while we're still draining
                // the other one). Not a real risk for these tools' actual
                // output (a one-line error message at most), but free to
                // get right.
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdoutTask, stderrTask);
                return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
            }
        });
}

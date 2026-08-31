namespace UICtl.Core;

/// <summary>
/// Tmds.DBus (like ScreenCaptureKit on macOS) is async-only, but
/// CommandDispatcher's dispatch is synchronous - see ENGINEERING.md. This
/// bridges the two the same way macOS's AsyncBridge.swift does: run the
/// async work via Task.Run (hopping off any calling context, though a
/// console/daemon process has none by default) and block on it.
/// </summary>
internal static class AsyncBridge
{
    /// <summary>
    /// Bound for AT-SPI/D-Bus operations that want a correct answer, not a
    /// fast-fail health check (see Permissions.cs's own separate, shorter
    /// 3s probe timeout for that case) - see ENGINEERING.md's "Known daemon
    /// reliability gap" section. Set well above the one confirmed-live slow
    /// case (~48s, GNOME Shell's own D-Bus service occasionally slow to
    /// answer, cause unconfirmed and outside this project's control) so
    /// this essentially never fires under legitimate, if unusually slow,
    /// GNOME Shell response times - its only job is bounding
    /// DaemonServer.cs's single-connection worst case to something finite
    /// instead of forever, not making normal calls faster.
    /// </summary>
    public static readonly TimeSpan DefaultDBusTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// timeout is opt-in (null = unbounded, the original behavior) since
    /// AsyncBridge also bridges non-D-Bus async work (FeedbackGitHub.cs's
    /// HttpClient calls, Clipboard.cs's forked-process I/O) that shouldn't
    /// be affected by a change scoped to the AT-SPI/D-Bus timeout gap.
    /// Bounds the *wait*, not the underlying call - Tmds.DBus has no way to
    /// cancel an in-flight method call once sent, so a timed-out call's
    /// Task keeps running to completion on its own thread-pool thread in
    /// the background rather than actually stopping; harmless; see
    /// Permissions.cs's TryWithTimeout for the same tradeoff already
    /// accepted there.
    /// </summary>
    public static T RunSync<T>(Func<Task<T>> func, TimeSpan? timeout = null)
    {
        var task = Task.Run(func);
        if (timeout is { } t && !task.Wait(t))
            throw new UiCtlException($"AT-SPI/D-Bus call timed out after {t.TotalSeconds:F0}s - GNOME Shell's own D-Bus service may be slow or unresponsive right now");
        return task.GetAwaiter().GetResult();
    }

    public static void RunSync(Func<Task> func, TimeSpan? timeout = null)
    {
        var task = Task.Run(func);
        if (timeout is { } t && !task.Wait(t))
            throw new UiCtlException($"AT-SPI/D-Bus call timed out after {t.TotalSeconds:F0}s - GNOME Shell's own D-Bus service may be slow or unresponsive right now");
        task.GetAwaiter().GetResult();
    }
}

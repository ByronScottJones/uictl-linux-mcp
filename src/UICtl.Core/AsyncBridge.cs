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
    /// 3s probe timeout for that case) - see ENGINEERING.md's "Daemon
    /// reliability: AT-SPI/D-Bus call timeouts" section. Set well above the
    /// one confirmed-live slow case (~48s, GNOME Shell's own D-Bus service
    /// occasionally slow to answer, cause unconfirmed and outside this
    /// project's control) so this essentially never fires under legitimate,
    /// if unusually slow, response times - its only job is bounding
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
    ///
    /// Uses Task.WaitAny, not task.Wait(timeout) - caught live by
    /// AsyncBridgeTests: Task.Wait(TimeSpan) rethrows a faulted task's
    /// exception (wrapped in AggregateException) as soon as it completes,
    /// even well within the timeout - so a call that fails *fast* (the
    /// common case for a real error, not a hang) would surface as a
    /// generic AggregateException instead of its original exception type/
    /// message. WaitAny only reports which task finished first and never
    /// throws, so the *separate* GetAwaiter().GetResult() below is the only
    /// thing that unwraps/rethrows - for both the "completed" and
    /// "genuinely timed out but finished on its own later" cases.
    /// </summary>
    public static T RunSync<T>(Func<Task<T>> func, TimeSpan? timeout = null)
    {
        var task = Task.Run(func);
        if (timeout is { } t && Task.WaitAny([task], t) == -1)
            throw new UiCtlException($"AT-SPI/D-Bus call timed out after {t.TotalSeconds:F0}s - an AT-SPI/D-Bus service (GNOME Shell, Mutter, or at-spi2-registryd, depending on the call) may be slow or unresponsive right now");
        return task.GetAwaiter().GetResult();
    }

    public static void RunSync(Func<Task> func, TimeSpan? timeout = null)
    {
        var task = Task.Run(func);
        if (timeout is { } t && Task.WaitAny([task], t) == -1)
            throw new UiCtlException($"AT-SPI/D-Bus call timed out after {t.TotalSeconds:F0}s - an AT-SPI/D-Bus service (GNOME Shell, Mutter, or at-spi2-registryd, depending on the call) may be slow or unresponsive right now");
        task.GetAwaiter().GetResult();
    }
}

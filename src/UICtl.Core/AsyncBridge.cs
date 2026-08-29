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
    public static T RunSync<T>(Func<Task<T>> func) => Task.Run(func).GetAwaiter().GetResult();

    public static void RunSync(Func<Task> func) => Task.Run(func).GetAwaiter().GetResult();
}

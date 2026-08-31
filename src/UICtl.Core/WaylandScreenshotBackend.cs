using Tmds.DBus;
using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// Wayland screenshot capture via the xdg-desktop-portal Screenshot
/// interface - see PortalRegistration.cs's doc comment for the app-id
/// registration dance this needs first, and Interop/PortalInterfaces.cs's
/// doc comment for why `interactive: true` - with its real per-call
/// consent dialog - is the only path that actually completes here.
///
/// **This means every Wayland screenshot needs a manual click** - there
/// is no zero-dialog path through this specific portal interface in this
/// GNOME build (the `interactive: false` "reuse a stored grant" variant
/// fails outright with no stored permission ever getting created via this
/// interface, at least for a non-sandboxed host app like this one - and a
/// follow-up investigation into whether a real `parent_window` handle
/// would change that hit an unrelated GirCore.GdkWayland-4.0 binding bug
/// before it could even be tested, see ENGINEERING.md). ScreenCast+PipeWire
/// (WaylandScreenCastBackend.cs) is the real one-time-consent alternative.
/// </summary>
internal static class WaylandScreenshotBackend
{
    public static byte[] CapturePng() => AsyncBridge.RunSync(async () =>
    {
        (Connection connection, string sender) = await PortalRegistration.ConnectAsync();

        string token = "uictl_" + Guid.NewGuid().ToString("N")[..12];
        var requestPath = new ObjectPath($"/org/freedesktop/portal/desktop/request/{sender}/{token}");

        var tcs = new TaskCompletionSource<(uint, IDictionary<string, object>)>();
        var request = connection.CreateProxy<IPortalRequest>(PortalRegistration.ServiceName, requestPath);
        using var subscription = await request.WatchResponseAsync(r => tcs.TrySetResult(r));

        var portal = connection.CreateProxy<IScreenshotPortal>(PortalRegistration.ServiceName, PortalRegistration.ServicePath);
        var options = new Dictionary<string, object> { ["handle_token"] = token, ["interactive"] = true };
        Console.Error.WriteLine("uictl: requesting Wayland screenshot via xdg-desktop-portal - a \"Take Screenshot\" consent dialog will appear and needs a manual click (see AGENTS.md)");
        await portal.ScreenshotAsync("", options);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(2)));
        if (completed != tcs.Task)
            throw new UiCtlException("screenshot portal request timed out waiting for the consent dialog to be answered");

        var (response, results) = tcs.Task.Result;
        if (response == 1)
            throw new UiCtlException("screenshot cancelled (consent dialog was dismissed/denied)");
        if (response != 0 || !results.TryGetValue("uri", out var uriObj))
            throw new UiCtlException($"screenshot portal request failed (response code {response})");

        var uri = new Uri((string)uriObj);
        return await File.ReadAllBytesAsync(uri.LocalPath);
    });
}

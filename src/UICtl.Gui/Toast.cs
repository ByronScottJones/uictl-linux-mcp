using System.Text.Json;

namespace UICtl.Gui;

/// <summary>
/// The "toast per call" indicator (MCP_INTERFACE.md's Activity log
/// section) - a single small window, not a native desktop notification
/// (see Program.cs's doc comment for why: avoids OS notification spam
/// during a busy automation session). Updates in place and resets its
/// fade timer on every new command (Program.cs); fades out via a CSS
/// opacity transition, not an abrupt hide, after 5s of no new activity.
///
/// Window placement is whatever GNOME's Wayland compositor decides -
/// Wayland deliberately gives a client no API to position its own
/// top-level window (unlike X11/Windows/macOS), so this can't pin itself
/// to a screen corner the way a real desktop notification does. Flagging
/// this as a known, structural Wayland limitation rather than something
/// to keep chasing.
/// </summary>
internal sealed class Toast
{
    private readonly Adw.Window _window;
    private readonly Gtk.Label _label;

    public Toast(Adw.Application app)
    {
        var cssProvider = Gtk.CssProvider.New();
        cssProvider.LoadFromString(".uictl-toast { transition: opacity 400ms ease-out; opacity: 1; } .uictl-toast.uictl-faded { opacity: 0; }");
        Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, cssProvider, 600 /* GTK_STYLE_PROVIDER_PRIORITY_APPLICATION */);

        _window = Adw.Window.New();
        _window.SetDefaultSize(360, 72);
        _window.SetTitle("uictl activity");
        _window.SetDecorated(false);
        _window.SetResizable(false);
        _window.AddCssClass("uictl-toast");
        app.AddWindow(_window);

        _label = Gtk.Label.New("");
        _label.SetWrap(true);
        _label.SetMarginTop(12);
        _label.SetMarginBottom(12);
        _label.SetMarginStart(16);
        _label.SetMarginEnd(16);
        _window.SetContent(_label);
    }

    public void Update(JsonElement entry)
    {
        string command = entry.GetProperty("command").GetString() ?? "?";
        bool success = entry.TryGetProperty("success", out var s) && s.GetBoolean();
        double durationMs = entry.TryGetProperty("durationMs", out var d) ? d.GetDouble() : 0;
        string icon = success ? "✓" : "✗";

        _label.SetText($"{icon} {command} ({durationMs:F0}ms)");
        _window.RemoveCssClass("uictl-faded");
        _window.SetVisible(true);
        _window.Present();
    }

    public void FadeOut()
    {
        _window.AddCssClass("uictl-faded");
        // Fully hide once the CSS transition has had time to finish, so it
        // doesn't linger as an invisible-but-mapped window.
        GLib.Functions.TimeoutAdd(0, 450, () =>
        {
            if (_window.HasCssClass("uictl-faded")) // a new call may have already un-faded it
                _window.SetVisible(false);
            return false;
        });
    }
}

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
        // background-color here must be a fully opaque color, not
        // libadwaita's `.card` class (its background token is
        // intentionally semi-transparent, meant to be layered over an
        // already-opaque window) - confirmed live: pairing an opaque-
        // looking `.card` box with a transparent window made the whole
        // toast see-through and hard to read against the desktop behind
        // it. @window_bg_color is the theme's normal, fully-opaque window
        // background, so only the corners end up transparent (clipped by
        // the compositor via border-radius), not the text-bearing interior.
        var cssProvider = Gtk.CssProvider.New();
        cssProvider.LoadFromString("""
            window.uictl-toast {
                background-color: @window_bg_color;
                border-radius: 12px;
                border: 1px solid alpha(currentColor, 0.15);
                transition: opacity 400ms ease-out;
                opacity: 1;
            }
            window.uictl-toast.uictl-faded { opacity: 0; }
            """);
        Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, cssProvider, 600 /* GTK_STYLE_PROVIDER_PRIORITY_APPLICATION */);

        _window = Adw.Window.New();
        _window.SetDefaultSize(360, 84);
        _window.SetTitle("uictl activity");
        _window.SetDecorated(false);
        _window.SetResizable(false);
        _window.AddCssClass("uictl-toast");
        app.AddWindow(_window);

        // A branding row ("uictl" + icon) above the per-call status line -
        // an undecorated window has no titlebar to show its own title in
        // (that's the whole point, for a toast), so without this there's
        // nothing on screen identifying which app the popup belongs to.
        var appIcon = Gtk.Image.NewFromIconName("utilities-terminal-symbolic");
        appIcon.SetPixelSize(14);
        appIcon.AddCssClass("dim-label");

        var appLabel = Gtk.Label.New("uictl-linux-mcp");
        appLabel.AddCssClass("caption-heading");
        appLabel.AddCssClass("dim-label");
        appLabel.SetXalign(0);

        var brandRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        brandRow.Append(appIcon);
        brandRow.Append(appLabel);

        _label = Gtk.Label.New("");
        _label.SetWrap(true);
        _label.SetXalign(0);

        // The window itself (not this box) carries the opaque background,
        // rounded corners, and border set up above - this box is just
        // layout/padding.
        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        card.SetMarginTop(12);
        card.SetMarginBottom(12);
        card.SetMarginStart(16);
        card.SetMarginEnd(16);
        card.Append(brandRow);
        card.Append(_label);

        _window.SetContent(card);
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

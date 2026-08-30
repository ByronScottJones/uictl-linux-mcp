using System.CommandLine;

namespace UICtl.Cli.Commands;

internal static class WindowCommands
{
    public static Command Activate()
    {
        var app = new Argument<string>("app") { Description = "App to bring to front (name substring or pid)." };
        var window = new Option<long?>("--window") { Description = "Disambiguate among that app's windows (from `windows`), if it has more than one." };

        var cmd = new Command("activate", "Bring an app's window to the front. Wayland needs the companion GNOME Shell extension - see gnome-extension/README.md.");
        cmd.Add(app);
        cmd.Add(window);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?> { ["app"] = pr.GetValue(app) };
            if (pr.GetValue(window) is { } w) args["window"] = w;
            return await CliRunner.RunAsync("activate", args, ct);
        });
        return cmd;
    }

    public static Command Focus()
    {
        var cmd = new Command("focus", "Pin focus to a window across a sequence of actions, even if a human clicks elsewhere meanwhile.");
        cmd.Add(Hold());
        cmd.Add(Release());
        cmd.Add(Status());
        return cmd;
    }

    private static Command Hold()
    {
        var app = new Option<string?>("--app") { Description = "App whose window to hold focus on (name substring or pid). Either this or --window is required." };
        var window = new Option<long?>("--window") { Description = "Window id (from `windows`) to hold focus on. Either this or --app is required." };

        var cmd = new Command("hold", "Activate a window and keep re-activating it before every click/move/scroll/key/type call, until `focus release`.");
        cmd.Add(app);
        cmd.Add(window);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?>();
            if (pr.GetValue(app) is { } a) args["app"] = a;
            if (pr.GetValue(window) is { } w) args["window"] = w;
            return await CliRunner.RunAsync("focus.hold", args, ct);
        });
        return cmd;
    }

    private static Command Release()
    {
        var cmd = new Command("release", "Stop pinning focus, and best-effort restore focus to whatever it was before `focus hold`.");
        cmd.SetAction(async (_, ct) => await CliRunner.RunAsync("focus.release", new Dictionary<string, object?>(), ct));
        return cmd;
    }

    private static Command Status()
    {
        var cmd = new Command("status", "Report whether focus is currently held, and on what.");
        cmd.SetAction(async (_, ct) => await CliRunner.RunAsync("focus.status", new Dictionary<string, object?>(), ct));
        return cmd;
    }
}

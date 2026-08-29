using System.CommandLine;

namespace UICtl.Cli.Commands;

internal static class CaptureCommands
{
    public static Command Elements()
    {
        var window = new Option<long?>("--window") { Description = "Window id (from `windows`). Either this or --app is required." };
        var app = new Option<string?>("--app") { Description = "App to inspect (name substring or pid)." };
        var role = new Option<string?>("--role") { Description = "Only include elements with this AT-SPI role name (e.g. \"push button\", \"text\")." };
        var title = new Option<string?>("--title") { Description = "Only include elements whose name contains this substring." };
        var maxDepth = new Option<int?>("--maxDepth") { Description = "Maximum tree depth to walk." };
        var maxElements = new Option<int?>("--maxElements") { Description = "Stop after this many matching elements." };

        var cmd = new Command("elements", "Walk a window's AT-SPI accessibility tree.");
        cmd.Add(window);
        cmd.Add(app);
        cmd.Add(role);
        cmd.Add(title);
        cmd.Add(maxDepth);
        cmd.Add(maxElements);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?>();
            if (pr.GetValue(window) is { } w) args["window"] = w;
            if (pr.GetValue(app) is { } a) args["app"] = a;
            if (pr.GetValue(role) is { } r) args["role"] = r;
            if (pr.GetValue(title) is { } t) args["title"] = t;
            if (pr.GetValue(maxDepth) is { } md) args["maxDepth"] = md;
            if (pr.GetValue(maxElements) is { } me) args["maxElements"] = me;
            return await CliRunner.RunAsync("elements", args, ct);
        });
        return cmd;
    }
}

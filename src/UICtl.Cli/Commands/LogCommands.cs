using System.CommandLine;

namespace UICtl.Cli.Commands;

internal static class LogCommands
{
    public static Command Log()
    {
        var cmd = new Command("log", "Inspect the daemon's activity log - every command it has handled, capped at ~2000 entries.");
        cmd.Add(Export());
        cmd.Add(Show());
        return cmd;
    }

    private static Command Show()
    {
        var cmd = new Command("show", "Open the live activity log window (GTK4/libadwaita) - also hosts the \"commands enabled\" kill switch, which only takes effect while this window is open.");
        cmd.SetAction(async (_, ct) => await CliRunner.RunAsync("log.show", new Dictionary<string, object?>(), ct));
        return cmd;
    }

    private static Command Export()
    {
        var outPath = new Option<string?>("--out") { Description = "Path to write the exported JSON to. Defaults to a timestamped file in the current directory." };

        var cmd = new Command("export", "Write the current activity log to a JSON file.");
        cmd.Add(outPath);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?>();
            if (pr.GetValue(outPath) is { } o) args["out"] = o;
            return await CliRunner.RunAsync("log.export", args, ct);
        });
        return cmd;
    }
}

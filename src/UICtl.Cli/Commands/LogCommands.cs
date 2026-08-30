using System.CommandLine;

namespace UICtl.Cli.Commands;

internal static class LogCommands
{
    public static Command Log()
    {
        var cmd = new Command("log", "Inspect the daemon's activity log - every command it has handled, capped at ~2000 entries.");
        cmd.Add(Export());
        // `log show` (a live GTK4/libadwaita window) is a deliberate
        // follow-up - the data/redaction/export side lands here first,
        // see ENGINEERING.md.
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

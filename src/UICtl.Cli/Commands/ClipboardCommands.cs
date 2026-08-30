using System.CommandLine;

namespace UICtl.Cli.Commands;

internal static class ClipboardCommands
{
    public static Command Clipboard()
    {
        var cmd = new Command("clipboard", "Read or write the system clipboard.");
        cmd.Add(Get());
        cmd.Add(Set());
        return cmd;
    }

    private static Command Get()
    {
        var cmd = new Command("get", "Read the clipboard's current text.");
        cmd.SetAction(async (_, ct) => await CliRunner.RunAsync("clipboard.get", new Dictionary<string, object?>(), ct));
        return cmd;
    }

    private static Command Set()
    {
        var text = new Argument<string>("text") { Description = "Text to place on the clipboard." };

        var cmd = new Command("set", "Replace the clipboard's contents with the given text.");
        cmd.Add(text);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?> { ["text"] = pr.GetValue(text) };
            return await CliRunner.RunAsync("clipboard.set", args, ct);
        });
        return cmd;
    }
}

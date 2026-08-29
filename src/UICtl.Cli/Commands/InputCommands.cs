using System.CommandLine;

namespace UICtl.Cli.Commands;

internal static class InputCommands
{
    public static Command Type()
    {
        var text = new Argument<string>("text") { Description = "Text to type." };
        var element = new Option<string?>("--element") { Description = "Element id (from `elements`) to type into. Required for now - typing into whatever has focus needs synthesized keystrokes, not implemented yet." };

        var cmd = new Command("type", "Set an element's text directly via AT-SPI (EditableText). No --element / synthesized-keystroke fallback yet - see ENGINEERING.md.");
        cmd.Add(text);
        cmd.Add(element);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?> { ["text"] = pr.GetValue(text) };
            if (pr.GetValue(element) is { } e) args["element"] = e;
            return await CliRunner.RunAsync("type", args, ct);
        });
        return cmd;
    }
}

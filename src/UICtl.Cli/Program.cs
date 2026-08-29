using System.CommandLine;
using System.CommandLine.Help;
using UICtl.Cli.Commands;

var root = new RootCommand(
    "Find, inspect, and drive running Linux/GNOME GUI apps from the command line or from an MCP client. " +
    "Every command prints one JSON object to stdout and exits 0 on success, non-zero on failure. " +
    "A small daemon (auto-started on first use) holds platform state across invocations - see `uictl daemon status`.");

// Global CLI convention (see ~/.claude/agents/*.md's "CLI and TUI Applications"
// rule): -h, -H, --help, --HELP, -? should all show help. System.CommandLine's
// built-in HelpOption already covers -h/--help/-?/?; add the uppercase aliases.
var help = root.Options.OfType<HelpOption>().First();
help.Aliases.Add("-H");
help.Aliases.Add("--HELP");

root.Add(DaemonCommands.Daemon());
root.Add(QueryCommands.Apps());
root.Add(QueryCommands.Permissions());
root.Add(QueryCommands.Windows());
root.Add(CaptureCommands.Elements());
root.Add(InputCommands.Type());
root.Add(InputCommands.Click());
root.Add(InputCommands.Move());
root.Add(InputCommands.Scroll());
root.Add(InputCommands.Key());
root.Add(WindowCommands.Activate());
root.Add(WindowCommands.Focus());

// More subcommands land here phase by phase (displays, screenshot,
// wait-for/ocr/pixel, clipboard, feedback, log, mcp) - see
// ENGINEERING.md's build plan.

return await root.Parse(args).InvokeAsync();

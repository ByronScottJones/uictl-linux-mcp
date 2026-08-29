using System.CommandLine;

namespace UICtl.Cli.Commands;

internal static class InputCommands
{
    public static Command Type()
    {
        var text = new Argument<string>("text") { Description = "Text to type." };
        var element = new Option<string?>("--element") { Description = "Element id (from `elements`) to type into via AT-SPI EditableText (falling back to synthesized keystrokes if that's not supported). Omit to send synthesized keystrokes to whatever currently has keyboard focus, system-wide." };

        var cmd = new Command("type", "Type text - directly into an element via AT-SPI when --element is given, or as synthesized keystrokes (US-QWERTY/ASCII only) otherwise.");
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

    public static Command Click()
    {
        var at = new Option<string?>("--at") { Description = "Screen coordinate \"x,y\" to click. Either this or --element is required." };
        var element = new Option<string?>("--element") { Description = "Element id (from `elements`) to click - clicks its center. Either this or --at is required." };
        var button = new Option<string?>("--button") { Description = "left (default), right, or center." };
        var doubleClick = new Option<bool>("--double") { Description = "Shorthand for --count 2." };
        var count = new Option<int?>("--count") { Description = "Number of clicks to send (overrides --double)." };

        var cmd = new Command("click", "Click at a screen coordinate or on an element, via a synthesized /dev/uinput pointer event.");
        cmd.Add(at);
        cmd.Add(element);
        cmd.Add(button);
        cmd.Add(doubleClick);
        cmd.Add(count);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?>();
            if (pr.GetValue(at) is { } a) args["at"] = a;
            if (pr.GetValue(element) is { } e) args["element"] = e;
            if (pr.GetValue(button) is { } b) args["button"] = b;
            if (pr.GetValue(doubleClick)) args["double"] = true;
            if (pr.GetValue(count) is { } c) args["count"] = c;
            return await CliRunner.RunAsync("click", args, ct);
        });
        return cmd;
    }

    public static Command Move()
    {
        var at = new Argument<string>("at") { Description = "Screen coordinate \"x,y\" to move the pointer to." };

        var cmd = new Command("move", "Move the pointer to a screen coordinate, via a synthesized /dev/uinput pointer event.");
        cmd.Add(at);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?> { ["at"] = pr.GetValue(at) };
            return await CliRunner.RunAsync("move", args, ct);
        });
        return cmd;
    }

    public static Command Scroll()
    {
        var at = new Argument<string>("at") { Description = "Screen coordinate \"x,y\" to move the pointer to before scrolling." };
        var dx = new Option<int?>("--dx") { Description = "Horizontal scroll ticks. Positive scrolls right." };
        var dy = new Option<int?>("--dy") { Description = "Vertical scroll ticks. Positive scrolls content down." };

        var cmd = new Command("scroll", "Scroll at a screen coordinate, via synthesized /dev/uinput wheel events.");
        cmd.Add(at);
        cmd.Add(dx);
        cmd.Add(dy);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?> { ["at"] = pr.GetValue(at) };
            if (pr.GetValue(dx) is { } x) args["dx"] = x;
            if (pr.GetValue(dy) is { } y) args["dy"] = y;
            return await CliRunner.RunAsync("scroll", args, ct);
        });
        return cmd;
    }

    public static Command Key()
    {
        var combo = new Argument<string>("combo") { Description = "Key combo, e.g. \"ctrl+shift+esc\". Linux modifier vocabulary: ctrl, shift, alt, super." };

        var cmd = new Command("key", "Send a key combo via a synthesized /dev/uinput keyboard event.");
        cmd.Add(combo);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?> { ["combo"] = pr.GetValue(combo) };
            return await CliRunner.RunAsync("key", args, ct);
        });
        return cmd;
    }
}

using System.CommandLine;

namespace UICtl.Cli.Commands;

internal static class CaptureCommands
{
    public static Command Screenshot()
    {
        var window = new Option<long?>("--window") { Description = "Window id (from `windows`) to capture. Either this, --app, or neither (whole screen) may be given." };
        var app = new Option<string?>("--app") { Description = "App whose window to capture (name substring or pid)." };
        var screen = new Option<int?>("--screen") { Description = "Monitor index (from `displays`) to capture when no --window/--app is given. Defaults to the whole virtual screen." };
        var outPath = new Option<string>("--out") { Description = "Path to write the PNG to. Defaults to a timestamped file in the current directory." };
        var annotate = new Option<bool>("--annotate") { Description = "Overlay numbered boxes on every AT-SPI element and return their legend (id/role/title/frame) alongside the image. Requires --app or --window." };
        var role = new Option<string?>("--role") { Description = "With --annotate, only number elements with this AT-SPI role name." };

        var cmd = new Command("screenshot",
            "Capture a window or the screen to a PNG file. On Wayland, every call shows a real \"Take Screenshot\" " +
            "consent dialog requiring a manual click - there is no silent path through the portal API this uses " +
            "yet (see ENGINEERING.md's ScreenCast+PipeWire follow-up note). X11 is instant, no dialog.");
        cmd.Add(window);
        cmd.Add(app);
        cmd.Add(screen);
        cmd.Add(outPath);
        cmd.Add(annotate);
        cmd.Add(role);
        cmd.SetAction(async (pr, ct) =>
        {
            string resolvedOut = pr.GetValue(outPath) is { Length: > 0 } o ? o : $"uictl-shot-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            var args = new Dictionary<string, object?> { ["out"] = resolvedOut };
            if (pr.GetValue(window) is { } w) args["window"] = w;
            if (pr.GetValue(app) is { } a) args["app"] = a;
            if (pr.GetValue(screen) is { } s) args["screen"] = s;
            if (pr.GetValue(annotate) is { } an) args["annotate"] = an;
            if (pr.GetValue(role) is { } r) args["role"] = r;
            return await CliRunner.RunAsync("screenshot", args, ct);
        });
        return cmd;
    }

    public static Command Pixel()
    {
        var at = new Option<string>("--at") { Description = "Screen coordinate \"x,y\" to sample.", Required = true };

        var cmd = new Command("pixel", "Read one pixel's RGBA color. Cheap on X11; on Wayland this samples a full screenshot (real consent dialog, same cost as `screenshot`) - don't use in a tight polling loop there, see AGENTS.md.");
        cmd.Add(at);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?> { ["at"] = pr.GetValue(at) };
            return await CliRunner.RunAsync("pixel", args, ct);
        });
        return cmd;
    }

    public static Command Ocr()
    {
        var image = new Option<string?>("--image") { Description = "OCR a standalone PNG file instead of a live capture. Can't be combined with --window/--app/--region." };
        var window = new Option<long?>("--window") { Description = "Window id (from `windows`) to OCR." };
        var app = new Option<string?>("--app") { Description = "App whose window to OCR (name substring or pid)." };
        var region = new Option<string?>("--region") { Description = "\"x,y,w,h\" screen-space rectangle to OCR - crops whatever --window/--app (or the whole screen, if neither given) resolved to." };

        var cmd = new Command("ocr",
            "Read on-screen text via Tesseract. With no --image/--window/--app/--region, OCRs the whole screen. " +
            "On Wayland this goes through the same real \"Take Screenshot\" consent dialog as `screenshot` on every call - see AGENTS.md.");
        cmd.Add(image);
        cmd.Add(window);
        cmd.Add(app);
        cmd.Add(region);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?>();
            if (pr.GetValue(image) is { } i) args["image"] = i;
            if (pr.GetValue(window) is { } w) args["window"] = w;
            if (pr.GetValue(app) is { } a) args["app"] = a;
            if (pr.GetValue(region) is { } r) args["region"] = r;
            return await CliRunner.RunAsync("ocr", args, ct);
        });
        return cmd;
    }

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

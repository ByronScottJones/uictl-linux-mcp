using System.CommandLine;

namespace UICtl.Cli.Commands;

internal static class FeedbackCommands
{
    public static Command Feedback()
    {
        var cmd = new Command("feedback", "Report feedback about uictl itself - stored locally first, submitted to GitHub only when you say so.");
        cmd.Add(Create());
        cmd.Add(List());
        cmd.Add(Get());
        cmd.Add(Update());
        cmd.Add(Delete());
        cmd.Add(CheckDuplicates());
        cmd.Add(Submit());
        return cmd;
    }

    private static Command Create()
    {
        var category = new Option<string>("--category") { Description = "A short free-form label (e.g. \"error\", \"idea\") - becomes the GitHub issue's label if submitted.", Required = true };
        var title = new Option<string>("--title") { Description = "Short summary.", Required = true };
        var body = new Option<string>("--body") { Description = "Full description.", Required = true };

        var cmd = new Command("create", "Save a new local feedback draft. Doesn't contact GitHub - see `feedback submit`.");
        cmd.Add(category);
        cmd.Add(title);
        cmd.Add(body);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?>
            {
                ["category"] = pr.GetValue(category),
                ["title"] = pr.GetValue(title),
                ["body"] = pr.GetValue(body),
            };
            return await CliRunner.RunAsync("feedback.create", args, ct);
        });
        return cmd;
    }

    private static Command List()
    {
        var cmd = new Command("list", "List every local feedback draft.");
        cmd.SetAction(async (_, ct) => await CliRunner.RunAsync("feedback.list", new Dictionary<string, object?>(), ct));
        return cmd;
    }

    private static Command Get()
    {
        var id = new Argument<int>("id") { Description = "Feedback draft id (from `feedback list`)." };
        var cmd = new Command("get", "Show one local feedback draft.");
        cmd.Add(id);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?> { ["id"] = pr.GetValue(id) };
            return await CliRunner.RunAsync("feedback.get", args, ct);
        });
        return cmd;
    }

    private static Command Update()
    {
        var id = new Argument<int>("id") { Description = "Feedback draft id (from `feedback list`)." };
        var category = new Option<string?>("--category") { Description = "Replace the category." };
        var title = new Option<string?>("--title") { Description = "Replace the title." };
        var body = new Option<string?>("--body") { Description = "Replace the body." };

        var cmd = new Command("update", "Edit a local feedback draft. Only the fields you pass are changed.");
        cmd.Add(id);
        cmd.Add(category);
        cmd.Add(title);
        cmd.Add(body);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?> { ["id"] = pr.GetValue(id) };
            if (pr.GetValue(category) is { } c) args["category"] = c;
            if (pr.GetValue(title) is { } t) args["title"] = t;
            if (pr.GetValue(body) is { } b) args["body"] = b;
            return await CliRunner.RunAsync("feedback.update", args, ct);
        });
        return cmd;
    }

    private static Command Delete()
    {
        var id = new Argument<int>("id") { Description = "Feedback draft id (from `feedback list`)." };
        var cmd = new Command("delete", "Remove a local feedback draft. Doesn't affect anything already filed on GitHub.");
        cmd.Add(id);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?> { ["id"] = pr.GetValue(id) };
            return await CliRunner.RunAsync("feedback.delete", args, ct);
        });
        return cmd;
    }

    private static Command CheckDuplicates()
    {
        var id = new Argument<int>("id") { Description = "Feedback draft id (from `feedback list`)." };
        var (repo, token) = RepoAndTokenOptions();

        var cmd = new Command("check-duplicates", "Search GitHub for issues whose title/body look like this draft's title. Best-effort text search, not exact matching.");
        cmd.Add(id);
        cmd.Add(repo);
        cmd.Add(token);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?> { ["id"] = pr.GetValue(id) };
            if (pr.GetValue(repo) is { } r) args["repo"] = r;
            if (pr.GetValue(token) is { } t) args["token"] = t;
            return await CliRunner.RunAsync("feedback.checkDuplicates", args, ct);
        });
        return cmd;
    }

    private static Command Submit()
    {
        var id = new Argument<int>("id") { Description = "Feedback draft id (from `feedback list`)." };
        var (repo, token) = RepoAndTokenOptions();

        var cmd = new Command("submit",
            "Check GitHub for duplicates, then open a pre-filled \"new issue\" page in your browser - " +
            "doesn't file anything itself, you still have to review and click \"Create\".");
        cmd.Add(id);
        cmd.Add(repo);
        cmd.Add(token);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?> { ["id"] = pr.GetValue(id) };
            if (pr.GetValue(repo) is { } r) args["repo"] = r;
            if (pr.GetValue(token) is { } t) args["token"] = t;
            return await CliRunner.RunAsync("feedback.submit", args, ct);
        });
        return cmd;
    }

    private static (Option<string?> Repo, Option<string?> Token) RepoAndTokenOptions()
    {
        var repo = new Option<string?>("--repo") { Description = "GitHub \"owner/repo\" to search/file against. Defaults to this project's own repo." };
        var token = new Option<string?>("--token") { Description = "GitHub token. Defaults to $GITHUB_TOKEN, then `gh auth token`." };
        return (repo, token);
    }
}

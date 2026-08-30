using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UICtl.Core;

/// <summary>
/// GitHub-facing half of `feedback.*` - duplicate search
/// (`feedback.checkDuplicates`) and submit (`feedback.submit`). Neither
/// writes back to FeedbackStore.cs's local file - see its doc comment
/// for why.
///
/// `submit` never files an issue via the API - it only opens a
/// pre-filled "new issue" page (`xdg-open`) for a human to review and
/// click "Create" themselves, same as MCP_INTERFACE.md documents for
/// macOS/Windows. Token resolution order: explicit `--token`/`token`
/// param, then `$GITHUB_TOKEN`, then shelling out to `gh auth token`
/// (the same fallback chain already used by uictl-mac-mcp/uictl-win-mcp
/// per MCP_INTERFACE.md).
/// </summary>
public static class FeedbackGitHub
{
    /// <summary>This repo lives under the personal account, not the org the other two platforms use - see MCP_INTERFACE.md's Feedback section for why.</summary>
    public const string DefaultRepo = "ByronScottJones/uictl-linux-mcp";

    private static readonly HttpClient Http = new();

    public static IReadOnlyList<GitHubIssueSummary> CheckDuplicates(FeedbackEntry entry, string? repo, string? token) =>
        AsyncBridge.RunSync(() => SearchAsync(entry.Title, repo ?? DefaultRepo, ResolveToken(token)));

    /// <summary>Checks for duplicates (same search `feedback.checkDuplicates` runs) then opens the pre-filled issue page, so a human sees both before deciding whether to actually create it.</summary>
    public static (IReadOnlyList<GitHubIssueSummary> Duplicates, string IssueUrl, bool Opened) Submit(FeedbackEntry entry, string? repo, string? token)
    {
        string resolvedRepo = repo ?? DefaultRepo;
        var duplicates = AsyncBridge.RunSync(() => SearchAsync(entry.Title, resolvedRepo, ResolveToken(token)));

        string issueUrl = BuildNewIssueUrl(resolvedRepo, entry);
        bool opened = TryOpenBrowser(issueUrl);
        return (duplicates, issueUrl, opened);
    }

    private static string BuildNewIssueUrl(string repo, FeedbackEntry entry) =>
        $"https://github.com/{repo}/issues/new" +
        $"?title={Uri.EscapeDataString(entry.Title)}" +
        $"&body={Uri.EscapeDataString(entry.Body)}" +
        $"&labels={Uri.EscapeDataString(entry.Category)}";

    private static bool TryOpenBrowser(string url)
    {
        try
        {
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(url);
            using var process = Process.Start(psi);
            return process is not null;
        }
        catch (Win32Exception)
        {
            return false; // xdg-open not installed - caller surfaces `issueUrl` for the human to open by hand
        }
    }

    /// <summary>Free-text search, not an exact-match duplicate detector - GitHub's search API matches the entry's title against issue title/body, same best-effort approach a human pasting the title into GitHub's own search box would get.</summary>
    private static async Task<IReadOnlyList<GitHubIssueSummary>> SearchAsync(string title, string repo, string token)
    {
        string query = $"repo:{repo} is:issue {title}";
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/search/issues?q={Uri.EscapeDataString(query)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("uictl-linux-mcp");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using HttpResponseMessage response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new UiCtlException($"GitHub duplicate search failed ({(int)response.StatusCode} {response.ReasonPhrase})");

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var results = new List<GitHubIssueSummary>();
        if (doc.RootElement.TryGetProperty("items", out JsonElement items))
            foreach (JsonElement item in items.EnumerateArray())
                results.Add(new GitHubIssueSummary(
                    Number: item.GetProperty("number").GetInt32(),
                    Title: item.GetProperty("title").GetString() ?? "",
                    Url: item.GetProperty("html_url").GetString() ?? "",
                    State: item.GetProperty("state").GetString() ?? ""));
        return results;
    }

    private static string ResolveToken(string? explicitToken)
    {
        if (explicitToken is { Length: > 0 })
            return explicitToken;
        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } envToken)
            return envToken;

        try
        {
            var psi = new ProcessStartInfo("gh") { UseShellExecute = false, RedirectStandardOutput = true };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("token");
            using Process? process = Process.Start(psi);
            if (process is not null)
            {
                string stdout = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0 && stdout.Trim() is { Length: > 0 } token)
                    return token.Trim();
            }
        }
        catch (Win32Exception)
        {
            // gh not installed - fall through to the actionable error below.
        }

        throw new UiCtlException("no GitHub token available - pass --token, set $GITHUB_TOKEN, or run `gh auth login`");
    }
}

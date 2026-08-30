using System.Text.Json;
using UICtl.Core;
using UICtl.Ipc;

namespace UICtl.Core.Tests;

/// <summary>
/// ActivityLog is `internal` to UICtl.Ipc (InternalsVisibleTo already
/// covers this test project). Tests never assume the log starts empty -
/// other tests in this suite don't touch it, but running order isn't
/// guaranteed - so each test finds its own entries by a unique command
/// name/marker rather than by index or exact count.
/// </summary>
public class ActivityLogTests
{
    [Fact]
    public void Record_RedactsTextParamForTypeAndClipboardSet()
    {
        using var doc = JsonDocument.Parse("""{"text":"secret-1","element":"marker-e1"}""");
        ActivityLog.Record("type", doc.RootElement, success: true, result: new Dictionary<string, object?> { ["method"] = "synthesizedKeystrokes" }, error: null, duration: TimeSpan.FromMilliseconds(5));

        var entry = ActivityLog.GetAll().Last(e => e.Command == "type" && Params(e)["element"]?.Equals("marker-e1") == true);
        Assert.Equal("[redacted]", Params(entry)["text"]);
        Assert.Equal("marker-e1", Params(entry)["element"]);
    }

    [Fact]
    public void Record_RedactsTextResultForClipboardGet()
    {
        using var doc = JsonDocument.Parse("{}");
        var result = new Dictionary<string, object?> { ["text"] = "secret-clipboard-marker-2" };
        ActivityLog.Record("clipboard.get", doc.RootElement, success: true, result: result, error: null, duration: TimeSpan.Zero);

        var entry = ActivityLog.GetAll().Last(e => e.Command == "clipboard.get" && result.ContainsValue("secret-clipboard-marker-2"));
        Assert.Equal("[redacted]", Result(entry)["text"]);
        // The caller's own dictionary must not be mutated in place - CommandDispatcher may still hold/use that reference elsewhere.
        Assert.Equal("secret-clipboard-marker-2", result["text"]);
    }

    [Fact]
    public void Record_DoesNotRedactOcrElementsOrScreenshot()
    {
        using var doc = JsonDocument.Parse("""{"text":"marker-3 unredacted ocr text"}""");
        ActivityLog.Record("ocr", doc.RootElement, success: true, result: new Dictionary<string, object?> { ["textBlocks"] = Array.Empty<object>() }, error: null, duration: TimeSpan.Zero);

        var entry = ActivityLog.GetAll().Last(e => e.Command == "ocr" && Params(e)["text"]?.Equals("marker-3 unredacted ocr text") == true);
        Assert.Equal("marker-3 unredacted ocr text", Params(entry)["text"]);
    }

    [Fact]
    public void Record_CapsAtTwoThousandEntriesEvictingOldestFirst()
    {
        using var doc = JsonDocument.Parse("{}");
        string command = $"marker-cap-{Guid.NewGuid():N}";
        for (int i = 0; i < 2005; i++)
            ActivityLog.Record(command, doc.RootElement, success: true, result: i, error: null, duration: TimeSpan.Zero);

        var all = ActivityLog.GetAll();
        Assert.True(all.Count <= 2000, $"expected the log capped at 2000 entries, got {all.Count}");

        var mine = all.Where(e => e.Command == command).ToList();
        Assert.DoesNotContain(mine, e => (int)e.Result! == 0); // the earliest of this test's own entries...
        Assert.Contains(mine, e => (int)e.Result! == 2004); // ...but the most recent survived
    }

    private static Dictionary<string, object?> Params(ActivityLogEntry entry) => (Dictionary<string, object?>)entry.Params!;
    private static Dictionary<string, object?> Result(ActivityLogEntry entry) => (Dictionary<string, object?>)entry.Result!;
}

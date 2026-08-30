using System.Text.Json;

namespace UICtl.Core;

/// <summary>
/// Local-first feedback draft storage (`~/.uictl/feedback.json`) - see
/// MCP_INTERFACE.md's Feedback section. `create`/`list`/`get`/`update`/
/// `delete` are pure local CRUD; GitHub duplicate-checking and the actual
/// "submit" (open a pre-filled new-issue page) live in FeedbackGitHub.cs,
/// which never writes back to this file - uictl has no way to learn
/// whether a human actually clicked "Create" on the page it opened for
/// them, so there's no "submitted" flag to track here; a human deletes
/// the local draft once they've filed it for real.
///
/// Ids are assigned from a monotonically increasing counter stored
/// alongside the entries (not derived from array length/max), so a
/// deleted id is never reused - same reasoning as any real datastore's
/// primary key, avoids a deleted-then-recreated entry silently colliding
/// with something a caller still has the old id for.
/// </summary>
public static class FeedbackStore
{
    private sealed record FileShape(int NextId, List<FeedbackEntry> Entries);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Lock = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".uictl", "feedback.json");

    public static FeedbackEntry Create(string category, string title, string body)
    {
        lock (Lock)
        {
            FileShape file = Load();
            var entry = new FeedbackEntry(file.NextId, category, title, body, DateTimeOffset.UtcNow, null);
            file.Entries.Add(entry);
            Save(file with { NextId = file.NextId + 1 });
            return entry;
        }
    }

    public static IReadOnlyList<FeedbackEntry> List()
    {
        lock (Lock) return Load().Entries;
    }

    public static FeedbackEntry Get(int id)
    {
        lock (Lock)
            return Load().Entries.FirstOrDefault(e => e.Id == id)
                ?? throw new UiCtlException($"no feedback entry with id {id}");
    }

    public static FeedbackEntry Update(int id, string? category, string? title, string? body)
    {
        lock (Lock)
        {
            FileShape file = Load();
            int index = file.Entries.FindIndex(e => e.Id == id);
            if (index < 0)
                throw new UiCtlException($"no feedback entry with id {id}");

            FeedbackEntry updated = file.Entries[index] with
            {
                Category = category ?? file.Entries[index].Category,
                Title = title ?? file.Entries[index].Title,
                Body = body ?? file.Entries[index].Body,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            file.Entries[index] = updated;
            Save(file);
            return updated;
        }
    }

    public static void Delete(int id)
    {
        lock (Lock)
        {
            FileShape file = Load();
            if (file.Entries.RemoveAll(e => e.Id == id) == 0)
                throw new UiCtlException($"no feedback entry with id {id}");
            Save(file);
        }
    }

    private static FileShape Load()
    {
        if (!File.Exists(FilePath))
            return new FileShape(1, new List<FeedbackEntry>());

        try
        {
            return JsonSerializer.Deserialize<FileShape>(File.ReadAllText(FilePath), JsonOptions)
                ?? new FileShape(1, new List<FeedbackEntry>());
        }
        catch (JsonException ex)
        {
            // Unlike Permissions.cs's preflight.json (bash-written, ok to
            // treat corruption as "never run"), this file holds real user
            // drafts - silently resetting to empty on a parse failure
            // would look like silent data loss, so this surfaces instead.
            throw new UiCtlException($"{FilePath} is corrupt and could not be read ({ex.Message}) - fix or remove it by hand");
        }
    }

    private static void Save(FileShape file)
    {
        string? dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(file, JsonOptions));
    }
}

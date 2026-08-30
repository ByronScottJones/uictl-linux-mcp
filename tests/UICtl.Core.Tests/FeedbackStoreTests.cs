namespace UICtl.Core.Tests;

/// <summary>
/// Exercises the real `~/.uictl/feedback.json` - no injectable path
/// (matches this project's no-DI, direct-static-class convention). Never
/// touches the whole file or pre-existing entries: each test only
/// creates-then-deletes its own entry via the public API, same
/// leave-it-as-found spirit as ClipboardTests.cs saving/restoring the
/// real clipboard.
/// </summary>
public class FeedbackStoreTests
{
    [Fact]
    public void CreateListGetUpdateDelete_RoundTripsCorrectly()
    {
        var created = FeedbackStore.Create("uictl-test", "Test title", "Test body");
        try
        {
            Assert.True(created.Id > 0);
            Assert.Null(created.UpdatedAt);

            Assert.Contains(FeedbackStore.List(), e => e.Id == created.Id);
            Assert.Equal(created, FeedbackStore.Get(created.Id));

            var updated = FeedbackStore.Update(created.Id, category: null, title: "New title", body: null);
            Assert.Equal("New title", updated.Title);
            Assert.Equal(created.Category, updated.Category); // fields not passed to Update are preserved
            Assert.Equal(created.Body, updated.Body);
            Assert.NotNull(updated.UpdatedAt);
        }
        finally
        {
            FeedbackStore.Delete(created.Id);
        }

        Assert.Throws<UiCtlException>(() => FeedbackStore.Get(created.Id));
    }

    [Fact]
    public void Get_ThrowsForNonexistentId()
    {
        Assert.Throws<UiCtlException>(() => FeedbackStore.Get(int.MaxValue));
    }

    [Fact]
    public void Delete_ThrowsForNonexistentId()
    {
        Assert.Throws<UiCtlException>(() => FeedbackStore.Delete(int.MaxValue));
    }

    [Fact]
    public void Create_AssignsIncreasingIdsNeverReused()
    {
        var first = FeedbackStore.Create("uictl-test", "First", "Body");
        FeedbackStore.Delete(first.Id);
        var second = FeedbackStore.Create("uictl-test", "Second", "Body");
        try
        {
            Assert.True(second.Id > first.Id, "a deleted id should never be reused");
        }
        finally
        {
            FeedbackStore.Delete(second.Id);
        }
    }
}

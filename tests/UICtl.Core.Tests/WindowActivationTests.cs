using UICtl.Core;

namespace UICtl.Core.Tests;

public class WindowActivationTests
{
    private sealed class FakeBackend : IWindowBackend
    {
        public List<BackendWindow> Windows { get; } = new();
        public long? ActivatedId { get; private set; }
        public bool ActivateResult { get; set; } = true;

        public IReadOnlyList<BackendWindow> ListWindows() => Windows;
        public bool Activate(long id) { ActivatedId = id; return ActivateResult; }
        public BackendWindow? GetFocusedWindow() => Windows.Count > 0 ? Windows[0] : null;
    }

    [Fact]
    public void ResolveTarget_ThrowsWhenPidHasNoWindows()
    {
        var backend = new FakeBackend();
        backend.Windows.Add(new BackendWindow(1, 999, "Other App", new Frame(0, 0, 100, 100)));

        var ex = Assert.Throws<UiCtlException>(() => WindowActivation.ResolveTarget(backend, 123, null));
        Assert.Contains("123", ex.Message);
    }

    [Fact]
    public void ResolveTarget_ReturnsTheOnlyMatchWithoutNeedingAWindowId()
    {
        var backend = new FakeBackend();
        backend.Windows.Add(new BackendWindow(1, 123, "Solo Window", new Frame(0, 0, 100, 100)));

        var result = WindowActivation.ResolveTarget(backend, 123, atspiWindowId: null);
        Assert.Equal(1, result.Id);
    }

    [Fact]
    public void PickByTitle_MatchesExactTitle()
    {
        var candidates = new List<BackendWindow>
        {
            new(1, 123, "Untitled Document 1", new Frame(0, 0, 100, 100)),
            new(2, 123, "Untitled Document 2", new Frame(0, 0, 100, 100)),
        };

        var result = WindowActivation.PickByTitle(candidates, "Untitled Document 2");
        Assert.Equal(2, result.Id);
    }

    [Fact]
    public void PickByTitle_FallsBackToFirstWhenTitleIsNull()
    {
        var candidates = new List<BackendWindow>
        {
            new(1, 123, "A", new Frame(0, 0, 100, 100)),
            new(2, 123, "B", new Frame(0, 0, 100, 100)),
        };

        var result = WindowActivation.PickByTitle(candidates, null);
        Assert.Equal(1, result.Id);
    }

    [Fact]
    public void PickByTitle_FallsBackToFirstWhenNothingMatches()
    {
        var candidates = new List<BackendWindow>
        {
            new(1, 123, "A", new Frame(0, 0, 100, 100)),
            new(2, 123, "B", new Frame(0, 0, 100, 100)),
        };

        var result = WindowActivation.PickByTitle(candidates, "Nonexistent");
        Assert.Equal(1, result.Id);
    }
}

using UICtl.Core;

namespace UICtl.Core.Tests;

/// <summary>
/// Mutates process-wide environment variables - save/restore around each
/// test so this doesn't leak into other tests (xunit can run different
/// test classes in parallel).
/// </summary>
public class SessionDetectionTests
{
    [Fact]
    public void WaylandDisplaySet_ReportsWayland()
    {
        using var _ = new EnvScope("WAYLAND_DISPLAY", "wayland-0", "DISPLAY", ":0");
        Assert.Equal("wayland", SessionDetection.SessionType);
        Assert.True(SessionDetection.HasWayland);
    }

    [Fact]
    public void OnlyDisplaySet_ReportsX11()
    {
        using var _ = new EnvScope("WAYLAND_DISPLAY", null, "DISPLAY", ":0");
        Assert.Equal("x11", SessionDetection.SessionType);
        Assert.False(SessionDetection.HasWayland);
        Assert.True(SessionDetection.HasX11);
    }

    [Fact]
    public void WaylandDisplayTakesPriorityOverDisplay()
    {
        // Both set (the common case in a real Wayland session, where
        // XWayland also sets $DISPLAY) - Wayland wins, matching
        // ENGINEERING.md's documented detection order.
        using var _ = new EnvScope("WAYLAND_DISPLAY", "wayland-0", "DISPLAY", ":0");
        Assert.Equal("wayland", SessionDetection.SessionType);
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly (string Name, string? Original)[] _saved;

        public EnvScope(params string?[] namesAndValues)
        {
            _saved = new (string, string?)[namesAndValues.Length / 2];
            for (int i = 0; i < namesAndValues.Length; i += 2)
            {
                string name = namesAndValues[i]!;
                _saved[i / 2] = (name, Environment.GetEnvironmentVariable(name));
                Environment.SetEnvironmentVariable(name, namesAndValues[i + 1]);
            }
        }

        public void Dispose()
        {
            foreach (var (name, original) in _saved)
                Environment.SetEnvironmentVariable(name, original);
        }
    }
}

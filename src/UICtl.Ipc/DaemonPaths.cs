namespace UICtl.Ipc;

internal static class DaemonPaths
{
    public static string BaseDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".uictl");

    public static string SocketPath => Path.Combine(BaseDir, "uictl.sock");

    public static string LogPath => Path.Combine(BaseDir, "daemon.log");
}

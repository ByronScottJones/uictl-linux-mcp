namespace UICtl.Core;

/// <summary>Bare /proc enumeration - no permission needed, same on X11/Wayland. Used for apps.list's `all:true` case (union with AT-SPI-registered apps) and as the name fallback for a pid AT-SPI doesn't know about.</summary>
internal static class ProcEnumeration
{
    public static IEnumerable<int> ListPids()
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            string name = Path.GetFileName(dir);
            if (int.TryParse(name, out int pid))
                yield return pid;
        }
    }

    public static string TryGetProcessName(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/comm").TrimEnd('\n'); }
        catch { return ""; }
    }
}

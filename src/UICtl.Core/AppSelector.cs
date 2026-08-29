namespace UICtl.Core;

/// <summary>Resolves an app selector string to a process id: name substring, then numeric pid - no middle tier (no bundle-id/package-family-name equivalent on Linux). See MCP_INTERFACE.md.</summary>
public static class AppSelector
{
    public static int Resolve(string selector)
    {
        foreach (int pid in ProcEnumeration.ListPids())
        {
            string name = ProcEnumeration.TryGetProcessName(pid);
            // /proc/[pid]/comm truncates to 15 chars (TASK_COMM_LEN), so a
            // selector longer than that (e.g. "gnome-text-editor" vs the
            // truncated "gnome-text-edit") would never match via
            // name.Contains(selector) alone - check both directions.
            if (name.Length > 0 &&
                (name.Contains(selector, StringComparison.OrdinalIgnoreCase) ||
                 selector.Contains(name, StringComparison.OrdinalIgnoreCase)))
                return pid;
        }

        if (int.TryParse(selector, out int numericPid) && ProcEnumeration.TryGetProcessName(numericPid).Length > 0)
            return numericPid;

        throw new UiCtlException($"no running app matches \"{selector}\"");
    }
}

namespace UICtl.Core;

/// <summary>Resolves an app selector string to a process id: name substring, then numeric pid - no middle tier (no bundle-id/package-family-name equivalent on Linux). See MCP_INTERFACE.md.</summary>
public static class AppSelector
{
    public static int Resolve(string selector)
    {
        foreach (int pid in ProcEnumeration.ListPids())
        {
            string name = ProcEnumeration.TryGetProcessName(pid);
            if (name.Length > 0 && name.Contains(selector, StringComparison.OrdinalIgnoreCase))
                return pid;
        }

        if (int.TryParse(selector, out int numericPid) && ProcEnumeration.TryGetProcessName(numericPid).Length > 0)
            return numericPid;

        throw new UiCtlException($"no running app matches \"{selector}\"");
    }
}

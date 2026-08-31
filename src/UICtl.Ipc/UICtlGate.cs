namespace UICtl.Ipc;

/// <summary>
/// The "commands-enabled" kill switch a human toggles from the `log show`
/// GTK4 window (UICtl.Gui) - see MCP_INTERFACE.md's Activity log section.
/// Defaults enabled; there is deliberately no public CLI/MCP command to
/// disable it (only the on-screen checkbox can) - the internal
/// `__gate_set__` command UICtl.Gui uses is not registered in the CLI's
/// command tree or the MCP tool list, the same way `__daemon_stop__`/
/// `__daemon_log_warning__` aren't. UICtl.Gui also re-enables on window
/// close, so the toggle can't be left disabled by walking away from a
/// closed window - see its own doc comment.
/// </summary>
internal static class UICtlGate
{
    private static volatile bool _enabled = true;

    public static bool Enabled => _enabled;

    public static void Set(bool enabled) => _enabled = enabled;
}

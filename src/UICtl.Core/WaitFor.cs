namespace UICtl.Core;

/// <summary>
/// `wait-for` - polls a window's AT-SPI elements (Accessibility.WalkWindow,
/// same as `elements`) every 250ms until one matches --role/--title, or
/// --timeout elapses. Re-resolves the window/app fresh on every poll
/// (WindowResolver.Resolve) rather than once up front, so this also works
/// for "wait for this app to even launch" - a resolution failure (app not
/// running yet, window not found) is treated as "not found yet, keep
/// polling" rather than an immediate error, since that's this command's
/// whole reason to exist. Only an upfront missing window/app selector
/// throws immediately - that can never succeed no matter how long it
/// polls, so failing fast is more useful than waiting out the full
/// timeout for nothing.
/// </summary>
public static class WaitFor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private const double DefaultTimeoutSeconds = 5.0;

    public static (bool Found, ElementInfo? Element) Poll(long? windowId, string? appSelector, string? roleFilter, string? titleContains, double? timeoutSeconds)
    {
        if (windowId is null && appSelector is null)
            throw new UiCtlException("either a window id or an app selector is required");

        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds ?? DefaultTimeoutSeconds);
        var options = new ElementWalkOptions(RoleFilter: roleFilter, TitleContains: titleContains, MaxElements: 1);

        while (true)
        {
            try
            {
                var resolved = WindowResolver.Resolve(windowId, appSelector);
                var walk = Accessibility.WalkWindow(resolved.WindowId, options);
                if (walk.Elements.Count > 0)
                    return (true, walk.Elements[0]);
            }
            catch (UiCtlException)
            {
                // App/window not found yet (hasn't launched, window not up
                // yet), or a transient AT-SPI hiccup - not found *yet*,
                // keep polling until the deadline rather than failing the
                // whole wait on one bad iteration.
            }

            if (DateTime.UtcNow >= deadline)
                return (false, null);

            Thread.Sleep(PollInterval);
        }
    }
}

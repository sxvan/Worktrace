using Windows.Win32.UI.Accessibility;

namespace Capture.Windows;

public enum FocusCheck
{
    /// <summary>The focused element is not a password field.</summary>
    NotPassword,

    /// <summary>The focused element reports itself as a password field.</summary>
    Password,

    /// <summary>The application did not answer in time or the query failed.</summary>
    Unknown,
}

/// <summary>
/// Asks Windows UI Automation (the accessibility API) whether the element that has keyboard focus
/// is a password field. Works for applications that describe their controls to that API; a
/// terminal or a custom-drawn input cannot be recognized this way.
/// </summary>
public sealed class PasswordFieldDetector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    private IUIAutomation? _automation;

    public async Task<FocusCheck> CheckFocusedElementAsync()
    {
        // The query goes to another process and can hang with it, so it runs with a time limit.
        Task<FocusCheck> query = Task.Run(Query);
        Task finished = await Task.WhenAny(query, Task.Delay(Timeout));
        if (finished != query)
        {
            return FocusCheck.Unknown;
        }
        return await query;
    }

    private FocusCheck Query()
    {
        try
        {
            _automation ??= (IUIAutomation)new CUIAutomation();
            IUIAutomationElement? element = _automation.GetFocusedElement();
            if (element is null)
            {
                return FocusCheck.NotPassword; // nothing has focus
            }
            return element.CurrentIsPassword ? FocusCheck.Password : FocusCheck.NotPassword;
        }
        catch (Exception)
        {
            return FocusCheck.Unknown;
        }
    }
}

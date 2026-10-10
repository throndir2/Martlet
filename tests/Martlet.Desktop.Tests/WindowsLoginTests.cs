using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class WindowsLoginTests
{
    [Fact]
    public void The_current_windows_login_has_a_sid_a_kind_and_a_name()
    {
        var login = WindowsLogin.Current;
        Assert.StartsWith("S-1-", login.Sid);
        Assert.Contains(login.Kind, new[] { WindowsLogin.Microsoft, WindowsLogin.Work, WindowsLogin.Local });
        Assert.False(string.IsNullOrWhiteSpace(login.UserName));
        Assert.False(string.IsNullOrWhiteSpace(login.DisplayName));
        if (login.Kind == WindowsLogin.Local) Assert.Null(login.EmailHint);
        if (login.EmailHint is { } hint) Assert.Contains('@', hint);
        Assert.Same(login, WindowsLogin.Current);
    }

    [Fact]
    public void Its_text_gives_the_kind_only_never_the_email_or_names()
    {
        var login = WindowsLogin.Current;
        var text = login.ToString();
        Assert.Contains(login.Kind, text);
        Assert.DoesNotContain('@', text);
        Assert.DoesNotContain(login.Sid, text);
        Assert.Equal($"{login.Kind} Windows login" + (login.HasEmailHint ? " with an e-mail hint" : ""), text);
    }
}

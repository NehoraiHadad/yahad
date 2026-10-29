namespace Yahad.Install;

public enum WebSetupState { Complete, Ready, Closed }

/// <summary>Only explicitly retrieving the launch URI exposes its setup fragment.</summary>
public sealed class OwnerWebHandoff
{
    private readonly Uri launch;
    public bool ExistingFamily { get; }
    public string DisplayOrigin { get; }
    internal OwnerWebHandoff(Uri launch, bool existing)
    { this.launch = launch; ExistingFamily = existing; DisplayOrigin = launch.GetLeftPart(UriPartial.Authority); }
    public Uri GetLaunchUri() => launch;
    public override string ToString() => ExistingFamily ? DisplayOrigin : DisplayOrigin + "/#setup=[redacted]";
}

/// <summary>Consumes the existing /api/setup/status contract; never creates a family.</summary>
public static class WebHandoff
{
    public static OwnerWebHandoff Create(Uri trustedOrigin, WebSetupState state, string? capability,
        DateTimeOffset? expires, DateTimeOffset now)
    {
        if (!trustedOrigin.IsAbsoluteUri || trustedOrigin.UserInfo != "" || trustedOrigin.Query != "" || trustedOrigin.Fragment != "" || trustedOrigin.AbsolutePath != "/")
            throw new InvalidOperationException("WEB_HANDOFF_ORIGIN");
        var host = trustedOrigin.Host;
        var loopback = host is "127.0.0.1" or "localhost" or "[::1]";
        if (!(loopback && trustedOrigin.Scheme is "http" or "https") &&
            !(trustedOrigin.Scheme == "https" && NetworkPlan.IsPrivateLanV4(host)))
            throw new InvalidOperationException("WEB_HANDOFF_UNCONFIGURED_ORIGIN");
        if (state == WebSetupState.Complete) return new(trustedOrigin, true); // Ignore any obsolete setup capability.
        if (state != WebSetupState.Ready || capability is null || capability.Length != 43 ||
            !System.Text.RegularExpressions.Regex.IsMatch(capability, "^[A-Za-z0-9_-]{43}$") || expires is null || expires <= now || expires > now.AddMinutes(15))
            throw new InvalidOperationException("WEB_SETUP_NOT_READY");
        return new(new Uri(trustedOrigin, "/#setup=" + capability), false);
    }
}

namespace Yahad.Authority;

internal enum InstalledAuthorityMode {Service,RecoveryA,RecoveryB,HelperProbe}
// Trusted entry factory for the integrated candidate. Native acceptance and
// release gates remain separate from configured source dispatch. CLI carries
// no operation IDs, nonce, paths, account, capability or arbitrary command.
internal static class InstalledAuthorityEntrypoint
{
    internal static InstalledAuthorityMode Parse(string[] arguments)=>arguments switch
    {
        ["--service"]=>InstalledAuthorityMode.Service,
        ["--system-recovery-service","A"]=>InstalledAuthorityMode.RecoveryA,
        ["--system-recovery-service","B"]=>InstalledAuthorityMode.RecoveryB,
        ["--recovery-slot-probe"]=>InstalledAuthorityMode.HelperProbe,
        _=>throw new IOException("AUTHORITY_INSTALLED_ARGUMENTS")
    };
    internal static int Run(string[] arguments)
    {
        switch(Parse(arguments))
        {
            case InstalledAuthorityMode.Service:return AuthorityServiceApplication.RunInstalled();
            case InstalledAuthorityMode.RecoveryA:
            case InstalledAuthorityMode.RecoveryB:return SystemBootstrapRecoveryBootApplication.RunInstalled(arguments,FullSystemAuthorityApplication.CreateHelperComposition);
            case InstalledAuthorityMode.HelperProbe:
                using(var composition=FullSystemAuthorityApplication.CreateProbeComposition())
                    return SystemBootstrapHelperProbeApplication.RunInstalledAsync(arguments,composition.Hooks.Policy).GetAwaiter().GetResult();
            default:throw new IOException("AUTHORITY_INSTALLED_ARGUMENTS");
        }
    }
}

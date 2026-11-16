using System.Reflection;
using System.Text.RegularExpressions;
namespace Yahad.Install;

// The infrastructure identity compiled into every .NET image: the host, the Updater,
// Platform and the Setup engine (docs/WINDOWS-APP-PACKAGE-PLAN.md 4.3). It comes from
// installer/Directory.Build.props, never from package.json, an installed file or an
// environment setting. The app version is the active pointer's, not this one.
public static class InfrastructureRelease
{
    private static readonly Assembly Core=typeof(InfrastructureRelease).Assembly;
    public static string Version { get; } = RequireVersion(Metadata("YahadInfrastructureVersion"));
    public static int ContractMin { get; } = RequireContract(Metadata("YahadInfrastructureContractMin"));
    public static int ContractMax { get; } = RequireRange(ContractMin,RequireContract(Metadata("YahadInfrastructureContractMax")));
    public static string SourceCommit => CompiledSourceCommit(Core);
    // The .NET runtime compiled or bundled into this image; the build requires the pinned one.
    public static string RuntimeVersion => Environment.Version.ToString();
    public static bool Supports(int contract)=>contract>=ContractMin&&contract<=ContractMax;
    public static string CompiledSourceCommit(Assembly assembly)
    {
        var revision = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+').Last();
        if (revision is null || !Regex.IsMatch(revision, @"\A[a-f0-9]{40}\z", RegexOptions.CultureInvariant))
            throw new IOException("INFRASTRUCTURE_SOURCE_IDENTITY_MISSING");
        return revision;
    }
    // Contracts are exact, not cumulative (section 6): a range is 1..1024 and contiguous.
    internal static bool IsContract(int value)=>value is >=1 and <=1024;
    private static string Metadata(string key)
        =>Core.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a=>a.Key==key)?.Value??throw new IOException("INFRASTRUCTURE_IDENTITY_MISSING");
    private static string RequireVersion(string value)=>SemVer.IsVersion(value)?value:throw new IOException("INFRASTRUCTURE_IDENTITY_MISSING");
    private static int RequireContract(string value)
        =>int.TryParse(value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var parsed)&&IsContract(parsed)?parsed:throw new IOException("INFRASTRUCTURE_IDENTITY_MISSING");
    private static int RequireRange(int min,int max)=>max>=min?max:throw new IOException("INFRASTRUCTURE_IDENTITY_MISSING");
}

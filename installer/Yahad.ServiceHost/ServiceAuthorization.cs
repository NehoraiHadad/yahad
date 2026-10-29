using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Yahad.Install;

namespace Yahad.Service;

internal sealed record ServiceTokenObservation(string UserSid,bool ServiceLogon,bool Administrator,string[] Privileges);
internal interface IServiceTokenReader {ServiceTokenObservation Read();string ExpectedServiceSid();}
internal interface IInstallationReader {InstallationBinding Read();}
internal sealed record InstallationBinding(string InstallationId,string ActivationReceiptDigest);
internal interface IInstalledActivationResolver
{Task<AuthorizedServiceStart> ResolveAsync(InstallationBinding binding,CancellationToken cancellation);}
internal sealed class UnconfiguredActivationResolver:IInstalledActivationResolver
{
    public Task<AuthorizedServiceStart> ResolveAsync(InstallationBinding binding,CancellationToken cancellation)
        =>throw new ServiceStartDenied(ServiceFailure.ActivationUnavailable);
}
internal static class ServiceIdentityPolicy
{
    internal static void Require(ServiceTokenObservation token,string expected)
    {
        if(!expected.StartsWith("S-1-5-80-",StringComparison.Ordinal)||token.UserSid!=expected||!token.ServiceLogon||token.Administrator||
            token.Privileges.Length!=1||token.Privileges[0]!="SeChangeNotifyPrivilege")throw new ServiceStartDenied(ServiceFailure.IdentityDenied);
    }
}
internal sealed class InstalledStartAuthorizer(IServiceTokenReader tokens,IInstallationReader installation,IInstalledActivationResolver activation):IServiceStartAuthorizer
{
    public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try{ServiceIdentityPolicy.Require(tokens.Read(),tokens.ExpectedServiceSid());}
        catch{throw new ServiceStartDenied(ServiceFailure.IdentityDenied);}
        InstallationBinding binding;
        try{binding=installation.Read();}catch{throw new ServiceStartDenied(ServiceFailure.BootstrapUnavailable);}
        return activation.ResolveAsync(binding,cancellation);
    }
}
internal sealed class WindowsInstallationReader:IInstallationReader
{
    internal static string DescriptorPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),ServiceDefinition.Name,"bootstrap.json");
    public InstallationBinding Read()
    {
        // Existing owner-only bootstrap capability policy: admin/System owner,
        // private DACL, application read-only, no-follow and single-link handle.
        using var pinned=WindowsFileBoundary.OpenSecret(DescriptorPath,SecretRole.SetupCapability);
        return Parse(pinned.Read());
    }
    internal static InstallationBinding Parse(byte[] bytes)
    {
        if(bytes.Length is <1 or >1024)throw new IOException("BOOTSTRAP_BOUND");
        using var document=JsonDocument.Parse(bytes);var value=document.RootElement;
        var properties=value.EnumerateObject().Select(p=>p.Name).ToArray();
        if(properties.Length!=4||properties.Distinct(StringComparer.Ordinal).Count()!=4||!properties.ToHashSet(StringComparer.Ordinal).SetEquals(new[]{"schema","service","installationId","activationReceiptDigest"})||
            value.GetProperty("schema").GetInt32()!=1||value.GetProperty("service").GetString()!=ServiceDefinition.Name)throw new IOException("BOOTSTRAP_SCHEMA");
        var id=value.GetProperty("installationId").GetString()!;var digest=value.GetProperty("activationReceiptDigest").GetString()!;
        if(!Guid.TryParseExact(id,"N",out _)||!System.Text.RegularExpressions.Regex.IsMatch(digest,"^[a-f0-9]{64}$"))throw new IOException("BOOTSTRAP_IDENTITY");
        return new(id,digest);
    }
}
internal sealed class WindowsServiceTokenReader:IServiceTokenReader
{
    [StructLayout(LayoutKind.Sequential)]private struct Luid {public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)]private struct LuidAttributes {public Luid Luid;public uint Attributes;}
    public string ExpectedServiceSid()=>((SecurityIdentifier)new NTAccount(ServiceDefinition.Account).Translate(typeof(SecurityIdentifier))).Value;
    public ServiceTokenObservation Read()
    {
        using var identity=WindowsIdentity.GetCurrent(TokenAccessLevels.Query|TokenAccessLevels.Duplicate);var principal=new WindowsPrincipal(identity);
        var token=identity.AccessToken;
        GetTokenInformation(token.DangerousGetHandle(),3,IntPtr.Zero,0,out var bytes);
        if(bytes is <4 or >65536)throw new IOException("SERVICE_TOKEN_BOUND");
        var buffer=Marshal.AllocHGlobal((int)bytes);
        try
        {
            if(!GetTokenInformation(token.DangerousGetHandle(),3,buffer,bytes,out _))throw new Win32Exception();
            var count=Marshal.ReadInt32(buffer);var stride=Marshal.SizeOf<LuidAttributes>();
            if(count is <0 or >128||4L+count*stride>bytes)throw new IOException("SERVICE_TOKEN_BOUND");
            var privileges=new List<string>();
            for(var i=0;i<count;i++)
            {
                var item=Marshal.PtrToStructure<LuidAttributes>(buffer+4+i*stride);var name=new StringBuilder(256);var length=256u;
                if(!LookupPrivilegeNameW(null,ref item.Luid,name,ref length))throw new Win32Exception();
                // Include disabled privileges: unexpected granted authority must
                // not be overlooked merely because it is currently disabled.
                privileges.Add(name.ToString());
            }
            return new(identity.User?.Value??throw new IOException("SERVICE_TOKEN_USER"),
                principal.IsInRole(new SecurityIdentifier("S-1-5-6")),principal.IsInRole(WindowsBuiltInRole.Administrator),privileges.ToArray());
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token,int type,IntPtr buffer,uint bytes,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeNameW(string? system,ref Luid luid,StringBuilder name,ref uint length);
}

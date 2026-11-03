using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
namespace Yahad.Production.Tests;

internal static class FullSystemPolicyTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("full system policy cannot be inferred from an existing routine enrollment",()=>
        {var f=new Fixture();Reject(()=>FullSystemUpdatePolicy.Open(f.Files,f.Installation,null,()=>f.Now,f));Assert(f.Resolves==0);return Task.CompletedTask;});
        await check("full consent remains effective without periodic renewal or a 100-update allowance",()=>
        {
            var f=new Fixture();var p=f.Open();f.Now=f.Now.AddDays(400);
            for(var i=0;i<105;i++){var op=p.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);p.RequireCurrent(op);}
            Assert(Directory.GetFiles(f.Files.PathOf("grants"),"usage-*.json").Length==0);return Task.CompletedTask;
        });
        await check("full policy requires every explicitly enrolled product scope and fixed publisher",()=>
        {
            foreach(var scope in FullSystemUpdatePolicy.RequiredScopes)
            {var f=new Fixture();f.Enrollment=f.Enrollment with{Scopes=FullSystemUpdatePolicy.RequiredScopes.Where(x=>x!=scope).ToArray()};f.WriteEnrollment();Reject(()=>f.Open());}
            foreach(var field in new[]{"repository","channel","trust","profile","consent"})
            {
                var f=new Fixture();f.Enrollment=field switch{"repository"=>f.Enrollment with{Repository="other/repo"},"channel"=>f.Enrollment with{Channel="preview"},"trust"=>f.Enrollment with{Trust="none"},"profile"=>f.Enrollment with{Profile="routine"},_=>f.Enrollment with{ConsentVersion=1}};
                f.WriteEnrollment();Reject(()=>f.Open());
            }
            return Task.CompletedTask;
        });
        await check("notify and revocation stop full authority and reenrollment cannot revive an old operation",()=>
        {
            var f=new Fixture();var policy=f.Open();var op=policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);
            policy.DisableCurrent();policy.DisableCurrent();Reject(()=>policy.RequireCurrent(op));
            f.Grant=f.Grant with{Id=Guid.NewGuid().ToString("N")};f.WriteGrant();Reject(()=>policy.RequireCurrent(op));
            var next=policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);policy.RequireCurrent(next);
            f.Put("intake/mode.json",new{Format=2,InstallationId=f.Installation,Mode="notify",DelegationId=(string?)null,ModeControlAllowed=true,GrantKind="system"});Reject(()=>policy.RequireCurrent(next));
            return Task.CompletedTask;
        });
        await check("full authority binds exact grant bytes and the immutable enrollment digest",()=>
        {
            var f=new Fixture();var policy=f.Open();var op=policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);
            f.Now=f.Now.AddSeconds(1);f.Grant=f.Grant with{ApprovedUnix=f.Now.ToUnixTimeSeconds()};f.WriteGrant();Reject(()=>policy.RequireCurrent(op));
            var next=policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);f.Enrollment=f.Enrollment with{Id=Guid.NewGuid().ToString("N")};f.Put("intake/full-system-enrollment.json",f.Enrollment);Reject(()=>policy.RequireCurrent(next));
            return Task.CompletedTask;
        });
        await check("schema changes require the verified candidate to declare the actual source schema",()=>
        {
            var f=new Fixture();var policy=f.Open();f.Candidate=f.Candidate with{TargetSchema=7,MigrationSources=Array.Empty<int>()};Reject(()=>policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed));
            f.Candidate=f.Candidate with{MigrationSources=new[]{6}};var op=policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);Assert(op.SourceSchema==6&&op.TargetSchema==7);
            f.Candidate=f.Candidate with{TargetSchema=5};Reject(()=>policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed));return Task.CompletedTask;
        });
        await check("full operation is a deep immutable authorization result and cannot be deserialized",()=>
        {
            var f=new Fixture();var op=f.Open().Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);var old=op.Components[0];
            f.Components[0]=f.Components[1];Assert(op.Components[0].Kind==old.Kind&&op.Components.Select(x=>x.Kind).Distinct().Count()==Enum.GetValues<SystemComponentKind>().Length);
            var rejected=false;try{_=JsonSerializer.Deserialize<VerifiedSystemOperation>(JsonSerializer.Serialize(op));}catch(NotSupportedException){rejected=true;}Assert(rejected);return Task.CompletedTask;
        });
        await check("interrupted full operation can be reminted for repair after revoke but cannot advance with the revoked grant",()=>
        {
            var f=new Fixture();var policy=f.Open();var op=policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);
            policy.DisableCurrent();var repair=policy.RemintBootstrapRecovery(op.OperationId);
            Assert(repair.ManifestDigest==op.ManifestDigest&&repair.GrantId==op.GrantId&&repair.PreviousActivationDigest==op.PreviousActivationDigest);
            Reject(()=>policy.RequireCurrent(repair));Reject(()=>policy.Authorize(op.OperationId,f.Receipt,f.Observed));
            f.Grant=f.Grant with{Id=Guid.NewGuid().ToString("N")};f.WriteGrant();Reject(()=>policy.RequireCurrent(repair));return Task.CompletedTask;
        });
        await check("full recovery refuses an absent admission, changed source or changed original grant bytes",()=>
        {
            var f=new Fixture();var policy=f.Open();Reject(()=>policy.RemintBootstrapRecovery(Guid.NewGuid().ToString("N")));
            var op=policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);
            f.Candidate=f.Candidate with{SourceCommit=new('b',40)};Reject(()=>policy.RemintBootstrapRecovery(op.OperationId));
            f.Candidate=f.Candidate with{SourceCommit=new('a',40)};f.Now=f.Now.AddSeconds(1);f.Grant=f.Grant with{ApprovedUnix=f.Now.ToUnixTimeSeconds()};f.WriteGrant();
            Reject(()=>policy.RemintBootstrapRecovery(op.OperationId));return Task.CompletedTask;
        });
        await check("full admission operation id cannot be rebound to a different candidate or source head",()=>
        {
            var f=new Fixture();var policy=f.Open();var op=policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);
            _=policy.Authorize(op.OperationId,f.Receipt,f.Observed);
            f.Candidate=f.Candidate with{Version="0.6.2"};Reject(()=>policy.Authorize(op.OperationId,f.Receipt,f.Observed));
            f.Candidate=f.Candidate with{Version="0.6.1"};Assert(policy.RemintBootstrapRecovery(op.OperationId).ToVersion=="0.6.1");return Task.CompletedTask;
        });
        await check("a previous app return retains its newer bootstrap version and later updates cannot downgrade it",()=>
        {
            var f=new Fixture();var policy=f.Open();f.Observed=f.Observed with{BootstrapEpoch=2,BootstrapVersion="0.6.2"};
            Reject(()=>policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed));
            f.Candidate=f.Candidate with{Version="0.6.3"};var op=policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);
            Assert(op.FromVersion=="0.6.0"&&op.BootstrapFromVersion=="0.6.2"&&op.ToVersion=="0.6.3");
            var repair=policy.RemintBootstrapRecovery(op.OperationId);Assert(repair.BootstrapFromVersion=="0.6.2");policy.RequireCurrent(repair);return Task.CompletedTask;
        });
    }
    private static void Reject(Action action){try{action();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception){return;}throw new IOException("FULL_SYSTEM_UNEXPECTED_AUTHORITY");}
    private static void Assert(bool condition){if(!condition)throw new IOException("FULL_SYSTEM_ASSERTION");}
    internal sealed class Fixture:IProtectedSystemCandidateResolver
    {
        internal readonly string Installation=Guid.NewGuid().ToString("N"),Receipt=new('c',64);
        internal readonly InstalledStateFiles Files=InstalledStateFiles.Private(SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"full-consent")));
        internal DateTimeOffset Now=DateTimeOffset.FromUnixTimeSeconds(1800000000);internal int Resolves;
        internal FullSystemEnrollment Enrollment;internal FullSystemModeGrant Grant;internal string Digest="";internal readonly SystemComponentDescriptor[] Components;
        internal SystemCandidatePolicyFacts Candidate;
        internal FullSystemInstalledState Observed=new("0.6.0",1,new('a',64),6);
        internal Fixture()
        {
            Directory.CreateDirectory(Files.PathOf("intake"));Directory.CreateDirectory(Files.PathOf("grants"));
            Components=Enum.GetValues<SystemComponentKind>().Select(kind=>new SystemComponentDescriptor(kind,FixedSystemComponents.SlotPrefix(kind),FixedSystemComponents.EntryPoint(kind),new('d',64),new[]{new SystemComponentFile(FixedSystemComponents.EntryPoint(kind),1,new('d',64))})).ToArray();
            Candidate=new(new('e',64),new('f',64),"0.6.1",new('a',40),6,Array.Empty<int>(),Components);
            Enrollment=new(1,FullSystemUpdatePolicy.Profile,Guid.NewGuid().ToString("N"),Installation,NativeCandidateIntake.Repository,"stable","https-github-unsigned",2,Now.ToUnixTimeSeconds(),FullSystemUpdatePolicy.RequiredScopes.ToArray());
            WriteEnrollment();Grant=new(1,Guid.NewGuid().ToString("N"),Installation,Enrollment.Id,Digest,Now.ToUnixTimeSeconds());WriteGrant();
        }
        internal void WriteEnrollment(){var bytes=JsonSerializer.SerializeToUtf8Bytes(Enrollment);File.WriteAllBytes(Files.PathOf("intake/full-system-enrollment.json"),bytes);Digest=Convert.ToHexStringLower(SHA256.HashData(bytes));}
        internal void WriteGrant(){Put("grants/system-grant-"+Grant.Id+".json",Grant);Put("intake/mode.json",new{Format=2,InstallationId=Installation,Mode="auto",DelegationId=Grant.Id,ModeControlAllowed=true,GrantKind="system"});}
        internal void Put<T>(string path,T value)=>File.WriteAllBytes(Files.PathOf(path),JsonSerializer.SerializeToUtf8Bytes(value));
        internal FullSystemUpdatePolicy Open()=>FullSystemUpdatePolicy.Open(Files,Installation,Digest,()=>Now,this);
        public SystemCandidatePolicyFacts RequireVerified(string receiptDigest){if(receiptDigest!=Receipt)throw new IOException("FULL_SYSTEM_CANDIDATE");Resolves++;return Candidate;}
    }
}

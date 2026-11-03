using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

internal sealed record UpdateSetupChoice(int ConsentVersion,bool Accepted,bool Automatic,long ApprovedUnix)
{
    internal void Require(DateTimeOffset now)
    {RequirePersisted(now);if(ApprovedUnix<now.ToUnixTimeSeconds()-1800)throw new IOException("UPDATE_CONSENT_REQUIRED");}
    internal void RequirePersisted(DateTimeOffset now)
    {if(ConsentVersion!=2||!Accepted||ApprovedUnix<=0||ApprovedUnix>now.ToUnixTimeSeconds()+300)throw new IOException("UPDATE_CONSENT_REQUIRED");}
    internal void RequireCompatibleRetry(UpdateSetupChoice? requested)
    {if(requested is not null&&(requested.ConsentVersion!=ConsentVersion||requested.Accepted!=Accepted||requested.Automatic!=Automatic))throw new IOException("UPDATE_CONSENT_ALREADY_CAPTURED");}
}
internal sealed record FirstOwnerBinding(int format,string installationId,string ownerSid,string familyId,string parentUserId)
{
    internal static FirstOwnerBinding Create(string installation,string owner)
        =>new(1,installation,owner,Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"));
    internal void Require(string installation,string owner)
    {
        if(format!=1||installationId!=installation||ownerSid!=owner||!Guid.TryParseExact(familyId,"D",out var f)||f.ToString("D")!=familyId||!Guid.TryParseExact(parentUserId,"D",out var p)||p.ToString("D")!=parentUserId||f==p)throw new IOException("UPDATE_OWNER_BINDING");
    }
}
internal enum FullServiceKind {Runtime,Authority,RecoveryA,RecoveryB}
internal sealed record FullServiceContract(FullServiceKind Kind,string Name,string Account,string Image,string Arguments,uint AuthorityRights,uint StartType,string? FailureCommand,bool FailureActionsOnNonCrash=false)
{
    internal string Command=>"\""+Image+"\" "+Arguments;
    internal static FullServiceContract Describe(FullServiceKind kind)
    {
        if(kind==FullServiceKind.Runtime)return new(kind,InstalledUpdaterLayout.RuntimeServiceName,ServiceDefinition.Account,InstalledUpdaterLayout.RuntimeExecutable,"--service",0x35,3,null);
        if(kind==FullServiceKind.Authority)return new(kind,InstalledUpdaterLayout.AuthorityServiceName,@"NT SERVICE\"+InstalledUpdaterLayout.AuthorityServiceName,InstalledUpdaterLayout.AuthorityExecutable,"--service",0x35,2,BootstrapRecoveryProvisioningContract.PrimaryFailureCommand(),true);
        var helper=BootstrapRecoveryProvisioningContract.Describe(kind==FullServiceKind.RecoveryA?RecoveryHelperSlot.A:kind==FullServiceKind.RecoveryB?RecoveryHelperSlot.B:throw new IOException("UPDATE_SERVICE_KIND"));
        return new(kind,helper.Name,helper.Account,helper.Executable,helper.Arguments,0x35,helper.StartType,helper.FailureCommand,helper.FailureActionsOnNonCrash);
    }
}
internal sealed record FullInstallRecord(string Path,byte[] Bytes,InstalledFileRole Role);
internal sealed record FullUpdateInstallPlan(FullSystemInstallerPlan Enrollment,InstalledAuthorityAnchor Anchor,SystemBootstrapSnapshot Bootstrap,RecoveryHelperSlotsSelection Helpers,FullInstallRecord[] Records);
internal static class FullUpdateInstallerPlan
{
    internal static FullUpdateInstallPlan Prepare(string installation,string owner,FirstOwnerBinding binding,UpdateSetupChoice choice,
        SystemCandidateDescriptor system,string policyDigest,string pinDigest)
    {
        // Fresh wire consent expires in thirty minutes. A durable protected
        // same-package installer intent retains its original approval time;
        // resuming publication must neither expire nor renew that consent.
        choice.RequirePersisted(DateTimeOffset.UtcNow);binding.Require(installation,owner);
        var enrollment=FullSystemInstallerEnrollment.Prepare(installation,choice.ConsentVersion,binding.familyId,binding.parentUserId,policyDigest,pinDigest,DateTimeOffset.FromUnixTimeSeconds(choice.ApprovedUnix),choice.Automatic);
        var probe=system.Components.Single(c=>c.Kind==SystemComponentKind.Probe);
        var anchor=new InstalledAuthorityAnchor(1,installation,InstalledUpdaterLayout.RuntimeServiceName,InstalledUpdaterLayout.AuthorityServiceName,policyDigest,pinDigest,probe.EntrySha256,enrollment.PolicyDigest);
        var components=system.Components.Select(c=>new SystemBootstrapComponentState(c.Kind,c.EntryPoint,c.EntrySha256,c.Files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray())).ToArray();
        var current=new SystemBootstrapSnapshot(1,installation,1,system.Version,system.SystemManifestDigest,system.SystemManifestDigest,components,enrollment.PolicyDigest,system.SourceCommit);
        var component=components.Single(c=>c.Kind==SystemComponentKind.Helper);
        var closure=new RecoveryHelperClosure(system.SystemManifestDigest,system.Version,component.EntrySha256,component.Files);
        var helpers=new RecoveryHelperSlotsSelection(1,installation,enrollment.PolicyDigest,1,RecoveryHelperSlot.A,
            [new(RecoveryHelperSlot.A,RecoveryHelperSlotPhase.Verified,closure),new(RecoveryHelperSlot.B,RecoveryHelperSlotPhase.Verified,closure)]);
        var records=new List<FullInstallRecord>{new(InstalledUpdaterLayout.AnchorFile,JsonSerializer.SerializeToUtf8Bytes(anchor),InstalledFileRole.Anchor),
            new(enrollment.EnrollmentPath,enrollment.Enrollment,InstalledFileRole.Recovery),new("intake/enrollment.json",enrollment.OperatorBinding,InstalledFileRole.Recovery),
            new("intake/mode.json",enrollment.Mode,InstalledFileRole.Recovery),new("system-bootstrap-current.json",JsonSerializer.SerializeToUtf8Bytes(current),InstalledFileRole.Control),
            new("bootstrap-helper-selection.json",JsonSerializer.SerializeToUtf8Bytes(helpers),InstalledFileRole.Control)};
        if(choice.Automatic)records.Add(new(enrollment.GrantPath,enrollment.InitialGrant,InstalledFileRole.Recovery));
        return new(enrollment,anchor,current,helpers,records.ToArray());
    }
    internal static void ValidatePinned(FullUpdateInstallPlan plan,FullInstallState state,SystemCandidateDescriptor system,string policyDigest,string pinDigest)
    {
        var enrollment=NativeJson.Read<FullSystemEnrollment>(plan.Enrollment.Enrollment);var grant=NativeJson.Read<FullSystemModeGrant>(plan.Enrollment.InitialGrant);
        var mode=NativeJson.Read<NativeUpdateMode>(plan.Enrollment.Mode);var binding=NativeJson.Read<NativeUpdateEnrollment>(plan.Enrollment.OperatorBinding);
        InstalledActivationResolver.Id(enrollment.Id);InstalledActivationResolver.Id(grant.Id);
        var digest=Convert.ToHexStringLower(SHA256.HashData(plan.Enrollment.Enrollment));
        if(enrollment.Format!=1||enrollment.Profile!=FullSystemUpdatePolicy.Profile||enrollment.InstallationId!=state.InstallationId||enrollment.Repository!=NativeCandidateIntake.Repository||enrollment.Channel!="stable"||enrollment.Trust!="https-github-unsigned"||enrollment.ConsentVersion!=2||enrollment.ApprovedUnix!=state.Consent.ApprovedUnix||!enrollment.Scopes.SequenceEqual(FullSystemUpdatePolicy.RequiredScopes)||plan.Enrollment.PolicyDigest!=digest)throw new IOException("UPDATE_INSTALLER_PLAN_CHANGED");
        if(plan.Enrollment.EnrollmentPath!="intake/full-system-enrollment.json"||plan.Enrollment.GrantPath!="grants/system-grant-"+grant.Id+".json"||grant.Format!=1||grant.InstallationId!=state.InstallationId||grant.EnrollmentId!=enrollment.Id||grant.PolicyDigest!=digest||grant.ApprovedUnix!=state.Consent.ApprovedUnix)throw new IOException("UPDATE_INSTALLER_PLAN_CHANGED");
        if(mode.Format!=2||mode.InstallationId!=state.InstallationId||mode.Mode!=(state.Consent.Automatic?"auto":"notify")||mode.DelegationId!=(state.Consent.Automatic?grant.Id:null)||!mode.ModeControlAllowed||mode.GrantKind!="system")throw new IOException("UPDATE_INSTALLER_PLAN_CHANGED");
        if(binding.Format!=1||binding.InstallationId!=state.InstallationId||binding.Repository!=NativeCandidateIntake.Repository||binding.Channel!="stable"||binding.AssetPattern!="yahad-app-native-{version}.zip"||binding.Trust!="https-github-unsigned"||binding.FamilyId!=state.FirstOwner.familyId||binding.UserId!=state.FirstOwner.parentUserId||binding.PolicyDigest!=policyDigest||binding.PinDigest!=pinDigest)throw new IOException("UPDATE_INSTALLER_PLAN_CHANGED");
        var expectedAnchor=new InstalledAuthorityAnchor(1,state.InstallationId,InstalledUpdaterLayout.RuntimeServiceName,InstalledUpdaterLayout.AuthorityServiceName,policyDigest,pinDigest,system.Components.Single(c=>c.Kind==SystemComponentKind.Probe).EntrySha256,digest);
        if(plan.Anchor!=expectedAnchor)throw new IOException("UPDATE_INSTALLER_PLAN_CHANGED");
        var components=system.Components.Select(c=>new SystemBootstrapComponentState(c.Kind,c.EntryPoint,c.EntrySha256,c.Files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray())).ToArray();
        var bootstrap=new SystemBootstrapSnapshot(1,state.InstallationId,1,system.Version,system.SystemManifestDigest,system.SystemManifestDigest,components,digest,system.SourceCommit);
        if(JsonSerializer.Serialize(plan.Bootstrap)!=JsonSerializer.Serialize(bootstrap))throw new IOException("UPDATE_INSTALLER_PLAN_CHANGED");
        var helper=components.Single(c=>c.Kind==SystemComponentKind.Helper);var closure=new RecoveryHelperClosure(system.SystemManifestDigest,system.Version,helper.EntrySha256,helper.Files);
        var helpers=new RecoveryHelperSlotsSelection(1,state.InstallationId,digest,1,RecoveryHelperSlot.A,[new(RecoveryHelperSlot.A,RecoveryHelperSlotPhase.Verified,closure),new(RecoveryHelperSlot.B,RecoveryHelperSlotPhase.Verified,closure)]);
        if(JsonSerializer.Serialize(plan.Helpers)!=JsonSerializer.Serialize(helpers))throw new IOException("UPDATE_INSTALLER_PLAN_CHANGED");
        var expected=new Dictionary<string,(byte[] Bytes,InstalledFileRole Role)>(StringComparer.Ordinal)
        {
            [InstalledUpdaterLayout.AnchorFile]=(JsonSerializer.SerializeToUtf8Bytes(expectedAnchor),InstalledFileRole.Anchor),
            [plan.Enrollment.EnrollmentPath]=(plan.Enrollment.Enrollment,InstalledFileRole.Recovery),["intake/enrollment.json"]=(plan.Enrollment.OperatorBinding,InstalledFileRole.Recovery),
            ["intake/mode.json"]=(plan.Enrollment.Mode,InstalledFileRole.Recovery),["system-bootstrap-current.json"]=(JsonSerializer.SerializeToUtf8Bytes(bootstrap),InstalledFileRole.Control),
            ["bootstrap-helper-selection.json"]=(JsonSerializer.SerializeToUtf8Bytes(helpers),InstalledFileRole.Control)
        };
        if(state.Consent.Automatic)expected.Add(plan.Enrollment.GrantPath,(plan.Enrollment.InitialGrant,InstalledFileRole.Recovery));
        if(plan.Records.Length!=expected.Count)throw new IOException("UPDATE_INSTALLER_PLAN_CHANGED");
        foreach(var record in plan.Records)if(!expected.Remove(record.Path,out var value)||record.Role!=value.Role||!record.Bytes.AsSpan().SequenceEqual(value.Bytes))throw new IOException("UPDATE_INSTALLER_PLAN_CHANGED");
    }
    internal static string Descriptor(InstalledFileRoles roles,InstalledFileRole role,bool directory,string? ownerClosure=null)
    {
        roles=roles.WithRecoveryHelpers();
        var flags=directory?"OICI":"";
        var authority=role is InstalledFileRole.Anchor or InstalledFileRole.Data or InstalledFileRole.MasterKey?"0x1200a9":"0x1301bf";
        var runtime=role==InstalledFileRole.Recovery?"":$"(A;{flags};{(role is InstalledFileRole.Data or InstalledFileRole.Lease?"0x1301bf":"0x1200a9")};;;{roles.Runtime.Value})";
        var updater=string.Concat(roles.UpdaterActors.Select(sid=>$"(A;{flags};{authority};;;{sid.Value})"));
        var descriptor=$"O:BAD:P(A;{flags};FA;;;SY)(A;{flags};FA;;;BA){updater}{runtime}";
        roles.Validate(new RawSecurityDescriptor(descriptor),role,directory);
        if(ownerClosure is not null)
        {
            var owner=new SecurityIdentifier(ownerClosure);if(!owner.IsAccountSid()||!ownerClosure.StartsWith("S-1-5-21-",StringComparison.Ordinal))throw new IOException("UPDATE_OWNER_BINDING");
            descriptor+=$"(A;{flags};0x1200a9;;;{ownerClosure})";
        }
        return descriptor;
    }
    internal static SystemCandidateDescriptor ReadPackage(InstallPackage package)
    {
        if(!package.FullSystem)throw new IOException("UPDATE_FULL_PACKAGE_REQUIRED");
        var manifest=package.ReadMember("system-manifest.json",8*1024*1024);
        if(Convert.ToHexStringLower(SHA256.HashData(manifest))!=package.Manifest.SystemManifestDigest)throw new IOException("UPDATE_SYSTEM_MANIFEST_HASH");
        var app=package.ReadMember("payload/bundle.json",8*1024*1024);
        using var build=JsonDocument.Parse(package.ReadMember("payload/legal/build-inputs.json",65536));
        var schema=PayloadSchema.Read(build.RootElement);
        var system=SystemCandidateDescriptor.ValidateInstaller(manifest,app,package.Manifest.Version,package.Manifest.SourceCommit,schema,Environment.OSVersion.Version.Build);
        foreach(var component in system.Components)foreach(var file in component.Files)
        {
            var record=package.Manifest.Files.SingleOrDefault(f=>f.Path==component.SlotRelativeDirectory+"/"+file.RelativePath);
            if(record is null||record.Size!=file.Size||record.Sha256!=file.Sha256)throw new IOException("UPDATE_COMPONENT_PACKAGE_BINDING");
        }
        return system;
    }
}

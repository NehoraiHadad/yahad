using System.IO;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Setup;
using Yahad.Service;

internal static class FullInstallerIntegrationTests
{
    private const string Owner="S-1-5-21-1-2-3-1001",Runtime="S-1-5-80-1-2-3-4-5",Authority="S-1-5-80-6-7-8-9-10";
    private static byte[] Json<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value);
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Assert(bool value){if(!value)throw new Exception("FULL_INSTALLER_ASSERTION");}
    private static void Deny(Action action){try{action();}catch(Exception e)when(e is IOException or UnauthorizedAccessException){return;}throw new Exception("FULL_INSTALLER_UNEXPECTED_ADMISSION");}
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("only a newly started recovery boot may finish successfully without staying running",()=>
        {
            foreach(var kind in new[]{FullServiceKind.RecoveryA,FullServiceKind.RecoveryB})
            {
                Assert(NativeFullService.OneShotCompleted(kind,true,1,0,0,0));
                Assert(!NativeFullService.OneShotCompleted(kind,true,2,0,0,0));
                Assert(!NativeFullService.OneShotCompleted(kind,true,2,uint.MaxValue,0,0));
                Assert(!NativeFullService.OneShotCompleted(kind,true,4,123,0,0));
                Assert(!NativeFullService.OneShotCompleted(kind,true,3,123,0,0));
                Deny(()=>NativeFullService.OneShotCompleted(kind,false,1,0,0,0));
                Deny(()=>NativeFullService.OneShotCompleted(kind,true,1,0,1077,0));
                Deny(()=>NativeFullService.OneShotCompleted(kind,true,1,0,1066,1));
                Deny(()=>NativeFullService.OneShotCompleted(kind,true,1,123,0,0));
                Deny(()=>NativeFullService.OneShotCompleted(kind,true,4,0,0,0));
            }
            foreach(var kind in new[]{FullServiceKind.Runtime,FullServiceKind.Authority})Deny(()=>NativeFullService.OneShotCompleted(kind,true,1,0,0,0));
            return Task.CompletedTask;
        });
        await check("stop is sent only when the service state accepts it and a pending state is bounded by its own progress",()=>
        {
            // START_PENDING without STOP is what refused removal with 1052.
            Assert(!NativeFullService.SendsStop(2,0,false));Assert(NativeFullService.SendsStop(2,1,false));
            Assert(NativeFullService.SendsStop(4,5,false));Assert(!NativeFullService.SendsStop(4,5,true));
            Assert(!NativeFullService.SendsStop(3,1,false));Assert(!NativeFullService.SendsStop(1,1,false));
            Assert(!NativeFullService.Stalled(300000,TimeSpan.FromMinutes(4)));Assert(NativeFullService.Stalled(300000,TimeSpan.FromMinutes(6)));
            Assert(!NativeFullService.Stalled(0,TimeSpan.FromSeconds(59)));Assert(NativeFullService.Stalled(0,TimeSpan.FromSeconds(61)));
            // A sent STOP moving RUNNING to STOP_PENDING is what refused removal as a generation change.
            Assert(NativeFullService.SameGeneration(42,3,42));Assert(NativeFullService.SameGeneration(42,4,42));
            Assert(!NativeFullService.SameGeneration(42,4,43));Assert(!NativeFullService.SameGeneration(42,2,42));
            return Task.CompletedTask;
        });
        await check("quiet setup accepts only explicit choices and builds the window's update consent",()=>
        {
            var install=QuietSetup.Parse(["--quiet-install","--updates","notify","--result",@"C:\out\result.json"])!;
            Assert(!install.Uninstall&&install.Mode==UninstallMode.KeepData&&install.AutomaticUpdates==false);
            Assert(QuietSetup.Parse(["--quiet-install","--updates","auto","--result",@"C:\r.json"])!.AutomaticUpdates==true);
            Assert(QuietSetup.Parse(["--quiet-uninstall","--data","delete","--result",@"C:\r.json"])!.Mode==UninstallMode.DeleteData);
            Assert(QuietSetup.Parse(["--quiet-uninstall","--data","keep","--result",@"C:\r.json"])!.Mode==UninstallMode.KeepData);
            foreach(var args in new[]{new[]{"--quiet-install","--result",@"C:\r.json"},["--quiet-install","--updates","off","--result",@"C:\r.json"],["--quiet-uninstall","--result",@"C:\r.json"]})Assert(QuietSetup.Parse(args) is null);
            try{QuietSetup.Parse(["--quiet-install","--updates","notify","--result","result.json"]);throw new Exception("relative result accepted");}catch(IOException){}
            var now=DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);var consent=QuietSetup.Consent(install,true,false,now)!;
            consent.Require(now);Assert(!consent.Automatic&&consent.ApprovedUnix==now.ToUnixTimeSeconds());
            Assert(QuietSetup.Consent(install,true,true,now) is null&&QuietSetup.Consent(install,false,false,now) is null);
            Assert(QuietSetup.Consent(QuietSetup.Parse(["--quiet-uninstall","--data","keep","--result",@"C:\r.json"])!,true,false,now) is null);
            return Task.CompletedTask;
        });
        await check("full installer parses source-bound baseline using policy protocol 2 without a fake GitHub receipt",()=>
        {
            var system=System();Assert(system.Components.Count==5&&system.Protocols["policy"].Writer==2&&system.SourceCommit==ApplicationRelease.SourceCommit);
            Deny(()=>SystemCandidateDescriptor.ValidateInstaller(system.ManifestBytes,system.ApplicationManifestBytes,system.Version,new string('a',40),6,19045));
            return Task.CompletedTask;
        });
        foreach(var automatic in new[]{false,true})await check("full installer pins actual first-parent binding and "+(automatic?"Auto":"Notify")+" consent across retries",()=>
        {
            var system=System();var installation=Guid.NewGuid().ToString("N");var owner=FirstOwnerBinding.Create(installation,Owner);var choice=new UpdateSetupChoice(2,true,automatic,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var plan=FullUpdateInstallerPlan.Prepare(installation,Owner,owner,choice,system,new string('b',64),new string('c',64));
            var state=State(system,installation,owner,choice);FullUpdateInstallerPlan.ValidatePinned(plan,state,system,new string('b',64),new string('c',64));
            var encoded=Json(plan);var reread=JsonSerializer.Deserialize<FullUpdateInstallPlan>(encoded)!;FullUpdateInstallerPlan.ValidatePinned(reread,state,system,new string('b',64),new string('c',64));Assert(Json(reread).SequenceEqual(encoded));
            var binding=JsonSerializer.Deserialize<NativeUpdateEnrollment>(plan.Enrollment.OperatorBinding)!;Assert(binding.FamilyId==owner.familyId&&binding.UserId==owner.parentUserId);
            Assert(plan.Records.Any(r=>r.Path.StartsWith("owner-ledger/system-grant-",StringComparison.Ordinal))==automatic);
            foreach(var changed in new[]{state with{OwnerSid="S-1-5-21-1-2-3-1002"},state with{FirstOwner=owner with{familyId=Guid.NewGuid().ToString("D")}},state with{Consent=choice with{Automatic=!automatic}}})Deny(()=>FullUpdateInstallerPlan.ValidatePinned(plan,changed,system,new string('b',64),new string('c',64)));
            var records=plan.Records.ToArray();records[0]=records[0] with{Bytes=[1,2,3]};Deny(()=>FullUpdateInstallerPlan.ValidatePinned(plan with{Records=records},state,system,new string('b',64),new string('c',64)));
            return Task.CompletedTask;
        });
        await check("full updater consent rejects absent acceptance, stale timestamps, future timestamps and old scope",()=>
        {
            var now=DateTimeOffset.UtcNow;foreach(var choice in new[]{new UpdateSetupChoice(1,true,true,now.ToUnixTimeSeconds()),new(2,false,true,now.ToUnixTimeSeconds()),new(2,true,true,now.AddHours(-1).ToUnixTimeSeconds()),new(2,true,true,now.AddHours(1).ToUnixTimeSeconds())})Deny(()=>choice.Require(now));
            return Task.CompletedTask;
        });
        await check("full service contracts bind each own virtual account and restrict updater versus Runtime grants",()=>
        {
            foreach(var kind in Enum.GetValues<FullServiceKind>())
            {
                var contract=FullServiceContract.Describe(kind);Assert(contract.AuthorityRights==0x35&&(contract.AuthorityRights&2)==0);
                var acl=new RawSecurityDescriptor(NativeFullService.ServiceDescriptor(Owner,Authority,contract.AuthorityRights,Runtime));Assert(acl.DiscretionaryAcl!.Count==7);
                var grant=acl.DiscretionaryAcl.Cast<CommonAce>().Single(a=>a.SecurityIdentifier.Value==Authority);Assert(grant.AccessMask==0x35);
                Assert(contract.Account==@"NT SERVICE\"+contract.Name);
                Assert(acl.DiscretionaryAcl.Cast<CommonAce>().Single(a=>a.SecurityIdentifier.Value==Runtime).AccessMask==5);
                foreach(var sid in UpdaterServicePrincipals.All(new(Authority)))Assert(acl.DiscretionaryAcl.Cast<CommonAce>().Single(a=>a.SecurityIdentifier==sid).AccessMask==0x35);
                if(kind is FullServiceKind.RecoveryA or FullServiceKind.RecoveryB)Assert(contract.StartType==2&&contract.FailureCommand is not null);
            }
        Deny(()=>NativeFullService.ServiceDescriptor(Owner,Authority,0xf01ff,Runtime));return Task.CompletedTask;
        });
        await check("installer role exception permits designated Owner RX only on the named immutable closure",()=>
        {
            var roles=new InstalledFileRoles(new(Authority),new(Runtime)).WithRecoveryHelpers();var state=State(System(),Guid.NewGuid().ToString("N"),null,null);var files=new FullInstallFiles(state);Assert(files.Role(files.PathOf(InstallerReadiness.RequestFile))==InstalledFileRole.Anchor);
            var descriptor=new RawSecurityDescriptor(FullUpdateInstallerPlan.Descriptor(roles,InstalledFileRole.Control,false,Owner));
            WindowsFileBoundary.ValidateInstallerRoleAcl(descriptor,roles,InstalledFileRole.Control,false,new(Owner));
            Deny(()=>roles.Validate(descriptor,InstalledFileRole.Control,false));
            Deny(()=>WindowsFileBoundary.ValidateInstallerRoleAcl(descriptor,roles,InstalledFileRole.Control,false,new("S-1-5-21-1-2-3-1002")));
            var widened=new RawSecurityDescriptor(FullUpdateInstallerPlan.Descriptor(roles,InstalledFileRole.Control,false,Owner).Replace("0x1200a9;;;"+Owner,"FA;;;"+Owner));Deny(()=>WindowsFileBoundary.ValidateInstallerRoleAcl(widened,roles,InstalledFileRole.Control,false,new(Owner)));
            var request=new RawSecurityDescriptor(FullUpdateInstallerPlan.Descriptor(roles,InstalledFileRole.Anchor,false));roles.Validate(request,InstalledFileRole.Anchor,false);
            var mutable=new RawSecurityDescriptor(FullUpdateInstallerPlan.Descriptor(roles,InstalledFileRole.Control,false));Deny(()=>roles.Validate(mutable,InstalledFileRole.Anchor,false));return Task.CompletedTask;
        });
        await check("full removal intent retains owner, exact binaries and choice beyond partial root deletion",()=>
        {
            var system=System();var state=State(system,Guid.NewGuid().ToString("N"),null,null);var inventory=Enumerable.Range(0,10).Select(i=>new InstallMember(i==0?"Yahad.Setup.exe":"bootstrap/authority/file-"+i+".dll",10,i==0?state.SetupSha256:new string('d',64))).ToArray();
            var application=new InstallerState(1,state.InstallationId,state.DataId,state.PackageDigest,state.PayloadDigest,state.SourceCommit,state.OwnerSid,state.Port,"committed",state.Files,state.RuntimeSid!,state.SetupSha256,6);
            var intent=new FullRemovalIntent(4,state with{Phase="removing_all_data"},UninstallMode.DeleteData,inventory,application,"prepared");
            foreach(var stage in FullRemovalIntent.Stages){var replay=JsonSerializer.Deserialize<FullRemovalIntent>(Json(intent with{Stage=stage}))!;replay.Require();replay.RequireSameInstallation(state);Assert(replay.AtLeast(stage));}
            var preserved=intent with{WasPreserved=true,State=intent.State with{PreservedBinaries=inventory}};preserved.Require();
            foreach(var stage in FullRemovalIntent.Stages)(preserved with{Stage=stage}).Require();
            Deny(()=>(intent with{WasPreserved=true}).Require());
            Deny(()=>(intent with{Mode=UninstallMode.KeepData}).Require());Deny(()=>(intent with{Stage="invented"}).Require());Deny(()=>intent.RequireSameInstallation(state with{InstallationId=Guid.NewGuid().ToString("N")}));
            var changed=inventory.ToArray();changed[1]=changed[1] with{Path="../unrelated.txt"};Deny(()=>(intent with{Binaries=changed}).Require());return Task.CompletedTask;
        });
        await check("full removal and preservation admit the standalone Setup size without widening component or total bounds",()=>
        {
            var state=State(System(),Guid.NewGuid().ToString("N"),null,null);
            var inventory=Enumerable.Range(0,10).Select(i=>new InstallMember(i==0?"Yahad.Setup.exe":"bootstrap/authority/file-"+i+".dll",i==0?576035434L:10,state.SetupSha256)).ToArray();
            var application=new InstallerState(1,state.InstallationId,state.DataId,state.PackageDigest,state.PayloadDigest,state.SourceCommit,state.OwnerSid,state.Port,"committed",state.Files,state.RuntimeSid!,state.SetupSha256,6);
            var intent=new FullRemovalIntent(4,state with{Phase="removing_all_data"},UninstallMode.DeleteData,inventory,application,"prepared");intent.Require();
            NativeFullSystemSetupHost.ValidateState(state with{PreservedBinaries=inventory});
            foreach(var size in new[]{1073741824L,1073741825L})
            {
                var next=inventory.ToArray();next[0]=next[0] with{Size=size};
                if(size==1073741824L){(intent with{Binaries=next}).Require();NativeFullSystemSetupHost.ValidateState(state with{PreservedBinaries=next});}
                else{Deny(()=>(intent with{Binaries=next}).Require());Deny(()=>NativeFullSystemSetupHost.ValidateState(state with{PreservedBinaries=next}));}
            }
            var ordinary=inventory.ToArray();ordinary[1]=ordinary[1] with{Size=536870913L};Deny(()=>(intent with{Binaries=ordinary}).Require());Deny(()=>NativeFullSystemSetupHost.ValidateState(state with{PreservedBinaries=ordinary}));
            var total=Enumerable.Range(0,17).Select(i=>new InstallMember(i==0?"Yahad.Setup.exe":"bootstrap/authority/file-"+i+".dll",i==0?1073741824L:536870912L,state.SetupSha256)).ToArray();
            Deny(()=>(intent with{Binaries=total}).Require());return Task.CompletedTask;
        });
        await check("full install ZIP accepts only the modern host closure while Format1 retains host",()=>
        {
            using(var legacy=LayoutPackage(false,false,false))Assert(!legacy.FullSystem);
            using(var modern=LayoutPackage(true,false,false))Assert(modern.FullSystem);
            Deny(()=>{using var duplicate=LayoutPackage(true,true,false);});
            Deny(()=>{using var missing=LayoutPackage(true,false,true);});
            return Task.CompletedTask;
        });
        await check("LAN full-profile namespace maps only the audited RuntimeHost closure into its existing live host paths",()=>
        {
            var files=new[]{new InstallMember("host/Yahad.ServiceHost.exe",1,new string('a',64)),new("bootstrap/servicehost/Yahad.ServiceHost.exe",2,new string('b',64)),
                new("bootstrap/servicehost/Yahad.ServiceHost.runtimeconfig.json",3,new string('c',64)),new("bootstrap/authority/Yahad.Authority.exe",4,new string('d',64)),new("bootstrap/servicehostX/foreign.dll",5,new string('e',64))};
            var modern=NativeLanInstall.HostBinaryMembers(files,true);var legacy=NativeLanInstall.HostBinaryMembers(files,false);
            Assert(modern.Length==2&&modern[0]==new LanOwnedFile("host/Yahad.ServiceHost.exe",2,new string('b',64))&&modern[1].Path=="host/Yahad.ServiceHost.runtimeconfig.json");
            Assert(legacy.Length==1&&legacy[0]==new LanOwnedFile("host/Yahad.ServiceHost.exe",1,new string('a',64)));return Task.CompletedTask;
        });
        await check("full-profile LAN changes preserve current cloned data context and lease the current restored output",()=>
        {
            var state=State(System(),Guid.NewGuid().ToString("N"),null,null);
            var recovery=new InstalledRecovery(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),new string('f',64),Guid.NewGuid().ToString("N"));
            var web=Json(new InstalledWeb(1,state.InstallationId,"http://127.0.0.1:"+state.Port,state.Port));
            var head=new InstalledActivation(1,state.InstallationId,3,state.PayloadDigest,Guid.NewGuid().ToString("N"),6,6,new string('a',64),new string('b',64),new string('c',64),new string('d',64),Hash(web),"committed",recovery);
            var bytes=Json(head);var before=new LanWebSnapshot(web,bytes,Json(new InstalledEpoch(1,state.InstallationId,3,Hash(bytes))),Json(new{schema=1,service=InstallLocations.ServiceName,installationId=state.InstallationId,activationReceiptDigest=Hash(bytes)}));
            var application=NativeLanInstall.FullApplication(state,head);var after=LanWebConfiguration.WithOrigin(before,application,"https://192.168.4.5");
            var observed=JsonSerializer.Deserialize<InstalledActivation>(after.Activation)!;Assert(observed.Recovery==recovery&&observed.DataId==head.DataId&&observed.OwnerIntentDigest==head.OwnerIntentDigest);
            Assert(NativeFullSystemSetupHost.CurrentDataPath(head)==Path.Combine(InstallLocations.State,"datasets",recovery.SourceDataId,"restore-"+recovery.OperationId,"completed","data"));
            Assert(NativeFullSystemSetupHost.CurrentDataPath(head)!=recovery.SourceDirectory(InstallLocations.State));
            Deny(()=>LanWebConfiguration.Require(before,application with{CurrentRecovery=null}));
            Deny(()=>LanWebConfiguration.Require(before,application with{CurrentRecovery=recovery with{OperationId=Guid.NewGuid().ToString("N")}}));
            var journal=new LanWebJournal(1,state.InstallationId,before,after);
            foreach(var name in LanWebConfiguration.Names)
            {
                var index=Array.IndexOf(LanWebConfiguration.Names,name);var a=LanWebConfiguration.Parts(before)[index];var b=LanWebConfiguration.Parts(after)[index];
                foreach(var partialBytes in new[]{a,b,a[..(a.Length/2)],b[..(b.Length/2)],Array.Empty<byte>()})LanWebConfiguration.RequireInstallerPartial(journal,name,partialBytes);
                Deny(()=>LanWebConfiguration.RequireInstallerPartial(journal,name,[0xff,0x00,0xfe]));
            }
            Deny(()=>LanWebConfiguration.RequireInstallerPartial(journal,"owner-intent.json",[]));return Task.CompletedTask;
        });
        await check("local Owner bootstrap requires its exact immutable full-profile installation anchor",()=>
        {
            var id=Guid.NewGuid().ToString("N");
            byte[] Anchor(string? full)=>Json(new{Format=1,InstallationId=id,RuntimeService=InstalledUpdaterLayout.RuntimeServiceName,AuthorityService=InstalledUpdaterLayout.AuthorityServiceName,OwnerSid=Owner,IntakePolicyDigest=new string('a',64),IntakePinDigest=new string('b',64),ProbeSha256=new string('c',64),FullSystemPolicyDigest=full});
            Assert(InstalledUpdaterLayout.AllowsOwnerLoopback(Anchor(new string('d',64)),id));Assert(!InstalledUpdaterLayout.AllowsOwnerLoopback(Anchor(null),id));
            Deny(()=>InstalledUpdaterLayout.AllowsOwnerLoopback(Anchor(new string('d',64)),Guid.NewGuid().ToString("N")));Deny(()=>InstalledUpdaterLayout.AllowsOwnerLoopback(Anchor("bad"),id));return Task.CompletedTask;
        });
        await check("full-profile consent crosses authenticated elevation framing without changing Owner or LAN selection",()=>
        {
            var choice=new UpdateSetupChoice(2,true,true,DateTimeOffset.UtcNow.ToUnixTimeSeconds());var wire=new SetupWire(1,"install",null,null,Update:choice);
            var observed=JsonSerializer.Deserialize<SetupWire>(Json(wire))!;Assert(observed.Update==choice&&observed.Lan is null&&observed.Removal is null);
            Assert(OwnerShortcuts.Arguments("confirm",new string('a',32))=="--confirm "+new string('a',32));Deny(()=>OwnerShortcuts.Arguments("run",new string('a',32)));Deny(()=>OwnerShortcuts.Arguments("recover","../path"));return Task.CompletedTask;
        });
        await check("state-only preserved installation never displays a fresh updater consent choice",()=>
        {
            var observed=new List<string>();Assert(MainWindow.HasExistingUpdateInstallation(path=>{observed.Add(path);return path==InstallLocations.State;}));Assert(observed.SequenceEqual(new[]{InstallLocations.State}));
            Assert(!MainWindow.HasExistingUpdateInstallation(_=>false));Assert(MainWindow.HasExistingUpdateInstallation(path=>path==InstallLocations.RemovalStateFile));return Task.CompletedTask;
        });
        await check("installer partial cleanup recognizes only exact generated metadata names with canonical nonces",()=>
        {
            foreach(var name in FullInstallerResidue.Metadata)
            {
                Assert(FullInstallerResidue.IsOwnedName(name+".partial-"+new string('a',32)));
                foreach(var suffix in new[]{"../data",new string('A',32),Guid.NewGuid().ToString("D"),""})Assert(!FullInstallerResidue.IsOwnedName(name+".partial-"+suffix));
            }
            foreach(var other in new[]{"activation.json","pending.json","epoch.json","master-key","bootstrap.json"})Assert(!FullInstallerResidue.IsOwnedName(other+".partial-"+new string('a',32)));return Task.CompletedTask;
        });
        await check("full-profile diagnostic records contain fixed stages and numeric failure evidence without private exception text",()=>
        {
            var failure=new SetupOperationException(UninstallPhase.FullStart,new IOException("private-token fake-secret-path C:\\private\\data"));
            var bytes=FullInstallDiagnostics.Encode(new string('a',32),failure,123);var text=global::System.Text.Encoding.UTF8.GetString(bytes);using var json=JsonDocument.Parse(bytes);
            Assert(!text.Contains("private",StringComparison.OrdinalIgnoreCase)&&!text.Contains("secret",StringComparison.OrdinalIgnoreCase)&&!text.Contains("StackTrace",StringComparison.Ordinal));
            Assert(json.RootElement.GetProperty("Stage").GetString()=="FullStart"&&json.RootElement.GetProperty("Code").GetString()=="INSTALL_FAILED");return Task.CompletedTask;
        });
        await check("interrupted service provisioning admits only the same never-started demand registration and administrative writers",()=>
        {
            NativeFullService.RequireProvisioningState(3,1,0,1077,0);
            foreach(var value in new(uint,uint,uint,uint,uint)[]{(2,1,0,1077,0),(4,1,0,1077,0),(3,4,123,0,0),(3,1,0,0,0),(3,1,0,1066,1),(3,1,123,1077,0)})Deny(()=>NativeFullService.RequireProvisioningState(value.Item1,value.Item2,value.Item3,value.Item4,value.Item5));
            NativeFullService.RequireAdministrativeWriters(new("O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x2008d;;;AU)"));
            Deny(()=>NativeFullService.RequireAdministrativeWriters(new("O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x35;;;AU)")));
        var exact="O:BA"+NativeFullService.ServiceDescriptor(Owner,Authority,0x35,Runtime);NativeFullService.RequireAdministrativeOwner(new(exact));
            Deny(()=>NativeFullService.RequireAdministrativeOwner(new(exact.Replace("O:BA","O:"+Owner))));
            Deny(()=>NativeFullService.RequireAdministrativeWriters(new(exact.Replace("O:BA","O:"+Owner))));return Task.CompletedTask;
        });
        await check("same-source protected consent retains its approval time during a delayed repair without renewing Auto",()=>
        {
            var system=System();var id=Guid.NewGuid().ToString("N");var owner=FirstOwnerBinding.Create(id,Owner);var old=new UpdateSetupChoice(2,true,true,DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeSeconds());Deny(()=>old.Require(DateTimeOffset.UtcNow));
            var plan=FullUpdateInstallerPlan.Prepare(id,Owner,owner,old,system,new string('b',64),new string('c',64));
            var state=State(system,id,owner,old);FullUpdateInstallerPlan.ValidatePinned(plan,state,system,new string('b',64),new string('c',64));
            var grant=JsonSerializer.Deserialize<FullSystemModeGrant>(plan.Enrollment.InitialGrant)!;Assert(grant.ApprovedUnix==old.ApprovedUnix);return Task.CompletedTask;
        });
        await check("concurrent installer retry cannot replace durable Auto choice or approval time before plan publication",()=>
        {
            var captured=new UpdateSetupChoice(2,true,true,123);captured.RequireCompatibleRetry(null);captured.RequireCompatibleRetry(captured with{ApprovedUnix=456});
            Deny(()=>captured.RequireCompatibleRetry(captured with{Automatic=false,ApprovedUnix=456}));Deny(()=>captured.RequireCompatibleRetry(captured with{ConsentVersion=1}));Assert(captured.ApprovedUnix==123);return Task.CompletedTask;
        });
    }
    private static InstallPackage LayoutPackage(bool full,bool duplicate,bool missing)
    {
        var names=new List<string>{"payload/bundle.json","payload/runtime/node/node.exe","payload/runtime/ffmpeg/ffmpeg.exe","payload/runtime/caddy/caddy.exe","payload/app/server/main.mjs","payload/app/dist/index.html","payload/app/native/Yahad.Platform.exe","payload/legal/fixture.json"};
        names.AddRange(full?new[]{"system-manifest.json","bootstrap/authority/Yahad.Authority.exe","bootstrap/owner/Yahad.Owner.exe","bootstrap/servicehost/Yahad.ServiceHost.exe","bootstrap/servicehost/Yahad.ServiceHost.runtimeconfig.json","bootstrap/probe/node/node.exe","bootstrap/handoff/Yahad.Authority.exe"}:new[]{"host/Yahad.ServiceHost.exe","host/Yahad.ServiceHost.runtimeconfig.json"});
        if(duplicate)names.Add("host/Yahad.ServiceHost.exe");if(missing)names.Remove("bootstrap/servicehost/Yahad.ServiceHost.runtimeconfig.json");
        var stream=new MemoryStream();using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))foreach(var name in names){using var entry=zip.CreateEntry(name).Open();entry.WriteByte(1);}
        var bytes=stream.ToArray();stream.Position=0;var sha=Hash([1]);
        try{return new(stream,Json(new InstallPackageManifest(full?2:1,ApplicationRelease.Version,ApplicationRelease.SourceCommit,Hash(bytes),new string('a',64),names.Select(n=>new InstallMember(n,1,sha)).ToArray(),full?new string('b',64):null)));}
        catch{stream.Dispose();throw;}
    }
    private static FullInstallState State(SystemCandidateDescriptor system,string installation,FirstOwnerBinding? binding,UpdateSetupChoice? choice)
    {
        var records=Enumerable.Range(0,10).Select(i=>new InstallMember("payload/file-"+i,1,new string('d',64))).ToArray();
        return new(3,installation,Guid.NewGuid().ToString("N"),new string('a',64),system.ApplicationManifestDigest,ApplicationRelease.SourceCommit,system.SystemManifestDigest,Owner,47123,6,"committed",new string('d',64),binding??FirstOwnerBinding.Create(installation,Owner),choice??new(2,true,false,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),records,Runtime,Authority);
    }
    private static SystemCandidateDescriptor System()
    {
        var application=Json(new{schema=1,kind="windows-runtime-candidate",profile="full",target="win-x64",version=ApplicationRelease.Version,authority="application-updater"});var sha=new string('a',64);
        var components=Enum.GetValues<SystemComponentKind>().Select(kind=>new{kind=kind.ToString(),prefix=FixedSystemComponents.SlotPrefix(kind),entryPoint=FixedSystemComponents.EntryPoint(kind),entrySha256=sha,files=new[]{new{path=FixedSystemComponents.EntryPoint(kind),size=10,sha256=sha},new{path="build-identity.json",size=10,sha256=sha}}});
        var protocols=new[]{"system","handoff","authorityRuntime","ownerAuthority","activation","journal","recovery","policy"}.ToDictionary(n=>n,n=>new{minimumReader=n=="policy"?2:1,maximumReader=n=="policy"?2:1,writer=n=="policy"?2:1});
        var manifest=Json(new{format=2,kind="windows-system-candidate",profile="full-system-v1",target="win-x64",version=ApplicationRelease.Version,sourceCommit=ApplicationRelease.SourceCommit,minimumWindowsBuild=19045,applicationManifestSha256=Hash(application),database=new{targetSchema=6,migrationsFrom=new[]{1,2,3,4,5}},protocols,components});
        return SystemCandidateDescriptor.ValidateInstaller(manifest,application,ApplicationRelease.Version,ApplicationRelease.SourceCommit,6,19045);
    }
}

using System.IO;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
using Yahad.Setup;
using Yahad.Service;

internal static class FullInstallerIntegrationTests
{
    private const string Owner="S-1-5-21-1-2-3-1001";
    private static byte[] Json<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value);
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Assert(bool value){if(!value)throw new Exception("FULL_INSTALLER_ASSERTION");}
    private static void Deny(Action action){try{action();}catch(Exception e)when(e is IOException or UnauthorizedAccessException){return;}throw new Exception("FULL_INSTALLER_UNEXPECTED_ADMISSION");}
    private static void DenyCode(string code,Action action){try{action();}catch(IOException e)when(e.Message==code){return;}throw new Exception("FULL_INSTALLER_EXPECTED_"+code);}
    internal static InstallerState State(string phase="committed",string? version=null)
    {
        var installation=Guid.NewGuid().ToString("N");
        var files=Enumerable.Range(0,10).Select(i=>new InstallMember(i==0?"host/Yahad.ServiceHost.exe":"payload/file-"+i,1,new string('d',64))).ToArray();
        return new(InstallerState.CurrentFormat,installation,Guid.NewGuid().ToString("N"),version??ApplicationRelease.Version,new string('a',64),new string('b',64),ApplicationRelease.SourceCommit,Owner,47123,phase,new string('c',64),
            FirstOwnerBinding.Create(installation,Owner),new UpdateSetupChoice(2,true,false,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),files,InstallLocations.ServiceSid);
    }
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("stop is sent only when the service state accepts it and a pending state is bounded by its own progress",()=>
        {
            // START_PENDING without STOP is what refused removal with 1052.
            Assert(!NativeRuntimeService.SendsStop(2,0,false));Assert(NativeRuntimeService.SendsStop(2,1,false));
            Assert(NativeRuntimeService.SendsStop(4,5,false));Assert(!NativeRuntimeService.SendsStop(4,5,true));
            Assert(!NativeRuntimeService.SendsStop(3,1,false));Assert(!NativeRuntimeService.SendsStop(1,1,false));
            Assert(!NativeRuntimeService.Stalled(300000,TimeSpan.FromMinutes(4)));Assert(NativeRuntimeService.Stalled(300000,TimeSpan.FromMinutes(6)));
            Assert(!NativeRuntimeService.Stalled(0,TimeSpan.FromSeconds(59)));Assert(NativeRuntimeService.Stalled(0,TimeSpan.FromSeconds(61)));
            // A sent STOP moving RUNNING to STOP_PENDING is what refused removal as a generation change.
            Assert(NativeRuntimeService.SameGeneration(42,3,42));Assert(NativeRuntimeService.SameGeneration(42,4,42));
            Assert(!NativeRuntimeService.SameGeneration(42,4,43));Assert(!NativeRuntimeService.SameGeneration(42,2,42));
            NativeRuntimeService.RequireRemovalStopped(1,0);Deny(()=>NativeRuntimeService.RequireRemovalStopped(1,12));Deny(()=>NativeRuntimeService.RequireRemovalStopped(3,0));
            return Task.CompletedTask;
        });
        await check("the Runtime service restarts after 5 s, 30 s and 2 min over one day and removal clears every action",()=>
        {
            var plan=NativeRuntimeService.FailurePlan(true);
            Assert(plan.Length==3&&plan.All(a=>a.Kind==1)&&plan.Select(a=>a.Delay).SequenceEqual(new uint[]{5000,30000,120000})&&NativeRuntimeService.FailureReset==86400);
            NativeRuntimeService.RequireFailurePlan(true,86400,plan);NativeRuntimeService.RequireFailurePlan(false,0,[]);
            Deny(()=>NativeRuntimeService.RequireFailurePlan(true,0,plan));Deny(()=>NativeRuntimeService.RequireFailurePlan(true,86400,plan[..2]));
            Deny(()=>NativeRuntimeService.RequireFailurePlan(true,86400,[plan[0],plan[1],new(){Kind=0,Delay=120000}]));
            Deny(()=>NativeRuntimeService.RequireFailurePlan(true,86400,[plan[0],plan[1],new(){Kind=1,Delay=60000}]));
            Deny(()=>NativeRuntimeService.RequireFailurePlan(false,0,plan));Deny(()=>NativeRuntimeService.RequireFailurePlan(false,86400,[]));
            Assert(NativeRuntimeService.AutoStart==2&&NativeRuntimeService.DemandStart==3&&NativeRuntimeService.Disabled==4);
            return Task.CompletedTask;
        });
        await check("the one service binds its own virtual account and grants the installer and Runtime query only",()=>
        {
            Assert(InstallLocations.ServiceName=="Yahad.Acceptance"&&InstallLocations.ServiceAccount==@"NT SERVICE\Yahad.Acceptance");
            // The installer grants files to the service SID before CreateService: it is SHA-1 of the upper-case name.
            Assert(InstallLocations.ServiceSidFor("TrustedInstaller")=="S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
            Assert(InstallLocations.ServiceSidFor("yahad.acceptance")==InstallLocations.ServiceSid&&InstallLocations.ServiceSid.StartsWith("S-1-5-80-",StringComparison.Ordinal));
            var exact="O:BA"+NativeRuntimeService.ServiceDescriptor(Owner,InstallLocations.ServiceSid);var acl=new RawSecurityDescriptor(exact);Assert(acl.DiscretionaryAcl!.Count==4);
            NativeRuntimeService.RequireProtection(acl,Owner);
            Assert(acl.DiscretionaryAcl.Cast<CommonAce>().Where(a=>a.SecurityIdentifier.Value is not("S-1-5-18" or "S-1-5-32-544")).All(a=>a.AccessMask==5));
            Deny(()=>NativeRuntimeService.RequireProtection(new(exact.Replace("0x00000005;;;"+Owner,"0x00000035;;;"+Owner)),Owner));
            Deny(()=>NativeRuntimeService.RequireProtection(new(exact+"(A;;0x00000005;;;AU)"),Owner));
            Deny(()=>NativeRuntimeService.RequireProtection(new(exact.Replace("O:BA","O:"+Owner)),Owner));
            Deny(()=>NativeRuntimeService.RequireProtection(acl,"S-1-5-21-1-2-3-1002"));
            Deny(()=>NativeRuntimeService.ServiceDescriptor("S-1-5-32-545",InstallLocations.ServiceSid));Deny(()=>NativeRuntimeService.ServiceDescriptor(Owner,"S-1-5-21-1-2-3-4"));
            Assert(InstallLocations.ServiceCommand("1.2.3")=="\""+Path.Combine(InstallLocations.Binaries,"versions","1.2.3","host","Yahad.ServiceHost.exe")+"\" --service");
            foreach(var bad in new[]{"1.2","01.2.3","1.2.3-beta","..","1.2.3\\..\\x"})Deny(()=>InstallLocations.ServiceCommand(bad));
            return Task.CompletedTask;
        });
        await check("interrupted service provisioning admits only the same never-started demand registration and administrative writers",()=>
        {
            NativeRuntimeService.RequireProvisioningState(3,1,0,1077,0);
            foreach(var value in new(uint,uint,uint,uint,uint)[]{(2,1,0,1077,0),(4,1,0,1077,0),(3,4,123,0,0),(3,1,0,0,0),(3,1,0,1066,1),(3,1,123,1077,0)})Deny(()=>NativeRuntimeService.RequireProvisioningState(value.Item1,value.Item2,value.Item3,value.Item4,value.Item5));
            NativeRuntimeService.RequireAdministrativeWriters(new("O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x2008d;;;AU)"));
            Deny(()=>NativeRuntimeService.RequireAdministrativeWriters(new("O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x35;;;AU)")));
            var exact="O:BA"+NativeRuntimeService.ServiceDescriptor(Owner,InstallLocations.ServiceSid);NativeRuntimeService.RequireAdministrativeOwner(new(exact));
            Deny(()=>NativeRuntimeService.RequireAdministrativeOwner(new(exact.Replace("O:BA","O:"+Owner))));
            Deny(()=>NativeRuntimeService.RequireAdministrativeWriters(new(exact.Replace("O:BA","O:"+Owner))));return Task.CompletedTask;
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
            var now=DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);var consent=QuietSetup.Consent(install,false,now)!;
            consent.Require(now);Assert(!consent.Automatic&&consent.ApprovedUnix==now.ToUnixTimeSeconds());
            Assert(QuietSetup.Consent(install,true,now) is null);
            Assert(QuietSetup.Consent(QuietSetup.Parse(["--quiet-uninstall","--data","keep","--result",@"C:\r.json"])!,false,now) is null);
            return Task.CompletedTask;
        });
        await check("updater consent rejects absent acceptance, stale timestamps, future timestamps and old scope",()=>
        {
            var now=DateTimeOffset.UtcNow;foreach(var choice in new[]{new UpdateSetupChoice(1,true,true,now.ToUnixTimeSeconds()),new(2,false,true,now.ToUnixTimeSeconds()),new(2,true,true,now.AddHours(-1).ToUnixTimeSeconds()),new(2,true,true,now.AddHours(1).ToUnixTimeSeconds())})Deny(()=>choice.Require(now));
            return Task.CompletedTask;
        });
        await check("concurrent installer retry cannot replace the durable update choice or its approval time",()=>
        {
            var captured=new UpdateSetupChoice(2,true,true,123);captured.RequireCompatibleRetry(null);captured.RequireCompatibleRetry(captured with{ApprovedUnix=456});
            Deny(()=>captured.RequireCompatibleRetry(captured with{Automatic=false,ApprovedUnix=456}));Deny(()=>captured.RequireCompatibleRetry(captured with{ConsentVersion=1}));Assert(captured.ApprovedUnix==123);
            // A kept installation retains its first approval across a later reinstall.
            var old=new UpdateSetupChoice(2,true,true,DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeSeconds());Deny(()=>old.Require(DateTimeOffset.UtcNow));old.RequirePersisted(DateTimeOffset.UtcNow);
            (State() with{Consent=old}).Require();return Task.CompletedTask;
        });
        await check("installer state admits only Format 6 of this one service and its own first owner",()=>
        {
            var state=State();state.Require();foreach(var phase in InstallerState.Phases)(state with{Phase=phase}).Require();
            var replay=JsonSerializer.Deserialize<InstallerState>(Json(state))!;Assert(Json(replay).SequenceEqual(Json(state)));
            foreach(var bad in new[]{state with{Format=5},state with{Phase="restoring"},state with{ServiceSid="S-1-5-80-1-2-3-4-5"},state with{OwnerSid="S-1-5-18"},state with{Port=80},
                state with{Version="1.2"},state with{InstallationId="x"},state with{SourceCommit=new string('g',40)},state with{SetupSha256="x"},state with{Files=state.Files[..9]},
                state with{Files=[..state.Files,state.Files[0]]},state with{Files=[..state.Files[..9],new("bootstrap/authority/Yahad.Authority.exe",1,new string('d',64))]},
                state with{FirstOwner=state.FirstOwner with{installationId=Guid.NewGuid().ToString("N")}},state with{Consent=state.Consent with{Accepted=false}}})Deny(bad.Require);
            return Task.CompletedTask;
        });
        await check("retired four-service records are refused for install and removal and never adopted",()=>
        {
            foreach(var format in InstallerState.LegacyFormats)DenyCode("LEGACY_INSTALL_UNINSTALL_FIRST",()=>NativeSetupHost.RequireCurrentFormat(Json(new{Format=format,Phase="committed"})));
            Assert(InstallerState.LegacyFormats.SequenceEqual(new[]{3,4,5}));
            NativeSetupHost.RequireCurrentFormat(Json(State()));
            foreach(var bytes in new[]{Json(new{Format=2}),Json(new{Format=7}),Json(new{Phase="committed"}),Json(new{Format="6"}),"not json"u8.ToArray()})DenyCode("INSTALL_STATE_INVALID",()=>NativeSetupHost.RequireCurrentFormat(bytes));
            return Task.CompletedTask;
        });
        await check("removal intent retains owner, exact binaries and choice beyond partial root deletion",()=>
        {
            var state=State();var inventory=RemovalIntent.Inventory(state,1234);
            Assert(inventory.Length==state.Files.Length+1&&inventory.Count(f=>f.Path==RemovalIntent.SetupMember&&f.Size==1234&&f.Sha256==state.SetupSha256)==1);
            Assert(inventory.Where(f=>f.Path!=RemovalIntent.SetupMember).All(f=>f.Path.StartsWith("versions/"+state.Version+"/",StringComparison.Ordinal)));
            var intent=new RemovalIntent(6,state with{Phase="removing_all_data"},UninstallMode.DeleteData,inventory,"prepared");
            foreach(var stage in RemovalIntent.Stages){var replay=JsonSerializer.Deserialize<RemovalIntent>(Json(intent with{Stage=stage}))!;replay.Require();replay.RequireSameInstallation(state);Assert(replay.AtLeast(stage)&&replay.AtLeast("prepared"));}
            Assert(!intent.AtLeast("lan_removed"));Deny(()=>intent.AtLeast("invented"));
            (intent with{Mode=UninstallMode.KeepData,State=state with{Phase="removing_keep_data"}}).Require();
            Deny(()=>(intent with{Mode=UninstallMode.KeepData}).Require());Deny(()=>(intent with{Stage="invented"}).Require());Deny(()=>(intent with{Format=4}).Require());
            Deny(()=>intent.RequireSameInstallation(state with{InstallationId=Guid.NewGuid().ToString("N")}));Deny(()=>intent.RequireSameInstallation(state with{Version="0.0.1"}));
            intent.RequireSameInstallation(state with{Phase="uninstalled"});
            foreach(var path in new[]{"../unrelated.txt","versions/9.9.9/host/Yahad.ServiceHost.exe","versions/"+state.Version+"/bootstrap/authority/a.dll","payloads/x/bundle.json"})
            {var changed=inventory.ToArray();changed[1]=changed[1] with{Path=path};Deny(()=>(intent with{Binaries=changed}).Require());}
            Deny(()=>(intent with{Binaries=inventory.Where(f=>f.Path!=RemovalIntent.SetupMember).ToArray()}).Require());
            Deny(()=>(intent with{Binaries=[..inventory,inventory[1]]}).Require());return Task.CompletedTask;
        });
        await check("runtime package accepts only Format 1 host and payload members anchored to its bundle",()=>
        {
            using(var exact=LayoutPackage())Assert(exact.Manifest.Format==1);
            DenyCode("PACKAGE_FORMAT",()=>{using var full=LayoutPackage(format:2);});
            DenyCode("PACKAGE_PATH",()=>{using var bootstrap=LayoutPackage(extra:"bootstrap/authority/Yahad.Authority.exe");});
            DenyCode("PACKAGE_PATH",()=>{using var manifest=LayoutPackage(extra:"system-manifest.json");});
            DenyCode("PACKAGE_MISSING",()=>{using var missing=LayoutPackage(omit:"host/Yahad.ServiceHost.runtimeconfig.json");});
            DenyCode("PACKAGE_PAYLOAD_ANCHOR",()=>{using var anchor=LayoutPackage(payloadDigest:new string('e',64));});
            DenyCode("PACKAGE_SOURCE",()=>{using var source=LayoutPackage(source:new string('0',40)==ApplicationRelease.SourceCommit?new string('1',40):new string('0',40));});
            return Task.CompletedTask;
        });
        await check("an interrupted extraction resumes only over this package's members and their owned partials",()=>
        {
            var expected=new[]{"Yahad.Setup.exe","versions/1.2.3/host/Yahad.ServiceHost.exe","versions/1.2.3/payload/app/server/main.mjs"};var partial=".partial-"+Guid.NewGuid().ToString("N");
            var partials=InstallPackage.RequireExtractedInventory(expected,[expected[1],expected[2]+partial,"Yahad.Setup.exe"+partial],["versions","versions/1.2.3","versions/1.2.3/host","versions/1.2.3/payload","versions/1.2.3/payload/app","versions/1.2.3/payload/app/server"]);
            Assert(partials.Length==2&&partials.Contains(expected[2]+partial));
            foreach(var file in new[]{"versions/1.2.3/host/foreign.dll",expected[1]+".partial-"+Guid.NewGuid().ToString("D"),expected[1]+".partial-"+new string('A',32),"other.exe"+partial,expected[1]+".partial-"})
                DenyCode("INSTALL_TARGET_CHANGED",()=>InstallPackage.RequireExtractedInventory(expected,[file],[]));
            foreach(var directory in new[]{"versions/1.2.4","versions/1.2.3/host/x","payloads"})DenyCode("INSTALL_TARGET_CHANGED",()=>InstallPackage.RequireExtractedInventory(expected,[],[directory]));
            return Task.CompletedTask;
        });
        await check("extraction never rewrites a present member and leaves no partial when a member fails",()=>
        {
            using var package=LayoutPackage();var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var other=SafeDirectory.CreatePrivateSession(Path.GetTempPath());
            string[] Extracted(string directory)=>Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories).Select(f=>Path.GetRelativePath(directory,f).Replace(Path.DirectorySeparatorChar,'/')).Where(f=>f!=".yahad-fixture").Order(StringComparer.Ordinal).ToArray();
            try
            {
                package.Extract(root,_=>{},default);package.Extract(root,_=>{},default);
                Assert(Extracted(root).SequenceEqual(package.Manifest.Files.Select(f=>f.Path).Order(StringComparer.Ordinal)));
                var node=Path.Combine(root,"payload","runtime","node","node.exe");File.WriteAllBytes(node,[2]);DenyCode("INSTALL_TARGET_CHANGED",()=>package.Extract(root,_=>{},default));
                Assert(File.ReadAllBytes(node).SequenceEqual(new byte[]{2}));
                var failing=new ExtractTarget(d=>Directory.CreateDirectory(d),ExtractTarget.Private.CreateFile,(_,_)=>throw new IOException("COMMIT_CUT"),ExtractTarget.Private.ExistingMatches);
                DenyCode("COMMIT_CUT",()=>package.Extract(other,_=>{},default,failing));Assert(Extracted(other).Length==0);
            }
            finally{Directory.Delete(root,true);Directory.Delete(other,true);}
            return Task.CompletedTask;
        });
        await check("LAN proxy copies only the installed release's own host closure",()=>
        {
            var files=new[]{new InstallMember("host/Yahad.ServiceHost.exe",1,new string('a',64)),new("host/Yahad.ServiceHost.runtimeconfig.json",3,new string('c',64)),
                new("payload/runtime/caddy/caddy.exe",4,new string('d',64)),new("hostX/foreign.dll",5,new string('e',64))};
            var members=NativeLanInstall.HostBinaryMembers(files);
            Assert(members.Length==2&&members[0]==new LanOwnedFile("host/Yahad.ServiceHost.exe",1,new string('a',64))&&members[1].Path=="host/Yahad.ServiceHost.runtimeconfig.json");return Task.CompletedTask;
        });
        await check("LAN origin changes rewrite only the launch file's origin and recover only its two exact versions",()=>
        {
            var state=State();var before=new LanWebSnapshot(InstalledLaunch.Encode(new(1,state.InstallationId,state.DataId,state.Port,"http://127.0.0.1:"+state.Port)));
            LanWebConfiguration.Require(before,state);var after=LanWebConfiguration.WithOrigin(before,state,"https://192.168.4.5");
            var launch=InstalledLaunch.Parse(after.Installed);Assert(launch.Origin=="https://192.168.4.5"&&launch.InstallationId==state.InstallationId&&launch.DataId==state.DataId&&launch.Port==state.Port);
            Assert(LanWebConfiguration.Names.SequenceEqual(new[]{"installed.json"}));
            foreach(var origin in new[]{"https://8.8.8.8","http://192.168.4.5","https://192.168.4.5:8443","http://127.0.0.1:47124"})Deny(()=>LanWebConfiguration.WithOrigin(before,state,origin));
            foreach(var other in new[]{state with{InstallationId=Guid.NewGuid().ToString("N")},state with{DataId=Guid.NewGuid().ToString("N")},state with{Port=47124}})DenyCode("LAN_APP_CONFIGURATION_CHANGED",()=>LanWebConfiguration.Require(before,other));
            DenyCode("LAN_APP_CONFIGURATION_CHANGED",()=>LanWebConfiguration.Require(new([1,2,3]),state));
            var journal=new LanWebJournal(1,state.InstallationId,before,after);
            foreach(var current in new[]{before.Installed,after.Installed})LanWebConfiguration.RequireRecoverable(journal,state,_=>current);
            DenyCode("LAN_JOURNAL_CHANGED",()=>LanWebConfiguration.RequireRecoverable(journal,state,_=>InstalledLaunch.Encode(new(1,state.InstallationId,state.DataId,state.Port,"https://192.168.4.6"))));
            DenyCode("LAN_JOURNAL_CHANGED",()=>LanWebConfiguration.RequireRecoverable(journal with{InstallationId=Guid.NewGuid().ToString("N")},state,_=>before.Installed));
            return Task.CompletedTask;
        });
        await check("local loopback bootstrap requires its exact immutable full-profile installation anchor",()=>
        {
            var id=Guid.NewGuid().ToString("N");
            byte[] Anchor(string? full)=>Json(new{Format=1,InstallationId=id,RuntimeService=InstalledUpdaterLayout.RuntimeServiceName,AuthorityService=InstalledUpdaterLayout.AuthorityServiceName,IntakePolicyDigest=new string('a',64),IntakePinDigest=new string('b',64),ProbeSha256=new string('c',64),FullSystemPolicyDigest=full});
            Assert(InstalledUpdaterLayout.AllowsLoopbackOrigin(Anchor(new string('d',64)),id));Assert(!InstalledUpdaterLayout.AllowsLoopbackOrigin(Anchor(null),id));
            Deny(()=>InstalledUpdaterLayout.AllowsLoopbackOrigin(Anchor(new string('d',64)),Guid.NewGuid().ToString("N")));Deny(()=>InstalledUpdaterLayout.AllowsLoopbackOrigin(Anchor("bad"),id));return Task.CompletedTask;
        });
        await check("state-only preserved installation never displays a fresh updater consent choice",()=>
        {
            var observed=new List<string>();Assert(MainWindow.HasExistingUpdateInstallation(path=>{observed.Add(path);return path==InstallLocations.State;}));Assert(observed.SequenceEqual(new[]{InstallLocations.State}));
            Assert(!MainWindow.HasExistingUpdateInstallation(_=>false));Assert(MainWindow.HasExistingUpdateInstallation(path=>path==InstallLocations.RemovalStateFile));return Task.CompletedTask;
        });
        await check("installer partial cleanup recognizes only exact generated record names with canonical nonces",()=>
        {
            Assert(FullInstallerResidue.Metadata.SequenceEqual(new[]{"installer-state.json","installed.json","lan-preparation.json","setup-capability.json","master-key"}));
            foreach(var name in FullInstallerResidue.Metadata)
            {
                Assert(FullInstallerResidue.IsOwnedName(name+".partial-"+new string('a',32)));
                foreach(var suffix in new[]{"../data",new string('A',32),Guid.NewGuid().ToString("D"),""})Assert(!FullInstallerResidue.IsOwnedName(name+".partial-"+suffix));
            }
            foreach(var other in new[]{"activation.json","pending.json","epoch.json","bootstrap.json","process.lock"})Assert(!FullInstallerResidue.IsOwnedName(other+".partial-"+new string('a',32)));return Task.CompletedTask;
        });
        await check("install diagnostic records contain fixed stages and numeric failure evidence without private exception text",()=>
        {
            var failure=new SetupOperationException(UninstallPhase.FullStart,new IOException("private-token fake-secret-path C:\\private\\data"));
            var bytes=FullInstallDiagnostics.Encode(new string('a',32),failure,123);var text=global::System.Text.Encoding.UTF8.GetString(bytes);using var json=JsonDocument.Parse(bytes);
            Assert(!text.Contains("private",StringComparison.OrdinalIgnoreCase)&&!text.Contains("secret",StringComparison.OrdinalIgnoreCase)&&!text.Contains("StackTrace",StringComparison.Ordinal));
            Assert(json.RootElement.GetProperty("Stage").GetString()=="FullStart"&&json.RootElement.GetProperty("Code").GetString()=="INSTALL_FAILED");return Task.CompletedTask;
        });
        await check("installer failure codes name legacy, upgrade and changed-target refusals",()=>
        {
            foreach(var code in new[]{"LEGACY_INSTALL_UNINSTALL_FIRST","INSTALL_UPGRADE_REQUIRED","INSTALL_DOWNGRADE_REFUSED","INSTALL_TARGET_CHANGED","PACKAGE_PAYLOAD_ANCHOR"})Assert(SetupOperation.Code(new IOException(code))==code);
            Assert(SetupOperation.Code(new IOException("UPDATE_LEGACY_PROFILE_REQUIRES_REMOVAL"))=="INSTALL_FAILED");
            Assert(SetupOperation.OperationTimeout("install")==TimeSpan.FromMinutes(48)&&SetupOperation.OperationTimeout("uninstall")==TimeSpan.FromMinutes(96));
            return Task.CompletedTask;
        });
    }
    private static InstallPackage LayoutPackage(int format=1,string? extra=null,string? omit=null,string? payloadDigest=null,string? source=null)
    {
        var names=new List<string>{"host/Yahad.ServiceHost.exe","host/Yahad.ServiceHost.runtimeconfig.json","payload/bundle.json","payload/runtime/node/node.exe","payload/runtime/ffmpeg/ffmpeg.exe","payload/runtime/caddy/caddy.exe","payload/app/server/main.mjs","payload/app/dist/index.html","payload/app/native/Yahad.Platform.exe","payload/legal/fixture.json"};
        if(extra is not null)names.Add(extra);if(omit is not null){names.Remove(omit);names.Add("payload/legal/other.json");}
        var stream=new MemoryStream();using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))foreach(var name in names){using var entry=zip.CreateEntry(name).Open();entry.WriteByte(1);}
        var bytes=stream.ToArray();stream.Position=0;var sha=Hash([1]);
        try{return new(stream,Json(new InstallPackageManifest(format,ApplicationRelease.Version,source??ApplicationRelease.SourceCommit,Hash(bytes),payloadDigest??sha,names.Select(n=>new InstallMember(n,1,sha)).ToArray())));}
        catch{stream.Dispose();throw;}
    }
}

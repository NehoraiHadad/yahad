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
    // A plain private target: the production target's protection needs elevation.
    internal static readonly ExtractTarget TestTarget=new(path=>Directory.CreateDirectory(path),path=>new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None),
        (partial,path)=>File.Move(partial,path,false),(path,record)=>{using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return file.Length==record.Size&&Convert.ToHexStringLower(SHA256.HashData(file))==record.Sha256;});
    private static InstallerState State(string phase="committed")=>PairFixture.State(phase);
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("stop is sent only when the service state accepts it and a pending state is bounded by its own progress",()=>
        {
            // START_PENDING without STOP is what refused removal with 1052.
            Assert(!NativeService.SendsStop(2,0,false));Assert(NativeService.SendsStop(2,1,false));
            Assert(NativeService.SendsStop(4,5,false));Assert(!NativeService.SendsStop(4,5,true));
            Assert(!NativeService.SendsStop(3,1,false));Assert(!NativeService.SendsStop(1,1,false));
            Assert(!NativeService.Stalled(300000,TimeSpan.FromMinutes(4)));Assert(NativeService.Stalled(300000,TimeSpan.FromMinutes(6)));
            Assert(!NativeService.Stalled(0,TimeSpan.FromSeconds(59)));Assert(NativeService.Stalled(0,TimeSpan.FromSeconds(61)));
            // A sent STOP moving RUNNING to STOP_PENDING is what refused removal as a generation change.
            Assert(NativeService.SameGeneration(42,3,42));Assert(NativeService.SameGeneration(42,4,42));
            Assert(!NativeService.SameGeneration(42,4,43));Assert(!NativeService.SameGeneration(42,2,42));
            NativeService.RequireStopped(1,0);Deny(()=>NativeService.RequireStopped(1,12));Deny(()=>NativeService.RequireStopped(3,0));
            return Task.CompletedTask;
        });
        await check("the Runtime service restarts after 5 s, 30 s and 2 min over one day and removal clears every action",()=>
        {
            var plan=NativeService.FailurePlan(true);
            Assert(plan.Length==3&&plan.All(a=>a.Kind==1)&&plan.Select(a=>a.Delay).SequenceEqual(new uint[]{5000,30000,120000})&&NativeService.FailureReset==86400);
            NativeService.RequireFailurePlan(true,86400,plan);NativeService.RequireFailurePlan(false,0,[]);
            Deny(()=>NativeService.RequireFailurePlan(true,0,plan));Deny(()=>NativeService.RequireFailurePlan(true,86400,plan[..2]));
            Deny(()=>NativeService.RequireFailurePlan(true,86400,[plan[0],plan[1],new(){Kind=0,Delay=120000}]));
            Deny(()=>NativeService.RequireFailurePlan(true,86400,[plan[0],plan[1],new(){Kind=1,Delay=60000}]));
            Deny(()=>NativeService.RequireFailurePlan(false,0,plan));Deny(()=>NativeService.RequireFailurePlan(false,86400,[]));
            Assert(NativeService.AutoStart==2&&NativeService.DemandStart==3&&NativeService.Disabled==4);
            return Task.CompletedTask;
        });
        await check("the one service binds its own virtual account and grants the installer and Runtime query only",()=>
        {
            Assert(ServiceDefinition.Name=="Yahad.Acceptance"&&ServiceDefinition.Account==@"NT SERVICE\Yahad.Acceptance");
            // The installer grants files to the service SID before CreateService: it is SHA-1 of the upper-case name.
            Assert(InstallLocations.ServiceSidFor("TrustedInstaller")=="S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
            Assert(InstallLocations.ServiceSidFor("yahad.acceptance")==InstallLocations.ServiceSid&&InstallLocations.ServiceSid.StartsWith("S-1-5-80-",StringComparison.Ordinal));
            var exact="O:BA"+NativeService.ServiceDescriptor(Owner,InstallLocations.ServiceSid);var acl=new RawSecurityDescriptor(exact);Assert(acl.DiscretionaryAcl!.Count==4);
            Assert(acl.DiscretionaryAcl.Cast<CommonAce>().Where(a=>a.SecurityIdentifier.Value is not("S-1-5-18" or "S-1-5-32-544")).All(a=>a.AccessMask==5));
            Assert(new RawSecurityDescriptor("O:BA"+NativeService.ServiceDescriptor(Owner,null)).DiscretionaryAcl!.Count==3);
            Assert(InstallLocations.ServiceCommand("1.2.3")=="\""+Path.Combine(InstallLocations.Binaries,"infra","1.2.3","host","Yahad.ServiceHost.exe")+"\" --service");
            foreach(var bad in new[]{"1.2","01.2.3","1.2.3-beta","..","1.2.3\\..\\x"})Deny(()=>InstallLocations.ServiceCommand(bad));
            return Task.CompletedTask;
        });
        await check("interrupted service provisioning admits only the same never-started demand registration",()=>
        {
            NativeService.RequireProvisioningState(3,1,0,1077,0);
            foreach(var value in new(uint,uint,uint,uint,uint)[]{(2,1,0,1077,0),(4,1,0,1077,0),(3,4,123,0,0),(3,1,0,0,0),(3,1,0,1066,1),(3,1,123,1077,0)})Deny(()=>NativeService.RequireProvisioningState(value.Item1,value.Item2,value.Item3,value.Item4,value.Item5));
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
        await check("installer state admits only Format 7 of this one service, its own first owner and a valid pair",()=>
        {
            var state=State();state.Require();foreach(var phase in InstallerState.Phases)(state with{Phase=phase}).Require();
            Assert(InstallerState.Phases.Contains("provisioned"));
            var replay=InstallerState.Parse(state.Encode());Assert(Json(replay).SequenceEqual(Json(state)));
            var active=state.Active;var infra=active.Infrastructure;var app=active.App;
            InstalledPair WithInfra(Func<InfrastructureRecord,InfrastructureRecord> change)=>active with{Infrastructure=change(infra)};
            InstalledPair WithApp(Func<AppRecord,AppRecord> change)=>active with{App=change(app)};
            foreach(var bad in new[]{state with{Format=6},state with{Phase="restoring"},state with{ServiceSid="S-1-5-80-1-2-3-4-5"},state with{OwnerSid="S-1-5-18"},state with{Port=80},
                state with{InstallationId="x"},state with{Active=null!},state with{ReleaseKeySha256="x"},
                state with{FirstOwner=state.FirstOwner with{installationId=Guid.NewGuid().ToString("N")}},state with{Consent=state.Consent with{Accepted=false}},
                state with{Active=WithInfra(i=>i with{Version="1.2"})},state with{Active=WithInfra(i=>i with{SourceCommit=new string('g',40)})},state with{Active=WithInfra(i=>i with{EngineSha256=new string('e',64)})},
                state with{Active=WithInfra(i=>i with{TreeSha256=new string('e',64)})},state with{Active=WithInfra(i=>i with{ContractMin=2,ContractMax=1})},
                state with{Active=WithInfra(i=>i with{Files=i.Files.Where(f=>f.Path!=InstalledLayout.InfraTreeName).ToArray()})},
                state with{Active=WithInfra(i=>i with{Files=[..i.Files,i.Files[0] with{Path=i.Files[0].Path.ToUpperInvariant()}]})},
                state with{Active=WithInfra(i=>i with{Files=[..i.Files,new("bootstrap/authority/Yahad.Authority.exe",1,new string('d',64))]})},
                state with{Active=WithInfra(i=>i with{Files=[..i.Files,new("app/server/main.mjs",1,new string('d',64))]})},
                state with{Active=WithApp(a=>a with{Files=[..a.Files,new("app/native/Yahad.Platform.exe",1,new string('d',64))]})},
                state with{Active=WithApp(a=>a with{Files=[..a.Files,new("host/Yahad.ServiceHost.exe",1,new string('d',64))]})},
                state with{Active=WithApp(a=>a with{RequiresContract=infra.ContractMax+1})},state with{Active=WithApp(a=>a with{DatabaseSchema=0})},
                state with{Active=WithApp(a=>a with{TreeSha256=new string('e',64)})},state with{Active=WithApp(a=>a with{SourceCommit="abc"})},
                // A retained pair is older on both tracks, a shared version is the very same tree, and it is never the active pair.
                state with{Previous=active},state with{Previous=PairFixture.Pair(app:"1.0.1")},state with{Previous=PairFixture.Pair(infra:"1.0.1",app:"0.9.0")},
                state with{Previous=PairFixture.Pair(app:"0.9.0",infraSalt:"other")},state with{Previous=PairFixture.Pair(infra:"0.9.0",appSalt:"other")}})Deny(bad.Require);
            (state with{Previous=PairFixture.Pair(app:"0.9.0")}).Require();(state with{Previous=PairFixture.Pair(infra:"0.9.0")}).Require();
            return Task.CompletedTask;
        });
        await check("a record, receipt or upgrade record of another format is a legacy layout, refused before anything reads it",()=>
        {
            foreach(var format in new[]{1,5,6,8})
            {
                var legacy=JsonSerializer.SerializeToUtf8Bytes(new{Format=format,Version="0.6.0",Files=Array.Empty<object>()});
                DenyCode("LEGACY_INSTALL_UNSUPPORTED",()=>InstallerState.Parse(legacy));DenyCode("LEGACY_INSTALL_UNSUPPORTED",()=>RemovalIntent.Parse(legacy));
            }
            foreach(var format in new[]{1,3})DenyCode("LEGACY_INSTALL_UNSUPPORTED",()=>UpgradeState.Parse(JsonSerializer.SerializeToUtf8Bytes(new{Format=format,From="1.0.0",To="1.0.1"})));
            foreach(var damaged in new[]{"","[]","{}","{\"Format\":\"7\"}","{\"Format\":7.5}","not json"})
            {var bytes=System.Text.Encoding.UTF8.GetBytes(damaged);DenyCode("INSTALL_STATE_INVALID",()=>InstallerState.Parse(bytes));DenyCode("UNINSTALL_INTENT_INVALID",()=>RemovalIntent.Parse(bytes));}
            var state=State();var text=System.Text.Encoding.UTF8.GetString(state.Encode());
            DenyCode("INSTALL_STATE_INVALID",()=>InstallerState.Parse(System.Text.Encoding.UTF8.GetBytes(text[..^1]+",\"Version\":\"0.6.0\"}")));
            return Task.CompletedTask;
        });
        await check("removal intent retains owner, exact binaries and choice beyond partial root deletion",()=>
        {
            var state=State();var inventory=RemovalIntent.Inventory(state);var infra=state.Active.Infrastructure;var app=state.Active.App;
            var pointer=state.Active.Pointer().Encode();
            Assert(inventory.Length==1+infra.Files.Length+app.Files.Length&&inventory[0]==new InstallMember(InstalledLayout.ActivePointerName,pointer.Length,TreeRecord.Sha256(pointer)));
            Assert(inventory.Count(f=>f.Path==RemovalIntent.EngineMember(infra.Version)&&f.Sha256==infra.EngineSha256)==1);
            Assert(inventory.Skip(1).All(f=>f.Path.StartsWith("infra/"+infra.Version+"/",StringComparison.Ordinal)||f.Path.StartsWith("apps/"+app.Version+"/",StringComparison.Ordinal)));
            Assert(inventory.Any(f=>f.Path=="infra/"+infra.Version+"/"+InstalledLayout.InfraTreeName)&&inventory.Any(f=>f.Path=="apps/"+app.Version+"/"+InstalledLayout.AppTreeName));
            var intent=new RemovalIntent(InstallerState.CurrentFormat,state with{Phase="removing_all_data"},UninstallMode.DeleteData,"prepared");
            // The receipt's inventory is the one its record implies, never a second copy.
            Assert(intent.Binaries.SequenceEqual(inventory)&&!JsonDocument.Parse(intent.Encode()).RootElement.TryGetProperty("Binaries",out _));
            DenyCode("UNINSTALL_INTENT_INVALID",()=>RemovalIntent.Parse(JsonSerializer.SerializeToUtf8Bytes(new{intent.Format,intent.State,intent.Mode,Binaries=inventory,intent.Stage})));
            foreach(var stage in RemovalIntent.Stages){var replay=RemovalIntent.Parse(Json(intent with{Stage=stage}));replay.RequireSameInstallation(state);Assert(replay.AtLeast(stage)&&replay.AtLeast("prepared"));}
            Assert(!intent.AtLeast("lan_removed"));Deny(()=>intent.AtLeast("invented"));
            (intent with{Mode=UninstallMode.KeepData,State=state with{Phase="removing_keep_data"}}).Require();
            Deny(()=>(intent with{Mode=UninstallMode.KeepData}).Require());Deny(()=>(intent with{Stage="invented"}).Require());Deny(()=>(intent with{Format=6}).Require());
            Deny(()=>intent.RequireSameInstallation(state with{InstallationId=Guid.NewGuid().ToString("N")}));Deny(()=>intent.RequireSameInstallation(state with{Active=PairFixture.Pair(appSalt:"other")}));
            intent.RequireSameInstallation(state with{Phase="uninstalled"});return Task.CompletedTask;
        });
        await check("every record two maximal pairs make is readable: lists are bounded at admission, records at their writer, readers at the derived bound",()=>
        {
            // About 30,000 members each: two such pairs make a record past 16 MiB that a 16 MiB
            // reader refused after the commit had changed the install.
            var names=Enumerable.Range(0,30000).Select(i=>"app/dist/assets/"+i.ToString("D6")+"-"+new string('x',200)+".js").Concat(PairFixture.AppMembers).ToArray();
            var infra=PairFixture.Infra();
            InstalledPair Big(string version)=>new(PairFixture.InfraRecord(infra),PairFixture.AppRecordOf(PairFixture.Build(TreeKind.App,version,PairFixture.AppCommit,names),6));
            var state=PairFixture.State(active:Big("1.0.1"),previous:Big("1.0.0"));var bytes=state.Encode();
            Assert(bytes.Length>TreeRecord.MaximumBytes&&bytes.Length<=InstallerState.MaximumBytes);
            var receipt=new RemovalIntent(InstallerState.CurrentFormat,state with{Phase="removing_keep_data"},UninstallMode.KeepData,"prepared");
            var upgrade=UpgradeState.Begin(UpgradeState.AppKind,state.Previous!,state.Active,1);
            var root=Path.Combine(Path.GetTempPath(),"yahad-record-bound-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                string Write(string name,byte[] content){var path=Path.Combine(root,name);File.WriteAllBytes(path,content);return path;}
                Assert(InstallerState.Read(Write("installer-state.json",bytes)).Active.App.Files.Length==names.Length+1);
                Assert(RemovalIntent.Read(Write("receipt.json",receipt.Encode())).Binaries.Length==receipt.Binaries.Length);
                Assert(UpgradeState.Parse(NativeInstallFiles.ReadRecord(Write("upgrade-state.json",upgrade.Encode()),UpgradeState.MaximumBytes)).To.App.Version=="1.0.1");
            }
            finally{Directory.Delete(root,true);}
            Assert(InstallerState.MaximumBytes==4*InstallerState.TreeListMaximumBytes+InstallerState.FixedMaximumBytes&&UpgradeState.MaximumBytes==InstallerState.MaximumBytes&&
                RemovalIntent.MaximumBytes==InstallerState.MaximumBytes+InstallerState.FixedMaximumBytes);
            // A list past its bound never reaches a record: a name System.Text.Json escapes
            // ('+' is six bytes) can pass the tree record's own bound and not this one.
            var escaped=Enumerable.Range(0,13000).Select(i=>"app/dist/"+i.ToString("D6")+new string('+',200)).Concat(PairFixture.AppMembers).ToArray();
            var wide=PairFixture.Build(TreeKind.App,"1.0.2",PairFixture.AppCommit,escaped);
            Assert(wide.Record.Length<=TreeRecord.MaximumBytes&&!InstallerState.WithinListBound(wide.Files));
            DenyCode("INSTALL_STATE_INVALID",()=>(state with{Previous=null,Active=new(PairFixture.InfraRecord(infra),PairFixture.AppRecordOf(wide,6))}).Encode());
            {var fixture=PairFixture.Package(app:wide);DenyCode("PACKAGE_BOUND",()=>{using var p=PairFixture.Open(fixture);});}
            return Task.CompletedTask;
        });
        await check("install package accepts only Format 2 of this infrastructure with exact infra/ and apps/ trees",()=>
        {
            using(var exact=PairFixture.Open(PairFixture.Package()))Assert(exact.Manifest.Format==2&&exact.Members(TreeKind.App).All(f=>f.Path==InstalledLayout.AppTreeName||f.Path.StartsWith("app/",StringComparison.Ordinal)));
            void Refused(string code,Func<(byte[] Zip,byte[] Manifest,byte[] Engine)> fixture){var bytes=fixture();DenyCode(code,()=>{using var p=PairFixture.Open(bytes);});}
            Refused("PACKAGE_FORMAT",()=>PairFixture.Package(change:m=>m with{Format=1}));
            Refused("PACKAGE_FORMAT",()=>PairFixture.Package(change:m=>m with{Infrastructure=null!}));
            Refused("PACKAGE_FORMAT",()=>PairFixture.Package(change:m=>m with{App=m.App with{Version="1.0"}}));
            Refused("PACKAGE_FORMAT",()=>PairFixture.Package(change:m=>m with{App=m.App with{SourceCommit="abc"}}));
            Refused("PACKAGE_FORMAT",()=>PairFixture.Package(schema:0,migrations:[]));
            foreach(var migrations in new[]{new[]{6,5},[5,5],[7],[0,6],[8]})Refused("PACKAGE_FORMAT",()=>PairFixture.Package(migrations:migrations));
            foreach(var extra in new[]{"infra/bootstrap/authority/Yahad.Authority.exe","system-manifest.json","payload/app/server/main.mjs","host/Yahad.ServiceHost.exe",
                "apps/host/x","infra/app/x"})Refused("PACKAGE_PATH",()=>PairFixture.Package(extra:extra));
            foreach(var required in InstallPackage.Required)Refused("PACKAGE_MISSING",()=>PairFixture.Package(omit:required));
            foreach(var record in new[]{"infra/"+InstalledLayout.InfraTreeName,"apps/"+InstalledLayout.AppTreeName})Refused("PACKAGE_MISSING",()=>PairFixture.Package(omit:record));
            Refused("PACKAGE_SOURCE",()=>PairFixture.Package(change:m=>m with{Infrastructure=m.Infrastructure with{ContractMax=m.Infrastructure.ContractMax+1}}));
            Refused("PACKAGE_CONTRACT",()=>PairFixture.Package(change:m=>m with{App=m.App with{RequiresContract=InfrastructureRelease.ContractMax+1}}));
            // Each tree record is the archive's own member and lists exactly the other members of its namespace.
            Refused("PACKAGE_TREE_MISMATCH",()=>PairFixture.Package(change:m=>m with{App=m.App with{TreeSha256=new string('e',64)}}));
            Refused("PACKAGE_TREE_MISMATCH",()=>PairFixture.Package(change:m=>m with{Infrastructure=m.Infrastructure with{TreeSha256=new string('e',64)}}));
            Refused("PACKAGE_TREE_MISMATCH",()=>PairFixture.Package(extra:"apps/app/extra.js"));Refused("PACKAGE_TREE_MISMATCH",()=>PairFixture.Package(extra:"infra/legal/other.json"));
            Refused("PACKAGE_TREE_MISMATCH",()=>PairFixture.Package(omit:"infra/legal/fixture.json"));
            Refused("PACKAGE_HASH",()=>PairFixture.Package(change:m=>m with{ZipSha256=new string('0',64)}));
            Refused("PACKAGE_MEMBERS",()=>PairFixture.Package(duplicate:true));
            Refused("PACKAGE_BOUND",()=>PairFixture.Package(change:m=>m with{Files=[..m.Files,m.Files[0]]}));
            // The writer's required members (setup_package.py REQUIRED_MEMBERS), the app's own legal records included.
            foreach(var legal in new[]{"server-bundle.json","ThirdPartyNotices-server.txt","build-inputs.json","SBOM.json","Yahad-LICENSE.txt"})Assert(InstallPackage.Required.Contains("apps/app/legal/"+legal));
            Assert(InstallPackage.Required.Where(r=>r.StartsWith("apps/",StringComparison.Ordinal)).SequenceEqual(AppMemberPolicy.Required.Select(r=>"apps/"+r)));
            // The bundle ships, never the source, the lockfile, node_modules, native code or an
            // executable, in any case: the app archive's own policy (AppMemberPolicy).
            foreach(var extra in new[]{"apps/app/src/main.ts","apps/app/node_modules/x/index.js","apps/app/package-lock.json","apps/app/SRC/main.ts","apps/app/Node_Modules/x.js","apps/app/Package-Lock.json",
                "apps/app/native/Yahad.Platform.exe","apps/app/Native/coreclr.dll","apps/app/dist/plugin.node","apps/app/dist/a.exe","apps/app/a.DLL","apps/app/tools/a.ps1","apps/app/a.cmd","apps/app/a.Bat"})
                Refused("PACKAGE_FORBIDDEN",()=>PairFixture.Package(extra:extra));
            // One archive entry per manifest path, exactly: a case-only difference is no match,
            // and two manifest paths that are one Windows path are refused.
            Refused("PACKAGE_MEMBER_TYPE",()=>PairFixture.Package(change:m=>m with{Files=m.Files.Select(f=>f.Path=="infra/legal/fixture.json"?f with{Path="infra/legal/FIXTURE.json"}:f).ToArray()}));
            Refused("PACKAGE_BOUND",()=>PairFixture.Package(change:m=>m with{Files=[..m.Files,m.Files.Single(f=>f.Path=="infra/legal/fixture.json") with{Path="infra/legal/FIXTURE.json"}]}));
            return Task.CompletedTask;
        });
        await check("the Setup image is the engine followed by its package, and the engine member is the image's own prefix",()=>
        {
            var fixture=PairFixture.Package();var image=PairFixture.Image(fixture);var session=PrivateSession.Create(Path.GetTempPath());var path=Path.Combine(session,"Yahad.Setup.exe");
            void Write(byte[] bytes)=>File.WriteAllBytes(path,bytes);
            void ImageRefused(string code,byte[] bytes){Write(bytes);DenyCode(code,()=>{using var p=InstallPackage.FromImage(path);});}
            static byte[] Length(byte[] bytes,int field,long delta)
            {
                var copy=bytes.ToArray();var at=copy.Length-SetupImage.TrailerLength+field;
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(copy.AsSpan(at),System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(copy.AsSpan(at))+delta);return copy;
            }
            try
            {
                Write(image);
                using(var package=InstallPackage.FromImage(path))
                {
                    Assert(package.Manifest.App.Version=="1.0.0");var root=Path.Combine(session,"tree");
                    package.Extract(TreeKind.Infrastructure,root,_=>{},default,TestTarget);
                    Assert(File.ReadAllBytes(Path.Combine(root,"setup","Yahad.Setup.exe")).SequenceEqual(fixture.Engine));
                }
                // The archive never carries the engine a second time.
                using(var archive=new ZipArchive(new MemoryStream(fixture.Zip)))Assert(archive.GetEntry(InstallPackage.EnginePath) is null);
                // The bare engine (no trailer) carries no package.
                ImageRefused("PACKAGE_NOT_EMBEDDED",fixture.Engine);ImageRefused("PACKAGE_NOT_EMBEDDED",[..image,0]);
                // A trailer whose lengths do not add up to the file is refused, never trimmed.
                ImageRefused("PACKAGE_IMAGE",[..image[..(image.Length-SetupImage.TrailerLength-1)],..image[^SetupImage.TrailerLength..]]);
                ImageRefused("PACKAGE_IMAGE",[0,..image]);
                foreach(var field in new[]{16,24,32})ImageRefused("PACKAGE_IMAGE",Length(image,field,1));
                ImageRefused("PACKAGE_IMAGE",Length(Length(image,16,-fixture.Engine.Length),24,fixture.Engine.Length));
                // A boundary moved between two parts breaks the archive's digest.
                ImageRefused("PACKAGE_HASH",Length(Length(image,16,1),24,-1));
                // A changed prefix is not the engine the manifest and the tree record name.
                {var changed=image.ToArray();changed[fixture.Engine.Length/2]^=1;ImageRefused("PACKAGE_ENGINE",changed);}
                DenyCode("PACKAGE_ENGINE",()=>{using var p=PairFixture.Open(PairFixture.Package(tamper:InstallPackage.EnginePath));});
                DenyCode("PACKAGE_ENGINE",()=>{using var p=PairFixture.Open(fixture with{Engine=[..fixture.Engine,0]});});
                // An archive that also carries the engine entry is not this package.
                {
                    using var data=new MemoryStream();data.Write(fixture.Zip);
                    using(var archive=new ZipArchive(data,ZipArchiveMode.Update,true))using(var entry=archive.CreateEntry(InstallPackage.EnginePath).Open())entry.Write(fixture.Engine);
                    var zip=data.ToArray();var manifest=JsonSerializer.Deserialize<InstallPackageManifest>(fixture.Manifest)! with{ZipSha256=Hash(zip)};
                    DenyCode("PACKAGE_MEMBERS",()=>{using var p=PairFixture.Open((zip,JsonSerializer.SerializeToUtf8Bytes(manifest),fixture.Engine));});
                }
            }
            finally{Directory.Delete(session,true);}
            return Task.CompletedTask;
        });
        await check("a tree record refuses a member that is also a directory, and one directory spelled two ways",()=>
        {
            byte[] Record(params string[] names)=>PairFixture.Build(TreeKind.App,"1.0.0",PairFixture.AppCommit,names).Record;
            _=TreeRecord.Parse(Record("app/a/b","app/a/c","app/b"),TreeKind.App);
            foreach(var names in new[]{new[]{"app/a","app/a/b"},["app/A","app/a/b"],["app/x/A","app/x/a/b"],["app/A/x","app/a/y"],["app/d/E/x","app/d/e/y"]})
                DenyCode("TREE_RECORD_INVALID",()=>TreeRecord.Parse(Record(names),TreeKind.App));
            return Task.CompletedTask;
        });
        await check("an interrupted extraction resumes only over this package's members and their owned partials",()=>
        {
            var expected=new[]{"active.json","infra/1.2.3/host/Yahad.ServiceHost.exe","apps/1.0.0/app/server/main.mjs"};var partial=".partial-"+Guid.NewGuid().ToString("N");
            var partials=InstallPackage.RequireExtractedInventory(expected,[expected[1],expected[2]+partial,"active.json"+partial],["infra","infra/1.2.3","infra/1.2.3/host","apps","apps/1.0.0","apps/1.0.0/app","apps/1.0.0/app/server"]);
            Assert(partials.Length==2&&partials.Contains(expected[2]+partial));
            foreach(var file in new[]{"infra/1.2.3/host/foreign.dll",expected[1]+".partial-"+Guid.NewGuid().ToString("D"),expected[1]+".partial-"+new string('A',32),"other.exe"+partial,expected[1]+".partial-"})
                DenyCode("INSTALL_TARGET_CHANGED",()=>InstallPackage.RequireExtractedInventory(expected,[file],[]));
            foreach(var directory in new[]{"infra/1.2.4","infra/1.2.3/host/x","versions","apps/1.0.1"})DenyCode("INSTALL_TARGET_CHANGED",()=>InstallPackage.RequireExtractedInventory(expected,[],[directory]));
            return Task.CompletedTask;
        });
        await check("extraction never rewrites a present member and leaves no partial when a member fails",()=>
        {
            using var package=PairFixture.Open(PairFixture.Package());var root=PrivateSession.Create(Path.GetTempPath());var other=PrivateSession.Create(Path.GetTempPath());
            string[] Extracted(string directory)=>Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories).Select(f=>Path.GetRelativePath(directory,f).Replace(Path.DirectorySeparatorChar,'/')).Where(f=>f!=".yahad-fixture").Order(StringComparer.Ordinal).ToArray();
            try
            {
                package.Extract(TreeKind.Infrastructure,root,_=>{},default,TestTarget);package.Extract(TreeKind.Infrastructure,root,_=>{},default,TestTarget);
                Assert(Extracted(root).SequenceEqual(package.Members(TreeKind.Infrastructure).Select(f=>f.Path).Order(StringComparer.Ordinal)));
                var node=Path.Combine(root,"runtime","node","node.exe");var bytes=File.ReadAllBytes(node);bytes[0]^=1;File.WriteAllBytes(node,bytes);
                DenyCode("INSTALL_TARGET_CHANGED",()=>package.Extract(TreeKind.Infrastructure,root,_=>{},default,TestTarget));Assert(File.ReadAllBytes(node).SequenceEqual(bytes));
                var failing=TestTarget with{Commit=(_,_)=>throw new IOException("COMMIT_CUT")};
                DenyCode("COMMIT_CUT",()=>package.Extract(TreeKind.App,other,_=>{},default,failing));Assert(Extracted(other).Length==0);
            }
            finally{Directory.Delete(root,true);Directory.Delete(other,true);}
            return Task.CompletedTask;
        });
        await check("LAN proxy runs from the installed infrastructure's folder and its image follows that version",()=>
        {
            var version=Path.Combine(InstallLocations.Binaries,"infra","1.2.3");
            Assert(ServiceSpec.Lan.Command("1.2.3")=="\""+Path.Combine(version,"host","Yahad.ServiceHost.exe")+"\" --lan-service");
            Assert(ServiceSpec.Lan.Command("1.2.4")!=ServiceSpec.Lan.Command("1.2.3"));
            Assert(LanProxyPaths.Caddy("1.2.3")==Path.Combine(version,"runtime","caddy","caddy.exe"));
            // The Runtime's YAHAD_PLATFORM_HELPER: infrastructure beside the host and Updater, never under the app.
            Assert(InstalledLayout.PlatformImage("1.2.3")==Path.Combine(version,"platform","Yahad.Platform.exe")&&Path.IsPathFullyQualified(InstalledLayout.PlatformImage("1.2.3")));
            DenyCode("PAYLOAD_VERSION",()=>ServiceSpec.Lan.Command("../other"));
            return Task.CompletedTask;
        });
        await check("LAN proxy's account reads the binary tree only: the state root does not name it",()=>
        {
            var proxy=InstallLocations.ServiceSidFor(LanProxyPaths.ServiceName);
            Assert(NativeSetupHost.BinaryAcl.Contains($"(A;OICI;0x1200a9;;;{proxy})",StringComparison.Ordinal));
            Assert(!NativeInstallFiles.InstallAcl(InstallLocations.ServiceSid).Contains(proxy,StringComparison.Ordinal));
            Assert(!NativeInstallFiles.DataAcl(InstallLocations.ServiceSid).Contains(proxy,StringComparison.Ordinal));
            return Task.CompletedTask;
        });
        await check("the image Apps & Features starts is readable by an unelevated user, and the state root stays closed",()=>
        {
            Assert(SetupOperation.Within(InstalledLayout.EngineImage("1.2.3"),InstallLocations.Binaries)&&InstalledLayout.EngineImage("1.2.3")==Path.Combine(InstallLocations.Binaries,"infra","1.2.3","setup","Yahad.Setup.exe"));
            Assert(NativeSetupHost.BinaryAcl.EndsWith("(A;OICI;0x1200a9;;;BU)",StringComparison.Ordinal));
            Assert(NativeSetupHost.BinaryAcl==NativeInstallFiles.InstallAcl(InstallLocations.ServiceSid,InstallLocations.ServiceSidFor(LanProxyPaths.ServiceName),"BU"));
            Assert(!NativeInstallFiles.InstallAcl(InstallLocations.ServiceSid).Contains(";BU)",StringComparison.Ordinal));
            Assert(!NativeInstallFiles.DataAcl(InstallLocations.ServiceSid).Contains(";BU)",StringComparison.Ordinal));
            return Task.CompletedTask;
        });
        await check("installer partial cleanup recognizes only exact generated record names with canonical nonces",()=>
        {
            Assert(FullInstallerResidue.Metadata.SequenceEqual(new[]{"installer-state.json","upgrade-state.json","installed.json","lan-preparation.json","setup-capability.json","master-key"}));
            foreach(var name in FullInstallerResidue.Metadata)
            {
                Assert(FullInstallerResidue.IsOwnedName(name+".partial-"+new string('a',32)));
                foreach(var suffix in new[]{"../data",new string('A',32),Guid.NewGuid().ToString("D"),""})Assert(!FullInstallerResidue.IsOwnedName(name+".partial-"+suffix));
            }
            foreach(var other in new[]{"activation.json","pending.json","epoch.json","bootstrap.json","process.lock"})Assert(!FullInstallerResidue.IsOwnedName(other+".partial-"+new string('a',32)));return Task.CompletedTask;
        });
        await check("installer failure codes name upgrade and changed-target refusals",()=>
        {
            foreach(var code in new[]{"INSTALL_UPGRADE_REQUIRED","INSTALL_DOWNGRADE_REFUSED","INSTALL_TARGET_CHANGED","LEGACY_INSTALL_UNSUPPORTED","PACKAGE_TREE_MISMATCH","PACKAGE_CONTRACT","PACKAGE_MISSING","PACKAGE_MEMBERS","PACKAGE_PATH",
                "INFRA_IDENTITY_MISMATCH","APP_IDENTITY_MISMATCH","ACTIVE_POINTER_INVALID","TREE_RECORD_INVALID","RELEASE_UNSUPPORTED_FROM"})Assert(SetupOperation.Code(new IOException(code))==code);
            foreach(var gone in new[]{"UPDATE_LEGACY_PROFILE_REQUIRES_REMOVAL","PACKAGE_PAYLOAD_ANCHOR","REPAIR_RUNNING_FROM_TARGET","REPAIR_RELAUNCHED",RepairHost.HandOff})Assert(SetupOperation.Code(new IOException(gone))=="INSTALL_FAILED");
            Assert(SetupOperation.OperationTimeout("install")==TimeSpan.FromMinutes(48)&&SetupOperation.OperationTimeout("uninstall")==TimeSpan.FromMinutes(96));
            return Task.CompletedTask;
        });
    }
}

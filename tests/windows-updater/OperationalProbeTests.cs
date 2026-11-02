using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;

internal static class OperationalProbeTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("operational probe uses verified baseline only when selection is genuinely absent",()=>
        {var f=new Fixture(false);var selected=f.Read();Assert(selected.Executable==f.Baseline&&selected.BootstrapEpoch==0&&f.BaselineReads==1);return Task.CompletedTask;});
        await check("full operational probe updates independently of the original pinned baseline",()=>
        {var f=new Fixture();File.WriteAllText(f.Baseline,"unused old baseline bytes");var selected=f.Read();Assert(selected.Executable==f.Candidate&&selected.SystemManifestDigest==f.ManifestDigest&&selected.BootstrapEpoch==2&&f.BaselineReads==0);return Task.CompletedTask;});
        foreach(var field in new[]{"policy","installation","epoch","source","manifest"})await check("operational selection rejects changed "+field+" without baseline fallback",()=>
        {
            var f=new Fixture();f.Selection=field switch
            {"policy"=>f.Selection with{PolicyDigest=new string('d',64)},"installation"=>f.Selection with{InstallationId=Guid.NewGuid().ToString("N")},"epoch"=>f.Selection with{Epoch=0},"source"=>f.Selection with{SourceCommit=new string('b',40)},_=>f.Selection with{OperationalProbeDigest=new string('c',64)}};
            f.Write();Reject(()=>f.Read());Assert(f.BaselineReads==0);return Task.CompletedTask;
        });
        await check("malformed present probe selection never silently chooses legacy",()=>
        {var f=new Fixture();File.WriteAllText(f.State.PathOf("system-bootstrap-current.json"),"{}");Reject(()=>f.Read());Assert(f.BaselineReads==0);return Task.CompletedTask;});
        await check("operational probe rejects unlisted shadow DLL and changed member bytes",()=>
        {
            var extra=new Fixture();File.WriteAllText(Path.Combine(Path.GetDirectoryName(extra.Candidate)!,"shadow.dll"),"unlisted");Reject(()=>extra.Read());
            var changed=new Fixture();File.WriteAllText(changed.Candidate,"modified candidate");Reject(()=>changed.Read());Assert(extra.BaselineReads==0&&changed.BaselineReads==0);return Task.CompletedTask;
        });
        await check("operational probe requires full consent pin and respects a minimum observed bootstrap epoch",()=>
        {var f=new Fixture();Reject(()=>InstalledOperationalProbe.Require(f.State,f.Binaries,f.Installation,null,f.Baseline,f.BaselineDigest));Reject(()=>InstalledOperationalProbe.Require(f.State,f.Binaries,f.Installation,f.Policy,f.Baseline,f.BaselineDigest,3));return Task.CompletedTask;});
        await check("operational probe authenticates outer manifest bytes before opening candidate",()=>
        {var f=new Fixture();File.WriteAllText(f.ManifestPath,"changed outer manifest");Reject(()=>f.Read());Assert(f.BaselineReads==0);return Task.CompletedTask;});
    }
    private static void Assert(bool value){if(!value)throw new Exception("OPERATIONAL_PROBE_ASSERTION");}
    private static void Reject(Action action)
    {try{action();}catch(Exception e)when(e is IOException or JsonException or InvalidOperationException){return;}throw new Exception("OPERATIONAL_PROBE_UNEXPECTED_SUCCESS");}
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class Fixture
    {
        internal InstalledStateFiles State{get;}internal InstalledStateFiles Binaries{get;}
        internal string Installation=Guid.NewGuid().ToString("N"),Policy=new('e',64);
        internal string Baseline{get;}internal string BaselineDigest{get;}internal string Candidate{get;}internal string ManifestDigest{get;}internal string ManifestPath{get;}
        internal SystemBootstrapSnapshot Selection;internal int BaselineReads;
        internal Fixture(bool publish=true)
        {
            var root=SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-operational-state"));var binary=SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-operational-binary"));
            Baseline=Path.Combine(binary,"bootstrap","probe","node","node.exe");var baseline=Encoding.UTF8.GetBytes("synthetic baseline");Put(Baseline,baseline);BaselineDigest=Hash(baseline);
            State=InstalledStateFiles.Private(root);Binaries=InstalledStateFiles.Private(binary,projection:(path,role)=>{if(path==Baseline)BaselineReads++;});
            var components=Enum.GetValues<SystemComponentKind>().Select(kind=>
            {
                var entry=FixedSystemComponents.EntryPoint(kind);var bytes=Encoding.UTF8.GetBytes("synthetic:"+kind+":0.6.1");
                return new SystemBootstrapComponentState(kind,entry,Hash(bytes),[new(entry,bytes.Length,Hash(bytes))]);
            }).ToArray();
            // Selector fixture only. Source/PE/build-identity validation belongs
            // to the independent intake tests and is not claimed by these bytes.
            var manifest=JsonSerializer.SerializeToUtf8Bytes(new{format=2,kind="windows-system-candidate",profile="full-system-v1",target="win-x64",version="0.6.1",sourceCommit=new string('a',40),
                components=components.Select(c=>new{kind=c.Kind.ToString(),prefix=FixedSystemComponents.SlotPrefix(c.Kind),entryPoint=c.EntryPoint,entrySha256=c.EntrySha256,files=c.Files.Select(f=>new{path=f.RelativePath,size=f.Size,sha256=f.Sha256})})});
            ManifestDigest=Hash(manifest);var slot=Path.Combine(binary,"system-slots",ManifestDigest);ManifestPath=Path.Combine(slot,"system-manifest.json");Put(ManifestPath,manifest);
            foreach(var component in components)foreach(var file in component.Files)Put(Path.Combine(slot,FixedSystemComponents.SlotPrefix(component.Kind),file.RelativePath.Replace('/',Path.DirectorySeparatorChar)),Encoding.UTF8.GetBytes("synthetic:"+component.Kind+":0.6.1"));
            Candidate=Path.GetFullPath(Path.Combine(slot,FixedSystemComponents.SlotPrefix(SystemComponentKind.Probe),FixedSystemComponents.EntryPoint(SystemComponentKind.Probe)));
            Selection=new(1,Installation,2,"0.6.1",ManifestDigest,ManifestDigest,components,Policy,new string('a',40));if(publish)Write();
        }
        internal void Write()=>File.WriteAllBytes(State.PathOf("system-bootstrap-current.json"),JsonSerializer.SerializeToUtf8Bytes(Selection));
        internal OperationalProbeSelection Read()=>InstalledOperationalProbe.Require(State,Binaries,Installation,Policy,Baseline,BaselineDigest);
        private static void Put(string path,byte[] bytes){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);}
    }
}

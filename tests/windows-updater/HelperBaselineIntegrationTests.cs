using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;

internal static class HelperBaselineIntegrationTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("CommitCurrent validates successive A/B helper generations without a legacy base copy",async()=>
        {
            var f=new Fixture();
            foreach(var version in new[]{"0.6.1","0.6.2"})
            {
                var op=f.Operation(version);f.Publish(op);
                var helper=op.Components.Single(c=>c.Kind==SystemComponentKind.Helper);
                var components=op.Components.Select(c=>new SystemBootstrapComponentState(c.Kind,c.EntryPoint,c.EntrySha256,
                    c.Files.Select(x=>new SystemBootstrapFileState(x.RelativePath,x.Size,x.Sha256)).ToArray())).ToArray();
                Assert(SystemBootstrapNative.FixedActiveComponents(components).Select(c=>c.Kind).Order().SequenceEqual(
                    new[]{SystemComponentKind.Authority,SystemComponentKind.RuntimeHost,SystemComponentKind.Owner}.Order()));
                Assert(!Directory.Exists(Path.Combine(f.Root,"bootstrap","handoff")));
                f.Observed.Clear();await SystemBootstrapNative.RequirePublishedHelpersAsync(f.Files,op,f.Verify,default);
                Assert(f.Observed.Order().SequenceEqual(new[]{RecoveryHelperSlot.A,RecoveryHelperSlot.B}.Order()));
                Assert(helper.EntrySha256==f.Selection.Slots[0].Closure.EntrySha256);
            }
        });
        await check("A/B rollback survivor stays ready but cannot commit the wrong helper generation",async()=>
        {
            var f=new Fixture();var previous=f.Operation("0.6.1");f.Publish(previous);
            var next=f.Operation("0.6.2");var wanted=Fixture.Closure(next);
            f.WriteSlot(RecoveryHelperSlot.B,next.ToVersion);
            f.Save(f.Selection with{Slots=[f.Selection.Slots[0],new(RecoveryHelperSlot.B,RecoveryHelperSlotPhase.Verified,wanted)]});
            var before=new SystemBootstrapSnapshot(1,f.Installation,1,previous.ToVersion,previous.ManifestDigest,null,[]);
            f.Observed.Clear();await new SystemBootstrapRecoverySlots(f.Files,new ReadinessKernel(f),_=>{}).RequireReadyAsync(next,before,default);
            Assert(f.Observed.Contains(RecoveryHelperSlot.A)&&f.Observed.Contains(RecoveryHelperSlot.B));
            await Denied(()=>SystemBootstrapNative.RequirePublishedHelpersAsync(f.Files,next,f.Verify,default),"SYSTEM_BOOTSTRAP_HELPER_SELECTION_INCOMPLETE");
        });
        await check("CommitCurrent rejects pending or misbound A/B helper selections before native closure observation",async()=>
        {
            var f=new Fixture();var op=f.Operation("0.6.1");f.Publish(op);var good=f.Selection;
            var variants=new[]{
                good with{InstallationId=Guid.NewGuid().ToString("N")},good with{PolicyDigest=new string('9',64)},good with{Epoch=0},
                good with{PendingOperationId=op.OperationId,PendingManifestDigest=op.ManifestDigest,GrantId=op.GrantId,GrantBindingDigest=op.GrantBindingDigest},
                good with{Slots=[good.Slots[0],good.Slots[1] with{Closure=good.Slots[1].Closure with{Version="0.6.0"}}]},
                good with{Slots=[good.Slots[0],good.Slots[1] with{Closure=good.Slots[1].Closure with{ManifestDigest=new string('8',64)}}]}
            };
            foreach(var value in variants)
            {
                f.Save(value);f.Observed.Clear();await Denied(()=>SystemBootstrapNative.RequirePublishedHelpersAsync(f.Files,op,f.Verify,default));
                Assert(f.Observed.Count==0);
            }
        });
        await check("CommitCurrent observes both real synthetic slot inventories and detects a changing selection",async()=>
        {
            var f=new Fixture();var op=f.Operation("0.6.1");f.Publish(op);
            File.WriteAllText(f.Entry(RecoveryHelperSlot.B),"corrupted synthetic helper");
            await Denied(()=>SystemBootstrapNative.RequirePublishedHelpersAsync(f.Files,op,f.Verify,default),"SYNTHETIC_HELPER_CLOSURE");
            f.Publish(op);var changed=false;
            await Denied(()=>SystemBootstrapNative.RequirePublishedHelpersAsync(f.Files,op,async(slot,closure,cancellation)=>
            {
                await f.Verify(slot,closure,cancellation);
                if(!changed){changed=true;f.Save(f.Selection with{Epoch=f.Selection.Epoch+1});}
            },default),"SYSTEM_BOOTSTRAP_HELPER_SELECTION_CHANGED");
        });
        await check("CommitCurrent helper verification preserves cancellation",async()=>
        {
            var f=new Fixture();var op=f.Operation("0.6.1");f.Publish(op);using var canceled=new CancellationTokenSource();canceled.Cancel();
            try{await SystemBootstrapNative.RequirePublishedHelpersAsync(f.Files,op,f.Verify,canceled.Token);}
            catch(OperationCanceledException failure)when(failure.CancellationToken==canceled.Token){Assert(f.Observed.Count==0);return;}
            throw new Exception("HELPER_BASELINE_CANCELLATION_LOST");
        });
    }
    private static void Assert(bool value){if(!value)throw new Exception("HELPER_BASELINE_ASSERTION");}
    private static async Task Denied(Func<Task> action,string? code=null)
    {try{await action();}catch(IOException failure)when(code is null||failure.Message==code){return;}throw new Exception("HELPER_BASELINE_UNEXPECTED_SUCCESS");}
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    // Actual private files and protected selection parsing; no native service,
    // token, PE admission or production ACL success is manufactured here.
    private sealed class Fixture
    {
        internal readonly string Root=SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-helper-baseline"));
        internal readonly string Installation=Guid.NewGuid().ToString("N");
        internal readonly string Policy=new('c',64);
        internal InstalledStateFiles Files{get;}
        internal RecoveryHelperSlotsSelection Selection=null!;
        internal readonly List<RecoveryHelperSlot> Observed=[];
        internal Fixture(){Files=InstalledStateFiles.Private(Root);}
        private static byte[] Bytes(string version,SystemComponentKind kind)=>Encoding.UTF8.GetBytes("synthetic "+kind+" "+version);
        internal VerifiedSystemOperation Operation(string version)
        {
            var components=Enum.GetValues<SystemComponentKind>().Select(kind=>
            {
                var bytes=Bytes(version,kind);var entry=FixedSystemComponents.EntryPoint(kind);var sha=Hash(bytes);
                return new SystemComponentDescriptor(kind,FixedSystemComponents.SlotPrefix(kind),entry,sha,[new(entry,bytes.Length,sha)]);
            });
            return VerifiedSystemOperation.CreateAuthorized(Installation,Guid.NewGuid().ToString("N"),Hash(Encoding.UTF8.GetBytes(version)),
                new string('a',64),Policy,Guid.NewGuid().ToString("N"),new string('b',64),"0.6.0",version,1,new string('d',64),6,6,new string('e',40),components);
        }
        internal static RecoveryHelperClosure Closure(VerifiedSystemOperation op)
        {var c=op.Components.Single(c=>c.Kind==SystemComponentKind.Helper);return new(op.ManifestDigest,op.ToVersion,c.EntrySha256,c.Files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray());}
        internal string Entry(RecoveryHelperSlot slot)=>Path.Combine(Root,slot.ToString(),FixedSystemComponents.EntryPoint(SystemComponentKind.Helper));
        internal void WriteSlot(RecoveryHelperSlot slot,string version)
        {Directory.CreateDirectory(Path.GetDirectoryName(Entry(slot))!);File.WriteAllBytes(Entry(slot),Bytes(version,SystemComponentKind.Helper));}
        internal void Publish(VerifiedSystemOperation op)
        {
            var closure=Closure(op);foreach(var slot in Enum.GetValues<RecoveryHelperSlot>())WriteSlot(slot,op.ToVersion);
            Save(new(1,Installation,Policy,(Selection?.Epoch??0)+1,RecoveryHelperSlot.A,
                Enum.GetValues<RecoveryHelperSlot>().Select(slot=>new RecoveryHelperSlotState(slot,RecoveryHelperSlotPhase.Verified,closure)).ToArray()));
        }
        internal void Save(RecoveryHelperSlotsSelection value)
        {Selection=value;File.WriteAllBytes(Files.PathOf("bootstrap-helper-selection.json"),JsonSerializer.SerializeToUtf8Bytes(value));}
        internal Task Verify(RecoveryHelperSlot slot,RecoveryHelperClosure closure,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();Observed.Add(slot);var folder=Path.GetDirectoryName(Entry(slot))!;
            var actual=Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            if(!actual.SequenceEqual(closure.Files.Select(f=>f.RelativePath).Order(StringComparer.Ordinal)))throw new IOException("SYNTHETIC_HELPER_CLOSURE");
            foreach(var file in closure.Files)
            {var bytes=File.ReadAllBytes(Path.Combine(folder,file.RelativePath));if(bytes.LongLength!=file.Size||Hash(bytes)!=file.Sha256)throw new IOException("SYNTHETIC_HELPER_CLOSURE");}
            return Task.CompletedTask;
        }
    }
    private sealed class ReadinessKernel(Fixture fixture):IRecoveryHelperSlotKernel
    {
        public bool IsExecuting(RecoveryHelperSlot slot)=>false;
        public void RequireProvisioned(){}
        public Task RequireClosureAsync(RecoveryHelperSlot slot,RecoveryHelperClosure closure,CancellationToken cancellation)=>fixture.Verify(slot,closure,cancellation);
        public Task PublishAsync(VerifiedSystemOperation op,RecoveryHelperSlot slot,RecoveryHelperClosure wanted,IReadOnlyList<RecoveryHelperClosure> prior,bool rollback,CancellationToken cancellation)=>throw new NotSupportedException();
        public Task<RecoveryHelperHealth> ObserveHealthyAsync(VerifiedSystemOperation op,RecoveryHelperSlot slot,RecoveryHelperClosure closure,string nonce,CancellationToken cancellation)=>throw new NotSupportedException();
    }
}

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Service;

internal sealed record ProbationRuntimeBinding(InstallationBinding Binding,string OperationId,string ReleaseDigest,string DataId,string Nonce)
{
    internal void Validate()
    {InstalledActivationResolver.Id(Binding.InstallationId);InstalledActivationResolver.Digest(Binding.ActivationReceiptDigest);InstalledActivationResolver.Id(OperationId);InstalledActivationResolver.Digest(ReleaseDigest);InstalledActivationResolver.Id(DataId);InstalledActivationResolver.Id(Nonce);}
}
internal static class InstalledProbationDescriptor
{
    internal static string Relative(string nonce)
    {InstalledActivationResolver.Id(nonce);return Path.Combine("runtime-staging",nonce,"probation.json");}
    internal static ProbationRuntimeBinding Read(InstalledStateFiles files,RuntimeAdmissionBinding admission)
    {
        admission.Validate();var value=NativeJson.Read<ProbationRuntimeBinding>(files.Read(files.PathOf(Relative(admission.Nonce)),8192));value.Validate();
        var activation=NativeJson.Read<InstalledActivation>(files.Read(files.PathOf("activation.json"),32768));
        if(value.Binding!=admission.Binding||value.Nonce!=admission.Nonce||value.ReleaseDigest!=admission.ReleaseDigest||
            activation.InstallationId!=value.Binding.InstallationId||activation.DataId!=value.DataId||activation.ReleaseDigest!=value.ReleaseDigest)
            throw new UnauthorizedAccessException("PROBATION_DESCRIPTOR_BINDING");return value;
    }
}
internal interface IProbationEffectsRuntime
{Task ReleaseEffectsAsync(ProbationRuntimeBinding binding,string requestId,CancellationToken cancellation);}
internal interface IProbationEffectsJournal
{Task RecordIntentAsync(ProbationRuntimeBinding binding,string requestId,CancellationToken cancellation);}
internal enum ProbationEffectsPhase {Holding,RecordingIntent,Releasing,Released,OutcomeUnknown,FailedBeforeRelease}

// Used by the existing authority transaction around its runtime proxy. A lost
// acknowledgement cannot authorize old-data rollback or an automatic re-send.
internal sealed class ProbationEffectsCoordinator(ProbationRuntimeBinding binding,IProbationEffectsJournal journal,IProbationEffectsRuntime runtime)
{
    private int issued;private ProbationEffectsPhase phase=ProbationEffectsPhase.Holding;
    internal ProbationEffectsPhase Phase=>phase;
    internal bool EffectsMayHaveBeenReleased=>phase is ProbationEffectsPhase.Releasing or ProbationEffectsPhase.Released or ProbationEffectsPhase.OutcomeUnknown;
    internal async Task ReleaseAsync(CancellationToken cancellation=default)
    {
        binding.Validate();if(Interlocked.Exchange(ref issued,1)!=0)throw new IOException("PROBATION_RELEASE_REPLAY");var id=Guid.NewGuid().ToString("N");
        try{phase=ProbationEffectsPhase.RecordingIntent;await journal.RecordIntentAsync(binding,id,cancellation);}
        catch{phase=ProbationEffectsPhase.FailedBeforeRelease;throw;}
        // The durable intent is written BEFORE any command can enable provider
        // handoff or HTTP writes. Recovery conservatively treats that intent as
        // possibly released even if this process had not yet sent the command.
        phase=ProbationEffectsPhase.Releasing;
        try{await runtime.ReleaseEffectsAsync(binding,id,cancellation);phase=ProbationEffectsPhase.Released;}
        catch{phase=ProbationEffectsPhase.OutcomeUnknown;throw;}
    }
}

internal interface IProbationAppSession:IContainedAppSession
{Task ReleaseEffectsAsync(ProbationRuntimeBinding binding,string id,CancellationToken cancellation);}

// Concrete child-control purpose of the existing launcher, job and host lease.
// Its three extra environment fields only contract execution into probation;
// they never grant release. ServiceProcessLauncher must allow these exact keys
// when the full-policy composition selects this factory.
internal sealed class ProbationProcessLauncher:IPrivateAppLauncher
{
    private readonly ProbationRuntimeBinding binding;private readonly ServiceProcessLauncher inner;private readonly ProbationChildControls controls;
    internal ProbationProcessLauncher(VerifiedProcessSpec source,ProbationRuntimeBinding binding)
    {
        binding.Validate();this.binding=binding;
        var environment=new Dictionary<string,string>(source.AppEnvironment,StringComparer.Ordinal)
        {
            ["YAHAD_NATIVE_PROBATION_NONCE"]=binding.Nonce,["YAHAD_NATIVE_PROBATION_ACTIVATION"]=binding.Binding.ActivationReceiptDigest,
            ["YAHAD_NATIVE_PROBATION_OPERATION"]=binding.OperationId
        };
        controls=new(binding);inner=new(source with{AppEnvironment=environment},controls);
    }
    public async Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation)
    {
        if(release.Id!=binding.ReleaseDigest)throw new IOException("PROBATION_RELEASE_BINDING");
        var session=await inner.StartAsync(release,cancellation);
        return new Session(session,controls.Current??throw new IOException("PROBATION_CONTROL_UNAVAILABLE"),binding);
    }
    public Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)
        =>inner.WaitForHealthyAsync(((Session)session).Inner,cancellation);
    private sealed class Session(IContainedAppSession inner,ProbationChildControls.Channel channel,ProbationRuntimeBinding expected):IProbationAppSession,IUpdateIdleSession
    {
        internal IContainedAppSession Inner=>inner;
        public Task ReleaseEffectsAsync(ProbationRuntimeBinding binding,string id,CancellationToken cancellation)
        {if(binding!=expected)throw new UnauthorizedAccessException("PROBATION_RELEASE_BINDING");return channel.ReleaseAsync(id,cancellation);}
        public Task<bool> PrepareUpdateAsync(string id,CancellationToken cancellation)=>channel.PrepareUpdateAsync(id,cancellation);
        public Task CancelUpdateAsync(string id,CancellationToken cancellation)=>channel.CancelUpdateAsync(id,cancellation);
        public Task RequestGracefulStopAsync(CancellationToken cancellation)=>inner.RequestGracefulStopAsync(cancellation);
        public Task<int> WaitForRootExitAsync(CancellationToken cancellation)=>inner.WaitForRootExitAsync(cancellation);
        public Task WaitForTreeEmptyAsync(CancellationToken cancellation)=>inner.WaitForTreeEmptyAsync(cancellation);
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation)=>inner.TerminateAndWaitForTreeEmptyAsync(cancellation);
        public ValueTask DisposeAsync()=>inner.DisposeAsync();
    }
}
internal static class ProbationRuntimeWire
{
    internal static byte[] Release(ProbationRuntimeBinding binding,string id)
    {binding.Validate();InstalledActivationResolver.Id(id);return JsonSerializer.SerializeToUtf8Bytes(new{type="release-effects",id,nonce=binding.Nonce,activationReceiptDigest=binding.Binding.ActivationReceiptDigest,operationId=binding.OperationId});}
    internal static void Require(byte[] bytes,ProbationRuntimeBinding binding,string type,string? id=null)
    {
        binding.Validate();if(bytes.Length is <2 or >1024)throw new IOException("PROBATION_FRAME_BOUND");
        var json=NativeJson.Read<JsonElement>(bytes);
        if(json.ValueKind!=JsonValueKind.Object||json.EnumerateObject().Count()!=(id is null?4:5)||json.GetProperty("type").GetString()!=type||
            json.GetProperty("nonce").GetString()!=binding.Nonce||json.GetProperty("activationReceiptDigest").GetString()!=binding.Binding.ActivationReceiptDigest||
            json.GetProperty("operationId").GetString()!=binding.OperationId||id is not null&&json.GetProperty("id").GetString()!=id)
            throw new UnauthorizedAccessException("PROBATION_FRAME_BINDING");
    }
}
internal sealed class ProbationChildControls(ProbationRuntimeBinding binding):IChildControlFactory
{
    internal Channel? Current{get;private set;}
    public IChildControl Create()
    {if(Current is not null)throw new IOException("PROBATION_CONTROL_REPLAY");return Current=new(binding);}
    internal sealed class Channel: IChildControl
    {
        private readonly ProbationRuntimeBinding binding;private readonly string name="Yahad."+Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream pipe;private readonly SemaphoreSlim serial=new(1,1);private bool ready,released,effectsAcknowledged;
        internal Channel(ProbationRuntimeBinding binding){this.binding=binding;pipe=LocalControlPipe.Create(name);}
        public string Endpoint=>@"\\.\pipe\"+name;
        public async Task<ChildReady> ReadyAsync(int expectedRootPid,CancellationToken cancellation)
        {
            await pipe.WaitForConnectionAsync(cancellation);
            if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var pid)||pid!=(uint)expectedRootPid)throw new UnauthorizedAccessException("PROBATION_CHILD_PEER");
            ProbationRuntimeWire.Require(await ReadAsync(cancellation),binding,"probation-ready");ready=true;return new(pid,"ready");
        }
        internal async Task ReleaseAsync(string id,CancellationToken cancellation)
        {
            InstalledActivationResolver.Id(id);await serial.WaitAsync(cancellation);
            try
            {
                if(!ready||released)throw new IOException("PROBATION_RELEASE_STATE");released=true;
                await pipe.WriteAsync(ProbationRuntimeWire.Release(binding,id),cancellation);await pipe.WriteAsync(new byte[]{10},cancellation);await pipe.FlushAsync(cancellation);
                // Finish the bounded response after write, even if the caller
                // cancels. A partially consumed frame is not a new boundary.
                using var response=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                ProbationRuntimeWire.Require(await ReadAsync(response.Token),binding,"effects-released",id);effectsAcknowledged=true;cancellation.ThrowIfCancellationRequested();
            }
            finally{serial.Release();}
        }
        public async Task StopAsync(CancellationToken cancellation)
        {await serial.WaitAsync(cancellation);try{await pipe.WriteAsync("{\"type\":\"stop\"}\n"u8.ToArray(),cancellation);await pipe.FlushAsync(cancellation);}finally{serial.Release();}}
        internal async Task<bool> PrepareUpdateAsync(string id,CancellationToken cancellation)
        {
            InstalledActivationResolver.Id(id);if(!effectsAcknowledged)return false;await serial.WaitAsync(cancellation);
            try
            {
                await SendUpdateAsync("prepare-update",id,cancellation);using var response=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var json=await UpdateReplyAsync(id,response.Token);var type=json.GetProperty("type").GetString();
                var idle=type switch{"update-idle"=>true,"update-busy"=>false,_=>throw new IOException("PROBATION_IDLE_REPLY")};cancellation.ThrowIfCancellationRequested();return idle;
            }
            finally{serial.Release();}
        }
        internal async Task CancelUpdateAsync(string id,CancellationToken cancellation)
        {
            InstalledActivationResolver.Id(id);if(!effectsAcknowledged)return;await serial.WaitAsync(cancellation);
            try
            {
                await SendUpdateAsync("cancel-update",id,cancellation);using var response=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var type=(await UpdateReplyAsync(id,response.Token)).GetProperty("type").GetString();
                if(type is "update-idle" or "update-busy")type=(await UpdateReplyAsync(id,response.Token)).GetProperty("type").GetString();
                if(type!="update-cancelled")throw new IOException("PROBATION_IDLE_CANCEL_UNCONFIRMED");
            }
            finally{serial.Release();}
        }
        private async Task SendUpdateAsync(string type,string id,CancellationToken cancellation)
        {await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new{type,id}),cancellation);await pipe.WriteAsync(new byte[]{10},cancellation);await pipe.FlushAsync(cancellation);}
        private async Task<JsonElement> UpdateReplyAsync(string id,CancellationToken cancellation)
        {
            var json=NativeJson.Read<JsonElement>(await ReadAsync(cancellation));
            if(json.ValueKind!=JsonValueKind.Object||json.EnumerateObject().Count()!=2||json.GetProperty("id").GetString()!=id)throw new UnauthorizedAccessException("PROBATION_IDLE_REPLY_BINDING");return json;
        }
        private async Task<byte[]> ReadAsync(CancellationToken cancellation)
        {
            using var bytes=new MemoryStream();var one=new byte[1];
            while(bytes.Length<1024){if(await pipe.ReadAsync(one,cancellation)!=1)throw new IOException("PROBATION_CHILD_EOF");if(one[0]==10)return bytes.ToArray();bytes.WriteByte(one[0]);}
            throw new IOException("PROBATION_FRAME_BOUND");
        }
        public void Dispose()=>pipe.Dispose();
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe,out uint pid);
    }
}

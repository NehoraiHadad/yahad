using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

/// <summary>What the name transaction asks of this computer: its resolver, a trusted HTTPS request and the two services. Tests replace it.</summary>
internal interface IHomeNameEnvironment
{
    /// <summary>The IPv4 addresses the system resolver returns for the name; empty when it returns none.</summary>
    Task<IReadOnlyList<string>> ResolveAsync(string name,CancellationToken cancellation);
    /// <summary>GET https://name/ with the system's trust; throws unless it succeeds.</summary>
    Task ProbeAsync(string name,CancellationToken cancellation);
    /// <summary>The confirmed stop of the proxy, a start, and the IP site answering as the app.</summary>
    Task RestartProxyAsync(CancellationToken cancellation);
    /// <summary>The Runtime stopped, started and healthy, so it reads home-name.json again.</summary>
    Task RestartRuntimeAsync(CancellationToken cancellation);
}
/// <summary>Where the name transaction keeps its state; production uses LanProxyPaths and InstallLocations.</summary>
internal sealed record HomeNamePaths(string Owner,string NameRecord,string Caddyfile,string Store);
internal sealed record HomeNameOutcome(string? Name,string? Failure);

/// <summary>
/// The name transaction (docs/HOME-NAME.md 3). Every step first persists the owner phase and is idempotent;
/// the Caddyfile may carry the name site before home-name.json exists, never the reverse. Inert in a build
/// without a configured name service.
/// </summary>
internal sealed class HomeNameSetup(IHomeNameRemote remote,IHomeNameEnvironment environment,HomeNamePaths paths,
    Func<LanHomeNameSite?,byte[]> caddyfile,Func<DateTimeOffset>? clock=null,TimeSpan? resolveBudget=null,TimeSpan? cleanupBudget=null)
{
    private HomeNameStore Store=>new(paths.Store);
    internal HomeNameOwner? ReadOwner()=>File.Exists(paths.Owner)?HomeNameOwnerRecord.Parse(NativeInstallFiles.ReadRecord(paths.Owner,4096)):null;
    private HomeNameOwner Save(string phase,string label,string? failure=null)
    {
        var owner=new HomeNameOwner(1,phase,label,label.Length==0?"":HomeNameRelease.NameFor(label),failure);
        NativeInstallFiles.Publish(paths.Owner,HomeNameOwnerRecord.Encode(owner));return owner;
    }
    private void WriteCaddyfile(string? name)=>NativeInstallFiles.Publish(paths.Caddyfile,caddyfile(name is null?null:Site(name)));
    internal LanHomeNameSite Site(string name)=>new(name,HomeNameStore.CertificateFile(Store.Live),HomeNameStore.KeyFile(Store.Live));
    private void RemoveNameRecord(){if(File.Exists(paths.NameRecord))File.Delete(paths.NameRecord);}
    /// <summary>The name the Caddyfile serves after an infrastructure move: only an active one.</summary>
    internal string? ActiveName()=>HomeNameRelease.Configured&&ReadOwner() is {Phase:HomeNameOwnerRecord.Active} owner?owner.Name:null;
    /// <summary>Whether a run without the owner's choice has a transaction to finish.</summary>
    internal bool Unfinished()=>HomeNameRelease.Configured&&ReadOwner()?.Phase is HomeNameOwnerRecord.Registered or HomeNameOwnerRecord.Issued or HomeNameOwnerRecord.Configured or HomeNameOwnerRecord.Failed;

    /// <summary>
    /// Before the IP path is configured again: the app goes back to the IP origin first. Label, credential and
    /// certificates stay, so the next transaction reuses them.
    /// </summary>
    internal void Suspend()
    {
        if(!HomeNameRelease.Configured)return;
        RemoveNameRecord();
        if(ReadOwner() is {Phase:HomeNameOwnerRecord.Configured or HomeNameOwnerRecord.Active} owner)Save(HomeNameOwnerRecord.Issued,owner.Label);
    }

    /// <summary>
    /// Registers, points and certifies the name for <paramref name="address"/>, serves it and checks it from this
    /// computer, then commits it with home-name.json. Any failure leaves the IP path as it was and returns its code;
    /// only a failure to get back there throws.
    /// </summary>
    internal async Task<HomeNameOutcome> RunAsync(string address,CancellationToken cancellation)
    {
        if(!HomeNameRelease.Configured)return new(null,null);
        var owner=ReadOwner();
        // Active means this very configuration was committed: nothing to do. A LAN change suspends it first.
        if(owner is {Phase:HomeNameOwnerRecord.Active})return new(owner.Name,null);
        RemoveNameRecord();WriteCaddyfile(null);
        string name;
        try{name=await TransactAsync(address,cancellation);}
        catch(Exception failure)when(failure is not OutOfMemoryException)
        {
            var code=Code(failure);
            try
            {
                using var cleanup=new CancellationTokenSource(cleanupBudget??TimeSpan.FromSeconds(45));
                RemoveNameRecord();WriteCaddyfile(null);
                string label;try{label=Store.ReadCredential()?.Label??"";}catch(IOException){label="";}
                Save(HomeNameOwnerRecord.Failed,label,code);
                await environment.RestartProxyAsync(cleanup.Token);
            }
            catch(Exception incomplete){throw SetupFailureDetail.Besides(failure,incomplete);}
            // The operation's own deadline or cancellation is not a name failure.
            cancellation.ThrowIfCancellationRequested();
            return new(null,code);
        }
        // Committed: a Runtime that does not come back is a failure of the Runtime, never undone as the name's.
        await environment.RestartRuntimeAsync(cancellation);
        return new(name,null);
    }
    private async Task<string> TransactAsync(string address,CancellationToken cancellation)
    {
        var store=Store;
        for(var attempt=0;;attempt++)
        {
            try
            {
                // The credential is on disk before anything else is asked of the name service.
                var credential=store.ReadCredential();
                if(credential is null){credential=await remote.RegisterAsync(cancellation);store.WriteCredential(credential);}
                var name=HomeNameRelease.NameFor(credential.Label);
                Save(HomeNameOwnerRecord.Registered,credential.Label);
                await remote.SetAddressAsync(credential,address,cancellation);
                if(!Current(store,name))
                {
                    using(var account=store.Account())store.Stage(await remote.IssueAsync(credential,name,account,cancellation));
                    if(store.Read(store.Next,name) is null)throw new IOException("HOME_NAME_STAGE_INCOMPLETE");
                    store.Swap();
                }
                Save(HomeNameOwnerRecord.Issued,credential.Label);
                WriteCaddyfile(name);Save(HomeNameOwnerRecord.Configured,credential.Label);
                await environment.RestartProxyAsync(cancellation);
                await ResolvesAsync(name,address,cancellation);
                await environment.ProbeAsync(name,cancellation);
                NativeInstallFiles.Publish(paths.NameRecord,InstalledHomeNameRecord.Encode(new(1,name,address)));
                Save(HomeNameOwnerRecord.Active,credential.Label);
                return name;
            }
            // 410: the label is gone at the name service. Its certificates name it, so they go with it; once.
            catch(HomeNameGoneException)when(attempt==0){store.Clear();}
        }
    }
    // A live certificate for this name that is not yet due is reused: a rerun does not ask the CA again.
    private bool Current(HomeNameStore store,string name)
    {
        try{return store.Read(store.Live,name) is {} live&&!AcmeCertificateCheck.RenewalDue(live.NotBefore,live.NotAfter,(clock??(()=>DateTimeOffset.UtcNow))());}
        catch(Exception error)when(error is IOException or System.Security.Cryptography.CryptographicException or ArgumentException){return false;}
    }
    // A router's DNS-rebind protection shows up here: the name resolves to nothing, or not to this address.
    private async Task ResolvesAsync(string name,string address,CancellationToken cancellation)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(resolveBudget??TimeSpan.FromSeconds(30));
        while(true)
        {
            try{if((await environment.ResolveAsync(name,deadline.Token)).Contains(address))return;}
            catch(SocketException){}
            catch(OperationCanceledException)when(deadline.IsCancellationRequested&&!cancellation.IsCancellationRequested){throw new IOException("HOME_NAME_RESOLVE_FAILED");}
            try{await Task.Delay(1000,deadline.Token);}
            catch(OperationCanceledException)when(!cancellation.IsCancellationRequested){throw new IOException("HOME_NAME_RESOLVE_FAILED");}
        }
    }

    /// <summary>
    /// The name is given up (LAN off, delete-data): the IP-only Caddyfile, no home-name.json, a best-effort
    /// DELETE at the name service, then the credential and certificates go. The ACME account key stays.
    /// </summary>
    internal async Task RemoveAsync(bool writeCaddyfile,CancellationToken cancellation)
    {
        if(!HomeNameRelease.Configured)return;
        RemoveNameRecord();
        if(writeCaddyfile&&File.Exists(paths.Caddyfile))WriteCaddyfile(null);
        var store=Store;HomeNameCredential? credential;
        try{credential=store.ReadCredential();}catch(IOException){credential=null;}
        if(credential is not null)
        {
            using var bound=CancellationTokenSource.CreateLinkedTokenSource(cancellation);bound.CancelAfter(TimeSpan.FromSeconds(10));
            // Offline or refused: the name service's own expiry removes the name.
            try{await remote.DeleteNameAsync(credential,bound.Token);}
            catch(Exception error)when(error is not OutOfMemoryException&&!cancellation.IsCancellationRequested){}
        }
        store.Clear();
        if(File.Exists(paths.Owner)||credential is not null)Save(HomeNameOwnerRecord.Removed,"");
    }

    internal static string Code(Exception failure)=>failure switch
    {
        HomeNameServiceException service when HomeNameOwnerRecord.IsFailureCode(service.Code)=>service.Code,
        AcmeException acme when HomeNameOwnerRecord.IsFailureCode(acme.Code)=>acme.Code,
        OperationCanceledException=>"HOME_NAME_TIMEOUT",
        IOException io when HomeNameOwnerRecord.IsFailureCode(io.Message)=>io.Message,
        _=>"HOME_NAME_FAILED"
    };
}

/// <summary>The production environment: the system resolver, system trust with no proxy, and the installed services.</summary>
internal sealed class NativeHomeNameEnvironment(Func<CancellationToken,Task> restartProxy,Func<CancellationToken,Task> restartRuntime):IHomeNameEnvironment
{
    public async Task<IReadOnlyList<string>> ResolveAsync(string name,CancellationToken cancellation)
        =>(await Dns.GetHostAddressesAsync(name,AddressFamily.InterNetwork,cancellation)).Select(a=>a.ToString()).ToArray();
    public async Task ProbeAsync(string name,CancellationToken cancellation)
    {
        using var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false};
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(10)};
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while(true)
        {
            try
            {
                using var response=await client.GetAsync("https://"+name+"/",HttpCompletionOption.ResponseHeadersRead,deadline.Token);
                if(response.IsSuccessStatusCode)return;
            }
            catch(HttpRequestException){}
            catch(TaskCanceledException)when(!deadline.IsCancellationRequested){}
            try{await Task.Delay(1000,deadline.Token);}
            catch(OperationCanceledException)when(!cancellation.IsCancellationRequested){throw new IOException("HOME_NAME_HTTPS_FAILED");}
        }
    }
    public Task RestartProxyAsync(CancellationToken cancellation)=>restartProxy(cancellation);
    public Task RestartRuntimeAsync(CancellationToken cancellation)=>restartRuntime(cancellation);
}

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
    /// <summary>
    /// Whether a run without the owner's choice has a transaction to finish. A credential with no owner phase
    /// after it (none, or removed) is a registration that stopped before its phase was saved.
    /// </summary>
    internal bool Unfinished()
    {
        if(!HomeNameRelease.Configured)return false;
        return ReadOwner()?.Phase switch
        {
            HomeNameOwnerRecord.Registered or HomeNameOwnerRecord.Issued or HomeNameOwnerRecord.Configured or HomeNameOwnerRecord.Failed=>true,
            null or HomeNameOwnerRecord.Removed=>File.Exists(Store.CredentialFile),
            _=>false
        };
    }

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
    /// <paramref name="desired"/>: the label the owner asked for in the window, or null (none asked, or a run without
    /// the owner's choice), which keeps the current label, or registers a random one when there is none. A label other
    /// than the current one is a rename: the new label is claimed, pointed and certified beside the active name, which
    /// stays committed and served meanwhile, so a name that is taken or cannot be prepared leaves the home exactly as it
    /// was. Only then is the name switched; the old label is deleted at the name service once the new one is committed.
    /// </summary>
    internal async Task<HomeNameOutcome> RunAsync(string address,string? desired,CancellationToken cancellation)
    {
        if(!HomeNameRelease.Configured)return new(null,null);
        if(desired is not null&&!HomeNameRelease.IsLabel(desired))throw new ArgumentException(HomeNameLabelException.Invalid);
        var store=Store;var owner=ReadOwner();
        // Active means this very configuration was committed (a LAN change suspends it first); it is kept
        // only while it still holds. Otherwise the transaction runs again, which re-registers a gone label.
        var check=owner is {Phase:HomeNameOwnerRecord.Active}?await CheckAsync(owner,address,cancellation):NameCheck.Redo;
        var current=CurrentLabel(store);var claim=Claim(store);
        // A claim credential.json already holds stopped past its commit point: it is the current label, finished below.
        var pending=claim is not null&&claim.Label==current;
        // A claim for another label than the one asked for now is given up, and retired so the name service deletes it.
        if(desired is not null&&claim is not null&&!pending&&claim.Label!=desired){store.Retire(claim);store.DiscardClaim();}
        var rename=desired is not null&&desired!=current;
        Exception? renameError=null;var prepared=false;
        if(check==NameCheck.Holds)
        {
            if(rename)
                try{await PrepareAsync(store,desired!,address,cancellation);prepared=true;}
                catch(Exception failure)when(failure is not OutOfMemoryException&&!cancellation.IsCancellationRequested){renameError=failure;}
            // Nothing to switch to: the active name stays exactly as it was.
            if(!prepared){await RetireAsync(store,cancellation);return new(owner!.Name,renameError is null?null:Code(renameError));}
        }
        // The Runtime may run on the name until it is restarted; a failure below sends it back to the IP origin.
        var named=File.Exists(paths.NameRecord);
        // The name is uncommitted before anything it serves from is touched.
        Suspend();RemoveNameRecord();WriteCaddyfile(null);
        string name;
        try
        {
            if(pending)store.Promote();
            // No active name to keep serving: the new label is prepared now. When it cannot be, the current label is
            // redone as a run without the owner's choice would; without one there is only the IP path.
            if(rename&&!prepared)
                try{await PrepareAsync(store,desired!,address,cancellation);prepared=true;}
                catch(Exception failure)when(current is not null&&failure is not OutOfMemoryException&&!cancellation.IsCancellationRequested){renameError=failure;}
            name=await TransactAsync(address,check==NameCheck.Gone,prepared,cancellation);
        }
        catch(Exception failure)when(failure is not OutOfMemoryException)
        {
            var code=Code(failure);
            Exception? incomplete=null;
            try
            {
                using var cleanup=new CancellationTokenSource(cleanupBudget??TimeSpan.FromSeconds(45));
                RemoveNameRecord();WriteCaddyfile(null);
                Save(HomeNameOwnerRecord.Failed,CurrentLabel(store)??"",code);
                await environment.RestartProxyAsync(cleanup.Token);
            }
            catch(Exception error){incomplete=error;}
            // Whatever the proxy did, a Runtime that ran on the name goes back to the IP origin, on a budget of its own.
            if(named)
                try{using var recovery=new CancellationTokenSource(cleanupBudget??TimeSpan.FromSeconds(45));await environment.RestartRuntimeAsync(recovery.Token);}
                catch(Exception error){incomplete=incomplete is null?error:new AggregateException(incomplete,error);}
            if(incomplete is not null)throw SetupFailureDetail.Besides(failure,incomplete);
            // The operation's own deadline or cancellation is not a name failure.
            cancellation.ThrowIfCancellationRequested();
            // The owner's request comes first: a taken name is what they can act on.
            return new(null,renameError is null?code:Code(renameError));
        }
        // Committed: a Runtime that does not come back is a failure of the Runtime, never undone as the name's.
        await environment.RestartRuntimeAsync(cancellation);
        // Only now is a label given up deleted at the name service.
        await RetireAsync(store,cancellation);
        return new(name,renameError is null?null:Code(renameError));
    }
    private async Task<string> TransactAsync(string address,bool gone,bool promote,CancellationToken cancellation)
    {
        var store=Store;
        // A label the name service called gone: its certificates name it, so they go with it.
        if(gone)store.Clear();
        // The prepared claim becomes the label: the current one is retired, the claim's certificate is live.
        if(promote)store.Promote();
        for(var attempt=0;;attempt++)
        {
            try
            {
                // The credential is on disk before anything else is asked of the name service. A label registered here
                // is random: a chosen one is always claimed (PrepareAsync) and promoted, never registered in place.
                var credential=store.ReadCredential();
                if(credential is null){credential=await remote.RegisterAsync(null,cancellation);store.WriteCredential(credential);}
                var name=HomeNameRelease.NameFor(credential.Label);
                Save(HomeNameOwnerRecord.Registered,credential.Label);
                await remote.SetAddressAsync(credential,address,cancellation);
                if(!Current(store,store.Live,name))
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
    /// <summary>
    /// Claims <paramref name="label"/> and points and certifies it, in rename.json and rename/, never touching what the
    /// active name serves from. Idempotent: a claim already on disk is reused, never registered again (the name service
    /// would answer label_taken), and so is its certificate while it is not due. A label this home gave up and has not
    /// deleted yet is taken back.
    /// </summary>
    private async Task PrepareAsync(HomeNameStore store,string label,string address,CancellationToken cancellation)
    {
        var name=HomeNameRelease.NameFor(label);
        for(var attempt=0;;attempt++)
        {
            try
            {
                var claim=store.ReadClaim();
                // Claimed before the retired copy goes, so RetireAsync never deletes it in between (it skips the claim).
                if(claim is null&&store.ReadRetired().FirstOrDefault(r=>r.Label==label) is {} back){store.WriteClaim(back);store.DiscardRetired(label);claim=back;}
                // The claim's credential is on disk before anything else is asked of the name service.
                if(claim is null){claim=await remote.RegisterAsync(label,cancellation);store.WriteClaim(claim);}
                if(claim.Label!=label)throw new IOException("HOME_NAME_CLAIM_MISMATCH");
                await remote.SetAddressAsync(claim,address,cancellation);
                if(!Current(store,store.Claimed,name))
                {
                    using(var account=store.Account())store.StageClaim(await remote.IssueAsync(claim,name,account,cancellation));
                    if(store.Read(store.Claimed,name) is null)throw new IOException("HOME_NAME_STAGE_INCOMPLETE");
                }
                return;
            }
            // 410: the claim expired at the name service. Registering the label again answers label_taken, since a
            // label never comes back; that is the outcome.
            catch(HomeNameGoneException)when(attempt==0){store.DiscardClaim();}
        }
    }
    /// <summary>
    /// Labels given up are deleted at the name service (best effort, each on a short budget) and then forgotten; one that
    /// cannot be deleted now is kept for the next run, and the service's own expiry is the backstop. Never the current
    /// label or the claim.
    /// </summary>
    private async Task RetireAsync(HomeNameStore store,CancellationToken cancellation)
    {
        var keep=new[]{CurrentLabel(store),Claim(store)?.Label};
        foreach(var credential in store.ReadRetired())
        {
            if(!keep.Contains(credential.Label))
            {
                using var bound=CancellationTokenSource.CreateLinkedTokenSource(cancellation);bound.CancelAfter(TimeSpan.FromSeconds(10));
                try{await remote.DeleteNameAsync(credential,bound.Token);}
                catch(Exception error)when(error is not OutOfMemoryException&&!cancellation.IsCancellationRequested){continue;}
            }
            store.DiscardRetired(credential.Label);
        }
    }
    // The label credential.json holds; null without one, or with one that cannot be read (the transaction then fails on it).
    private static string? CurrentLabel(HomeNameStore store)
    {
        try{return store.ReadCredential()?.Label;}
        catch(IOException){return null;}
    }
    // A claim that cannot be read cannot be used: it goes, and the name service's expiry removes its label.
    private static HomeNameCredential? Claim(HomeNameStore store)
    {
        try{return store.ReadClaim();}
        catch(IOException){store.DiscardClaim();return null;}
    }
    private enum NameCheck{Holds,Redo,Gone}
    /// <summary>
    /// An active name still holds when its credential and a valid live certificate are here and the name service
    /// still knows the label (the unchanged address is a no-op there that also refreshes it). Only the service's
    /// 410 says the label is gone; a service that cannot be reached says nothing about it.
    /// </summary>
    private async Task<NameCheck> CheckAsync(HomeNameOwner owner,string address,CancellationToken cancellation)
    {
        if(!File.Exists(paths.NameRecord))return NameCheck.Redo;
        var store=Store;HomeNameCredential? credential;
        try
        {
            credential=store.ReadCredential();
            if(credential is null||credential.Label!=owner.Label||store.Read(store.Live,owner.Name) is not {} live||(clock??(()=>DateTimeOffset.UtcNow))()>=live.NotAfter)return NameCheck.Redo;
        }
        catch(Exception error)when(error is IOException or System.Security.Cryptography.CryptographicException or ArgumentException){return NameCheck.Redo;}
        try{await remote.SetAddressAsync(credential,address,cancellation);return NameCheck.Holds;}
        // Gone: the transaction discards its credential and certificates, after the name is uncommitted.
        catch(HomeNameGoneException){return NameCheck.Gone;}
        catch(Exception error)when(error is not OutOfMemoryException&&!cancellation.IsCancellationRequested){return NameCheck.Holds;}
    }
    // A certificate for this name in directory (live/, or rename/ for a claim) that is not yet due is reused: a rerun does not ask the CA again.
    private bool Current(HomeNameStore store,string directory,string name)
    {
        try{return store.Read(directory,name) is {} found&&!AcmeCertificateCheck.RenewalDue(found.NotBefore,found.NotAfter,(clock??(()=>DateTimeOffset.UtcNow))());}
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
    /// DELETE at the name service of every label this home holds (the current one, a claim, the retired ones), then
    /// the credentials and certificates go. The ACME account key stays.
    /// </summary>
    internal async Task RemoveAsync(bool writeCaddyfile,CancellationToken cancellation)
    {
        if(!HomeNameRelease.Configured)return;
        RemoveNameRecord();
        if(writeCaddyfile&&File.Exists(paths.Caddyfile))WriteCaddyfile(null);
        var store=Store;HomeNameCredential? credential;
        try{credential=store.ReadCredential();}catch(IOException){credential=null;}
        var held=new List<HomeNameCredential>();
        if(credential is not null)held.Add(credential);
        if(Claim(store) is {} claim)held.Add(claim);
        held.AddRange(store.ReadRetired());
        foreach(var label in held.DistinctBy(c=>c.Label))
        {
            using var bound=CancellationTokenSource.CreateLinkedTokenSource(cancellation);bound.CancelAfter(TimeSpan.FromSeconds(10));
            // Offline or refused: the name service's own expiry removes the name.
            try{await remote.DeleteNameAsync(label,bound.Token);}
            catch(Exception error)when(error is not OutOfMemoryException&&!cancellation.IsCancellationRequested){}
        }
        store.ClearAll();
        if(File.Exists(paths.Owner)||held.Count!=0)Save(HomeNameOwnerRecord.Removed,"");
    }

    /// <summary>The label rule (<see cref="HomeNameLabel"/>) in the owner's words.</summary>
    internal const string LabelRule="שם לכתובת הוא 3 עד 20 תווים: אותיות קטנות באנגלית, ספרות ומקף. הוא מתחיל ומסתיים באות או בספרה, בלי שני מקפים ברצף, ואינו שם שמור כמו www.";
    /// <summary>
    /// The owner's words for a name failure. <paramref name="name"/>: the name the home kept (a rename that did not
    /// happen), or null when it works at the IP origin.
    /// </summary>
    internal static string FailureText(string code,string? name)
    {
        var what=code switch
        {
            HomeNameLabelException.Taken=>"השם כבר תפוס. אפשר לבחור שם אחר ולהריץ את ההתקנה שוב.",
            HomeNameLabelException.Invalid=>"השם לא תקין. "+LabelRule,
            _=>name is null?"הכתובת הקבועה לא הוגדרה ("+code+"). הפעלה חוזרת של ההתקנה תנסה שוב.":"השם החדש לא הוגדר ("+code+"). הפעלה חוזרת של ההתקנה תנסה שוב."
        };
        return what+(name is null?" יחד פועלת בכתובת לפי מספר.":" הכתובת הקבועה הקודמת נשארה פעילה.");
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

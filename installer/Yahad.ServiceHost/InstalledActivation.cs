using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;

namespace Yahad.Service;

// These records are capabilities from the sole installation/update authority,
// read through admin/System-owned, application-read-only file handles. No UI
// Boolean, document-selected public key, environment override or fallback trust.
internal sealed record InstalledRecovery(string SourceDataId,string BackupId,string OperationId,string ReceiptDigest);
internal sealed record InstalledActivation(int Format,string InstallationId,long Epoch,string ReleaseDigest,string DataId,
    int MinimumSchema,int MaximumSchema,string MasterKeyDigest,string PolicyDigest,string PinDigest,string OwnerIntentDigest,
    string WebDigest,string Phase,InstalledRecovery? Recovery);
internal sealed record InstalledEpoch(int Format,string InstallationId,long Epoch,string ActivationDigest);
internal sealed record InstalledOwnerIntent(int Format,string InstallationId,long Epoch,string Nonce,string ReleaseDigest,
    string DataId,string Operation,string Subject,long ApprovedUnix,long? ExpiresUnix);
internal sealed record InstalledWeb(int Format,string InstallationId,string Origin,int Port);
internal interface IInstalledControlFiles
{byte[] Read(string path,int maximum,ICollection<IDisposable> retained);}
internal sealed class AdminInstalledControlFiles:IInstalledControlFiles
{
    public byte[] Read(string path,int maximum,ICollection<IDisposable> retained)
    {var file=WindowsFileBoundary.OpenInstalledRead(path);retained.Add(file);return file.Small(maximum);}
}

internal sealed class InstalledActivationResolver:IInstalledActivationResolver,IObservedAdmission
{
    public TimeSpan AdmissionBudget=>TimeSpan.FromMinutes(5);
    public event Action? ObservedWork;
    internal static string StateRoot=>Path.GetDirectoryName(WindowsInstallationReader.DescriptorPath)!;
    private readonly string root;private readonly IInstalledControlFiles control;private readonly IChildControlFactory channels;
    private readonly Func<DateTimeOffset> clock;private readonly JobProcessFaultInjection? probeFault;
    internal InstalledActivationResolver():this(StateRoot,new AdminInstalledControlFiles(),new LocalChildControlFactory(),()=>DateTimeOffset.UtcNow){}
    internal InstalledActivationResolver(string root,IInstalledControlFiles control,IChildControlFactory channels,Func<DateTimeOffset> clock,JobProcessFaultInjection? probeFault=null)
    {this.root=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(root));this.control=control;this.channels=channels;this.clock=clock;this.probeFault=probeFault;}
    public async Task<AuthorizedServiceStart> ResolveAsync(InstallationBinding binding,CancellationToken cancellation)
    {
        var last=System.Diagnostics.Stopwatch.GetTimestamp();
        void Work()
        {
            cancellation.ThrowIfCancellationRequested();var now=System.Diagnostics.Stopwatch.GetTimestamp();
            if(System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref last),now)<TimeSpan.FromSeconds(1))return;
            Interlocked.Exchange(ref last,now);ObservedWork?.Invoke();
        }
        var reservation=new InstalledReservation(root,clock,Work,cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();reservation.Hold(WindowsFileBoundary.PinPrivateDirectory(root));reservation.CheckPending();
            byte[] Protected(string name,int bound=16384)=>control.Read(Path.Combine(root,name),bound,reservation.Resources);
            var bytes=Protected("activation.json");if(Hash(bytes)!=binding.ActivationReceiptDigest)throw new IOException("INSTALLED_ACTIVATION_ANCHOR");
            var head=NativeJson.Read<InstalledActivation>(bytes);var floor=NativeJson.Read<InstalledEpoch>(Protected("epoch.json"));
            Id(binding.InstallationId);Id(head.DataId);Digest(head.ReleaseDigest);
            if(head.Format!=1||head.InstallationId!=binding.InstallationId||head.Epoch<1||floor.Format!=1||floor.InstallationId!=binding.InstallationId||
                floor.Epoch!=head.Epoch||floor.ActivationDigest!=binding.ActivationReceiptDigest||head.MinimumSchema<1||head.MaximumSchema<head.MinimumSchema||head.MaximumSchema>100000)throw new IOException("INSTALLED_EPOCH_OR_IDENTITY");
            var ownerBytes=Protected("owner-intent.json");if(Hash(ownerBytes)!=head.OwnerIntentDigest)throw new IOException("INSTALLED_OWNER_ANCHOR");
            var owner=NativeJson.Read<InstalledOwnerIntent>(ownerBytes);Id(owner.Nonce);
            if(owner.Format!=1||owner.InstallationId!=binding.InstallationId||owner.Epoch!=head.Epoch||owner.ReleaseDigest!=head.ReleaseDigest||owner.DataId!=head.DataId||
                owner.Operation!=(head.Recovery is not null?"recover":"activate")||string.IsNullOrWhiteSpace(owner.Subject)||owner.Subject.Length>128||owner.ApprovedUnix<=0)throw new IOException("INSTALLED_OWNER_SCOPE");
            reservation.Owner=owner;reservation.CheckOwner();
            var webBytes=Protected("web.json");if(Hash(webBytes)!=head.WebDigest)throw new IOException("INSTALLED_WEB_ANCHOR");
            var web=NativeJson.Read<InstalledWeb>(webBytes);
            if(web.Format!=1||web.InstallationId!=binding.InstallationId||web.Port is <1024 or >65535||!Uri.TryCreate(web.Origin,UriKind.Absolute,out var origin)||origin.Scheme!="https"||origin.UserInfo!=""||origin.Query!=""||origin.Fragment!=""||origin.AbsolutePath!="/")throw new IOException("INSTALLED_WEB_CONFIGURATION");
            if(head.Phase is not("committed" or "recovered")||head.Phase=="recovered"&&head.Recovery is null)throw new IOException("INSTALLED_INCOMPLETE_ACTIVATION");
            var selected=Path.Combine(root,"datasets",head.DataId);var key=Path.Combine(selected,"master-key");var policy=Path.Combine(selected,"update-policy");var pins=Path.Combine(selected,"update-pin");
            if(head.Recovery is { } recovery)
            {
                Id(recovery.SourceDataId);Id(recovery.BackupId);Id(recovery.OperationId);Digest(recovery.ReceiptDigest);
                selected=Path.Combine(root,"datasets",recovery.SourceDataId,"restore-"+recovery.OperationId,"completed");
                key=Path.Combine(selected,"identity","master-key");policy=Path.Combine(selected,"policy","update-policy");pins=Path.Combine(selected,"policy","update-pin");
            }
            reservation.Data=Path.Combine(selected,"data");reservation.Hold(WindowsFileBoundary.PinPrivateDirectory(selected));
            reservation.Lease=WindowsFileBoundary.AcquireDataLease(reservation.Data);reservation.Hold(reservation.Lease);reservation.CheckWal();
            if(reservation.PinDigest(key)!=head.MasterKeyDigest||reservation.PinDigest(policy)!=head.PolicyDigest||reservation.PinDigest(pins)!=head.PinDigest)throw new IOException("INSTALLED_DATA_IDENTITY");
            reservation.PinData();
            var payload=Path.Combine(root,"payloads",head.ReleaseDigest);var payloadManifest=reservation.PinBytes(Path.Combine(payload,"bundle.json"),16*1024*1024);
            if(Hash(payloadManifest)!=head.ReleaseDigest)throw new IOException("INSTALLED_RELEASE_ANCHOR");
            reservation.VerifyPayload(payload,payloadManifest);
            var node=Path.Combine(payload,"runtime","node","node.exe");var temporary=Path.Combine(root,"temporary");
            reservation.Hold(WindowsFileBoundary.PinPrivateDirectory(temporary));
            var schema=await FrozenAdmissionSchema.ReadAsync(node,Path.Combine(reservation.Data,"app.sqlite"),temporary,cancellation,probeFault);
            if(schema<head.MinimumSchema||schema>head.MaximumSchema||schema>reservation.PayloadSchema||head.MaximumSchema!=reservation.PayloadSchema)throw new IOException("INSTALLED_SCHEMA_INCOMPATIBLE");
            if(head.Recovery is { } recovered)
            {
                var source=Path.Combine(root,"datasets",recovered.SourceDataId,"data");
                if(head.Phase=="recovered")
                {
                    var restoredSchema=NativeRecoveryAdmission.VerifyCompleted(Path.Combine(root,"datasets",recovered.SourceDataId),source,recovered.BackupId,recovered.OperationId,recovered.ReceiptDigest,
                        selected,key,policy,pins,reservation.DataPins);
                    if(restoredSchema!=schema)throw new IOException("INSTALLED_RECOVERY_SCHEMA");
                    throw new IOException("INSTALLED_RECOVERY_COMMIT_REQUIRED");
                }
                var baseline=NativeRecoveryAdmission.VerifyCommittedEvidence(Path.Combine(root,"datasets",recovered.SourceDataId),source,recovered.BackupId,recovered.OperationId,recovered.ReceiptDigest,
                    selected,key,policy,pins,reservation.DataPins);
                if(baseline<head.MinimumSchema||baseline>head.MaximumSchema||schema<baseline)throw new IOException("INSTALLED_RECOVERY_SCHEMA");
            }
            reservation.Release=head.ReleaseDigest;await reservation.RequireStartAllowedAsync(new(head.ReleaseDigest),cancellation);
            var environment=new Dictionary<string,string>{{"HOST","127.0.0.1"},{"PORT",web.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                {"APP_ORIGIN",origin.GetLeftPart(UriPartial.Authority)},{"COOKIE_SECURE","true"},{"DATA_DIR",reservation.Data},{"MASTER_KEY_FILE",key},
                {"CONNECTOR","mock"},{"ALLOW_REAL_WHATSAPP","no"},{"SETUP_CAPABILITY_FILE",Path.Combine(root,"Secrets","setup-capability.json")}};
            var spec=new VerifiedProcessSpec(node,reservation.NodeDigest,["--import","tsx","src/main.ts"],Path.Combine(payload,"app"),temporary,
                Path.Combine(payload,"runtime","ffmpeg","ffmpeg.exe"),environment,reservation.Lease,reservation.BeginMutableStart);
            return new(new(head.ReleaseDigest),new ServiceProcessLauncher(spec,channels),reservation,reservation);
        }
        catch(ProbeOwnershipException owned){owned.Retain(reservation);throw;}
        catch{reservation.Dispose();throw;}
    }
    internal static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string FileHash(WindowsFileBoundary.PrivateReadLease file,Action work)
    {using var h=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var b=new byte[1024*1024];long offset=0;try{int n;while((n=file.Read(offset,b))>0){h.AppendData(b,0,n);offset+=n;work();}return Convert.ToHexStringLower(h.GetHashAndReset());}finally{CryptographicOperations.ZeroMemory(b);}}
    internal static void Id(string id){if(!Guid.TryParseExact(id,"N",out var parsed)||parsed.ToString("N")!=id)throw new IOException("INSTALLED_IDENTIFIER");}
    internal static void Digest(string digest){if(!System.Text.RegularExpressions.Regex.IsMatch(digest,"^[a-f0-9]{64}$"))throw new IOException("INSTALLED_DIGEST");}
    private sealed class InstalledReservation(string root,Func<DateTimeOffset> clock,Action work,CancellationToken admissionCancellation):IAppRecoveryBarrier,IDisposable
    {
        internal readonly List<IDisposable> Resources=[],DataPins=[];private bool disposed,mutable;
        internal WindowsFileBoundary.DataLease Lease=null!;internal InstalledOwnerIntent Owner=null!;internal string Data="",Release="",Payload="",NodeDigest="";internal int PayloadSchema;
        private string[] dataMembers=[],payloadMembers=[];
        internal void Hold(IDisposable item)=>Resources.Add(item);
        internal byte[] PinBytes(string path,int max){var file=WindowsFileBoundary.OpenPrivateRead(path);Hold(file);return file.Small(max);}
        internal string PinDigest(string path){var file=WindowsFileBoundary.OpenPrivateRead(path);Hold(file);return FileHash(file,work);}
        internal void CheckOwner(){var now=clock().ToUnixTimeSeconds();if(Owner.ApprovedUnix>now||Owner.ExpiresUnix is { } expiry&&(expiry<=now||expiry<=Owner.ApprovedUnix))throw new IOException("INSTALLED_OWNER_EXPIRED");}
        internal void CheckPending(){if(Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Any(n=>n!.Equals("pending.json",StringComparison.OrdinalIgnoreCase)||new[]{"activation.json","epoch.json","owner-intent.json","web.json","bootstrap.json"}.Any(p=>n.StartsWith(p+".partial",StringComparison.OrdinalIgnoreCase))))throw new IOException("INSTALLED_PENDING_ACTIVATION");}
        internal void CheckWal(){foreach(var name in new[]{"app.sqlite-wal","app.sqlite-shm","app.sqlite-journal"})if(Path.Exists(Path.Combine(Data,name)))throw new IOException("INSTALLED_WAL_RECONCILIATION_REQUIRED");}
        internal string[] Names(string directory,int max=50001,CancellationToken cancellation=default)
        {
            var names=new List<string>();void Walk(string path,int depth)
            {if(depth>64)throw new IOException("INSTALLED_DEPTH");using var pinned=WindowsFileBoundary.PinPrivateDirectory(path);foreach(var child in Directory.EnumerateFileSystemEntries(path)){cancellation.ThrowIfCancellationRequested();work();if((File.GetAttributes(child)&FileAttributes.ReparsePoint)!=0)throw new IOException("INSTALLED_REPARSE");if(Directory.Exists(child))Walk(child,depth+1);else{names.Add(Path.GetRelativePath(directory,child).Replace('\\','/'));if(names.Count>max)throw new IOException("INSTALLED_MEMBERS");}}}
            Walk(directory,0);return names.Order(StringComparer.Ordinal).ToArray();
        }
        internal void PinData()
        {
            dataMembers=Names(Data,100001).Where(n=>n!="process.lock").ToArray();if(!dataMembers.Contains("app.sqlite"))throw new IOException("INSTALLED_DATABASE_MISSING");
            foreach(var name in dataMembers){work();DataPins.Add(WindowsFileBoundary.OpenPrivateRead(Path.Combine(Data,name.Replace('/',Path.DirectorySeparatorChar))));}
        }
        internal void VerifyPayload(string payload,byte[] manifest)
        {
            Payload=payload;var value=NativeJson.Read<JsonElement>(manifest);
            if(value.GetProperty("schema").GetInt32()!=1||value.GetProperty("kind").GetString()!="windows-runtime-candidate"||value.GetProperty("target").GetString()!="win-x64"||value.GetProperty("profile").GetString()!="full"||value.GetProperty("authority").GetString()!="application-updater")throw new IOException("INSTALLED_RELEASE_FORMAT");
            var launch=value.GetProperty("launch");foreach(var pair in new Dictionary<string,string>{{"node","runtime/node/node.exe"},{"entry","app/src/main.ts"},{"helper","app/native/Yahad.Platform.exe"},{"ffmpeg","runtime/ffmpeg/ffmpeg.exe"},{"caddy","runtime/caddy/caddy.exe"}})if(launch.GetProperty(pair.Key).GetString()!=pair.Value)throw new IOException("INSTALLED_LAUNCH_FORMAT");
            var expected=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"bundle.json"};long total=0;
            foreach(var record in value.GetProperty("files").EnumerateArray())
            {
                work();
                var name=record.GetProperty("path").GetString()!;var path=WindowsFileBoundary.CanonicalLocalPath(Path.Combine(payload,name.Replace('/',Path.DirectorySeparatorChar)));
                if(name.Length>240||name.Contains('\\')||!name.StartsWith("app/",StringComparison.Ordinal)&&!name.StartsWith("runtime/",StringComparison.Ordinal)&&!name.StartsWith("legal/",StringComparison.Ordinal)||!expected.Add(name)||expected.Count>50001)throw new IOException("INSTALLED_RELEASE_PATH");
                var size=record.GetProperty("size").GetInt64();if(size<0||size>512L*1024*1024||(total+=size)>2L*1024*1024*1024)throw new IOException("INSTALLED_RELEASE_BOUND");
                var digest=record.GetProperty("sha256").GetString()!;Digest(digest);var file=WindowsFileBoundary.OpenPrivateRead(path);Hold(file);
                if(file.Length!=size||FileHash(file,work)!=digest)throw new IOException("INSTALLED_RELEASE_BYTES");if(name=="runtime/node/node.exe")NodeDigest=digest;
            }
            foreach(var required in new[]{"runtime/node/node.exe","app/src/main.ts","app/node_modules/tsx/package.json","app/native/Yahad.Platform.exe","runtime/ffmpeg/ffmpeg.exe","runtime/caddy/caddy.exe","legal/build-inputs.json"})if(!expected.Contains(required))throw new IOException("INSTALLED_RELEASE_INCOMPLETE");
            payloadMembers=Names(payload);if(!expected.SetEquals(payloadMembers))throw new IOException("INSTALLED_RELEASE_CLOSURE");
            using var build=JsonDocument.Parse(PinBytes(Path.Combine(payload,"legal","build-inputs.json"),65536));PayloadSchema=build.RootElement.GetProperty("databaseSchema").GetInt32();
        }
        public Task RequireStartAllowedAsync(AppRelease release,CancellationToken cancellation)
            =>Task.Run(()=>CheckStart(release,cancellation),cancellation);
        private void CheckStart(AppRelease release,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();ObjectDisposedException.ThrowIf(disposed,this);if(mutable||release.Id!=Release)throw new IOException("INSTALLED_ALREADY_STARTED");
            CheckOwner();CheckPending();CheckWal();if(!Names(Data,100001,cancellation).Where(n=>n!="process.lock").SequenceEqual(dataMembers)||!Names(Payload,50001,cancellation).SequenceEqual(payloadMembers))throw new IOException("INSTALLED_SELECTION_CHANGED");
        }
        internal void BeginMutableStart()
        {
            CheckStart(new(Release),admissionCancellation);mutable=true;
            for(var i=DataPins.Count-1;i>=0;i--)DataPins[i].Dispose();DataPins.Clear(); // exclusive OS gate is continuously retained
        }
        public void Dispose(){if(disposed)return;disposed=true;for(var i=DataPins.Count-1;i>=0;i--)DataPins[i].Dispose();for(var i=Resources.Count-1;i>=0;i--)Resources[i].Dispose();}
    }
}

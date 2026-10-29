using System.Security.Cryptography;
using System.Text;
using Yahad.Install;

namespace Yahad.Service;

internal sealed record ReadOnlyAdmissionView(string ReleaseId,int ObservedSchema,string DataDirectory,string MasterKeyPath,
    string NodePath,string EntryPath,string DataTreeDigest);
internal interface IReadOnlyAdmissionLauncherFactory
{IPrivateAppLauncher Create(ReadOnlyAdmissionView view);}
internal sealed record AdmissionBackup(int Format,string InstallationId,string DataId,string BackupId,int Schema,
    string DataTreeDigest,string MasterKeyDigest,string PolicyDigest,string PinDigest);

// Explicit fixture-only bridge into the existing lifecycle. Program.cs does not
// construct this resolver or load test keys. Production still uses its deny-only
// resolver. Holding this read-only data reservation is NOT the production
// writer-lease handoff ABI and must not be used to launch the real mutable Store.
internal sealed class SignedFixtureAdmissionResolver(string root,JobProcess retainedEmptyAppJob,string privateNode,
    FixtureDocumentVerifier releases,FixtureDocumentVerifier control,FixtureDocumentVerifier owners,
    long minimumEpoch,IReadOnlyAdmissionLauncherFactory launcherFactory,Func<DateTimeOffset> clock):IInstalledActivationResolver
{
    public async Task<AuthorizedServiceStart> ResolveAsync(InstallationBinding binding,CancellationToken cancellation)
    {
        var reservation=new Reservation(Path.GetFullPath(root),retainedEmptyAppJob,clock);
        try
        {
            if(minimumEpoch<1)throw new IOException("ADMISSION_EPOCH_ANCHOR");
            SafeDirectory.ValidateSession(reservation.Root);
            reservation.Hold(WindowsFileBoundary.PinPrivateDirectory(reservation.Root));
            reservation.RequireNoPendingArtifacts();
            await retainedEmptyAppJob.WaitForTreeEmptyAsync(cancellation);
            var headBytes=reservation.Read("head.json");
            if(Hash(headBytes)!=binding.ActivationReceiptDigest)throw new IOException("ADMISSION_HEAD_ANCHOR");
            var head=control.Verify<AdmissionHead>(headBytes,"head");
            if(head.Format!=1||head.InstallationId!=binding.InstallationId||head.Epoch<minimumEpoch)throw new IOException("ADMISSION_HEAD_IDENTITY");
            Id(head.InstallationId);
            if(reservation.Digest("update-policy")!=head.PolicyDigest||reservation.Digest("update-pin")!=head.PinDigest)throw new IOException("ADMISSION_POLICY_DRIFT");
            byte[] Document(string name,string digest){var bytes=reservation.Read(name);if(Hash(bytes)!=digest)throw new IOException("ADMISSION_DOCUMENT_IDENTITY");return bytes;}
            var release=releases.Verify<AdmissionRelease>(Document("release.json",head.ReleaseDigest),"release");
            var selection=control.Verify<AdmissionSelection>(Document("selection.json",head.SelectionDigest),"selection");
            var intent=control.Verify<AdmissionIntent>(Document("intent.json",head.IntentDigest),"intent");
            var owner=owners.Verify<AdmissionOwner>(Document("owner.json",head.OwnerDigest),"owner");
            if(release.Format!=1||!System.Text.RegularExpressions.Regex.IsMatch(release.ReleaseId,"^[a-z0-9][a-z0-9.-]{0,63}$")||
                release.MinimumSchema<1||release.MaximumSchema<release.MinimumSchema||release.MaximumSchema>100000||release.Files.Length is <1 or >64)throw new IOException("ADMISSION_RELEASE");
            if(selection.Format!=1||selection.InstallationId!=head.InstallationId||selection.Epoch!=head.Epoch||selection.ReleaseId!=release.ReleaseId)throw new IOException("ADMISSION_SELECTION");
            Id(selection.DataId);
            if(intent.Format!=1||intent.InstallationId!=head.InstallationId||intent.Epoch!=head.Epoch||intent.ReleaseDigest!=head.ReleaseDigest||
                intent.SelectionDigest!=head.SelectionDigest||intent.DataId!=selection.DataId||intent.PolicyDigest!=head.PolicyDigest||intent.PinDigest!=head.PinDigest)throw new IOException("ADMISSION_INTENT_IDENTITY");
            Id(intent.Nonce);
            if(intent.Phase is not("committed" or "recovered"))throw new IOException("ADMISSION_INCOMPLETE_INTENT");
            if(owner.Format!=1||owner.InstallationId!=head.InstallationId||owner.Epoch!=head.Epoch||owner.Nonce!=intent.Nonce||
                owner.ReleaseDigest!=head.ReleaseDigest||owner.SelectionDigest!=head.SelectionDigest||owner.DataId!=selection.DataId||
                owner.PolicyDigest!=head.PolicyDigest||owner.PinDigest!=head.PinDigest||owner.Operation!=(intent.Phase=="recovered"?"recover":"activate"))throw new IOException("ADMISSION_OWNER_SCOPE");
            reservation.OwnerExpiry=owner.ExpiresUnix;reservation.RequireOwnerTime();
            var container="data/"+selection.DataId;var state=Child(reservation.Root,container+"/state");
            reservation.Hold(WindowsFileBoundary.PinPrivateDirectory(Child(reservation.Root,container)));
            var identityBytes=reservation.Read(container+"/identity.json");
            if(Hash(identityBytes)!=selection.IdentityDigest)throw new IOException("ADMISSION_DATA_IDENTITY");
            var identity=control.Verify<AdmissionDataIdentity>(identityBytes,"data-identity");
            if(identity.Format!=1||identity.InstallationId!=head.InstallationId||identity.DataId!=selection.DataId||reservation.Digest(container+"/master-key")!=identity.MasterKeyDigest)throw new IOException("ADMISSION_KEY_IDENTITY");
            reservation.DataDirectory=state;reservation.Hold(WindowsFileBoundary.AcquireDataLease(state));
            await retainedEmptyAppJob.WaitForTreeEmptyAsync(cancellation);reservation.RequireNoWal();
            var tree=reservation.PinDataTree();
            var payloadNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long payloadTotal=0;
            foreach(var file in release.Files)
            {
                if(!payloadNames.Add(file.Path)||file.Bytes<0||file.Bytes>256L*1024*1024||(payloadTotal+=file.Bytes)>512L*1024*1024)throw new IOException("ADMISSION_RELEASE_FILES");
                var pinned=reservation.Pin("payload/"+file.Path);
                if(pinned.Length!=file.Bytes||Digest(pinned)!=file.Sha256)throw new IOException("ADMISSION_RELEASE_BYTES");
            }
            if(!payloadNames.Contains(release.Entry)||!reservation.Names(Child(reservation.Root,"payload")).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(payloadNames))throw new IOException("ADMISSION_RELEASE_MEMBERS");
            var node=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(privateNode));SafeDirectory.NoReparseAncestors(node);
            var image=new FileStream(node,FileMode.Open,FileAccess.Read,FileShare.Read);reservation.Hold(image);
            if(Convert.ToHexStringLower(SHA256.HashData(image))!=release.NodeSha256)throw new IOException("ADMISSION_RUNTIME_IDENTITY");
            var observations=Child(reservation.Root,"observations");reservation.Hold(WindowsFileBoundary.PinPrivateDirectory(observations));
            var schema=await FrozenAdmissionSchema.ReadAsync(node,Path.Combine(state,"app.sqlite"),observations,cancellation);
            if(schema<release.MinimumSchema||schema>release.MaximumSchema)throw new IOException("ADMISSION_SCHEMA_INCOMPATIBLE");
            if(intent.Phase=="recovered")
            {
                if(head.RecoveryDigest is null)throw new IOException("ADMISSION_RECOVERY_MISSING");
                var recovery=control.Verify<AdmissionRecovery>(Document("recovery.json",head.RecoveryDigest),"recovery");
                var backup=control.Verify<AdmissionBackup>(Document("backup.json",recovery.BackupDigest),"backup");
                if(recovery.Format!=1||recovery.InstallationId!=head.InstallationId||recovery.Epoch!=head.Epoch||recovery.Phase!="complete"||
                    recovery.ReleaseDigest!=head.ReleaseDigest||recovery.SelectionDigest!=head.SelectionDigest||recovery.DataId!=selection.DataId||
                    recovery.OwnerDigest!=head.OwnerDigest||recovery.Schema!=schema||recovery.DataTreeDigest!=tree||
                    backup.Format!=1||backup.InstallationId!=head.InstallationId||backup.DataId!=selection.DataId||backup.Schema!=schema||backup.DataTreeDigest!=tree||
                    backup.MasterKeyDigest!=identity.MasterKeyDigest||backup.PolicyDigest!=head.PolicyDigest||backup.PinDigest!=head.PinDigest)throw new IOException("ADMISSION_RECOVERY_UNVERIFIED");
                Id(backup.BackupId);
            }
            else if(head.RecoveryDigest is not null)throw new IOException("ADMISSION_UNEXPECTED_RECOVERY");
            reservation.PayloadMembers=payloadNames;
            reservation.Release=release.ReleaseId;
            await reservation.RequireStartAllowedAsync(new(release.ReleaseId),cancellation);
            var view=new ReadOnlyAdmissionView(release.ReleaseId,schema,state,Child(reservation.Root,container+"/master-key"),node,Child(reservation.Root,"payload/"+release.Entry),tree);
            return new(new(release.ReleaseId),launcherFactory.Create(view),reservation,reservation);
        }
        catch(ProbeOwnershipException owned){owned.Retain(reservation);throw;}
        catch{reservation.Dispose();throw;}
    }
    internal static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string Digest(WindowsFileBoundary.PrivateReadLease file)
    {
        var buffer=new byte[1024*1024];using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long offset=0;
        try{int count;while((count=file.Read(offset,buffer))>0){hash.AppendData(buffer,0,count);offset+=count;}return Convert.ToHexStringLower(hash.GetHashAndReset());}
        finally{CryptographicOperations.ZeroMemory(buffer);}
    }
    private static void Id(string id){if(!Guid.TryParseExact(id,"N",out var parsed)||parsed.ToString("N")!=id)throw new IOException("ADMISSION_IDENTIFIER");}
    private static string Child(string root,string relative)
    {
        if(relative.Length is <1 or >200||relative.Contains('\\')||relative.Split('/').Any(p=>p is "" or "." or ".."||p.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new IOException("ADMISSION_PATH");
        return WindowsFileBoundary.CanonicalLocalPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
    }
    private sealed class Reservation(string root,JobProcess job,Func<DateTimeOffset> clock):IAppRecoveryBarrier,IDisposable
    {
        private readonly List<IDisposable> held=[];private string[] members=[];private bool disposed;
        internal HashSet<string> PayloadMembers=[];
        internal string Root=>root;internal string DataDirectory="",Release="";internal long OwnerExpiry;
        internal void Hold(IDisposable resource)=>held.Add(resource);
        internal WindowsFileBoundary.PrivateReadLease Pin(string name){var file=WindowsFileBoundary.OpenPrivateRead(Child(root,name));Hold(file);return file;}
        internal byte[] Read(string name)=>Pin(name).Small(1024*1024);
        internal string Digest(string name)=>SignedFixtureAdmissionResolver.Digest(Pin(name));
        internal void RequireOwnerTime(){var now=clock().ToUnixTimeSeconds();if(OwnerExpiry<=now||OwnerExpiry>now+600)throw new IOException("ADMISSION_OWNER_EXPIRED");}
        internal string[] Names(string directory)
        {
            var names=new List<string>();
            void Walk(string current,int depth)
            {
                if(depth>32)throw new IOException("ADMISSION_DEPTH");using var pin=WindowsFileBoundary.PinPrivateDirectory(current);
                foreach(var path in Directory.EnumerateFileSystemEntries(current))
                {
                    if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("ADMISSION_REPARSE");
                    if(Directory.Exists(path))Walk(path,depth+1);else {names.Add(Path.GetRelativePath(directory,path).Replace('\\','/'));if(names.Count>1024)throw new IOException("ADMISSION_MEMBERS");}
                }
            }
            Walk(directory,0);return names.Order(StringComparer.Ordinal).ToArray();
        }
        internal void RequireNoPendingArtifacts()
        {
            foreach(var path in Directory.EnumerateFileSystemEntries(root))
            {
                var name=Path.GetFileName(path);
                if(name.Equals("pending.json",StringComparison.OrdinalIgnoreCase)||new[]{"head.json","selection.json","intent.json","recovery.json"}.Any(n=>name.StartsWith(n+".partial",StringComparison.OrdinalIgnoreCase)))throw new IOException("ADMISSION_PENDING_ARTIFACT");
            }
        }
        internal void RequireNoWal()
        {foreach(var name in new[]{"app.sqlite-wal","app.sqlite-shm","app.sqlite-journal"})if(File.Exists(Path.Combine(DataDirectory,name))||Directory.Exists(Path.Combine(DataDirectory,name)))throw new IOException("ADMISSION_WAL_RECONCILIATION_REQUIRED");}
        internal string PinDataTree()
        {
            members=Names(DataDirectory).Where(n=>n!="process.lock").ToArray();if(!members.Contains("app.sqlite"))throw new IOException("ADMISSION_DATABASE_MISSING");
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long total=0;
            foreach(var name in members)
            {
                var file=WindowsFileBoundary.OpenPrivateRead(Child(DataDirectory,name));Hold(file);
                if(file.Length>256L*1024*1024||(total+=file.Length)>512L*1024*1024)throw new IOException("ADMISSION_DATA_BOUND");
                hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{name}\0{file.Length}\0{SignedFixtureAdmissionResolver.Digest(file)}\n")));
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        public async Task RequireStartAllowedAsync(AppRelease release,CancellationToken cancellation)
        {
            ObjectDisposedException.ThrowIf(disposed,this);if(release.Id!=Release)throw new IOException("ADMISSION_RELEASE_CHANGED");
            RequireOwnerTime();RequireNoPendingArtifacts();await job.WaitForTreeEmptyAsync(cancellation);RequireNoWal();
            if(!Names(Child(root,"payload")).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(PayloadMembers))throw new IOException("ADMISSION_RELEASE_MEMBERS");
            if(!Names(DataDirectory).Where(n=>n!="process.lock").SequenceEqual(members))throw new IOException("ADMISSION_DATA_MEMBERS_CHANGED");
        }
        public void Dispose(){if(disposed)return;disposed=true;for(var i=held.Count-1;i>=0;i--)held[i].Dispose();}
    }
}

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using Yahad.Install;
namespace Yahad.Setup;

// Normal authenticated/elevated uninstall dispatch owns the global setup lock.
// This adapter cannot adopt an enrolled profile, a running service or user data.
internal sealed class NativePreparingCancellationHost:IPreparingCancellationHost
{
    private byte[]? receipt;
    private static readonly FullServiceKind[] Initial=[FullServiceKind.Runtime,FullServiceKind.Authority];
    private static FullServiceKind[] PlannedKinds(PreparingCancellationIntent intent)=>intent.State.RuntimeSid is null?Initial:[FullServiceKind.Runtime,FullServiceKind.Authority,FullServiceKind.RecoveryA,FullServiceKind.RecoveryB];
    private const string Registration=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Yahad.FirstInstall";
    private static bool FilePresent(string path)
    {
        try{var value=File.GetAttributes(path);if((value&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)throw new IOException("UPDATE_PREPARING_FILE_CHANGED");return true;}
        catch(FileNotFoundException){return false;}catch(DirectoryNotFoundException){return false;}
    }
    internal static bool Selected()
    {
        var path=FilePresent(InstallLocations.RemovalStateFile)?InstallLocations.RemovalStateFile:InstallLocations.StateFile;
        using var file=WindowsFileBoundary.OpenInstallerMetadata(path);using var json=JsonDocument.Parse(file.Small(16777216));var root=json.RootElement;
        return root.GetProperty("Format").GetInt32()==5||root.GetProperty("Format").GetInt32()==3&&root.GetProperty("Phase").GetString()=="preparing";
    }
    public PreparingCancellationIntent? ReadIntent()
    {
        if(!FilePresent(InstallLocations.RemovalStateFile))return null;
        using var strict=WindowsFileBoundary.PinPreparingInstallMember(InstallLocations.RemovalStateFile,false);
        using var file=WindowsFileBoundary.OpenInstallerMetadata(InstallLocations.RemovalStateFile);receipt=file.Small(16777216);
        return NativeJson.Read<PreparingCancellationIntent>(receipt);
    }
    public PreparingCancellationIntent Prepare()
    {
        using var file=WindowsFileBoundary.OpenInstallerMetadata(InstallLocations.StateFile);var bytes=file.Small(16777216);
        var state=NativeJson.Read<FullInstallState>(bytes);NativeFullSystemSetupHost.ValidateState(state);
        if(state.RuntimeSid is null){using var root=WindowsFileBoundary.PinPreparingInstallMember(InstallLocations.State,true);}
        else new FullInstallFiles(state).Validate(InstallLocations.State,true);
        var stages=Directory.EnumerateFileSystemEntries(InstallLocations.State).Select(Path.GetFileName).Where(n=>n is not null&&n.StartsWith("setup-stage-",StringComparison.Ordinal)).ToArray();
        if(stages.Length!=1)throw new IOException("UPDATE_PREPARING_CANCEL_REFUSED");
        return new(5,bytes,stages[0]!,NativeFullService.Exists(FullServiceKind.Runtime),NativeFullService.Exists(FullServiceKind.Authority));
    }
    internal static void RequireInventory(PreparingCancellationIntent intent,IReadOnlyCollection<string> files,IReadOnlyCollection<string> directories,bool complete)
    {
        var expected=intent.State.Files.Select(f=>intent.StagingDirectory+"/"+f.Path).Append("installer-state.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var required=expected.ToHashSet(StringComparer.OrdinalIgnoreCase);var state=intent.State;
        var parents=new HashSet<string>(StringComparer.OrdinalIgnoreCase){intent.StagingDirectory};
        foreach(var member in intent.State.Files)
        {
            var path=intent.StagingDirectory+"/"+member.Path;var slash=path.LastIndexOf('/');
            while(slash>0){parents.Add(path[..slash]);slash=path.LastIndexOf('/',slash-1);}
        }
        var requiredDirectories=parents.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(state.RuntimeSid is not null)
            foreach(var directory in new[]{"datasets","datasets/"+state.DataId,"datasets/"+state.DataId+"/data","temporary","Secrets","intake","grants","authority-temporary",".authority",".authority/update-serial",".authority/updates"})parents.Add(directory);
        if(files.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Count||directories.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=directories.Count||
            files.Any(p=>!expected.Contains(p))||directories.Any(p=>!parents.Contains(p))||complete&&(!required.IsSubsetOf(files)||!requiredDirectories.IsSubsetOf(directories)))
            throw new IOException("UPDATE_PREPARING_INVENTORY_CHANGED");
    }
    // Before enrollment the binary root may hold only verified members of the
    // package payload, which Setup copies first; any other binary means the
    // install went further and must be repaired, not cancelled.
    private static Dictionary<string,InstallMember> PayloadMembers(FullInstallState state)
        =>state.Files.Where(f=>f.Path.StartsWith("payload/",StringComparison.Ordinal)).ToDictionary(f=>"payloads/"+state.PayloadDigest+"/"+f.Path[8..],StringComparer.OrdinalIgnoreCase);
    internal static void RequireBinaryInventory(PreparingCancellationIntent intent,IReadOnlyCollection<string> files,IReadOnlyCollection<string> directories)
    {
        var expected=intent.State.RuntimeSid is null?new Dictionary<string,InstallMember>():PayloadMembers(intent.State);var parents=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in expected.Keys){var slash=path.LastIndexOf('/');while(slash>0){parents.Add(path[..slash]);slash=path.LastIndexOf('/',slash-1);}}
        if(files.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Count||directories.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=directories.Count||
            files.Any(p=>!expected.ContainsKey(p))||directories.Any(p=>!parents.Contains(p)))throw new IOException("UPDATE_PREPARING_BINARIES_PUBLISHED");
    }
    private static RemovalTree? InspectBinaries(PreparingCancellationIntent intent,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();if(!RemovalTree.DirectoryPresent(InstallLocations.Binaries))return null;
        var state=intent.State;var roles=state.RuntimeSid is null?null:new FullInstallFiles(state,true);
        var tree=RemovalTree.Inspect(InstallLocations.Binaries,(p,d)=>{cancellation.ThrowIfCancellationRequested();if(roles is null){using var pin=WindowsFileBoundary.PinPreparingInstallMember(p,d);}else roles.Validate(p,d);});
        string Relative(string path)=>Path.GetRelativePath(InstallLocations.Binaries,path).Replace('\\','/');
        RequireBinaryInventory(intent,tree.Files.Select(Relative).ToArray(),tree.Directories.Select(Relative).ToArray());
        var expected=PayloadMembers(state);
        foreach(var path in tree.Files)
        {using var input=WindowsFileBoundary.OpenInstallerRoleRead(path,roles!.Roles,roles.Role(path),administrativeOwner:true);RequireFileContents(expected[Relative(path)],input.Length,(offset,buffer)=>input.Read(offset,buffer),cancellation);}
        return tree;
    }
    private static RemovalTree? Inspect(PreparingCancellationIntent intent,bool complete,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(!RemovalTree.DirectoryPresent(InstallLocations.State)){if(complete)throw new IOException("UPDATE_PREPARING_INVENTORY_CHANGED");return null;}
        var state=intent.State;var roles=state.RuntimeSid is null?null:new FullInstallFiles(state);
        bool Staged(string path)=>Path.GetRelativePath(InstallLocations.State,path).Replace('\\','/') is var relative&&(relative==intent.StagingDirectory||relative.StartsWith(intent.StagingDirectory+"/",StringComparison.Ordinal));
        var tree=RemovalTree.Inspect(InstallLocations.State,(p,d)=>{cancellation.ThrowIfCancellationRequested();if(roles is null||Staged(p)){using var pin=WindowsFileBoundary.PinPreparingInstallMember(p,d);}else roles.Validate(p,d);});
        string Relative(string path)=>Path.GetRelativePath(InstallLocations.State,path).Replace('\\','/');
        RequireInventory(intent,tree.Files.Select(Relative).ToArray(),tree.Directories.Select(Relative).ToArray(),complete);
        var expected=intent.State.Files.ToDictionary(f=>intent.StagingDirectory+"/"+f.Path,StringComparer.OrdinalIgnoreCase);
        foreach(var path in tree.Files)
        {
            cancellation.ThrowIfCancellationRequested();
            var relative=Relative(path);
            if(relative=="installer-state.json")
            {using var metadata=WindowsFileBoundary.OpenInstallerMetadata(path);if(!metadata.Small(16777216).AsSpan().SequenceEqual(intent.StateBytes))throw new IOException("UPDATE_PREPARING_STATE_CHANGED");}
            else
            {
                var member=expected[relative];using var input=roles is not null&&!Staged(path)?WindowsFileBoundary.OpenInstallerRoleRead(path,roles.Roles,roles.Role(path),administrativeOwner:true):WindowsFileBoundary.OpenInstallerMetadata(path);
                RequireFileContents(member,input.Length,(offset,buffer)=>input.Read(offset,buffer),cancellation);
            }
        }
        return tree;
    }
    internal static void RequireFile(InstallMember member,long size,string hash)
    {if(size!=member.Size||hash!=member.Sha256)throw new IOException("UPDATE_PREPARING_FILE_CHANGED");}
    internal static void RequireFileContents(InstallMember member,long size,Func<long,byte[],int> read,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();if(size!=member.Size)throw new IOException("UPDATE_PREPARING_FILE_CHANGED");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[1048576];long offset=0;
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();var count=read(offset,buffer);if(count==0)break;
            if(count<0||count>buffer.Length||offset+count>size)throw new IOException("UPDATE_PREPARING_FILE_CHANGED");
            hash.AppendData(buffer,0,count);offset+=count;
        }
        RequireFile(member,offset,Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
    private static bool Planned(PreparingCancellationIntent intent,FullServiceKind kind)=>kind switch{FullServiceKind.Runtime=>intent.RuntimeCreated,FullServiceKind.Authority=>intent.AuthorityCreated,_=>intent.State.RuntimeSid is not null};
    public async Task VerifyAsync(PreparingCancellationIntent intent,bool complete,bool servicesRemoved,CancellationToken cancellation)
    {
        NativeInstallFiles.RequireAdministrator();var state=intent.State;var provisioned=state.RuntimeSid is not null;
        if(complete&&provisioned&&(state.RuntimeSid!=NativeFullService.Sid(FullServiceKind.Runtime)||state.AuthoritySid!=NativeFullService.Sid(FullServiceKind.Authority)))throw new IOException("UPDATE_PREPARING_SERVICE_CHANGED");
        _=Inspect(intent,complete,cancellation);
        if(InspectBinaries(intent,cancellation) is null&&complete)throw new IOException("UPDATE_PREPARING_INVENTORY_CHANGED");
        using var registration=Registry.LocalMachine.OpenSubKey(Registration);if(registration is not null)throw new IOException("UPDATE_PREPARING_ENROLLED");
        if(LanServiceRegistration.Exists()||!provisioned&&(NativeFullService.Exists(FullServiceKind.RecoveryA)||NativeFullService.Exists(FullServiceKind.RecoveryB)))throw new IOException("UPDATE_PREPARING_ENROLLED");
        foreach(var kind in PlannedKinds(intent))
        {
            cancellation.ThrowIfCancellationRequested();
            using var service=await NativeFullService.OpenForRemovalAsync(kind,cancellation);if(service is null){if(complete&&Planned(intent,kind))throw new IOException("UPDATE_PREPARING_SERVICE_CHANGED");continue;}
            if(servicesRemoved||!Planned(intent,kind))throw new IOException("UPDATE_PREPARING_SERVICE_CHANGED");
            if(provisioned)service.RequireProvisionedPreparingCancellation(state.OwnerSid,state.AuthoritySid!);else service.RequirePreparingCancellation();
        }
    }
    public void Publish(PreparingCancellationIntent intent)
    {
        receipt=JsonSerializer.SerializeToUtf8Bytes(intent);
        if(receipt.Length>16777216)throw new IOException("UPDATE_PREPARING_CANCEL_REFUSED");
        NativeInstallFiles.PublishNewProtectedFile(InstallLocations.RemovalStateFile,receipt,NativeInstallFiles.Descriptor());
    }
    public async Task RemoveServicesAsync(PreparingCancellationIntent intent,CancellationToken cancellation)
    {
        var state=intent.State;var kinds=PlannedKinds(intent);if(state.RuntimeSid is not null)Array.Reverse(kinds);
        foreach(var kind in kinds)
        {
            cancellation.ThrowIfCancellationRequested();
            using(var service=await NativeFullService.OpenForRemovalAsync(kind,cancellation))
            {
                if(service is not null){if(!Planned(intent,kind))throw new IOException("UPDATE_PREPARING_SERVICE_CHANGED");if(state.RuntimeSid is not null)service.RequireProvisionedPreparingCancellation(state.OwnerSid,state.AuthoritySid!);else service.RequirePreparingCancellation();service.Delete();}
            }
            var deadline=Environment.TickCount64+15000;
            while(NativeFullService.Exists(kind))
            {cancellation.ThrowIfCancellationRequested();if(Environment.TickCount64>=deadline)throw new IOException("UNINSTALL_SERVICE_REMOVAL_PENDING");await Task.Delay(100,cancellation);}
        }
    }
    public void RemoveFiles(PreparingCancellationIntent intent,CancellationToken cancellation)
    {
        var tree=Inspect(intent,false,cancellation);
        var state=intent.State;var roles=state.RuntimeSid is null?null:new FullInstallFiles(state);
        if(tree is not null)
        {
            var expected=intent.State.Files.ToDictionary(f=>intent.StagingDirectory+"/"+f.Path,StringComparer.OrdinalIgnoreCase);
            foreach(var path in tree.Files.Where(p=>!p.Equals(InstallLocations.StateFile,StringComparison.OrdinalIgnoreCase)))
            {
                cancellation.ThrowIfCancellationRequested();var relative=Path.GetRelativePath(InstallLocations.State,path).Replace('\\','/');var member=expected[relative];
                if(roles is null||relative.StartsWith(intent.StagingDirectory+"/",StringComparison.Ordinal))WindowsFileBoundary.DeletePreparingInstallMember(path,false,member.Size,member.Sha256);else{roles.VerifyFile(relative,member);roles.Delete(path,false);}
            }
            foreach(var directory in tree.Directories){cancellation.ThrowIfCancellationRequested();if(roles is null)WindowsFileBoundary.DeletePreparingInstallMember(directory,true);else roles.Delete(directory,true);}
            if(FilePresent(InstallLocations.StateFile)){if(roles is null)WindowsFileBoundary.DeletePreparingInstallMember(InstallLocations.StateFile,false,intent.StateBytes.Length,PreparingCancellationIntent.Hash(intent.StateBytes));else roles.Delete(InstallLocations.StateFile,false);}
            if(roles is null)WindowsFileBoundary.DeletePreparingInstallMember(InstallLocations.State,true);else roles.Delete(InstallLocations.State,true);
        }
        if(InspectBinaries(intent,cancellation) is { } binaryTree)
        {
            if(roles is null)WindowsFileBoundary.DeletePreparingInstallMember(InstallLocations.Binaries,true);
            else
            {
                var binaries=new FullInstallFiles(state,true);var payload=PayloadMembers(state);
                foreach(var path in binaryTree.Files){cancellation.ThrowIfCancellationRequested();var relative=Path.GetRelativePath(InstallLocations.Binaries,path).Replace('\\','/');binaries.VerifyFile(relative,payload[relative]);binaries.Delete(path,false);}
                foreach(var directory in binaryTree.Directories){cancellation.ThrowIfCancellationRequested();binaries.Delete(directory,true);}
                binaries.Delete(InstallLocations.Binaries,true);
            }
        }
    }
    public async Task CompleteAsync(PreparingCancellationIntent intent,CancellationToken cancellation)
    {
        await VerifyAsync(intent,false,true,cancellation);
        if(RemovalTree.DirectoryPresent(InstallLocations.State)||RemovalTree.DirectoryPresent(InstallLocations.Binaries))throw new IOException("UPDATE_PREPARING_INVENTORY_CHANGED");
        FullInstallDiagnostics.DeleteOwned(intent.State);
        var bytes=receipt??throw new IOException("UPDATE_PREPARING_INTENT_REQUIRED");
        WindowsFileBoundary.DeletePreparingInstallMember(InstallLocations.RemovalStateFile,false,bytes.Length,PreparingCancellationIntent.Hash(bytes));
    }
}

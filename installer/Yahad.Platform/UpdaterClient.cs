using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Platform;

internal readonly record struct UpdaterObservation(uint State,uint ProcessId,string Account,string Command);
internal readonly record struct UpdaterOperator(string FamilyId,string UserId);
// The app's windows-updates transport: one request from stdin, one reply on stdout.
// Anything that goes wrong is the same refusal, which the app shows as unavailable.
internal static class UpdaterClient
{
    private static readonly TimeSpan Limit=TimeSpan.FromSeconds(14);
    private static readonly JsonSerializerOptions Input=new(){MaxDepth=4,AllowDuplicateProperties=false};
    private static JsonObject Refusal()=>new(){["schema"]=1,["ok"]=false,["code"]="UPDATES_UNAVAILABLE"};
    private static JsonObject Result(JsonNode value)=>new(){["schema"]=1,["ok"]=true,["result"]=value};

    internal static async Task<JsonObject> HandleAsync(byte[] input)
    {
        using var deadline=new CancellationTokenSource(Limit);
        return await HandleAsync(input,ReadOperator,AskAsync,deadline.Token);
    }
    internal static async Task<JsonObject> HandleAsync(byte[] input,Func<UpdaterOperator> reader,Func<UpdaterRequest,CancellationToken,Task<UpdaterReply>> ask,CancellationToken cancellation)
    {
        try
        {
            var message=JsonSerializer.Deserialize<JsonElement>(input,Input);var kind=message.GetProperty("kind").GetString();
            var owner=reader();
            if(kind=="status")
            {
                var reply=await ask(new(1,"status"),cancellation);if(!reply.Ok||reply.Status is null)return Refusal();
                var status=JsonSerializer.SerializeToNode(reply.Status,UpdaterProtocol.Options)!.AsObject();
                status["operator"]=new JsonObject{["familyId"]=owner.FamilyId,["userId"]=owner.UserId};status["modeControlAllowed"]=true;
                return Result(status);
            }
            if(kind!="submit")return Refusal();
            var command=message.GetProperty("command");
            if(command.GetProperty("familyId").GetString()!=owner.FamilyId||command.GetProperty("userId").GetString()!=owner.UserId)return Refusal();
            var submitted=await ask(Request(command),cancellation);
            return submitted switch
            {
                {Ok:true}=>Result(new JsonObject{["state"]="queued"}),
                {Code:"BUSY"}=>Result(new JsonObject{["state"]="pending"}),
                {Code:"STALE_TARGET"}=>Result(new JsonObject{["state"]="target_changed"}),
                _=>Refusal()
            };
        }
        catch(Exception error)when(error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException or TimeoutException or UnauthorizedAccessException or Win32Exception){return Refusal();}
    }
    private static UpdaterRequest Request(JsonElement command)
    {
        var id=command.GetProperty("id").GetString();
        switch(command.GetProperty("action").GetString())
        {
            case "check":return new(1,"check",id);
            case "set_mode":return new(1,"set-mode",id,Mode:command.GetProperty("mode").GetString());
            case "install":var target=command.GetProperty("target");return new(1,"install",id,target.GetProperty("version").GetString(),target.GetProperty("fingerprint").GetString());
            default:throw new IOException("UPDATER_ACTION");
        }
    }
    // The account the status belongs to is the installation's first owner, which
    // Setup bound when it ran, and never anything in the request.
    private static UpdaterOperator ReadOperator()
    {
        using var file=WindowsFileBoundary.OpenSecret(Path.Combine(InstalledLayout.StateRoot,"Secrets","setup-capability.json"),SecretRole.SetupCapability);
        var bytes=file.Read();
        try
        {
            var owner=JsonSerializer.Deserialize<JsonElement>(bytes).GetProperty("firstOwner");
            return new(owner.GetProperty("familyId").GetString()!,owner.GetProperty("parentUserId").GetString()!);
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    // The pipe's name proves nothing, since anyone could try to hold it. The server
    // must be the process the SCM runs as the Updater service: Running, LocalSystem,
    // this release's own image. The SCM vouches for the process instead of its token,
    // because the Runtime's account may not open a SYSTEM process's token.
    internal static void RequireServer(uint serverProcess,UpdaterObservation service,string version)
    {
        if(service.State!=4||service.ProcessId==0||service.ProcessId!=serverProcess||!string.Equals(service.Account,ServiceDefinition.UpdaterAccount,StringComparison.OrdinalIgnoreCase)||
            service.Command!=InstalledLayout.UpdaterCommand(version))throw new IOException("UPDATER_SERVER_IDENTITY");
    }
    internal static Task<UpdaterReply> AskAsync(UpdaterRequest request,CancellationToken cancellation)
        =>AskAsync(UpdaterProtocol.PipeName,process=>RequireServer(process,Observe(),ApplicationRelease.Version),request,cancellation);
    internal static async Task<UpdaterReply> AskAsync(string name,Action<uint> verify,UpdaterRequest request,CancellationToken cancellation)
    {
        // Identification: enough for the service to name this client, never to act as it.
        await using var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(3000,cancellation);
        if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out var process))throw new Win32Exception();
        verify(process);
        await UpdaterProtocol.WriteAsync(pipe,request,cancellation);
        return await UpdaterProtocol.ReadAsync<UpdaterReply>(pipe,cancellation);
    }
    private static UpdaterObservation Observe()
    {
        var manager=OpenSCManagerW(null,null,1);if(manager==IntPtr.Zero)throw new Win32Exception();
        var service=IntPtr.Zero;
        try
        {
            service=OpenServiceW(manager,ServiceDefinition.UpdaterName,0x5);if(service==IntPtr.Zero)throw new Win32Exception();
            if(!QueryServiceStatusEx(service,0,out var status,(uint)Marshal.SizeOf<StatusProcess>(),out _))throw new Win32Exception();
            QueryServiceConfigW(service,IntPtr.Zero,0,out var bytes);if(bytes<(uint)Marshal.SizeOf<Configuration>()||bytes>16384)throw new IOException("UPDATER_SERVICE_CONFIG");
            var memory=Marshal.AllocHGlobal((int)bytes);
            try
            {
                if(!QueryServiceConfigW(service,memory,bytes,out _))throw new Win32Exception();
                var config=Marshal.PtrToStructure<Configuration>(memory);
                return new(status.State,status.Pid,Marshal.PtrToStringUni(config.Account)??"",Marshal.PtrToStringUni(config.Image)??"");
            }
            finally{Marshal.FreeHGlobal(memory);}
        }
        finally{if(service!=IntPtr.Zero)CloseServiceHandle(service);CloseServiceHandle(manager);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct Configuration{public uint Type,Start,Error;public IntPtr Image,Group;public uint Tag;public IntPtr Dependencies,Account,Display;}
    [StructLayout(LayoutKind.Sequential)]private struct StatusProcess{public uint Type,State,Accepted,Win32,Specific,Checkpoint,WaitHint,Pid,Flags;}
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe,out uint process);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenSCManagerW(string? machine,string? database,uint rights);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenServiceW(IntPtr manager,string name,uint rights);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceStatusEx(IntPtr service,uint level,out StatusProcess status,uint bytes,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryServiceConfigW(IntPtr service,IntPtr memory,uint bytes,out uint needed);
    [DllImport("advapi32.dll")]private static extern bool CloseServiceHandle(IntPtr service);
}

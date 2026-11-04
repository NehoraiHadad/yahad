using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
namespace Yahad.Updater;

// One instance for the life of the service, so nobody else can ever hold the name.
// The DACL admits SYSTEM and the Runtime; each client's token is checked again.
// A client's fault closes its connection and nothing else. Once the right client has
// asked, a failure is this service's own: it answers INTERNAL and reports the cause.
internal sealed class UpdaterPipe(string name,SecurityIdentifier allowed,string descriptor,Func<UpdaterRequest,UpdaterReply> handle,Action<Exception> report)
{
    private static readonly TimeSpan Timeout=TimeSpan.FromSeconds(5);
    static UpdaterPipe()=>Marshal.PrelinkAll(typeof(Native));
    internal async Task RunAsync(CancellationToken cancellation)
    {
        using var server=LocalControlPipe.Create(name,descriptor);
        while(true)
        {
            await server.WaitForConnectionAsync(cancellation);
            try{await ServeAsync(server,cancellation);}
            catch(Exception error)when(!cancellation.IsCancellationRequested&&ClientFault(error)){}
            try{server.Disconnect();}catch(IOException){}
        }
    }
    // Bytes that do not parse, a stream that broke or stalled, a refused account. A file
    // that will not load is an IOException too, and it is never the client's doing.
    private static bool ClientFault(Exception error)=>error is OperationCanceledException or UnauthorizedAccessException{Message:"UPDATER_PEER"}or IOException and not(FileNotFoundException or FileLoadException);
    private byte[] Answer(UpdaterRequest request)
    {
        try{return UpdaterProtocol.Frame(handle(request));}
        catch(Exception error){report(error);return UpdaterProtocol.Frame(new UpdaterReply(1,false,"INTERNAL"));}
    }
    private async Task ServeAsync(NamedPipeServerStream server,CancellationToken cancellation)
    {
        using var read=CancellationTokenSource.CreateLinkedTokenSource(cancellation);read.CancelAfter(Timeout);
        var request=await UpdaterProtocol.ReadAsync<UpdaterRequest>(server,read.Token);
        if(!Peer(server).Equals(allowed))throw new UnauthorizedAccessException("UPDATER_PEER");
        var reply=Answer(request);
        using var write=CancellationTokenSource.CreateLinkedTokenSource(cancellation);write.CancelAfter(Timeout);
        await server.WriteAsync(reply,write.Token);await server.FlushAsync(write.Token);
        // Disconnecting discards unread data; the client's close says it has the reply.
        _=await server.ReadAsync(new byte[1],write.Token);
    }
    // The client grants Identification only, so while this thread wears its token no
    // file can be opened, and a first use of any managed type that loads an assembly
    // fails. The token therefore leaves through bound native calls alone, and is read
    // after reverting. Impersonation needs the first read to have completed.
    private static SecurityIdentifier Peer(NamedPipeServerStream server)
    {
        var token=IntPtr.Zero;var opened=false;
        if(Native.ImpersonateNamedPipeClient(server.SafePipeHandle))
        {
            opened=Native.OpenThreadToken(Native.GetCurrentThread(),8,true,out token);
            if(!Native.RevertToSelf())Environment.FailFast("UPDATER_REVERT");
        }
        if(!opened)throw new UnauthorizedAccessException("UPDATER_PEER");
        using var owned=new SafeAccessTokenHandle(token);using var identity=new WindowsIdentity(owned.DangerousGetHandle());
        return identity.User??throw new UnauthorizedAccessException("UPDATER_PEER");
    }
    private static class Native
    {
        [DllImport("advapi32.dll")][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool ImpersonateNamedPipeClient(SafePipeHandle pipe);
        [DllImport("advapi32.dll")][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool OpenThreadToken(IntPtr thread,uint access,[MarshalAs(UnmanagedType.Bool)]bool openAsSelf,out IntPtr token);
        [DllImport("advapi32.dll")][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool RevertToSelf();
        [DllImport("kernel32.dll")]internal static extern IntPtr GetCurrentThread();
    }
}

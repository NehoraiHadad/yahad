using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using Yahad.Install;
namespace Yahad.Updater;

// One instance for the life of the service, so nobody else can ever hold the name.
// The DACL admits SYSTEM and the Runtime; each client's token is checked again.
internal sealed class UpdaterPipe(string name,SecurityIdentifier allowed,string descriptor,Func<UpdaterRequest,UpdaterReply> handle)
{
    private static readonly TimeSpan Timeout=TimeSpan.FromSeconds(5);
    internal async Task RunAsync(CancellationToken cancellation)
    {
        using var server=LocalControlPipe.Create(name,descriptor);
        while(true)
        {
            await server.WaitForConnectionAsync(cancellation);
            // A misbehaving client costs only its own connection.
            try{await ServeAsync(server,cancellation);}
            catch(Exception error)when(!cancellation.IsCancellationRequested&&error is IOException or UnauthorizedAccessException or OperationCanceledException or Win32Exception){}
            try{server.Disconnect();}catch(IOException){}
        }
    }
    private async Task ServeAsync(NamedPipeServerStream server,CancellationToken cancellation)
    {
        using var read=CancellationTokenSource.CreateLinkedTokenSource(cancellation);read.CancelAfter(Timeout);
        var request=await UpdaterProtocol.ReadAsync<UpdaterRequest>(server,read.Token);
        // Impersonation needs the first read to have completed. The client's
        // Identification-level token is enough to name its user.
        SecurityIdentifier? peer=null;
        server.RunAsClient(()=>{using var identity=WindowsIdentity.GetCurrent(TokenAccessLevels.Query);peer=identity.User;});
        if(peer is null||!peer.Equals(allowed))throw new UnauthorizedAccessException("UPDATER_PEER");
        var reply=handle(request);
        using var write=CancellationTokenSource.CreateLinkedTokenSource(cancellation);write.CancelAfter(Timeout);
        await UpdaterProtocol.WriteAsync(server,reply,write.Token);
        // Disconnecting discards unread data; the client's close says it has the reply.
        _=await server.ReadAsync(new byte[1],write.Token);
    }
}

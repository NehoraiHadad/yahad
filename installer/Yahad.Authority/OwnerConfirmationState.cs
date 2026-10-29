using System.Text.Json;
namespace Yahad.Authority;

// The WPF view and private tests share this one exact-operation state machine.
// Answers carry no SID, credential, path, operation document or authority claims.
internal sealed class OwnerConfirmationState
{
    internal OperationConfirmation Snapshot{get;}internal string State{get;private set;}="ready";
    private readonly Func<DateTimeOffset> clock;private int answered;
    internal OwnerConfirmationState(OperationConfirmation snapshot,string expectedInstallation,Func<DateTimeOffset> clock)
    {
        if(snapshot.Format!=1||snapshot.InstallationId!=expectedInstallation||snapshot.Scope is not("activate" or "recover" or "delegate"))throw new IOException("OWNER_VIEW_BINDING");
        Yahad.Service.InstalledActivationResolver.Id(snapshot.RequestId);Yahad.Service.InstalledActivationResolver.Id(snapshot.Challenge);Yahad.Service.InstalledActivationResolver.Digest(snapshot.Digest);
        if(snapshot.ExpiresUnix<=clock().ToUnixTimeSeconds())throw new IOException("OWNER_VIEW_EXPIRED");Snapshot=snapshot;this.clock=clock;
    }
    internal async Task<EndpointReply> AnswerAsync(bool approve,Func<AuthorityFrame,CancellationToken,Task<EndpointReply>> send,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();if(clock().ToUnixTimeSeconds()>=Snapshot.ExpiresUnix)throw new IOException("OWNER_VIEW_EXPIRED");
        if(Interlocked.CompareExchange(ref answered,1,0)!=0)throw new IOException("OWNER_VIEW_ALREADY_ANSWERED");State="sending";
        var answer=new OwnerAnswer(1,Snapshot.Challenge,Snapshot.Digest,approve);
        var frame=new AuthorityFrame(1,Guid.NewGuid().ToString("N"),"answer",JsonSerializer.SerializeToElement(answer));
        try{var reply=await send(frame,cancellation);if(reply.Id!=frame.Id)throw new IOException("OWNER_REPLY_BINDING");State=approve?"submitted":"denied";return reply;}
        catch{State="outcome_unknown";throw;} // Query status, never resend an uncertain approval.
    }
    internal void Close(){if(Interlocked.CompareExchange(ref answered,1,0)==0)State="closed";}
}

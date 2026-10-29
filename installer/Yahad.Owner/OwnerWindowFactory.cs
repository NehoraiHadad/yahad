using System.IO;
using Yahad.Authority;
namespace Yahad.Owner;

// Future installed shortcut entry calls this fixed transport. The current App
// never invokes it. Private tests exercise its state model without opening UI.
internal static class OwnerWindowFactory
{
    internal static async Task<ApprovalWindow> ReadAsync(string installation,CancellationToken cancellation)
    {
        var frame=new AuthorityFrame(1,Guid.NewGuid().ToString("N"),"confirmation",System.Text.Json.JsonSerializer.SerializeToElement(new{}));
        var reply=await WindowsAuthorityEndpoint.SendOwnerAsync(installation,frame,cancellation);
        var snapshot=reply.Confirmation??throw new IOException("OWNER_NO_PENDING_CONFIRMATION");
        var state=new OwnerConfirmationState(snapshot,installation,()=>DateTimeOffset.UtcNow);
        return new(state,(answer,token)=>WindowsAuthorityEndpoint.SendOwnerAsync(installation,answer,token));
    }
}

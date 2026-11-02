using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

// Narrow unprivileged Platform entry. Source, executable, service names, keys,
// enrollment and owner identity cannot be supplied in this command.
public static class RuntimeUpdateBridge
{
    public static async Task<JsonElement> ExecuteAsync(byte[] request,CancellationToken cancellation)
    {
        if(request.Length is <2 or >4096)throw new IOException("RUNTIME_UPDATE_BOUND");
        var input=NativeJson.Read<JsonElement>(request);var kind=input.GetProperty("kind").GetString();
        var binding=new WindowsInstallationReader().Read();
        var id=Guid.NewGuid().ToString("N");AuthorityFrame frame;
        if(kind=="status")
        {
            if(input.EnumerateObject().Count()!=1)throw new IOException("RUNTIME_UPDATE_FIELDS");
            frame=new(1,id,"status",JsonSerializer.SerializeToElement(new{}));
        }
        else if(kind is "submit" or "defer")
        {
            var command=input.GetProperty("command");if(input.EnumerateObject().Count()!=2)throw new IOException("RUNTIME_UPDATE_FIELDS");
            var text=command.GetProperty("id").GetString();if(!Guid.TryParseExact(text,"D",out var parsed)||parsed.ToString("D")!=text)throw new IOException("RUNTIME_UPDATE_ID");id=parsed.ToString("N");
            var action=kind=="defer"?"defer":command.GetProperty("action").GetString();var mode=action=="set_mode"?command.GetProperty("mode").GetString():null;
            string? target=null;
            if(action=="install")target=command.GetProperty("target").GetProperty("fingerprint").GetString();
            var wire=new AuthorityRequest(1,id,action!,target,mode);if(kind!="defer")_=AuthorityProtocol.Request(JsonSerializer.SerializeToUtf8Bytes(wire));
            var allowed=new HashSet<string>{"id","familyId","userId"};if(kind=="defer")allowed.Add("untilUnix");else allowed.Add("action");if(action=="set_mode")allowed.Add("mode");if(action=="install")allowed.Add("target");
            if(command.EnumerateObject().Any(p=>!allowed.Contains(p.Name)))throw new IOException("RUNTIME_UPDATE_FIELDS");
            // The authenticated app enforces its designated parent session. Bind
            // that selection again to Authority's protected enrollment metadata.
            var observed=await WindowsAuthorityEndpoint.SendRuntimeAsync(binding.InstallationId,new(1,Guid.NewGuid().ToString("N"),"status",JsonSerializer.SerializeToElement(new{})),cancellation);
            if(observed.View is not{} v||command.GetProperty("familyId").GetString()!=v.FamilyId||command.GetProperty("userId").GetString()!=v.UserId)throw new UnauthorizedAccessException("RUNTIME_UPDATE_OPERATOR");
            frame=kind=="defer"?new(1,id,"defer",JsonSerializer.SerializeToElement(new{UntilUnix=command.GetProperty("untilUnix").GetInt64()})):new(1,id,"request",JsonSerializer.SerializeToElement(wire));
        }
        else throw new IOException("RUNTIME_UPDATE_KIND");
        var reply=await WindowsAuthorityEndpoint.SendRuntimeAsync(binding.InstallationId,frame,cancellation);
        if(kind is "submit" or "defer")return JsonSerializer.SerializeToElement(new{state=reply.State switch{"queued" or "pending" or "approval_required"=>reply.State,"observed" or "recorded"=>"queued",_=>throw new IOException("RUNTIME_UPDATE_RESULT")}});
        return Status(reply);
    }
    internal static JsonElement Status(EndpointReply reply)
    {
        var view=reply.View??throw new IOException("RUNTIME_UPDATE_UNCONFIGURED");
        return JsonSerializer.SerializeToElement(new
        {
            enabled=view.Enabled,mode=reply.Mode,modeControlAllowed=view.ModeControlAllowed,
            profile=reply.SystemGrant?.Profile,systemGrant=reply.SystemGrant is{} system?new{state=system.State,untilRevoked=system.UntilRevoked,scopes=system.Scopes}:null,
            delegation=reply.Delegation is{} d?new{state=d.State,expiresUnix=d.ExpiresUnix,remainingOperations=d.RemainingOperations}:null,
            @operator=new{familyId=view.FamilyId,userId=view.UserId},phase=reply.Phase=="stopped"?"idle":reply.Phase,
            currentVersion=view.CurrentVersion,latestVersion=reply.Target?.Version,
            installTarget=reply.Target is{} t?new{version=t.Version,fingerprint=t.Fingerprint}:null,
            lastRequest=reply.LastRequest,lastModeRequest=reply.LastModeRequest,lastUpdateAt=view.LastUpdateUnix*1000,deferredUntil=view.DeferredUntilUnix*1000,
            checkedAt=view.CheckedAt,updatedAt=view.UpdatedAt,error=view.Error switch{"update_failed" or "quarantined"=>"health","approval_required"=>"configuration","storage"=>"storage","maintenance"=>"maintenance",null=>null,_=>"internal"}
        });
    }
}

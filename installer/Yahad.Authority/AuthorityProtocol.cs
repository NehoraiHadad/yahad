using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record AuthorityRequest(int Format,string Id,string Action,string? TargetDigest,string? Mode);
internal sealed record OwnerAnswer(int Format,string Challenge,string OperationDigest,bool Approve);
internal sealed record AuthorityPeer(string Sid,int ProcessId,long Created,int Session,bool Interactive);
internal sealed record OperationFacts(string CurrentVersion,string TargetVersion,string Channel,string Origin,long PayloadBytes,string IntakePolicyDigest,string IntakePinDigest);
internal static class AuthorityProtocol
{
    internal const int MaximumFrame=32768;
    internal static AuthorityRequest Request(byte[] frame)
    {
        var request=Read<AuthorityRequest>(frame);InstalledActivationResolver.Id(request.Id);
        if(request.Format!=1||request.Action is not("check" or "install" or "set_mode")||
            (request.Action=="install")!=(request.TargetDigest is not null)||
            (request.Action=="set_mode")!=(request.Mode is not null)||request.Mode is not(null or "auto" or "notify"))throw new IOException("AUTHORITY_REQUEST");
        if(request.TargetDigest is not null)InstalledActivationResolver.Digest(request.TargetDigest);return request;
    }
    internal static OwnerAnswer Answer(byte[] frame)
    {var a=Read<OwnerAnswer>(frame);InstalledActivationResolver.Id(a.Challenge);InstalledActivationResolver.Digest(a.OperationDigest);if(a.Format!=1)throw new IOException("AUTHORITY_FRAME_VERSION");return a;}
    private static T Read<T>(byte[] frame)
    {if(frame.Length is <2 or >MaximumFrame)throw new IOException("AUTHORITY_FRAME_BOUND");return NativeJson.Read<T>(frame);}
    internal static string Digest<T>(T value)=>InstalledActivationResolver.Hash(JsonSerializer.SerializeToUtf8Bytes(value));
}

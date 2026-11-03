using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace Yahad.Install;

// The local contract between Platform and the Updater service: a 4-byte
// little-endian length, then at most 4096 bytes of UTF-8 JSON. One request and one
// reply per connection. The status names are the ones src/updates.ts accepts.
internal sealed record UpdaterRequest(int V,string Op,string? Id=null,string? Version=null,string? Fingerprint=null,string? Mode=null);
internal sealed record UpdaterTarget(string Version,string Fingerprint);
internal sealed record UpdaterLastRequest(string Id,string Action,string State,long StartedAt,long? FinishedAt,string? Error,UpdaterTarget? Target);
internal sealed record UpdaterLastMode(string Id,string Mode,string State,string? Error=null);
internal sealed record UpdaterStatus(bool Enabled,string Mode,string Phase,string CurrentVersion,string? LatestVersion,UpdaterTarget? InstallTarget,long? CheckedAt,long UpdatedAt,string? Error,UpdaterLastRequest? LastRequest,UpdaterLastMode? LastModeRequest);
internal sealed record UpdaterReply(int V,bool Ok,string? Code=null,UpdaterStatus? Status=null);
// updater\state.json. Setup seeds the mode from the administrator's choice; from
// then on only the Updater writes it, and it survives an upgrade and a rollback.
internal sealed record UpdaterRecord(int Format,string Mode,string? Phase=null,UpdaterLastRequest? LastRequest=null,UpdaterLastMode? LastModeRequest=null,long? CheckedAt=null,long? LastUpdateAt=null,string? Latest=null,string? Error=null,string? RefusedVersion=null)
{
    internal static UpdaterRecord Seed(bool automatic)=>new(1,automatic?UpdaterProtocol.Auto:UpdaterProtocol.Notify);
    internal byte[] Encode()=>JsonSerializer.SerializeToUtf8Bytes(this,UpdaterProtocol.Options);
    internal static UpdaterRecord Parse(byte[] bytes)
    {
        try
        {
            var value=JsonSerializer.Deserialize<UpdaterRecord>(bytes,UpdaterProtocol.Options);
            if(value is {Format:1,Mode:UpdaterProtocol.Auto or UpdaterProtocol.Notify})return value;
        }
        catch(JsonException){}
        throw new IOException("UPDATER_STATE_INVALID");
    }
}
internal static class UpdaterProtocol
{
    internal const string PipeName="Yahad.Updater",Auto="auto",Notify="notify";
    internal const int MaximumFrame=4096;
    internal static readonly JsonSerializerOptions Options=new()
    {MaxDepth=4,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,AllowDuplicateProperties=false};
    internal static bool IsId(string? value)=>value is not null&&Guid.TryParseExact(value,"D",out var id)&&id.ToString("D")==value;
    internal static bool IsFingerprint(string? value)=>value is not null&&Regex.IsMatch(value,@"\A[a-f0-9]{64}\z");
    internal static async Task WriteAsync<T>(Stream stream,T value,CancellationToken cancellation)
    {
        var body=JsonSerializer.SerializeToUtf8Bytes(value,Options);if(body.Length>MaximumFrame)throw new IOException("UPDATER_FRAME");
        await stream.WriteAsync((byte[])[..BitConverter.GetBytes(body.Length),..body],cancellation);await stream.FlushAsync(cancellation);
    }
    // The length is checked before anything is allocated, and the body is parsed
    // only once all of it arrived.
    internal static async Task<T> ReadAsync<T>(Stream stream,CancellationToken cancellation)
    {
        var header=new byte[4];await stream.ReadExactlyAsync(header,cancellation);var length=BitConverter.ToInt32(header);
        if(length is <1 or >MaximumFrame)throw new IOException("UPDATER_FRAME");
        var body=new byte[length];await stream.ReadExactlyAsync(body,cancellation);
        try{return JsonSerializer.Deserialize<T>(body,Options)??throw new IOException("UPDATER_FRAME");}
        catch(JsonException){throw new IOException("UPDATER_FRAME");}
    }
}

using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal static class NativeRequestProjection
{
    internal static JsonElement? Read(InstalledStateFiles files,bool mode)
    {
        try{return ReadAvailable(files,mode);}
        catch(System.ComponentModel.Win32Exception error)when(error.NativeErrorCode is 32 or 33){return null;}
    }
    private static JsonElement? ReadAvailable(InstalledStateFiles files,bool mode)
    {
        var paths=Directory.EnumerateFiles(files.PathOf(".authority"),"rpc-*.json").Take(1001).ToArray();
        if(paths.Length>1000)throw new IOException("AUTHORITY_REQUEST_BOUND");
        foreach(var path in paths.OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var record=NativeJson.Read<JsonElement>(files.Read(path,32768));var action=record.GetProperty("Action").GetString();
            if((action=="set_mode")!=mode)continue;
            var id=Path.GetFileNameWithoutExtension(path)[4..];InstalledActivationResolver.Id(id);
            var donePath=files.PathOf(".authority/done-"+id+".json");JsonElement? done=File.Exists(donePath)?NativeJson.Read<JsonElement>(files.Read(donePath,32768)):null;
            var phase=done?.GetProperty("Phase").GetString();var uuid=Guid.ParseExact(id,"N").ToString("D");
            if(mode)return JsonSerializer.SerializeToElement(new{id=uuid,mode=record.GetProperty("Mode").GetString(),state=done is null?"pending":phase is "cancelled" or "recovery_required"?"rejected":"applied",error=phase is "cancelled" or "recovery_required"?"local_setup":null});
            JsonElement? target=null;
            if(record.TryGetProperty("Catalog",out var catalog))target=JsonSerializer.SerializeToElement(new{version=catalog.GetProperty("Facts").GetProperty("TargetVersion").GetString(),fingerprint=record.GetProperty("Target").GetString()});
            else if(record.TryGetProperty("Profile",out var profile)&&profile.GetString()==FullSystemUpdatePolicy.Profile&&record.TryGetProperty("TargetVersion",out var version)&&record.TryGetProperty("Target",out var fingerprint))target=JsonSerializer.SerializeToElement(new{version=version.GetString(),fingerprint=fingerprint.GetString()});
            var failed=done is{} completedError&&completedError.TryGetProperty("Error",out var failure)&&failure.ValueKind==JsonValueKind.String;
            return JsonSerializer.SerializeToElement(new{id=uuid,action=action=="install"?"install":"check",state=done is null?"running":!failed&&(phase is "healthy" or "available"||action=="check"&&phase=="idle")?"succeeded":"failed",
                startedAt=new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(),finishedAt=done is null?(long?)null:new DateTimeOffset(File.GetLastWriteTimeUtc(donePath)).ToUnixTimeMilliseconds(),
                error=done is{} completed&&completed.TryGetProperty("Error",out var code)?code.GetString():done is not null&&phase is not("healthy" or "available" or "idle")?"health":null,target});
        }
        return null;
    }
}

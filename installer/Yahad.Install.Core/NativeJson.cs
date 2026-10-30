using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yahad.Install;

internal sealed record HostLeaseGrant(int Format,int ProcessId,ulong Handle,string DataDirectory,string Nonce);
internal static class NativeJson
{
    private static readonly JsonSerializerOptions Strict=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,RespectRequiredConstructorParameters=true};
    internal static T Read<T>(byte[] bytes)
    {
        using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=48});
        void Check(JsonElement value)
        {
            if(value.ValueKind==JsonValueKind.Object)
            {var names=new HashSet<string>(StringComparer.Ordinal);foreach(var property in value.EnumerateObject()){if(!names.Add(property.Name))throw new IOException("NATIVE_DUPLICATE_FIELD");Check(property.Value);}}
            else if(value.ValueKind==JsonValueKind.Array)foreach(var child in value.EnumerateArray())Check(child);
        }
        Check(document.RootElement);return JsonSerializer.Deserialize<T>(bytes,Strict)??throw new IOException("NATIVE_JSON_NULL");
    }
}

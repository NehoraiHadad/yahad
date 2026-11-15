using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yahad.Install;

internal sealed record HostLeaseGrant(int Format,int ProcessId,ulong Handle,string DataDirectory,string Nonce);
internal static class NativeJson
{
    private static readonly JsonSerializerOptions Strict=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,RespectRequiredConstructorParameters=true};
    // The camelCase records of docs/WINDOWS-APP-PACKAGE-PLAN.md (active.json, the tree
    // records): the same strictness, exact camelCase names, nulls refused.
    private static readonly JsonSerializerOptions StrictCamel=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,RespectRequiredConstructorParameters=true,
        RespectNullableAnnotations=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    internal static T Read<T>(byte[] bytes)=>Read<T>(bytes,Strict);
    internal static T ReadCamel<T>(byte[] bytes)=>Read<T>(bytes,StrictCamel);
    internal static byte[] WriteCamel<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,StrictCamel);
    private static T Read<T>(byte[] bytes,JsonSerializerOptions options)
    {
        using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=48});
        void Check(JsonElement value)
        {
            if(value.ValueKind==JsonValueKind.Object)
            {var names=new HashSet<string>(StringComparer.Ordinal);foreach(var property in value.EnumerateObject()){if(!names.Add(property.Name))throw new IOException("NATIVE_DUPLICATE_FIELD");Check(property.Value);}}
            else if(value.ValueKind==JsonValueKind.Array)foreach(var child in value.EnumerateArray())Check(child);
        }
        Check(document.RootElement);return JsonSerializer.Deserialize<T>(bytes,options)??throw new IOException("NATIVE_JSON_NULL");
    }
}

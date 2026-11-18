using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Yahad.Install;

internal sealed record HostLeaseGrant(int Format,int ProcessId,ulong Handle,string DataDirectory,string Nonce);
// The native records' two strictness variants, compiled ahead of time (no reflection
// metadata at run time). Each assembly that owns native records declares its own
// context with exactly one of these option sets; NativeJson refuses any other.
[JsonSourceGenerationOptions(UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,RespectRequiredConstructorParameters=true)]
[JsonSerializable(typeof(HostLeaseGrant))]
internal sealed partial class NativeJsonContext:JsonSerializerContext;
// The camelCase records of docs/WINDOWS-APP-PACKAGE-PLAN.md (active.json, the tree
// records): the same strictness, exact camelCase names, nulls refused.
[JsonSourceGenerationOptions(UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,RespectRequiredConstructorParameters=true,
    RespectNullableAnnotations=true,PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ActivePointer))][JsonSerializable(typeof(TreeRecord))]
[JsonSerializable(typeof(HomeNameCredential))][JsonSerializable(typeof(HomeNameAddress))][JsonSerializable(typeof(HomeNameChallenge))]
[JsonSerializable(typeof(HomeNameAccount))][JsonSerializable(typeof(HomeNameSwapJournal))]
[JsonSerializable(typeof(HomeNameRegistration))][JsonSerializable(typeof(HomeNameError))]
internal sealed partial class NativeCamelJsonContext:JsonSerializerContext;
internal static class NativeJson
{
    internal static T Read<T>(byte[] bytes,JsonTypeInfo<T> type)=>Parse(bytes,Strict(type,camel:false));
    internal static T ReadCamel<T>(byte[] bytes,JsonTypeInfo<T> type)=>Parse(bytes,Strict(type,camel:true));
    internal static byte[] WriteCamel<T>(T value,JsonTypeInfo<T> type)=>JsonSerializer.SerializeToUtf8Bytes(value,Strict(type,camel:true));
    // A context declared with other options is a programming error, never input.
    internal static JsonTypeInfo<T> Strict<T>(JsonTypeInfo<T> type,bool camel)
    {
        var options=type.Options;
        if(options.UnmappedMemberHandling!=JsonUnmappedMemberHandling.Disallow||!options.RespectRequiredConstructorParameters||options.RespectNullableAnnotations!=camel||
            options.PropertyNamingPolicy!=(camel?JsonNamingPolicy.CamelCase:null)||options.AllowDuplicateProperties!=true||options.MaxDepth!=0||options.AllowTrailingCommas||
            options.ReadCommentHandling!=JsonCommentHandling.Disallow||options.NumberHandling!=JsonNumberHandling.Strict||options.PropertyNameCaseInsensitive||options.Converters.Count!=0)
            throw new InvalidOperationException("NATIVE_JSON_OPTIONS");
        return type;
    }
    private static T Parse<T>(byte[] bytes,JsonTypeInfo<T> type)
    {
        using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=48});
        void Check(JsonElement value)
        {
            if(value.ValueKind==JsonValueKind.Object)
            {var names=new HashSet<string>(StringComparer.Ordinal);foreach(var property in value.EnumerateObject()){if(!names.Add(property.Name))throw new IOException("NATIVE_DUPLICATE_FIELD");Check(property.Value);}}
            else if(value.ValueKind==JsonValueKind.Array)foreach(var child in value.EnumerateArray())Check(child);
        }
        Check(document.RootElement);return JsonSerializer.Deserialize(bytes,type)??throw new IOException("NATIVE_JSON_NULL");
    }
}

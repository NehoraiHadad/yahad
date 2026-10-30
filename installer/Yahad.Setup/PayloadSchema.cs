using System.IO;
using System.Text.Json;
namespace Yahad.Setup;

internal static class PayloadSchema
{
    // The build record is inside the byte-verified embedded payload. Schema
    // admission still checks the actual SQLite header before mutable startup.
    internal static int Read(JsonElement verifiedBuild)
    {
        if(!verifiedBuild.TryGetProperty("databaseSchema",out var value)||value.ValueKind!=JsonValueKind.Number||!value.TryGetInt32(out var schema)||schema is <1 or >64)
            throw new IOException("PAYLOAD_SCHEMA");
        return schema;
    }
}

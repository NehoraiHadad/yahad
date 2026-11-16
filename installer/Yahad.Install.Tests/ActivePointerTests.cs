using System.Text;
using Yahad.Install;

internal static class ActivePointerTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        void Assert(bool value) { if (!value) throw new Exception("assertion failed"); }
        void Refuse(Action action)
        { try { action(); } catch (IOException error) when (error.Message == "ACTIVE_POINTER_INVALID") { return; } throw new Exception("expected ACTIVE_POINTER_INVALID"); }
        var digest = new string('a', 64); var other = new string('b', 64);
        var min = InfrastructureRelease.ContractMin; var max = InfrastructureRelease.ContractMax;
        ActivePointer Good() => new(1, new ActiveInfrastructure(InfrastructureRelease.Version, min, max, digest), new ActiveApp("2.3.4", min, other));
        string Json(string? infrastructure = null, string? app = null, string top = "\"format\":1")
            => "{" + top + ",\"infrastructure\":{" + (infrastructure ?? "\"version\":\"1.0.0\",\"contractMin\":1,\"contractMax\":1,\"treeSha256\":\"" + digest + "\"") +
               "},\"app\":{" + (app ?? "\"version\":\"2.3.4\",\"requiresContract\":1,\"treeSha256\":\"" + other + "\"") + "}}";
        ActivePointer Parse(string json) => ActivePointer.Parse(Encoding.UTF8.GetBytes(json));
        await check("active pointer encodes and parses back to the same camelCase record", () =>
        {
            var bytes = Good().Encode(); var text = Encoding.UTF8.GetString(bytes);
            Assert(bytes.Length <= ActivePointer.MaximumBytes && text.Contains("\"format\":1") && text.Contains("\"treeSha256\"") && text.Contains("\"requiresContract\""));
            var parsed = ActivePointer.Parse(bytes);
            Assert(parsed.Format == 1 && parsed.Infrastructure == Good().Infrastructure && parsed.App == Good().App);
            Assert(Parse(Json()).App.Version == "2.3.4"); return Task.CompletedTask;
        });
        await check("active pointer refuses empty, oversized and malformed bytes", () =>
        {
            Refuse(() => ActivePointer.Parse([])); Refuse(() => ActivePointer.Parse(new byte[ActivePointer.MaximumBytes + 1]));
            Refuse(() => Parse("{")); Refuse(() => Parse("null")); Refuse(() => Parse("[]"));
            return Task.CompletedTask;
        });
        await check("active pointer accepts exactly 4096 bytes and no more", () =>
        {
            var json = Json(); var padded = json + new string(' ', ActivePointer.MaximumBytes - json.Length);
            Assert(Encoding.UTF8.GetByteCount(padded) == ActivePointer.MaximumBytes && Parse(padded).App.Version == "2.3.4");
            Refuse(() => Parse(padded + " ")); return Task.CompletedTask;
        });
        await check("active pointer refuses unknown, duplicate and missing fields", () =>
        {
            Refuse(() => Parse(Json(top: "\"format\":1,\"extra\":1")));
            Refuse(() => Parse(Json(top: "\"format\":1,\"format\":1")));
            Refuse(() => Parse(Json(infrastructure: "\"version\":\"1.0.0\",\"contractMin\":1,\"contractMax\":1,\"contractMax\":1,\"treeSha256\":\"" + digest + "\"")));
            Refuse(() => Parse(Json(app: "\"version\":\"2.3.4\",\"requiresContract\":1,\"treeSha256\":\"" + other + "\",\"extra\":true")));
            Refuse(() => Parse(Json(app: "\"version\":\"2.3.4\",\"treeSha256\":\"" + other + "\"")));
            Refuse(() => Parse("{\"format\":1,\"infrastructure\":null,\"app\":null}"));
            Refuse(() => Parse("{\"format\":1,\"Infrastructure\":{},\"app\":{}}"));
            return Task.CompletedTask;
        });
        await check("active pointer refuses a wrong format, version, contract or digest", () =>
        {
            Refuse(() => Parse(Json(top: "\"format\":2")));
            Refuse(() => (Good() with { Format = 0 }).Encode());
            Refuse(() => (Good() with { App = Good().App with { Version = "1.0" } }).Encode());
            Refuse(() => (Good() with { Infrastructure = Good().Infrastructure with { Version = "01.0.0" } }).Encode());
            Refuse(() => (Good() with { App = Good().App with { RequiresContract = max + 1 } }).Encode());
            Refuse(() => (Good() with { App = Good().App with { RequiresContract = min - 1 } }).Encode());
            Refuse(() => (Good() with { Infrastructure = Good().Infrastructure with { ContractMin = 0 } }).Encode());
            Refuse(() => (Good() with { Infrastructure = Good().Infrastructure with { ContractMax = 1025 } }).Encode());
            Refuse(() => (Good() with { Infrastructure = Good().Infrastructure with { ContractMin = max + 1, ContractMax = max } }).Encode());
            foreach (var bad in new[] { "", new string('a', 63), new string('a', 65), new string('A', 64), new string('g', 64) })
            {
                Refuse(() => (Good() with { Infrastructure = Good().Infrastructure with { TreeSha256 = bad } }).Encode());
                Refuse(() => (Good() with { App = Good().App with { TreeSha256 = bad } }).Encode());
                Refuse(() => Parse(Json(app: "\"version\":\"2.3.4\",\"requiresContract\":1,\"treeSha256\":\"" + bad + "\"")));
            }
            return Task.CompletedTask;
        });
        await check("active pointer binds only the infrastructure that reads it", () =>
        {
            Good().RequireThisInfrastructure();
            Refuse(() => (Good() with { Infrastructure = Good().Infrastructure with { Version = "9.9.9" } }).RequireThisInfrastructure());
            Refuse(() => (Good() with { Infrastructure = Good().Infrastructure with { ContractMax = max + 1 } }).RequireThisInfrastructure());
            Refuse(() => (Good() with { Infrastructure = Good().Infrastructure with { ContractMin = min + 1, ContractMax = max + 1 }, App = Good().App with { RequiresContract = max + 1 } }).RequireThisInfrastructure());
            // A wider pointer range that holds the app's contract is still not this infrastructure's range.
            Refuse(() => (Good() with { Infrastructure = Good().Infrastructure with { ContractMax = max + 1 }, App = Good().App with { RequiresContract = max + 1 } }).RequireThisInfrastructure());
            return Task.CompletedTask;
        });
    }
}

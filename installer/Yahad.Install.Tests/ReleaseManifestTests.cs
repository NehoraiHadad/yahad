using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Yahad.Install;

// docs/WINDOWS-APP-PACKAGE-PLAN.md 5.1 and 9.1: the format-2 release manifest, its v2
// signature, and the track selection the Updater and the Setup engine share.
internal static class ReleaseManifestTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        void Assert(bool value, string what) { if (!value) throw new Exception("assertion failed: " + what); }
        void Refuse(string code, Action action, string what)
        { try { action(); } catch (IOException error) when (error.Message == code) { return; } throw new Exception("expected " + code + ": " + what); }
        string Digest(char c) => new(c, 64);
        string Commit(char c) => new(c, 40);
        using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256); var spki = signing.ExportSubjectPublicKeyInfo();
        byte[] SignWith(string prefix, byte[] manifest) => signing.SignData([.. Encoding.ASCII.GetBytes(prefix), .. manifest], HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        JsonObject Fields(string version = "0.7.1") => new()
        {
            ["format"] = 2, ["product"] = "yahad-windows-release", ["version"] = version, ["sourceCommit"] = Commit('a'), ["keyId"] = TrustedReleaseKey.Id(spki), ["createdUnix"] = 1700000000,
            ["infrastructure"] = new JsonObject
            {
                ["version"] = "1.0.0", ["contractMin"] = 1, ["contractMax"] = 2, ["sourceCommit"] = Commit('b'), ["treeSha256"] = Digest('c'),
                ["setup"] = new JsonObject { ["name"] = "Yahad.Setup-" + version + ".exe", ["sha256"] = Digest('d'), ["bytes"] = 10 }
            },
            ["app"] = new JsonObject
            {
                ["archive"] = new JsonObject { ["name"] = "yahad-windows-app-" + version + ".zip", ["sha256"] = Digest('e'), ["bytes"] = 20 },
                ["treeSha256"] = Digest('f'), ["requiresContract"] = 1, ["databaseSchema"] = 6, ["databaseMigrationsFrom"] = new JsonArray(3, 5)
            },
            ["minimumFrom"] = new JsonObject { ["app"] = "0.0.0", ["infrastructure"] = "0.0.0" }
        };
        byte[] Encode(JsonNode value) => Encoding.UTF8.GetBytes(value.ToJsonString());
        ReleaseManifest Parse(JsonNode value) => ReleaseManifest.Parse(Encode(value));
        JsonObject With(Action<JsonObject> change) { var value = Fields(); change(value); return value; }
        JsonObject Infra(JsonObject value) => value["infrastructure"]!.AsObject();
        JsonObject App(JsonObject value) => value["app"]!.AsObject();

        await check("semver: canonical cores only, compared by number", () =>
        {
            foreach (var good in new[] { "0.0.0", "1.2.3", "10.20.30" }) Assert(SemVer.IsVersion(good), good);
            foreach (var bad in new[] { "", "1.2", "1.2.3.4", "01.2.3", "1.02.3", "1.2.03", "v1.2.3", "1.2.3-rc1", "1.2.3+x", " 1.2.3", "1.2.3\n", "-1.2.3", "1.2.3" + new string('0', 38) })
                Assert(!SemVer.IsVersion(bad), bad);
            Assert(SemVer.Compare("10.0.0", "9.0.0") > 0 && SemVer.Compare("1.10.0", "1.9.9") > 0 && SemVer.Compare("1.2.3", "1.2.3") == 0 && SemVer.Compare("1.2.3", "1.2.4") < 0, "order");
            Refuse("RELEASE_MANIFEST_INVALID", () => SemVer.Compare("1.2", "1.2.3"), "an invalid version compared");
            return Task.CompletedTask;
        });
        await check("release manifest: the documented format 2 parses into both tracks", () =>
        {
            var value = Parse(Fields());
            Assert(value is { Format: 2, Product: "yahad-windows-release", Version: "0.7.1", CreatedUnix: 1700000000 } && value.SourceCommit == Commit('a'), "top");
            Assert(value.Infrastructure is { Version: "1.0.0", ContractMin: 1, ContractMax: 2 } && value.Infrastructure.Setup == new ReleaseFile("Yahad.Setup-0.7.1.exe", Digest('d'), 10), "infrastructure");
            Assert(value.App.Archive == new ReleaseFile("yahad-windows-app-0.7.1.zip", Digest('e'), 20) && value.App is { RequiresContract: 1, DatabaseSchema: 6 } && value.App.DatabaseMigrationsFrom.SequenceEqual([3, 5]), "app");
            Assert(value.MinimumFrom == new ReleaseMinimumFrom("0.0.0", "0.0.0"), "minimumFrom");
            Assert(ReleaseManifest.ManifestName("0.7.1") == "yahad-windows-release-0.7.1.json" && ReleaseManifest.SignatureName("0.7.1") == "yahad-windows-release-0.7.1.json.sig" &&
                ReleaseManifest.SetupName("0.7.1") == "Yahad.Setup-0.7.1.exe" && ReleaseManifest.AppArchiveName("0.7.1") == "yahad-windows-app-0.7.1.zip", "names");
            // At the documented bounds.
            Assert(Parse(With(v => { Infra(v)["setup"]!["bytes"] = ReleaseManifest.MaximumSetupBytes; App(v)["archive"]!["bytes"] = ReleaseManifest.MaximumAppArchiveBytes; })).App.Archive.Bytes == ReleaseManifest.MaximumAppArchiveBytes, "size bounds");
            var most = new JsonArray(); for (var s = 1; s <= ReleaseManifest.MaximumMigrationSources; s++) most.Add(s);
            Assert(Parse(With(v => { App(v)["databaseSchema"] = 33; App(v)["databaseMigrationsFrom"] = most; })).App.DatabaseMigrationsFrom.Length == 32, "32 sources");
            Assert(Parse(With(v => App(v)["databaseMigrationsFrom"] = new JsonArray())).App.DatabaseMigrationsFrom.Length == 0, "no sources");
            Assert(Parse(With(v => v["minimumFrom"] = new JsonObject { ["app"] = "0.7.1", ["infrastructure"] = "1.0.0" })).MinimumFrom.App == "0.7.1", "minimumFrom at its own versions");
            return Task.CompletedTask;
        });
        await check("release manifest: accepts exactly 4096 bytes and no more", () =>
        {
            var text = Fields().ToJsonString(); var padded = text + new string(' ', ReleaseManifest.MaximumBytes - text.Length);
            Assert(ReleaseManifest.Parse(Encoding.UTF8.GetBytes(padded)).Version == "0.7.1", "at the bound");
            Refuse("RELEASE_MANIFEST_INVALID", () => ReleaseManifest.Parse(Encoding.UTF8.GetBytes(padded + " ")), "over the bound");
            Refuse("RELEASE_MANIFEST_INVALID", () => ReleaseManifest.Parse([]), "empty");
            return Task.CompletedTask;
        });
        await check("release manifest: strict camelCase shape, every field present, no unknown or duplicate field", () =>
        {
            var cases = new (string What, Func<JsonNode> Value)[]
            {
                ("unknown top field", () => With(v => v["extra"] = 1)),
                ("unknown nested field", () => With(v => Infra(v)["extra"] = 1)),
                ("unknown file field", () => With(v => App(v)["archive"]!["extra"] = 1)),
                ("PascalCase key", () => With(v => { v.Remove("version"); v["Version"] = "0.7.1"; })),
                ("missing top field", () => With(v => v.Remove("keyId"))),
                ("missing infrastructure", () => With(v => v.Remove("infrastructure"))),
                ("missing setup", () => With(v => Infra(v).Remove("setup"))),
                ("missing migrations", () => With(v => App(v).Remove("databaseMigrationsFrom"))),
                ("missing minimumFrom half", () => With(v => v["minimumFrom"]!.AsObject().Remove("infrastructure"))),
                ("null version", () => With(v => v["version"] = null)),
                ("null app", () => With(v => v["app"] = null)),
                ("not an object", () => new JsonArray()),
                ("number as string", () => With(v => v["format"] = "2")),
            };
            foreach (var (what, value) in cases) Refuse("RELEASE_MANIFEST_INVALID", () => Parse(value()), what);
            var text = Fields().ToJsonString();
            Refuse("RELEASE_MANIFEST_INVALID", () => ReleaseManifest.Parse(Encoding.UTF8.GetBytes("{\"format\":2," + text[1..])), "duplicate field");
            Refuse("RELEASE_MANIFEST_INVALID", () => ReleaseManifest.Parse(Encoding.UTF8.GetBytes(text + "x")), "trailing bytes");
            Refuse("RELEASE_MANIFEST_INVALID", () => ReleaseManifest.Parse(Encoding.UTF8.GetBytes("null")), "null");
            return Task.CompletedTask;
        });
        await check("release manifest: every value is checked", () =>
        {
            var cases = new (string What, Action<JsonObject> Change)[]
            {
                ("format 1", v => v["format"] = 1), ("format 3", v => v["format"] = 3), ("v1 product", v => v["product"] = "yahad-windows"),
                ("version", v => v["version"] = "0.7"), ("leading zero", v => v["version"] = "0.07.1"), ("commit", v => v["sourceCommit"] = Commit('A')), ("short commit", v => v["sourceCommit"] = "abc"),
                ("key id", v => v["keyId"] = "ABCDEF0123456789"), ("created", v => v["createdUnix"] = 0),
                ("infra version", v => Infra(v)["version"] = "1"), ("contract 0", v => Infra(v)["contractMin"] = 0), ("contract over", v => Infra(v)["contractMax"] = 1025),
                ("inverted range", v => { Infra(v)["contractMin"] = 2; Infra(v)["contractMax"] = 1; }), ("infra commit", v => Infra(v)["sourceCommit"] = Commit('g')), ("infra tree", v => Infra(v)["treeSha256"] = "x"),
                ("setup name", v => Infra(v)["setup"]!["name"] = "Yahad.Setup.exe"), ("setup of another version", v => Infra(v)["setup"]!["name"] = "Yahad.Setup-0.7.0.exe"),
                ("setup hash", v => Infra(v)["setup"]!["sha256"] = Digest('D')), ("setup empty", v => Infra(v)["setup"]!["bytes"] = 0), ("setup over", v => Infra(v)["setup"]!["bytes"] = ReleaseManifest.MaximumSetupBytes + 1),
                ("archive name", v => App(v)["archive"]!["name"] = "yahad-windows-app.zip"), ("archive hash", v => App(v)["archive"]!["sha256"] = "x"),
                ("archive empty", v => App(v)["archive"]!["bytes"] = 0), ("archive over", v => App(v)["archive"]!["bytes"] = ReleaseManifest.MaximumAppArchiveBytes + 1),
                ("app tree", v => App(v)["treeSha256"] = Digest('z')), ("requires 0", v => App(v)["requiresContract"] = 0),
                ("schema 0", v => App(v)["databaseSchema"] = 0), ("schema over", v => App(v)["databaseSchema"] = 1025),
                ("source 0", v => App(v)["databaseMigrationsFrom"] = new JsonArray(0, 3)), ("source at schema", v => App(v)["databaseMigrationsFrom"] = new JsonArray(3, 6)),
                ("source above schema", v => App(v)["databaseMigrationsFrom"] = new JsonArray(7)), ("duplicate source", v => App(v)["databaseMigrationsFrom"] = new JsonArray(3, 3)),
                ("33 sources", v => { var many = new JsonArray(); for (var s = 1; s <= 33; s++) many.Add(s); App(v)["databaseSchema"] = 40; App(v)["databaseMigrationsFrom"] = many; }),
                ("minimum app newer than itself", v => v["minimumFrom"]!["app"] = "0.7.2"), ("minimum infra newer than its own", v => v["minimumFrom"]!["infrastructure"] = "1.0.1"),
                ("minimum version", v => v["minimumFrom"]!["app"] = "any"),
            };
            foreach (var (what, change) in cases) Refuse("RELEASE_MANIFEST_INVALID", () => Parse(With(change)), what);
            return Task.CompletedTask;
        });
        await check("release signature: v2 over the raw bytes, signature first, then the parse, then the key id", () =>
        {
            var manifest = Encode(Fields()); var signature = SignWith(ReleaseSignature.Prefix, manifest);
            Assert(ReleaseSignature.Prefix == "yahad-release-v2\n" && ReleaseVerification.Verify(spki, manifest, signature).Version == "0.7.1", "verifies");
            // A v1 signature over the very same bytes never verifies as v2.
            Refuse("RELEASE_SIGNATURE_INVALID", () => ReleaseVerification.Verify(spki, manifest, SignWith("yahad-release-v1\n", manifest)), "v1 prefix");
            Refuse("RELEASE_SIGNATURE_INVALID", () => ReleaseVerification.Verify(spki, manifest, SignWith("", manifest)), "no prefix");
            Refuse("RELEASE_SIGNATURE_INVALID", () => ReleaseVerification.Verify(spki, manifest, null), "no signature");
            Refuse("RELEASE_SIGNATURE_INVALID", () => ReleaseVerification.Verify(spki, manifest, [.. signature, 0]), "65 bytes");
            var flipped = (byte[])manifest.Clone(); flipped[20] ^= 1; Refuse("RELEASE_SIGNATURE_INVALID", () => ReleaseVerification.Verify(spki, flipped, signature), "changed bytes");
            using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256)) Refuse("RELEASE_SIGNATURE_INVALID", () => ReleaseVerification.Verify(other.ExportSubjectPublicKeyInfo(), manifest, signature), "another key");
            // Garbage that is rightly signed is a parse failure; a signature over an oversized message is never checked.
            var garbage = Encoding.UTF8.GetBytes("{{{"); Refuse("RELEASE_MANIFEST_INVALID", () => ReleaseVerification.Verify(spki, garbage, SignWith(ReleaseSignature.Prefix, garbage)), "signed garbage");
            var big = new byte[ReleaseManifest.MaximumBytes + 1]; Refuse("RELEASE_SIGNATURE_INVALID", () => ReleaseVerification.Verify(spki, big, SignWith(ReleaseSignature.Prefix, big)), "oversized");
            var foreign = Encode(With(v => v["keyId"] = "0123456789abcdef")); Refuse("RELEASE_KEY_UNTRUSTED", () => ReleaseVerification.Verify(spki, foreign, SignWith(ReleaseSignature.Prefix, foreign)), "key id");
            return Task.CompletedTask;
        });
        await check("release manifest: AcceptsSchema is the target or a listed source", () =>
        {
            var value = Parse(Fields());
            foreach (var schema in new[] { 6, 3, 5 }) Assert(value.AcceptsSchema(schema), "accepts " + schema);
            foreach (var schema in new[] { 0, 1, 2, 4, 7, -1 }) Assert(!value.AcceptsSchema(schema), "refuses " + schema);
            return Task.CompletedTask;
        });

        // Selection over records: Select reads only what the strict parse already checked.
        ActivePointer Installed(string infra = "1.0.0", string app = "1.0.0", int min = 1, int max = 2, string tree = "c")
            => new(1, new ActiveInfrastructure(infra, min, max, Digest(tree[0])), new ActiveApp(app, 1, Digest('9')));
        ReleaseManifest Release(string infra, string app, int requires = 1, int min = 1, int max = 2, string tree = "c", string minimumApp = "0.0.0", string minimumInfra = "0.0.0")
            => new(2, ReleaseManifest.ProductName, app, Commit('a'), TrustedReleaseKey.Id(spki), 1700000000,
                new(infra, min, max, Commit('b'), Digest(tree[0]), new(ReleaseManifest.SetupName(app), Digest('d'), 10)),
                new(new(ReleaseManifest.AppArchiveName(app), Digest('e'), 20), Digest('f'), requires, 6, [5]), new(minimumApp, minimumInfra));
        await check("release selection: infrastructure newer, equal or older against app newer or equal", () =>
        {
            var installed = Installed();
            var cases = new (string Infra, string App, ReleaseTrack? Expected)[]
            {
                ("2.0.0", "1.0.1", ReleaseTrack.Setup), ("2.0.0", "1.0.0", ReleaseTrack.Setup), ("1.0.1", "2.0.0", ReleaseTrack.Setup),
                ("1.0.0", "1.0.1", ReleaseTrack.App), ("1.0.0", "1.0.0", null),
                ("0.9.0", "1.0.1", null), ("0.9.0", "1.0.0", null),
            };
            foreach (var (infra, app, expected) in cases) Assert(ReleaseSelection.Select(Release(infra, app), installed) == expected, infra + "/" + app);
            // A newer infrastructure may bring another range and tree: that is what the Setup track installs.
            Assert(ReleaseSelection.Select(Release("2.0.0", "1.0.1", requires: 3, min: 3, max: 3, tree: "x"), installed) == ReleaseTrack.Setup, "new range and tree");
            return Task.CompletedTask;
        });
        await check("release selection: an app outside its own release's contract range is a release defect on every machine", () =>
        {
            var installed = Installed();
            foreach (var (infra, app) in new[] { ("2.0.0", "1.0.1"), ("1.0.0", "1.0.1"), ("1.0.0", "1.0.0"), ("0.9.0", "1.0.1") })
            {
                Refuse("APP_NEEDS_INFRASTRUCTURE", () => ReleaseSelection.Select(Release(infra, app, requires: 3), installed), "above " + infra + "/" + app);
                Refuse("APP_NEEDS_INFRASTRUCTURE", () => ReleaseSelection.Select(Release(infra, app, requires: 1, min: 2, max: 2), Installed(min: 2, max: 2)), "below " + infra + "/" + app);
            }
            Assert(ReleaseSelection.Select(Release("1.0.0", "1.0.1", requires: 2), installed) == ReleaseTrack.App, "the range's top");
            return Task.CompletedTask;
        });
        await check("release selection: one infrastructure version names one tree and one range", () =>
        {
            var installed = Installed();
            Refuse("INFRA_IDENTITY_MISMATCH", () => ReleaseSelection.Select(Release("1.0.0", "1.0.1", tree: "x"), installed), "tree");
            Refuse("INFRA_IDENTITY_MISMATCH", () => ReleaseSelection.Select(Release("1.0.0", "1.0.1", max: 3), installed), "max");
            Refuse("INFRA_IDENTITY_MISMATCH", () => ReleaseSelection.Select(Release("1.0.0", "1.0.1", min: 1, max: 1), installed), "min");
            // Not an update anyway: nothing to compare.
            Assert(ReleaseSelection.Select(Release("1.0.0", "1.0.0", tree: "x"), installed) is null && ReleaseSelection.Select(Release("0.9.0", "1.0.1", tree: "x"), installed) is null, "not an update");
            return Task.CompletedTask;
        });
        await check("release selection: minimumFrom bounds both tracks, and RequireFrom alone", () =>
        {
            var installed = Installed("1.0.0", "1.0.0");
            Refuse("RELEASE_UNSUPPORTED_FROM", () => ReleaseSelection.Select(Release("2.0.0", "1.0.1", minimumApp: "1.0.1"), installed), "setup track app");
            Refuse("RELEASE_UNSUPPORTED_FROM", () => ReleaseSelection.Select(Release("2.0.0", "1.0.1", minimumInfra: "1.0.1"), installed), "setup track infra");
            Refuse("RELEASE_UNSUPPORTED_FROM", () => ReleaseSelection.Select(Release("1.0.0", "1.0.1", minimumApp: "1.0.1"), installed), "app track");
            Assert(ReleaseSelection.Select(Release("0.9.0", "1.0.1", minimumApp: "1.0.1"), installed) is null && ReleaseSelection.Select(Release("1.0.0", "1.0.0", minimumApp: "1.0.0"), installed) is null, "not an update is not refused");
            Assert(ReleaseSelection.Select(Release("1.0.0", "1.0.1", minimumApp: "1.0.0", minimumInfra: "1.0.0"), installed) == ReleaseTrack.App, "at the minimum");
            ReleaseSelection.RequireFrom(Release("1.0.0", "10.0.0", minimumApp: "1.0.0", minimumInfra: "1.0.0"), installed);
            ReleaseSelection.RequireFrom(Release("1.0.0", "10.0.0", minimumApp: "0.9.10", minimumInfra: "0.10.0"), installed);
            foreach (var (app, infra) in new[] { ("1.0.1", "0.0.0"), ("0.0.0", "1.0.1"), ("1.1.0", "1.0.0"), ("2.0.0", "2.0.0") })
                Refuse("RELEASE_UNSUPPORTED_FROM", () => ReleaseSelection.RequireFrom(Release("9.0.0", "10.0.0", minimumApp: app, minimumInfra: infra), installed), app + "/" + infra);
            return Task.CompletedTask;
        });
    }
}

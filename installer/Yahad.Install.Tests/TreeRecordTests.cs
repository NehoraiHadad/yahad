using System.Text;
using Yahad.Install;

internal static class TreeRecordTests
{
    public static async Task Run(string parent, Func<string, Func<Task>, Task> check)
    {
        void Assert(bool value) { if (!value) throw new Exception("assertion failed"); }
        void RefuseParse(byte[] bytes, TreeKind kind)
        { try { TreeRecord.Parse(bytes, kind); } catch (IOException error) when (error.Message is "TREE_RECORD_INVALID" or "NATIVE_DUPLICATE_FIELD") { return; } throw new Exception("expected refusal"); }
        void Refuse(Action action)
        { try { action(); } catch (IOException error) when (error.Message == "ACTIVE_POINTER_INVALID") { return; } throw new Exception("expected ACTIVE_POINTER_INVALID"); }
        var digest = new string('c', 64); var commit = new string('d', 40);
        TreeMember M(string path, long size = 1, string? sha = null) => new(path, size, sha ?? digest);
        byte[] Bytes(params TreeMember[] files) => NativeJson.WriteCamel(new TreeRecord(1, "1.2.3", commit, files), NativeCamelJsonContext.Default.TreeRecord);
        string RawText(string version = "1.2.3", string? sourceCommit = null, string format = "1", string? files = null) =>
            "{\"format\":" + format + ",\"version\":\"" + version + "\",\"sourceCommit\":\"" + (sourceCommit ?? commit) + "\",\"files\":[" +
            (files ?? "{\"path\":\"host/a\",\"size\":1,\"sha256\":\"" + digest + "\"}") + "]}";
        byte[] Raw(string version = "1.2.3", string? sourceCommit = null, string format = "1", string? files = null) => Encoding.UTF8.GetBytes(RawText(version, sourceCommit, format, files));
        await check("tree record parses a sorted, unique, in-prefix member list", () =>
        {
            var infra = TreeRecord.Parse(Bytes(M("host/a.exe"), M("legal/x"), M("platform/p.exe"), M("runtime/node/node.exe", 0), M("setup/s.exe"), M("updater/u.exe")), TreeKind.Infrastructure);
            Assert(infra.Version == "1.2.3" && infra.SourceCommit == commit && infra.Files.Length == 6 && infra.Files[3].Size == 0);
            var app = TreeRecord.Parse(Bytes(M("app/package.json"), M("app/server/main.mjs")), TreeKind.App);
            Assert(app.Files.Length == 2); return Task.CompletedTask;
        });
        await check("tree record refuses a BOM, empty or oversized bytes and unknown or duplicate fields", () =>
        {
            RefuseParse([0xEF, 0xBB, 0xBF, .. Bytes(M("host/a"))], TreeKind.Infrastructure);
            RefuseParse([], TreeKind.Infrastructure); RefuseParse(new byte[TreeRecord.MaximumBytes + 1], TreeKind.Infrastructure);
            RefuseParse(Encoding.UTF8.GetBytes("{"), TreeKind.Infrastructure);
            RefuseParse(Encoding.UTF8.GetBytes(RawText().Replace("\"format\":1", "\"format\":1,\"extra\":1")), TreeKind.Infrastructure);
            RefuseParse(Encoding.UTF8.GetBytes(RawText().Replace("\"format\":1", "\"format\":1,\"format\":1")), TreeKind.Infrastructure);
            RefuseParse(Raw(files: "{\"path\":\"host/a\",\"size\":1,\"sha256\":\"" + digest + "\",\"mode\":1}"), TreeKind.Infrastructure);
            RefuseParse(Encoding.UTF8.GetBytes("{\"format\":1,\"version\":\"1.2.3\",\"sourceCommit\":\"" + commit + "\",\"files\":null}"), TreeKind.Infrastructure);
            RefuseParse(Raw(format: "2"), TreeKind.Infrastructure); RefuseParse(Raw(version: "1.2"), TreeKind.Infrastructure);
            RefuseParse(Encoding.UTF8.GetBytes("{\"format\":1,\"version\":\"1.2.3\",\"sourceCommit\":\"" + commit + "\",\"files\":[]}"), TreeKind.Infrastructure);
            return Task.CompletedTask;
        });
        await check("tree record refuses unsorted, duplicate and case-duplicate paths", () =>
        {
            RefuseParse(Bytes(M("host/b"), M("host/a")), TreeKind.Infrastructure);
            RefuseParse(Bytes(M("host/a"), M("host/a")), TreeKind.Infrastructure);
            RefuseParse(Bytes(M("host/A"), M("host/a")), TreeKind.Infrastructure);
            return Task.CompletedTask;
        });
        await check("tree record refuses a path outside its tree prefixes and any app/native member", () =>
        {
            RefuseParse(Bytes(M("other/a")), TreeKind.Infrastructure); RefuseParse(Bytes(M("app/a")), TreeKind.Infrastructure);
            RefuseParse(Bytes(M("host")), TreeKind.Infrastructure); RefuseParse(Bytes(M("Host/a")), TreeKind.Infrastructure);
            RefuseParse(Bytes(M("host/a")), TreeKind.App); RefuseParse(Bytes(M("app/native/x.node")), TreeKind.App);
            RefuseParse(Bytes(M("app/Native/x.node")), TreeKind.App);
            foreach (var bad in new[] { "host/../a", "host//a", "host/./a", "host/a.", "host/a ", "host\\a", "host/c:a", "host/NUL", "/host/a", "host/" })
                RefuseParse(Bytes(M(bad)), TreeKind.Infrastructure);
            RefuseParse(Bytes(M("host/" + new string('a', MemberNames.MaximumLength))), TreeKind.Infrastructure);
            return Task.CompletedTask;
        });
        await check("tree record enforces size bounds, digests and the commit", () =>
        {
            Assert(TreeRecord.Parse(Bytes(M("host/a", TreeRecord.MaximumMemberBytes)), TreeKind.Infrastructure).Files.Length == 1);
            RefuseParse(Bytes(M("host/a", -1)), TreeKind.Infrastructure); RefuseParse(Bytes(M("host/a", TreeRecord.MaximumMemberBytes + 1)), TreeKind.Infrastructure);
            Assert(TreeRecord.Parse(Bytes(M("host/a", TreeRecord.MaximumMemberBytes), M("host/b", TreeRecord.MaximumMemberBytes), M("host/c", TreeRecord.MaximumMemberBytes), M("host/d", TreeRecord.MaximumMemberBytes)), TreeKind.Infrastructure).Files.Length == 4);
            RefuseParse(Bytes(M("host/a", TreeRecord.MaximumMemberBytes), M("host/b", TreeRecord.MaximumMemberBytes), M("host/c", TreeRecord.MaximumMemberBytes), M("host/d", TreeRecord.MaximumMemberBytes), M("host/e")), TreeKind.Infrastructure);
            foreach (var bad in new[] { "", new string('c', 63), new string('c', 65), new string('C', 64), new string('z', 64) }) RefuseParse(Bytes(M("host/a", 1, bad)), TreeKind.Infrastructure);
            foreach (var bad in new[] { new string('d', 39), new string('d', 41), new string('D', 40), new string('g', 40) }) RefuseParse(Raw(sourceCommit: bad), TreeKind.Infrastructure);
            return Task.CompletedTask;
        });

        // VerifyInstalled over a real directory: record bytes, exact member set and sizes.
        (string Path, string Content)[] InfraFiles = [("host/Yahad.ServiceHost.exe", "host-image"), ("runtime/node/node.exe", "node!"), ("setup/Yahad.Setup.exe", "engine")];
        (string Path, string Content)[] AppFiles = [("app/package.json", "{}"), ("app/server/main.mjs", "export{}")];
        string Tree(out string sha, TreeKind kind = TreeKind.Infrastructure, Action<string>? alter = null)
        {
            var root = Path.Combine(PrivateSession.Create(parent), "tree"); Directory.CreateDirectory(root);
            var members = new List<TreeMember>();
            foreach (var (path, content) in kind == TreeKind.Infrastructure ? InfraFiles : AppFiles)
            {
                var full = Path.Combine(root, path.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var bytes = Encoding.UTF8.GetBytes(content); File.WriteAllBytes(full, bytes); members.Add(M(path, bytes.Length, TreeRecord.Sha256(bytes)));
            }
            var record = Bytes([.. members]); File.WriteAllBytes(Path.Combine(root, TreeRecord.RecordName(kind)), record); sha = TreeRecord.Sha256(record);
            alter?.Invoke(root); return root;
        }
        await check("installed trees verify against the record sha", () =>
        {
            var root = Tree(out var sha); var record = TreeRecord.VerifyInstalled(root, TreeKind.Infrastructure, "1.2.3", sha);
            Assert(record.Files.Length == 3 && record.Version == "1.2.3");
            var app = Tree(out var appSha, TreeKind.App); Assert(TreeRecord.VerifyInstalled(app, TreeKind.App, "1.2.3", appSha).Files.Length == 2);
            return Task.CompletedTask;
        });
        await check("installed tree refuses a wrong record sha, version, root or tampered or missing record", () =>
        {
            var root = Tree(out var sha);
            Refuse(() => TreeRecord.VerifyInstalled(root, TreeKind.Infrastructure, "1.2.3", digest));
            Refuse(() => TreeRecord.VerifyInstalled(root, TreeKind.Infrastructure, "1.2.4", sha));
            Refuse(() => TreeRecord.VerifyInstalled(Path.Combine(root, "missing"), TreeKind.Infrastructure, "1.2.3", sha));
            var tampered = Tree(out var tamperedSha, alter: r => File.AppendAllText(Path.Combine(r, "infra-tree.json"), " "));
            Refuse(() => TreeRecord.VerifyInstalled(tampered, TreeKind.Infrastructure, "1.2.3", TreeRecord.Sha256(File.ReadAllBytes(Path.Combine(tampered, "infra-tree.json"))[..^1])));
            Refuse(() => TreeRecord.VerifyInstalled(tampered, TreeKind.Infrastructure, "1.2.3", tamperedSha));
            var noRecord = Tree(out var noSha, alter: r => File.Delete(Path.Combine(r, "infra-tree.json")));
            Refuse(() => TreeRecord.VerifyInstalled(noRecord, TreeKind.Infrastructure, "1.2.3", noSha));
            return Task.CompletedTask;
        });
        await check("installed tree refuses a missing member, a wrong size, an extra file and an extra directory", () =>
        {
            var missing = Tree(out var a, alter: r => File.Delete(Path.Combine(r, "runtime", "node", "node.exe")));
            Refuse(() => TreeRecord.VerifyInstalled(missing, TreeKind.Infrastructure, "1.2.3", a));
            var size = Tree(out var b, alter: r => File.AppendAllText(Path.Combine(r, "setup", "Yahad.Setup.exe"), "x"));
            Refuse(() => TreeRecord.VerifyInstalled(size, TreeKind.Infrastructure, "1.2.3", b));
            var extra = Tree(out var c, alter: r => File.WriteAllText(Path.Combine(r, "host", "extra.dll"), "x"));
            Refuse(() => TreeRecord.VerifyInstalled(extra, TreeKind.Infrastructure, "1.2.3", c));
            var top = Tree(out var d, alter: r => File.WriteAllText(Path.Combine(r, "stray.txt"), "x"));
            Refuse(() => TreeRecord.VerifyInstalled(top, TreeKind.Infrastructure, "1.2.3", d));
            var directory = Tree(out var e, alter: r => Directory.CreateDirectory(Path.Combine(r, "host", "empty")));
            Refuse(() => TreeRecord.VerifyInstalled(directory, TreeKind.Infrastructure, "1.2.3", e));
            var topDirectory = Tree(out var f, alter: r => Directory.CreateDirectory(Path.Combine(r, "unlisted")));
            Refuse(() => TreeRecord.VerifyInstalled(topDirectory, TreeKind.Infrastructure, "1.2.3", f));
            // An app tree is not an infrastructure tree: its members fall outside the infrastructure prefixes.
            var app = Tree(out var g, TreeKind.App); Refuse(() => TreeRecord.VerifyInstalled(app, TreeKind.Infrastructure, "1.2.3", g));
            return Task.CompletedTask;
        });
    }
}

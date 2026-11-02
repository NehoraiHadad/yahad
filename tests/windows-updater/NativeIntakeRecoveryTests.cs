using System.Security.Cryptography;
using Yahad.Install;

// Private fixture storage only. These checks exercise actual Windows file IDs
// and handle renames, not a production installed-role/SCM acceptance run.
internal static class NativeIntakeRecoveryTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        foreach(var boundary in new[]{"recovery_intent_flushed","recovery_object_created","recovery_identity_flushed","recovery_download_reset","recovery_download_published"})
            await check("known download recovers at durable boundary "+boundary,()=>
            {
                var f=new Fixture();var bytes=new byte[]{1,2,3,4};var digest=Hash(bytes);var source=new string('a',64);
                var broken=f.Recovery(point=>{if(point==boundary)throw new IOException("SYNTHETIC_CRASH");});broken.Reconcile(default);
                Refused(()=>{using var output=broken.Download(source,digest,bytes.Length);output.Stream.Write(bytes);output.Publish();});
                var recovered=f.Recovery();recovered.Reconcile(default);var archive=f.Files.PathOf("intake/archive-"+digest+".zip");
                if(!File.Exists(archive)){using var output=recovered.Download(source,digest,bytes.Length);output.Stream.Write(bytes);output.Publish();}
                Assert(File.ReadAllBytes(archive).SequenceEqual(bytes)&&!Directory.EnumerateFiles(f.Files.PathOf("intake"),"download-*").Any());f.AssertData();return Task.CompletedTask;
            });
        await check("same owned partial resets in place across repeated interrupted bodies without residue growth",()=>
        {
            var f=new Fixture();var bytes=new byte[]{1,2,3,4};var digest=Hash(bytes);var source=new string('a',64);
            string? temporary=null;
            for(var attempt=0;attempt<5;attempt++)
            {
                var recovery=f.Recovery();recovery.Reconcile(default);using(var output=recovery.Download(source,digest,bytes.Length)){output.Stream.Write(bytes.AsSpan(0,2));output.Stream.Flush(true);}
                var path=Directory.GetFiles(f.Files.PathOf("intake"),"download-*.zip.partial").Single();temporary??=path;Assert(path==temporary);
            }
            var final=f.Recovery();final.Reconcile(default);using(var output=final.Download(source,digest,bytes.Length)){Assert(output.Stream.Length==0);output.Stream.Write(bytes);output.Publish();}
            Assert(Directory.GetFiles(f.Files.PathOf("intake/recovery"),"operation-*.json").Length==0&&Directory.GetDirectories(f.Files.PathOf("intake/recovery-audit")).Length==1);Assert(!Directory.EnumerateFileSystemEntries(f.Files.PathOf("intake/recovery"),"retained-*").Any());f.AssertData();return Task.CompletedTask;
        });
        await check("changed original partial identity is never truncated or adopted",()=>
        {
            var f=new Fixture();var bytes=new byte[]{1,2,3,4};var digest=Hash(bytes);var source=new string('a',64);var first=f.Recovery();first.Reconcile(default);
            using(var output=first.Download(source,digest,bytes.Length))output.Stream.WriteByte(1);
            var path=Directory.GetFiles(f.Files.PathOf("intake"),"download-*.zip.partial").Single();var saved=f.Files.PathOf("intake/original-fixture-partial");File.Move(path,saved);File.WriteAllBytes(path,[9,9]);
            Refused(()=>f.Recovery().Reconcile(default));Assert(File.ReadAllBytes(path).SequenceEqual(new byte[]{9,9})&&File.ReadAllBytes(saved).SequenceEqual(new byte[]{1}));f.AssertData();return Task.CompletedTask;
        });
        foreach(var kind in new[]{"payload","system"})
            await check("known interrupted "+kind+" staging is retained and next verified closure publishes",()=>
            {
                var f=new Fixture();var bytes=new byte[]{1,2,3,4};var digest=Hash(bytes);var members=new[]{new NativeIntakeMember("a.bin",bytes.Length,digest)};
                var recovery=f.Recovery(kind:kind);recovery.Reconcile(default);var root=recovery.Stage(kind,digest,members);File.WriteAllBytes(Path.Combine(root,"a.bin"),[1,2]);
                var next=f.Recovery(kind:kind);next.Reconcile(default);var parent=kind=="payload"?"payloads":"system-slots";var retained=Directory.GetDirectories(f.Files.PathOf(parent),".retained-*").Single();
                Assert(!Directory.Exists(root)&&File.ReadAllBytes(Path.Combine(retained,"a.bin")).SequenceEqual(new byte[]{1,2}));
                var replacement=next.Stage(kind,digest,members);File.WriteAllBytes(Path.Combine(replacement,"a.bin"),bytes);next.PublishedDirectory(replacement,f.Files.PathOf(parent+"/"+digest));
                Assert(File.ReadAllBytes(f.Files.PathOf(parent+"/"+digest+"/a.bin")).SequenceEqual(bytes));f.AssertData();return Task.CompletedTask;
            });
        await check("staging publication rejects missing or changed closure and preserves unknown members",()=>
        {
            var f=new Fixture();var bytes=new byte[]{1,2,3,4};var digest=Hash(bytes);var recovery=f.Recovery();recovery.Reconcile(default);var root=recovery.Stage("payload",digest,[new("a.bin",bytes.Length,digest)]);
            Refused(()=>recovery.PublishedDirectory(root,f.Files.PathOf("payloads/"+digest)));File.WriteAllBytes(Path.Combine(root,"unknown.bin"),[9]);
            Refused(()=>f.Recovery().Reconcile(default));Assert(File.ReadAllBytes(Path.Combine(root,"unknown.bin")).SequenceEqual(new byte[]{9}));f.AssertData();return Task.CompletedTask;
        });
        await check("retained staging bound stops further allocation without deleting earlier evidence",()=>
        {
            var f=new Fixture();var bytes=new byte[]{1};var digest=Hash(bytes);
            for(var attempt=0;attempt<8;attempt++){var recovery=f.Recovery();recovery.Reconcile(default);var root=recovery.Stage("payload",digest,[new("a.bin",1,digest)]);File.WriteAllBytes(Path.Combine(root,"a.bin"),bytes);f.Recovery().Reconcile(default);}
            var ninth=f.Recovery();ninth.Reconcile(default);var pending=ninth.Stage("payload",digest,[new("a.bin",1,digest)]);File.WriteAllBytes(Path.Combine(pending,"a.bin"),bytes);
            Refused(()=>f.Recovery().Reconcile(default));Assert(Directory.GetDirectories(f.Files.PathOf("payloads"),".retained-*").Length==8&&Directory.Exists(pending));f.AssertData();return Task.CompletedTask;
        });
        foreach(var boundary in new[]{"recovery_payload_published","recovery_pointer_published","recovery_residue_retained"})
            await check("publication-before-completion crash reconciles "+boundary,()=>
            {
                var f=new Fixture();var bytes=new byte[]{1,2,3};var digest=Hash(bytes);var recovery=f.Recovery(point=>{if(point==boundary)throw new IOException("SYNTHETIC_CRASH");});recovery.Reconcile(default);
                if(boundary=="recovery_pointer_published"){var temp=recovery.Pointer("available.json",bytes);Refused(()=>recovery.PublishedPointer(temp));}
                else {var root=recovery.Stage("payload",digest,[new("a.bin",bytes.Length,digest)]);File.WriteAllBytes(Path.Combine(root,"a.bin"),bytes);if(boundary=="recovery_payload_published")Refused(()=>recovery.PublishedDirectory(root,f.Files.PathOf("payloads/"+digest)));else Refused(()=>recovery.Reconcile(default));}
                f.Recovery().Reconcile(default);f.AssertData();return Task.CompletedTask;
            });
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Assert(bool condition){if(!condition)throw new IOException("INTAKE_RECOVERY_ASSERTION");}
    private static void Refused(Action action){try{action();}catch(IOException){return;}throw new IOException("INTAKE_RECOVERY_EXPECTED_REFUSAL");}
    private sealed class Fixture
    {
        internal readonly InstalledStateFiles Files=InstalledStateFiles.Private(SafeDirectory.CreatePrivateSession(Path.GetTempPath()));
        internal Fixture(){foreach(var name in new[]{"intake","payloads","system-slots","datasets","backups"})Directory.CreateDirectory(Files.PathOf(name));File.WriteAllBytes(Files.PathOf("datasets/user-data"),[5,6,7]);File.WriteAllBytes(Files.PathOf("backups/prior"),[8,9]);}
        internal NativeIntakeRecovery Recovery(Action<string>? fault=null,string kind="payload")=>new(Files,kind=="system"?"system-slots/.intake-recovery":"intake/recovery",fault);
        internal void AssertData(){Assert(File.ReadAllBytes(Files.PathOf("datasets/user-data")).SequenceEqual(new byte[]{5,6,7}));Assert(File.ReadAllBytes(Files.PathOf("backups/prior")).SequenceEqual(new byte[]{8,9}));}
    }
}

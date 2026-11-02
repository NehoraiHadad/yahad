using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yahad.Install;

// Native file handles in private fixtures only; no installed-role/native-service
// acceptance and no real disk is filled to exercise the injected I/O failures.
internal static class NativeIntakeMetadataTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        foreach(var kind in new[]{"operation","identity","complete","archive"})foreach(var boundary in new[]{"created","during_write","before_flush","before_rename","published"})
            await check("atomic "+kind+" metadata recovers write interruption at "+boundary,()=>
            {
                var f=new Fixture();var point="metadata_"+kind+"_"+boundary;var broken=f.Recovery(observed=>{if(observed==point)throw new IOException("SYNTHETIC_METADATA_DISK_FULL");});broken.Reconcile(default);
                Refused(()=>{using var output=broken.Download(f.Source,f.Digest,f.Bytes.Length);output.Stream.Write(f.Bytes);output.Publish();});
                f.Finish();f.AssertData();return Task.CompletedTask;
            });
        foreach(var boundary in new[]{"metadata_archive_member_moved","metadata_archive_complete"})
            await check("completed intake metadata compaction resumes after "+boundary,()=>
            {
                var f=new Fixture();var broken=f.Recovery(point=>{if(point==boundary)throw new IOException("SYNTHETIC_ARCHIVE_CRASH");});broken.Reconcile(default);
                Refused(()=>{using var output=broken.Download(f.Source,f.Digest,f.Bytes.Length);output.Stream.Write(f.Bytes);output.Publish();});
                f.Finish();Assert(Directory.EnumerateFiles(f.Journal,"operation-*.json").Count()==0&&Directory.GetDirectories(f.Audit).Length==1);f.AssertData();return Task.CompletedTask;
            });
        foreach(var truncated in new[]{false,true})
            await check("legacy torn operation metadata without authorized objects recovers "+truncated,()=>
            {
                var f=new Fixture();var broken=f.Recovery(point=>{if(point=="recovery_intent_flushed")throw new IOException("SYNTHETIC_CRASH");});broken.Reconcile(default);Refused(()=>{using var output=broken.Download(f.Source,f.Digest,f.Bytes.Length);});
                var operation=Directory.GetFiles(f.Journal,"operation-*.json").Single();var original=File.ReadAllBytes(operation);File.WriteAllBytes(operation,truncated?original[..(original.Length/2)]:[]);
                f.Finish();Assert(Directory.GetFiles(f.Journal,"metadata-retained-*").Length==1);f.AssertData();return Task.CompletedTask;
            });
        foreach(var nonempty in new[]{false,true})foreach(var truncated in new[]{false,true})
            await check("legacy torn identity only adopts an exact empty generated object "+nonempty+"/"+truncated,()=>
            {
                var f=new Fixture();var recovery=f.Recovery();recovery.Reconcile(default);using(var output=recovery.Download(f.Source,f.Digest,f.Bytes.Length)){if(nonempty)output.Stream.WriteByte(5);}
                var identity=Directory.GetFiles(f.Journal,"identity-*.json").Single();var original=File.ReadAllBytes(identity);File.WriteAllBytes(identity,truncated?original[..(original.Length/2)]:[]);
                if(nonempty){var partial=Directory.GetFiles(f.Files.PathOf("intake"),"download-*.zip.partial").Single();Refused(()=>f.Recovery().Reconcile(default));Assert(File.ReadAllBytes(partial).SequenceEqual(new byte[]{5}));}
                else f.Finish();f.AssertData();return Task.CompletedTask;
            });
        await check("torn operation with any generated object and complete malformed JSON remain blocked",()=>
        {
            foreach(var malformed in new[]{false,true})
            {
                var f=new Fixture();var broken=f.Recovery(point=>{if(point=="recovery_intent_flushed")throw new IOException("SYNTHETIC_CRASH");});broken.Reconcile(default);Refused(()=>{using var output=broken.Download(f.Source,f.Digest,f.Bytes.Length);});
                var operation=Directory.GetFiles(f.Journal,"operation-*.json").Single();var intent=JsonDocument.Parse(File.ReadAllBytes(operation));var temporary=f.Files.PathOf(intent.RootElement.GetProperty("Temporary").GetString()!);
                if(!malformed)File.WriteAllBytes(temporary,[9]);File.WriteAllBytes(operation,malformed?Encoding.UTF8.GetBytes("{}"):[]);
                Refused(()=>f.Recovery().Reconcile(default));Assert(File.Exists(operation));if(!malformed)Assert(File.ReadAllBytes(temporary).SequenceEqual(new byte[]{9}));f.AssertData();
            }
            return Task.CompletedTask;
        });
        await check("empty legacy completion marker reconstructs only from verified published identity",()=>
        {
            var f=new Fixture();var broken=f.Recovery(point=>{if(point=="recovery_download_published")throw new IOException("SYNTHETIC_CRASH");});broken.Reconcile(default);
            Refused(()=>{using var output=broken.Download(f.Source,f.Digest,f.Bytes.Length);output.Stream.Write(f.Bytes);output.Publish();});
            var operation=Directory.GetFiles(f.Journal,"operation-*.json").Single();var id=Path.GetFileName(operation)[10..^5];File.WriteAllBytes(Path.Combine(f.Journal,"complete-"+id+".bin"),[]);
            f.Finish();f.AssertData();return Task.CompletedTask;
        });
        await check("more than 512 identical pointer checks create no new operation and next candidate still publishes",()=>
        {
            var f=new Fixture();var first=Encoding.UTF8.GetBytes("first protected pointer");var second=Encoding.UTF8.GetBytes("second protected pointer");
            for(var i=0;i<520;i++){var recovery=f.Recovery();recovery.Reconcile(default);var temporary=recovery.Pointer("available.json",first);recovery.PublishedPointer(temporary);}
            Assert(Directory.GetDirectories(f.Audit).Length==1&&!Directory.GetFiles(f.Journal,"operation-*.json").Any());
            var next=f.Recovery();next.Reconcile(default);next.PublishedPointer(next.Pointer("available.json",second));Assert(File.ReadAllBytes(f.Files.PathOf("intake/available.json")).SequenceEqual(second)&&Directory.GetDirectories(f.Audit).Length==2);f.AssertData();return Task.CompletedTask;
        });
        await check("more than 512 verified pointer publications preserve audit outside active operation budget",()=>
        {
            var f=new Fixture();for(var i=0;i<513;i++){var recovery=f.Recovery();recovery.Reconcile(default);recovery.PublishedPointer(recovery.Pointer("available.json",Encoding.UTF8.GetBytes("pointer-"+i)));}
            Assert(Directory.GetDirectories(f.Audit).Length==513&&!Directory.GetFiles(f.Journal,"operation-*.json").Any()&&Directory.GetFileSystemEntries(f.Journal).Length==0);f.AssertData();return Task.CompletedTask;
        });
    }
    private static void Assert(bool condition){if(!condition)throw new IOException("INTAKE_METADATA_ASSERTION");}
    private static void Refused(Action work){try{work();}catch(Exception error)when(error is IOException or JsonException){return;}throw new IOException("INTAKE_METADATA_EXPECTED_REFUSAL");}
    private sealed class Fixture
    {
        internal readonly InstalledStateFiles Files=InstalledStateFiles.Private(SafeDirectory.CreatePrivateSession(Path.GetTempPath()));
        internal readonly byte[] Bytes=[1,2,3,4];internal string Source=>new('a',64);internal string Digest=>Convert.ToHexStringLower(SHA256.HashData(Bytes));
        internal string Journal=>Files.PathOf("intake/recovery");internal string Audit=>Files.PathOf("intake/recovery-audit");
        internal Fixture(){Directory.CreateDirectory(Files.PathOf("intake"));Directory.CreateDirectory(Files.PathOf("payloads"));Directory.CreateDirectory(Files.PathOf("datasets"));File.WriteAllBytes(Files.PathOf("datasets/user-data"),[7,8,9]);}
        internal NativeIntakeRecovery Recovery(Action<string>? fault=null)=>new(Files,fault:fault);
        internal void Finish(){var recovery=Recovery();recovery.Reconcile(default);var archive=Files.PathOf("intake/archive-"+Digest+".zip");if(!File.Exists(archive)){using var output=recovery.Download(Source,Digest,Bytes.Length);output.Stream.Write(Bytes);output.Publish();}Assert(File.ReadAllBytes(archive).SequenceEqual(Bytes));}
        internal void AssertData()=>Assert(File.ReadAllBytes(Files.PathOf("datasets/user-data")).SequenceEqual(new byte[]{7,8,9}));
    }
}

using System.IO;
using System.Text.Json;
using Yahad.Install;
using Yahad.Setup;

// Supplementary read-only coverage using existing, independently owned fixtures.
// Does not retry link construction or relabel earlier construction failures.
internal static class ExistingLinkGuardsProbe
{
    private static string Fixture(string path)
    {
        path=Path.GetFullPath(path);
        var name=Path.GetFileName(path);
        if(!name.StartsWith("yahad-fixture-",StringComparison.Ordinal)||!Guid.TryParseExact(name[14..],"N",out _))throw new IOException("EXISTING_FIXTURE_NAME");
        PrivateSession.Validate(path);return path;
    }
    private static void Refused(Func<IDisposable> open)
    {
        try{using var lease=open();}
        catch(IOException e)when(e.Message=="NATIVE_REPARSE_KIND_OR_HARDLINK"){return;}
        throw new IOException("EXISTING_LINK_GUARD_NOT_OBSERVED");
    }
    internal static int Run(string hardRoot,string junctionRoot,string targetRoot,string output)
    {
        hardRoot=Fixture(hardRoot);junctionRoot=Fixture(junctionRoot);targetRoot=Fixture(targetRoot);
        var junction=Path.Combine(junctionRoot,"junction");
        var target=new DirectoryInfo(junction).LinkTarget;
        if(target is null||!Path.IsPathFullyQualified(target)||!string.Equals(Path.GetFullPath(target),targetRoot,StringComparison.OrdinalIgnoreCase)||
           (File.GetAttributes(junction)&FileAttributes.ReparsePoint)==0)throw new IOException("EXISTING_JUNCTION_TARGET");
        var cases=new List<object>();var failures=0;
        void Check(string name,Action run){try{run();cases.Add(new{name,status="PASS"});}catch(Exception e){failures++;cases.Add(new{name,status="FAIL",reason=e.GetType().Name,hresult=e.HResult});}}
        Check("existing synthetic hardlinked file is refused before its content is read",()=>Refused(()=>WindowsFileBoundary.OpenRead(Path.Combine(hardRoot,"receipt.json"))));
        Check("removal inspection refuses existing synthetic hardlink without deleting members",()=>
        {
            try{RemovalTree.Inspect(hardRoot,WindowsFileBoundary.RequireNoLink);}
            catch(IOException e)when(e.Message=="NATIVE_REPARSE_KIND_OR_HARDLINK"){return;}
            throw new IOException("EXISTING_REMOVAL_LINK_GUARD_NOT_OBSERVED");
        });
        Check("existing synthetic junction ancestor is refused before reading its file",()=>Refused(()=>WindowsFileBoundary.OpenRead(Path.Combine(junction,"receipt.json"))));
        output=PrivateSession.Create(output);
        var report=JsonSerializer.Serialize(new{suite="existing-native-link-guards-readonly",passed=cases.Count-failures,failed=failures,linkCreationAttempted=false,originalConstructionFailuresRemainRecorded=true,fixtureContentsModified=false,realScmInvoked=false,liveReceiptModified=false,hardRoot,junctionRoot,targetRoot,cases},new JsonSerializerOptions{WriteIndented=true});
        using(var file=new FileStream(Path.Combine(output,"report.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){file.Write(System.Text.Encoding.UTF8.GetBytes(report));}
        Console.WriteLine(report);return failures==0?0:1;
    }
}

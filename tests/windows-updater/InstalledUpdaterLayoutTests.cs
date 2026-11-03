using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;

internal static class InstalledUpdaterLayoutTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("updater layout reuses first-install roots and existing runtime image without replacing receipt",()=>
        {
            Assert(InstalledUpdaterLayout.StateRoot==FirstInstallProfile.StateRoot&&InstalledUpdaterLayout.BinaryRoot==FirstInstallProfile.BinaryRoot);
            Assert(WindowsInstallationReader.DescriptorPath==Path.Combine(FirstInstallProfile.StateRoot,"bootstrap.json"));
            Assert(InstalledUpdaterLayout.RuntimeExecutable==Path.Combine(FirstInstallProfile.BinaryRoot,"host","Yahad.ServiceHost.exe"));
            Assert(InstalledUpdaterLayout.AuthorityExecutable==Path.Combine(FirstInstallProfile.BinaryRoot,"bootstrap","authority","Yahad.Authority.exe"));
            Assert(InstalledUpdaterLayout.ProbeExecutable==Path.Combine(FirstInstallProfile.BinaryRoot,"bootstrap","probe","node","node.exe"));
            Assert(ServiceDefinition.Name==InstalledUpdaterLayout.RuntimeServiceName&&InstalledAuthorityReader.AuthorityService==InstalledUpdaterLayout.AuthorityServiceName);
            Assert(InstalledUpdaterLayout.AnchorFile=="update-authority.json"&&Path.GetFileName(FirstInstallProfile.ReceiptPath)=="installation.json");return Task.CompletedTask;
        });
        await check("only genuine missing updater anchor permits legacy discovery",()=>
        {
            Assert(!InstalledUpdaterLayout.AnchorPresent(()=>throw new FileNotFoundException()));
            Assert(!InstalledUpdaterLayout.AnchorPresent(()=>throw new DirectoryNotFoundException()));
            Assert(InstalledUpdaterLayout.AnchorPresent(()=>FileAttributes.ReadOnly));return Task.CompletedTask;
        });
        foreach(var kind in new[]{"access","sharing","reparse","directory"})await check("updater profile discovery fails closed on "+kind+" rather than falling back",()=>
        {
            Denied(()=>InstalledUpdaterLayout.AnchorPresent(()=>kind switch
            {"access"=>throw new UnauthorizedAccessException(),"sharing"=>throw new IOException("sharing violation"),"reparse"=>FileAttributes.ReparsePoint,_=>FileAttributes.Directory}));return Task.CompletedTask;
        });
        foreach(var kind in new[]{"receipt_format","missing_probe","wrong_service","noncanonical_id","extra_field"})await check("present updater anchor refuses "+kind+" and cannot become legacy receipt",()=>
        {
            var fields=Anchor();
            if(kind=="receipt_format")fields["Format"]=2;
            if(kind=="missing_probe")fields["ProbeSha256"]=null;
            if(kind=="wrong_service")fields["RuntimeService"]="Unrelated.Service";
            if(kind=="noncanonical_id")fields["InstallationId"]=new string('A',32);
            if(kind=="extra_field")fields["PackageDigest"]=new string('b',64);
            Assert(InstalledUpdaterLayout.AnchorPresent(()=>FileAttributes.Normal));Denied(()=>InstalledUpdaterLayout.RequireInstalledAnchor(JsonSerializer.SerializeToUtf8Bytes(fields)));return Task.CompletedTask;
        });
        await check("separate updater anchor preserves Format-2 first-install receipt and both anchor roles",()=>
        {
            var files=InstalledStateFiles.Private(SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-updater-layout")));var id=Guid.NewGuid().ToString("N");var hash=new string('a',64);
            var receipt=new FirstInstallReceipt(2,id,Guid.NewGuid().ToString("N"),hash,ApplicationRelease.Version,hash,32000,hash,"committed",hash);
            Assert(FirstInstallProfile.ValidateReceipt(receipt)==receipt);
            var path=files.PathOf("installation.json");var bytes=JsonSerializer.SerializeToUtf8Bytes(receipt);File.WriteAllBytes(path,bytes);
            var anchor=JsonSerializer.SerializeToUtf8Bytes(Anchor());File.WriteAllBytes(files.PathOf(InstalledUpdaterLayout.AnchorFile),anchor);InstalledUpdaterLayout.RequireInstalledAnchor(anchor);
            Assert(files.Role(path)==InstalledFileRole.Anchor&&files.Role(files.PathOf(InstalledUpdaterLayout.AnchorFile))==InstalledFileRole.Anchor);
            Assert(File.ReadAllBytes(path).SequenceEqual(bytes));return Task.CompletedTask;
        });
    }
    private static Dictionary<string,object?> Anchor()=>new()
    {
        ["Format"]=1,["InstallationId"]=new string('a',32),["RuntimeService"]=InstalledUpdaterLayout.RuntimeServiceName,
        ["AuthorityService"]=InstalledUpdaterLayout.AuthorityServiceName,
        ["IntakePolicyDigest"]=new string('b',64),["IntakePinDigest"]=new string('c',64),["ProbeSha256"]=new string('d',64)
    };
    private static void Assert(bool value){if(!value)throw new Exception("UPDATER_LAYOUT_ASSERTION");}
    private static void Denied(Action action)
    {try{action();}catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException){return;}throw new Exception("UPDATER_LAYOUT_UNEXPECTED_FALLBACK");}
}

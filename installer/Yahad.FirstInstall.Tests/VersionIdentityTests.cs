using System.IO;
using System.Text;
using System.Text.Json;
using Yahad.Install;
using Yahad.Setup;
using Yahad.Service;

internal static class VersionIdentityTests
{
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check,Func<bool,(byte[] Zip,byte[] Manifest)> package,string repo)
    {
        void Assert(bool value){if(!value)throw new IOException("VERSION_ASSERTION");}
        void Refuse(Action run){try{run();}catch(Exception e)when(e is IOException or JsonException){return;}throw new IOException("VERSION_ACCEPTED");}
        await check("embedded application version matches the source package rather than the old installer pin",()=>
        {
            using var source=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repo,"package.json")));
            Assert(ApplicationRelease.Version==source.RootElement.GetProperty("version").GetString());
            return Task.CompletedTask;
        });
        await check("canonical future versions parse without another native source edit",()=>
        {
            foreach(var version in new[]{"0.0.0","0.6.0","12.34.56"})Assert(ApplicationRelease.ParsePackageVersion(JsonSerializer.SerializeToUtf8Bytes(new{version}))==version);
            return Task.CompletedTask;
        });
        await check("invalid missing duplicate prerelease and oversized version metadata fails closed",()=>
        {
            foreach(var json in new[]{"{}","[]","null","{\"version\":7}","{\"version\":\"0.6.0\",\"version\":\"0.5.3\"}"})Refuse(()=>ApplicationRelease.ParsePackageVersion(Encoding.UTF8.GetBytes(json)));
            foreach(var version in new[]{"01.2.3","0.6.0-beta","0.6.0+build"," 0.6.0","0.6.0\n","0.6","١.٢.٣",new string('1',41)+".0.0"})Refuse(()=>ApplicationRelease.ParsePackageVersion(JsonSerializer.SerializeToUtf8Bytes(new{version})));
            Refuse(()=>ApplicationRelease.ParsePackageVersion(new byte[65537]));return Task.CompletedTask;
        });
        await check("current full installer package is admitted but legacy and foreign versions cannot be relabeled",()=>
        {
            var fixture=package(false);var manifest=NativeJson.Read<InstallPackageManifest>(fixture.Manifest);
            using(var current=new InstallPackage(new MemoryStream(fixture.Zip),fixture.Manifest))Assert(current.Manifest.Version==ApplicationRelease.Version);
            foreach(var version in new[]{"0.5.3","999.0.0"})Refuse(()=>{using var wrong=new InstallPackage(new MemoryStream(fixture.Zip),JsonSerializer.SerializeToUtf8Bytes(manifest with{Version=version}));});
            return Task.CompletedTask;
        });
        await check("service receipt admission shares the compiled identity and preserves repair barriers",()=>
        {
            var receipt=new FirstInstallReceipt(2,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),new string('a',64),ApplicationRelease.Version,new string('b',64),47123,new string('c',64),"committed",new string('d',64));
            Assert(FirstInstallProfile.ValidateReceipt(receipt)==receipt);
            foreach(var changed in new[]{receipt with{Version="0.5.3"},receipt with{Version="999.0.0"},receipt with{Format=1},receipt with{Phase="preparing"},receipt with{ReleaseDigest="invalid"}})Refuse(()=>FirstInstallProfile.ValidateReceipt(changed));
            return Task.CompletedTask;
        });
    }
}

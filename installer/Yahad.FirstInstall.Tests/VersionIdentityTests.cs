using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using Yahad.Install;
using Yahad.Setup;

// The infrastructure identity is installer/Directory.Build.props, compiled into every
// .NET image; the app's is its package and tree record, never a .NET constant
// (docs/WINDOWS-APP-PACKAGE-PLAN.md 4.3).
internal static class VersionIdentityTests
{
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check,string repo)
    {
        void Assert(bool value){if(!value)throw new IOException("VERSION_ASSERTION");}
        void RefuseCode(string code,Action run){try{run();}catch(IOException e)when(e.Message==code){return;}throw new IOException("VERSION_EXPECTED_"+code);}
        await check("compiled infrastructure identity is the one installer/Directory.Build.props declares",()=>
        {
            var properties=XDocument.Load(Path.Combine(repo,"installer","Directory.Build.props")).Descendants();
            string Property(string name)=>properties.Single(e=>e.Name.LocalName==name).Value;
            Assert(InfrastructureRelease.Version==Property("YahadInfrastructureVersion")&&ReleaseManifest.IsVersion(InfrastructureRelease.Version));
            Assert(InfrastructureRelease.ContractMin.ToString(System.Globalization.CultureInfo.InvariantCulture)==Property("YahadInfrastructureContractMin"));
            Assert(InfrastructureRelease.ContractMax.ToString(System.Globalization.CultureInfo.InvariantCulture)==Property("YahadInfrastructureContractMax"));
            Assert(InfrastructureRelease.ContractMin<=InfrastructureRelease.ContractMax&&InfrastructureRelease.Supports(InfrastructureRelease.ContractMin));
            Assert(TreeRecord.IsCommit(InfrastructureRelease.SourceCommit)&&InfrastructureRelease.CompiledSourceCommit(typeof(SetupEntry).Assembly)==InfrastructureRelease.SourceCommit);
            return Task.CompletedTask;
        });
        await check("no .NET image compiles the app version: package.json is not read by the engine",()=>
        {
            Assert(typeof(InfrastructureRelease).Assembly.GetManifestResourceNames().All(n=>!n.EndsWith("package.json",StringComparison.OrdinalIgnoreCase)));
            Assert(typeof(InfrastructureRelease).Assembly.GetType("Yahad.Install.ApplicationRelease") is null);
            // The payload-free engine (no embedded package) names the infrastructure it is.
            Assert(InstallPackage.IsEmbedded||InstallPackage.DisplayVersion()==InfrastructureRelease.Version);
            return Task.CompletedTask;
        });
        await check("a package of this infrastructure is admitted and neither track can be relabeled",()=>
        {
            using(var current=PairFixture.Open(PairFixture.Package()))
                Assert(current.Manifest.Infrastructure.Version==InfrastructureRelease.Version&&current.Manifest.App.Version=="1.0.0"&&current.Pair().Describe==InfrastructureRelease.Version+"/1.0.0");
            // The engine extracts only the infrastructure compiled into it.
            foreach(var version in new[]{"0.5.3","999.0.0"})
            {
                RefuseCode("PACKAGE_SOURCE",()=>{using var p=PairFixture.Open(PairFixture.Package(change:m=>m with{Infrastructure=m.Infrastructure with{Version=version}}));});
                RefuseCode("PACKAGE_SOURCE",()=>{using var p=PairFixture.Open(PairFixture.Package(infra:PairFixture.Infra(version)));});
                // The app's tree record carries its own version: the manifest cannot rename it.
                RefuseCode("PACKAGE_TREE_MISMATCH",()=>{using var p=PairFixture.Open(PairFixture.Package(change:m=>m with{App=m.App with{Version=version}}));});
            }
            RefuseCode("PACKAGE_SOURCE",()=>{using var p=PairFixture.Open(PairFixture.Package(change:m=>m with{Infrastructure=m.Infrastructure with{SourceCommit=new string(m.Infrastructure.SourceCommit[0]=='0'?'1':'0',40)}}));});
            RefuseCode("PACKAGE_TREE_MISMATCH",()=>{using var p=PairFixture.Open(PairFixture.Package(change:m=>m with{App=m.App with{SourceCommit=new string('b',40)}}));});
            return Task.CompletedTask;
        });
        await check("the package manifest is strict JSON",()=>
        {
            var fixture=PairFixture.Package();var text=System.Text.Encoding.UTF8.GetString(fixture.Manifest);
            RefuseCode("PACKAGE_FORMAT",()=>{using var p=new InstallPackage(new MemoryStream(fixture.Zip),System.Text.Encoding.UTF8.GetBytes(text[..^1]+",\"Unknown\":1}"));});
            RefuseCode("PACKAGE_FORMAT",()=>{using var p=new InstallPackage(new MemoryStream(fixture.Zip),JsonSerializer.SerializeToUtf8Bytes(new{Format=1,Version="1.0.0"}));});
            return Task.CompletedTask;
        });
    }
}

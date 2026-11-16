using System.Text.RegularExpressions;
using Yahad.Install;

internal static class InfrastructureReleaseTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        void Assert(bool value) { if (!value) throw new Exception("assertion failed"); }
        string Props(string name)
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var file = Path.Combine(directory.FullName, "Directory.Build.props");
                if (File.Exists(file)) return Regex.Match(File.ReadAllText(file), "<" + name + ">([^<]+)</" + name + ">").Groups[1].Value;
            }
            throw new Exception("Directory.Build.props not found");
        }
        await check("infrastructure release identity is the Directory.Build.props value", () =>
        {
            Assert(SemVer.IsVersion(InfrastructureRelease.Version) && InfrastructureRelease.Version == Props("YahadInfrastructureVersion"));
            Assert(InfrastructureRelease.ContractMin == 1 && InfrastructureRelease.ContractMax == 1);
            Assert(InfrastructureRelease.ContractMin.ToString() == Props("YahadInfrastructureContractMin") && InfrastructureRelease.ContractMax.ToString() == Props("YahadInfrastructureContractMax"));
            Assert(InfrastructureRelease.Supports(1) && !InfrastructureRelease.Supports(0) && !InfrastructureRelease.Supports(2));
            return Task.CompletedTask;
        });
        await check("infrastructure source commit is the compiled forty-hex revision", () =>
        {
            Assert(Regex.IsMatch(InfrastructureRelease.SourceCommit, @"\A[a-f0-9]{40}\z")); return Task.CompletedTask;
        });
    }
}

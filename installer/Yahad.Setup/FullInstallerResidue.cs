using System.IO;
namespace Yahad.Setup;
internal static class FullInstallerResidue
{
    internal static readonly string[] Metadata=["installer-state.json","full-installer-plan.json","installer-readiness.json","lan-preparation.json"];
    internal static bool IsOwnedName(string name)
    {
        foreach(var target in Metadata)
        {
            var prefix=target+".partial-";if(!name.StartsWith(prefix,StringComparison.Ordinal))continue;
            var suffix=name[prefix.Length..];return Guid.TryParseExact(suffix,"N",out var id)&&id.ToString("N")==suffix;
        }
        return false;
    }
}

namespace Yahad.Install;

internal enum SystemComponentKind {Authority,RuntimeHost,Owner,Probe,Helper}

// Fixed archive prefixes and live destinations are distinct. Probe is selected
// from its verified operational slot; the immutable baseline probe is untouched.
internal static class FixedSystemComponents
{
    internal const string SlotsDirectory="system-slots";
    internal static string SlotPrefix(SystemComponentKind kind)=>kind switch
    {
        SystemComponentKind.Authority=>"bootstrap/authority",SystemComponentKind.RuntimeHost=>"bootstrap/servicehost",
        SystemComponentKind.Owner=>"bootstrap/owner",SystemComponentKind.Probe=>"bootstrap/probe",SystemComponentKind.Helper=>"bootstrap/handoff",
        _=>throw new IOException("SYSTEM_COMPONENT_KIND")
    };
    internal static string EntryPoint(SystemComponentKind kind)=>kind switch
    {
        SystemComponentKind.Authority or SystemComponentKind.Helper=>"Yahad.Authority.exe",SystemComponentKind.RuntimeHost=>"Yahad.ServiceHost.exe",
        SystemComponentKind.Owner=>"Yahad.Owner.exe",SystemComponentKind.Probe=>"node/node.exe",_=>throw new IOException("SYSTEM_COMPONENT_KIND")
    };
    internal static string? ActiveDirectory(SystemComponentKind kind)=>kind switch
    {
        SystemComponentKind.Authority=>"bootstrap/authority",SystemComponentKind.RuntimeHost=>"host",SystemComponentKind.Owner=>"bootstrap/owner",
        SystemComponentKind.Helper=>"bootstrap/handoff",SystemComponentKind.Probe=>null,_=>throw new IOException("SYSTEM_COMPONENT_KIND")
    };
    internal static void Digest(string value)
    {if(value is null||!System.Text.RegularExpressions.Regex.IsMatch(value,"^[a-f0-9]{64}$"))throw new IOException("SYSTEM_COMPONENT_DIGEST");}
    internal static void Member(string path)
    {
        if(path is null||path.Length is <1 or >200||path.Contains('\\')||path.Contains(':')||path.StartsWith('/')||path.Split('/').Any(p=>p is "" or "." or ".."||p.EndsWith('.')||p.EndsWith(' ')||p.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new IOException("SYSTEM_COMPONENT_PATH");
        foreach(var part in path.Split('/'))
        {
            var stem=part.Split('.')[0].ToUpperInvariant();
            if(stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$"||stem.Length==4&&(stem.StartsWith("COM",StringComparison.Ordinal)||stem.StartsWith("LPT",StringComparison.Ordinal))&&(stem[3] is >= '0' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'))throw new IOException("SYSTEM_COMPONENT_PATH");
        }
    }
}
internal sealed class SystemComponentFile
{
    public string RelativePath{get;}public long Size{get;}public string Sha256{get;}
    internal SystemComponentFile(string relativePath,long size,string sha256)
    {FixedSystemComponents.Member(relativePath);FixedSystemComponents.Digest(sha256);if(size<0||size>512L*1024*1024)throw new IOException("SYSTEM_COMPONENT_FILE_BOUND");RelativePath=relativePath;Size=size;Sha256=sha256;}
}
internal sealed class SystemComponentDescriptor
{
    public SystemComponentKind Kind{get;}public string SlotRelativeDirectory{get;}public string EntryPoint{get;}public string EntrySha256{get;}
    public IReadOnlyList<SystemComponentFile> Files{get;}
    internal SystemComponentDescriptor(SystemComponentKind kind,string slotRelativeDirectory,string entryPoint,string entrySha256,IEnumerable<SystemComponentFile> files)
    {
        if(slotRelativeDirectory!=FixedSystemComponents.SlotPrefix(kind)||entryPoint!=FixedSystemComponents.EntryPoint(kind))throw new IOException("SYSTEM_COMPONENT_BINDING");
        FixedSystemComponents.Digest(entrySha256);var copy=files.Select(f=>new SystemComponentFile(f.RelativePath,f.Size,f.Sha256)).Take(50001).ToArray();
        if(copy.Length is <1 or >50000||copy.Sum(f=>f.Size)>2L*1024*1024*1024||copy.Select(f=>f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=copy.Length)throw new IOException("SYSTEM_COMPONENT_CLOSURE");
        var entry=copy.SingleOrDefault(f=>f.RelativePath==entryPoint);if(entry is null||entry.Size<1||entry.Sha256!=entrySha256)throw new IOException("SYSTEM_COMPONENT_ENTRY");
        Kind=kind;SlotRelativeDirectory=slotRelativeDirectory;EntryPoint=entryPoint;EntrySha256=entrySha256;Files=Array.AsReadOnly(copy);
    }
}

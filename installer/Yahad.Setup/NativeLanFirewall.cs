using System.IO;
using System.Runtime.InteropServices;
using Yahad.Install;
namespace Yahad.Setup;

// Called only by the elevated in-process Setup operation after explicit
// network consent. Tests use the pure LanFirewallRule model, never this adapter.
internal static class NativeLanFirewall
{
    private static dynamic Policy()=>Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")??throw new IOException("LAN_FIREWALL_UNAVAILABLE"))!;
    private static dynamic NewRule()=>Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")??throw new IOException("LAN_FIREWALL_UNAVAILABLE"))!;
    private static dynamic? Find(dynamic policy,string name)
    {
        dynamic? found=null;
        // Windows may contain several rules with the same display name. Never
        // pick one arbitrarily and then remove a different rule by name.
        foreach(dynamic rule in policy.Rules)
            if(string.Equals((string)rule.Name,name,StringComparison.OrdinalIgnoreCase))
            {if(found is not null)throw new IOException("LAN_FIREWALL_DUPLICATE");found=rule;}
        return found;
    }
    private static LanFirewallRule Read(dynamic rule)
    {
        object? interfaces=rule.Interfaces;
        if(interfaces is not null && (interfaces is not Array list||list.Length!=0))throw new IOException("LAN_FIREWALL_CHANGED");
        if((int)rule.EdgeTraversalOptions!=0)throw new IOException("LAN_FIREWALL_CHANGED");
        return new((string)rule.Name,(string)rule.Description,(string)rule.Grouping,
            (string)rule.ApplicationName,(string)rule.LocalAddresses,(string)rule.LocalPorts,(string)rule.RemoteAddresses,
            (string)rule.RemotePorts,(string)rule.InterfaceTypes,(int)rule.Profiles,(int)rule.Protocol,(int)rule.Direction,
            (int)rule.Action,(bool)rule.Enabled,(bool)rule.EdgeTraversal,(string?)rule.ServiceName??"");
    }
    internal static void RequireNoDrift(IReadOnlyList<LanFirewallRule> rules)
    {
        dynamic policy=Policy();
        try{foreach(var expected in rules){dynamic? observed=Find(policy,expected.Name);if(observed is not null&&!expected.Matches(Read(observed)))throw new IOException("LAN_FIREWALL_CHANGED");}}
        finally{Marshal.FinalReleaseComObject(policy);}
    }
    internal static void RequirePresentAndMatching(IReadOnlyList<LanFirewallRule> rules)
    {
        dynamic policy=Policy();
        try{foreach(var expected in rules){dynamic? observed=Find(policy,expected.Name);if(observed is null||!expected.Matches(Read(observed)))throw new IOException("LAN_FIREWALL_REPAIR_REQUIRED");}}
        finally{Marshal.FinalReleaseComObject(policy);}
    }
    internal static void Add(IReadOnlyList<LanFirewallRule> rules)
    {
        RequireNoDrift(rules);dynamic policy=Policy();
        try
        {
            foreach(var expected in rules)
            {
                dynamic? observed=Find(policy,expected.Name);
                if(observed is not null)continue;
                dynamic rule=NewRule();
                try
                {
                    rule.Name=expected.Name;rule.Description=expected.Description;rule.Grouping=expected.Grouping;
                    rule.ApplicationName=expected.ApplicationName;rule.Protocol=expected.Protocol;rule.Direction=expected.Direction;
                    rule.LocalPorts=expected.LocalPorts;rule.RemotePorts=expected.RemotePorts;rule.LocalAddresses=expected.LocalAddresses;
                    rule.RemoteAddresses=expected.RemoteAddresses;rule.InterfaceTypes=expected.InterfaceTypes;rule.Profiles=expected.Profiles;
                    rule.Action=expected.Action;rule.EdgeTraversal=expected.EdgeTraversal;rule.Enabled=expected.Enabled;
                    policy.Rules.Add(rule);
                }
                finally{Marshal.FinalReleaseComObject(rule);}
            }
        }
        finally{Marshal.FinalReleaseComObject(policy);}
    }
    internal static void Remove(IReadOnlyList<LanFirewallRule> rules)
    {
        RequireNoDrift(rules);dynamic policy=Policy();
        try{foreach(var expected in rules){if(Find(policy,expected.Name) is not null)policy.Rules.Remove(expected.Name);}}
        finally{Marshal.FinalReleaseComObject(policy);}
    }
}

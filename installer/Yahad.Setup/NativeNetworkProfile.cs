using System.IO;
using System.Runtime.InteropServices;

namespace Yahad.Setup;

// Read-only NLM adapter. GUID return values cannot travel through the dynamic
// IDispatch/VARIANT path: GetAdapterId then fails with E_INVALIDARG. Use the
// SDK's typed dual-interface vtable for that call instead. No category setter.
internal static class NativeNetworkProfile
{
    internal static int ReadCategory(string selectedAdapter)
    {
        if(!Guid.TryParse(selectedAdapter,out var selected)||selected==Guid.Empty)throw new IOException("NETWORK_SELECTION_CHANGED");
        dynamic manager=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))??throw new IOException("LAN_NETWORK_PROFILE_UNAVAILABLE"))!;
        try
        {
            var categories=new List<int>();
            foreach(dynamic network in manager.GetNetworks(1))
                foreach(object connection in network.GetNetworkConnections())
                    if(((INetworkConnectionIdentity)connection).GetAdapterId()==selected)categories.Add((int)network.GetCategory());
            return RequireConsistentCategory(categories);
        }
        finally{Marshal.FinalReleaseComObject(manager);}
    }
    internal static void RequirePrivateCategory(int category)
    {if(category!=1)throw new IOException("LAN_PRIVATE_PROFILE_REQUIRED");}
    internal static int RequireConsistentCategory(IReadOnlyList<int> categories)
    {
        // NLM permits multiple connections for one adapter. A Private first
        // match must never conceal a different category on another connection.
        if(categories.Count==0||categories.Any(category=>category!=categories[0]))throw new IOException("NETWORK_SELECTION_CHANGED");
        return categories[0];
    }

    // Exact order and types from Microsoft's netlistmgr.idl INetworkConnection.
    // Preceding slots must remain present even though only GetAdapterId is used.
    [ComImport,Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"),InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnectionIdentity
    {
        [DispId(1)] [return:MarshalAs(UnmanagedType.Interface)] object GetNetwork();
        [DispId(2)] bool IsConnectedToInternet { [return:MarshalAs(UnmanagedType.VariantBool)] get; }
        [DispId(3)] bool IsConnected { [return:MarshalAs(UnmanagedType.VariantBool)] get; }
        [DispId(4)] int GetConnectivity();
        [DispId(5)] Guid GetConnectionId();
        [DispId(6)] Guid GetAdapterId();
        [DispId(7)] int GetDomainType();
    }
}

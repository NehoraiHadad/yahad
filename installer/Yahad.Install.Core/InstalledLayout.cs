namespace Yahad.Install;

// Fixed machine locations: mutable state under ProgramData, administrator-owned
// binaries under Program Files (docs/WINDOWS-APP-PACKAGE-PLAN.md 4.1):
//   <binaries>\release-key.spki
//   <binaries>\active.json                     the active (infrastructure, app) pair
//   <binaries>\infra\<infra-version>\{host,updater,platform,runtime,legal,setup}\, infra-tree.json
//   <binaries>\apps\<app-version>\app\..., app-tree.json
// Every path below takes a version that was validated here, so no caller can name
// a directory outside the two trees.
internal static class InstalledLayout
{
    private const string Product="Yahad.FirstInstall";
    internal const string ActivePointerName="active.json",InfraTreeName="infra-tree.json",AppTreeName="app-tree.json",EngineName="Yahad.Setup.exe";
    internal static string StateRoot=>Path.Combine(MachineFolders.Required(Environment.SpecialFolder.CommonApplicationData),Product);
    internal static string BinaryRoot=>Path.Combine(MachineFolders.Required(Environment.SpecialFolder.ProgramFiles),Product);
    // The Updater's own folder, inside the state root's ACL: the Runtime can read it
    // but cannot write it, so it can never shape what the Updater stages or runs.
    internal static string UpdaterRoot=>Path.Combine(StateRoot,"updater");
    internal static string ActivePointerFile=>Path.Combine(BinaryRoot,ActivePointerName);
    internal static string InfraParent=>Path.Combine(BinaryRoot,"infra");
    internal static string AppParent=>Path.Combine(BinaryRoot,"apps");
    internal static string InfraRoot(string infrastructure)=>Path.Combine(InfraParent,Version(infrastructure));
    internal static string AppRoot(string app)=>Path.Combine(AppParent,Version(app));
    internal static string InfraTreeFile(string infrastructure)=>Path.Combine(InfraRoot(infrastructure),InfraTreeName);
    internal static string AppTreeFile(string app)=>Path.Combine(AppRoot(app),AppTreeName);
    // The application's working directory: /health/ready reads package.json from it.
    internal static string AppDirectory(string app)=>Path.Combine(AppRoot(app),"app");
    internal static string HostImage(string infrastructure)=>Path.Combine(InfraRoot(infrastructure),"host","Yahad.ServiceHost.exe");
    internal static string UpdaterImage(string infrastructure)=>Path.Combine(InfraRoot(infrastructure),"updater","Yahad.Updater.exe");
    internal static string UpdaterCommand(string infrastructure)=>"\""+UpdaterImage(infrastructure)+"\" --service";
    // Platform is infrastructure, beside the host and the Updater: the app tree
    // never carries it. The host hands this image to the app as YAHAD_PLATFORM_HELPER.
    internal static string PlatformImage(string infrastructure)=>Path.Combine(InfraRoot(infrastructure),"platform","Yahad.Platform.exe");
    internal static string Node(string infrastructure)=>Path.Combine(InfraRoot(infrastructure),"runtime","node","node.exe");
    internal static string Caddy(string infrastructure)=>Path.Combine(InfraRoot(infrastructure),"runtime","caddy","caddy.exe");
    // The payload-free Setup engine of this infrastructure (4.4): repair, uninstall,
    // the Apps & Features entry and app applies run it.
    internal static string EngineImage(string infrastructure)=>Path.Combine(InfraRoot(infrastructure),"setup",EngineName);
    internal static string Version(string value)=>SemVer.IsVersion(value)?value:throw new IOException("PAYLOAD_VERSION");
}

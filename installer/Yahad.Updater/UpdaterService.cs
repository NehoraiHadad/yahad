using System.IO;
using System.Security.Principal;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Updater;

// The SCM loop. Stop cancels everything; a failure is reported so the SCM's failure
// actions restart the service. Setup stops this service during an upgrade.
internal sealed class UpdaterService(IScmApi scm,Func<Action,CancellationToken,Task> work)
{
    private const uint StartStopHint=30000;
    internal int Run(){scm.Dispatch(ServiceDefinition.UpdaterName,_=>Main());return 0;}
    private void Main()
    {
        using var stop=new CancellationTokenSource();var gate=new object();var stopping=false;
        scm.Register(ServiceDefinition.UpdaterName,control=>
        {
            if(control is ServiceDefinition.Interrogate)return 0;
            if(control is not(ServiceDefinition.Stop or ServiceDefinition.Shutdown))return 120;
            lock(gate){if(!stopping){stopping=true;scm.Publish(new(ServiceState.StopPending,0,1,StartStopHint,0,0));}}
            stop.Cancel();return 0;
        });
        scm.Publish(new(ServiceState.StartPending,0,1,StartStopHint,0,0));
        uint specific=0;
        try{work(()=>{lock(gate){if(!stopping)scm.Publish(new(ServiceState.Running,0x1|0x4,0,0,0,0));}},stop.Token).GetAwaiter().GetResult();}
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception error){specific=1;_=ServiceStartupDiagnostic.TryRecord(StartupDiagnosticRole.Updater,error);}
        scm.Publish(new(ServiceState.Stopped,0,0,0,specific==0?0u:1066u,specific));
    }
    // Refuses to run from anywhere but this infrastructure's own updater folder.
    internal static async Task WorkAsync(Action ready,CancellationToken cancellation)
    {
        var expected=WindowsFileBoundary.CanonicalLocalPath(Path.GetDirectoryName(InstalledLayout.UpdaterImage(InfrastructureRelease.Version))!);
        var actual=WindowsFileBoundary.CanonicalLocalPath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)));
        if(!string.Equals(actual,expected,StringComparison.OrdinalIgnoreCase))throw new IOException("UPDATER_IMAGE_LOCATION");
        // The installed pair is read again for every decision (9.2); one this image is not
        // the infrastructure of stops the service here, as at every later read.
        Live();
        var runtime=(SecurityIdentifier)new NTAccount(ServiceDefinition.Account).Translate(typeof(SecurityIdentifier));
        var key=Path.Combine(InstalledLayout.BinaryRoot,TrustedReleaseKey.InstalledName);
        using var source=new GitHubReleaseSource();
        var engine=new UpdaterEngine(UpdaterPaths.Installed,Live,
            ()=>File.Exists(key)?NativeInstallFiles.ReadRecord(key):null,TrustedReleaseKey.Embedded,source,UpdateLauncher.Launch,()=>DateTimeOffset.UtcNow,
            failure=>ServiceStartupDiagnostic.TryRecordUpdate(failure.Operation,failure.Error,failure.Code,failure.Permanent,failure.From,failure.Version,failure.Cause));
        engine.Start(cancellation);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var pipe=new UpdaterPipe(UpdaterProtocol.PipeName,runtime,$"D:P(A;;GA;;;SY)(A;;GRGW;;;{runtime.Value})",engine.Handle,error=>ServiceStartupDiagnostic.TryRecord(StartupDiagnosticRole.Updater,error));
        var serving=pipe.RunAsync(linked.Token);ready();
        var scheduled=engine.RunScheduleAsync(linked.Token);
        // Either one ending means the service can no longer do its job.
        await Task.WhenAny(serving,scheduled);linked.Cancel();
        await Task.WhenAll(serving,scheduled);
    }
    private static ActivePointer Live(){var pointer=ActivePointer.Read();pointer.RequireThisInfrastructure();return pointer;}
}

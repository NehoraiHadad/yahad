using System.Security.Principal;
using Yahad.Install;
using Yahad.Updater;

// The Updater's pipe in a process that has done nothing else, as in the service: what
// the server first needs while it identifies a client is not loaded yet. Runs before
// anything else in this executable, and only its parent ever connects.
internal static class UpdaterPipeProbe
{
    internal static async Task<int> RunAsync(string name,string allowed)
    {
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var serving=new UpdaterPipe(name,new SecurityIdentifier(allowed),$"D:P(A;;GA;;;{allowed})",_=>new(1,true,"PROBE"),_=>{}).RunAsync(limit.Token);
        Console.WriteLine("READY");
        try{await serving;}catch(OperationCanceledException){}
        return 0;
    }
}

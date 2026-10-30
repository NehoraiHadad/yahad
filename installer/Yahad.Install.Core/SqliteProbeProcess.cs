using System.Text.Json;
namespace Yahad.Install;

public sealed class ProbeOwnershipException:IOException
{
    private readonly SqliteProbeProcess.Session session;
    internal ProbeOwnershipException(SqliteProbeProcess.Session session,Exception failure):base("SQLITE_PROBE_QUIESCENCE_UNCONFIRMED",failure){this.session=session;}
    public IContainedAppSession OwnedSession=>session;
    public bool QuiescenceConfirmed=>session.Quiescent;
    public bool CleanupComplete=>session.Disposed;
    public void Retain(IDisposable resource)=>session.Retain(resource);
    internal void KeepAlive(object owner)=>session.Owners.Add(owner);
    internal void AfterCleanup(Action action)=>session.After.Add(action);
    public async Task RetryCleanupAsync(CancellationToken cancellation)
    {await session.TerminateAndWaitForTreeEmptyAsync(cancellation);await session.DisposeAsync();}
}
internal static class SqliteProbeProcess
{
    internal static async Task<int> ReadAsync(string node,string script,string database,string temporary,string output,
        TimeSpan timeout,CancellationToken cancellation,JobProcessFaultInjection? fault=null)
    {
        Session? session=null;
        try
        {
            JobProcess job;
            try{job=fault is null?JobProcess.Start(node,["-e",script,database,output],temporary,LaunchEnvironment.Clean(temporary)):
                JobProcess.StartWithFault(node,["-e",script,database,output],temporary,LaunchEnvironment.Clean(temporary),null,fault);}
            catch(JobStartQuiescenceUnconfirmedException e){session=new(e.OwnedProcess,output);throw;}
            session=new(job,output);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(timeout);
            if(await job.WaitAsync(deadline.Token)!=0)throw new IOException("SQLITE_PROBE_REFUSED");await job.WaitForTreeEmptyAsync(deadline.Token);session.Quiescent=true;
            int schema;
            {using var file=WindowsFileBoundary.OpenPrivateRead(output);using var json=JsonDocument.Parse(file.Small(4096));
            schema=json.RootElement.GetProperty("schema").GetInt32();if(schema<1||schema>100000)throw new IOException("SQLITE_PROBE_SCHEMA");}
            await session.DisposeAsync();return schema;
        }
        catch(Exception failure)
        {
            if(session is null)throw;
            try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await session.TerminateAndWaitForTreeEmptyAsync(cleanup.Token);await session.DisposeAsync();}
            catch(Exception cleanup)
            {var owned=new ProbeOwnershipException(session,new AggregateException(failure,cleanup));if(fault is not null)fault.FailedStartSession=session;throw owned;}
            throw;
        }
    }
    internal sealed class Session(JobProcess job,string output):IContainedAppSession
    {
        internal bool Quiescent;private bool disposed;internal bool Disposed=>disposed;
        private readonly List<IDisposable> resources=[];internal readonly List<object> Owners=[];internal readonly List<Action> After=[];
        internal void Retain(IDisposable resource)=>resources.Add(resource);
        public Task RequestGracefulStopAsync(CancellationToken cancellation)=>Task.FromException(new IOException("FAILED_PROBE_HAS_NO_GRACEFUL_CHANNEL"));
        public Task<int> WaitForRootExitAsync(CancellationToken cancellation)=>job.WaitAsync(cancellation);
        public Task WaitForTreeEmptyAsync(CancellationToken cancellation)=>job.WaitForTreeEmptyAsync(cancellation);
        public async Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation)
        {if(disposed||Quiescent)return;await job.TerminateAndWaitForTreeEmptyAsync(cancellation);Quiescent=true;}
        public ValueTask DisposeAsync()
        {
            if(disposed)return ValueTask.CompletedTask;if(!Quiescent)throw new IOException("PROBE_DISPOSE_WITHOUT_QUIESCENCE");
            if(File.Exists(output))File.Delete(output);job.Dispose();
            while(resources.Count>0){var i=resources.Count-1;resources[i].Dispose();resources.RemoveAt(i);}
            foreach(var action in After)action();After.Clear();Owners.Clear();disposed=true;return ValueTask.CompletedTask;
        }
    }
}

using System.IO;
using System.Reflection;
using System.Text.Json;
using Yahad.Setup;
internal static class ElevationFramingRegression
{
    internal sealed record Result(bool Ok,bool TimedOut,int QueuedUiContinuations,bool FrameValid);
    private sealed class BlockedUi:SynchronizationContext
    {internal int Posts;public override void Post(SendOrPostCallback action,object? state)=>Interlocked.Increment(ref Posts);}
    private sealed class AsyncWrite:MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,CancellationToken cancellation=default)
        {await Task.Delay(10,cancellation).ConfigureAwait(false);base.Write(bytes.Span);}
    }
    internal static async Task<Result> RunAsync()
    {
        var context=new BlockedUi();var stream=new AsyncWrite();var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker=new Thread(()=>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var method=typeof(SetupElevation).GetMethod("WriteAsync",BindingFlags.NonPublic|BindingFlags.Static)!;
                ((Task)method.Invoke(null,[stream,new SetupWire(1,"progress",new SetupProgress("checking",0),null),CancellationToken.None])!).GetAwaiter().GetResult();
                done.TrySetResult(true);
            }
            catch{done.TrySetResult(false);}
        }){IsBackground=true};worker.Start();
        if(await Task.WhenAny(done.Task,Task.Delay(1500))!=done.Task)return new(false,true,context.Posts,false);
        var bytes=stream.ToArray();var valid=false;
        if(bytes.Length>4&&BitConverter.ToInt32(bytes,0)==bytes.Length-4)
        {var frame=JsonSerializer.Deserialize<SetupWire>(bytes.AsSpan(4));valid=frame?.Format==1&&frame.Kind=="progress"&&frame.Progress?.Phase=="checking";}
        var ok=await done.Task&&valid&&context.Posts==0;stream.Dispose();return new(ok,false,context.Posts,valid);
    }
}

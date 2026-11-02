using System.Collections.Concurrent;
namespace Yahad.Authority;

// Strong roots survive ephemeral kernel/exception graphs. Entry removal follows
// the owner's observed cleanup; this registry is not an authorization grant.
internal static class NativeCleanupOwnershipGate
{
    private sealed class Entry(Func<CancellationToken,Task> cleanup,bool active)
    {internal Func<CancellationToken,Task> Cleanup{get;}=cleanup;internal SemaphoreSlim Serial{get;}=new(1,1);internal volatile bool Active=active;}
    private static readonly ConcurrentDictionary<object,Entry> owners=new(ReferenceEqualityComparer.Instance);
    internal static int Count=>owners.Count;
    internal static void RequireClear(){if(!owners.IsEmpty)throw new IOException("NATIVE_CLEANUP_OWNER_RETAINED");}
    internal static void Retain(object owner,Func<CancellationToken,Task> cleanup,bool active=false)
    {if(!owners.TryAdd(owner,new(cleanup,active)))throw new IOException("NATIVE_CLEANUP_OWNER_ALREADY_RETAINED");}
    internal static void MarkUnresolved(object owner)
    {if(!owners.TryGetValue(owner,out var entry))throw new IOException("NATIVE_CLEANUP_OWNER_MISSING");entry.Active=false;}
    internal static void CompleteObserved(object owner)=>owners.TryRemove(owner,out _);
    internal static async Task RetryAsync(object owner,CancellationToken cancellation)
    {
        if(!owners.TryGetValue(owner,out var entry))return;await entry.Serial.WaitAsync(cancellation);
        try
        {
            if(!owners.TryGetValue(owner,out var current)||!ReferenceEquals(current,entry))return;
            if(entry.Active)throw new IOException("NATIVE_CLEANUP_OBSERVATION_STILL_ACTIVE");
            await entry.Cleanup(cancellation);owners.TryRemove(owner,out _);
        }
        finally{entry.Serial.Release();}
    }
    internal static async Task RetryRetainedAsync(CancellationToken cancellation)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellation);budget.CancelAfter(TimeSpan.FromSeconds(30));
        var snapshot=owners.ToArray();if(snapshot.Length>128)throw new IOException("NATIVE_CLEANUP_OWNER_CATALOG_BOUND");var errors=new List<Exception>();
        foreach(var owner in snapshot)
        {
            budget.Token.ThrowIfCancellationRequested();if(owner.Value.Active)continue;
            try{await RetryAsync(owner.Key,budget.Token);}catch(OperationCanceledException)when(budget.IsCancellationRequested){throw;}catch(Exception failure){errors.Add(failure);}
        }
        if(errors.Count!=0)throw new IOException("NATIVE_CLEANUP_RETRY_UNCONFIRMED",new AggregateException(errors));
    }
}

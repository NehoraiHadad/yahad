namespace Yahad.Authority;

// Shared by normal activation and explicit owner recovery, only after actual
// retained runtime health. The independent update journal remains the startup
// gate until its terminal record is durable, even if an RPC reply was lost.
internal static class UpdateCompletionProtocol
{
    internal static void Complete(Action publishDone,Action terminal,Action expose,Action<string>? fault=null)
    {
        fault?.Invoke("before-rpc-done");publishDone();fault?.Invoke("after-rpc-done");
        terminal();fault?.Invoke("after-terminal-journal");expose();
    }
}

using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
namespace Yahad.Authority;

internal static class AuthorityEndpointConnection
{
    // One bounded frame per connection. The same parser serves native and memory streams.
    internal static async Task ServeAsync(Stream stream,AuthorityEndpointRouter router,AuthorityEndpointRole role,AuthorityPeer peer,CancellationToken cancellation)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var header=new byte[4];await stream.ReadExactlyAsync(header,deadline.Token);var length=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length is <2 or >32768)throw new IOException("AUTHORITY_FRAME_BOUND");var body=new byte[length];await stream.ReadExactlyAsync(body,deadline.Token);
        var reply=await router.DispatchAsync(role,peer,body,deadline.Token);var encoded=JsonSerializer.SerializeToUtf8Bytes(reply);
        if(encoded.Length>32768)throw new IOException("AUTHORITY_REPLY_BOUND");BinaryPrimitives.WriteInt32LittleEndian(header,encoded.Length);await stream.WriteAsync(header,deadline.Token);await stream.WriteAsync(encoded,deadline.Token);await stream.FlushAsync(deadline.Token);
    }
    internal static Task ServeNativeConnectedAsync(Stream stream,SafePipeHandle connected,AuthorityEndpointRouter router,AuthorityEndpointRole role,CancellationToken cancellation)
    {
        // Caller must supply an accepted local pipe. No listener is constructed
        // here. Native identification is synchronous and fully reverted first.
        return NativeAsync();
        async Task NativeAsync()
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var header=new byte[4];await stream.ReadExactlyAsync(header,deadline.Token);var length=BinaryPrimitives.ReadInt32LittleEndian(header);
            if(length is <2 or >32768)throw new IOException("AUTHORITY_FRAME_BOUND");var body=new byte[length];await stream.ReadExactlyAsync(body,deadline.Token);
            // Identify only after reading a message, as required by the Windows pipe API.
            var peer=WindowsAuthorityPeer.Native().Read(connected);var reply=await router.DispatchAsync(role,peer,body,deadline.Token);var bytes=JsonSerializer.SerializeToUtf8Bytes(reply);
            if(bytes.Length>32768)throw new IOException("AUTHORITY_REPLY_BOUND");BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);await stream.WriteAsync(header,deadline.Token);await stream.WriteAsync(bytes,deadline.Token);await stream.FlushAsync(deadline.Token);
        }
    }
}

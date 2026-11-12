using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace PotDanmaku.App;

/// <summary>Starting the same configuration again opens its existing settings window.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly string name = "PotDanmaku-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "|" + AppPaths.DataDirectory.ToLowerInvariant())))[..20];
    private readonly Mutex mutex;
    private readonly CancellationTokenSource cancellation = new();
    private bool owns;
    public SingleInstance() => mutex = new Mutex(false, name);
    public bool TryAcquire()
    {
        try { owns = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; }
        return owns;
    }
    public async Task OpenExisting()
    {
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000);
        await client.WriteAsync(new byte[] { 1 }); await client.FlushAsync();
    }
    public void Listen(Action openSettings)
    {
        var token = cancellation.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(token);
                    byte[] request = new byte[1];
                    if (await pipe.ReadAsync(request, token).AsTask().WaitAsync(TimeSpan.FromSeconds(2), token) == 1 && request[0] == 1)
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(openSettings);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (ex is System.IO.IOException or TimeoutException)
                { AppLog.Write("重复启动请求未完成：" + ex.Message); }
            }
        });
    }
    public void Dispose()
    {
        cancellation.Cancel(); cancellation.Dispose();
        if (owns) { mutex.ReleaseMutex(); owns = false; }
        mutex.Dispose();
    }
}

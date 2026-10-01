using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FeatherBrowser.Services;

/// <summary>同一用户数据目录只运行一个宿主；再次启动通过本机管道打开新窗口。</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipeName;
    public bool IsOwner { get; }

    public SingleInstance(string dataRoot)
    {
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant())))[..32];
        _pipeName = "FeatherBrowser-" + id;
        _mutex = new Mutex(false, @"Local\" + _pipeName);
        try { IsOwner = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsOwner = true; }
    }

    public void Listen(Action<string> openWindow) => _ = ListenAsync(openWindow);

    private async Task ListenAsync(Action<string> openWindow)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                string command = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                if (command != null && command.Length <= 65536) openWindow(JsonSerializer.Deserialize<string>(command));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warn("接收新窗口请求失败: " + ex.Message); }
        }
    }

    public async Task ForwardAsync(string url)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(10000).ConfigureAwait(false);
        using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(url)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (IsOwner) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

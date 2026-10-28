using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Scrappy.Bench;

/// <summary>
/// A one-endpoint stand-in for the tablet's <c>GET /status</c>, on loopback. Same shape as the
/// responder in <c>Scrappy.Verify</c>: accept, answer, close. Port 0 means the OS picks one, so two
/// copies of the benchmark can run at once.
/// </summary>
internal sealed class StatusResponder : IDisposable
{
    private readonly TcpListener _listener;
    private readonly byte[] _response;
    private readonly CancellationTokenSource _stop = new();

    public StatusResponder(string? body = null)
    {
        _response = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body ?? DefaultBody)}\r\n" +
            $"Connection: close\r\n\r\n{body ?? DefaultBody}");

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    /// <summary>What a 0.2.x tablet answers: the fields the PC app actually reads.</summary>
    public const string DefaultBody = """
        {"name":"25091RP04C","model":"25091RP04C","android":"15","port":27183,
         "phase":"Waiting for the PC","message":"","lastError":"","wiredReady":true,
         "version":"0.2.1","width":2560,"height":1708,"refreshHz":120,
         "refreshModes":[60,90,120],"h264Hardware":true,"h265Hardware":true,"av1Hardware":false,
         "freeStorageMb":41220}
        """;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        // Read the request first, like a real server: answering before the client has
                        // finished writing can make Windows reset the connection and the client sees
                        // an error instead of the body. That would be a flaky benchmark, not a result.
                        var buffer = new byte[1024];
                        int read = await client.GetStream().ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            return;
                        }

                        await client.GetStream().WriteAsync(_response, _stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // The client gave up; nothing to report.
                    }
                }
            });
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}

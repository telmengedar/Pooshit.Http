using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Http.Tests.TestSupport;

/// <summary>
/// single-shot http server on a loopback socket, writing a raw response whose content type and body bytes are dictated verbatim
/// </summary>
public class LoopbackServer : IDisposable {
    readonly TcpListener listener;

    /// <summary>
    /// creates a new <see cref="LoopbackServer"/>
    /// </summary>
    /// <param name="mediaType">value of the content type header, or null to omit the header entirely</param>
    /// <param name="body">response body written verbatim, with a matching content length</param>
    public LoopbackServer(string? mediaType, byte[] body) {
        listener = new(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => Serve(mediaType, body));
    }

    /// <summary>
    /// port the server listens on
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// url of the served resource
    /// </summary>
    public string Url => $"http://127.0.0.1:{Port}/probe";

    async Task Serve(string? mediaType, byte[] body) {
        try {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();

            byte[] buffer = new byte[8192];
            int total = 0;
            while (total < buffer.Length) {
                int read = await stream.ReadAsync(buffer, total, buffer.Length - total);
                if (read <= 0)
                    break;
                total += read;
                if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n"))
                    break;
            }

            StringBuilder head = new();
            head.Append("HTTP/1.1 200 OK\r\n");
            if (mediaType != null)
                head.Append($"Content-Type: {mediaType}\r\n");
            head.Append($"Content-Length: {body.Length}\r\n");
            head.Append("Connection: close\r\n\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            await stream.WriteAsync(headBytes, 0, headBytes.Length);
            await stream.WriteAsync(body, 0, body.Length);
            await stream.FlushAsync();
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    /// <inheritdoc />
    public void Dispose() {
        listener.Stop();
    }
}

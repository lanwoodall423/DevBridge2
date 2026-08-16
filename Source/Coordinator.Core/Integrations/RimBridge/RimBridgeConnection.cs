using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DevBridge.Coordinator;

internal sealed class RimBridgeProtocolException : Exception
{
    internal RimBridgeProtocolException(string code, string message, bool authenticationFailure = false)
        : base(message)
    {
        Code = code;
        AuthenticationFailure = authenticationFailure;
    }

    internal string Code { get; }
    internal bool AuthenticationFailure { get; }
}

internal sealed class RimBridgeConnection : IDisposable
{
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly DateTime deadlineUtc;

    private RimBridgeConnection(TcpClient client, NetworkStream stream, DateTime deadlineUtc)
    {
        this.client = client;
        this.stream = stream;
        this.deadlineUtc = deadlineUtc;
    }

    internal static RimBridgeConnection Open(RimBridgeEndpoint endpoint, string expectedLaunchId,
        TimeSpan timeout)
    {
        if (endpoint == null || !endpoint.IsValid)
            throw new RimBridgeProtocolException("RIMBRIDGE_ENDPOINT_NOT_FOUND",
                "no valid loopback RimBridge endpoint is available");

        TimeSpan bounded = Bound(timeout);
        TcpClient client = new();
        try
        {
            Task connect = client.ConnectAsync(endpoint.Host, endpoint.Port);
            try
            {
                if (!connect.Wait(bounded))
                    throw new TimeoutException("RimBridge connection timed out.");
            }
            catch (AggregateException exception) when (exception.InnerException is SocketException socket)
            {
                throw socket;
            }

            if (!client.Connected)
                throw new SocketException((int)SocketError.NotConnected);

            int milliseconds = Math.Max(1, (int)Math.Min(int.MaxValue, bounded.TotalMilliseconds));
            client.ReceiveTimeout = milliseconds;
            client.SendTimeout = milliseconds;
            NetworkStream stream = client.GetStream();
            RimBridgeConnection connection = new(client, stream, DateTime.UtcNow + bounded);
            GabpResponseEnvelope welcome = connection.Request("session/hello",
                RimBridgeProtocolContract.SessionHello(endpoint.Token,
                    ComponentVersions.CoordinatorHandshakeVersion(), "RimWorld", expectedLaunchId));
            connection.ThrowIfError(welcome, "session/hello");
            return connection;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal GabpResponseEnvelope Request(string method, object parameters)
    {
        EnsureBeforeDeadline();
        string id = Guid.NewGuid().ToString("D");
        SendFrame(RimBridgeProtocolContract.Request(method, id, parameters));

        while (true)
        {
            using JsonDocument response = ReadFrame();
            if (RimBridgeProtocolContract.IsEvent(response.RootElement))
                continue;
            return RimBridgeProtocolContract.ParseResponse(response.RootElement, id);
        }
    }

    private void ThrowIfError(GabpResponseEnvelope response, string method)
    {
        if (response.Error == null)
        {
            if (!response.Result.HasValue)
                throw new RimBridgeProtocolException("RIMBRIDGE_PROTOCOL_ERROR",
                    "RimBridge returned no result for " + method + ".");
            return;
        }

        if (response.Error.Code == RimBridgeProtocolContract.AuthenticationFailed)
            throw new RimBridgeProtocolException("RIMBRIDGE_AUTH_FAILED",
                "RimBridge rejected the bridge credentials.", authenticationFailure: true);
        throw new RimBridgeProtocolException("RIMBRIDGE_PROTOCOL_ERROR",
            "RimBridge rejected " + method + ": " +
            (response.Error.Message ?? "unknown protocol error"));
    }

    private void SendFrame(object message)
    {
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, CoordinatorSerialization.JsonOptions));
        if (body.Length > RimBridgeProtocolContract.MaxMessageBytes)
            throw new RimBridgeProtocolException("RIMBRIDGE_PROTOCOL_ERROR",
                "RimBridge request exceeded the bounded message size.");

        byte[] header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length +
            "\r\nContent-Type: application/json\r\n\r\n");
        stream.Write(header, 0, header.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private JsonDocument ReadFrame()
    {
        List<byte> header = new();
        while (true)
        {
            int value = ReadByte();
            if (value < 0)
                throw new IOException("RimBridge closed the connection before returning a response.");
            header.Add((byte)value);
            if (header.Count > RimBridgeProtocolContract.MaxHeaderBytes)
                throw new InvalidDataException("RimBridge response headers exceeded the bounded size.");
            if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' &&
                header[^2] == '\r' && header[^1] == '\n')
                break;
        }

        int length = 0;
        int contentLengthCount = 0;
        string headerText = Encoding.ASCII.GetString(header.ToArray());
        foreach (string line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                continue;
            contentLengthCount++;
            if (contentLengthCount > 1 ||
                !int.TryParse(line.Substring("Content-Length:".Length).Trim(), out int parsed) ||
                parsed <= 0 || parsed > RimBridgeProtocolContract.MaxMessageBytes)
                throw new InvalidDataException("RimBridge response contained an invalid Content-Length.");
            length = parsed;
        }
        if (contentLengthCount != 1 || length <= 0 ||
            length > RimBridgeProtocolContract.MaxMessageBytes)
            throw new InvalidDataException("RimBridge response did not contain a bounded Content-Length.");

        byte[] body = new byte[length];
        int offset = 0;
        while (offset < body.Length)
        {
            EnsureBeforeDeadline();
            int read;
            try
            {
                read = stream.Read(body, offset, body.Length - offset);
            }
            catch (IOException exception) when (DateTime.UtcNow >= deadlineUtc ||
                                                IsSocketTimeout(exception))
            {
                throw new TimeoutException("RimBridge response timed out.");
            }
            if (read <= 0)
                throw new IOException("RimBridge closed the response before it was complete.");
            offset += read;
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new RimBridgeProtocolException("RIMBRIDGE_PROTOCOL_ERROR",
                "RimBridge returned invalid JSON: " + exception.Message);
        }
    }

    private int ReadByte()
    {
        EnsureBeforeDeadline();
        byte[] one = new byte[1];
        int read;
        try
        {
            read = stream.Read(one, 0, 1);
        }
        catch (IOException exception) when (DateTime.UtcNow >= deadlineUtc ||
                                            IsSocketTimeout(exception))
        {
            throw new TimeoutException("RimBridge response timed out.");
        }
        return read == 0 ? -1 : one[0];
    }

    private void EnsureBeforeDeadline()
    {
        if (DateTime.UtcNow >= deadlineUtc)
            throw new TimeoutException("RimBridge request exceeded the bounded timeout.");
    }

    private static bool IsSocketTimeout(IOException exception)
    {
        SocketException socket = exception.InnerException as SocketException;
        return socket?.SocketErrorCode == SocketError.TimedOut;
    }

    private static TimeSpan Bound(TimeSpan timeout) => timeout <= TimeSpan.Zero
        ? TimeSpan.FromMilliseconds(1)
        : timeout > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : timeout;

    public void Dispose()
    {
        stream.Dispose();
        client.Dispose();
    }
}

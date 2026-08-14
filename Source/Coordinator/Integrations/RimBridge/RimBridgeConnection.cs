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
    private const string ProtocolVersion = "gabp/1";
    private const int MaxHeaderBytes = 8192;
    private const int MaxMessageBytes = 16 * 1024 * 1024;
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
            using JsonDocument welcome = connection.Request("session/hello", new Dictionary<string, object>
            {
                ["token"] = endpoint.Token,
                ["bridgeVersion"] = ComponentVersions.CoordinatorHandshakeVersion(),
                ["platform"] = "RimWorld",
                ["launchId"] = expectedLaunchId,
                ["clientInfo"] = "DevBridge-controlled route"
            });
            connection.ThrowIfError(welcome.RootElement, "session/hello");
            return connection;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal JsonDocument Request(string method, object parameters)
    {
        EnsureBeforeDeadline();
        string id = Guid.NewGuid().ToString("N");
        SendFrame(new Dictionary<string, object>
        {
            ["v"] = ProtocolVersion,
            ["type"] = "request",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new Dictionary<string, object>()
        });

        while (true)
        {
            JsonDocument response = ReadFrame();
            if (!response.RootElement.TryGetProperty("id", out JsonElement responseId) ||
                !string.Equals(responseId.GetString(), id, StringComparison.Ordinal))
            {
                response.Dispose();
                continue;
            }

            return response;
        }
    }

    private void ThrowIfError(JsonElement root, string method)
    {
        if (!root.TryGetProperty("error", out JsonElement error) ||
            error.ValueKind != JsonValueKind.Object)
        {
            if (!root.TryGetProperty("result", out _))
                throw new RimBridgeProtocolException("RIMBRIDGE_PROTOCOL_ERROR",
                    "RimBridge returned no result for " + method + ".");
            return;
        }

        int code = error.TryGetProperty("code", out JsonElement codeValue) &&
                   codeValue.TryGetInt32(out int parsed) ? parsed : 0;
        string message = error.TryGetProperty("message", out JsonElement messageValue) &&
                         messageValue.ValueKind == JsonValueKind.String
            ? messageValue.GetString()
            : "RimBridge rejected " + method + ".";
        if (code == -31000)
            throw new RimBridgeProtocolException("RIMBRIDGE_AUTH_FAILED",
                "RimBridge rejected the bridge credentials.", authenticationFailure: true);
        throw new RimBridgeProtocolException("RIMBRIDGE_PROTOCOL_ERROR",
            "RimBridge rejected " + method + ": " + message);
    }

    private void SendFrame(object message)
    {
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Program.JsonOptions));
        if (body.Length > MaxMessageBytes)
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
            if (header.Count > MaxHeaderBytes)
                throw new InvalidDataException("RimBridge response headers exceeded the bounded size.");
            if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' &&
                header[^2] == '\r' && header[^1] == '\n')
                break;
        }

        int length = 0;
        string headerText = Encoding.ASCII.GetString(header.ToArray());
        foreach (string line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line.Substring("Content-Length:".Length).Trim(), out int parsed))
                length = parsed;
        }
        if (length <= 0 || length > MaxMessageBytes)
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
            catch (IOException) when (DateTime.UtcNow >= deadlineUtc)
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
        catch (IOException) when (DateTime.UtcNow >= deadlineUtc)
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

    private static TimeSpan Bound(TimeSpan timeout) => timeout <= TimeSpan.Zero
        ? TimeSpan.FromMilliseconds(1)
        : timeout > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : timeout;

    public void Dispose()
    {
        stream.Dispose();
        client.Dispose();
    }
}

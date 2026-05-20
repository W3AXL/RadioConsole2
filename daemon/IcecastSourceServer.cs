using Serilog;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace daemon
{
    internal sealed class IcecastSourceRequest
    {
        public string Method { get; init; } = "";
        public string Mount { get; init; } = "";
        public string Protocol { get; init; } = "";
        public Dictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Stream Body { get; init; } = Stream.Null;
        public IPEndPoint RemoteEndPoint { get; init; }
    }

    internal sealed class IcecastSourceServer
    {
        private readonly IPAddress listenAddress;
        private readonly int listenPort;
        private readonly string mount;
        private readonly string sourcePassword;
        private readonly Func<IcecastSourceRequest, CancellationToken, Task> sourceHandler;
        private readonly Action<IcecastSourceRequest> metadataHandler;
        private readonly object clientLock = new object();
        private TcpListener listener;
        private TcpClient activeClient;
        private CancellationTokenSource stopCts;
        private Task acceptTask;

        public IcecastSourceServer(
            IPAddress listenAddress,
            int listenPort,
            string mount,
            string sourcePassword,
            Func<IcecastSourceRequest, CancellationToken, Task> sourceHandler,
            Action<IcecastSourceRequest> metadataHandler = null)
        {
            this.listenAddress = listenAddress;
            this.listenPort = listenPort;
            this.mount = NormalizeMount(mount);
            this.sourcePassword = sourcePassword ?? "";
            this.sourceHandler = sourceHandler;
            this.metadataHandler = metadataHandler;
        }

        public void Start()
        {
            stopCts = new CancellationTokenSource();
            listener = new TcpListener(listenAddress, listenPort);
            listener.Start();
            acceptTask = Task.Run(() => AcceptLoop(stopCts.Token));
            Log.Information("Starting sdrtrunk Icecast source listener on {address}:{port}{mount}", listenAddress, listenPort, mount);
        }

        public async Task Stop()
        {
            stopCts?.Cancel();

            lock (clientLock)
            {
                activeClient?.Close();
                activeClient = null;
            }

            listener?.Stop();

            if (acceptTask != null)
            {
                try
                {
                    await acceptTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void CloseActiveSource(string reason)
        {
            TcpClient client = null;

            lock (clientLock)
            {
                client = activeClient;
                activeClient = null;
            }

            if (client == null)
            {
                return;
            }

            Log.Warning("Closing active sdrtrunk source connection: {reason}", reason);
            client.Close();
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await listener.AcceptTcpClientAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleClient(client, token), token);
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken token)
        {
            IPEndPoint remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
            Log.Information("sdrtrunk source connection opened from {remote}", remoteEndPoint);

            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    IcecastSourceRequest request = await ReadRequest(stream, remoteEndPoint, token);
                    Log.Debug(
                        "sdrtrunk source request {method} {mount} {protocol} from {remote}",
                        request.Method,
                        request.Mount,
                        request.Protocol,
                        remoteEndPoint);

                    if (IsMetadataRequest(request))
                    {
                        Log.Debug("Accepting sdrtrunk metadata update {mount}", request.Mount);
                        metadataHandler?.Invoke(request);
                        await WriteResponse(stream, request, "200 OK", token);
                        return;
                    }

                    if (!IsSupportedSourceMethod(request.Method))
                    {
                        await WriteResponse(stream, request, "405 Method Not Allowed", token);
                        return;
                    }

                    if (!string.Equals(NormalizeMount(request.Mount), mount, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warning("Rejecting sdrtrunk source for mount {requestMount}; expected {mount}", request.Mount, mount);
                        await WriteResponse(stream, request, "404 Not Found", token);
                        return;
                    }

                    if (!IsAuthorized(request.Headers))
                    {
                        Log.Warning("Rejecting unauthorized sdrtrunk source connection from {remote}", remoteEndPoint);
                        await WriteResponse(stream, request, "401 Unauthorized", token, "WWW-Authenticate: Basic realm=\"RC2 SDRTrunk\"\r\n");
                        return;
                    }

                    TcpClient previousClient = null;
                    lock (clientLock)
                    {
                        previousClient = activeClient;
                        activeClient = client;
                    }

                    if (previousClient != null)
                    {
                        Log.Warning("Replacing existing sdrtrunk source connection");
                        previousClient.Close();
                    }

                    if (request.Headers.TryGetValue("expect", out string expect) && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] continueBytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                        await stream.WriteAsync(continueBytes, token);
                    }

                    await WriteResponse(stream, request, "200 OK", token);
                    await sourceHandler(request, token);
                }
            }
            catch (EndOfStreamException)
            {
                Log.Warning("sdrtrunk source connection closed before request completed");
            }
            catch (IOException ex)
            {
                Log.Warning("sdrtrunk source connection closed: {message}", ex.Message);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error(ex, "sdrtrunk source connection failed");
            }
            finally
            {
                lock (clientLock)
                {
                    if (ReferenceEquals(activeClient, client))
                    {
                        activeClient = null;
                    }
                }

                Log.Information("sdrtrunk source connection closed from {remote}", remoteEndPoint);
            }
        }

        private async Task<IcecastSourceRequest> ReadRequest(NetworkStream stream, IPEndPoint remoteEndPoint, CancellationToken token)
        {
            string requestLine = await ReadLine(stream, token);
            string[] requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (requestParts.Length < 2)
            {
                throw new InvalidDataException($"Invalid source request line: {requestLine}");
            }

            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                string line = await ReadLine(stream, token);
                if (line.Length == 0)
                {
                    break;
                }

                int separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                headers[key] = value;
            }

            return new IcecastSourceRequest
            {
                Method = requestParts[0],
                Mount = requestParts[1],
                Protocol = requestParts.Length >= 3 ? requestParts[2] : "",
                Headers = headers,
                Body = stream,
                RemoteEndPoint = remoteEndPoint
            };
        }

        private static async Task<string> ReadLine(NetworkStream stream, CancellationToken token)
        {
            List<byte> bytes = new List<byte>();

            while (true)
            {
                byte[] buffer = new byte[1];
                int read = await stream.ReadAsync(buffer, token);

                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                if (buffer[0] == '\n')
                {
                    break;
                }

                if (buffer[0] != '\r')
                {
                    bytes.Add(buffer[0]);
                }
            }

            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private bool IsAuthorized(Dictionary<string, string> headers)
        {
            if (string.IsNullOrEmpty(sourcePassword))
            {
                return true;
            }

            if (headers.TryGetValue("authorization", out string authorization) &&
                authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                string encoded = authorization.Substring("Basic ".Length).Trim();
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                int separator = decoded.IndexOf(':');

                if (separator >= 0 && decoded.Substring(separator + 1) == sourcePassword)
                {
                    return true;
                }
            }

            if (headers.TryGetValue("ice-password", out string icePassword) && icePassword == sourcePassword)
            {
                return true;
            }

            return false;
        }

        private static bool IsSupportedSourceMethod(string method)
        {
            return method.Equals("SOURCE", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMetadataRequest(IcecastSourceRequest request)
        {
            return request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Mount.StartsWith("/admin/metadata", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task WriteResponse(NetworkStream stream, IcecastSourceRequest request, string status, CancellationToken token, string extraHeaders = "")
        {
            string response;

            if (request.Method.Equals("SOURCE", StringComparison.OrdinalIgnoreCase))
            {
                response = $"HTTP/1.0 {status}\r\n{extraHeaders}\r\n";
            }
            else
            {
                response =
                    $"HTTP/1.1 {status}\r\n" +
                    "Server: Icecast 2.4.4\r\n" +
                    "Connection: keep-alive\r\n" +
                    "Content-Length: 0\r\n" +
                    extraHeaders +
                    "\r\n";
            }

            byte[] bytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);
        }

        private static string NormalizeMount(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "/sdrtrunk";
            }

            return value.StartsWith("/") ? value : "/" + value;
        }
    }
}

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ClashSshProxy;

internal static class Program
{
    private const int MaximumHttpHeaderBytes = 16 * 1024;
    private static readonly TimeSpan ProxyConnectTimeout = TimeSpan.FromSeconds(10);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            var proxyUri = ValidateProxyUri(options.Proxy);
            var targetHost = ValidateTargetHost(options.Host);
            var targetPort = ParsePort(options.Port, "target port");

            using var proxyClient = new TcpClient { NoDelay = true };
            using (var connectTimeout = new CancellationTokenSource(ProxyConnectTimeout))
            {
                await proxyClient.ConnectAsync(proxyUri.Host, proxyUri.Port, connectTimeout.Token).ConfigureAwait(false);
                var proxyStream = proxyClient.GetStream();
                if (proxyUri.Scheme == "socks5")
                {
                    await EstablishSocks5TunnelAsync(proxyStream, targetHost, targetPort, connectTimeout.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await EstablishHttpConnectTunnelAsync(proxyStream, targetHost, targetPort, connectTimeout.Token)
                        .ConfigureAwait(false);
                }
            }

            await RelayStandardStreamsAsync(proxyClient).ConfigureAwait(false);
            return 0;
        }
        catch (ArgumentException exception)
        {
            await Console.Error.WriteLineAsync("ClashSshProxy: " + exception.Message).ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("ClashSshProxy: " + exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static Options ParseArguments(string[] args)
    {
        string? proxy = null;
        string? host = null;
        string? port = null;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                case "--proxy":
                    proxy = ReadValue(args, ref index);
                    break;
                case "--host":
                    host = ReadValue(args, ref index);
                    break;
                case "--port":
                    port = ReadValue(args, ref index);
                    break;
                default:
                    throw new ArgumentException("Unknown option: " + args[index]);
            }
        }

        if (!showHelp && (proxy is null || host is null || port is null))
        {
            throw new ArgumentException("--proxy, --host, and --port are required.");
        }

        return new Options(proxy ?? string.Empty, host ?? string.Empty, port ?? string.Empty, showHelp);
    }

    private static string ReadValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Missing value after " + args[index] + ".");
        }

        index++;
        return args[index];
    }

    private static Uri ValidateProxyUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "socks5") ||
            !uri.IsLoopback ||
            uri.Port is < 1 or > 65535 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Proxy must be a local HTTP or SOCKS5 URL, for example http://127.0.0.1:7890 or socks5://127.0.0.1:7891.");
        }

        return uri;
    }

    private static string ValidateTargetHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('/'))
        {
            throw new ArgumentException("The SSH target host is invalid.");
        }

        return value;
    }

    private static int ParsePort(string value, string description)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException("The " + description + " must be between 1 and 65535.");
        }

        return port;
    }

    private static async Task EstablishSocks5TunnelAsync(
        NetworkStream stream,
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken).ConfigureAwait(false);
        var methodReply = new byte[2];
        await ReadExactlyAsync(stream, methodReply, cancellationToken).ConfigureAwait(false);
        if (methodReply[0] != 5 || methodReply[1] != 0)
        {
            throw new IOException("The local SOCKS5 proxy did not accept unauthenticated connections.");
        }

        byte[] address;
        byte addressType;
        if (IPAddress.TryParse(targetHost, out var ipAddress))
        {
            address = ipAddress.GetAddressBytes();
            addressType = ipAddress.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        }
        else
        {
            var asciiHost = new IdnMapping().GetAscii(targetHost);
            address = Encoding.ASCII.GetBytes(asciiHost);
            if (address.Length is < 1 or > 255)
            {
                throw new ArgumentException("The target host name is too long for SOCKS5.");
            }

            addressType = 3;
        }

        var request = new byte[4 + (addressType == 3 ? 1 : 0) + address.Length + 2];
        request[0] = 5;
        request[1] = 1;
        request[2] = 0;
        request[3] = addressType;
        var offset = 4;
        if (addressType == 3)
        {
            request[offset++] = (byte)address.Length;
        }

        Buffer.BlockCopy(address, 0, request, offset, address.Length);
        offset += address.Length;
        request[offset++] = (byte)(targetPort >> 8);
        request[offset] = (byte)targetPort;
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        var replyHeader = new byte[4];
        await ReadExactlyAsync(stream, replyHeader, cancellationToken).ConfigureAwait(false);
        if (replyHeader[0] != 5 || replyHeader[1] != 0)
        {
            throw new IOException("The local SOCKS5 proxy rejected the SSH target (reply " + replyHeader[1] + ").");
        }

        var bindAddressLength = replyHeader[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => throw new IOException("The local SOCKS5 proxy returned an invalid address type.")
        };
        var remainingReply = new byte[bindAddressLength + 2];
        await ReadExactlyAsync(stream, remainingReply, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EstablishHttpConnectTunnelAsync(
        NetworkStream stream,
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
    {
        var authority = IPAddress.TryParse(targetHost, out var ipAddress) && ipAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? "[" + targetHost + "]:" + targetPort.ToString(CultureInfo.InvariantCulture)
            : targetHost + ":" + targetPort.ToString(CultureInfo.InvariantCulture);
        var request = Encoding.ASCII.GetBytes(
            "CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority + "\r\nProxy-Connection: Keep-Alive\r\n\r\n");
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        using var header = new MemoryStream();
        var previous = 0;
        var matched = 0;
        while (header.Length < MaximumHttpHeaderBytes)
        {
            var current = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
            header.WriteByte((byte)current);
            matched = (previous == '\r' && current == '\n') ? matched + 1 : current == '\r' ? 1 : 0;
            previous = current;
            if (matched == 2)
            {
                var response = Encoding.ASCII.GetString(header.ToArray());
                var firstLineEnd = response.IndexOf("\r\n", StringComparison.Ordinal);
                var statusLine = firstLineEnd >= 0 ? response[..firstLineEnd] : response;
                var statusParts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (statusParts.Length < 2 || statusParts[1] != "200")
                {
                    throw new IOException("The local HTTP proxy refused the SSH tunnel: " + statusLine);
                }

                return;
            }
        }

        throw new IOException("The local HTTP proxy response header was too large.");
    }

    private static async Task RelayStandardStreamsAsync(TcpClient proxyClient)
    {
        var networkStream = proxyClient.GetStream();
        using var stopInput = new CancellationTokenSource();
        var inputTask = CopyAsync(Console.OpenStandardInput(), networkStream, stopInput.Token);
        var outputTask = CopyAsync(networkStream, Console.OpenStandardOutput(), CancellationToken.None);
        var completedTask = await Task.WhenAny(inputTask, outputTask).ConfigureAwait(false);

        if (completedTask == inputTask)
        {
            await inputTask.ConfigureAwait(false);
            try
            {
                proxyClient.Client.Shutdown(SocketShutdown.Send);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            await outputTask.ConfigureAwait(false);
            return;
        }

        await outputTask.ConfigureAwait(false);
        stopInput.Cancel();
        try
        {
            await inputTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The local proxy closed the connection during its handshake.");
            }

            offset += read;
        }
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return buffer[0];
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: ClashSshProxy --proxy <http://127.0.0.1:7890|socks5://127.0.0.1:7891> --host <host> --port <port>");
        Console.Error.WriteLine("This program is intended for OpenSSH ProxyCommand and relays only that SSH connection.");
    }

    private sealed record Options(string Proxy, string Host, string Port, bool ShowHelp);
}

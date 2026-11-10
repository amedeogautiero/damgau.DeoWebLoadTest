using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using LoadTest.Protocol;

namespace LoadTest.Agent;

public sealed class GrpcControllerLink : IControllerLink
{
    private readonly GrpcChannel _channel;
    private readonly SocketsHttpHandler _handler;
    private readonly AsyncDuplexStreamingCall<AgentMsg, ControllerMsg> _call;

    private GrpcControllerLink(GrpcChannel channel, SocketsHttpHandler handler, AsyncDuplexStreamingCall<AgentMsg, ControllerMsg> call)
    {
        _channel = channel; _handler = handler; _call = call;
    }

    public static GrpcControllerLink Connect(AgentOptions o, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            EnableMultipleHttp2Connections = true
        };
        if (!string.IsNullOrEmpty(o.ClientCertificatePath))
        {
            var cert = X509CertificateLoader.LoadPkcs12FromFile(o.ClientCertificatePath,
                string.IsNullOrEmpty(o.ClientCertificatePassword) ? null : o.ClientCertificatePassword);
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { cert };
        }
        if (o.AllowInvalidServerCertificate)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        var channel = GrpcChannel.ForAddress(o.ControllerUrl, new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = ProtocolInfo.MaxMessageBytes,
            MaxSendMessageSize = ProtocolInfo.MaxMessageBytes
        });

        var headers = new Metadata();
        if (!string.IsNullOrEmpty(o.Token)) headers.Add(ProtocolInfo.AuthMetadataKey, "Bearer " + o.Token);

        var client = new AgentLink.AgentLinkClient(channel);
        return new GrpcControllerLink(channel, handler, client.Connect(headers, cancellationToken: ct));
    }

    public IAsyncEnumerable<ControllerMsg> ReadAllAsync(CancellationToken ct) => _call.ResponseStream.ReadAllAsync(ct);

    public Task WriteAsync(AgentMsg message, CancellationToken ct) => _call.RequestStream.WriteAsync(message, ct);

    public Task CompleteWritesAsync() => _call.RequestStream.CompleteAsync();

    public ValueTask DisposeAsync()
    {
        _call.Dispose();
        _channel.Dispose();
        _handler.Dispose();
        return ValueTask.CompletedTask;
    }
}

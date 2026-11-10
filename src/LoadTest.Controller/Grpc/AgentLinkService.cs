using Grpc.Core;
using LoadTest.Protocol;
using Microsoft.Extensions.Options;

namespace LoadTest.Controller.Grpc;

public sealed class AgentLinkService(ControllerHub hub, IOptions<ControllerOptions> options) : AgentLink.AgentLinkBase
{
    public override Task Connect(IAsyncStreamReader<AgentMsg> requestStream, IServerStreamWriter<ControllerMsg> responseStream, ServerCallContext context)
    {
        var http = context.GetHttpContext();
        var header = context.RequestHeaders.GetValue(ProtocolInfo.AuthMetadataKey);
        if (!ControllerAuth.IsAuthorized(options.Value, header, http.Connection.ClientCertificate is not null))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "token o certificato client mancante o non valido"));
        return hub.HandleAsync(requestStream, responseStream, context.CancellationToken);
    }
}

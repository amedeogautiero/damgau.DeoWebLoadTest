using Google.Protobuf.WellKnownTypes;

namespace LoadTest.Protocol;

/// <summary>
/// Decisione del controller sull'handshake (CONTRATTO sez. 7: "rifiuta agenti con protocol_version o sdk_version incompatibili").
/// Regole (PROPOSTA G5): protocol_version identica; SDK con lo stesso major e minor dell'agente >= minor del controller
/// (i .csx sono compilati dal controller contro il proprio SDK: un agente con SDK più vecchio potrebbe non avere i membri usati).
/// </summary>
public static class Handshake
{
    public static Welcome Evaluate(Hello hello, string controllerName, Version controllerSdk, DateTimeOffset controllerNowUtc)
    {
        var reason = Check(hello, controllerSdk);
        var welcome = new Welcome
        {
            Accepted = reason is null,
            ControllerName = controllerName,
            ProtocolVersion = ProtocolInfo.ProtocolVersion,
            SdkVersion = controllerSdk.ToString(),
            UtcNow = Timestamp.FromDateTimeOffset(controllerNowUtc)
        };
        if (reason is not null) welcome.RejectReason = reason;
        else welcome.ClockOffsetMs = (long)Math.Round((controllerNowUtc - hello.UtcNow.ToDateTimeOffset()).TotalMilliseconds);
        return welcome;
    }

    private static string? Check(Hello h, Version controllerSdk)
    {
        if (h.ProtocolVersion != ProtocolInfo.ProtocolVersion)
            return $"protocol_version {h.ProtocolVersion} non supportata (attesa {ProtocolInfo.ProtocolVersion})";
        if (!Version.TryParse(h.SdkVersion, out var agentSdk))
            return $"sdk_version '{h.SdkVersion}' non valida";
        if (agentSdk.Major != controllerSdk.Major || agentSdk.Minor < controllerSdk.Minor)
            return $"sdk_version {h.SdkVersion} incompatibile con quella del controller ({controllerSdk})";
        if (string.IsNullOrWhiteSpace(h.AgentId)) return "agent_id mancante";
        if (string.IsNullOrWhiteSpace(h.Rid)) return "rid mancante";
        if (h.MaxUsers < 0) return "max_users negativo";
        if (h.UtcNow is null) return "utc_now mancante";
        return null;
    }
}

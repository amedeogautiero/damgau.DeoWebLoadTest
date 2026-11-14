using LoadTest.Sdk;

namespace LoadTest.WebTestSample.Scripts;
public class TokenExtractor : IExecutionRule
{
    public ExtractionResult Extract(
        RequestContext req,
        ResponseContext resp,
        IReadOnlyDictionary<string, string> args)
    {
        var token = resp.BodyText;

        return string.IsNullOrWhiteSpace(token)
            ? ExtractionResult.Fail("Token non trovato")
            : ExtractionResult.Ok(token);
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Http.Tests.TestSupport;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceMediaTypeFallbackTests {
    const string jsonBody = "{\"Value\":\"42\"}";

    static HttpService LoopbackService() => new() { Timeout = TimeSpan.FromSeconds(20) };

    static HttpResponseMessage Canned(string? mediaType, byte[] body) {
        ProbeContent content = new(body);
        if (mediaType != null)
            content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
        return new(HttpStatusCode.OK) { Content = content };
    }

    static IEnumerable<TestCaseData> JsonCarryingMediaTypes() {
        foreach (HttpCompletionOption option in new[] { HttpCompletionOption.ResponseContentRead, HttpCompletionOption.ResponseHeadersRead }) {
            yield return new TestCaseData("text/javascript", option).SetName($"Vendor_TextJavascript_{option}");
            yield return new TestCaseData("application/hal+json", option).SetName($"Suffix_HalJson_{option}");
            yield return new TestCaseData("application/vnd.acme.thing+json", option).SetName($"Suffix_VendorJson_{option}");
            yield return new TestCaseData("text/json", option).SetName($"TextJson_{option}");
            yield return new TestCaseData("Application/JSON", option).SetName($"Cased_ApplicationJson_{option}");
            yield return new TestCaseData("APPLICATION/HAL+JSON", option).SetName($"Cased_SuffixJson_{option}");
            yield return new TestCaseData(null, option).SetName($"AbsentContentType_{option}");
        }
    }

    [TestCaseSource(nameof(JsonCarryingMediaTypes)), Parallelizable]
    public async Task Get_JsonBodyUnderMediaType_DecodesToDomainType(string? mediaType, HttpCompletionOption option) {
        using LoopbackServer server = new(mediaType, Encoding.UTF8.GetBytes(jsonBody));

        ProbeDto? result = await LoopbackService().Get<ProbeDto>(server.Url, new HttpOptions { CompletionOption = option });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value, Is.EqualTo("42"));
    }

    [TestCase(HttpCompletionOption.ResponseContentRead), TestCase(HttpCompletionOption.ResponseHeadersRead), Parallelizable]
    [Description("pins that the sniff materialises the body without draining the handle the decoder needs, which only fails under the streaming option")]
    public async Task Get_UnrecognisedMediaType_DecodesUnderEitherCompletionOption(HttpCompletionOption option) {
        using LoopbackServer server = new("text/javascript", Encoding.UTF8.GetBytes(jsonBody));

        ProbeDto? result = await LoopbackService().Get<ProbeDto>(server.Url, new HttpOptions { CompletionOption = option });

        Assert.That(result!.Value, Is.EqualTo("42"));
    }

    [TestCase(HttpCompletionOption.ResponseContentRead), TestCase(HttpCompletionOption.ResponseHeadersRead), Parallelizable]
    public void Get_NonJsonBodyUnderUnrecognisedMediaType_ThrowsUnderEitherCompletionOption(HttpCompletionOption option) {
        using LoopbackServer server = new("text/javascript", Encoding.UTF8.GetBytes("not json at all"));
        HttpService service = LoopbackService();
        string url = server.Url;

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<ProbeDto>(url, new HttpOptions { CompletionOption = option }));

        Assert.That(exception!.Body, Is.EqualTo("not json at all"));
    }

    [TestCase(HttpCompletionOption.ResponseContentRead), TestCase(HttpCompletionOption.ResponseHeadersRead), Parallelizable]
    public async Task Get_ByteOrderMarkAndWhitespaceBeforeJson_Decodes(HttpCompletionOption option) {
        List<byte> body = [0xEF, 0xBB, 0xBF];
        body.AddRange(Encoding.UTF8.GetBytes($" \r\n\t {jsonBody}"));
        using LoopbackServer server = new("text/javascript", body.ToArray());

        ProbeDto? result = await LoopbackService().Get<ProbeDto>(server.Url, new HttpOptions { CompletionOption = option });

        Assert.That(result!.Value, Is.EqualTo("42"));
    }

    [Test, Parallelizable]
    public async Task Get_JsonArrayUnderUnrecognisedMediaType_DecodesToCollection() {
        using LoopbackServer server = new("text/javascript", Encoding.UTF8.GetBytes("[{\"Value\":\"a\"},{\"Value\":\"b\"}]"));

        ProbeDto[]? result = await LoopbackService().Get<ProbeDto[]>(server.Url);

        Assert.That(result, Has.Length.EqualTo(2));
        Assert.That(result![0].Value, Is.EqualTo("a"));
        Assert.That(result[1].Value, Is.EqualTo("b"));
    }

    [Test, Parallelizable]
    public void Get_NonJsonBody_ExceptionNamesUrlMediaTypeAndRequestedType() {
        using LoopbackServer server = new("text/javascript", Encoding.UTF8.GetBytes("not json at all"));
        HttpService service = LoopbackService();
        string url = server.Url;

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(() => service.Get<ProbeDto>(url));

        Assert.That(exception!.Message, Does.Contain(url));
        Assert.That(exception.Message, Does.Contain("text/javascript"));
        Assert.That(exception.Message, Does.Contain(nameof(ProbeDto)));
        Assert.That(exception.Body, Is.EqualTo("not json at all"));
    }

    [Test, Parallelizable]
    public void Get_NonJsonBodyWithoutContentType_ReportsAbsentMediaTypeExplicitly() {
        using LoopbackServer server = new(null, Encoding.UTF8.GetBytes("not json at all"));
        HttpService service = LoopbackService();
        string url = server.Url;

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(() => service.Get<ProbeDto>(url));

        Assert.That(exception!.Message, Does.Contain("<none>"));
        Assert.That(exception.Message, Does.Not.Contain("media type ''"));
    }

    [Test, Parallelizable]
    public void Get_BinaryBodyUnderOctetStream_ThrowsDescriptiveException() {
        using LoopbackServer server = new("application/octet-stream", [0x00, 0x01, 0x02, 0x7F]);
        HttpService service = LoopbackService();
        string url = server.Url;

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(() => service.Get<ProbeDto>(url));

        Assert.That(exception!.Message, Does.Contain("application/octet-stream"));
        Assert.That(exception.Message, Does.Contain(nameof(ProbeDto)));
    }

    static IEnumerable<TestCaseData> EveryFallbackShape() {
        yield return new TestCaseData("text/javascript", jsonBody).SetName("Vendor_JsonBody");
        yield return new TestCaseData("application/hal+json", jsonBody).SetName("Suffix_JsonBody");
        yield return new TestCaseData("text/json", jsonBody).SetName("TextJson_JsonBody");
        yield return new TestCaseData("Application/JSON", jsonBody).SetName("Cased_JsonBody");
        yield return new TestCaseData(null, jsonBody).SetName("Absent_JsonBody");
        yield return new TestCaseData("text/javascript", "not json at all").SetName("Vendor_NonJsonBody");
        yield return new TestCaseData("application/octet-stream", "\u0001\u0002").SetName("OctetStream_BinaryBody");
        yield return new TestCaseData(null, "not json at all").SetName("Absent_NonJsonBody");
        yield return new TestCaseData("text/javascript", "{not valid json").SetName("Vendor_BraceButInvalidJson");
    }

    [TestCaseSource(nameof(EveryFallbackShape)), Parallelizable]
    [Description("S5: the deleted force-cast must be unreachable, so no invalid cast may surface from any dispatch outcome")]
    public async Task ReadResponse_NoDispatchOutcome_NeverSurfacesInvalidCastException(string? mediaType, string body) {
        using LoopbackServer server = new(mediaType, Encoding.UTF8.GetBytes(body));

        Exception? caught = null;
        try {
            await LoopbackService().Get<ProbeDto>(server.Url);
        }
        catch (Exception exception) {
            caught = exception;
        }

        for (Exception? walk = caught; walk != null; walk = walk.InnerException)
            Assert.That(walk, Is.Not.InstanceOf<InvalidCastException>());
    }

    [Test, Parallelizable]
    public async Task Get_UnrecognisedMediaType_UsesConfiguredDecoder() {
        using LoopbackServer server = new("text/javascript", Encoding.UTF8.GetBytes(jsonBody));
        CountingDecoder decoder = new();

        ProbeDto? result = await LoopbackService().Get<ProbeDto>(server.Url, new HttpOptions { Decoder = decoder });

        Assert.That(decoder.Calls, Is.EqualTo(1));
        Assert.That(result!.Value, Is.EqualTo("42"));
    }

    [Test, Parallelizable]
    public async Task Get_UnrecognisedMediaTypeDecoded_DisposesResponse() {
        ProbeContent content = new(Encoding.UTF8.GetBytes(jsonBody));
        content.Headers.TryAddWithoutValidation("Content-Type", "text/javascript");
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        HttpService service = new(new SequenceHandler(response));

        await service.Get<ProbeDto>("https://example.test/probe");

        Assert.That(content.Disposed, Is.True);
    }

    [Test, Parallelizable]
    [Description("the throw path leaves the response alive so a caller can inspect it, matching the error-status convention")]
    public void Get_UnrecognisedMediaTypeRefused_DoesNotDisposeResponse() {
        ProbeContent content = new(Encoding.UTF8.GetBytes("not json at all"));
        content.Headers.TryAddWithoutValidation("Content-Type", "text/javascript");
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<ProbeDto>("https://example.test/probe"));

        Assert.That(content.Disposed, Is.False);
        Assert.That(exception!.Response, Is.SameAs(response));
    }

    static IEnumerable<TestCaseData> PredicateClaimedMediaTypes() {
        yield return new TestCaseData("application/json").SetName("Canonical");
        yield return new TestCaseData("text/json").SetName("TextJson");
        yield return new TestCaseData("application/hal+json").SetName("SuffixJson");
        yield return new TestCaseData("application/vnd.acme.thing+json").SetName("VendorSuffixJson");
        yield return new TestCaseData("Application/JSON").SetName("CasedCanonical");
        yield return new TestCaseData("Text/JSON").SetName("CasedTextJson");
        yield return new TestCaseData("APPLICATION/HAL+JSON").SetName("CasedSuffixJson");
    }

    [TestCaseSource(nameof(PredicateClaimedMediaTypes)), Parallelizable]
    [Description("a media type the json predicate claims goes straight to the decoder, so an unparseable body fails as a decode error and never as the refusal only the body sniff produces")]
    public void Get_JsonFamilyMediaTypeWithNonJsonBody_ReportsDecodeFailureNotRefusal(string mediaType) {
        using LoopbackServer server = new(mediaType, Encoding.UTF8.GetBytes("not json at all"));
        HttpService service = LoopbackService();
        string url = server.Url;

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(() => service.Get<ProbeDto>(url));

        Assert.That(exception!.Message, Does.StartWith("Error decoding response of"));
        Assert.That(exception.InnerException, Is.Not.Null);
    }

    static IEnumerable<TestCaseData> MediaTypesNamingJsonWithoutBeingJson() {
        yield return new TestCaseData("application/json-seq").SetName("JsonTextSequence");
        yield return new TestCaseData("application/x-ndjson").SetName("NewlineDelimitedJson");
    }

    [TestCaseSource(nameof(MediaTypesNamingJsonWithoutBeingJson)), Parallelizable]
    [Description("the json predicate matches a canonical name or a +json suffix and never a media type that merely contains the word, so these reach the body sniff instead of the decoder")]
    public void Get_MediaTypeContainingJsonWithoutSuffix_IsNotClaimedByPredicate(string mediaType) {
        using LoopbackServer server = new(mediaType, Encoding.UTF8.GetBytes("not json at all"));
        HttpService service = LoopbackService();
        string url = server.Url;

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(() => service.Get<ProbeDto>(url));

        Assert.That(exception!.Message, Does.StartWith("Unable to decode response of"));
        Assert.That(exception.InnerException, Is.Null);
    }

    [TestCase("text/javascript"), TestCase("application/json"), Parallelizable]
    [Description("a residual byte order mark is a leading artifact, not body content, so a body carrying one fails as a decode error on either path rather than being refused as non-json")]
    public void Get_ResidualByteOrderMarkBeforeJson_FailsAsDecodeErrorOnEitherPath(string mediaType) {
        List<byte> body = [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF];
        body.AddRange(Encoding.UTF8.GetBytes(jsonBody));
        using LoopbackServer server = new(mediaType, body.ToArray());
        HttpService service = LoopbackService();
        string url = server.Url;

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(() => service.Get<ProbeDto>(url));

        Assert.That(exception!.Message, Does.StartWith("Error decoding response of"));
        Assert.That(exception.InnerException, Is.Not.Null);
    }

    [Test, Parallelizable]
    [Description("text/javascript is caught by the body sniff rather than by a vendor entry in the media type predicate")]
    public void Get_UnrecognisedMediaTypeWithNonJsonBody_ReportsRefusalNotDecodeFailure() {
        using LoopbackServer server = new("text/javascript", Encoding.UTF8.GetBytes("not json at all"));
        HttpService service = LoopbackService();
        string url = server.Url;

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(() => service.Get<ProbeDto>(url));

        Assert.That(exception!.Message, Does.StartWith("Unable to decode response of"));
    }

    [Test, Parallelizable]
    [Description("a body opening with a json structure token but not parseable must fail loudly, never decode to a best-effort value")]
    public void Get_BraceOpenedBodyThatIsNotJson_WrapsDecodeFailure() {
        using LoopbackServer server = new("text/javascript", Encoding.UTF8.GetBytes("{not valid json"));
        HttpService service = LoopbackService();
        string url = server.Url;

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(() => service.Get<ProbeDto>(url));

        Assert.That(exception!.Message, Does.Contain("text/javascript"));
        Assert.That(exception.InnerException, Is.Not.Null);
    }

    [Test, Parallelizable]
    [Description("compatibility tail: object requested against an unrecognised media type used to hand back the raw body stream")]
    public async Task Get_ObjectRequestedUnderUnrecognisedMediaType_ReturnsDecodedValueNotStream() {
        using LoopbackServer server = new("text/javascript", Encoding.UTF8.GetBytes(jsonBody));

        object? result = await LoopbackService().Get<object>(server.Url);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Not.InstanceOf<Stream>());
        Assert.That(result, Is.InstanceOf<IDictionary<string, object>>());
        Assert.That(((IDictionary<string, object>)result!)["Value"], Is.EqualTo("42"));
    }

    [Test, Parallelizable]
    [Description("compatibility tail: the fallback decodes instead of handing back the body, so a memory stream request yields the decoder's empty stream exactly as the canonical json path already does")]
    public async Task Get_MemoryStreamRequestedUnderUnrecognisedMediaType_ReturnsEmptyStreamFromDecoder() {
        using LoopbackServer server = new("text/javascript", Encoding.UTF8.GetBytes(jsonBody));
        CountingDecoder decoder = new();

        MemoryStream? result = await LoopbackService().Get<MemoryStream>(server.Url, new HttpOptions { Decoder = decoder });

        Assert.That(decoder.Calls, Is.EqualTo(1));
        Assert.That(result!.Length, Is.EqualTo(0));
    }

    [Test, Parallelizable]
    [Description("compatibility tail: a disposable request used to succeed by receiving the raw body stream")]
    public void Get_DisposableRequestedUnderUnrecognisedMediaType_Throws() {
        using LoopbackServer server = new("text/javascript", Encoding.UTF8.GetBytes(jsonBody));
        HttpService service = LoopbackService();
        string url = server.Url;

        Assert.ThrowsAsync<HttpServiceException>(() => service.Get<IDisposable>(url));
    }

    [Test, Parallelizable]
    [Description("the passthrough types return before the fallback, so the way to ask for an undecoded body is unchanged")]
    public async Task Get_StreamRequestedUnderUnrecognisedMediaType_StillCarriesBody() {
        using LoopbackServer server = new("text/javascript", Encoding.UTF8.GetBytes(jsonBody));

        Stream? result = await LoopbackService().Get<Stream>(server.Url);

        using StreamReader reader = new(result!);
        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo(jsonBody));
    }

    [Test, Parallelizable]
    public async Task Get_CanonicalJsonMediaType_DecodesAsBefore() {
        using HttpResponseMessage response = Canned("application/json", Encoding.UTF8.GetBytes(jsonBody));
        HttpService service = new(new SequenceHandler(response));

        ProbeDto? result = await service.Get<ProbeDto>("https://example.test/probe");

        Assert.That(result!.Value, Is.EqualTo("42"));
    }

    [TestCase("application/xml"), TestCase("text/xml"), Parallelizable]
    public async Task Get_XmlMediaType_LoadsDocumentAsBefore(string mediaType) {
        using HttpResponseMessage response = Canned(mediaType, Encoding.UTF8.GetBytes("<root><child /></root>"));
        HttpService service = new(new SequenceHandler(response));

        XDocument? result = await service.Get<XDocument>("https://example.test/probe");

        Assert.That(result!.Root!.Name.LocalName, Is.EqualTo("root"));
    }

    [Test, Parallelizable]
    public async Task Get_PlainTextMediaType_ReturnsStringAsBefore() {
        using HttpResponseMessage response = Canned("text/plain", Encoding.UTF8.GetBytes("hello"));
        HttpService service = new(new SequenceHandler(response));

        string? result = await service.Get<string>("https://example.test/probe");

        Assert.That(result, Is.EqualTo("hello"));
    }
}

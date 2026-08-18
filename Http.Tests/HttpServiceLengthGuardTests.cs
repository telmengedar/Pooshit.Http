using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Http.Tests.TestSupport;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceLengthGuardTests {

    [Test, Parallelizable]
    public async Task PresentZeroLength_DefaultOption_ReturnsTypeDefault() {
        ProbeContent content = new([], declaresLength: true) {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
        };
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        ProbeDto? result = await service.Get<ProbeDto>("https://example.test/probe");

        Assert.That(result, Is.Null);
    }

    [Test, Parallelizable]
    public async Task PresentZeroLength_DefaultOption_DisposesResponse() {
        ProbeContent content = new([], declaresLength: true);
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        await service.Get<ProbeDto>("https://example.test/probe");

        Assert.That(content.Disposed, Is.True);
    }

    [Test, Parallelizable]
    public async Task AbsentLength_StreamingOption_ReturnsReadableStream() {
        ProbeContent content = new("streamed-body"u8.ToArray(), declaresLength: false);
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        using Stream stream = await service.Get<Stream>("https://example.test/probe",
            new HttpOptions { CompletionOption = HttpCompletionOption.ResponseHeadersRead });

        Assert.That(stream, Is.Not.Null);
        using StreamReader reader = new(stream);
        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("streamed-body"));
    }

    [Test, Parallelizable]
    public async Task AbsentLength_DefaultOption_DecodesDomainType() {
        ProbeContent content = new("{\"value\":\"hello\"}"u8.ToArray(), declaresLength: false) {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
        };
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        ProbeDto? result = await service.Get<ProbeDto>("https://example.test/probe");

        Assert.That(result?.Value, Is.EqualTo("hello"));
    }

    [Test, Parallelizable]
    public async Task AbsentLengthEmptyBody_StreamingOption_ReturnsEmptyStringNotNull() {
        ProbeContent content = new([], declaresLength: false);
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        string? result = await service.Get<string>("https://example.test/probe",
            new HttpOptions { CompletionOption = HttpCompletionOption.ResponseHeadersRead });

        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test, Parallelizable]
    public void AbsentLengthEmptyBodyNoContentType_StreamingOption_ThrowsInvalidCast() {
        ProbeContent content = new([], declaresLength: false);
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        Assert.ThrowsAsync<InvalidCastException>(() => service.Get<ProbeDto>("https://example.test/probe",
            new HttpOptions { CompletionOption = HttpCompletionOption.ResponseHeadersRead }));
    }

    [Test, Parallelizable]
    public async Task PresentNonZeroLength_DefaultOption_DecodesDomainType() {
        ProbeContent content = new("{\"value\":\"hello\"}"u8.ToArray(), declaresLength: true) {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
        };
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        ProbeDto? result = await service.Get<ProbeDto>("https://example.test/probe");

        Assert.That(result?.Value, Is.EqualTo("hello"));
    }
}

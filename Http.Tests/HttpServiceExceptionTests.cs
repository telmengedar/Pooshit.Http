using System.Net;
using System.Net.Http;
using Http.Tests.TestSupport;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceExceptionTests {

    [Test, Parallelizable]
    public void BodyCarriesResponseText() {
        using HttpResponseMessage response = new(HttpStatusCode.NotFound);
        HttpServiceException exception = new(response, "error", body: "{\"code\":\"data_entitynotfound\"}");
        Assert.That(exception.Body, Is.EqualTo("{\"code\":\"data_entitynotfound\"}"));
    }

    [Test, Parallelizable]
    public void BodyNullWhenOmitted() {
        using HttpResponseMessage response = new(HttpStatusCode.InternalServerError);
        HttpServiceException exception = new(response, "error");
        Assert.That(exception.Body, Is.Null);
    }

    [Test, Parallelizable]
    public void StreamingOption_ErrorBody_StillCaptured() {
        ProbeContent content = new("{\"code\":\"missing\"}"u8.ToArray());
        using HttpResponseMessage response = new(HttpStatusCode.NotFound) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(() => service.Get<string>(
            "https://example.test/probe", new HttpOptions { CompletionOption = HttpCompletionOption.ResponseHeadersRead }));

        Assert.That(exception!.Body, Is.EqualTo("{\"code\":\"missing\"}"));
    }

    [Test, Parallelizable]
    public void DefaultOption_ErrorBody_StillCaptured() {
        ProbeContent content = new("{\"code\":\"missing\"}"u8.ToArray());
        using HttpResponseMessage response = new(HttpStatusCode.NotFound) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe"));

        Assert.That(exception!.Body, Is.EqualTo("{\"code\":\"missing\"}"));
    }
}

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

    [Test, Parallelizable]
    [Description("DiVoid #9938: an arbitrary vendor payload is only safe to log by the application's judgement, so the message stops carrying it while Body keeps it")]
    public void FailingCall_ResponseBody_CarriedOnBodyAndNotInMessage() {
        using HttpResponseMessage response = new(HttpStatusCode.NotFound) { Content = new StringContent("{\"secret\":\"topsecretbodyvalue\"}") };
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe"));

        Assert.That(exception!.Message, Does.Not.Contain("topsecretbodyvalue"));
        Assert.That(exception.Body, Is.EqualTo("{\"secret\":\"topsecretbodyvalue\"}"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: an implementation that empties the message passes the body-is-gone assertion, so what the message keeps is pinned")]
    public void FailingCall_ResponseBodyRemovedFromMessage_UrlStatusAndHeaderBlockRemain() {
        using HttpResponseMessage response = new(HttpStatusCode.NotFound) { Content = new StringContent("{\"secret\":\"topsecretbodyvalue\"}") };
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe"));

        Assert.That(exception!.Message, Does.Contain("https://example.test/probe"));
        Assert.That(exception.Message, Does.Contain("NotFound"));
        Assert.That(exception.Message, Does.Contain("Request Headers"));
        Assert.That(exception.Message, Does.Contain("Response Headers"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: collapsing the two throws must not turn Body from null into an empty string for a bodiless error")]
    public void FailingCall_EmptyResponseBody_BodyStaysNull() {
        using HttpResponseMessage response = new(HttpStatusCode.InternalServerError) { Content = new StringContent("") };
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe"));

        Assert.That(exception!.Body, Is.Null);
    }
}

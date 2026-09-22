using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Http.Tests.TestSupport;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceStatusBandTests {

    static HttpResponseMessage Empty(HttpStatusCode status) =>
        new(status) { Content = new StringContent(string.Empty) };

    static HttpResponseMessage Redirect(HttpStatusCode status, string location) {
        HttpResponseMessage response = Empty(status);
        response.Headers.Location = new Uri(location);
        return response;
    }

    [Test, Parallelizable]
    [Description("DiVoid #8316: a redirect nobody followed reached the zero-length short-circuit and became an untyped null")]
    public void Get_Redirect302_DefaultOptions_Throws() {
        using HttpResponseMessage redirect = Redirect(HttpStatusCode.Redirect, "https://other-host.example/target");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe"))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(error.Response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    [Test, Parallelizable]
    public void Get_Redirect302_DefaultOptions_MessageNamesTheTarget() {
        using HttpResponseMessage redirect = Redirect(HttpStatusCode.Redirect, "https://other-host.example/target");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe"))!;

        Assert.That(error.Message, Does.Contain("a redirect to 'https://other-host.example/target' which was not followed"));
    }

    [Test, Parallelizable]
    public void Get_Redirect302_MessageNamesFollowRedirectsAndRawResponse() {
        using HttpResponseMessage redirect = Redirect(HttpStatusCode.Redirect, "https://other-host.example/target");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe"))!;

        Assert.That(error.Message, Does.Contain("FollowRedirects"));
        Assert.That(error.Message, Does.Contain("HttpResponseMessage"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: the target named in the message is a new url surface, so it is query-redacted like every other")]
    public void Get_Redirect302_TargetQueryIsRedactedInTheMessage() {
        using HttpResponseMessage redirect = Redirect(HttpStatusCode.Redirect, "https://other-host.example/target?token=super-secret-value&page=2");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe"))!;

        Assert.That(error.Message, Does.Contain("a redirect to 'https://other-host.example/target?token=<redacted>&page=2'"));
        Assert.That(error.Message, Does.Not.Contain("super-secret-value"));
    }

    [Parallelizable]
    [TestCase(300)]
    [TestCase(304)]
    [TestCase(305)]
    [TestCase(399)]
    [Description("DiVoid #8316: the silence is defined by the band, so a 3xx outside the five recognised statuses is silent in exactly the same way")]
    public void Get_NonFamily3xx_DefaultOptions_Throws(int status) {
        using HttpResponseMessage redirect = Redirect((HttpStatusCode)status, "https://other-host.example/target");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe"))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That((int)error.Response.StatusCode, Is.EqualTo(status));
    }

    [Parallelizable]
    [TestCase(300)]
    [TestCase(304)]
    [TestCase(305)]
    [TestCase(399)]
    [Description("DiVoid #14516 section 2.2: these statuses matched neither redirect arm and stayed silent even with following on")]
    public void Get_NonFamily3xx_FollowRedirectsTrue_Throws(int status) {
        using HttpResponseMessage redirect = Redirect((HttpStatusCode)status, "https://other-host.example/target");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe", new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That((int)error.Response.StatusCode, Is.EqualTo(status));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8316: the result-less members call the status check directly, so a redirect was silent there with no null to notice")]
    public void GetResultLess_Redirect302_Throws() {
        using HttpResponseMessage redirect = Redirect(HttpStatusCode.Redirect, "https://other-host.example/target");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get("https://example.test/probe"))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(error.Response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8323: a chain is followed to its end and the band then classifies that end, rather than the chain itself being the failure")]
    public void Get_Redirect308Chain_EndingOutsideTheBand_Throws() {
        using HttpResponseMessage first = Redirect(HttpStatusCode.PermanentRedirect, "https://other-host.example/target");
        using HttpResponseMessage second = Redirect(HttpStatusCode.PermanentRedirect, "https://third-host.example/target");
        using HttpResponseMessage last = Empty(HttpStatusCode.NotFound);

        SequenceHandler handler = new(first, second, last);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe", new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(3));
        Assert.That(error.Response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Parallelizable]
    public async Task Get_Status200EmptyBody_StillReturnsNull() {
        using HttpResponseMessage response = Empty(HttpStatusCode.OK);

        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        string? result = await service.Get<string>("https://example.test/probe");

        Assert.That(result, Is.Null);
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [Test, Parallelizable]
    public async Task Get_Status204_StillReturnsNull() {
        using HttpResponseMessage response = Empty(HttpStatusCode.NoContent);

        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        string? result = await service.Get<string>("https://example.test/probe");

        Assert.That(result, Is.Null);
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [Test, Parallelizable]
    public async Task Get_Status207_StillSucceeds() {
        ProbeContent content = new("{\"value\":\"hello\"}"u8.ToArray()) {
                                                                          Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                                                                      };
        using HttpResponseMessage response = new((HttpStatusCode)207) { Content = content };

        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        ProbeDto? result = await service.Get<ProbeDto>("https://example.test/probe");

        Assert.That(result?.Value, Is.EqualTo("hello"));
    }

    [Parallelizable]
    [TestCase(100)]
    [TestCase(101)]
    [TestCase(199)]
    [Description("DiVoid #14585: the success band's lower edge and the redirect clause's own lower gate are both observable only below 200, and neither was pinned")]
    public void Get_Status1xx_Throws(int status) {
        using HttpResponseMessage response = Empty((HttpStatusCode)status);

        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe"))!;

        Assert.That((int)error.Response.StatusCode, Is.EqualTo(status));
        Assert.That(error.Message, Does.Not.Contain("a redirect to"));
    }

    [Test, Parallelizable]
    public async Task Get_Status299_StillSucceeds() {
        using HttpResponseMessage response = new((HttpStatusCode)299) { Content = new StringContent("done") };

        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        string? result = await service.Get<string>("https://example.test/probe");

        Assert.That(result, Is.EqualTo("done"));
    }

    [Parallelizable]
    [TestCase(300)]
    [TestCase(399)]
    [Description("DiVoid #8316: the clause carries its own band edges, which are not the ones the success check moved")]
    public void Get_3xxBandEdge_MessageStillNamesARedirect(int status) {
        using HttpResponseMessage redirect = Redirect((HttpStatusCode)status, "https://other-host.example/target");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe"))!;

        Assert.That((int)error.Response.StatusCode, Is.EqualTo(status));
        Assert.That(error.Message, Does.Contain("a redirect to 'https://other-host.example/target' which was not followed"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9617: the target is a header value, so a caller who declares Location sensitive has it withheld from the message too")]
    public void Get_Redirect302_LocationInSensitiveHeaders_TargetIsRedactedInTheMessage() {
        using HttpResponseMessage redirect = Redirect(HttpStatusCode.Redirect, "https://other-host.example/target");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);
        service.SensitiveHeaders.Add("Location");

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe"))!;

        Assert.That(error.Message, Does.Contain("a redirect to '<redacted>' which was not followed"));
        Assert.That(error.Message, Does.Not.Contain("other-host.example"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9617: a caller who suppressed the header block suppressed the target with it, because the target is one of those headers")]
    public void Get_Redirect302_OmittedDumpMode_MessageWithholdsTheTarget() {
        using HttpResponseMessage redirect = Redirect(HttpStatusCode.Redirect, "https://other-host.example/target");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe", new HttpOptions { HeaderDumpMode = HeaderDumpMode.Omitted }))!;

        Assert.That(error.Message, Does.Contain("a redirect to an undisclosed location"));
        Assert.That(error.Message, Does.Not.Contain("other-host.example"));
        Assert.That(error.Message, Does.Contain("FollowRedirects"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9617: a caller who asked for every header verbatim asked for the target verbatim as well")]
    public void Get_Redirect302_FullDumpMode_TargetIsNotRedacted() {
        using HttpResponseMessage redirect = Redirect(HttpStatusCode.Redirect, "https://other-host.example/target?token=super-secret-value");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe", new HttpOptions { HeaderDumpMode = HeaderDumpMode.Full }))!;

        Assert.That(error.Message, Does.Contain("a redirect to 'https://other-host.example/target?token=super-secret-value'"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8316: a 304 carries no location, which is the shape the message has to render without one")]
    public void Get_NotModifiedWithoutLocation_ThrowsAndSaysThereIsNoTarget() {
        using HttpResponseMessage response = Empty(HttpStatusCode.NotModified);

        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe", new HttpOptions { HeaderDumpMode = HeaderDumpMode.Omitted }))!;

        Assert.That(error.Response.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        Assert.That(error.Message, Does.Contain("a redirect to no location"));
        Assert.That(error.Message, Does.Contain("FollowRedirects"));
    }

    [Test, Parallelizable]
    public void Get_Status404_MessageDoesNotNameARedirect() {
        using HttpResponseMessage response = Empty(HttpStatusCode.NotFound);

        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe"))!;

        Assert.That(error.Response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(error.Message, Does.Contain("NotFound"));
        Assert.That(error.Message, Does.Not.Contain("a redirect to"));
        Assert.That(error.Message, Does.Not.Contain("FollowRedirects"));
    }

    [Test, Parallelizable]
    public async Task Get_RawResponse_Redirect302_StillReturnsLiveResponse() {
        ProbeContent content = new("moved"u8.ToArray());
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect) { Content = content };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        using HttpResponseMessage result = await service.Get<HttpResponseMessage>("https://example.test/probe");

        Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(result.Headers.Location, Is.EqualTo(new Uri("https://other-host.example/target")));
        Assert.That(content.Disposed, Is.False);
    }

    [Test, Parallelizable]
    public async Task Get_Redirect302_FollowRedirectsTrue_SingleHop_StillSucceeds() {
        using HttpResponseMessage redirect = Redirect(HttpStatusCode.Redirect, "https://other-host.example/target");
        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        string? result = await service.Get<string>("https://example.test/probe", new HttpOptions { FollowRedirects = true });

        Assert.That(result, Is.EqualTo("done"));
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
    }
}

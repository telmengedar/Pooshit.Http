using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Http.Tests.TestSupport;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceRedirectTests {

    [Test, Parallelizable]
    public async Task AbsoluteLocationResolvesToAbsoluteUrl() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) {
                                                                      Content = new StringContent("done")
                                                                  };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        string result = await service.Get<string>("https://original-host.example/start", new HttpOptions { FollowRedirects = true });

        Assert.That(result, Is.EqualTo("done"));
        Assert.That(handler.RequestedUris, Has.Count.EqualTo(2));
        Assert.That(handler.RequestedUris[1], Is.EqualTo(new Uri("https://other-host.example/target")));
    }

    [Test, Parallelizable]
    public async Task RelativeLocationResolvesAgainstRequestUri() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("/target", UriKind.Relative);

        using HttpResponseMessage final = new(HttpStatusCode.OK) {
                                                                      Content = new StringContent("done")
                                                                  };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        string result = await service.Get<string>("https://original-host.example/start", new HttpOptions { FollowRedirects = true });

        Assert.That(result, Is.EqualTo("done"));
        Assert.That(handler.RequestedUris, Has.Count.EqualTo(2));
        Assert.That(handler.RequestedUris[1], Is.EqualTo(new Uri("https://original-host.example/target")));
    }

    [Test, Parallelizable]
    public async Task UrlProcessorIsAppliedBeforeUriResolution() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target?raw=1");

        using HttpResponseMessage final = new(HttpStatusCode.OK) {
                                                                      Content = new StringContent("done")
                                                                  };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        string result = await service.Get<string>("https://original-host.example/start",
                                                    new HttpOptions {
                                                                        FollowRedirects = true,
                                                                        UrlProcessor = location => new Uri(location).GetLeftPart(UriPartial.Path)
                                                                    });

        Assert.That(result, Is.EqualTo("done"));
        Assert.That(handler.RequestedUris[1], Is.EqualTo(new Uri("https://other-host.example/target")));
    }

    [Test, Parallelizable]
    public async Task StreamingOptionCarriesThroughRedirectHop() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        ProbeContent finalContent = new("done"u8.ToArray());
        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = finalContent };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        using HttpResponseMessage result = await service.Get<HttpResponseMessage>(
            "https://original-host.example/start",
            new HttpOptions { FollowRedirects = true, CompletionOption = HttpCompletionOption.ResponseHeadersRead });

        Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(finalContent.BytesRead, Is.Zero);
    }

    [Test, Parallelizable]
    public async Task SupersededRedirectResponseIsDisposed() {
        ProbeContent redirectContent = new([]);
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect) { Content = redirectContent };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Get<string>("https://original-host.example/start", new HttpOptions { FollowRedirects = true });

        Assert.That(redirectContent.Disposed, Is.True);
    }

    static string[] HeaderValues(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.ToArray() : [];

    [Test, Parallelizable]
    public async Task SendWithPreBuiltRequest_CallerHeader_ReachesBothHops() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        HttpRequestMessage request = new(HttpMethod.Get, "https://original-host.example/start");
        request.Headers.TryAddWithoutValidation("X-Caller-Marker", "caller-header-value");

        await service.Send<string>(request, new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "X-Caller-Marker"), Is.EqualTo(new[] { "caller-header-value" }));
        Assert.That(HeaderValues(handler.Requests[1], "X-Caller-Marker"), Is.EqualTo(new[] { "caller-header-value" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9609: an options bag hop 0 ignored must not be applied by the redirect hop either")]
    public async Task SendWithPreBuiltRequest_OptionHeader_ReachesNeitherHop() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        HttpRequestMessage request = new(HttpMethod.Get, "https://original-host.example/start");
        request.Headers.TryAddWithoutValidation("X-Caller-Marker", "caller-header-value");

        await service.Send<string>(request,
                                   new HttpOptions {
                                                       FollowRedirects = true,
                                                       Headers = [new HttpHeader { Key = "X-Option-Marker", Value = "option-header-value" }]
                                                   });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "X-Caller-Marker"), Is.EqualTo(new[] { "caller-header-value" }));
        Assert.That(HeaderValues(handler.Requests[1], "X-Caller-Marker"), Is.EqualTo(new[] { "caller-header-value" }));
        Assert.That(HeaderValues(handler.Requests[0], "X-Option-Marker"), Is.Empty);
        Assert.That(HeaderValues(handler.Requests[1], "X-Option-Marker"), Is.Empty);
    }

    [Test, Parallelizable]
    public async Task GetWithTokenProvider_AuthorizationHeader_ReachesBothHops() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Get<string>("https://original-host.example/start",
                                  new HttpOptions { FollowRedirects = true, TokenProvider = new CountingTokenProvider("url-overload-token") });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "Authorization"), Is.EqualTo(new[] { "Bearer url-overload-token" }));
        Assert.That(HeaderValues(handler.Requests[1], "Authorization"), Is.EqualTo(new[] { "Bearer url-overload-token" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9618 section 5: the redirect hop stops constructing its own request, so one call mints one token")]
    public async Task GetWithTokenProvider_FollowedRedirect_RequestsTokenOnce() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);
        CountingTokenProvider tokenProvider = new("url-overload-token");

        await service.Get<string>("https://original-host.example/start",
                                  new HttpOptions { FollowRedirects = true, TokenProvider = tokenProvider });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(tokenProvider.Calls, Is.EqualTo(1));
    }

    [Test, Parallelizable]
    public async Task PostWithExpectContinue_ExpectHeader_IsDroppedOnRedirectHop() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Post<string, string>("https://original-host.example/start", "body",
                                           new HttpOptions {
                                                               FollowRedirects = true,
                                                               ExpectContinue = true,
                                                               Headers = [new HttpHeader { Key = "X-Option-Marker", Value = "option-header-value" }]
                                                           });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "Expect"), Is.EqualTo(new[] { "100-continue" }));
        Assert.That(HeaderValues(handler.Requests[0], "X-Option-Marker"), Is.EqualTo(new[] { "option-header-value" }));
        Assert.That(HeaderValues(handler.Requests[1], "Expect"), Is.Empty);
        Assert.That(HeaderValues(handler.Requests[1], "X-Option-Marker"), Is.EqualTo(new[] { "option-header-value" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9618 section 4.2: body descriptors do not survive the bodyless hop, everything else does")]
    public async Task SendWithTransferEncoding_BodyDescriptor_IsDroppedWhileOtherHeadersSurvive() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        HttpRequestMessage request = new(HttpMethod.Get, "https://original-host.example/start");
        request.Headers.TransferEncodingChunked = true;
        request.Headers.TryAddWithoutValidation("X-Caller-Marker", "caller-header-value");

        await service.Send<string>(request, new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "Transfer-Encoding"), Is.EqualTo(new[] { "chunked" }));
        Assert.That(HeaderValues(handler.Requests[1], "Transfer-Encoding"), Is.Empty);
        Assert.That(HeaderValues(handler.Requests[1], "X-Caller-Marker"), Is.EqualTo(new[] { "caller-header-value" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9618 section 7: an unstamped response leaves no previous request to inherit from, so the hop goes out bare")]
    public async Task RedirectFromUnstampedResponse_HopCarriesNoHeaders() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final) { StampRequestMessage = false };
        HttpService service = new(handler);

        HttpRequestMessage request = new(HttpMethod.Get, "https://original-host.example/start");
        request.Headers.TryAddWithoutValidation("X-Caller-Marker", "caller-header-value");

        await service.Send<string>(request,
                                   new HttpOptions {
                                                       FollowRedirects = true,
                                                       Headers = [new HttpHeader { Key = "X-Option-Marker", Value = "option-header-value" }]
                                                   });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "X-Caller-Marker"), Is.EqualTo(new[] { "caller-header-value" }));
        Assert.That(handler.Requests[1].Headers, Is.Empty);
    }

    [Test, Parallelizable]
    [Description("DiVoid #9626: copying a header means copying every one of its values, not just the first")]
    public async Task SendWithMultiValuedHeader_EveryValue_ReachesTheRedirectHop() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        HttpRequestMessage request = new(HttpMethod.Get, "https://original-host.example/start");
        request.Headers.TryAddWithoutValidation("X-Multi-Marker", "first-header-value");
        request.Headers.TryAddWithoutValidation("X-Multi-Marker", "second-header-value");

        await service.Send<string>(request, new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "X-Multi-Marker"), Is.EqualTo(new[] { "first-header-value", "second-header-value" }));
        Assert.That(HeaderValues(handler.Requests[1], "X-Multi-Marker"), Is.EqualTo(new[] { "first-header-value", "second-header-value" }));
    }
}

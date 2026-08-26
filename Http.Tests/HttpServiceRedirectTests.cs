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
    [Description("DiVoid #9622: the superseded response is disposed because the hop happened, not because an unfollowed 302 was read out")]
    public async Task SupersededRedirectResponseIsDisposed() {
        ProbeContent redirectContent = new("superseded"u8.ToArray());
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect) { Content = redirectContent };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Get<string>("https://original-host.example/start", new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
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
    public async Task GetWithTokenProvider_SameOriginRedirect_AuthorizationReachesBothHops() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://original-host.example/target");

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
    [Description("DiVoid #9622: the hop is re-issued as a bodyless GET, so the verb that produced the redirect does not carry to it")]
    public async Task PostWithBody_FollowedRedirect_HopIsIssuedAsGetWithoutBody() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.Requests[0].Method.Method, Is.EqualTo("POST"));
        Assert.That(handler.Requests[0].Content, Is.Not.Null);
        Assert.That(handler.Requests[1].Method.Method, Is.EqualTo("GET"));
        Assert.That(handler.Requests[1].Content, Is.Null);
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

    [Test, Parallelizable]
    [Description("DiVoid #9619: a followed redirect must not hand the caller's token to a host the remote server named")]
    public async Task GetWithTokenProvider_CrossOriginRedirect_AuthorizationIsStripped() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Get<string>("https://original-host.example/start",
                                  new HttpOptions { FollowRedirects = true, TokenProvider = new CountingTokenProvider("url-overload-token") });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "Authorization"), Is.EqualTo(new[] { "Bearer url-overload-token" }));
        Assert.That(HeaderValues(handler.Requests[1], "Authorization"), Is.Empty);
    }

    [Parallelizable]
    [TestCase("https://original-host.example/start", "https://original-host.example/target", true)]
    [TestCase("https://original-host.example/start", "https://other-host.example/target", false)]
    [TestCase("https://original-host.example/start", "https://cdn.original-host.example/target", false)]
    [TestCase("https://original-host.example/start", "https://original-host.example:8443/target", false)]
    [TestCase("http://original-host.example/start", "https://original-host.example/target", false)]
    [TestCase("http://original-host.example:8443/start", "https://original-host.example:8443/target", false)]
    [Description("DiVoid #9633: an origin is scheme, host and port together, so a change in any one of them alone strips the credential")]
    public async Task GetWithTokenProvider_Redirect_AuthorizationSurvivesOnlyWithinTheOrigin(string start, string location, bool kept) {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri(location);

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Get<string>(start,
                                  new HttpOptions { FollowRedirects = true, TokenProvider = new CountingTokenProvider("url-overload-token") });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "Authorization"), Is.EqualTo(new[] { "Bearer url-overload-token" }));
        Assert.That(HeaderValues(handler.Requests[1], "Authorization"),
                    Is.EqualTo(kept ? new[] { "Bearer url-overload-token" } : Array.Empty<string>()));
    }

    [Test, Parallelizable]
    public async Task GetWithTokenProvider_RelativeLocation_AuthorizationReachesBothHops() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("/target", UriKind.Relative);

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Get<string>("https://original-host.example/start",
                                  new HttpOptions { FollowRedirects = true, TokenProvider = new CountingTokenProvider("url-overload-token") });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.RequestedUris[1], Is.EqualTo(new Uri("https://original-host.example/target")));
        Assert.That(HeaderValues(handler.Requests[0], "Authorization"), Is.EqualTo(new[] { "Bearer url-overload-token" }));
        Assert.That(HeaderValues(handler.Requests[1], "Authorization"), Is.EqualTo(new[] { "Bearer url-overload-token" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9633: only the credential names leave the cross-origin hop, every other caller header still rides it")]
    public async Task SendWithAuthorizationHeader_CrossOriginRedirect_CredentialIsStrippedWhileOtherHeadersSurvive() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        HttpRequestMessage request = new(HttpMethod.Get, "https://original-host.example/start");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer caller-token");
        request.Headers.TryAddWithoutValidation("X-Caller-Marker", "caller-header-value");

        await service.Send<string>(request, new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "Authorization"), Is.EqualTo(new[] { "Bearer caller-token" }));
        Assert.That(HeaderValues(handler.Requests[0], "X-Caller-Marker"), Is.EqualTo(new[] { "caller-header-value" }));
        Assert.That(HeaderValues(handler.Requests[1], "Authorization"), Is.Empty);
        Assert.That(HeaderValues(handler.Requests[1], "X-Caller-Marker"), Is.EqualTo(new[] { "caller-header-value" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9633: the strip list is the public SensitiveHeaders set, so a name the caller adds is withheld from the hop and not merely redacted")]
    public async Task SendWithCallerAddedSensitiveHeader_CrossOriginRedirect_IsStripped() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);
        service.SensitiveHeaders.Add("X-Tenant-Secret");

        HttpRequestMessage request = new(HttpMethod.Get, "https://original-host.example/start");
        request.Headers.TryAddWithoutValidation("X-Tenant-Secret", "tenant-secret-value");

        await service.Send<string>(request, new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "X-Tenant-Secret"), Is.EqualTo(new[] { "tenant-secret-value" }));
        Assert.That(HeaderValues(handler.Requests[1], "X-Tenant-Secret"), Is.Empty);
    }

    [Test, Parallelizable]
    [Description("DiVoid #9633: a response stamped with no request uri leaves the origin unprovable, and unprovable strips even though the target is same-origin")]
    public async Task SendWithAuthorizationHeader_UnprovableOrigin_CredentialIsStripped() {
        HttpRequestMessage stampedWithoutUri = new();
        stampedWithoutUri.Headers.TryAddWithoutValidation("Authorization", "Bearer caller-token");
        stampedWithoutUri.Headers.TryAddWithoutValidation("X-Caller-Marker", "caller-header-value");

        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect) { RequestMessage = stampedWithoutUri };
        redirect.Headers.Location = new Uri("https://original-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final) { StampRequestMessage = false };
        HttpService service = new(handler);

        HttpRequestMessage request = new(HttpMethod.Get, "https://original-host.example/start");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer caller-token");
        request.Headers.TryAddWithoutValidation("X-Caller-Marker", "caller-header-value");

        await service.Send<string>(request, new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "Authorization"), Is.EqualTo(new[] { "Bearer caller-token" }));
        Assert.That(handler.RequestedUris[1], Is.EqualTo(new Uri("https://original-host.example/target")));
        Assert.That(HeaderValues(handler.Requests[1], "Authorization"), Is.Empty);
        Assert.That(HeaderValues(handler.Requests[1], "X-Caller-Marker"), Is.EqualTo(new[] { "caller-header-value" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9646: a non-ASCII host keeps its case through Uri, so the origin comparison has to match it ignoring case")]
    public async Task GetWithTokenProvider_HostDiffersOnlyByNonAsciiCase_AuthorizationReachesBothHops() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://\u00e4\u00f6\u00fc.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Get<string>("https://\u00c4\u00d6\u00dc.example/start",
                                  new HttpOptions { FollowRedirects = true, TokenProvider = new CountingTokenProvider("url-overload-token") });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.Requests[0].RequestUri!.Host, Is.EqualTo("\u00c4\u00d6\u00dc.example"));
        Assert.That(handler.Requests[1].RequestUri!.Host, Is.EqualTo("\u00e4\u00f6\u00fc.example"));
        Assert.That(HeaderValues(handler.Requests[0], "Authorization"), Is.EqualTo(new[] { "Bearer url-overload-token" }));
        Assert.That(HeaderValues(handler.Requests[1], "Authorization"), Is.EqualTo(new[] { "Bearer url-overload-token" }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9646: a name removed from SensitiveHeaders rides the cross-origin hop, so the set is the sole authority and not a floor over hard-wired defaults")]
    public async Task SendWithAuthorizationHeader_NameRemovedFromSensitiveHeaders_CredentialRidesCrossOriginHop() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);
        service.SensitiveHeaders.Remove("Authorization");

        HttpRequestMessage request = new(HttpMethod.Get, "https://original-host.example/start");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer caller-token");

        await service.Send<string>(request, new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.Requests[1].RequestUri!.Host, Is.EqualTo("other-host.example"));
        Assert.That(HeaderValues(handler.Requests[0], "Authorization"), Is.EqualTo(new[] { "Bearer caller-token" }));
        Assert.That(HeaderValues(handler.Requests[1], "Authorization"), Is.EqualTo(new[] { "Bearer caller-token" }));
    }
}

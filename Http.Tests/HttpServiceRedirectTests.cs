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

    [Test, Parallelizable]
    [Description("DiVoid #14513: a 308 matched no redirect arm and returned a silent null where the request had to be repeated")]
    public async Task Post308_HopRepeatsPostWithSameBody() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        string result = await service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true });

        Assert.That(result, Is.EqualTo("done"));
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.Requests[1].Method.Method, Is.EqualTo("POST"));
        Assert.That(handler.Requests[1].Content, Is.Not.Null);
        Assert.That(handler.RequestedUris[1], Is.EqualTo(new Uri("https://other-host.example/target")));
    }

    [Test, Parallelizable]
    public async Task Post307_HopRepeatsPostWithSameBody() {
        using HttpResponseMessage redirect = new(HttpStatusCode.RedirectKeepVerb);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        string result = await service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true });

        Assert.That(result, Is.EqualTo("done"));
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.Requests[1].Method.Method, Is.EqualTo("POST"));
        Assert.That(handler.Requests[1].Content, Is.Not.Null);
        Assert.That(handler.RequestedUris[1], Is.EqualTo(new Uri("https://other-host.example/target")));
    }

    [Test, Parallelizable]
    public async Task Post308_BodyBytesAreIdenticalOnBothHops() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        MemoryStream body = new("seekable-body-bytes"u8.ToArray());

        await service.Post<Stream, string>("https://original-host.example/start", body, new HttpOptions { FollowRedirects = true });

        Assert.That(handler.RequestBodies, Has.Count.EqualTo(2));
        Assert.That(handler.RequestBodies[0], Is.EqualTo("seekable-body-bytes"u8.ToArray()));
        Assert.That(handler.RequestBodies[1], Is.EqualTo("seekable-body-bytes"u8.ToArray()));
    }

    [Test, Parallelizable]
    public async Task Post308_ContentTypeRidesTheHop() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.Requests[0].Content!.Headers.ContentType!.MediaType, Is.EqualTo("application/json"));
        Assert.That(handler.Requests[1].Content!.Headers.ContentType!.MediaType, Is.EqualTo("application/json"));
    }

    [Test, Parallelizable]
    public void Post308_NonSeekableStreamBody_ThrowsHttpServiceException() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        NonSeekableStream body = new("one-shot-body"u8.ToArray());

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<Stream, string>("https://original-host.example/start", body, new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.RequestBodies[0], Is.EqualTo("one-shot-body"u8.ToArray()));
        Assert.That(error.InnerException, Is.TypeOf<HttpRequestException>());
        Assert.That(error.InnerException!.InnerException, Is.TypeOf<InvalidOperationException>());
        Assert.That(error.Response.StatusCode, Is.EqualTo(HttpStatusCode.PermanentRedirect));
        Assert.That(error.Message, Does.Contain("the request body cannot be sent a second time"));
        Assert.That(error.Message, Does.Contain("POST"));
        Assert.That(error.Message, Does.Contain("308"));
        Assert.That(error.Message, Does.Contain("https://other-host.example/target"));
        Assert.That(error.Message, Does.Contain("https://original-host.example/start"));
    }

    [Test, Parallelizable]
    public void Post308_WithoutLocation_ThrowsAndSendsNoSecondRequest() {
        ProbeContent redirectContent = new("superseded"u8.ToArray());
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect) { Content = redirectContent };

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(error.InnerException, Is.Null);
        Assert.That(error.Response.StatusCode, Is.EqualTo(HttpStatusCode.PermanentRedirect));
        Assert.That(error.Message, Does.Contain("the response names no target to repeat it against"));
        Assert.That(redirectContent.Disposed, Is.False);
    }

    [Test, Parallelizable]
    public void Post308_UnstampedResponse_ThrowsInsteadOfDowngrading() {
        ProbeContent redirectContent = new("superseded"u8.ToArray());
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect) { Content = redirectContent };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final) { StampRequestMessage = false };
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(error.Response.StatusCode, Is.EqualTo(HttpStatusCode.PermanentRedirect));
        Assert.That(error.Message, Does.Contain("Error repeating the original request"));
        Assert.That(error.Message, Does.Contain("the response carries no request to repeat"));
        Assert.That(redirectContent.Disposed, Is.False);
    }

    [Test, Parallelizable]
    [Description("DiVoid #9633: the credential strip is a property of the target origin, so it survives a hop which now carries the body across it")]
    public async Task Post308_CrossOrigin_AuthorizationStrippedWhileBodyRides() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Post<string, string>("https://original-host.example/start", "body",
                                           new HttpOptions { FollowRedirects = true, TokenProvider = new CountingTokenProvider("url-overload-token") });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "Authorization"), Is.EqualTo(new[] { "Bearer url-overload-token" }));
        Assert.That(HeaderValues(handler.Requests[1], "Authorization"), Is.Empty);
        Assert.That(handler.Requests[1].Content, Is.Not.Null);
        Assert.That(handler.RequestBodies[1], Is.EqualTo("\"body\""u8.ToArray()));
    }

    [Test, Parallelizable]
    public async Task Post308_ExpectContinueSurvivesTheBodyCarryingHop() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Post<string, string>("https://original-host.example/start", "body",
                                           new HttpOptions { FollowRedirects = true, ExpectContinue = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(HeaderValues(handler.Requests[0], "Expect"), Is.EqualTo(new[] { "100-continue" }));
        Assert.That(HeaderValues(handler.Requests[1], "Expect"), Is.EqualTo(new[] { "100-continue" }));
    }

    [Test, Parallelizable]
    public async Task Get308_BodylessHop_StillDropsBodyDescriptor() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        HttpRequestMessage request = new(HttpMethod.Get, "https://original-host.example/start");
        request.Headers.TransferEncodingChunked = true;
        request.Headers.TryAddWithoutValidation("X-Caller-Marker", "caller-header-value");

        await service.Send<string>(request, new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.Requests[1].Method.Method, Is.EqualTo("GET"));
        Assert.That(handler.Requests[1].Content, Is.Null);
        Assert.That(HeaderValues(handler.Requests[0], "Transfer-Encoding"), Is.EqualTo(new[] { "chunked" }));
        Assert.That(HeaderValues(handler.Requests[1], "Transfer-Encoding"), Is.Empty);
        Assert.That(HeaderValues(handler.Requests[1], "X-Caller-Marker"), Is.EqualTo(new[] { "caller-header-value" }));
    }

    [Test, Parallelizable]
    public void Post308_HopFailsWithAnUnrelatedTransportError_IsNotReportedAsAnUnreplayableBody() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        SingleUseContent body = new("single-use-body"u8.ToArray(), new IOException("the connection was reset"));

        HttpRequestException error = Assert.ThrowsAsync<HttpRequestException>(
            () => service.Post<HttpContent, string>("https://original-host.example/start", body, new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.RequestBodies[0], Is.EqualTo("single-use-body"u8.ToArray()));
        Assert.That(error.InnerException!.InnerException, Is.TypeOf<IOException>());
    }

    [Test, Parallelizable]
    [Description("DiVoid #14530: an exception the failure translation does not match carries no response to the caller, so the superseded one has to be released here")]
    public void Post308_HopFailsWithAnUnrelatedTransportError_DisposesTheSupersededResponse() {
        ProbeContent redirectContent = new("superseded"u8.ToArray());
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect) { Content = redirectContent };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        SingleUseContent body = new("single-use-body"u8.ToArray(), new IOException("the connection was reset"));

        HttpRequestException error = Assert.ThrowsAsync<HttpRequestException>(
            () => service.Post<HttpContent, string>("https://original-host.example/start", body, new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(error.InnerException!.InnerException, Is.TypeOf<IOException>());
        Assert.That(redirectContent.Disposed, Is.True);
    }

    [Test, Parallelizable]
    [Description("DiVoid #14530: a stamped request without a uri leaves no origin to resolve the target against, and the arm must say so rather than dereference it")]
    public void Post308_ResponseStampedWithoutRequestUri_ThrowsInsteadOfFailingToResolve() {
        HttpRequestMessage stampedWithoutUri = new();
        stampedWithoutUri.Headers.TryAddWithoutValidation("X-Caller-Marker", "caller-header-value");

        ProbeContent redirectContent = new("superseded"u8.ToArray());
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect) { RequestMessage = stampedWithoutUri, Content = redirectContent };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final) { StampRequestMessage = false };
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(error.Response.StatusCode, Is.EqualTo(HttpStatusCode.PermanentRedirect));
        Assert.That(error.Message, Does.Contain("the response carries no request uri to resolve the target against"));
        Assert.That(redirectContent.Disposed, Is.False);
    }

    [Test, Parallelizable]
    [Description("DiVoid #9939: the redirect failure message is a new error surface, so the target it names is query-redacted like every other")]
    public void Post308_NonSeekableStreamBody_FailureMessageRedactsTheTargetQuery() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target?token=super-secret-value&page=2");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        NonSeekableStream body = new("one-shot-body"u8.ToArray());

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<Stream, string>("https://original-host.example/start", body,
                                               new HttpOptions { FollowRedirects = true, HeaderDumpMode = HeaderDumpMode.Omitted }))!;

        Assert.That(error.Message, Does.Contain("token=<redacted>"));
        Assert.That(error.Message, Does.Contain("page=2"));
        Assert.That(error.Message, Does.Not.Contain("super-secret-value"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #14516 D3: the superseded response is handed over live so the caller can read the location it could not follow")]
    public void Post308_NonSeekableStreamBody_LeavesTheSupersededResponseUndisposed() {
        ProbeContent redirectContent = new("superseded"u8.ToArray());
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect) { Content = redirectContent };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        NonSeekableStream body = new("one-shot-body"u8.ToArray());

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<Stream, string>("https://original-host.example/start", body, new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(redirectContent.Disposed, Is.False);
        Assert.That(error.Response, Is.SameAs(redirect));
        Assert.That(error.Response.Headers.Location, Is.EqualTo(new Uri("https://other-host.example/target")));
    }

    [Test, Parallelizable]
    public async Task Post308_SupersededRedirectResponseIsDisposed() {
        ProbeContent redirectContent = new("superseded"u8.ToArray());
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect) { Content = redirectContent };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(redirectContent.Disposed, Is.True);
    }

    [Test, Parallelizable]
    [Description("DiVoid #14516 section 6.2: UrlProcessor is the caller's mitigation for the body now crossing the origin, so it has to run on the arm that carries it")]
    public async Task Post308_UrlProcessorIsAppliedBeforeUriResolution() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target?raw=1");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        await service.Post<string, string>("https://original-host.example/start", "body",
                                           new HttpOptions {
                                                               FollowRedirects = true,
                                                               UrlProcessor = location => new Uri(location).GetLeftPart(UriPartial.Path)
                                                           });

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.RequestedUris[1], Is.EqualTo(new Uri("https://other-host.example/target")));
        Assert.That(handler.Requests[1].Content, Is.Not.Null);
    }

    [Test, Parallelizable]
    [Description("DiVoid #14530: the body-replay diagnosis belongs to the arm that replays a body, so a failing legacy hop must not borrow it")]
    public void Get302_HopSendFails_IsNotReportedAsAnUnreplayableBody() {
        ProbeContent redirectContent = new("superseded"u8.ToArray());
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect) { Content = redirectContent };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => service.Get<string>("https://original-host.example/start", new HttpOptions { FollowRedirects = true }));

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.Requests[1].Content, Is.Null);
        Assert.That(redirectContent.Disposed, Is.True);
    }

    [Test, Parallelizable]
    [Description("DiVoid #14538: UrlProcessor is caller code running on the redirect path, so a throw out of it must not strand the superseded response")]
    public void Post308_UrlProcessorThrows_DisposesTheSupersededResponse() {
        ProbeContent redirectContent = new("superseded"u8.ToArray());
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect) { Content = redirectContent };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => service.Post<string, string>("https://original-host.example/start", "body",
                                               new HttpOptions {
                                                                   FollowRedirects = true,
                                                                   UrlProcessor = _ => throw new InvalidOperationException("processor refused the location")
                                                               }));

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(redirectContent.Disposed, Is.True);
    }

    [Test, Parallelizable]
    [Description("DiVoid #9617: the redirect failure message is a new DumpHeaders call site, so the configured redaction governs it like every other")]
    public void Post308_FailureMessage_CarriesTheHeaderBlockUnderTheConfiguredMode() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.TryAddWithoutValidation("Set-Cookie", "session=cookie-secret-value");
        redirect.Headers.TryAddWithoutValidation("X-Trace-Marker", "trace-header-value");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true }))!;

        Assert.That(error.Message, Does.Contain("Response Headers"));
        Assert.That(error.Message, Does.Contain("Set-Cookie: <redacted>"));
        Assert.That(error.Message, Does.Contain("X-Trace-Marker: trace-header-value"));
        Assert.That(error.Message, Does.Not.Contain("cookie-secret-value"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9617: a per-call dump mode has to reach the redirect failure site, or a caller who quietened this call still gets the service default")]
    public void Post308_FailureMessage_PerCallDumpModeOverridesTheServiceDefault() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.TryAddWithoutValidation("X-Trace-Marker", "trace-header-value");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler) { HeaderDumpMode = HeaderDumpMode.Full };

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<string, string>("https://original-host.example/start", "body",
                                               new HttpOptions { FollowRedirects = true, HeaderDumpMode = HeaderDumpMode.Omitted }))!;

        Assert.That(error.Message, Does.Contain("the response names no target to repeat it against"));
        Assert.That(error.Message, Does.Not.Contain("Response Headers"));
        Assert.That(error.Message, Does.Not.Contain("trace-header-value"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #14546: the exemption is for an exception carrying this response, not for every exception of a type that could carry one")]
    public void Post308_UrlProcessorThrowsHttpServiceException_DisposesTheSupersededResponse() {
        ProbeContent redirectContent = new("superseded"u8.ToArray());
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect) { Content = redirectContent };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };
        using HttpResponseMessage unrelated = new(HttpStatusCode.NotFound);

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<string, string>("https://original-host.example/start", "body",
                                               new HttpOptions {
                                                                   FollowRedirects = true,
                                                                   UrlProcessor = _ => throw new HttpServiceException(unrelated, "discovery lookup failed")
                                                               }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(error.Response, Is.SameAs(unrelated));
        Assert.That(redirectContent.Disposed, Is.True);
    }

    [Test, Parallelizable]
    public async Task Post301_HopIsIssuedAsGetWithoutBody() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Moved);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        string result = await service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true });

        Assert.That(result, Is.EqualTo("done"));
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.Requests[1].Method.Method, Is.EqualTo("GET"));
        Assert.That(handler.Requests[1].Content, Is.Null);
        Assert.That(handler.RequestedUris[1], Is.EqualTo(new Uri("https://other-host.example/target")));
    }

    [Test, Parallelizable]
    public async Task Post303_HopIsIssuedAsGetWithoutBody() {
        using HttpResponseMessage redirect = new(HttpStatusCode.RedirectMethod);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        string result = await service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true });

        Assert.That(result, Is.EqualTo("done"));
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.Requests[1].Method.Method, Is.EqualTo("GET"));
        Assert.That(handler.Requests[1].Content, Is.Null);
        Assert.That(handler.RequestedUris[1], Is.EqualTo(new Uri("https://other-host.example/target")));
    }

    [Test, Parallelizable]
    [Description("DiVoid #14538: a location that will not resolve fails before any hop, on both arms, and still releases the response it superseded")]
    public void Get302_LocationDoesNotResolve_DisposesTheSupersededResponse() {
        ProbeContent redirectContent = new("superseded"u8.ToArray());
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect) { Content = redirectContent };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        Assert.ThrowsAsync<UriFormatException>(
            () => service.Get<string>("https://original-host.example/start",
                                      new HttpOptions { FollowRedirects = true, UrlProcessor = _ => "http://" }));

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(redirectContent.Disposed, Is.True);
    }

    [Test, Parallelizable]
    [Description("DiVoid #14538: building the hop request is the last thing that can fail before the send, and it releases the superseded response like every other failure")]
    public void Get302_RedirectRequestCannotBeBuilt_DisposesTheSupersededResponse() {
        ProbeContent redirectContent = new("superseded"u8.ToArray());
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect) { Content = redirectContent };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final) { StampRequestMessage = false };
        HttpService service = new(handler);

        Assert.ThrowsAsync<UriFormatException>(
            () => service.Get<string>("https://original-host.example/start",
                                      new HttpOptions { FollowRedirects = true, UrlProcessor = _ => "http://" }));

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(redirectContent.Disposed, Is.True);
    }

    [Test, Parallelizable]
    public void Post308_ExhaustionSignalledAtTheOutermostLevel_IsTranslated() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
        Assert.That(error.Message, Does.Contain("the request body cannot be sent a second time"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #14516 D3: ObjectDisposedException is inside the net by inheritance, and it is how a runtime that disposes request content after a send is meant to degrade")]
    public void Post308_ExhaustionSignalledThreeLevelsDeep_IsTranslated() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        SingleUseContent body = new("single-use-body"u8.ToArray(), new ObjectDisposedException("request content"));

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<HttpContent, string>("https://original-host.example/start", body, new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(error.InnerException, Is.TypeOf<HttpRequestException>());
        Assert.That(error.InnerException!.InnerException, Is.TypeOf<HttpRequestException>());
        Assert.That(error.InnerException.InnerException!.InnerException, Is.TypeOf<ObjectDisposedException>());
        Assert.That(error.Message, Does.Contain("the request body cannot be sent a second time"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #14538: on a path whose only product is a diagnosis, the guards have to fire in the order that names the real cause")]
    public void Post308_WithoutRequestUriAndWithoutLocation_ReportsTheMissingRequestUri() {
        HttpRequestMessage stampedWithoutUri = new();

        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect) { RequestMessage = stampedWithoutUri };

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final) { StampRequestMessage = false };
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(error.Message, Does.Contain("the response carries no request uri to resolve the target against"));
        Assert.That(error.Message, Does.Contain("redirect to ''"));
        Assert.That(error.Message, Does.Not.Contain("names no target"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8316: leaving redirect following off does not make a redirect silent")]
    public void Get308_FollowRedirectsFalse_Throws() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect) { Content = new StringContent(string.Empty) };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        SequenceHandler handler = new(redirect);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://original-host.example/start"))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(error.Response.StatusCode, Is.EqualTo(HttpStatusCode.PermanentRedirect));
    }

    [Test, Parallelizable]
    [Description("DiVoid #14559: the processor resolves a target rather than rewriting one, so it is asked even when the response named none")]
    public void Post308_NoLocation_UrlProcessorReceivesNull() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect) { Content = new StringContent(string.Empty) };

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        List<string> seen = [];

        Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<string, string>("https://original-host.example/start", "body",
                                               new HttpOptions {
                                                                   FollowRedirects = true,
                                                                   UrlProcessor = location => {
                                                                                      seen.Add(location);
                                                                                      return location;
                                                                                  }
                                                               }));

        Assert.That(seen, Has.Count.EqualTo(1));
        Assert.That(seen[0], Is.Null);
    }

    [Test, Parallelizable]
    [Description("DiVoid #14559: a processor may name a target the response did not, and the hop uses what it named; its processor is idempotent by design")]
    public async Task Post308_NoLocation_UrlProcessorSynthesisesTarget_HopUsesIt() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect) { Content = new StringContent(string.Empty) };

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        string result = await service.Post<string, string>("https://original-host.example/start", "body",
                                                          new HttpOptions {
                                                                              FollowRedirects = true,
                                                                              UrlProcessor = location => location ?? "https://fallback-host.example/target"
                                                                          });

        Assert.That(result, Is.EqualTo("done"));
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.RequestedUris[1], Is.EqualTo(new Uri("https://fallback-host.example/target")));
    }

    [Test, Parallelizable]
    [Description("DiVoid #14559: returning null declines the hop, and the library's own no-target handling is what backstops it")]
    public void Post308_UrlProcessorReturnsNull_FailsWithNoTarget() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect) { Content = new StringContent(string.Empty) };
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Post<string, string>("https://original-host.example/start", "body",
                                               new HttpOptions { FollowRedirects = true, UrlProcessor = _ => null }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(error.Message, Does.Contain("the response names no target to repeat it against"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #14559: the contract is a property of the seam, so it does not differ between the two redirect arms")]
    public async Task Get302_NoLocation_UrlProcessorReceivesNull() {
        using HttpResponseMessage redirect = new(HttpStatusCode.Redirect) { Content = new StringContent(string.Empty) };

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        List<string> seen = [];

        await service.Get<string>("https://original-host.example/start",
                                  new HttpOptions {
                                                      FollowRedirects = true,
                                                      UrlProcessor = location => {
                                                                         seen.Add(location);
                                                                         return location;
                                                                     }
                                                  });

        Assert.That(seen, Has.Count.EqualTo(1));
        Assert.That(seen[0], Is.Null);
    }
}

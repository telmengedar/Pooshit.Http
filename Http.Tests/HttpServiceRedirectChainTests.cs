using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Http.Tests.TestSupport;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceRedirectChainTests {

    static HttpResponseMessage Redirect(HttpStatusCode status, string location) {
        HttpResponseMessage response = new(status) { Content = new StringContent(string.Empty) };
        response.Headers.Location = new Uri(location);
        return response;
    }

    static HttpResponseMessage Final() =>
        new(HttpStatusCode.OK) { Content = new StringContent("done") };

    static string[] HeaderValues(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.ToArray() : [];

    [Test, Parallelizable]
    [Description("DiVoid #9633: a hop inherits from the request before it, so a credential dropped when the chain left the origin cannot come back when it returns")]
    public async Task GetWithTokenProvider_ChainCrossingOriginMidway_CredentialStrippedAndNotRestored() {
        using HttpResponseMessage first = Redirect(HttpStatusCode.Redirect, "https://other-host.example/mid");
        using HttpResponseMessage second = Redirect(HttpStatusCode.Redirect, "https://original-host.example/final");
        using HttpResponseMessage final = Final();

        SequenceHandler handler = new(first, second, final);
        HttpService service = new(handler);

        await service.Get<string>("https://original-host.example/start",
                                  new HttpOptions { FollowRedirects = true, TokenProvider = new CountingTokenProvider("url-overload-token") });

        Assert.That(handler.Requests, Has.Count.EqualTo(3));
        Assert.That(handler.RequestedUris[2], Is.EqualTo(new Uri("https://original-host.example/final")));
        Assert.That(HeaderValues(handler.Requests[0], "Authorization"), Is.EqualTo(new[] { "Bearer url-overload-token" }));
        Assert.That(HeaderValues(handler.Requests[1], "Authorization"), Is.Empty);
        Assert.That(HeaderValues(handler.Requests[2], "Authorization"), Is.Empty);
    }

    [Test, Parallelizable]
    [Description("DiVoid #8323: a chain of ordinary length is followed to its end rather than delivered as its second redirect")]
    public async Task Get302_ThreeHopChain_FollowsToTheEnd() {
        using HttpResponseMessage first = Redirect(HttpStatusCode.Redirect, "https://other-host.example/second");
        using HttpResponseMessage second = Redirect(HttpStatusCode.Redirect, "https://other-host.example/third");
        using HttpResponseMessage final = Final();

        SequenceHandler handler = new(first, second, final);
        HttpService service = new(handler);

        string? result = await service.Get<string>("https://original-host.example/start", new HttpOptions { FollowRedirects = true });

        Assert.That(result, Is.EqualTo("done"));
        Assert.That(handler.Requests, Has.Count.EqualTo(3));
        Assert.That(handler.RequestedUris[2], Is.EqualTo(new Uri("https://other-host.example/third")));
    }

    [Test, Parallelizable]
    public void Get302_ChainExceedingTheCap_Throws() {
        HttpResponseMessage[] chain = Enumerable.Range(0, 11)
                                                .Select(hop => Redirect(HttpStatusCode.Redirect, $"https://other-host.example/hop{hop}"))
                                                .ToArray();

        SequenceHandler handler = new(chain);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://original-host.example/start", new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(11));
        Assert.That(error.Response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8323: the counter is what makes a cycle terminate, so it has to survive the iteration it counts")]
    public void Get302_Cycle_TerminatesAtTheCap() {
        HttpResponseMessage[] chain = Enumerable.Range(0, 15)
                                                .Select(_ => Redirect(HttpStatusCode.Redirect, "https://original-host.example/loop"))
                                                .ToArray();

        SequenceHandler handler = new(chain);
        HttpService service = new(handler);

        Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://original-host.example/loop", new HttpOptions { FollowRedirects = true }));

        Assert.That(handler.Requests, Has.Count.EqualTo(11));
        Assert.That(handler.RequestedUris.Distinct().ToList(), Has.Count.EqualTo(1));
    }

    [Test, Parallelizable]
    public void Get302_ChainExceedingTheCap_ExceptionCarriesTheLastResponseUndisposed() {
        ProbeContent lastContent = new("superseded"u8.ToArray());
        HttpResponseMessage[] chain = Enumerable.Range(0, 11)
                                                .Select(hop => Redirect(HttpStatusCode.Redirect, $"https://other-host.example/hop{hop}"))
                                                .ToArray();
        chain[10].Content = lastContent;

        SequenceHandler handler = new(chain);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://original-host.example/start", new HttpOptions { FollowRedirects = true }))!;

        Assert.That(error.Response, Is.SameAs(chain[10]));
        Assert.That(lastContent.Disposed, Is.False);
    }

    [Test, Parallelizable]
    public void Get302_ChainExceedingTheCap_MessageNamesTheLimitAndTheTarget() {
        HttpResponseMessage[] chain = Enumerable.Range(0, 11)
                                                .Select(hop => Redirect(HttpStatusCode.Redirect, $"https://other-host.example/hop{hop}"))
                                                .ToArray();

        SequenceHandler handler = new(chain);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://original-host.example/start",
                                      new HttpOptions { FollowRedirects = true, HeaderDumpMode = HeaderDumpMode.Omitted }))!;

        Assert.That(error.Message, Does.Contain("more than 10 redirects were followed"));
        Assert.That(error.Message, Does.Contain("https://other-host.example/hop10"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8323: the cap is reached by counting hops, so it does not depend on the last response naming a target")]
    public void Get302_ChainExceedingTheCapEndingWithoutALocation_StillThrows() {
        HttpResponseMessage[] chain = Enumerable.Range(0, 11)
                                                .Select(hop => Redirect(HttpStatusCode.Redirect, $"https://other-host.example/hop{hop}"))
                                                .ToArray();
        chain[10].Headers.Location = null;

        SequenceHandler handler = new(chain);
        HttpService service = new(handler);

        HttpServiceException error = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://original-host.example/start", new HttpOptions { FollowRedirects = true }))!;

        Assert.That(handler.Requests, Has.Count.EqualTo(11));
        Assert.That(error.Message, Does.Contain("more than 10 redirects were followed"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #14516 D2: one content instance serves every hop, so replay must not degrade with depth")]
    public async Task Post308_ThreeHopChain_BodyRepeatsOnEveryHop() {
        using HttpResponseMessage first = Redirect(HttpStatusCode.PermanentRedirect, "https://other-host.example/second");
        using HttpResponseMessage second = Redirect(HttpStatusCode.PermanentRedirect, "https://other-host.example/third");
        using HttpResponseMessage final = Final();

        SequenceHandler handler = new(first, second, final);
        HttpService service = new(handler);

        await service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true });

        Assert.That(handler.RequestBodies, Has.Count.EqualTo(3));
        Assert.That(handler.RequestBodies[0], Is.EqualTo("\"body\""u8.ToArray()));
        Assert.That(handler.RequestBodies[1], Is.EqualTo("\"body\""u8.ToArray()));
        Assert.That(handler.RequestBodies[2], Is.EqualTo("\"body\""u8.ToArray()));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8323: the status is read again on every iteration, so a chain may mix the two arms")]
    public async Task Get302Then308_MixedChain_EachHopUsesItsOwnArm() {
        using HttpResponseMessage first = Redirect(HttpStatusCode.Redirect, "https://other-host.example/second");
        using HttpResponseMessage second = Redirect(HttpStatusCode.PermanentRedirect, "https://other-host.example/third");
        using HttpResponseMessage final = Final();

        SequenceHandler handler = new(first, second, final);
        HttpService service = new(handler);

        await service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(3));
        Assert.That(handler.Requests[0].Method.Method, Is.EqualTo("POST"));
        Assert.That(handler.Requests[0].Content, Is.Not.Null);
        Assert.That(handler.Requests[1].Method.Method, Is.EqualTo("GET"));
        Assert.That(handler.Requests[1].Content, Is.Null);
        Assert.That(handler.Requests[2].Method.Method, Is.EqualTo("GET"));
        Assert.That(handler.Requests[2].Content, Is.Null);
    }

    [Test, Parallelizable]
    [Description("DiVoid #8323: the arm is decided per hop, so a preserving status early in a chain does not make the rest of it preserving")]
    public async Task Post308Then302_MixedChain_EachHopUsesItsOwnArm() {
        using HttpResponseMessage first = Redirect(HttpStatusCode.PermanentRedirect, "https://other-host.example/second");
        using HttpResponseMessage second = Redirect(HttpStatusCode.Redirect, "https://other-host.example/third");
        using HttpResponseMessage final = Final();

        SequenceHandler handler = new(first, second, final);
        HttpService service = new(handler);

        await service.Post<string, string>("https://original-host.example/start", "body", new HttpOptions { FollowRedirects = true });

        Assert.That(handler.Requests, Has.Count.EqualTo(3));
        Assert.That(handler.Requests[1].Method.Method, Is.EqualTo("POST"));
        Assert.That(handler.Requests[1].Content, Is.Not.Null);
        Assert.That(handler.Requests[2].Method.Method, Is.EqualTo("GET"));
        Assert.That(handler.Requests[2].Content, Is.Null);
    }

    [Test, Parallelizable]
    public async Task Get302_SingleHop_StillIssuesExactlyTwoRequests() {
        using HttpResponseMessage redirect = Redirect(HttpStatusCode.Redirect, "https://other-host.example/target");
        using HttpResponseMessage final = Final();

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        string? result = await service.Get<string>("https://original-host.example/start", new HttpOptions { FollowRedirects = true });

        Assert.That(result, Is.EqualTo("done"));
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
    }

    [Test, Parallelizable]
    public async Task Get302_NoRedirect_IssuesOneRequest() {
        using HttpResponseMessage final = Final();

        SequenceHandler handler = new(final);
        HttpService service = new(handler);

        string? result = await service.Get<string>("https://original-host.example/start", new HttpOptions { FollowRedirects = true });

        Assert.That(result, Is.EqualTo("done"));
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }
}

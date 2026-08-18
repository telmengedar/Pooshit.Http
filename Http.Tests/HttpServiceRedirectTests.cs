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
}

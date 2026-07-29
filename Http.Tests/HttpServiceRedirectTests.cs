using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceRedirectTests {

    /// <summary>
    /// handler which returns a pre-configured sequence of responses without touching the network,
    /// recording the uri of every request it received
    /// </summary>
    class SequenceHandler : HttpMessageHandler {
        readonly Queue<HttpResponseMessage> responses;

        public SequenceHandler(params HttpResponseMessage[] responses) {
            this.responses = new(responses);
        }

        public List<Uri?> RequestedUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            RequestedUris.Add(request.RequestUri);
            HttpResponseMessage response = responses.Dequeue();
            // real handlers (SocketsHttpHandler etc.) stamp the originating request onto the
            // response; HttpService relies on RequestMessage.RequestUri to resolve redirects
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

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
}

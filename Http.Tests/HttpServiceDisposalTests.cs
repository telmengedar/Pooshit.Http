using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Http.Tests.TestSupport;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceDisposalTests {

    static IEnumerable<TestCaseData> ResultLessMembers() {
        const string url = "https://example.test/probe";
        yield return new TestCaseData(new Func<HttpService, HttpOptions?, Task>(
            (s, o) => s.Post(url, o))).SetName("Post_NoBody");
        yield return new TestCaseData(new Func<HttpService, HttpOptions?, Task>(
            (s, o) => s.Post(url, "body", o))).SetName("Post_WithBody");
        yield return new TestCaseData(new Func<HttpService, HttpOptions?, Task>(
            (s, o) => s.Put(url, "body", o))).SetName("Put");
        yield return new TestCaseData(new Func<HttpService, HttpOptions?, Task>(
            (s, o) => s.Patch(url, "body", o))).SetName("Patch");
        yield return new TestCaseData(new Func<HttpService, HttpOptions?, Task>(
            (s, o) => s.Get(url, o))).SetName("Get");
        yield return new TestCaseData(new Func<HttpService, HttpOptions?, Task>(
            (s, o) => s.Delete(url, o))).SetName("Delete");
        yield return new TestCaseData(new Func<HttpService, HttpOptions?, Task>(
            (s, o) => s.Request("PUT", url, "body", o))).SetName("Request");
        yield return new TestCaseData(new Func<HttpService, HttpOptions?, Task>(
            (s, o) => s.Send(new HttpRequestMessage(HttpMethod.Get, url), o))).SetName("Send");
    }

    [TestCaseSource(nameof(ResultLessMembers)), Parallelizable]
    public async Task ResultLessMember_DisposesResponseOnSuccess(Func<HttpService, HttpOptions?, Task> invoke) {
        ProbeContent content = new([]);
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        await invoke(service, null);

        Assert.That(content.Disposed, Is.True);
    }

    [Test, Parallelizable]
    public void Get_Failure_ThrowsWithBodyAndErrorPathUnchanged() {
        ProbeContent content = new("failure text"u8.ToArray());
        using HttpResponseMessage response = new(HttpStatusCode.BadRequest) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get("https://example.test/probe"));

        Assert.That(exception!.Body, Is.EqualTo("failure text"));
        Assert.That(content.Disposed, Is.False);
    }

    [Test, Parallelizable]
    public void Post_NoBody_DoesNotValidateStatus() {
        using HttpResponseMessage response = new(HttpStatusCode.InternalServerError);
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        Assert.DoesNotThrowAsync(() => service.Post("https://example.test/probe"));
    }

    [Test, Parallelizable]
    public void Send_CalledWithSingleArgument_CompilesAndSucceeds() {
        using HttpResponseMessage response = new(HttpStatusCode.OK);
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        Assert.DoesNotThrowAsync(() => service.Send(new HttpRequestMessage(HttpMethod.Get, "https://example.test/probe")));
    }

    [Test, Parallelizable]
    public async Task Send_WithStreamingOption_HonoursOptionAndStillDisposes() {
        ProbeContent content = new("payload"u8.ToArray());
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        await service.Send(new HttpRequestMessage(HttpMethod.Get, "https://example.test/probe"),
            new HttpOptions { CompletionOption = HttpCompletionOption.ResponseHeadersRead });

        Assert.That(content.BytesRead, Is.Zero);
        Assert.That(content.Disposed, Is.True);
    }
}

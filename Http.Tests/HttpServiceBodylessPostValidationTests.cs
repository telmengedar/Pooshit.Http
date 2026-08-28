using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Http.Tests.TestSupport;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceBodylessPostValidationTests {
    const string url = "https://example.test/trigger";

    [TestCase(HttpStatusCode.Unauthorized), TestCase(HttpStatusCode.NotFound), TestCase(HttpStatusCode.InternalServerError), Parallelizable]
    [Description("DiVoid #8317: the body-less result-less POST returned successfully whatever the server answered, so a failing trigger was indistinguishable from a succeeding one")]
    public void Post_NoBody_ErrorStatus_ThrowsHttpServiceException(HttpStatusCode status) {
        ProbeContent content = new("failure text"u8.ToArray());
        using HttpResponseMessage response = new(status) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(() => service.Post(url));

        Assert.That(exception!.Response.StatusCode, Is.EqualTo(status));
        Assert.That(exception.Body, Is.EqualTo("failure text"));
        Assert.That(exception.Message, Does.Contain(url));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8317: validation precedes disposal as in the sibling void overloads, so a caught exception still carries a readable response")]
    public void Post_NoBody_ErrorStatus_LeavesResponseUndisposed() {
        ProbeContent content = new("failure text"u8.ToArray());
        using HttpResponseMessage response = new(HttpStatusCode.BadRequest) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(() => service.Post(url));

        Assert.That(exception!.Response, Is.SameAs(response));
        Assert.That(content.Disposed, Is.False);
    }

    [TestCase(HttpStatusCode.OK), TestCase(HttpStatusCode.Accepted), TestCase(HttpStatusCode.NoContent), Parallelizable]
    [Description("DiVoid #8317: the dual of the throw - a status the check accepts must still return normally and must still dispose")]
    public async Task Post_NoBody_SuccessStatus_ReturnsAndDisposes(HttpStatusCode status) {
        ProbeContent content = new([]);
        using HttpResponseMessage response = new(status) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        await service.Post(url);

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(handler.Requests[0].Method.Method, Is.EqualTo("POST"));
        Assert.That(handler.Requests[0].Content, Is.Null);
        Assert.That(content.Disposed, Is.True);
    }
}

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Http.Tests.TestSupport;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceExplicitMediaTypeRefusalTests {
    const string xmlBody = "<probe><Value>42</Value></probe>";
    const string plainBody = "{\"Value\":\"42\"}";

    static HttpService LoopbackService() => new() { Timeout = TimeSpan.FromSeconds(20) };

    static HttpService CannedService(string mediaType, string body, out ProbeContent content, out HttpResponseMessage response) {
        content = new(Encoding.UTF8.GetBytes(body));
        content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
        response = new(HttpStatusCode.OK) { Content = content };
        return new(new SequenceHandler(response));
    }

    static IEnumerable<TestCaseData> ExplicitBranches() {
        foreach (HttpCompletionOption option in new[] { HttpCompletionOption.ResponseContentRead, HttpCompletionOption.ResponseHeadersRead }) {
            yield return new TestCaseData("application/xml", xmlBody, option).SetName($"ApplicationXml_{option}");
            yield return new TestCaseData("text/xml", xmlBody, option).SetName($"TextXml_{option}");
            yield return new TestCaseData("text/plain", plainBody, option).SetName($"TextPlain_{option}");
        }
    }

    static IEnumerable<TestCaseData> ExplicitBranchBodies() {
        yield return new TestCaseData("application/xml", xmlBody).SetName("ApplicationXml");
        yield return new TestCaseData("text/xml", xmlBody).SetName("TextXml");
        yield return new TestCaseData("text/plain", plainBody).SetName("TextPlain");
    }

    static IEnumerable<TestCaseData> ExplicitBranchProducts() {
        yield return new TestCaseData("application/xml", xmlBody, typeof(XDocument)).SetName("ApplicationXml_ProducesXDocument");
        yield return new TestCaseData("text/xml", xmlBody, typeof(XDocument)).SetName("TextXml_ProducesXDocument");
        yield return new TestCaseData("text/plain", plainBody, typeof(string)).SetName("TextPlain_ProducesString");
    }

    [TestCaseSource(nameof(ExplicitBranches)), Parallelizable]
    public void Get_DomainTypeUnderExplicitMediaType_ThrowsDescriptiveException(string mediaType, string body, HttpCompletionOption option) {
        using LoopbackServer server = new(mediaType, Encoding.UTF8.GetBytes(body));
        HttpService service = LoopbackService();
        string url = server.Url;

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<ProbeDto>(url, new HttpOptions { CompletionOption = option }));

        Assert.That(exception!.Message, Does.Contain(url));
        Assert.That(exception.Message, Does.Contain(mediaType));
        Assert.That(exception.Message, Does.Contain(nameof(ProbeDto)));
        Assert.That(exception.Body, Is.EqualTo(body));
    }

    [TestCaseSource(nameof(ExplicitBranches)), Parallelizable]
    [Description("DiVoid #9664 item 2: the refusal must arise at all before its shape means anything, so the throw is asserted before the cast is ruled out of its chain")]
    public async Task Get_DomainTypeUnderExplicitMediaType_NeverSurfacesInvalidCastException(string mediaType, string body, HttpCompletionOption option) {
        using LoopbackServer server = new(mediaType, Encoding.UTF8.GetBytes(body));

        Exception? caught = null;
        try {
            await LoopbackService().Get<ProbeDto>(server.Url, new HttpOptions { CompletionOption = option });
        }
        catch (Exception exception) {
            caught = exception;
        }

        Assert.That(caught, Is.TypeOf<HttpServiceException>());
        for (Exception? walk = caught; walk != null; walk = walk.InnerException)
            Assert.That(walk, Is.Not.InstanceOf<InvalidCastException>());
    }

    [TestCaseSource(nameof(ExplicitBranches)), Parallelizable]
    [Description("the branch fires only on a declared media type, so the absent-media-type marker of the shared context is unreachable from here and must not be reported")]
    public void Get_DomainTypeUnderExplicitMediaType_ReportsDeclaredMediaTypeNeverAbsentMarker(string mediaType, string body, HttpCompletionOption option) {
        using LoopbackServer server = new(mediaType, Encoding.UTF8.GetBytes(body));
        HttpService service = LoopbackService();
        string url = server.Url;

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<ProbeDto>(url, new HttpOptions { CompletionOption = option }));

        Assert.That(exception!.Message, Does.Contain($"media type '{mediaType}'"));
        Assert.That(exception.Message, Does.Not.Contain("<none>"));
    }

    [TestCase("application/xml", HttpCompletionOption.ResponseContentRead), TestCase("application/xml", HttpCompletionOption.ResponseHeadersRead)]
    [TestCase("text/xml", HttpCompletionOption.ResponseContentRead), TestCase("text/xml", HttpCompletionOption.ResponseHeadersRead)]
    [Parallelizable]
    public async Task Get_XDocumentUnderXmlMediaType_StillLoadsDocument(string mediaType, HttpCompletionOption option) {
        using LoopbackServer server = new(mediaType, Encoding.UTF8.GetBytes(xmlBody));

        XDocument? result = await LoopbackService().Get<XDocument>(server.Url, new HttpOptions { CompletionOption = option });

        Assert.That(result!.Root!.Name.LocalName, Is.EqualTo("probe"));
    }

    [TestCaseSource(nameof(ExplicitBranchProducts)), Parallelizable]
    [Description("object was measured succeeding on both branches before the guard, and a guard reading the assignment in the wrong direction would refuse it")]
    public async Task Get_ObjectUnderExplicitMediaType_StillYieldsTheProducedType(string mediaType, string body, Type produced) {
        using LoopbackServer server = new(mediaType, Encoding.UTF8.GetBytes(body));

        object? result = await LoopbackService().Get<object>(server.Url);

        Assert.That(result, Is.TypeOf(produced));
    }

    [TestCase("application/xml"), TestCase("text/xml"), Parallelizable]
    public async Task Get_XmlBaseTypeUnderXmlMediaType_StillLoadsDocument(string mediaType) {
        using LoopbackServer server = new(mediaType, Encoding.UTF8.GetBytes(xmlBody));

        XNode? result = await LoopbackService().Get<XNode>(server.Url);

        Assert.That(result, Is.TypeOf<XDocument>());
    }

    [Test, Parallelizable]
    public async Task Get_InterfaceImplementedByStringUnderPlainText_StillYieldsBody() {
        using LoopbackServer server = new("text/plain", Encoding.UTF8.GetBytes(plainBody));

        IConvertible? result = await LoopbackService().Get<IConvertible>(server.Url);

        Assert.That(result, Is.EqualTo(plainBody));
    }

    [TestCaseSource(nameof(ExplicitBranches)), Parallelizable]
    public void Get_ValueTypeUnderExplicitMediaType_ThrowsDescriptiveException(string mediaType, string body, HttpCompletionOption option) {
        using LoopbackServer server = new(mediaType, Encoding.UTF8.GetBytes(body));
        HttpService service = LoopbackService();
        string url = server.Url;

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<int>(url, new HttpOptions { CompletionOption = option }));

        Assert.That(exception!.Message, Does.Contain(nameof(Int32)));
        Assert.That(exception.Body, Is.EqualTo(body));
    }

    [TestCaseSource(nameof(ExplicitBranchBodies)), Parallelizable]
    [Description("the throw path leaves the response alive so a caller can inspect it, matching the error-status convention")]
    public void Get_ExplicitMediaTypeRefused_DoesNotDisposeResponse(string mediaType, string body) {
        HttpService service = CannedService(mediaType, body, out ProbeContent content, out HttpResponseMessage response);

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<ProbeDto>("https://example.test/probe"));

        Assert.That(content.Disposed, Is.False);
        Assert.That(exception!.Response, Is.SameAs(response));
        response.Dispose();
    }

    [TestCaseSource(nameof(ExplicitBranchProducts)), Parallelizable]
    public async Task Get_ExplicitMediaTypeAccepted_DisposesResponse(string mediaType, string body, Type produced) {
        HttpService service = CannedService(mediaType, body, out ProbeContent content, out HttpResponseMessage response);

        object? result = await service.Get<object>("https://example.test/probe");

        Assert.That(result, Is.TypeOf(produced));
        Assert.That(content.Disposed, Is.True);
        response.Dispose();
    }

    [TestCaseSource(nameof(ExplicitBranchBodies)), Parallelizable]
    public void Get_ExplicitMediaTypeRefused_CredentialQueryParameterRedacted(string mediaType, string body) {
        using LoopbackServer server = new(mediaType, Encoding.UTF8.GetBytes(body));
        HttpService service = LoopbackService();
        string url = $"{server.Url}?access_token=topsecretvalue";

        HttpServiceException? exception = Assert.ThrowsAsync<HttpServiceException>(() => service.Get<ProbeDto>(url));

        Assert.That(exception!.Message, Does.Contain("access_token"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }
}

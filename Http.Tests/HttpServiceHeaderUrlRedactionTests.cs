using System.Net;
using System.Net.Http;
using Http.Tests.TestSupport;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceHeaderUrlRedactionTests {

    static HttpResponseMessage ErrorResponse() =>
        new(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"code\":\"denied\"}") };

    static HttpResponseMessage ErrorResponse(string headerName, params string[] values) {
        HttpResponseMessage response = ErrorResponse();
        foreach (string value in values)
            response.Headers.TryAddWithoutValidation(headerName, value);
        return response;
    }

    static HttpServiceException Capture(HttpService service, HttpOptions? options = null) =>
        Assert.ThrowsAsync<HttpServiceException>(() => service.Get<string>("https://example.test/probe", options))!;

    static HttpServiceException CaptureResponseHeader(string headerName, params string[] values) =>
        Capture(new HttpService(new SequenceHandler(ErrorResponse(headerName, values))));

    static HttpServiceException CaptureRequestHeader(string headerName, string value) =>
        Capture(new HttpService(new SequenceHandler(ErrorResponse())),
                new() { Headers = [new HttpHeader { Key = headerName, Value = value }] });

    [Test, Parallelizable]
    public void LocationHeader_CredentialQueryParameter_KeepsUrlDropsValue() {
        HttpServiceException exception = CaptureResponseHeader("Location", "https://cdn.test/object?access_token=topsecretvalue");

        Assert.That(exception.Message, Does.Contain("Location: https://cdn.test/object?access_token=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: the request header block reaches the dump through its own loop, so the two collections are pinned separately")]
    public void RefererHeader_CredentialQueryParameter_KeepsUrlDropsValue() {
        HttpServiceException exception = CaptureRequestHeader("Referer", "https://portal.test/page?sig=topsecretvalue");

        Assert.That(exception.Message, Does.Contain("Referer: https://portal.test/page?sig=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: an implementation redacting every query it can find in any header value passes every credential case here and fails only on a header outside the set")]
    public void HeaderOutsideTheUrlValuedSet_CarryingAUrlWithACredential_DumpedVerbatim() {
        HttpServiceException exception = CaptureResponseHeader("X-Callback-Url", "https://vendor.test/hook?access_token=visiblevalue");

        Assert.That(exception.Message, Does.Contain("X-Callback-Url: https://vendor.test/hook?access_token=visiblevalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: redacting the whole url hides the target too, so the parts of it that are not the credential are pinned")]
    public void LocationHeader_BenignQueryParameter_DumpedVerbatim() {
        HttpServiceException exception = CaptureResponseHeader("Location", "https://cdn.test/object?page=17&access_token=topsecretvalue");

        Assert.That(exception.Message, Does.Contain("Location: https://cdn.test/object?page=17&access_token=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: a header can carry several values and joining them before redacting reads the whole join as one url, which leaves a credential behind the first value's query legible")]
    public void MultiValuedLocationHeader_CredentialInTheSecondValue_StillRedacted() {
        HttpServiceException exception = CaptureResponseHeader("Location",
                                                              "https://first.test/object?page=17",
                                                              "https://second.test/object?access_token=topsecretvalue");

        Assert.That(exception.Message, Does.Contain("access_token=<redacted>"));
        Assert.That(exception.Message, Does.Contain("page=17"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: Full is the explicit show me everything hatch and already un-redacts credential headers, so redacting inside it would make the hatch lie")]
    public void FullMode_LocationHeader_DumpedVerbatim() {
        HttpService service = new(new SequenceHandler(ErrorResponse("Location", "https://cdn.test/object?access_token=topsecretvalue")));

        HttpServiceException exception = Capture(service, new() { HeaderDumpMode = HeaderDumpMode.Full });

        Assert.That(exception.Message, Does.Contain("Location: https://cdn.test/object?access_token=topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: suppression drops the header block whole, so the url inside it goes with the block rather than surviving redacted")]
    public void OmittedMode_LocationHeader_NotDumpedAtAll() {
        HttpService service = new(new SequenceHandler(ErrorResponse("Location", "https://cdn.test/object?access_token=topsecretvalue")));

        HttpServiceException exception = Capture(service, new() { HeaderDumpMode = HeaderDumpMode.Omitted });

        Assert.That(exception.Response.Headers.Contains("Location"), Is.True);
        Assert.That(exception.Message, Does.Not.Contain("Location"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: a name reaching both sets must lose its whole value, because the credential set is the stronger statement and query redaction would put the value back")]
    public void NameInBothSets_WholeValueRedactedRatherThanItsQuery() {
        HttpService service = new(new SequenceHandler(ErrorResponse("Location", "https://cdn.test/object?access_token=topsecretvalue")));
        service.SensitiveHeaders.Add("Location");

        HttpServiceException exception = Capture(service);

        Assert.That(exception.Message, Does.Contain("Location: <redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("cdn.test"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: the query word set drives header rendering too, so an implementation carrying its own word list for headers fails here")]
    public void ServiceQuerySet_AddedWord_RedactsInsideAUrlValuedHeader() {
        HttpService service = new(new SequenceHandler(ErrorResponse("Location", "https://cdn.test/object?vendornonce=topsecretvalue")));
        service.SensitiveQueryParameters.Add("vendornonce");

        HttpServiceException exception = Capture(service);

        Assert.That(exception.Message, Does.Contain("vendornonce=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: the set is the only source of sensitive words on this path as well, so an implementation with a hard coded fallback list fails here")]
    public void ServiceQuerySet_Cleared_NothingRedactedInsideAUrlValuedHeader() {
        HttpService service = new(new SequenceHandler(ErrorResponse("Location", "https://cdn.test/object?access_token=stillvisible")));
        service.SensitiveQueryParameters.Clear();

        HttpServiceException exception = Capture(service);

        Assert.That(exception.Message, Does.Contain("Location: https://cdn.test/object?access_token=stillvisible"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: the redactor was written against an absolute request url and a relative Location never reaches it that way, so the rewrite is pinned to work off any prefix")]
    public void RelativeLocationHeader_CredentialQueryParameter_StillRedacted() {
        HttpServiceException exception = CaptureResponseHeader("Location", "/object/4711?access_token=topsecretvalue");

        Assert.That(exception.Message, Does.Contain("Location: /object/4711?access_token=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    public void LocationHeader_WithoutAQuery_DumpedVerbatim() {
        HttpServiceException exception = CaptureResponseHeader("Location", "https://cdn.test/object");

        Assert.That(exception.Message, Does.Contain("Location: https://cdn.test/object"));
    }

    [TestCase("plain text carrying no url at all", "plain%20text%20carrying%20no%20url%20at%20all")]
    [TestCase("plain text? carrying a stray question mark", "plain%20text?%20carrying%20a%20stray%20question%20mark")]
    [TestCase("?", "?"), TestCase("?=", "?="), Parallelizable]
    [Description("DiVoid #9940: a known header parses its value as a uri and hands the dump an escaped rendering rather than the raw text, so what is pinned here is that the redactor adds nothing on top of that and does not fault on a value carrying no query")]
    public void LocationHeader_ValueThatIsNotAUrl_SurvivesTheRedactorUnaltered(string value, string rendered) {
        HttpServiceException exception = CaptureResponseHeader("Location", value);

        Assert.That(exception.Message, Does.Contain($"Location: {rendered}"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: the redactor reads a query span out of whatever it is given, so a value that merely looks like a url is over redacted rather than left alone, which is the accepted cost of not parsing")]
    public void LocationHeader_NonUrlValueShapedLikeAQuery_OverRedacted() {
        HttpServiceException exception = CaptureResponseHeader("Location", "not a url? key=topsecretvalue");

        Assert.That(exception.Message, Does.Contain("Location: not%20a%20url?%20key=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: an empty value carries no query and must render as an empty line rather than fault on the missing question mark")]
    public void LocationHeader_EmptyValue_DumpedAsAnEmptyValue() {
        HttpServiceException exception = CaptureResponseHeader("Location", "");

        Assert.That(exception.Message, Does.Contain("Location: \r\n").Or.Contain("Location: \n"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: Content-Location is a content header, so it is absent from both collections the dump walks and an entry for it in the url valued set could never fire; if this fails the dump has grown to content headers and the set has to grow with it")]
    public void ContentLocationHeader_CarryingACredential_NotDumpedBecauseContentHeadersAreNotWalked() {
        HttpResponseMessage response = ErrorResponse();
        response.Content.Headers.TryAddWithoutValidation("Content-Location", "https://cdn.test/object?access_token=topsecretvalue");
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException exception = Capture(service);

        Assert.That(exception.Response.Content.Headers.Contains("Content-Location"), Is.True);
        Assert.That(exception.Message, Does.Not.Contain("Content-Location"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: this is why Link is excluded rather than added to the set - the redactor reads one query span from the first question mark on, so in a field holding several urls the second one's credential sits inside the first one's value and is copied out verbatim")]
    public void UrlValuedHeaderCarryingAStructuredMultiUrlValue_CredentialInTheSecondUrlSurvives() {
        HttpServiceException exception = CaptureResponseHeader("Location", "<https://api.test/x?q=a>; rel=\"next\", <https://api.test/y?access_token=leakedvalue>; rel=\"prev\"");

        Assert.That(exception.Message, Does.Contain("access_token=leakedvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9940: Link is deliberately outside the set because its RFC 8288 syntax is not a bare url, so its value is dumped whole; the case above shows what adding it without a parser would cost")]
    public void LinkHeader_CarryingACredential_DumpedVerbatim() {
        HttpServiceException exception = CaptureResponseHeader("Link", "<https://api.test/next?access_token=visiblevalue>; rel=\"next\"");

        Assert.That(exception.Message, Does.Contain("Link: <https://api.test/next?access_token=visiblevalue>; rel=\"next\""));
    }
}

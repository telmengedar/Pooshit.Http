using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Http.Tests.TestSupport;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceQueryRedactionTests {

    static HttpResponseMessage ErrorResponse() =>
        new(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"code\":\"denied\"}") };

    static HttpResponseMessage OkResponse(string mediaType, string body) {
        StringContent content = new(body, Encoding.UTF8, mediaType);
        return new(HttpStatusCode.OK) { Content = content };
    }

    static HttpOptions RejectingOptions() => new() { Decoder = new ThrowingDecoder() };

    static HttpServiceException Capture(HttpService service, string url) =>
        Assert.ThrowsAsync<HttpServiceException>(() => service.Get<string>(url))!;

    static HttpServiceException CaptureFailingCall(string url) =>
        Capture(new HttpService(new SequenceHandler(ErrorResponse())), url);

    [Test, Parallelizable]
    public void CredentialParameter_KeepsNameDropsValue() {
        HttpServiceException exception = CaptureFailingCall("https://example.test/probe?access_token=topsecretvalue");

        Assert.That(exception.Message, Does.Contain("access_token"));
        Assert.That(exception.Message, Does.Contain("<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: an implementation that redacts every query value passes every token-is-gone assertion, so the non-sensitive direction is pinned too")]
    public void BenignParameterAlongsideCredential_KeptVerbatim() {
        HttpServiceException exception = CaptureFailingCall("https://example.test/probe?access_token=topsecretvalue&page=17");

        Assert.That(exception.Message, Does.Contain("page=17"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: dropping the query wholesale hides the credential too, so what survives around it is pinned")]
    public void CredentialParameter_SchemeHostPathAndStatusSurvive() {
        HttpServiceException exception = CaptureFailingCall("https://example.test/probe?access_token=topsecretvalue");

        Assert.That(exception.Message, Does.Contain("https://example.test/probe?"));
        Assert.That(exception.Message, Does.Contain("Unauthorized"));
    }

    [TestCase("access_token"), TestCase("X-Amz-Signature"), TestCase("X-Amz-Security-Token")]
    [TestCase("X-Amz-Credential"), TestCase("AWSAccessKeyId"), TestCase("apiKey")]
    [TestCase("key"), TestCase("sig"), TestCase("client_secret"), TestCase("password"), TestCase("x_auth")]
    [Parallelizable]
    public void CredentialWordInParameterName_ValueRedacted(string parameterName) {
        HttpServiceException exception = CaptureFailingCall($"https://example.test/probe?{parameterName}=topsecretvalue");

        Assert.That(exception.Message, Does.Contain($"{parameterName}=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [TestCase("token_id"), TestCase("access_token_v2"), TestCase("x_key_1"), TestCase("v4_signature")]
    [TestCase("access_token"), Parallelizable]
    [Description("DiVoid #9938: the word sits leading, medial and trailing across these names, so a rule anchored at either end of the name fails here")]
    public void CredentialWordAtAnyPositionInName_ValueRedacted(string parameterName) {
        HttpServiceException exception = CaptureFailingCall($"https://example.test/probe?{parameterName}=topsecretvalue");

        Assert.That(exception.Message, Does.Contain($"{parameterName}=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [TestCase("accesskey"), TestCase("authtoken"), TestCase("secretkey"), TestCase("apitoken")]
    [TestCase("bearertoken"), TestCase("privatekey"), TestCase("sessionkey"), TestCase("apikey")]
    [TestCase("accesstoken"), Parallelizable]
    [Description("DiVoid #9938: these carry no separator and no camel case boundary, so a rule that splits the name into words leaves every one of them legible")]
    public void ConcatenatedCredentialCompound_ValueRedacted(string parameterName) {
        HttpServiceException exception = CaptureFailingCall($"https://example.test/probe?{parameterName}=topsecretvalue");

        Assert.That(exception.Message, Does.Contain($"{parameterName}=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [TestCase("key2"), TestCase("apikey2"), TestCase("sig1"), TestCase("signature_v4"), Parallelizable]
    [Description("DiVoid #9938: a digit beside the word neither creates nor removes a match, which a rule treating digits as part of a word boundary gets wrong")]
    public void CredentialWordBesideDigits_ValueRedacted(string parameterName) {
        HttpServiceException exception = CaptureFailingCall($"https://example.test/probe?{parameterName}=topsecretvalue");

        Assert.That(exception.Message, Does.Contain($"{parameterName}=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [TestCase("sortkey"), TestCase("keyword"), TestCase("monkey"), TestCase("author")]
    [TestCase("assignee"), TestCase("design"), Parallelizable]
    [Description("DiVoid #9938: over redacting a benign word that carries a credential word is the accepted cost of the rule, so restoring a word boundary fails here instead of passing quietly")]
    public void BenignWordCarryingACredentialWord_ValueRedacted(string parameterName) {
        HttpServiceException exception = CaptureFailingCall($"https://example.test/probe?{parameterName}=visiblevalue");

        Assert.That(exception.Message, Does.Contain($"{parameterName}=<redacted>"));
    }

    [TestCase("page"), TestCase("limit"), TestCase("offset"), TestCase("filter")]
    [TestCase("id"), TestCase("format"), Parallelizable]
    [Description("DiVoid #9938: an implementation that redacts every query value passes every credential case in this fixture and fails only here")]
    public void ParameterWithNoCredentialWord_ValueKeptVerbatim(string parameterName) {
        HttpServiceException exception = CaptureFailingCall($"https://example.test/probe?{parameterName}=visiblevalue");

        Assert.That(exception.Message, Does.Contain($"{parameterName}=visiblevalue"));
    }

    [TestCase(""), TestCase("--"), TestCase("_"), Parallelizable]
    [Description("DiVoid #9938: a name carrying no letters matches nothing and keeps its value, rather than faulting on an empty or punctuation only name")]
    public void NameWithoutLetters_ValueKeptVerbatim(string parameterName) {
        HttpServiceException exception = CaptureFailingCall($"https://example.test/probe?{parameterName}=visiblevalue");

        Assert.That(exception.Message, Does.Contain($"?{parameterName}=visiblevalue"));
    }

    [TestCase("access%5Ftoken", "access_token"), TestCase("api%2Dkey", "api-key"), Parallelizable]
    [Description("DiVoid #9938: the rendered url decodes an unreserved escape before the redactor reads it, so an encoded separator reaches the rule as its plain character and cannot hide the credential word")]
    public void PercentEncodedSeparatorInName_DecodedAndRedacted(string parameterName, string renderedName) {
        HttpServiceException exception = CaptureFailingCall($"https://example.test/probe?{parameterName}=topsecretvalue");

        Assert.That(exception.Message, Does.Contain($"{renderedName}=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: only the name is matched, so a benign parameter whose value happens to carry a credential word keeps that value")]
    public void CredentialWordInValueOnly_ValueKeptVerbatim() {
        HttpServiceException exception = CaptureFailingCall("https://example.test/probe?page=tokenholder&filter=secretsauce");

        Assert.That(exception.Message, Does.Contain("?page=tokenholder&filter=secretsauce"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: the query starts at the first question mark, so a later one inside a value does not move the span and leave the credential ahead of it verbatim")]
    public void QuestionMarkInsideAValue_QueryStillStartsAtTheFirstOne() {
        HttpServiceException exception = CaptureFailingCall("https://example.test/probe?sig=topsecretvalue&q=a?b");

        Assert.That(exception.Message, Does.Contain("?sig=<redacted>&q=a?b"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    public void CredentialParameterInDifferentCasing_ValueRedacted() {
        HttpServiceException exception = CaptureFailingCall("https://example.test/probe?ACCESS_TOKEN=topsecretvalue");

        Assert.That(exception.Message, Does.Contain("ACCESS_TOKEN=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    public void ServiceSet_AddedWord_ValueRedacted() {
        HttpService service = new(new SequenceHandler(ErrorResponse()));
        service.SensitiveQueryParameters.Add("vendorsecret");

        HttpServiceException exception = Capture(service, "https://example.test/probe?vendorsecret=topsecretvalue");

        Assert.That(exception.Message, Does.Contain("vendorsecret=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: the entry is upper case while the name is lower case, so a comparison that only folds the name away fails here")]
    public void ServiceSet_AddedWordInDifferentCasing_ValueRedacted() {
        HttpService service = new(new SequenceHandler(ErrorResponse()));
        service.SensitiveQueryParameters.Add("VENDORSECRET");

        HttpServiceException exception = Capture(service, "https://example.test/probe?vendorsecret=topsecretvalue");

        Assert.That(exception.Message, Does.Contain("vendorsecret=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    public void ServiceSet_RemovedWord_ValueKeptVerbatim() {
        HttpService service = new(new SequenceHandler(ErrorResponse()));
        service.SensitiveQueryParameters.Remove("key");

        HttpServiceException exception = Capture(service, "https://example.test/probe?key=nolongerredacted");

        Assert.That(exception.Message, Does.Contain("key=nolongerredacted"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: a consumer reaches a shipped entry through any casing, which pins the set's own comparer rather than the name comparison")]
    public void ServiceSet_RemovedWordInDifferentCasing_ValueKeptVerbatim() {
        HttpService service = new(new SequenceHandler(ErrorResponse()));
        service.SensitiveQueryParameters.Remove("KEY");

        HttpServiceException exception = Capture(service, "https://example.test/probe?key=nolongerredacted");

        Assert.That(exception.Message, Does.Contain("key=nolongerredacted"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: signature is redundant while sig is present and ships anyway so that removing sig, the entry a consumer is most likely to drop, does not silently take signature coverage with it")]
    public void ServiceSet_RemovedSig_SignatureCoverageSurvives() {
        HttpService service = new(new SequenceHandler(ErrorResponse()));
        service.SensitiveQueryParameters.Remove("sig");

        HttpServiceException exception = Capture(service, "https://example.test/probe?X-Amz-Signature=topsecretvalue&design=visiblevalue");

        Assert.That(exception.Message, Does.Contain("X-Amz-Signature=<redacted>"));
        Assert.That(exception.Message, Does.Contain("design=visiblevalue"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: the set is the only source of sensitive words, so an implementation carrying a hard coded fallback list fails here")]
    public void ServiceSet_Cleared_NothingRedacted() {
        HttpService service = new(new SequenceHandler(ErrorResponse()));
        service.SensitiveQueryParameters.Clear();

        HttpServiceException exception = Capture(service, "https://example.test/probe?access_token=stillvisible");

        Assert.That(exception.Message, Does.Contain("access_token=stillvisible"));
        Assert.That(exception.Message, Does.Not.Contain("<redacted>"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: SensitiveHeaders also governs the cross-origin redirect strip, so a later edit merging the two sets must fail here")]
    public void HeaderOnlyName_UsedAsQueryParameter_ValueKeptVerbatim() {
        HttpService service = new(new SequenceHandler(ErrorResponse()));
        service.SensitiveHeaders.Add("X-Tenant-Marker");

        HttpServiceException exception = Capture(service, "https://example.test/probe?X-Tenant-Marker=visiblevalue");

        Assert.That(exception.Message, Does.Contain("X-Tenant-Marker=visiblevalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: the query set must not reach header rendering, which is the direction that would change what travels to a redirect target")]
    public void QueryOnlyWord_UsedAsHeaderName_DumpedVerbatim() {
        HttpService service = new(new SequenceHandler(ErrorResponse()));
        service.SensitiveQueryParameters.Add("tenantmarker");
        HttpOptions options = new() { Headers = [new HttpHeader { Key = "tenantmarker", Value = "visiblevalue" }] };

        HttpServiceException exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<string>("https://example.test/probe", options))!;

        Assert.That(exception.Message, Does.Contain("tenantmarker: visiblevalue"));
    }

    [Test, Parallelizable]
    public void ParameterWithoutValue_EmittedUnchanged() {
        HttpServiceException exception = CaptureFailingCall("https://example.test/probe?flag&access_token=topsecretvalue");

        Assert.That(exception.Message, Does.Contain("?flag&access_token=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: a name ends at the first equals sign, so a value carrying its own equals signs is replaced whole and a benign one keeps them")]
    public void ValueCarryingEqualsSigns_ReplacedWholeWhileBenignOneKeepsThem() {
        HttpServiceException exception = CaptureFailingCall("https://example.test/probe?sig=ab==topsecretvalue&filter=a=b");

        Assert.That(exception.Message, Does.Contain("sig=<redacted>"));
        Assert.That(exception.Message, Does.Contain("filter=a=b"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    public void CredentialParameterWithEmptyValue_StillRedacted() {
        HttpServiceException exception = CaptureFailingCall("https://example.test/probe?access_token=&x=1");

        Assert.That(exception.Message, Does.Contain("?access_token=<redacted>&x=1"));
    }

    [Test, Parallelizable]
    public void FragmentAfterQuery_SurvivesVerbatim() {
        HttpServiceException exception = CaptureFailingCall("https://example.test/probe?access_token=topsecretvalue#section-4");

        Assert.That(exception.Message, Does.Contain("?access_token=<redacted>#section-4"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    public void SeveralCredentialParameters_AllRedactedWithOrderAndSeparatorsPreserved() {
        HttpServiceException exception = CaptureFailingCall("https://example.test/probe?api_key=topsecretkey&page=17&sig=topsecretsig");

        Assert.That(exception.Message, Does.Contain("?api_key=<redacted>&page=17&sig=<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretkey"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretsig"));
    }

    [Test, Parallelizable]
    public void UrlWithoutQuery_RenderedUnchanged() {
        HttpServiceException exception = CaptureFailingCall("https://example.test/probe");

        Assert.That(exception.Message, Does.Contain("'https://example.test/probe' -> status"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: a response the handler never stamped has no request uri, and the url renders as nothing rather than as a sentinel")]
    public void ResponseWithoutRequestMessage_RendersEmptyUrl() {
        SequenceHandler handler = new(ErrorResponse()) { StampRequestMessage = false };
        HttpService service = new(handler);

        HttpServiceException exception = Capture(service, "https://example.test/probe");

        Assert.That(exception.Message, Does.StartWith("Error sending request to '' -> status"));
    }

    [Test, Parallelizable]
    public void StatusCheckSite_CredentialParameter_Redacted() {
        HttpServiceException exception = CaptureFailingCall("https://example.test/probe?access_token=topsecretvalue");

        Assert.That(exception.Message, Does.Contain("Error sending request to"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    public void JsonDecodeFailureSite_CredentialParameter_Redacted() {
        HttpService service = new(new SequenceHandler(OkResponse("application/json", "{\"Value\":\"42\"}")));

        HttpServiceException exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<ProbeDto>("https://example.test/probe?access_token=topsecretvalue", RejectingOptions()))!;

        Assert.That(exception.Message, Does.Contain("Error decoding response of"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    public void UnknownMediaTypeSite_CredentialParameter_Redacted() {
        HttpService service = new(new SequenceHandler(OkResponse("text/javascript", "not json at all")));

        HttpServiceException exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<ProbeDto>("https://example.test/probe?access_token=topsecretvalue"))!;

        Assert.That(exception.Message, Does.Contain("Unable to decode response of"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    [Test, Parallelizable]
    public void UnknownMediaTypeDecodeFailureSite_CredentialParameter_Redacted() {
        HttpService service = new(new SequenceHandler(OkResponse("text/javascript", "{\"Value\":\"42\"}")));

        HttpServiceException exception = Assert.ThrowsAsync<HttpServiceException>(
            () => service.Get<ProbeDto>("https://example.test/probe?access_token=topsecretvalue", RejectingOptions()))!;

        Assert.That(exception.Message, Does.Contain("Error decoding response of"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretvalue"));
    }

    static string HttpServiceSourcePath([CallerFilePath] string testFilePath = "") {
        string testDir = Path.GetDirectoryName(testFilePath)!;
        return Path.GetFullPath(Path.Combine(testDir, "..", "Pooshit.Http", "HttpService.cs"));
    }

    static List<string> InterpolationHoles(string source) {
        List<string> holes = [];
        foreach (Match literal in Regex.Matches(source, "(?:\\$@|@\\$|\\$)\"(?:\\\\.|\"\"|[^\"])*\""))
            foreach (Match hole in Regex.Matches(literal.Value, @"\{[^{}]*\}"))
                holes.Add(hole.Value);
        return holes;
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: a message site added later is invisible to a hand written fan, so the absence of a raw request uri is asserted against the source itself")]
    public void EveryUrlInAMessageGoesThroughTheRedactor() {
        List<string> holes = InterpolationHoles(File.ReadAllText(HttpServiceSourcePath()));

        Assert.That(holes.Where(hole => hole.Contains("RequestUri")), Is.Empty);
        Assert.That(holes.Count(hole => hole.Contains("DumpUrl(response)")), Is.GreaterThanOrEqualTo(3));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9938: the scanner above cannot read a raw string literal, so its arrival is failed here rather than silently narrowing what the previous test sees")]
    public void SourceCarriesNoLiteralShapeTheScannerCannotRead() {
        string source = File.ReadAllText(HttpServiceSourcePath());

        Assert.That(Regex.Matches(source, "\\$+\"\"\""), Is.Empty);
    }
}

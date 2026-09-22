using System;
using Pooshit.Http.Paths;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class RestPathEncodingTests {
    const string baseUrl = "http://host/api";

    class RenderedSegment(string rendered) {
        public override string ToString() => rendered;
    }

    [TestCase("/", "%2F"), TestCase("?", "%3F"), TestCase("#", "%23"), TestCase("&", "%26"), TestCase("=", "%3D")]
    [TestCase("\\", "%5C"), TestCase(" ", "%20"), TestCase("ä", "%C3%A4"), Parallelizable]
    public void Path_SegmentCarriesStructuralCharacter_EscapesIt(string character, string escaped) {
        Assert.That(Rest.Path(baseUrl, $"a{character}b"), Is.EqualTo($"{baseUrl}/a{escaped}b"));
    }

    [TestCase("/"), TestCase("?"), TestCase("#"), TestCase("\\"), TestCase(" "), Parallelizable]
    [Description("DiVoid #8320: a string assertion alone passes while the built url is still restructured, so the outcome is read off the Uri")]
    public void Path_SegmentCarriesStructuralCharacter_DoesNotRestructureUrl(string character) {
        Uri url = new(Rest.Path(baseUrl, $"a{character}b", "tail"));

        Assert.That(url.AbsolutePath.Split('/'), Has.Length.EqualTo(4));
        Assert.That(url.AbsolutePath.Split('/')[3], Is.EqualTo("tail"));
        Assert.That(url.Query, Is.Empty);
        Assert.That(url.Fragment, Is.Empty);
    }

    [Test, Parallelizable]
    [Description("DiVoid #8320: the premise of the restructuring guard - the same shape with a benign segment yields the same four parts")]
    public void Path_SegmentCarriesNoStructuralCharacter_KeepsFourParts() {
        Uri url = new(Rest.Path(baseUrl, "aXb", "tail"));

        Assert.That(url.AbsolutePath.Split('/'), Has.Length.EqualTo(4));
        Assert.That(url.AbsolutePath.Split('/')[3], Is.EqualTo("tail"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8320: the base carries scheme and authority and a caller's own dot segments, so it is never escaped nor guarded")]
    public void Path_BaseUrl_IsEmittedVerbatim() {
        Assert.That(Rest.Path("https://host:8080/api/../v2", "x"), Is.EqualTo("https://host:8080/api/../v2/x"));
    }

    [Test, Parallelizable]
    public void Path_MultipleSegments_EmitsOneSeparatorPerSegment() {
        Assert.That(Rest.Path(baseUrl, "a", 7, "c"), Is.EqualTo($"{baseUrl}/a/7/c"));
    }

    [Test, Parallelizable]
    public void Path_NoSegments_ReturnsBaseUrl() {
        Assert.That(Rest.Path(baseUrl), Is.EqualTo(baseUrl));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8320: %2E%2E traverses exactly as .. does before escaping and has to stay inert after it")]
    public void Path_SegmentCarriesPreEncodedDotSegment_IsInert() {
        string result = Rest.Path(baseUrl, "%2E%2E", "admin");

        Assert.That(result, Is.EqualTo($"{baseUrl}/%252E%252E/admin"));
        Assert.That(new Uri(result).AbsolutePath, Is.EqualTo("/api/%252E%252E/admin"));
    }

    [Test, Parallelizable]
    public void Path_NullBaseUrl_ThrowsNamingBaseUrl() {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => Rest.Path(null!, "a"))!;

        Assert.That(exception.ParamName, Is.EqualTo("baseUrl"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8320: a null renders empty today and doubles the separator, which addresses a resource the caller did not name")]
    public void Path_NullSegment_ThrowsNamingItsIndex() {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => Rest.Path(baseUrl, "a", null!, "c"))!;

        Assert.That(exception.ParamName, Is.EqualTo("segments"));
        Assert.That(exception.Message, Does.Contain("index 1"));
    }

    [Test, Parallelizable]
    public void Path_NullSegmentArray_ThrowsNamingSegments() {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => Rest.Path(baseUrl, (object[])null!))!;

        Assert.That(exception.ParamName, Is.EqualTo("segments"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8320: an empty segment is the trailing-slash idiom a caller wrote, unlike a null, which is what an unset value looks like")]
    public void Path_EmptySegment_YieldsTrailingSlash() {
        Assert.That(Rest.Path(baseUrl, "users", ""), Is.EqualTo($"{baseUrl}/users/"));
    }

    [TestCase("."), TestCase(".."), Parallelizable]
    [Description("DiVoid #8320: escaping does not neutralise a dot segment - Uri removes it before the request line is built, and percent-encoding the dots does not help")]
    public void Path_WholeDotSegment_ThrowsNamingItsIndex(string segment) {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => Rest.Path(baseUrl, "a", segment))!;

        Assert.That(exception.ParamName, Is.EqualTo("segments"));
        Assert.That(exception.Message, Does.Contain("index 1"));
    }

    [TestCase("a..b"), TestCase("..."), TestCase("a."), TestCase("..a"), TestCase(".hidden"), Parallelizable]
    [Description("DiVoid #8320: Uri removes whole dot segments only, so a segment that merely carries dots is measured safe and has to keep passing")]
    public void Path_SegmentCarriesDotsWithoutBeingOne_IsAccepted(string segment) {
        Assert.That(Rest.Path(baseUrl, "a", segment), Is.EqualTo($"{baseUrl}/a/{segment}"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8320: what reaches the url is the rendered form, so the guard reads that rather than the declared type")]
    public void Path_NonStringSegmentRenderingToDotSegment_Throws() {
        Assert.Throws<ArgumentException>(() => Rest.Path(baseUrl, new RenderedSegment("..")));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8320: the premise of the rendered-form guard - the same carrier with a benign rendering passes")]
    public void Path_NonStringSegmentRenderingToValue_IsAccepted() {
        Assert.That(Rest.Path(baseUrl, new RenderedSegment("a/b")), Is.EqualTo($"{baseUrl}/a%2Fb"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8320: the join rule exists once - PathQuery renders its path portion through Path")]
    public void PathQuery_PathPortion_IsIdenticalToPath() {
        string path = Rest.Path(baseUrl, "a/b", "c d");

        Assert.That(Rest.PathQuery("q=1", baseUrl, "a/b", "c d"), Is.EqualTo($"{path}?q=1"));
        Assert.That(Rest.PathQuery("?q=1", baseUrl, "a/b", "c d"), Is.EqualTo($"{path}?q=1"));
        Assert.That(Rest.PathQuery("", baseUrl, "a/b", "c d"), Is.EqualTo(path));
    }

    [Test, Parallelizable]
    public void PathQuery_WholeDotSegment_Throws() {
        Assert.Throws<ArgumentException>(() => Rest.PathQuery("q=1", baseUrl, ".."));
    }

    [Test, Parallelizable]
    public void PathQuery_NullSegment_Throws() {
        Assert.Throws<ArgumentNullException>(() => Rest.PathQuery("q=1", baseUrl, "a", null!));
    }

    [Test, Parallelizable]
    public void PathQuery_QueryParameters_EscapesSegments() {
        QueryParameters parameters = new("q", "1");

        Assert.That(Rest.PathQuery(parameters, baseUrl, "a/b"), Is.EqualTo($"{baseUrl}/a%2Fb?q=1"));
    }
}

using System.Net;
using System.Net.Http;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceExceptionTests {

    [Test, Parallelizable]
    public void BodyCarriesResponseText() {
        using HttpResponseMessage response = new(HttpStatusCode.NotFound);
        HttpServiceException exception = new(response, "error", body: "{\"code\":\"data_entitynotfound\"}");
        Assert.That(exception.Body, Is.EqualTo("{\"code\":\"data_entitynotfound\"}"));
    }

    [Test, Parallelizable]
    public void BodyNullWhenOmitted() {
        using HttpResponseMessage response = new(HttpStatusCode.InternalServerError);
        HttpServiceException exception = new(response, "error");
        Assert.That(exception.Body, Is.Null);
    }
}

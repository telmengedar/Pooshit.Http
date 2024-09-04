using NightlyCode.Http.Paths;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class QueryParametersTests {
    class TestObject {
        public string[] Arguments { get; set; }
    }

    
    [Test, Parallelizable]
    public void NoParameters() {
        QueryParameters parameters = new();
        Assert.That(parameters.ToString(), Is.EqualTo(""));
    }

    [Test, Parallelizable]
    public void ArrayParameters() {
        QueryParameters parameters = new("test", new[] { 1, 2, 3, 4, 5 });
        Assert.That(parameters.ToString(), Is.EqualTo("?test={1,2,3,4,5}"));
    }

    [Test, Parallelizable]
    public void ArrayParametersFromObject() {
        QueryParameters parameters = QueryParameters.FromValue(new TestObject {
                                                                                  Arguments = ["a", "b", "c"]
                                                                              }, false);
        Assert.That(parameters.ToString(), Is.EqualTo("?arguments={a,b,c}"));
    }
}
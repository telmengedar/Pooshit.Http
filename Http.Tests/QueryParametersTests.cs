using System.Linq;
using Pooshit.Http.Paths;

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

    [Test, Parallelizable]
    [Description("DiVoid #8322: the value is typed object, so comparing it with == asked whether two boxes were the same box")]
    public void Contains_BoxedValueType_FindsTheValue() {
        QueryParameters parameters = new();
        parameters.Add("n", 42);

        Assert.That(parameters.Contains("n", 42), Is.True);
    }

    [Test, Parallelizable]
    public void Contains_BoxedValueTypeWithADifferentValue_IsFalse() {
        QueryParameters parameters = new();
        parameters.Add("n", 42);

        Assert.That(parameters.Contains("n", 43), Is.False);
    }

    [Test, Parallelizable]
    public void Contains_NameMatchingButValueNot_IsFalse() {
        QueryParameters parameters = new();
        parameters.Add("n", 42);

        Assert.That(parameters.Contains("other", 42), Is.False);
    }

    [Test, Parallelizable]
    public void Contains_NullValue_FindsTheEntryCarryingNull() {
        QueryParameters parameters = new(new QueryParameter("k", null));

        Assert.That(parameters.Contains("k", null), Is.True);
    }

    [Test, Parallelizable]
    public void Contains_StringValue_StillFindsTheValue() {
        QueryParameters parameters = new();
        parameters.Add("s", "hello");

        Assert.That(parameters.Contains("s", "hello"), Is.True);
    }

    [Test, Parallelizable]
    [Description("DiVoid #8322: assigning twice left both entries, so a read-back returned the value that had been replaced")]
    public void Indexer_AssigningAKeyThatIsPresent_ReplacesIt() {
        QueryParameters parameters = new();
        parameters["k"] = 1;
        parameters["k"] = 2;

        Assert.That(parameters.Parameters.Count(), Is.EqualTo(1));
        Assert.That(parameters["k"], Is.EqualTo(2));
        Assert.That(parameters.ToString(), Is.EqualTo("?k=2"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8322: assigning replaces every entry carrying the name, so the indexer depends on Remove clearing all matches rather than the first")]
    public void Indexer_AssigningAKeyAddedTwice_ReplacesEveryEntry() {
        QueryParameters parameters = new();
        parameters.Add("k", 1);
        parameters.Add("k", 2);
        parameters["k"] = 3;

        Assert.That(parameters.Parameters.Count(), Is.EqualTo(1));
        Assert.That(parameters["k"], Is.EqualTo(3));
        Assert.That(parameters.ToString(), Is.EqualTo("?k=3"));
    }

    [Test, Parallelizable]
    public void Indexer_AssigningAKeyThatIsAbsent_AddsIt() {
        QueryParameters parameters = new();
        parameters["k"] = 1;

        Assert.That(parameters.Parameters.Count(), Is.EqualTo(1));
        Assert.That(parameters.ToString(), Is.EqualTo("?k=1"));
    }

    [Test, Parallelizable]
    public void Indexer_AssigningAKey_LeavesOtherKeysAlone() {
        QueryParameters parameters = new();
        parameters["a"] = 1;
        parameters["b"] = 2;
        parameters["a"] = 3;

        Assert.That(parameters.Parameters.Count(), Is.EqualTo(2));
        Assert.That(parameters.ToString(), Is.EqualTo("?b=2&a=3"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8322: Add drops null and empty, so replace-then-add makes assigning either of them clear the key")]
    public void Indexer_AssigningNullToAKeyThatIsPresent_ClearsIt() {
        QueryParameters parameters = new();
        parameters["k"] = "v";
        parameters["k"] = null;

        Assert.That(parameters.Parameters.Count(), Is.EqualTo(0));
        Assert.That(parameters.ToString(), Is.EqualTo(""));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8322: clearing removes every entry carrying the name, so a key built by repeated Add clears completely rather than leaving the later entry standing")]
    public void Indexer_AssigningNullToAKeyAddedTwice_ClearsEveryEntry() {
        QueryParameters parameters = new();
        parameters.Add("k", 1);
        parameters.Add("k", 2);
        parameters["k"] = null;

        Assert.That(parameters.Parameters.Count(), Is.EqualTo(0));
        Assert.That(parameters.ToString(), Is.EqualTo(""));
    }

    [Test, Parallelizable]
    [Description("DiVoid #8322: Add drops the empty string as it drops null, so assigning it removes every entry carrying the name exactly as null does")]
    public void Indexer_AssigningAnEmptyStringToAKeyAddedTwice_ClearsEveryEntry() {
        QueryParameters parameters = new();
        parameters.Add("k", 1);
        parameters.Add("k", 2);
        parameters["k"] = "";

        Assert.That(parameters.Parameters.Count(), Is.EqualTo(0));
        Assert.That(parameters.ToString(), Is.EqualTo(""));
    }

    [Test, Parallelizable]
    public void Add_CalledTwiceForOneName_StillAppends() {
        QueryParameters parameters = new();
        parameters.Add("k", 1);
        parameters.Add("k", 2);

        Assert.That(parameters.Parameters.Count(), Is.EqualTo(2));
        Assert.That(parameters.ToString(), Is.EqualTo("?k=1&k=2"));
    }
}
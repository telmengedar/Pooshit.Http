namespace Http.Tests.TestSupport;

/// <summary>
/// minimal domain type used to exercise the json decode branch of the typed read
/// </summary>
public class ProbeDto {
    public string Value { get; set; } = "";
}

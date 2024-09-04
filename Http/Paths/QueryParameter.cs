namespace NightlyCode.Http.Paths; 

/// <summary>
/// parameter used in query string
/// </summary>
public struct QueryParameter {

    /// <summary>
    /// creates a new <see cref="QueryParameter"/>
    /// </summary>
    /// <param name="name">name of parameter</param>
    /// <param name="value">value for parameter</param>
    public QueryParameter(string name, object value) {
        Name = name;
        Value = value;
    }

    /// <summary>
    /// name of parameter
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// value for parameter
    /// </summary>
    public object Value { get; }
}
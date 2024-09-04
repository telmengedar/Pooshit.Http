namespace NightlyCode.Http; 

/// <summary>
/// header in a http message
/// </summary>
public class HttpHeader {
        
    /// <summary>
    /// string representing key
    /// </summary>
    public string Key { get; set; }
        
    /// <summary>
    /// value of header
    /// </summary>
    public string Value { get; set; }
}
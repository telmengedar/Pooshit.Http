namespace Pooshit.Http;

/// <summary>
/// determines how http headers are rendered into the message of a <see cref="HttpServiceException"/>
/// </summary>
public enum HeaderDumpMode {

    /// <summary>
    /// headers are dumped with the values of sensitive headers replaced by a placeholder
    /// </summary>
    Redacted,

    /// <summary>
    /// no headers are dumped at all
    /// </summary>
    Omitted,

    /// <summary>
    /// all headers are dumped with their values unchanged
    /// </summary>
    Full
}

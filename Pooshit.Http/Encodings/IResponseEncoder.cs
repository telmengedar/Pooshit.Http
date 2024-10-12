using System.Net.Http;

namespace Pooshit.Http.Encodings; 

/// <summary>
/// encoder for http content
/// </summary>
public interface IResponseEncoder {

    /// <summary>
    /// encodes data to be sent using a http client
    /// </summary>
    /// <param name="data">data to be encoded</param>
    /// <returns>encoded content</returns>
    HttpContent Encode(object data);
}
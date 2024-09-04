using System.Net.Http;
using System.Threading.Tasks;

namespace NightlyCode.Http.Encodings; 

/// <summary>
/// decoder for http responses
/// </summary>
public interface IResponseDecoder {
        
    /// <summary>
    /// decodes the content of a <see cref="HttpResponseMessage"/>
    /// </summary>
    /// <typeparam name="T">type of object to decode</typeparam>
    /// <param name="message">message containing response to decode</param>
    /// <returns>decoded type</returns>
    Task<T> Decode<T>(HttpResponseMessage message);
        
    /// <summary>
    /// decodes the content of a <see cref="HttpResponseMessage"/>
    /// </summary>
    /// <typeparam name="T">type of object to decode</typeparam>
    /// <param name="message">message containing response to decode</param>
    /// <returns>decoded type</returns>
    T DecodeSync<T>(HttpResponseMessage message);
}
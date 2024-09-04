using System;
using System.Net.Http;

namespace NightlyCode.Http; 

/// <summary>
/// error thrown when sending requests using a http client
/// </summary>
public class HttpServiceException : Exception {
    /// <summary>
    /// creates a new <see cref="HttpServiceException"/>
    /// </summary>
    /// <param name="response">response of http call</param>
    /// <param name="message">error message</param>
    /// <param name="innerException">inner exception which lead to this exception (optional)</param>
    public HttpServiceException(HttpResponseMessage response, string message=null, Exception innerException=null)
        : base(message??$"{response.StatusCode}: {response.ReasonPhrase}", innerException) {
        Response = response;
    }

    /// <summary>
    /// error response message
    /// </summary>
    public HttpResponseMessage Response { get; }
}
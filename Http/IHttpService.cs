using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace NightlyCode.Http; 

/// <summary>
/// service for sending web / rest requests
/// </summary>
public interface IHttpService {
        
    /// <summary>
    /// allowed timeout
    /// </summary>
    TimeSpan Timeout { get; set; }
        
    /// <summary>
    /// posts a request to a server
    /// </summary>
    /// <param name="url">url to post request to</param>
    /// <param name="content">content to post</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>response message</returns>
    Task<TResponse> Post<TRequest, TResponse>(string url, TRequest content, HttpOptions options = null);

    /// <summary>
    /// posts a request to a server without body data
    /// </summary>
    /// <param name="url">url to post request to</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>response message</returns>
    Task Post(string url, HttpOptions options = null);

    /// <summary>
    /// posts a request to a server without body data
    /// </summary>
    /// <param name="url">url to post request to</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>response message</returns>
    Task<TResponse> Post<TResponse>(string url, HttpOptions options = null);

    /// <summary>
    /// posts a request to a server
    /// </summary>
    /// <param name="url">url to post request to</param>
    /// <param name="content">content to post</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>response message</returns>
    Task Post<TRequest>(string url, TRequest content, HttpOptions options = null);

    /// <summary>
    /// puts a request to a server
    /// </summary>
    /// <param name="url">url to post request to</param>
    /// <param name="content">content to post</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>response message</returns>
    Task<TResponse> Put<TRequest, TResponse>(string url, TRequest content, HttpOptions options = null);

    /// <summary>
    /// puts a request to a server
    /// </summary>
    /// <param name="url">url to post request to</param>
    /// <param name="content">content to post</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>response message</returns>
    Task Put<TRequest>(string url, TRequest content, HttpOptions options = null);

    /// <summary>
    /// puts a request to a server
    /// </summary>
    /// <param name="url">url to post request to</param>
    /// <param name="content">content to post</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>response message</returns>
    Task<TResponse> Patch<TRequest, TResponse>(string url, TRequest content, HttpOptions options = null);

    /// <summary>
    /// puts a request to a server
    /// </summary>
    /// <param name="url">url to post request to</param>
    /// <param name="content">content to post</param>
    /// <param name="options">options for request (optional)</param>
    Task Patch<TRequest>(string url, TRequest content, HttpOptions options = null);

    /// <summary>
    /// sends a GET request to a server
    /// </summary>
    /// <param name="url">url to get resource from</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>response message</returns>
    Task Get(string url, HttpOptions options = null);

    /// <summary>
    /// sends a GET request to a server
    /// </summary>
    /// <param name="url">url to get resource from</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>response message</returns>
    Task<T> Get<T>(string url, HttpOptions options = null);

    /// <summary>
    /// sends a DELETE request to a server
    /// </summary>
    /// <param name="url">url to send DELETE to</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>response message</returns>
    Task Delete(string url, HttpOptions options = null);
        
    /// <summary>
    /// sends a DELETE request to a server
    /// </summary>
    /// <param name="url">url to send DELETE to</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>response message</returns>
    Task<T> Delete<T>(string url, HttpOptions options = null);

    /// <summary>
    /// sends a custom request to a server
    /// </summary>
    /// <typeparam name="TBody">type of body to send</typeparam>
    /// <param name="method">HTTP VERB to use</param>
    /// <param name="url">url to send request to</param>
    /// <param name="body">body to include in request</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>response message</returns>
    Task Request<TBody>(string method, string url, TBody body, HttpOptions options = null);

    /// <summary>
    /// sends a custom request to a server
    /// </summary>
    /// <typeparam name="TBody">type of body to send</typeparam>
    /// <typeparam name="TResponse">type of response to return</typeparam>
    /// <param name="method">HTTP VERB to use</param>
    /// <param name="url">url to send request to</param>
    /// <param name="body">body to include in request</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>response message</returns>
    Task<TResponse> Request<TBody, TResponse>(string method, string url, TBody body, HttpOptions options = null);

    /// <summary>
    /// creates a request to be sent
    /// </summary>
    /// <remarks>
    /// this can be used to customize http request before sending it
    /// </remarks>
    /// <param name="url">url to send request to</param>
    /// <param name="method">method to use</param>
    /// <param name="options">options for request (optional)</param>
    /// <returns>created message</returns>
    Task<HttpRequestMessage> CreateRequest(string url, HttpMethod method, HttpOptions options = null);

    /// <summary>
    /// sends a request to the server
    /// </summary>
    /// <param name="request">request to send</param>
    /// <param name="options">options for request (optional)</param>
    /// <typeparam name="TResponse">type of response expected</typeparam>
    /// <returns>response from server</returns>
    Task<TResponse> Send<TResponse>(HttpRequestMessage request, HttpOptions options = null);
        
    /// <summary>
    /// sends a request to the server
    /// </summary>
    /// <param name="request">request to send</param>
    /// <returns>response from server</returns>
    Task Send(HttpRequestMessage request);
}
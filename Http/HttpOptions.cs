using System;
using NightlyCode.Http.Encodings;

namespace NightlyCode.Http; 

/// <summary>
/// options for http requests
/// </summary>
public class HttpOptions {
        
    /// <summary>
    /// decoder for responses (optional, defaults to json)
    /// </summary>
    public IResponseDecoder Decoder { get; set; }

    /// <summary>
    /// encoder for content (optional, defaults to json)
    /// </summary>
    public IResponseEncoder Encoder { get; set; }
        
    /// <summary>
    /// provider for authentication tokens
    /// </summary>
    public ITokenProvider TokenProvider { get; set; }

    /// <summary>
    /// media type to use for direct stream output
    /// </summary>
    public string MediaType { get; set; }

    /// <summary>
    /// determines whether to follow redirects automatically
    /// </summary>
    public bool FollowRedirects { get; set; }

    /// <summary>
    /// determines whether to add expect continue header
    /// </summary>
    public bool? ExpectContinue { get; set; }
        
    /// <summary>
    /// if set this function is used to process urls before requests
    /// </summary>
    public Func<string, string> UrlProcessor { get; set; }

    /// <summary>
    /// headers to add to request
    /// </summary>
    public HttpHeader[] Headers { get; set; }
}
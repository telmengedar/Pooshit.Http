using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Pooshit.Http.Encodings;

namespace Pooshit.Http; 

/// <inheritdoc />
public class HttpService : IHttpService {
    static readonly ISet<string> redirectExcludedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "Expect",
        "Transfer-Encoding"
    };

    readonly HttpClient client;
    readonly Random random = new();
        
    /// <summary>
    /// creates a new <see cref="HttpService"/>
    /// </summary>
    public HttpService(HttpMessageHandler handler = null) {
        if (handler != null)
            client = new(handler);
        else if (!RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER"))) {
            // wasm crashes with allowautoredirect
            client = new(new HttpClientHandler {
                                                   AllowAutoRedirect = false
                                               });
        }
        else client = new();
    }
        
    /// <summary>
    /// access to http timeout
    /// </summary>
    public TimeSpan Timeout {
        get => client.Timeout;
        set => client.Timeout = value;
    }

    /// <summary>
    /// header rendering used for error messages when a call does not specify one of its own
    /// </summary>
    public HeaderDumpMode HeaderDumpMode { get; set; } = HeaderDumpMode.Redacted;

    /// <summary>
    /// names of headers treated as credentials: their values are replaced by a placeholder when headers are dumped in redacted mode, and they are not carried onto a redirect hop which leaves the origin; matched ignoring case and not safe to mutate once the service has been used
    /// </summary>
    public ISet<string> SensitiveHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "Api-Key",
        "X-Api-Key",
        "X-Auth-Token",
        "X-Access-Token"
    };

    /// <summary>
    /// words treated as credentials when they appear anywhere inside a query parameter name: the value of such a parameter is replaced by a placeholder in error messages while its name survives; the name is matched as a substring ignoring case, so a longer word carrying an entry is redacted too, separate from <see cref="SensitiveHeaders"/> and not safe to mutate once the service has been used
    /// </summary>
    public ISet<string> SensitiveQueryParameters { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "token",
        "key",
        "secret",
        "password",
        "signature",
        "sig",
        "auth",
        "credential"
    };

#if !NETSTANDARD2_0
    /// <summary>
    /// access default request version of http client
    /// </summary>
    public Version DefaultRequestVersion {
        get => client.DefaultRequestVersion;
        set => client.DefaultRequestVersion = value;
    }

    /// <summary>
    /// access default version policy of http client
    /// </summary>
    public HttpVersionPolicy DefaultVersionPolicy {
        get => client.DefaultVersionPolicy;
        set => client.DefaultVersionPolicy = value;
    }
#endif
        
    static bool IsSameOrigin(Uri origin, Uri target) {
        if (origin == null || target == null)
            return false;

        return string.Equals(origin.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Host, target.Host, StringComparison.OrdinalIgnoreCase)
            && origin.Port == target.Port;
    }

    HttpRequestMessage CreateRedirectRequest(string url, HttpRequestMessage redirected) {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        if (redirected != null) {
            bool sameOrigin = IsSameOrigin(redirected.RequestUri, request.RequestUri);
            foreach (KeyValuePair<string, IEnumerable<string>> header in redirected.Headers) {
                if (redirectExcludedHeaders.Contains(header.Key))
                    continue;
                if (!sameOrigin && SensitiveHeaders.Contains(header.Key))
                    continue;
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return request;
    }

    static string EncodeHeaderString (string input)
    {
        StringBuilder sb = new();

        foreach (char ch in input) {
            if (ch > 127)
                sb.Append($"\\u{(int)ch:x4}");
            else sb.Append(ch);
        }

        return sb.ToString ();
    }
    
    public async Task<HttpRequestMessage> CreateRequest(string url, HttpMethod method, HttpOptions options) {
        HttpRequestMessage request = new(method, url);
        if (options?.TokenProvider != null) {
            string token = await options.TokenProvider.GetTokenAsync();
            string authMethod = options.TokenProvider.Method;
            if (string.IsNullOrEmpty(authMethod))
                authMethod = "Bearer";
                
            if(token != null)
                request.Headers.Authorization = new(authMethod, token);
        }

        if (options?.Headers != null) {
            foreach (HttpHeader header in options.Headers) {
                request.Headers.TryAddWithoutValidation(EncodeHeaderString(header.Key), EncodeHeaderString(header.Value));
            }
        }
        return request;
    }

    async Task<HttpRequestMessage> CreateRequest<T>(string url, HttpMethod method, T body, HttpOptions options) {
        if(body == null)
            throw new ArgumentNullException(nameof(body), $"Must provide a body for '{method}'");
            
        HttpRequestMessage request = await CreateRequest(url, method, options);
        request.Headers.ExpectContinue = options?.ExpectContinue;
        if (options?.MediaType == "application/x-www-form-urlencoded") {
            if (body is IDictionary dic) {
                FormUrlEncodedContent content = new(dic.Keys.Cast<object>().Select(k => new KeyValuePair<string, string>(k.ToString(), dic[k]?.ToString())));
                content.Headers.ContentType = new(options.MediaType);
                request.Content = content;
            }
            else throw new("Body type not supported for x-www-form-urlencoded requests");
        }
        else if (body is FormData or FormData[]) {
            MultipartFormDataContent content = new($"------------------------{random.Next():x8}{random.Next():x8}");

            // disable quotes in boundary
            // some servers don't understand boundaries in quotes
            NameValueHeaderValue boundary = content.Headers.ContentType?.Parameters.First(p => p.Name == "boundary");
            if (boundary?.Value != null)
                boundary.Value = boundary.Value.Replace("\"", string.Empty);

            if (body is FormData sfd) {
                content.Add(sfd.Content);
            }
            else
                foreach (FormData data in (FormData[])(object)body)
                    content.Add(data.Content);
            request.Content = content;
        }
        else if(body is HttpContent httpcontent)
            request.Content = httpcontent;
        else if(body is Stream stream) {
            request.Content = new StreamContent(stream);

            if (!string.IsNullOrEmpty(options?.MediaType))
                request.Content.Headers.ContentType = new(options.MediaType);
        }
        else {
            IResponseEncoder encoder = options?.Encoder ?? new JsonEncoder();
            request.Content = encoder.Encode(body);
        }

        return request;
    }

    void DumpHeader(StringBuilder builder, KeyValuePair<string, IEnumerable<string>> header, HeaderDumpMode mode) {
        builder.Append(header.Key).Append(": ");
        if (mode == HeaderDumpMode.Redacted && SensitiveHeaders.Contains(header.Key))
            builder.AppendLine("<redacted>");
        else builder.AppendLine(string.Join("; ", header.Value));
    }

    string DumpHeaders(HttpResponseMessage response, HttpOptions options) {
        HeaderDumpMode mode = options?.HeaderDumpMode ?? HeaderDumpMode;
        if (mode == HeaderDumpMode.Omitted)
            return string.Empty;

        StringBuilder builder = new();
        builder.AppendLine("Request Headers");
        if(response.RequestMessage!=null)
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.RequestMessage.Headers)
                DumpHeader(builder, header, mode);

        builder.AppendLine("Response Headers");
        foreach(KeyValuePair<string, IEnumerable<string>> header in response.Headers)
            DumpHeader(builder, header, mode);
        return builder.ToString();
    }
        
    bool IsSensitiveQueryParameter(string name) {
        return SensitiveQueryParameters.Any(word => name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    string DumpUrl(HttpResponseMessage response) {
        string url = response.RequestMessage?.RequestUri?.ToString();
        if (url == null)
            return string.Empty;

        int queryStart = url.IndexOf('?');
        if (queryStart < 0)
            return url;

        int queryEnd = url.IndexOf('#', queryStart);
        if (queryEnd < 0)
            queryEnd = url.Length;

        StringBuilder builder = new();
        builder.Append(url, 0, queryStart + 1);

        string[] parameters = url.Substring(queryStart + 1, queryEnd - queryStart - 1).Split('&');
        for (int index = 0; index < parameters.Length; ++index) {
            if (index > 0)
                builder.Append('&');

            string parameter = parameters[index];
            int separator = parameter.IndexOf('=');
            if (separator >= 0 && IsSensitiveQueryParameter(parameter.Substring(0, separator)))
                builder.Append(parameter, 0, separator + 1).Append("<redacted>");
            else builder.Append(parameter);
        }

        return builder.Append(url, queryEnd, url.Length - queryEnd).ToString();
    }

    Task<HttpResponseMessage> SendRequest(HttpRequestMessage request, HttpOptions options) {
        return client.SendAsync(request, options?.CompletionOption ?? HttpCompletionOption.ResponseContentRead);
    }

    async Task CheckHttpResponse(HttpResponseMessage response, HttpOptions options) {
        if ((int)response.StatusCode < 200 || (int)response.StatusCode > 399) {
            using StreamReader reader = new(await response.Content.ReadAsStreamAsync());
            string responseBody = await reader.ReadToEndAsync();
            throw new HttpServiceException(response, $"Error sending request to '{DumpUrl(response)}' -> status {response.StatusCode}\n{DumpHeaders(response, options)}", body: string.IsNullOrEmpty(responseBody) ? null : responseBody);
        }
    }

    async Task<T> ReadResponse<T>(HttpResponseMessage response, IResponseDecoder decoder) {
        if(typeof(T) == typeof(HttpResponseMessage))
            return (T)(object)response;

        if(response.Content.Headers.ContentLength == 0) {
            response.Dispose();
            return default;
        }

        if(typeof(T) == typeof(Stream))
            // don't close http response if it is to be read as stream
            // as it would close the stream
            return (T)(object)await response.Content.ReadAsStreamAsync();

        if(typeof(T) == typeof(string))
            using(response)
                return (T)(object)await response.Content.ReadAsStringAsync();
        if(typeof(T) == typeof(byte[]))
            using(response)
                return (T)(object)await response.Content.ReadAsByteArrayAsync();

        string mediaType = response.Content.Headers.ContentType?.MediaType;

        if (IsJsonMediaType(mediaType))
            using (response) {
                decoder ??= new JsonDecoder();
                try {
                    return await decoder.Decode<T>(response);
                }
                catch (Exception e) {
                    throw new HttpServiceException(response, $"Error decoding response of '{DumpUrl(response)}'", e);
                }
            }

        switch(mediaType)
        {
            case "application/xml":
            case "text/xml":
                using (response)
                    return (T)(object)XDocument.Load(await response.Content.ReadAsStreamAsync());
            case "text/plain":
                using(response)
                    return (T)(object)await response.Content.ReadAsStringAsync();
        }

        return await DecodeUnknownMediaType<T>(response, decoder, mediaType);
    }

    static bool IsJsonMediaType(string mediaType) {
        if (string.IsNullOrEmpty(mediaType))
            return false;

        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "text/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    static bool StartsJsonStructure(string body) {
        foreach (char character in body) {
            if (character == '\ufeff' || char.IsWhiteSpace(character))
                continue;
            return character is '{' or '[';
        }

        return false;
    }

    async Task<T> DecodeUnknownMediaType<T>(HttpResponseMessage response, IResponseDecoder decoder, string mediaType) {
        string body = await response.Content.ReadAsStringAsync();
        string reported = string.IsNullOrEmpty(mediaType) ? "<none>" : mediaType;
        string context = $"'{DumpUrl(response)}' (media type '{reported}', requested type '{typeof(T).Name}')";

        if (!StartsJsonStructure(body))
            throw new HttpServiceException(response, $"Unable to decode response of {context}", body: body);

        decoder ??= new JsonDecoder();

        T decoded;
        try {
            decoded = await decoder.Decode<T>(response);
        }
        catch (Exception e) {
            throw new HttpServiceException(response, $"Error decoding response of {context}", e, body);
        }

        response.Dispose();
        return decoded;
    }

    async Task<T> HandleResponse<T>(HttpResponseMessage response, HttpOptions options) {
        if (options?.FollowRedirects ?? false) {
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod) {
                string location = response.Headers.Location?.ToString();
                if (options.UrlProcessor != null)
                    location = options.UrlProcessor(location);

                Uri requestUri = response.RequestMessage?.RequestUri;
                string url = requestUri != null ? new Uri(requestUri, location).ToString() : location;
                HttpResponseMessage previousResponse = response;
                response = await SendRequest(CreateRedirectRequest(url, previousResponse.RequestMessage), options);
                previousResponse.Dispose();
            }
            else if (response.StatusCode is HttpStatusCode.RedirectKeepVerb)
                // TODO 307/308: re-send with original method + body instead of GET
                throw new NotSupportedException("307 redirect is not implemented yet");
        }
            
        if (!(typeof(T) == typeof(HttpResponseMessage)))
            await CheckHttpResponse(response, options);

        return await ReadResponse<T>(response, options?.Decoder);
    }

    /// <inheritdoc />
    public async Task<TResponse> Post<TRequest, TResponse>(string url, TRequest content, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, HttpMethod.Post, content, options), options);
        return await HandleResponse<TResponse>(response, options);
    }

    /// <inheritdoc />
    public async Task Post(string url, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, HttpMethod.Post, options), options);
        response.Dispose();
    }

    /// <inheritdoc />
    public async Task<TResponse> Post<TResponse>(string url, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, HttpMethod.Post, options), options);
        return await HandleResponse<TResponse>(response, options);
    }

    /// <inheritdoc />
    public async Task Post<TRequest>(string url, TRequest content, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, HttpMethod.Post, content, options), options);
        await CheckHttpResponse(response, options);
        response.Dispose();
    }

    /// <inheritdoc />
    public async Task<TResponse> Put<TRequest, TResponse>(string url, TRequest content, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, HttpMethod.Put, content, options), options);
        return await HandleResponse<TResponse>(response, options);
    }

    /// <inheritdoc />
    public async Task Put<TRequest>(string url, TRequest content, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, HttpMethod.Put, content, options), options);
        await CheckHttpResponse(response, options);
        response.Dispose();
    }

    /// <inheritdoc />
    public async Task<TResponse> Patch<TRequest, TResponse>(string url, TRequest content, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, new HttpMethod("PATCH"), content, options), options);
        return await HandleResponse<TResponse>(response, options);
    }

    /// <inheritdoc />
    public async Task Patch<TRequest>(string url, TRequest content, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, new HttpMethod("PATCH"), content, options), options);
        await CheckHttpResponse(response, options);
        response.Dispose();
    }

    /// <inheritdoc />
    public async Task Get(string url, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, HttpMethod.Get, options), options);
        await CheckHttpResponse(response, options);
        response.Dispose();
    }

    /// <inheritdoc />
    public async Task<T> Get<T>(string url, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, HttpMethod.Get, options), options);
        return await HandleResponse<T>(response, options);
    }

    /// <inheritdoc />
    public async Task Delete(string url, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, HttpMethod.Delete, options), options);
        await CheckHttpResponse(response, options);
        response.Dispose();
    }

    /// <inheritdoc />
    public async Task<T> Delete<T>(string url, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, HttpMethod.Delete, options), options);
        return await HandleResponse<T>(response, options);
    }

    /// <inheritdoc />
    public async Task Request<TBody>(string method, string url, TBody body, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, new HttpMethod(method), body, options), options);
        await CheckHttpResponse(response, options);
        response.Dispose();
    }

    /// <inheritdoc />
    public async Task<TResponse> Request<TBody, TResponse>(string method, string url, TBody body, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(await CreateRequest(url, new HttpMethod(method), body, options), options);
        return await HandleResponse<TResponse>(response, options);
    }

    /// <inheritdoc />
    public async Task<TResponse> Send<TResponse>(HttpRequestMessage request, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(request, options);
        return await HandleResponse<TResponse>(response, options);
    }

    /// <inheritdoc />
    public async Task Send(HttpRequestMessage request, HttpOptions options = null) {
        HttpResponseMessage response = await SendRequest(request, options);
        await CheckHttpResponse(response, options);
        response.Dispose();
    }
}
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

    static readonly ISet<string> urlValuedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "Location",
        "Referer"
    };

    // HttpStatusCode.PermanentRedirect does not exist on netstandard2.0 (CS0117), the cast compiles on every target
    const HttpStatusCode permanentRedirect = (HttpStatusCode)308;

    readonly HttpClient client;
    readonly Random random = new();
        
    /// <summary>
    /// creates a new <see cref="HttpService"/>
    /// </summary>
    public HttpService(HttpMessageHandler handler = null) {
        if (handler != null)
            client = new(handler);
        else if (!RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER"))) {
            // per the Fetch standard a browser answers a manual redirect with an opaque response - status 0, no Location - which cannot be followed here, so on that platform redirect policy stays the user agent's
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
        "apiKey",
        "X-ApiKey",
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

    HttpRequestMessage CreateRedirectRequest(string url, HttpRequestMessage redirected, bool preserveRequest) {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        if (redirected != null) {
            if (preserveRequest) {
                request.Method = redirected.Method;
                request.Content = redirected.Content;
            }

            bool sameOrigin = IsSameOrigin(redirected.RequestUri, request.RequestUri);
            foreach (KeyValuePair<string, IEnumerable<string>> header in redirected.Headers) {
                if (request.Content == null && redirectExcludedHeaders.Contains(header.Key))
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

    bool IsSensitiveQueryParameter(string name) {
        return SensitiveQueryParameters.Any(word => name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    string RedactQuery(string url) {
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

    string DumpUrl(HttpResponseMessage response) {
        string url = response.RequestMessage?.RequestUri?.ToString();
        return url == null ? string.Empty : RedactQuery(url);
    }

    string DumpHeaderValue(string name, IEnumerable<string> values, HeaderDumpMode mode) {
        if (mode == HeaderDumpMode.Redacted) {
            if (SensitiveHeaders.Contains(name))
                return "<redacted>";

            if (urlValuedHeaders.Contains(name))
                return string.Join("; ", values.Select(RedactQuery));
        }

        return string.Join("; ", values);
    }

    void DumpHeader(StringBuilder builder, KeyValuePair<string, IEnumerable<string>> header, HeaderDumpMode mode) {
        builder.Append(header.Key).Append(": ").AppendLine(DumpHeaderValue(header.Key, header.Value, mode));
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

    Task<HttpResponseMessage> SendRequest(HttpRequestMessage request, HttpOptions options) {
        return client.SendAsync(request, options?.CompletionOption ?? HttpCompletionOption.ResponseContentRead);
    }

    string DumpRedirect(HttpResponseMessage response, HttpOptions options) {
        if ((int)response.StatusCode < 300 || (int)response.StatusCode > 399)
            return string.Empty;

        HeaderDumpMode mode = options?.HeaderDumpMode ?? HeaderDumpMode;
        string target;
        if (!response.Headers.TryGetValues("Location", out IEnumerable<string> location))
            target = "no location";
        else if (mode == HeaderDumpMode.Omitted)
            target = "an undisclosed location";
        else target = $"'{DumpHeaderValue("Location", location, mode)}'";

        return $", a redirect to {target} which was not followed; set HttpOptions.FollowRedirects to follow it, or request HttpResponseMessage to read the response yourself";
    }

    async Task CheckHttpResponse(HttpResponseMessage response, HttpOptions options) {
        if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299) {
            using StreamReader reader = new(await response.Content.ReadAsStreamAsync());
            string responseBody = await reader.ReadToEndAsync();
            throw new HttpServiceException(response, $"Error sending request to '{DumpUrl(response)}' -> status {response.StatusCode}{DumpRedirect(response, options)}\n{DumpHeaders(response, options)}", body: string.IsNullOrEmpty(responseBody) ? null : responseBody);
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
                if (!typeof(T).IsAssignableFrom(typeof(XDocument)))
                    throw new HttpServiceException(response, $"Unable to decode response of {DumpResponseContext<T>(response, mediaType)}", body: await response.Content.ReadAsStringAsync());
                using (response)
                    return (T)(object)XDocument.Load(await response.Content.ReadAsStreamAsync());
            case "text/plain":
                if (!typeof(T).IsAssignableFrom(typeof(string)))
                    throw new HttpServiceException(response, $"Unable to decode response of {DumpResponseContext<T>(response, mediaType)}", body: await response.Content.ReadAsStringAsync());
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

    string DumpResponseContext<T>(HttpResponseMessage response, string mediaType) {
        string reported = string.IsNullOrEmpty(mediaType) ? "<none>" : mediaType;
        return $"'{DumpUrl(response)}' (media type '{reported}', requested type '{typeof(T).Name}')";
    }

    async Task<T> DecodeUnknownMediaType<T>(HttpResponseMessage response, IResponseDecoder decoder, string mediaType) {
        string body = await response.Content.ReadAsStringAsync();
        string context = DumpResponseContext<T>(response, mediaType);

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

    static bool IsConsumedContentFailure(Exception error) {
        for (Exception walk = error; walk != null; walk = walk.InnerException)
            if (walk is InvalidOperationException)
                return true;

        return false;
    }

    static bool CarriesResponse(Exception error, HttpResponseMessage response) {
        return error is HttpServiceException failure && ReferenceEquals(failure.Response, response);
    }

    HttpServiceException RedirectFailure(HttpResponseMessage response, HttpOptions options, string url, string reason, Exception inner = null) {
        HttpMethod method = response.RequestMessage?.Method;
        string repeated = method != null ? $"{method} '{DumpUrl(response)}'" : "the original request";
        string target = url == null ? string.Empty : RedactQuery(url);
        return new(response, $"Error repeating {repeated} on the status {(int)response.StatusCode} redirect to '{target}': {reason}\n{DumpHeaders(response, options)}", inner);
    }

    async Task<HttpResponseMessage> SendRedirect(HttpResponseMessage response, HttpOptions options, bool preserveRequest) {
        string location = response.Headers.Location?.ToString();
        if (options.UrlProcessor != null)
            location = options.UrlProcessor(location);

        HttpRequestMessage redirected = response.RequestMessage;
        Uri requestUri = redirected?.RequestUri;
        string url = requestUri != null ? new Uri(requestUri, location).ToString() : location;

        if (preserveRequest) {
            if (redirected == null)
                throw RedirectFailure(response, options, url, "the response carries no request to repeat");
            if (requestUri == null)
                throw RedirectFailure(response, options, url, "the response carries no request uri to resolve the target against");
            if (url == requestUri.ToString())
                throw RedirectFailure(response, options, url, "the response names no target to repeat it against");
        }

        HttpRequestMessage request = CreateRedirectRequest(url, redirected, preserveRequest);

        try {
            return await SendRequest(request, options);
        }
        catch (Exception e) when (preserveRequest && IsConsumedContentFailure(e)) {
            throw RedirectFailure(response, options, url, "the request body cannot be sent a second time", e);
        }
    }

    async Task<HttpResponseMessage> FollowRedirect(HttpResponseMessage response, HttpOptions options, bool preserveRequest) {
        HttpResponseMessage hop;
        try {
            hop = await SendRedirect(response, options, preserveRequest);
        }
        catch (Exception e) when (!CarriesResponse(e, response)) {
            response.Dispose();
            throw;
        }

        response.Dispose();
        return hop;
    }

    async Task<T> HandleResponse<T>(HttpResponseMessage response, HttpOptions options) {
        if (options?.FollowRedirects ?? false) {
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod)
                response = await FollowRedirect(response, options, false);
            else if (response.StatusCode is HttpStatusCode.RedirectKeepVerb or permanentRedirect)
                response = await FollowRedirect(response, options, true);
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
        await CheckHttpResponse(response, options);
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
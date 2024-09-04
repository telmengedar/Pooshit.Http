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
using NightlyCode.Http.Encodings;

namespace NightlyCode.Http; 

/// <inheritdoc />
public class HttpService : IHttpService {
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
                request.Headers.Add(header.Key, header.Value);
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

    string DumpHeaders(HttpResponseMessage response) {
        StringBuilder builder = new();
        builder.AppendLine("Request Headers");
        if(response.RequestMessage!=null)
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.RequestMessage.Headers)
                builder.Append(header.Key).Append(": ").AppendLine(string.Join("; ", header.Value));
            
        builder.AppendLine("Response Headers");
        foreach(KeyValuePair<string, IEnumerable<string>> header in response.Headers)
            builder.Append(header.Key).Append(": ").AppendLine(string.Join("; ", header.Value));
        return builder.ToString();
    }
        
    async Task CheckHttpResponse(HttpResponseMessage response) {
        if ((int)response.StatusCode < 200 || (int)response.StatusCode > 399) {
            using StreamReader reader = new(await response.Content.ReadAsStreamAsync());
            string responseBody = await reader.ReadToEndAsync();
            if(!string.IsNullOrEmpty(responseBody))
                throw new HttpServiceException(response, $"Error sending request to '{response.RequestMessage?.RequestUri}' -> status {response.StatusCode}\n{DumpHeaders(response)}\n{responseBody}");
            throw new HttpServiceException(response, $"Error sending request to '{response.RequestMessage?.RequestUri}' -> status {response.StatusCode}\n{DumpHeaders(response)}");
        }
    }

    async Task<T> ReadResponse<T>(HttpResponseMessage response, IResponseDecoder decoder) {
        if(typeof(T) == typeof(HttpResponseMessage))
            return (T)(object)response;

        if((response.Content.Headers.ContentLength ?? 0) == 0)
            return default;

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

        switch(response.Content.Headers.ContentType?.MediaType)
        {
            case "application/json":
                using (response) {
                    decoder ??= new JsonDecoder();
                    try {
                        return await decoder.Decode<T>(response);
                    }
                    catch (Exception e) {
                        throw new HttpServiceException(response, $"Error decoding response of '{response.RequestMessage?.RequestUri}'", e);
                    }
                }
            case "application/xml":
            case "text/xml":
                using (response)
                    return (T)(object)XDocument.Load(await response.Content.ReadAsStreamAsync());
            case "text/plain":
                using(response)
                    return (T)(object)await response.Content.ReadAsStringAsync();
        }

        return (T)(object)await response.Content.ReadAsStreamAsync();
    }

    async Task<T> HandleResponse<T>(HttpResponseMessage response, HttpOptions options) {
        if (options?.FollowRedirects ?? false) {
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod) {
                string url = response.Headers.Location?.ToString();
                if (options.UrlProcessor != null)
                    url = options.UrlProcessor(url);
                url = $"{response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Authority)}{url}";
                response = await client.SendAsync(await CreateRequest(url, HttpMethod.Get, options));
            }
            else if (response.StatusCode is HttpStatusCode.RedirectKeepVerb)
                throw new NotSupportedException("307 redirect is not implemented yet");
        }
            
        if (!(typeof(T) == typeof(HttpResponseMessage)))
            await CheckHttpResponse(response);

        return await ReadResponse<T>(response, options?.Decoder);
    }

    /// <inheritdoc />
    public async Task<TResponse> Post<TRequest, TResponse>(string url, TRequest content, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(await CreateRequest(url, HttpMethod.Post, content, options));
        return await HandleResponse<TResponse>(response, options);
    }

    /// <inheritdoc />
    public async Task Post(string url, HttpOptions options = null) {
        await client.SendAsync(await CreateRequest(url, HttpMethod.Post, options));
    }

    /// <inheritdoc />
    public async Task<TResponse> Post<TResponse>(string url, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(await CreateRequest(url, HttpMethod.Post, options));
        return await HandleResponse<TResponse>(response, options);
    }

    /// <inheritdoc />
    public async Task Post<TRequest>(string url, TRequest content, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(await CreateRequest(url, HttpMethod.Post, content, options));
        await CheckHttpResponse(response);
    }

    /// <inheritdoc />
    public async Task<TResponse> Put<TRequest, TResponse>(string url, TRequest content, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(await CreateRequest(url, HttpMethod.Put, content, options));
        return await HandleResponse<TResponse>(response, options);
    }

    /// <inheritdoc />
    public async Task Put<TRequest>(string url, TRequest content, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(await CreateRequest(url, HttpMethod.Put, content, options));
        await CheckHttpResponse(response);
    }

    /// <inheritdoc />
    public async Task<TResponse> Patch<TRequest, TResponse>(string url, TRequest content, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(await CreateRequest(url, new HttpMethod("PATCH"), content, options));
        return await HandleResponse<TResponse>(response, options);
    }

    /// <inheritdoc />
    public async Task Patch<TRequest>(string url, TRequest content, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(await CreateRequest(url, new HttpMethod("PATCH"), content, options));
        await CheckHttpResponse(response);
    }

    /// <inheritdoc />
    public async Task Get(string url, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(await CreateRequest(url, HttpMethod.Get, options));
        await CheckHttpResponse(response);
    }

    /// <inheritdoc />
    public async Task<T> Get<T>(string url, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(await CreateRequest(url, HttpMethod.Get, options));
        return await HandleResponse<T>(response, options);
    }

    /// <inheritdoc />
    public async Task Delete(string url, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(await CreateRequest(url, HttpMethod.Delete, options));
        await CheckHttpResponse(response);
    }

    /// <inheritdoc />
    public async Task<T> Delete<T>(string url, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(await CreateRequest(url, HttpMethod.Delete, options));
        return await HandleResponse<T>(response, options);
    }

    /// <inheritdoc />
    public async Task Request<TBody>(string method, string url, TBody body, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(await CreateRequest(url, new HttpMethod(method), body, options));
        await CheckHttpResponse(response);
    }

    /// <inheritdoc />
    public async Task<TResponse> Request<TBody, TResponse>(string method, string url, TBody body, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(await CreateRequest(url, new HttpMethod(method), body, options));
        return await HandleResponse<TResponse>(response, options);
    }

    /// <inheritdoc />
    public async Task<TResponse> Send<TResponse>(HttpRequestMessage request, HttpOptions options = null) {
        HttpResponseMessage response = await client.SendAsync(request);
        return await HandleResponse<TResponse>(response, options);
    }

    /// <inheritdoc />
    public async Task Send(HttpRequestMessage request) {
        HttpResponseMessage response = await client.SendAsync(request);
        await CheckHttpResponse(response);
    }
}
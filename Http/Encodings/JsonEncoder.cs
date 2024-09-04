using System.Net.Http;
using System.Net.Http.Headers;
using NightlyCode.Json;

namespace NightlyCode.Http.Encodings; 

/// <summary>
/// encodes data to json for requests
/// </summary>
public class JsonEncoder : IResponseEncoder {
        
    readonly JsonOptions options = new() {
                                             ExcludeNullProperties = true,
                                             NamingStrategy = NamingStrategies.CamelCase
                                         };
        
    /// <inheritdoc />
    public HttpContent Encode(object data) {
        // TODO: WriteAsync Stream?
        StringContent content = new(Json.Json.WriteString(data, options));
        content.Headers.ContentType = new("application/json");
        return content;
    }
}
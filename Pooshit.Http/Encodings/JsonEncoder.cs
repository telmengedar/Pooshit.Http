using System.Net.Http;
using Pooshit.Json;

namespace Pooshit.Http.Encodings; 

/// <summary>
/// encodes data to json for requests
/// </summary>
public class JsonEncoder : IResponseEncoder {
    
    /// <inheritdoc />
    public HttpContent Encode(object data) {
        // TODO: WriteAsync Stream?
        StringContent content = new(Json.Json.WriteString(data, JsonOptions.RestApi));
        content.Headers.ContentType = new("application/json");
        return content;
    }
}
using System.Net.Http;
using Pooshit.Json;

namespace Pooshit.Http.Encodings; 

/// <summary>
/// encodes data to json for requests
/// </summary>
public class JsonEncoder : IResponseEncoder {
    readonly JsonOptions options;

    /// <summary>
    /// creates a new <see cref="JsonEncoder"/> using default json options
    /// </summary>
    public JsonEncoder() : this(JsonOptions.RestApi){
    }
    
    /// <summary>
    /// creates a new <see cref="JsonEncoder"/> using custom json options
    /// </summary>
    /// <param name="options">options to use when encoding data</param>
    public JsonEncoder(JsonOptions options) => this.options = options;

    /// <inheritdoc />
    public HttpContent Encode(object data) {
        // TODO: WriteAsync Stream?
        StringContent content = new(Json.Json.WriteString(data, options));
        content.Headers.ContentType = new("application/json");
        return content;
    }
}
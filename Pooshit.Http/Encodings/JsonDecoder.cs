using System.Net.Http;
using System.Threading.Tasks;

namespace Pooshit.Http.Encodings; 

/// <inheritdoc />
public class JsonDecoder : IResponseDecoder {
        
    /// <inheritdoc />
    public async Task<T> Decode<T>(HttpResponseMessage message) {
        return await Json.Json.ReadAsync<T>(await message.Content.ReadAsStreamAsync());
    }

    public T DecodeSync<T>(HttpResponseMessage message) {
        return Json.Json.Read<T>(message.Content.ReadAsStreamAsync().Result);
    }
}
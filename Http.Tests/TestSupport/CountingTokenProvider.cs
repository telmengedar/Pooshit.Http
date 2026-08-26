using System.Threading.Tasks;
using Pooshit.Http;

namespace Http.Tests.TestSupport;

/// <summary>
/// token provider handing out a fixed token and counting how often it was asked for one
/// </summary>
public class CountingTokenProvider : ITokenProvider {
    readonly string token;

    /// <summary>
    /// creates a new <see cref="CountingTokenProvider"/>
    /// </summary>
    /// <param name="token">token to hand out on every request</param>
    public CountingTokenProvider(string token) {
        this.token = token;
    }

    /// <summary>
    /// number of times a token was requested
    /// </summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public string Method => string.Empty;

    /// <inheritdoc />
    public Task<string> GetTokenAsync() {
        ++Calls;
        return Task.FromResult(token);
    }
}

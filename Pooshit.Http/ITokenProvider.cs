using System.Threading.Tasks;

namespace Pooshit.Http; 

/// <summary>
/// provides tokens to be used for authentication 
/// </summary>
public interface ITokenProvider {
    
    /// <summary>
    /// get a authentication token from auth0
    /// </summary>
    /// <returns>token to use to authenticate</returns>
    Task<string> GetTokenAsync();
        
    /// <summary>
    /// authentication method
    /// </summary>
    /// <remarks>
    /// if method is empty, 'Bearer' is used as default
    /// </remarks>
    string Method { get; }
}
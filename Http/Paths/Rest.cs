namespace NightlyCode.Http.Paths; 

/// <summary>
/// helper method for rest paths
/// </summary>
public static class Rest {

    /// <summary>
    /// creates an api url for rest calls
    /// </summary>
    /// <param name="elements">elements used to build path</param>
    /// <returns>rest url to which to send request</returns>
    public static string Path(params object[] elements) {
        return $"{string.Join("/", elements)}";
    }

    /// <summary>
    /// creates an api url for rest calls
    /// </summary>
    /// <param name="elements">elements used to build path</param>
    /// <param name="querystring">parameter string to append</param>
    /// <returns>rest url to which to send request</returns>
    public static string PathQuery(string querystring, params object[] elements) {
        if (string.IsNullOrEmpty(querystring))
            return Path(elements);
        if(!querystring.StartsWith("?"))
            return $"{string.Join("/", elements)}?{querystring}";
        return $"{string.Join("/", elements)}{querystring}";
    }
        
    /// <summary>
    /// creates an api url for rest calls
    /// </summary>
    /// <param name="elements">elements used to build path</param>
    /// <param name="querystring">parameter string to append</param>
    /// <returns>rest url to which to send request</returns>
    public static string PathQuery(QueryParameters querystring, params object[] elements) {
        return PathQuery(querystring.ToString(), elements);
    }
}
using System;
using System.Text;

namespace Pooshit.Http.Paths; 

/// <summary>
/// helper method for rest paths
/// </summary>
public static class Rest {

    /// <summary>
    /// creates an api url for rest calls
    /// </summary>
    /// <param name="baseUrl">url the segments are appended to, used verbatim</param>
    /// <param name="segments">path segments, each rendered and percent-encoded</param>
    /// <returns>rest url to which to send request</returns>
    /// <exception cref="ArgumentNullException">base url, segment array or a segment is null</exception>
    /// <exception cref="ArgumentException">a segment renders to the dot segment '.' or '..'</exception>
    public static string Path(string baseUrl, params object[] segments) {
        if (baseUrl == null)
            throw new ArgumentNullException(nameof(baseUrl));
        if (segments == null)
            throw new ArgumentNullException(nameof(segments));

        StringBuilder url = new(baseUrl);
        for (int index = 0; index < segments.Length; ++index) {
            object segment = segments[index];
            if (segment == null)
                throw new ArgumentNullException(nameof(segments), $"Segment at index {index} is null");

            string rendered = segment.ToString();
            if (rendered == "." || rendered == "..")
                throw new ArgumentException($"Segment at index {index} is the dot segment '{rendered}'", nameof(segments));

            url.Append('/').Append(Uri.EscapeDataString(rendered));
        }

        return url.ToString();
    }

    /// <summary>
    /// creates an api url for rest calls
    /// </summary>
    /// <param name="querystring">parameter string to append, with or without a leading '?'</param>
    /// <param name="baseUrl">url the segments are appended to, used verbatim</param>
    /// <param name="segments">path segments, each rendered and percent-encoded</param>
    /// <returns>rest url to which to send request</returns>
    /// <exception cref="ArgumentNullException">base url, segment array or a segment is null</exception>
    /// <exception cref="ArgumentException">a segment renders to the dot segment '.' or '..'</exception>
    public static string PathQuery(string querystring, string baseUrl, params object[] segments) {
        string path = Path(baseUrl, segments);
        if (string.IsNullOrEmpty(querystring))
            return path;
        if(!querystring.StartsWith("?"))
            return $"{path}?{querystring}";
        return $"{path}{querystring}";
    }

    /// <summary>
    /// creates an api url for rest calls
    /// </summary>
    /// <param name="querystring">parameters to append</param>
    /// <param name="baseUrl">url the segments are appended to, used verbatim</param>
    /// <param name="segments">path segments, each rendered and percent-encoded</param>
    /// <returns>rest url to which to send request</returns>
    /// <exception cref="ArgumentNullException">base url, segment array or a segment is null</exception>
    /// <exception cref="ArgumentException">a segment renders to the dot segment '.' or '..'</exception>
    public static string PathQuery(QueryParameters querystring, string baseUrl, params object[] segments) {
        return PathQuery(querystring.ToString(), baseUrl, segments);
    }
}

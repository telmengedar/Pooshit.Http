using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace NightlyCode.Http; 

/// <summary>
/// data to be posted as form-data
/// </summary>
public class FormData {

    /// <summary>
    /// creates new <see cref="FormData"/>
    /// </summary>
    /// <param name="content">content to post</param>
    /// <param name="field">name of field (optional)</param>
    /// <param name="file">name of file (optional)</param>
    /// <param name="contenttype">content type to specify (optional)</param>
    public FormData(byte[] content, string field = null, string file = null, string contenttype = null) 
        : this(new ByteArrayContent(content), field, file, contenttype)
    {
    }

    /// <summary>
    /// creates new <see cref="FormData"/>
    /// </summary>
    /// <param name="content">content to post</param>
    /// <param name="field">name of field (optional)</param>
    /// <param name="file">name of file (optional)</param>
    /// <param name="contenttype">content type to specify (optional)</param>
    public FormData(string content, string field = null, string file = null, string contenttype = null) 
        : this(new StringContent(content), field, file, contenttype)
    {
    }

    /// <summary>
    /// creates new <see cref="FormData"/>
    /// </summary>
    /// <param name="content">content to post</param>
    /// <param name="field">name of field (optional)</param>
    /// <param name="file">name of file (optional)</param>
    /// <param name="contenttype">content type to specify (optional)</param>
    public FormData(Stream content, string field = null, string file = null, string contenttype = null) 
        : this(new StreamContent(content), field, file, contenttype)
    {
    }

    /// <summary>
    /// creates new <see cref="FormData"/>
    /// </summary>
    /// <param name="content">content to post</param>
    /// <param name="field">name of field (optional)</param>
    /// <param name="file">name of file (optional)</param>
    /// <param name="contenttype">content type to specify (optional)</param>
    public FormData(HttpContent content, string field = null, string file = null, string contenttype = null) {
        Field = field;
        File = file;
        Content = content;
        content.Headers.ContentDisposition = new("form-data");
        if(!string.IsNullOrEmpty(field))
            content.Headers.ContentDisposition.Name = field;
        if(!string.IsNullOrEmpty(file))
            content.Headers.ContentDisposition.FileName = $"\"{file}\"";
        if(!string.IsNullOrEmpty(contenttype))
            content.Headers.ContentType = new(contenttype);
    }

    /// <summary>
    /// name of field in form
    /// </summary>
    public string Field { get; }

    /// <summary>
    /// filename for file uploads
    /// </summary>
    public string File { get; }

    /// <summary>
    /// content data
    /// </summary>
    public HttpContent Content { get; }
}
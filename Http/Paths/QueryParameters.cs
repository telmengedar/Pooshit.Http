using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Web;

namespace NightlyCode.Http.Paths; 

/// <summary>
/// parameters used in query strings
/// </summary>
public class QueryParameters {
    readonly List<QueryParameter> parameters = [];

    /// <summary>
    /// creates new <see cref="QueryParameters"/>
    /// </summary>
    /// <param name="parameters">parameters to include</param>
    public QueryParameters(params QueryParameter[] parameters) {
        this.parameters.AddRange(parameters);
    }

    /// <summary>
    /// creates new <see cref="QueryParameters"/> with a single parameter contained
    /// </summary>
    /// <param name="name">name of parameter</param>
    /// <param name="value">parameter value</param>
    public QueryParameters(string name, object value) {
        parameters.Add(new(name, value));
    }

    /// <summary>
    /// parameter values wrapped by the collection
    /// </summary>
    public IEnumerable<QueryParameter> Parameters => parameters;
    
    /// <summary>
    /// indexer
    /// </summary>
    /// <param name="name">parameter key</param>
    public object this[string name] {
        get { return parameters.FirstOrDefault(p => p.Name == name).Value; }
        set => Add(name, value);
    }
        
    /// <summary>
    /// adds a parameter to the query parameter collection
    /// </summary>
    /// <param name="parameter">parameter to add</param>
    public void Add(QueryParameter parameter) {
        parameters.Add(parameter);
    }

    /// <summary>
    /// adds a parameter to the query parameter collection
    /// </summary>
    /// <param name="name">name of parameter to add</param>
    /// <param name="value">value of parameter to add</param>
    public void Add(string name, object value) {
        if (value == null)
            return;
        if (value is "")
            return;

        Add(new(name, value));
    }

    /// <summary>
    /// removes parameters by name
    /// </summary>
    /// <param name="name">name of parameter to remove</param>
    public void Remove(string name) {
        parameters.RemoveAll(p => p.Name == name);
    }

    /// <summary>
    /// determines whether the query string contains a specific value
    /// </summary>
    /// <param name="name">name of parameter to check for</param>
    /// <returns>true if queryparameters contain a parameter with the specified name, false otherwise</returns>
    public bool Contains(string name) {
        return parameters.Any(p => p.Name == name);
    }

    /// <summary>
    /// determines whether the query string contains a specific value
    /// </summary>
    /// <param name="name">name of parameter to check for</param>
    /// <param name="value">value to check for</param>
    /// <returns>true if queryparameters contain a parameter with the specified name and value, false otherwise</returns>
    public bool Contains(string name, object value) {
        return parameters.Any(p => p.Name == name && p.Value == value);
    }

    /// <summary>
    /// creates new <see cref="QueryParameters"/> from value
    /// </summary>
    /// <param name="queryValue">value to convert to query parameters</param>
    /// <param name="useDefaultArrayLogic">determines whether to split array items into separate query parameters like supported by asp by default</param>
    /// <returns>created <see cref="QueryParameters"/></returns>
    public static QueryParameters FromValue(object queryValue, bool useDefaultArrayLogic=true) {
        QueryParameters parameters = new();
        if (queryValue == null)
            return parameters;

        foreach(PropertyInfo property in queryValue.GetType().GetProperties()) {
            object value = property.GetValue(queryValue);
            if (value == null)
                continue;

            string propertyname;
            if (Attribute.IsDefined(property, typeof(DataMemberAttribute))) {
                DataMemberAttribute attribute=(DataMemberAttribute)Attribute.GetCustomAttribute(property, typeof(DataMemberAttribute));
                propertyname = attribute.Name;
            }
            else propertyname = property.Name.ToLower();
                
            if(value is Array array && useDefaultArrayLogic) {
                for (int i = 0; i < array.Length; ++i)
                    parameters.Add(propertyname, array.GetValue(i));
            }
            else {
                parameters.Add(propertyname, value);
            }
        }

        return parameters;
    }
        
    /// <inheritdoc />
    public override string ToString() {
        if(parameters.Count == 0)
            return "";

        StringBuilder sb = new("?");
        foreach (QueryParameter parameter in parameters) {
            if (parameter.Value == null)
                continue;
            
            string value;
            if(parameter.Value is DateTime dt)
                value = dt.ToString("O");
            else if (parameter.Value is IEnumerable array and not string)
                value = $"{{{string.Join(",", array.Cast<object>().Select(i => HttpUtility.UrlEncode(i.ToString())))}}}";
            else value = HttpUtility.UrlEncode(parameter.Value.ToString());
            
            sb.Append(HttpUtility.UrlEncode(parameter.Name))
              .Append('=')
              .Append(value)
              .Append('&');
        }

        --sb.Length;
        return sb.ToString();
    }
}
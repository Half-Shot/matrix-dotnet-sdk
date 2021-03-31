using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Matrix.Backends
{
  public class HttpBackend : IMatrixAPIBackend
  {
    private string baseurl;
    private string access_token;
    private string user_id;
    private HttpClient client;

    public HttpBackend(string apiurl, string user_id = null, HttpClient client = null)
    {
      baseurl = apiurl;
      if (baseurl.EndsWith("/"))
      {
        baseurl = baseurl.Substring(0, baseurl.Length - 1);
      }
      ServicePointManager.ServerCertificateValidationCallback += acceptCertificate;
      this.client = client ?? new HttpClient();
      this.user_id = user_id;
    }

    public HttpBackend(string apiurl, string user_id = null, HttpClientHandler handler = null)
      : this(apiurl, user_id, new HttpClient(handler)) { }

    private bool acceptCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
      return true;//Find a better way to handle mono certs.
    }

    public void SetAccessToken(string token)
    {
      access_token = token;
    }

    private string GetPath(string apiPath, bool auth)
    {
      apiPath = baseurl + apiPath;
      if (auth)
      {
        apiPath += (apiPath.Contains("?") ? "&" : "?") + "access_token=" + access_token;
        if (user_id != null)
        {
          apiPath += "&user_id=" + user_id;
        }
      }
      return apiPath;
    }

    private async Task<MatrixAPIResult> RequestWrap(Task<HttpResponseMessage> task)
    {
      var apiResult = new MatrixAPIResult();
      try
      {
        Tuple<JToken, HttpStatusCode> res = await GenericRequest(task);
        apiResult.result = res.Item1;
        apiResult.error = new MatrixRequestError("", MatrixErrorCode.CL_NONE, res.Item2);
      }
      catch (MatrixServerError e)
      {
        int retryAfter = -1;
        if (e.ErrorObject.ContainsKey("retry_after_ms"))
        {
          retryAfter = e.ErrorObject["retry_after_ms"].ToObject<int>();
        }
        apiResult.error = new MatrixRequestError(e.Message, e.ErrorCode, HttpStatusCode.InternalServerError, retryAfter);
      }
      return apiResult;
    }

    public MatrixRequestError Get(string apiPath, bool authenticate, out JToken result)
    {
      apiPath = GetPath(apiPath, authenticate);
      Task<HttpResponseMessage> task = client.GetAsync(apiPath);
      var res = RequestWrap(task);
      res.Wait();
      result = res.Result.result;
      return res.Result.error;
    }

    public Task<MatrixAPIResult> GetAsync(string apiPath, bool authenticate)
    {
      Task<HttpResponseMessage> task = client.GetAsync(GetPath(apiPath, authenticate));
      return RequestWrap(task);
    }

    public MatrixRequestError Delete(string apiPath, bool authenticate, out JToken result)
    {
      apiPath = GetPath(apiPath, authenticate);
      Task<HttpResponseMessage> task = client.DeleteAsync(apiPath);
      var res = RequestWrap(task);
      res.Wait();
      result = res.Result.result;
      return res.Result.error;
    }

    public MatrixRequestError Put(string apiPath, bool authenticate, JToken data, out JToken result)
    {
      StringContent content = new StringContent(data.ToString(Formatting.None), Encoding.UTF8, "application/json");
      apiPath = GetPath(apiPath, authenticate);
      Task<HttpResponseMessage> task = client.PutAsync(apiPath, content);
      var res = RequestWrap(task);
      res.Wait();
      result = res.Result.result;
      return res.Result.error;
    }

    public Task<MatrixAPIResult> PutAsync(string apiPath, bool authenticate, JToken request)
    {
      StringContent content = new StringContent(request.ToString(Formatting.None), Encoding.UTF8, "application/json");
      apiPath = GetPath(apiPath, authenticate);
      Task<HttpResponseMessage> task = client.PutAsync(apiPath, content);
      return RequestWrap(task);
    }

    public MatrixRequestError Post(string apiPath, bool authenticate, JToken data, Dictionary<string, string> headers, out JToken result)
    {
      StringContent content;
      if (data != null)
      {
        content = new StringContent(data.ToString(), Encoding.UTF8, "application/json");
      }
      else
      {
        content = new StringContent("{}");
      }

      foreach (KeyValuePair<string, string> header in headers)
      {
        content.Headers.Add(header.Key, header.Value);
      }

      apiPath = GetPath(apiPath, authenticate);
      Task<HttpResponseMessage> task = client.PostAsync(apiPath, content);
      var res = RequestWrap(task);
      res.Wait();
      result = res.Result.result;
      return res.Result.error;
    }

    public MatrixRequestError Post(string apiPath, bool authenticate, byte[] data, Dictionary<string, string> headers,
      out JToken result)
    {
      ByteArrayContent content;
      if (data != null)
      {
        content = new ByteArrayContent(data);
      }
      else
      {
        content = new ByteArrayContent(new byte[0]);
      }

      foreach (KeyValuePair<string, string> header in headers)
      {
        content.Headers.Add(header.Key, header.Value);
      }

      apiPath = GetPath(apiPath, authenticate);
      Task<HttpResponseMessage> task = client.PostAsync(apiPath, content);
      var res = RequestWrap(task);
      res.Wait();
      result = res.Result.result;
      return res.Result.error;
    }

    public MatrixRequestError Post(string apiPath, bool authenticate, JToken data, out JToken result)
    {
      return Post(apiPath, authenticate, data, new Dictionary<string, string>(), out result);
    }

    private async Task<Tuple<JToken, HttpStatusCode>> GenericRequest(Task<HttpResponseMessage> task)
    {
      Task<string> stask = null;
      JToken result = null;
      HttpResponseMessage httpResult;
      try
      {
        httpResult = await task;
        if (httpResult.StatusCode.HasFlag(HttpStatusCode.OK))
        {
          stask = httpResult.Content.ReadAsStringAsync();
        }
        else
        {
          return new Tuple<JToken, HttpStatusCode>(null, httpResult.StatusCode);
        }
      }
      catch (WebException e)
      {
        throw e;
      }
      catch (AggregateException e)
      {
        throw new MatrixException(e.InnerException.Message, e.InnerException);
      }

      string json = await stask;
      result = JToken.Parse(json);
      if (result.Type == JTokenType.Object && result["errcode"] != null)
      {
        throw new MatrixServerError(result["errcode"].ToObject<string>(), result["error"].ToObject<string>(), result as JObject);
      }
      return new Tuple<JToken, HttpStatusCode>(result, httpResult.StatusCode);
    }
  }
}


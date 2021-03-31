using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Matrix.Backends;
using Matrix.Structures;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

/**
 * This class contains all the methods needed to call the Matrix C2S API. The methods are split into files
 * inside ./Api.
 */

namespace Matrix
{
  public delegate void MatrixAPIRoomJoinedDelegate(string roomid, MatrixEventRoomJoined joined);
  public delegate void MatrixAPIRoomInviteDelegate(string roomid, MatrixEventRoomInvited invited);
  // We need to mock MatrixAPI, hence needing virtuals.
  // ReSharper disable once ClassWithVirtualMembersNeverInherited.Global
  public partial class MatrixAPI
  {
    public event MatrixAPIRoomJoinedDelegate SyncJoinEvent;
    public event MatrixAPIRoomInviteDelegate SyncInviteEvent;

    public bool IsConnected { get; private set; }
    public virtual bool RunningInitialSync { get; private set; } = true;
    public virtual string BaseURL { get; private set; }
    public int BadSyncTimeout { get; set; } = 25000;
    public virtual string UserId { get; set; }

    private MatrixVersions versions;
    private string syncToken = "";
    private ILogger log = Logger.Factory.CreateLogger<MatrixAPI>();
    private bool IsAS;
    private MatrixLoginResponse current_login;
    private Thread poll_thread;
    private bool shouldRun;
    private Random rng;
    private JSONEventConverter event_converter;
    private IMatrixAPIBackend mbackend;

    static readonly JSONSerializer matrixSerializer = new JSONSerializer();

    /// <summary>
    /// Timeout in seconds between sync requests.
    /// </summary>
    public int SyncTimeout { get; set; } = 10000;

    public MatrixAPI(string URL, System.Net.Http.HttpClientHandler handler = null)
    {
      if (!Uri.IsWellFormedUriString(URL, UriKind.Absolute))
      {
        throw new MatrixException("URL is not valid");
      }
      IsAS = false;
      mbackend = new HttpBackend(URL, handler: handler);
      BaseURL = URL;
      rng = new Random(DateTime.Now.Millisecond);
      event_converter = new JSONEventConverter();
    }

    public MatrixAPI(string URL, string application_token, string user_id)
    {
      if (!Uri.IsWellFormedUriString(URL, UriKind.Absolute))
      {
        throw new MatrixException("URL is not valid");
      }

      IsAS = true;
      mbackend = new HttpBackend(URL, user_id);
      mbackend.SetAccessToken(application_token);
      UserId = user_id;
      BaseURL = URL;
      rng = new Random(DateTime.Now.Millisecond);
      event_converter = new JSONEventConverter();
    }

    public MatrixAPI(string URL, IMatrixAPIBackend backend)
    {
      if (!Uri.IsWellFormedUriString(URL, UriKind.Absolute))
      {
        throw new MatrixException("URL is not valid");
      }

      IsAS = true;
      mbackend = backend;
      BaseURL = URL;
      rng = new Random(DateTime.Now.Millisecond);
      event_converter = new JSONEventConverter();
    }


    public void AddMessageType(string name, Type type)
    {
      event_converter.AddMessageType(name, type);
    }

    public void AddEventType(string msgtype, Type type)
    {
      event_converter.AddEventType(msgtype, type);
    }

    private void pollThread_Run()
    {
      while (shouldRun)
      {
        try
        {
          ClientSync(true);
        }
        catch (Exception e)
        {
#if DEBUG
          Console.WriteLine("[warn] A Matrix exception occured during sync!");
          Console.WriteLine(e);
#endif
        }
        Thread.Sleep(250);
      }
    }

    public void SetSyncToken(string synctoken)
    {
      syncToken = synctoken;
      RunningInitialSync = false;
    }

    public virtual string GetSyncToken()
    {
      return syncToken;
    }

    public virtual string GetAccessToken()
    {
      if (current_login != null)
      {
        return current_login.access_token;
      }

      return null;
    }

    public virtual MatrixLoginResponse GetCurrentLogin()
    {
      return current_login;
    }

    public void SetLogin(MatrixLoginResponse response)
    {
      current_login = response;
      UserId = response.user_id;
      mbackend.SetAccessToken(response.access_token);
    }

    public static JObject ObjectToJson(object data)
    {
      JObject container;
      using (JTokenWriter writer = new JTokenWriter())
      {
        try
        {
          matrixSerializer.Serialize(writer, data);
          container = (JObject)writer.Token;
        }
        catch (Exception e)
        {
          throw new Exception("Couldn't convert obj to JSON", e);
        }
      }
      return container;
    }

    public bool IsLoggedIn()
    {
      //TODO: Check token is still valid
      return current_login != null;
    }

    private void ProcessSync(MatrixSync syncData)
    {
      //Grab data from rooms the user has joined.
      foreach (KeyValuePair<string, MatrixEventRoomJoined> room in syncData.rooms.join)
      {
        SyncJoinEvent?.Invoke(room.Key, room.Value);
      }
      foreach (KeyValuePair<string, MatrixEventRoomInvited> room in syncData.rooms.invite)
      {
        SyncInviteEvent?.Invoke(room.Key, room.Value);
      }
      syncToken = syncData.next_batch;
    }

    [MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "get-matrix-client-versions")]
    public MatrixVersions ClientVersions()
    {
      MatrixRequestError error = mbackend.Get("/_matrix/client/versions", false, out var result);
      if (error.IsOk)
      {
        var res = result.ToObject<MatrixVersions>();
        versions = res;
        return res;
      }

      throw new MatrixException(error.ToString());//TODO: Need a better exception
    }

    [MatrixSpec(EMatrixSpecApiVersion.R040, EMatrixSpecApi.ClientServer,
      "get-matrix-client-r0-joined_rooms")]
    public List<string> GetJoinedRooms()
    {
      MatrixRequestError error = mbackend.Get("/_matrix/client/r0/joined_rooms", true, out var result);
      if (!error.IsOk)
      {
        throw new MatrixException(error.ToString());
      }
      return (result as JObject)?.GetValue("joined_rooms").ToObject<List<string>>();
    }

    [MatrixSpec(EMatrixSpecApiVersion.R040, EMatrixSpecApi.ClientServer,
      "get-matrix-client-r0-joined_members")]
    public Dictionary<string, MatrixProfile> GetJoinedMembers(string roomId)
    {
      MatrixRequestError error = mbackend.Get($"/_matrix/client/r0/rooms/{roomId}/joined_members", true, out var result);
      if (!error.IsOk)
      {
        throw new MatrixException(error.ToString());
      }
      return (result as JObject)?.GetValue("joined").ToObject<Dictionary<string, MatrixProfile>>();
    }

    public void RegisterUserAsAS(string user)
    {
      if (!IsAS)
      {
        throw new MatrixException("This client is not registered as a application service client. You can't create new appservice users");
      }
      JObject request = JObject.FromObject(new
      {
        type = "m.login.application_service",
        user
      });

      MatrixRequestError error = mbackend.Post("/_matrix/client/r0/register", true, request, out var result);
      if (!error.IsOk)
      {
        throw new MatrixException(error.ToString());//TODO: Need a better exception
      }
    }

    public void ThrowIfNotSupported([CallerMemberName] string name = null)
    {
      if (name == null)
      {
        return;
      }
      if (versions == null)
      {
        ClientVersions();
      }
      MatrixSpec spec = typeof(MatrixAPI).GetMethod(name).GetCustomAttribute(typeof(MatrixSpec)) as MatrixSpec;
      if (spec == null)
      {
#if DEBUG
        log.LogWarning($"{name} has no MatrixSpec attribute, cannot determine homeserver support");
#endif
        return;
      }
      // Ensure we support a version of the spec >= the min version and <= the last version.
      if (!versions.supportedVersions().Any(
        version => version >= spec.MinVersion && version <= spec.LastVersion)
      )
      {
        return;
      }
      string msg = "This homeserver doesn't support this endpoint.";
      if (spec.LastVersion != EMatrixSpecApiVersion.Unknown)
      {
        msg += $"The endpoint was removed in spec version {MatrixSpec.GetStringForVersion(spec.LastVersion)}";
      }
      else
      {
        msg += $"The endpoint was added in spec version {MatrixSpec.GetStringForVersion(spec.MinVersion)}";
      }
      throw new MatrixException(msg);
    }
  }
}

﻿using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Linq;
using System.IO;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Matrix.Structures;
using Matrix.Backends;
namespace Matrix
{
    public delegate void MatrixAPIRoomJoinedDelegate(string roomid, MatrixEventRoomJoined joined);
    public delegate void MatrixAPIRoomInviteDelegate(string roomid, MatrixEventRoomInvited invited);
	public class MatrixAPI
	{
		public const string VERSION = "r0.0.1";
		public bool IsConnected { get; private set; }
		public virtual bool RunningInitialSync { get; private set; } = true;
		public virtual string BaseURL  { get; private set; }
		public int BadSyncTimeout { get; set; } = 25000;
		public int FailMessageAfter { get; set; } = 300;
		public virtual string user_id {get; set;} = null;

		string syncToken = "";
		bool IsAS;

		MatrixLoginResponse current_login = null;
		Thread poll_thread;
		bool shouldRun = false;
		ConcurrentQueue<MatrixAPIPendingEvent> pendingMessages  = new ConcurrentQueue<MatrixAPIPendingEvent> ();
		Random rng;

		static JSONSerializer matrixSerializer = new JSONSerializer ();

		JSONEventConverter event_converter;

		IMatrixAPIBackend mbackend;


        public event MatrixAPIRoomJoinedDelegate SyncJoinEvent;
        public event MatrixAPIRoomInviteDelegate SyncInviteEvent;

		/// <summary>
		/// Timeout in seconds between sync requests.
		/// </summary>
		public int SyncTimeout {get;set;} = 10000;

		public MatrixAPI (string URL)
		{
			if (!Uri.IsWellFormedUriString (URL, UriKind.Absolute)) {
				throw new MatrixException ("URL is not valid");
			}

			IsAS = false;
			mbackend = new HttpBackend (URL);
			BaseURL = URL;
			rng = new Random (DateTime.Now.Millisecond);
			event_converter = new JSONEventConverter ();
		}

		public MatrixAPI(string URL, string application_token, string user_id){
			if (!Uri.IsWellFormedUriString (URL, UriKind.Absolute)) {
				throw new MatrixException("URL is not valid");
			}

			IsAS = true;
			mbackend = new HttpBackend (URL,user_id);
			mbackend.SetAccessToken(application_token);
			this.user_id = user_id;
			BaseURL = URL;
			rng = new Random (DateTime.Now.Millisecond);
			event_converter = new JSONEventConverter ();
		}

		public MatrixAPI (string URL, IMatrixAPIBackend backend)
		{
			if (!Uri.IsWellFormedUriString (URL, UriKind.Absolute)) {
				throw new MatrixException("URL is not valid");
			}

			IsAS = true;
			mbackend = backend;
			BaseURL = URL;
			rng = new Random (DateTime.Now.Millisecond);
			event_converter = new JSONEventConverter ();
		}


		public void AddMessageType (string name, Type type)
		{
			event_converter.AddMessageType(name,type);
		}

		public void AddEventType (string msgtype, Type type)
		{
			event_converter.AddEventType(msgtype, type);
		}

        public void FlushMessageQueue ()
		{
			MatrixAPIPendingEvent evt;
			MatrixRequestError error;
			while (pendingMessages.TryDequeue (out evt)) {
				error = sendRoomMessage (evt);
				if (!error.IsOk) {

					if (error.MatrixErrorCode != MatrixErrorCode.M_UNKNOWN) { //M_UNKNOWN unoffically means it failed to validate.
						Console.WriteLine("Trying to resend failed message of type " + evt.type);
						evt.backoff_duration += evt.backoff;
						evt.backoff = evt.backoff == 0 ? 2: (int)Math.Pow(evt.backoff,2);
						if (evt.backoff_duration > FailMessageAfter) {
							evt.backoff = 0;
							continue; //Give up trying to send
						}

						Console.WriteLine($"Waiting {evt.backoff} seconds before resending");

						Thread.Sleep(evt.backoff*1000);
						pendingMessages.Enqueue (evt);

					}
				}
			}
        }

		private void pollThread_Run(){
			while (shouldRun) {
				try
				{
					ClientSync (true);
				}
				catch(Exception e){
					#if DEBUG
					Console.WriteLine ("[warn] A Matrix exception occured during sync!");
					Console.WriteLine (e);
					#endif
				}
                FlushMessageQueue();
				Thread.Sleep(250);
			}
		}

		public void SetSyncToken(string synctoken){
			syncToken = synctoken;
			RunningInitialSync = false;
		}

		public virtual string GetSyncToken(){
			return syncToken;
		}

		public virtual string GetAccessToken ()
		{
			if (current_login != null) {
				return current_login.access_token;
			}
			else
			{
				return null;
			}
		}

		public virtual MatrixLoginResponse GetCurrentLogin ()
		{
			return current_login;
		}

		public void SetLogin(MatrixLoginResponse response){
			current_login = response;
			user_id = response.user_id;
			mbackend.SetAccessToken(response.access_token);
		}

		public void ClientTokenRefresh(string refreshToken){
			JObject request = new JObject ();
			request.Add ("refresh_token", refreshToken);
			JObject response;
			MatrixRequestError error = mbackend.Post ("/_matrix/r0/tokenrefresh", true, request,out response);
			if (!error.IsOk) {
				throw new MatrixServerError (error.MatrixErrorCode.ToString(), error.MatrixError);
			}
		}

		public void StartSyncThreads(){
			if (poll_thread == null) {
				poll_thread = new Thread (pollThread_Run);
				poll_thread.Start ();
				shouldRun = true;
			} else {
				if (poll_thread.IsAlive) {
					throw new Exception ("Can't start thread, already running");
				}
				poll_thread.Start ();
			}

		}

		public void StopSyncThreads(){
			shouldRun = false;
			poll_thread.Join ();
			FlushMessageQueue();
		}

		public static JObject ObjectToJson (object data)
		{
			JObject container;
			using (JTokenWriter writer = new JTokenWriter ()) {
				try {
					matrixSerializer.Serialize (writer, data);
					container = (JObject)writer.Token;
				} catch (Exception e) {
					throw new Exception("Couldn't convert obj to JSON",e);
				}
			}
			return container;
		}

		public static bool IsVersionSupported(string[] version){
			return (new List<string> (version).Contains (VERSION));//TODO: Support version checking properly.
		}

		public bool IsLoggedIn(){
			//TODO: Check token is still valid
			return current_login != null;
		}

		private void ProcessSync(MatrixSync syncData){
			syncToken = syncData.next_batch;
			//Grab data from rooms the user has joined.
			foreach (KeyValuePair<string,MatrixEventRoomJoined> room in syncData.rooms.join)
			{
				SyncJoinEvent?.Invoke (room.Key, room.Value);
			}
            foreach (KeyValuePair<string,MatrixEventRoomInvited> room in syncData.rooms.invite)
            {
	            SyncInviteEvent?.Invoke (room.Key, room.Value);
            }

		}

		private MatrixRequestError sendRoomMessage (MatrixAPIPendingEvent msg)
		{
			JObject msgData = ObjectToJson (msg.content);
			JObject result;
			MatrixRequestError error = mbackend.Put (String.Format ("/_matrix/client/r0/rooms/{0}/send/{1}/{2}", System.Uri.EscapeDataString (msg.room_id), msg.type, msg.txnId), true, msgData, out result);

			#if DEBUG
			if(!error.IsOk){
				Console.WriteLine (error.GetErrorString());
			}
			#endif
			return error;
		}

		[MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "post-matrix-client-r0-login")]
		public MatrixLoginResponse ClientLogin(MatrixLogin login) {
			JObject result;
			MatrixRequestError error = mbackend.Post ("/_matrix/client/r0/login",false,JObject.FromObject(login),out result);
			if (error.IsOk) {
				return result.ToObject<MatrixLoginResponse> ();
			}
			throw new MatrixException (error.ToString());//TODO: Need a better exception
		}

		[MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "get-matrix-client-r0-profile-userid")]
		public virtual MatrixProfile ClientProfile(string userid){
			JObject response;
			MatrixRequestError error = mbackend.Get ("/_matrix/client/r0/profile/" + userid,true, out response);
			if (error.IsOk) {
				return response.ToObject<MatrixProfile> ();
			} else {
				return null;
			}
		}

		[MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "get-matrix-client-r0-profile-displayname")]
		public void ClientSetDisplayName(string userid,string displayname){
			JObject response;
			JObject request = new JObject();
			request.Add("displayname",JToken.FromObject(displayname));
			MatrixRequestError error = mbackend.Put (string.Format("/_matrix/client/r0/profile/{0}/displayname",Uri.EscapeUriString(userid)),true,request, out response);
			if (!error.IsOk) {
				throw new MatrixException (error.ToString());//TODO: Need a better exception
			}
		}

		[MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "get-matrix-client-r0-profile-userid-displayname")]
		public void ClientSetAvatar(string userid,string avatar_url){
			JObject response;
			JObject request = new JObject();
			request.Add("avatar_url",JToken.FromObject(avatar_url));
			MatrixRequestError error = mbackend.Put (string.Format("/_matrix/client/r0/profile/{0}/avatar_url",Uri.EscapeUriString(userid)),true,request, out response);
			if (!error.IsOk) {
				throw new MatrixException (error.ToString());//TODO: Need a better exception
			}
		}

		[MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "get-matrix-client-r0-sync")]
		public void ClientSync(bool ConnectionFailureTimeout = false){
			JObject response;
			string url = "/_matrix/client/r0/sync?timeout="+SyncTimeout;
			if (!String.IsNullOrEmpty(syncToken)) {
				url += "&since=" + syncToken;
			}
			MatrixRequestError error = mbackend.Get (url,true, out response);
			if (error.IsOk) {
				try {
					MatrixSync sync = JsonConvert.DeserializeObject<MatrixSync> (response.ToString (), event_converter);
					ProcessSync (sync);
					IsConnected = true;
				} catch (Exception e) {
					Console.WriteLine(e.InnerException);
					throw new MatrixException ("Could not decode sync", e);
				}
			} else if (ConnectionFailureTimeout) {
				IsConnected = false;
				Console.Error.WriteLine ("Couldn't reach the matrix home server during a sync.");
				Console.Error.WriteLine(error.ToString());
				Thread.Sleep (BadSyncTimeout);
			}
			if (RunningInitialSync)
				RunningInitialSync = false;
		}

		[MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "get-matrix-client-versions")]
		public string[] ClientVersions(){
			JObject result;
			MatrixRequestError error = mbackend.Get ("/_matrix/client/versions",false, out result);
			if (error.IsOk) {
				return result.GetValue ("versions").ToObject<string[]> ();
			} else {
				throw new MatrixException ("Non OK result returned from request");//TODO: Need a better exception
			}
		}

		[MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "post-matrix-client-r0-rooms-roomid-join")]
		public string ClientJoin(string roomid){
			JObject result;
			MatrixRequestError error = mbackend.Post(String.Format("/_matrix/client/r0/join/{0}",System.Uri.EscapeDataString(roomid)),true,null,out result);
			if (error.IsOk) {
				roomid = result ["room_id"].ToObject<string> ();
				return roomid;
			} else {
				return null;
			}

		}

		[MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "post-matrix-client-r0-rooms-roomid-leave")]
		public void RoomLeave(string roomid){
			JObject result;
			MatrixRequestError error = mbackend.Post(String.Format("/_matrix/client/r0/rooms/{0}/leave",System.Uri.EscapeDataString(roomid)),true,null,out result);
			if (!error.IsOk) {
				throw new MatrixException (error.ToString ());
			}
		}

		[MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "post-matrix-client-r0-createroom")]
		public string ClientCreateRoom(MatrixCreateRoom roomrequest = null){
			JObject result;
			JObject req = null;
			if (roomrequest != null) {
				req = ObjectToJson(roomrequest);
			}
			MatrixRequestError error = mbackend.Post ("/_matrix/client/r0/createRoom", true, req, out result);
			if (error.IsOk) {
				string roomid = result ["room_id"].ToObject<string> ();
				return roomid;
			} else {
				return null;
			}
		}

		[MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "put-matrix-client-r0-rooms-roomid-state-eventtype")]
		public virtual void RoomStateSend(string roomid,string type,MatrixRoomStateEvent message,string key = ""){
			JObject msgData = ObjectToJson (message);
			JObject result;
			MatrixRequestError error = mbackend.Put (String.Format ("/_matrix/client/r0/rooms/{0}/state/{1}/{2}", System.Uri.EscapeDataString(roomid),type,key), true, msgData,out result);
			if (!error.IsOk) {
				throw new MatrixException (error.ToString());//TODO: Need a better exception
			}
		}

		[MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "post-matrix-client-r0-rooms-roomid-invite")]
		public void InviteToRoom(string roomid, string userid){
			JObject result;
			JObject msgData = JObject.FromObject(new {user_id=userid});
			MatrixRequestError error = mbackend.Post (String.Format ("/_matrix/client/r0/rooms/{0}/invite", System.Uri.EscapeDataString(roomid)), true, msgData,out result);
			if (!error.IsOk) {
				throw new MatrixException (error.ToString());//TODO: Need a better exception
			}
		}

		[MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "put-matrix-client-r0-rooms-roomid-send-eventtype-txnid")]
		public void RoomMessageSend (string roomid, string type, MatrixMRoomMessage message)
		{
			bool collision = true;
			MatrixAPIPendingEvent evt = new MatrixAPIPendingEvent ();
			evt.room_id = roomid;
			evt.type = type;
			evt.content = message;
			if (((MatrixMRoomMessage)evt.content).body == null) {
				throw new Exception("Missing body in message");
			}
			while (collision) {
				evt.txnId = rng.Next (1, 64);
				collision = pendingMessages.FirstOrDefault (x => x.txnId == evt.txnId) != default(MatrixAPIPendingEvent);
			}
			pendingMessages.Enqueue (evt);
			if (IsAS) {
				FlushMessageQueue();
			}
		}

		[MatrixSpec(EMatrixSpecApiVersion.R001, EMatrixSpecApi.ClientServer, "post-matrix-client-r0-rooms-roomid-receipt-receipttype-eventid")]
		public void RoomTypingSend (string roomid, bool typing, int timeout = 0)
		{
			JObject msgData;
			if (timeout == 0) {
				msgData = JObject.FromObject (new {typing = typing});
			} else {
				msgData = JObject.FromObject(new {typing=typing,timeout=timeout});
			}
			MatrixRequestError error = mbackend.Put (
				$"/_matrix/client/r0/rooms/{System.Uri.EscapeDataString(roomid)}/typing/{System.Uri.EscapeDataString(user_id)}", true, msgData,out _);
			if (!error.IsOk) {
				throw new MatrixException (error.ToString());
			}
		}

		[MatrixSpec(EMatrixSpecApiVersion.R040, EMatrixSpecApi.ClientServer,
			"get-matrix-client-r0-user-userid-rooms-roomid-tags")]
		public RoomTags RoomGetTags(string roomid)
		{
			MatrixRequestError error = mbackend.Get($"/_matrix/client/r0/user/{user_id}/rooms/{roomid}/tags", true, out var result);
			if (!error.IsOk) {
				throw new MatrixException (error.ToString());
			}
			return result.ToObject<RoomTags>();
		}

		public PublicRooms PublicRooms(int limit, string since, string server)
		{
			var qs = HttpUtility.ParseQueryString(string.Empty);
			if (limit != 0)
				qs.Set("limit", limit.ToString());
			if (since != "")
				qs.Set("since", since);
			if (server != "")
				qs.Set("server", server);
			MatrixRequestError error = mbackend.Get($"/_matrix/client/r0/publicRooms?{qs}", true, out var result);
			if (!error.IsOk) {
				throw new MatrixException (error.ToString());
			}
			return result.ToObject<PublicRooms>();
		}
		
		public void DeleteFromRoomDirectory(string alias)
		{
			MatrixRequestError error = mbackend.Delete($"/_matrix/client/r0/directory/room/{alias}", true, out var _);
			if (!error.IsOk) {
				throw new MatrixException (error.ToString());
			}
		}

		public string MediaUpload(string contentType,byte[] data)
		{
			MatrixRequestError error = mbackend.Post("/_matrix/media/r0/upload",true,data,new Dictionary<string,string>(){{"Content-Type",contentType}},out var result);
			if (!error.IsOk) {
				throw new MatrixException (error.ToString());//TODO: Need a better exception
			}
			return result.GetValue("content_uri").ToObject<string>();
		}

		public void RegisterUserAsAS (string user)
		{
			if(!IsAS){
				throw new MatrixException("This client is not registered as a application service client. You can't create new appservice users");
			}
			JObject request = JObject.FromObject( new {
				type = "m.login.application_service",
				user = user
			});

			JObject result = new JObject();

			MatrixRequestError error = mbackend.Post("/_matrix/client/r0/register",true,request,out result);
			if (!error.IsOk) {
				throw new MatrixException (error.ToString());//TODO: Need a better exception
			}
		}
	}

	public class MatrixAPIPendingEvent : MatrixEvent{
		public int txnId;
		public int backoff = 0;
		public int backoff_duration = 0;
	}
}

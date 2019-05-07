using System;
using System.Collections.Concurrent;
using System.Linq;
using Matrix.Structures;
using Microsoft.Extensions.Logging;

namespace Matrix.Client
{
    /// <summary>
    /// The Matrix Client is a wrapper over the MatrixAPI object which provides a safe managed way
    /// to interact with a Matrix Home Server.
    /// </summary>
    public class MatrixClient : IDisposable
    {
        private ILogger log = Logger.Factory.CreateLogger<MatrixClient>();

        public delegate void MatrixInviteDelegate(string roomid, MatrixEventRoomInvited joined);

        /// <summary>
        /// How long to poll for a Sync request before we retry.
        /// </summary>
        /// <value>The sync timeout in milliseconds.</value>
        public int SyncTimeout { get => Api.SyncTimeout;
            set{ Api.SyncTimeout = value; } }

        readonly ConcurrentDictionary<string,MatrixRoom> _rooms	= new ConcurrentDictionary<string,MatrixRoom>();

        public event MatrixInviteDelegate OnInvite;

        public string UserId => Api.UserId;

        /// <summary>
        /// Get the underlying API that MatrixClient wraps. Here be dragons 🐲.
        /// </summary>
        public MatrixAPI Api { get; }
        
        public Client.Keys Keys { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Matrix.Client.MatrixClient"/> class.
        /// The client will preform a connection and try to retrieve version information.
        /// If this fails, a MatrixUnsuccessfulConnection Exception will be thrown.
        /// </summary>
        /// <param name="URL">URL before /_matrix/</param>
        public MatrixClient (string URL)
        {
            log.LogDebug($"Created new MatrixClient instance for {URL}");
            Api = new MatrixAPI (URL);
            Keys = new Client.Keys(Api);
            try{
                Api.ClientVersions ();
                Api.SyncJoinEvent += MatrixClient_OnEvent;
                Api.SyncInviteEvent += MatrixClient_OnInvite;
            }
            catch(MatrixException e){
                throw new MatrixException("An exception occured while trying to connect",e);
            }
        }


        /// <summary>
        /// Initializes a new instance of the <see cref="Matrix.Client.MatrixClient"/> class.
        /// This intended for Application Services only who want to preform actions as another user.
        /// Sync is not preformed.
        /// </summary>
        /// <param name="URL">URL before /_matrix/</param>
        /// <param name="application_token">Application token for the AS.</param>
        /// <param name="userid">Userid as the user you intend to go as.</param>
        public MatrixClient (string URL, string application_token, string userid)
        {
            Api = new MatrixAPI (URL,application_token, userid);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Matrix.Client.MatrixClient"/> class for testing.
        /// </summary>
        public MatrixClient (MatrixAPI api){
            log.LogDebug("ctor: baseurl={}", api.BaseURL);
            this.Api = api;
            api.SyncJoinEvent += MatrixClient_OnEvent;
            api.SyncInviteEvent += MatrixClient_OnInvite;
        }

        /// <summary>
        /// Gets the sync token from the API.
        /// </summary>
        /// <returns>The sync token.</returns>
        public string GetSyncToken() {
            return Api.GetSyncToken ();
        }

        /// <summary>
        /// Gets the access token from the API.
        /// </summary>
        /// <returns>The access token.</returns>
        public string GetAccessToken ()
        {
            return Api.GetAccessToken();
        }

        public MatrixLoginResponse GetCurrentLogin ()
        {
            return Api.GetCurrentLogin();
        }

        private void MatrixClient_OnInvite(string roomid, MatrixEventRoomInvited joined){
            if(OnInvite != null){
                OnInvite.Invoke(roomid,joined);
            }
        }

        private void MatrixClient_OnEvent (string roomid, MatrixEventRoomJoined joined)
        {
            MatrixRoom mroom;
            if (!_rooms.ContainsKey (roomid)) {
                mroom = new MatrixRoom (Api, roomid);
                _rooms.TryAdd (roomid, mroom);
                //Update existing room
            } else {
                mroom = _rooms [roomid];
            }
            joined.state.events.ToList ().ForEach (x => {mroom.FeedEvent (x);});
            joined.timeline.events.ToList ().ForEach (x => {mroom.FeedEvent (x);});
            mroom.SetEphemeral(joined.ephemeral);
        }

        /// <summary>
        /// Login with a given username and password.
        /// Currently, this is the only login method the SDK supports.
        /// </summary>
        /// <param name="username">Username</param>
        /// <param name="password">Password</param>
        public MatrixLoginResponse LoginWithPassword(string username, string password, string device_id = null){
            var result = Api.ClientLogin (new MatrixLoginPassword (username, password, device_id));
            Api.SetLogin(result);
            return result;
        }

        /// <param name="syncToken"> If you stored the sync token before, you can set it for the API here</param>
        public void StartSync(string syncToken = "")
        {
            Api.SetSyncToken(syncToken);
            Api.ClientSync ();
            Api.StartSyncThreads ();
        }

        /// <summary>
        /// Login with a given username and token.
        /// This method will also start a sync with the server
        /// Currently, this is the only login method the SDK supports.
        /// </summary>
        /// <param name="username">Username</param>
        /// <param name="token">Access Token</param>
        public void LoginWithToken (string username, string token)
        {
            Api.ClientLogin (new MatrixLoginToken (username, token));
            Api.ClientSync ();
            Api.StartSyncThreads ();
        }

        /// <summary>
        /// Use existing login information when connecting to Matrix.
        /// </summary>
        /// <param name="user_id">Full Matrix user id.</param>
        /// <param name="access_token">Access token.</param>
        /// <param name="refresh_token">Refresh token.</param>
        public void UseExistingToken (string user_id, string access_token)
        {
            Api.SetLogin(new MatrixLoginResponse
            {
                user_id = user_id,
                access_token = access_token,
                home_server = Api.BaseURL
            });
        }


        /// <summary>
        /// Get information about a user from the server.
        /// </summary>
        /// <returns>A MatrixUser object</returns>
        /// <param name="userid">User ID</param>
        public MatrixUser GetUser(string userid = null){
            userid = userid == null ? Api.UserId : userid;
            MatrixProfile profile = Api.ClientProfile (userid);
            if (profile != null) {
                return new MatrixUser (profile, userid);
            }
            return null;
        }

        public void SetDisplayName (string displayname)
        {
            Api.ClientSetDisplayName(Api.UserId, displayname);
        }

        public void SetAvatar (string avatar)
        {
            Api.ClientSetAvatar(Api.UserId, avatar);
        }

        /// <summary>
        /// Get all the Rooms that the user has joined.
        /// </summary>
        /// <returns>Array of MatrixRooms</returns>
        public MatrixRoom[] GetAllRooms(){
            return _rooms.Values.ToArray ();
        }

        /// <summary>
        /// Creates a new room with the specified details, or a blank one otherwise.
        /// </summary>
        /// <returns>A MatrixRoom object</returns>
        /// <param name="roomdetails">Optional set of options to send to the server.</param>
        public MatrixRoom CreateRoom(MatrixCreateRoom roomdetails = null){
            string roomid = Api.ClientCreateRoom (roomdetails);
            if (roomid != null) {
                MatrixRoom room = JoinRoom(roomid);
                return room;
            }
            return null;
        }

        /// <summary>
        /// Alias for <see cref="Matrix.MatrixClient.CreateRoom"/> which lets you set common items before creation.
        /// </summary>
        /// <returns>A MatrixRoom object</returns>
        /// <param name="name">The room name.</param>
        /// <param name="alias">The primary alias</param>
        /// <param name="topic">The room topic</param>
        public MatrixRoom CreateRoom(string name, string alias = null,string topic = null){
            MatrixCreateRoom room = new MatrixCreateRoom ();
            room.name = name;
            room.room_alias_name = alias;
            room.topic = topic;
            return CreateRoom (room);
        }

        /// <summary>
        /// Join a matrix room. If the user has already joined this room, do nothing.
        /// </summary>
        /// <returns>The room.</returns>
        /// <param name="roomid">roomid or alias</param>
        public MatrixRoom JoinRoom(string roomid){//TODO: Maybe add a try method.
            if (!_rooms.ContainsKey (roomid)) {//TODO: Check the status of the room too.
                roomid = Api.ClientJoin (roomid);
                if(roomid == null){
                    return null;
                }
                MatrixRoom room = new MatrixRoom (Api, roomid);
                _rooms.TryAdd (room.ID, room);
            }
            return _rooms [roomid];
        }

        public MatrixMediaFile UploadFile(string contentType,byte[] data){
            string url = Api.MediaUpload(contentType,data);
            return new MatrixMediaFile(Api,url,contentType);
        }

        /// <summary>
        /// Return a joined room object by it's roomid.
        /// </summary>
        /// <returns>The room.</returns>
        /// <param name="roomid">Roomid.</param>
        public MatrixRoom GetRoom(string roomid) {//TODO: Maybe add a try method.
            MatrixRoom room = null;
            _rooms.TryGetValue(roomid,out room);
            if (room == null)
            {
                log.LogInformation($"Don't have {roomid} synced, getting the room from /state");
                // If we don't have the room, attempt to grab it's state.
                var state = Api.GetRoomState(roomid);
                room = new MatrixRoom(Api, roomid);
                foreach (var matrixEvent in state)
                {
                    room.FeedEvent(matrixEvent);
                }
                _rooms.TryAdd(roomid, room);
            }
            return room;
        }
        /// <summary>
        /// Get a room object by any of it's registered aliases.
        /// </summary>
        /// <returns>The room by alias.</returns>
        /// <param name="alias">CanonicalAlias or any Alias</param>
        public MatrixRoom GetRoomByAlias(string alias){
            MatrixRoom room = _rooms.Values.FirstOrDefault( x => {
                if(x.CanonicalAlias == alias){
                    return true;
                }

                if(x.Aliases != null){
                    return x.Aliases.Contains(alias);
                }
                return false;
            });
            if (room != default(MatrixRoom)) {
                return room;
            }

            return null;
        }

        /// <summary>
        /// Add a new type of message to be decoded during sync operations.
        /// </summary>
        /// <param name="msgtype">msgtype.</param>
        /// <param name="type">Type that inheritis MatrixMRoomMessage</param>
        public void AddRoomMessageType (string msgtype, Type type)
        {
            Api.AddMessageType(msgtype, type);
        }

        /// <summary>
        /// Add a new type of state event to be decoded during sync operations.
        /// </summary>
        /// <param name="msgtype">msgtype.</param>
        /// <param name="type">Type that inheritis MatrixMRoomMessage</param>
        public void AddStateEventType (string msgtype, Type type)
        {
            Api.AddEventType(msgtype, type);
        }

        public PublicRooms GetPublicRooms(int limit = 0, string since = "", string server = "")
        {
            return Api.PublicRooms(limit, since, server);
        }

        public void DeleteFromRoomDirectory(string alias)
        {
            Api.DeleteFromRoomDirectory(alias);
        }
 
        /// <summary>
        /// Releases all resource used by the <see cref="Matrix.Client.MatrixClient"/> object.
        /// In addition, this will stop the sync thread.
        /// </summary>
        /// <remarks>Call <see cref="Dispose"/> when you are finished using the <see cref="Matrix.Client.MatrixClient"/>. The
        /// <see cref="Dispose"/> method leaves the <see cref="Matrix.Client.MatrixClient"/> in an unusable state. After
        /// calling <see cref="Dispose"/>, you must release all references to the <see cref="Matrix.Client.MatrixClient"/>
        /// so the garbage collector can reclaim the memory that the <see cref="Matrix.Client.MatrixClient"/> was occupying.</remarks>
        public void Dispose(){
            Api.StopSyncThreads ();
        }
    }
}

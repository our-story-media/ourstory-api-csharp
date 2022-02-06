/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using Bootleg.API.Exceptions;
using Bootleg.API.Model;
using GcmSharp.Serialization;
using Ionic.Zip;
using Microsoft.AppCenter.Analytics;
using Newtonsoft.Json;
using RestSharp;
using RestSharp.Serializers;
//using Rssdp;
#if SOCKETS
using SocketIOClient;
#endif
using SQLite;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bootleg.API
{
    public sealed class Bootlegger
    {
        #region Constants
        public static bool DEBUGMODE = false;

        internal void SaveMedia(MediaItem media)
        {
            lock (database)
            {
                database.Update(media);
            }

            media.FireProgress();
        }

        internal static string API_KEY = "REPLACEME";
        private const int API_VERSION = 13;
        private const bool DIRECT_S3_UPLOAD = true;
        const int TIME_SINCE_RECORDED = 10;
        const int TIME_SINCE_MESSAGE = 5;
        const int TIME_CHECK_INTERVAL = 5;
        const int DEFAULT_WANTED_CLIPS = 6;
        const string DEFAULT_EVENT_INFO = "<body style=\"background:black\"><p style=\"color:silver; text-align:center;\">no instructions are available</p></body>";
        static bool INCLUDE_ALL_UPLOADS = false;


        public enum Platform { iOS, Android };
        public enum JoinStatus { JOINED, NOTLIVE, ERROR };
        public enum BootleggerNotificationType { CrewReminder, ShootUpdated, RoleUpdated, PhaseChanged, GoingLive };
        public enum BootleggerEditStatus { Draft, InProgress, Complete};
        public enum MediaItemFilterType { STARS, CONTRIBUTOR, DATE, SHOT, ROLE, LENGTH, PHASE, TOPIC };
        public enum MediaItemFilterDirection { ASCENDING, DESCENDING };

        #endregion

        #region Util Functions

        public int GetNumUploadsForShoot(Shoot item)
        {
            lock (UploadQueue)
            {
                return (from n in UploadQueue where n.Static_Meta[MetaDataFields.EventId] == item.id select n).Count();
            }
        }

        public static void FireDebug(string message)
        {
            OnDebugMessage?.Invoke(message);
        }

        public void UploadFileEx(MediaItem media, string url, string fileFormName, string contenttype, NameValueCollection querystring, CookieContainer cookies)
        {
#if DEBUG
            //Thread.Sleep(100000);
#endif

            var targetfilename = DateTime.Now.Ticks + "_" + new FileInfo(media.Filename).Name;
            //RestClient client = new RestClient();
            RestRequest request = new RestRequest($"api/media/signupload/{media.event_id}");
            request.AddQueryParameter("filename", targetfilename);
            request.AddQueryParameter("apikey", API_KEY);
            //request.AddHeader("Cookie", originalcookie.ToString());
            client.CookieContainer = session;
            var result = client.Execute(request);
            if (result.StatusCode != HttpStatusCode.OK)
                throw new Exception();
            Hashtable signed;
            try
            {
                signed = JsonConvert.DeserializeObject<Hashtable>(result.Content);
            }
            catch
            {
                throw new BadPasswordException();
            }

            //DEBUG
            //throw new Exception();

            if (signed.ContainsKey("signed_request"))
            {
                var thefile = new FileInfo(media.Filename);
                HttpWebRequest webrequest = (HttpWebRequest)WebRequest.Create(signed["signed_request"].ToString());
                webrequest.CookieContainer = cookies;
                webrequest.Method = "PUT";
                //webrequest.ContentType = "video/mp4";
                //webrequest.Headers.Add("x-amz-acl", "public-read");

                FileStream fileStream = new FileStream(media.Filename, FileMode.Open, FileAccess.Read);
                long length = fileStream.Length;
                webrequest.ContentLength = length;
                webrequest.AllowWriteStreamBuffering = false;

                webrequest.ReadWriteTimeout = (int)TimeSpan.FromSeconds(5).TotalMilliseconds;
                Stream requestStream = webrequest.GetRequestStream();
                // Write out the file contents
                byte[] buffer = new Byte[checked((uint)Math.Min(4096, (int)fileStream.Length))];
                int bytesRead = 0;
                int total = 0;
                int last = 0;
                int current = 0;
                while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    requestStream.Write(buffer, 0, bytesRead);
                    total += bytesRead;

                    //update progress
                    current = (int)(((double)total / (double)webrequest.ContentLength) * 100);
                    if (last < current)
                    {
                        last = current;
                        media.Progress = current;
                        media.FileSize = (int)(total / (1024 * 1024)) + "/" + (int)(fileStream.Length / (1024 * 1024)) + "MB";
                        FireGlobalUploadProgress(media.Progress / 100.0);
                        media.FireProgress();
                        //Console.WriteLine(total);
                    }

                    //check for cancelled
                    if (!CanUpload)
                    {
                        throw new Exception("Cancelled");
                    }
                }

                media.Progress = 100;
                WebResponse responce = webrequest.GetResponse();
                Stream s = responce.GetResponseStream();
                StreamReader sr = new StreamReader(s);
                string res = sr.ReadToEnd();
                try
                {
                    request = new RestRequest("api/media/uploadcomplete/" + media.id + "?apikey=" + API_KEY, Method.POST);
                    request.AddHeader("Cookie", originalcookie.ToString());
                    request.AddParameter("filename", targetfilename);
                    client.CookieContainer = session;
                    result = client.Execute(request);
                    if (result.StatusCode != HttpStatusCode.OK)
                        throw new Exception();
                }
                catch (Exception e)
                {
                    throw e;
                }

            }
            else
            {
                throw new Exception();
            }
        }

        #endregion

        #region Fields

        //public string CustomAppBuildId { get; private set; }

        private Cookie originalcookie;

        public string Language { set; internal get; } = "en";

        RestClient client;
        private DateTime lastmessage;
        private Dictionary<TimeSpan, string> CurrentTimedMeta = new Dictionary<TimeSpan, string>();
        Thread metaupload;
        bool doingimages = false;
        object ZipLock = new object();

        private string cachedir;

        private SQLiteConnection database;

        private bool loadingmymedia;

        //SINGLETON:
        private static Bootlegger _client;

        private DirectoryInfo storagelocation;

        private Uri mainuri;

#if SOCKETS
        private SocketIOClient.Client socket;
#endif
        private SailsSocket sails;
        //stores the session cookie...
        private CookieContainer session = new CookieContainer();


        private bool _canupload;
        private int _uploadcurrentcount;
        private int _uploadlastcount;

        Task upload_worker;

        #endregion private vars

        #region Properties

        //public List<Shoot> Nearby { get; set; }

        public List<MediaItem> UploadQueue { get; set; }

        public List<OfflineSelection> EventCache = new List<OfflineSelection>();

        public Cookie SessionCookie
        {
            get { return originalcookie; }
            set
            {
                if (value != null)
                {
                    session = new CookieContainer();
                    originalcookie = value;
                    try
                    {
                        session.Add(value);
                    }
                    catch (Exception)
                    {

                    }
                }
            }
        }

        public bool Connected { get; private set; }

        public bool IsRecording { get; set; }

        public bool InBackground { get; set; }

        public string StorageLocation { get { return storagelocation.FullName; } }

        public Uri ValidLoginUri { get; set; }

        public int CameraGap { get; set; }

        public int TotalWaitingTime { get; set; }

        public string PushID { get; set; }

        public bool OfflineMode { get; set; }

        public string DefaultInstructions
        {
            get
            {
                return DEFAULT_EVENT_INFO;
            }
        }

        public static Bootlegger BootleggerClient
        {
            get
            {
                if (_client == null)
                    throw new NullReferenceException("No Bootlegger clients have been initialised");
                else
                    return _client;
            }
        }

        /// <summary>
        /// Google API Project Number - Get from Google Devloper Console
        /// </summary>
        public static string SenderID { get; set; }

        //[Obsolete("Not Currently Used")]
        //public bool ProMode
        //{
        //    get
        //    {
        //        return promode;
        //    }
        //    set
        //    {
        //        promode = value;
        //        if (OnSavePreferences != null)
        //            OnSavePreferences("promode", value.ToString());
        //    }
        //}

        public string server { get; private set; }
        public int port { get; private set; }
        public List<Shoot> MyEvents { get; private set; }

        /// <summary>
        /// Object with the Current Connected Shoot
        /// </summary>
        public Shoot CurrentEvent
        {
            get;
            private set;
        }

        /// <summary>
        /// Current cached or connected user of the API
        /// </summary>
        public User CurrentUser { get; private set; }
        /// <summary>
        /// Current user camera role
        /// </summary>
        public Role CurrentClientRole { get; set; }
        /// <summary>
        /// Current user selected shot
        /// </summary>
        public Shot CurrentClientShotType { get; private set; }
        /// <summary>
        /// Current shot server has asked for, and thinks user is delivering
        /// </summary>
        public Shot CurrentServerShotType { get; private set; }
        /// <summary>
        /// Returns limited list of media items (20) which can be used as a summary for a shoot
        /// </summary>
        /// <param name="for_event"></param>
        /// <returns></returns>
        public List<MediaItem> GetSampleMediaForEvent(Shoot for_event)
        {
            lock (database)
                return (from n in database.Table<MediaItem>() where n.event_id == for_event.id && n.Status == MediaItem.MediaStatus.DONE && n.Thumb != null select n).Take(1).ToList();
        }

        [Obsolete("Not Currently Used")]
        public bool IsLocalServer { get; private set; }

        /// <summary>
        /// Set or clear this flag to enable background uploading of videos. Thubmails and meta-data are already uploading when there is a live connection.
        /// OnCurrentUploadsComplete and OnGlobalUploadProgress are fired during uploads.
        /// </summary>
        public bool CanUpload
        {
            get
            {
                return _canupload;
            }
            set
            {
                _canupload = value;
                if (value)
                {
                    //cancel any background meta-data uploading
                    if (metaupload != null)
                    {
                        metaupload.Abort();
                    }

                    upload_worker = Task.Factory.StartNew(
                    (o) =>
                    {
                        worker_DoWork();
                    }, "upload worker");
                    //upload_worker.Priority = ThreadPriority.Lowest;
                    //upload_worker.Start();
                }
                else
                {
                    if (upload_worker != null)
                    {
                        upload_worker = null;
                    }
                }

                //reset global progress values:
                if (value)
                {
                    lock (UploadQueue)
                    {
                        _uploadlastcount = UploadQueueEditing.Count;
                        _uploadcurrentcount = 0;
                    }
                }
            }
        }

        public Uri LoginUrl { get; private set; }

        public Uri CurrentEventBaseUri { get; set; }

        #endregion properties

        #region Events

        /// <summary>
        /// Fired when the current API key is invalid for this server.
        /// </summary>
        public event Action OnApiKeyInvalid;

        /// <summary>
        /// Fired when the current API version is out-of-date, and should be updated.
        /// </summary>
        public event Action OnApiVersionChanged;

        //fire when more media has been loaded -- ie refresh ui
        /// <summary>
        /// Fired when new media is available in the cache after initiating GetAllMedia.
        /// </summary>
        public event Action<List<MediaItem>> OnMoreMediaLoaded;

        /// <summary>
        /// Fired when GetAllMedia cache update from the server is complete.
        /// </summary>
        public event Action<int> OnMediaLoadingComplete;

        /// <summary>
        /// Fired for external libraries to report or log internal errors.
        /// </summary>
        public event Action<Exception> OnReportError;

        /// <summary>
        /// Fired for external libraries to report or log externally.
        /// </summary>
        public event Action<string> OnReportLog;

        /// <summary>
        /// Fired to external libraries to view debug messages.
        /// </summary>
        public static event Action<string> OnDebugMessage;

        /// <summary>
        /// Fired when the auto-director requests the CurrentUser needs to get a shot.
        /// </summary>
        public event Action<Shot, string> OnShotRequest;

        /// <summary>
        /// Fired when the server or local API needs to alert the user about something.
        /// </summary>
        public event Action<BootleggerNotificationType, string, bool, bool, bool> OnMessage;

        /// <summary>
        /// Fired when the server requests the CurrentUser should change roles.
        /// </summary>
        public event Action<Role> OnRoleChanged;

        /// <summary>
        /// Fired when the server initiates a record countdown in auto-director mode.
        /// </summary>
        public event Action<int> OnCountdown;

        /// <summary>
        /// Fired when the server initiates the CurrentUser to start recording.
        /// </summary>
        public event Action<bool, int> OnLive;

        [Obsolete("Not used anymore")]
        public event Action<int> OnTime;

        /// <summary>
        /// Fired when the server changes the CurrentEvent shooting mode (e.g. selector)
        /// </summary>
        public event Action<string> OnModeChange;

        /// <summary>
        /// Fired when one of the participants in an auto-director mode shoot initiates the start of the live event.
        /// </summary>
        public event Action OnEventStarted;

        /// <summary>
        /// Fired when the API cannot communicate with the server, and the API needs to be manually restarted. This  may be due to the server dying.
        /// </summary>
        public event Action OnServerDied;

        /// <summary>
        /// Fired when there has been a temporary drop in connection with the server. No action nessesary.
        /// </summary>
        public event Action OnLostConnection;

        /// <summary>
        /// Fired when a temporary drop with the server has been restored. No action nessesary.
        /// </summary>
        public event Action OnGainedConnection;

        /// <summary>
        /// Fired when the API needs to alert the user of something, which is not related to a CurrentEvent.
        /// </summary>
        public event Action<BootleggerNotificationType, string> OnNotification;

        /// <summary>
        /// Fired when native application should save preferences.
        /// </summary>
        public event Action<string, string> OnSavePreferences;

        /// <summary>
        /// Fired when phase is changed in real-time from the server.
        /// </summary>
        public event Action<MetaPhase> OnPhaseChange;

        /// <summary>
        /// Fired when CurrentEvent shoot phase has changed, i.e. when a cached version is updated, and there are changes.
        /// </summary>
        public event Action OnEventUpdated;

        /// <summary>
        /// Fired when the server reports that the current session details are no longer valid.
        /// </summary>
        public event Action OnSessionLost;

        /// <summary>
        /// Fired on progress of CurrentEvent images assets being downloaded from the server.
        /// </summary>
        public event Action<int> OnImagesDownloading;

        /// <summary>
        /// Fired when CurrentEvent images have been unzipped and successfully updated in the cache.
        /// </summary>
        public event Action OnImagesUpdated;

        /// <summary>
        /// Fired when permissions for a shoot have been changed since the user has accepted them.
        /// </summary>
        public event Action OnPermissionsChanged;

        /// <summary>
        /// Fired when the CurrentUser has lodded in elsewhere for a real-time shoot, thus this device will not receive correct real-time direction.
        /// </summary>
        public event Action OnLoginElsewhere;

        /// <summary>
        /// Fired when all re-connect attempts have failed, prompting user intervention.
        /// </summary>
        public event Action OnCantReconnect;

        /// <summary>
        /// Fired when all current uploads (either for CurrentEvent, or globally) have completed.
        /// </summary>
        public event Action OnCurrentUploadsComplete;

        /// <summary>
        /// Fired when all current uploads (either for CurrentEvent, or globally) have failed.
        /// </summary>
        public event Action OnCurrentUploadsFailed;

        /// <summary>
        /// Fired when total upload progress has changed (either for CurrentEvent or globally).
        /// </summary>
        public event Action<double, int, int> OnGlobalUploadProgress;

        /// <summary>
        /// Fired when the cached version of an edit has been updated. Usually when processing progress has changed.
        /// </summary>
        public event Action<Edit> OnEditUpdated;

        #endregion event handlers

        #region Logging

        class LogBody
        {
            public string action;
            public Dictionary<string,string> data;
        }

        public void LogUserAction(string action, params KeyValuePair<string,string>[] data)
        {
            if (WhiteLabelConfig.LOCAL_SERVER)
            {
                var req = new RestRequest("/api/log", Method.POST);

                var body = new LogBody()
                {
                    action = action,
                    data = data.ToDictionary((key) => key.Key, (val) => val.Value)
                };

                GetAResponsePost(req, body, new CancellationTokenSource().Token).ContinueWith(OnMyAsyncMethodFailed, TaskContinuationOptions.OnlyOnFaulted);
            }
            else
            {
                Analytics.TrackEvent(action,data.ToDictionary((key)=>key.Key,(val)=>val.Value));
            }
        }

        public static void OnMyAsyncMethodFailed(Task task)
        {
            Console.WriteLine(task.Exception);
        }

        #endregion

        #region Capture

        /// <summary>
        /// Sets the Anonymity settings for the CurrentUser on the given event.
        /// </summary>
        /// <param name="makeprivate">True to hide name</param>
        /// <param name="eventid"></param>
        public async void SetUserPrivacy(bool makeprivate, string eventid)
        {
            var res = await GetAResponsePost(new RestRequest("/auth/setprivacy"), new Bootleg.API.SailsSocket.JsonMessage() { eventid = eventid, privacy = makeprivate }, new CancellationTokenSource().Token);
            //sails.Get("/auth/setprivacy", new Bootleg.API.SailsSocket.JsonMessage() { eventid = eventid, privacy = makeprivate });
            CurrentUser.permissions[eventid] = makeprivate;
        }


        /// <summary>
        /// Returns number of shots that CurrentUser has captured on this device for CurrentEvent that match a given shot id.
        /// </summary>
        /// <param name="pin">Shot id to match against</param>
        /// <returns></returns>
        public int GetNumShotsByType(int pin)
        {
            lock (UploadQueue)
                return (from n in MyMediaEditing where int.Parse(n.Static_Meta[MetaDataFields.Shot]) == pin && n.Static_Meta[MetaDataFields.EventId] == CurrentEvent.id select n).Count();
        }

        /// <summary>
        /// Returns fraction of shots (of the requested amount in the template) that CurrentUser has captured on this device for CurrentEvent that match a given shot id.
        /// </summary>
        /// <param name="pin"></param>
        /// <returns></returns>
        public double GetShotsQuantityByType(int pin, List<MediaItem> searchlist)
        {
            if (CurrentEvent.shottypes != null)
            {
                double want = CurrentEvent.shottypes[pin].wanted;
                if (want == 0)
                    want = DEFAULT_WANTED_CLIPS;

                var items = searchlist.Union(UploadQueue);

                return (from n in items where n.Static_Meta.ContainsKey(MetaDataFields.Shot) && int.Parse(n.Static_Meta[MetaDataFields.Shot]) == pin && n.event_id == CurrentEvent.id && n.created_by == CurrentUser.id select n).Count() / want;
            }
            else
            {
                return 0;
            }
        }

        /// <summary>
        /// Sets the currently selected shot when filming in offline mode.
        /// </summary>
        /// <param name="item"></param>
        public void SetShot(Shot item)
        {
            CurrentClientShotType = item;
        }


        /// <summary>
        /// Indicate that recording has started.
        /// </summary>
        public void RecordingStarted()
        {
            IsRecording = true;
            try
            {
                Task res = GetAResponsePost(new RestRequest("/event/startrecording"), new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id }, new CancellationTokenSource().Token);
            }
            catch
            {
                //do nothing:
            }

            //sails.Get("/event/startrecording", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id });
        }

        /// <summary>
        /// Indicate that recording has stopped.
        /// </summary>
        public void RecordingStopped()
        {
            IsRecording = false;
            //ClientShotChoices.Clear();
            //sails.Get("/event/stoprecording", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id });
            try
            {
                Task res = GetAResponsePost(new RestRequest("/event/stoprecording"), new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id }, new CancellationTokenSource().Token);
            }
            catch
            {
                //do nothing:
            }
        }
        #endregion

        #region Live Mode
        /// <summary>
        /// Indicate the CurrentUser is ready to start capturing video.
        /// </summary>
        public void ReadyToShoot()
        {
            //sails.Get("/event/ready", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id });
            try
            {
                Task res = GetAResponsePost(new RestRequest("/event/ready"), new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id }, new CancellationTokenSource().Token);
            }
            catch
            {
                //do nothing:
            }
        }

        /// <summary>
        /// Indicate the CurrentUser is no longer able to capture video.
        /// </summary>
        public void NotReadyToShoot()
        {
            try
            {
                Task res = GetAResponsePost(new RestRequest("/event/notready"), new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id }, new CancellationTokenSource().Token);
            }
            catch
            {
                //do nothing:
            }
            //sails.Get("/event/notready", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id });
        }

        /// <summary>
        /// Accept and get ready to record the shot allocated by the server.
        /// </summary>
        /// <param name="shot"></param>
        public void AcceptShot(Shot shot)
        {
            CurrentClientShotType = shot;
            CurrentServerShotType = null;
            //ClientShotChoices.Clear();
            try
            {
                Task res = GetAResponsePost(new RestRequest("/event/acceptshot"), new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id, shotid = shot.id }, new CancellationTokenSource().Token);
            }
            catch
            {
                //do nothing:
            }
            //sails.Get("/event/acceptshot", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id, shotid = shot.id });
        }

        /// <summary>
        /// Reject and refuse to capture the shot allocated by the server. This may result in another allocation.
        /// </summary>
        /// <param name="shot"></param>
        public void RejectShot(Shot shot)
        {
            CurrentClientShotType = null;
            //ClientShotChoices.Clear();
            sails.Get("/event/rejectshot", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id, shotid = shot.id });
        }


        /// <summary>
        /// Indicate that the event has started, and shot allocations should be sent.
        /// </summary>
        public void EventStarted()
        {
            sails.Get("/event/started", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id });
        }

        /// <summary>
        /// Indicate that 
        /// </summary>
        public void HoldShot()
        {
            sails.Get("/event/holdrecording", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id });
        }

        [Obsolete("Not used in this version of API")]
        public void SkipShot()
        {
            sails.Get("/event/skiprecording", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id });
        }

        [Obsolete("Not used in this version of the API")]
        public void TriggerInterest(Role role, Shot shot)
        {
            sails.Get("/event/tiggerinterest", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id, roleid = role.id, shotid = shot.id });
        }
        #endregion

        #region Role Selection

        /// <summary>
        /// Choose the current role
        /// </summary>
        /// <param name="role"></param>
        /// <param name="confirmed"></param>
        /// <param name="cancel"></param>
        /// <returns></returns>
        public async Task<RoleStatus> SelectRole(Role role, bool confirmed, CancellationToken cancel)
        {
            if (CurrentEvent==null)
            {
                return new RoleStatus() { State = RoleStatus.RoleState.NO };
            }

           
            CurrentClientRole = role;
            //link shots to role for offline connected one:
            CurrentClientRole._shots.Clear();
            foreach (var s in CurrentClientRole.shot_ids)
            {
                var shot = (from n in CurrentEvent.shottypes where n.id == s select n);
                if (shot.Count() == 1)
                    CurrentClientRole._shots.Add(shot.First());
            }

            if (CurrentClientRole._shots.Count == 0)
            {
                foreach (var s in CurrentEvent.shottypes)
                {
                    CurrentClientRole._shots.Add(s);
                }
            }

            CurrentEvent.CurrentMode = CurrentEvent.generalrule;

            //we are offline, so do this without contacting server...
            SaveOfflineSelection();

            return new RoleStatus() { State = RoleStatus.RoleState.OK };
            //}
            //else
            //{

            //    var result = await GetAResponsePost(new RestRequest("/event/chooserole"), new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id, roleid = role.id, confirm = confirmed }, cancel);
            //    //string result = await sails.GetResult("/event/chooserole", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id, roleid = role.id, confirm = confirmed });
            //    try
            //    {
            //        RoleResult r = await DecodeJson<RoleResult>(result);
            //        if (r.status == "ok")
            //        {
            //            CurrentClientRole = CurrentEvent.roles.Where(o => o.id == role.id).First();
            //            SaveOfflineSelection();
            //            OnReportLog("select role " + role.id);
            //            return new RoleStatus() { State = RoleStatus.RoleState.OK };
            //        }
            //        else if (r.status == "confirm")
            //        {
            //            return new RoleStatus() { State = RoleStatus.RoleState.CONFIRM, Message = r.msg };
            //        }
            //        else
            //        {
            //            //if its a fail -- check that its not because the server somehow lost the user between connecting to the event and selecting a role...
            //            if (r.status == "fail" && CurrentEvent.offline)
            //            {
            //                await ConnectToEvent(CurrentEvent, true, cancel);
            //                return await SelectRole(role, confirmed, cancel);
            //            }
            //            else
            //            {
            //                return new RoleStatus() { State = RoleStatus.RoleState.NO, Message = r.msg };
            //            }
            //        }
            //    }
            //    catch (Exception e)
            //    {
            //        return new RoleStatus() { State = RoleStatus.RoleState.NO, Message = e.Message };
            //    }
            //}
        }

        /// <summary>
        /// Unselects CurrentRole for CurrentUser, also informing the server that the user is leaving the shoot if required.
        /// </summary>
        public async Task UnSelectRole(bool offline, bool alsoleaveevent)
        {
            //if (CurrentEvent?.offline ?? false)
            SaveOfflineSelection();

            if (CurrentClientRole != null)
            {
                //set client role to null so that login screen does not try auto logging in to event
                CurrentClientRole = null;
                //Console.WriteLine("UNSELECTED ROLE");
                //if (sails != null)
                if (!offline)
                    await GetAResponsePost(new RestRequest("/event/unselectrole"), new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id }, new CancellationTokenSource().Token);

                //await sails.Get("/event/unselectrole", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id });
            }

            if (!offline && CurrentEvent != null && !CurrentEvent.offline)
            {
                if (alsoleaveevent && sails != null)
                    await GetAResponsePost(new RestRequest("/event/leaveshoot"), new Bootleg.API.SailsSocket.JsonMessage() { id = CurrentEvent.id }, new CancellationTokenSource().Token);

                //await sails.Get("/event/leaveshoot", new SailsSocket.SailsEventArgs() { id = CurrentEvent.id });
            }
        }

        /// <summary>
        /// Accept the role that the server offered during role selection.
        /// </summary>
        /// <param name="role"></param>
        public void AcceptRole(Role role)
        {
            CurrentClientRole = role;
            sails.Get("/event/acceptrole", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id, roleid = role.id });
        }

        /// <summary>
        /// Reject the role that the sever offered for this user during role selection.
        /// </summary>
        /// <param name="role"></param>
        public void RejectRole(Role role)
        {
            sails.Get("/event/rejectrole", new Bootleg.API.SailsSocket.JsonMessage() { eventid = CurrentEvent.id, roleid = role.id });
        }

        bool uploadmediaalreadyhappening = false;

        //THIS IS HAPPENING AT THE SAME TIME!!
        public async Task UploadMedia(MediaItem media)
        {
            uploadmediaalreadyhappening = true;

            //check to see if this has been done while we were waiting...
            if (media.Status == MediaItem.MediaStatus.META_UPLOADED)
                return;

            SailsSocket.MediaArgs args = new SailsSocket.MediaArgs();
            args.static_meta = JsonConvert.SerializeObject(media.Static_Meta);
            args.timed_meta = JsonConvert.SerializeObject(media.TimedMeta);

            try
            {
                //var r = await sails.Post(mainuri.ToString() + "media/addmedia/", args);
                RestRequest req = new RestRequest("/media/addmedia/");
                var r = await GetAResponsePost(req, args, new CancellationToken());
                //var r = await sails.Post(mainuri.ToString() + "media/addmedia/", args);

                if (r != null && r != "")
                {
                    media.Status = MediaItem.MediaStatus.META_UPLOADED;

                    lock (database)
                    {
                        database.Delete(media);
                    }
                    lock (UploadQueue)
                    {
                        UploadQueue.Remove(media);
                    }
                    media.id = r;
                    lock (database)
                    {
                        database.Insert(media);
                    }
                    lock (UploadQueue)
                    {
                        UploadQueue.Add(media);
                    }

                    //only upload thumb if the media has got an id
                    try
                    {
                        UploadThumb(media);
                    }
                    catch (Exception e)
                    {
                        //thumb failed- - ignore for now and will try again later...
                    }
                }
            }
            catch (Exception e)
            {
                //didnt upload -- will do next time
                media.Status = MediaItem.MediaStatus.NOTONSERVER;
                throw e;
            }
            finally
            {
                lock (database)
                    database.Update(media);
                uploadmediaalreadyhappening = false;
            }

        }

        /// <summary>
        /// Create a media object with all the appropriate fields filled in, and adds it to the upload queue
        /// </summary>
        /// <param name="media"></param>
        /// <returns></returns>
        public MediaItem CreateMediaMeta(MediaItem media, Dictionary<string, string> meta = null, Dictionary<string, string> timed = null, string thumbnail = null)
        {
            Role temprole = CurrentClientRole;

            //add to the queue
            if (meta == null)
                meta = new Dictionary<string, string>();
            if (timed == null)
                timed = new Dictionary<string, string>();

            media.Status = MediaItem.MediaStatus.NOTONSERVER;

            //user has contributed to this event, so add it to the event history table:
            if (CurrentEvent != null)
            {
                CurrentEvent.private_user_id = CurrentUser.id;
                CurrentEvent.last_touched = DateTime.Now.ToString();
                try
                {
                    lock (database)
                        database.Update(CurrentEvent);
                }
                catch
                {
                    lock (database)
                        database.Insert(CurrentEvent);
                }

                //SaveOfflineSelection();
            }

            media.CreatedAt = DateTime.Now;
            media.Static_Meta.Add(MetaDataFields.EventId, CurrentEvent.id);
            media.Static_Meta.Add(MetaDataFields.CreatedBy, CurrentUser.id);

            if (meta != null)
            {
                foreach (var k in meta)
                {
                    try
                    {


                        media.Static_Meta.Add(k.Key, k.Value);
                    }
                    catch
                    {
                    }
                }
            }
            if (timed != null)
            {
                foreach (var k in timed)
                {
                    try
                    {
                        media.TimedMeta.Add(k.Key, k.Value);
                    }
                    catch
                    {

                    }

                }
            }

            //for if you dont add the captured at time specifically...
            if (!media.Static_Meta.ContainsKey("captured_at"))
            {
                media.Static_Meta.Add("captured_at", DateTime.Now.ToString("dd/MM/yyyy H:mm:ss.ff tt zz"));

                if (media.Static_Meta.ContainsKey("clip_length"))
                {
                    var captured = DateTime.Parse(media.Static_Meta["captured_at"]);

                    TimeSpan clip_length = TimeSpan.Parse(media.Static_Meta["clip_length"]);
                    captured = captured.Subtract(clip_length);
                    media.Static_Meta["captured_at"] = captured.ToString("dd/MM/yyyy H:mm:ss.ff tt zz");
                }
            }

            media.Static_Meta.Add(MetaDataFields.MetaPhase, CurrentEvent.currentphase.ToString());

            if (media.Filename != null)
            {
                FileInfo ff = new FileInfo(media.Filename);
                media.Static_Meta.Add("filesize", (ff.Length / 1024).ToString("F1"));
                media.Static_Meta.Add("local_filename", ff.Name);
            }


            if (thumbnail != null)
                media.Thumb = thumbnail;

            if (temprole != null)
            {
                media.Static_Meta.Add(MetaDataFields.Role, CurrentClientRole.id.ToString());
            }

            if (CurrentClientShotType != null)
            {
                media.Static_Meta.Add("media_type", CurrentClientShotType.shot_type.ToString());
                media.Static_Meta.Add(MetaDataFields.Shot, CurrentClientShotType.id.ToString());
                media.Static_Meta.Add("shot_meta", CurrentClientShotType.meta);
                media.DummyName = CurrentClientShotType.name + " Shot";
                if (CurrentClientShotType.release)
                    media.Static_Meta.Add("release", "true");
                if (CurrentClientShotType.coverage_class != null)
                    media.Static_Meta.Add("coverage_class", CurrentClientShotType.coverage_class.ToString());
            }

            if (CurrentServerShotType != null && CurrentClientShotType == null)
            {
                media.Static_Meta.Add(MetaDataFields.Shot, CurrentServerShotType.id.ToString());
                media.Static_Meta.Add("shot_meta", CurrentServerShotType.meta);
                if (CurrentServerShotType.coverage_class != null)
                    media.Static_Meta.Add("coverage_class", CurrentServerShotType.coverage_class.ToString());
                media.DummyName = CurrentServerShotType.name + " Shot";
            }

            if (CurrentTimedMeta.Count > 0)
            {
                foreach (var k in CurrentTimedMeta)
                {
                    try
                    {
                        //Console.WriteLine(k.Key.ToString(@"hh\:mm\:ss\\fffff"));
                        media.TimedMeta.Add(k.Key.ToString(@"hh\:mm\:ss\\fffff"), k.Value);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            CurrentTimedMeta.Clear();

            media.id = Guid.NewGuid().ToString();


            //should only be doing this once!!!
            lock (database)
                database.Insert(media);
            lock (UploadQueue)
                UploadQueue.Add(media);
            return media;
        }

        /// <summary>
        /// During a recording session, add single item of timed meta to the current list that will be applied when CreateMedia is next called.
        /// </summary>
        /// <param name="time"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        public void AddTimedMeta(TimeSpan time, string key, string value)
        {
            if (!CurrentTimedMeta.ContainsKey(time))
            {
                CurrentTimedMeta.Add(time, key + "_" + value);
            }
        }

        #endregion

        #region Joining Shoots

        //public async Task<Shoot> GetLocalShoot(Tuple<double, double> latlng, CancellationToken cancel)
        //{
        //    //heuristic to get closest / most relevant shoot:

        //    //1. In range of beacon (done from native code in the app)

        //    //2. Query for nearby given current coordinates

        //    //3. Closest time to shoot starting / ending?
        //    return null;
        //}

        public Tuple<double, double> UserLocation { get; set; }

        /// <summary>
        /// Update list of nearby and featured events from the server
        /// </summary>
        /// <param name="cancel"></param>
        /// <returns></returns>
        public async Task UpdateFeatured(CancellationToken cancel)
        {

            //BackgroundWorker bb = new BackgroundWorker();
            //bb.DoWork += (o,e) => {
            //TODO: for location
            //RestRequest req = new RestRequest($"/event/featured/{UserLocation.Item1}/{UserLocation.Item2}");
            RestRequest req = new RestRequest($"/event/featured");


            //CancellationTokenSource cancel = new CancellationTokenSource(5000);
            Task<List<Shoot>> tt = GetAResponse<List<Shoot>>(req, cancel);
            //Task.WaitAll(tt);
            await tt;
            //string s = tt.Result;

            var ress = tt.Result;

            foreach (var r in ress)
            {
                r.Featured = true;
                lock (database)
                {
                    if (database.Find<Shoot>(r.id) != null)
                        database.Update(r);
                    else
                        database.Insert(r);
                }
            }
        }

        ///// <summary>
        ///// Update list of featured events from the server.
        ///// </summary>
        ///// <param name="cancel"></param>
        ///// <returns></returns>
        //public async Task UpdateFeatured(CancellationToken cancel)
        //{

        //    //BackgroundWorker bb = new BackgroundWorker();
        //    //bb.DoWork += (o,e) => {
        //    RestRequest req = new RestRequest("/event/featured");

        //    //CancellationTokenSource cancel = new CancellationTokenSource(5000);
        //    Task<List<Shoot>> tt = GetAResponse<List<Shoot>>(req, cancel);
        //    //Task.WaitAll(tt);
        //    await tt;
        //    //string s = tt.Result;

        //    var ress = tt.Result;

        //    foreach (var r in ress)
        //    {
        //        r.Featured = true;
        //        lock (database)
        //        {
        //            if (database.Find<Shoot>(r.id) != null)
        //                database.Update(r);
        //            else
        //                database.Insert(r);
        //        }
        //    }
        //}

        ///// <summary>
        ///// Get list of cached nearby events.
        ///// </summary>
        //public List<Shoot> NearbyEvents
        //{
        //    get
        //    {
        //        lock (database)
        //        {
        //            //remove ones from the list that I am contributing to:
        //            return (from e in database.Table<Shoot>() where e.Featured orderby e.starts select e).Take(6).Except(ShootHistory).ToList();
        //        }
        //    }
        //}

        /// <summary>
        /// Get list of cached featured events.
        /// </summary>
        public List<Shoot> FeaturedEvents
        {
            get
            {
                lock (database)
                {
                    //remove ones from the list that I am contributing to:
                    var shoots = (from e in database.Table<Shoot>() where e.Featured orderby e.starts select e).Take(6).Except(ShootHistory).ToList();

                    //filter out-of-date ones out:
                    return shoots.Where(s => ((s.RealStarts ?? DateTime.MinValue) <= DateTime.Now) && ((s.RealEnds ?? DateTime.MaxValue) >= DateTime.Now)).ToList();

                }
            }
        }

        /// <summary>
        /// Become a contributor to an event given an individual join code (invitation).
        /// </summary>
        /// <param name="code"></param>
        /// <param name="cancel"></param>
        /// <returns></returns>
        public async Task<string> JoinEvent(string code, CancellationToken cancel)
        {
            RestRequest request = new RestRequest("event/registercode/" + code);
            //request.CookieContainer = session;
            string s = await GetAResponse(request, cancel);
            Bootleg.API.SailsSocket.JsonMessage msg = await DecodeJson<Bootleg.API.SailsSocket.JsonMessage>(s);
            if (msg.code == 200)
            {
                //refresh event list:
                return msg.eventid;
            }
            else
            {
                throw new Exception("Invalid Code");
            }
        }

        /// <summary>
        /// Become a contributor to an event given a shared join code.
        /// </summary>
        /// <param name="code"></param>
        /// <param name="cancel"></param>
        /// <returns></returns>
        public async Task<string> JoinSharedEvent(string code, CancellationToken cancel)
        {
            RestRequest request = new RestRequest("joincode/");
            var obj = new Dictionary<string, string>();
            obj.Add("code", code);
            //request.CookieContainer = session;
            string s = await GetAResponsePost(request, obj, cancel);
            Bootleg.API.SailsSocket.JsonMessage msg = await DecodeJson<Bootleg.API.SailsSocket.JsonMessage>(s);
            return msg.eventid;
        }

        /// <summary>
        /// Download image assets for CurrentEvent from the server and unpack them.
        /// </summary>
        /// <returns></returns>
        public async Task GetImages()
        {
            if (doingimages)
            {
                SetLocalImagePaths();
                return;
            }

            //for each file in the event, check if it exists. If they all dont, download the zip and put in the same dir...
            var downloadneeded = false;

            var missing = from n in CurrentEvent.shottypes where !File.Exists(storagelocation.FullName + "/bootlegger/" + n.image.Replace(storagelocation.FullName + "/bootlegger/", "")) && !File.Exists(storagelocation.FullName + "/bootlegger/icons/" + n.icon.Replace(storagelocation.FullName + "/bootlegger/", "")) select n;
            downloadneeded = missing.Count() > 0;

            //Console.WriteLine("download needed: " + downloadneeded + " " + missing.Count() + " not there.");

            if (downloadneeded && Connected)
            {
                doingimages = true;
                FileInfo zipfile = new FileInfo(storagelocation.FullName + "/bootlegger/" + Guid.NewGuid() + ".zip");
                storagelocation.CreateSubdirectory("bootlegger");
                var t1 = Task.Factory.StartNew(() =>
                {
                    var uri = new Uri(mainuri, "/static/data/" + CurrentEvent.id + ".zip");

                    byte[] buffer = new byte[4096];

                    WebRequest wr = WebRequest.Create(uri);
                    long counter = 0;
                    using (WebResponse response = wr.GetResponse())
                    {
                        using (Stream responseStream = response.GetResponseStream())
                        {
                            using (FileStream memoryStream = zipfile.OpenWrite())
                            {
                                int count = 0;
                                do
                                {
                                    count = responseStream.Read(buffer, 0, buffer.Length);
                                    memoryStream.Write(buffer, 0, count);
                                    counter += count;
                                    if (OnImagesDownloading != null)
                                    {
                                        OnImagesDownloading((int)((counter / (double)response.ContentLength) * 100));
                                    }
                                    //Console.WriteLine("got: " + (counter / (double)response.ContentLength));
                                } while (count != 0);

                                memoryStream.Flush();
                                memoryStream.Close();
                                //result = memoryStream.ToArray();
                            }
                        }
                    }
                    Console.WriteLine("done download");
                });
                await t1;

                await Task.Delay(1000);

                var t2 = Task.Factory.StartNew(() =>
                {
                    try
                    {
#if __IOS__
						var zip = new MiniZip.ZipArchive.ZipArchive();
						zip.UnzipOpenFile(zipfile.FullName);
						zip.UnzipFileTo(storagelocation.FullName + "/bootlegger/", true);
						zip.UnzipCloseFile();
#else


                        //ZipFile.ExtractToDirectory(zipPath, extractPath);
                        Bootleg.API.Decompress.Decompress decompress = new Bootleg.API.Decompress.Decompress(zipfile.FullName, storagelocation.FullName + "/bootlegger/");
                        decompress.UnZip();

                        //ZipFile zip = ZipFile.Read(zipfile.FullName);
                        //zip.ExtractAll(storagelocation.FullName + "/bootlegger/", ExtractExistingFileAction.OverwriteSilently);
#endif

                        SetLocalImagePaths();
                    }
                    catch (Exception ff)
                    {
                        //Log.Error("bootlegger",ex.Message);
                        //Log.Error("bootlegger", "cant unzip");
                        SetLocalImagePaths();
                        try
                        {
                            zipfile.Delete();
                        }
                        catch (Exception f)
                        {
                            Console.WriteLine(f);
                        }
                    }
                    finally
                    {

                    }
                });

                await t2;
                doingimages = false;
                OnImagesUpdated?.Invoke();
                SetLocalImagePaths();
            }
            else
            {
                //download not needed -- set all paths
                doingimages = false;
                SetLocalImagePaths();
                await Task.Yield();
            }
        }

        /// <summary>
        /// Returns cached list of events that CurrentUser has contributed to.
        /// </summary>
        public List<Shoot> ShootHistory
        {
            get
            {
                if (CurrentUser != null)
                {
                    lock (database)
                    {
                        var fromcache = (from n in EventCache where n.Event != null && n.User.id == CurrentUser.id orderby n.LastTouched descending select n.Event);

                        var fromdb = (from n in database.Table<Shoot>() where n.private_user_id == CurrentUser.id select n).ToList().OrderBy((arg) => arg.RealStarts);

                        var others = fromdb.Except(fromcache);

                        var union = fromcache.Union(fromdb);
                        foreach (var ev in union)
                        {
                            lock (database)
                                ev.myclips = (from n in database.Table<MediaItem>() where n.created_by == CurrentUser.id && n.event_id == ev.id select n).Count();
                        }

                        return union.ToList();
                    }
                }
                else
                    return new List<Shoot>();
            }
        }

        /// <summary>
        /// Updates the local cache with CurrentUser event history from the server.
        /// </summary>
        /// <param name="cancel"></param>
        /// <returns></returns>
        public async Task GetShootHistory(CancellationToken cancel)
        {
            //load the shoot history from the server...
            RestRequest request = new RestRequest("event/mycontributions/");
            var events = await GetAResponse<List<Shoot>>(request, cancel);

            foreach (var r in events)
            {
                r.Contributed = true;
                r.private_user_id = CurrentUser.id;
                lock (database)
                {
                    if (database.Find<Shoot>(r.id) != null)
                        database.Update(r);
                    else
                        database.Insert(r);
                }
            }
            //does nothing
            return;
        }

        /// <summary>
        /// Get full event information from the server, including template.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="cancel"></param>
        /// <returns></returns>
        public async Task<Shoot> GetEventInfo(string id, CancellationToken cancel)
        {
            try
            {
                RestRequest request = new RestRequest("event/info/" + id);
                var dat = await GetAResponse<Shoot>(request, cancel);
                dat.localroleimage = dat.roleimg;

                if (CurrentEvent?.id == dat.id)
                {
                    CurrentEvent.numberofclips = dat.numberofclips;
                    CurrentEvent.numberofcontributors = dat.numberofcontributors;
                    CurrentEvent.publicview = dat.publicview;
                    CurrentEvent.publicedit = dat.publicedit;
                    CurrentEvent.ispublic = dat.ispublic;
                    CurrentEvent.topics = dat.topics;
                    CurrentEvent.name = dat.name;
                    CurrentEvent.roleimg = dat.roleimg;

                    AddEventToHistory(CurrentEvent);
                }

                return dat;
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        /// <summary>
        /// Register that you are a producer in this event.
        /// </summary>
        /// <param name="ev"></param>
        public async Task ConnectToEvent(Shoot ev, bool force, CancellationToken cancel, bool inbackground = false)
        {
            //v2 - check if this event is in production right now:
            if (!inbackground)
            {
                CurrentEvent = ev;
            }

            RestRequest request = new RestRequest("event/subscribe/" + ev.id);
            var s = await GetAResponse(request, cancel);
            //Console.WriteLine(s);
            var mesg = await DecodeJson<Hashtable>(s);
            if (!mesg.Contains("status"))
            {
                //Console.WriteLine(mesg["phases"].ToString());
                Shoot old_temp = CurrentEvent;
                try
                {
                    //this transmits back the event information + fills in the current event object:
                    CurrentEvent = await DecodeJson<Shoot>(s);
                }
                catch (Exception)
                {

                }

                CurrentEvent.LinkRolestoShots();
                SetLocalImagePaths();

                //v2 - detect changes to template and update / inform user that it has happend:
                if (inbackground)
                {
                    //for events that were offline and are now online...
                    UpdateAndCheckForEventChanges(old_temp, CurrentEvent);
                }

                //if (CurrentEvent.offline)
                //{
                //    //TODO Check this works -- added for ios version to make sure that images are downloaded for offline events after reinstall
                //    await GetImages();
                //}

                //if the event is not realtime control, setup thread to reconnect to server every so often
                if (CurrentEvent.offline && !inbackground)
                {

                    //Start thread in case its not been started already (for offline events, this function has already been called from this thread anyway)
                    OnResume();

                    if (metaupload == null)
                    {
                        Thread metaupload = new Thread(new ThreadStart(() =>
                        {
                            metaupload_DoWork(null, null);
                        }));
                        metaupload.Priority = ThreadPriority.Lowest;
                        metaupload.Start();
                    }
                }

                ////if offline -- save for later...
                //SaveOfflineSelection();

                //get role img if there is one:
                if (!string.IsNullOrEmpty(CurrentEvent.roleimg))
                {
                    await SaveRoleImg(CurrentEvent);
                }

                AddEventToHistory(CurrentEvent);

                //if its a livemode shoot
                if (!CurrentEvent.offline)
                {
                    await StartSocket();
                    await sails.Get("/event/sub/", new SailsSocket.SailsEventArgs() { id = CurrentEvent.id, force = force });
                }
                //connected
                //Console.WriteLine("Connected to Shoot");
                OnReportLog("connected to event");
            }
            else
            {
                throw new Bootleg.API.Exceptions.ServerErrorException();
            }
        }

        private async Task SaveRoleImg(Shoot currentEvent)
        {
            RestRequest req = new RestRequest(currentEvent.roleimg.Replace(mainuri.ToString(), ""));
            client.CookieContainer = session;
            var data = await client.ExecuteTaskAsync(req);
            var filename = storagelocation.FullName + "/bootlegger/" + data.ResponseUri.Segments.Last();
            currentEvent.localroleimage = "file:" + filename;
            File.WriteAllBytes(filename, data.RawBytes);
        }

        /// <summary>
        /// Adds this event to the CurrentUser event history.
        /// </summary>
        /// <param name="event"></param>
        public void AddEventToHistory(Shoot @event)
        {
            //add an OfflineEvent object with the relevent details:
            var tmp = (from n in EventCache where n.Event != null && n.Event.id == @event.id select n);
            if (tmp.Count() == 0)
            {
                var cc = new OfflineSelection();
                cc.Event = @event;
                cc.User = CurrentUser;
                cc.LastTouched = DateTime.Now;
                EventCache.Add(cc);
                SaveCache();
            }
            else
            {
                SaveOfflineSelection();
            }
        }

        #endregion //shoots

        #region Connection

        /// <summary>
        /// Connect to bootlegger with existing session (normal use, i.e. with a browser already having logged in)
        /// </summary>
        /// <param name="server"></param>
        /// <param name="port"></param>
        /// <param name="cookie"></param>
        public async Task Connect(Cookie cookie, CancellationToken cancel)
        {
            originalcookie = cookie;
            session.Add(mainuri, cookie);
            await Connect(cancel);
        }

        ManualResetEvent pause_worker_flag = new ManualResetEvent(false);

        /// <summary>
        /// Connect to an offline capable event. Can be called without a live connection, and will try intermittently to connect.
        /// </summary>
        /// <param name="eventid"></param>
        /// <returns></returns>
        public async Task OfflineConnect(string eventid, CancellationToken cancel)
        {
            //arbitary delay entered so that the ui can catch up
            await Task.Delay(100);

            originalcookie = new Cookie("sails.sid", CurrentUser.session, "/", mainuri.Host);
            session.Add(mainuri, originalcookie);

            CurrentEventBaseUri = new Uri(this.storagelocation.FullName + "/bootlegger/");

            //load all offline things into the right place...
            OfflineMode = true;

            OfflineSelection tmp = GetCachedEvent(eventid);
            //Console.WriteLine("OFFLINE CONNECT CACHED ROLE: " + tmp.Role);


            //if this offline event does not have all of the information we need:
            if (tmp.Event.shottypes == null)
            {
                //try and get the relevant information:
                //var fullev = await GetEventInfo(tmp.Event.id,cancel);
                await ConnectToEvent(tmp.Event, false, cancel);
                tmp.Event = CurrentEvent;
            }

            LoadCachedUser();
            //CurrentUser = tmp.User;
            CurrentEvent = tmp.Event;
            CurrentClientRole = tmp.Role;
            SetLocalImagePaths();

            //Console.WriteLine("OFFLINE CONNECT CACHED SHOTS: " + CurrentEvent.shottypes.Count);

            //if (CurrentClientRole == null)
            //{
            //    //SaveOfflineSelection();
            //    //throw new RoleNotSelectedException();
            //    CurrentClientRole = CurrentEvent.roles.First();
            //}

            //link shots to role for offline connected one:
            //CurrentClientRole._shots.Clear();
            //foreach (var s in CurrentClientRole.shot_ids)
            //{
            //    var shot = (from n in CurrentEvent.shottypes where n.id == s select n);
            //    if (shot.Count() == 1)
            //        CurrentClientRole._shots.Add(shot.First());
            //}

            //if (CurrentClientRole._shots.Count == 0)
            //{
            //    foreach (var s in CurrentEvent.shottypes)
            //    {
            //        CurrentClientRole._shots.Add(s);
            //    }
            //}


            CurrentEvent.CurrentMode = CurrentEvent.generalrule;
            //Console.WriteLine(CurrentClientRole.Shots.Count + " current role shots");
            //Thread.Sleep(500);
            //CurrentClientRole = CurrentEvent.roles.Find(o => o.id == CurrentClientRole.id);

            CurrentEvent.LinkRolestoShots();

            //Console.WriteLine("OFFLINE CONNECT CACHED SHOTS AFTER LINK: " + CurrentEvent.shottypes.Count);

            //Console.WriteLine(CurrentClientRole.Shots.Count + " current role shots after link");
            CurrentEvent.HasStarted = true;
            //setup thread to process meta-data upload:

            if (metaupload == null)
            {
                metaupload = new Thread(new ThreadStart(() =>
                {
                    metaupload_DoWork(null, null);
                }));
                metaupload.Name = "Meta-data Background Upload";
                metaupload.Priority = ThreadPriority.Lowest;
                metaupload.Start();
            }

           

            //REMINDER LOGIC
            //Timer timer = new Timer(new TimerCallback((o) =>
            //{
            //    lock (UploadQueue)
            //    {
            //        if (UploadQueue.Count > 0)
            //        {
            //            var media = UploadQueue.OrderByDescending(r => r.CreatedAt).First();

            //            if (media != null && media.CreatedAt < DateTime.Now.Subtract(TimeSpan.FromMinutes(TIME_SINCE_RECORDED)) && !IsRecording && lastmessage < DateTime.Now.Subtract(TimeSpan.FromMinutes(TIME_SINCE_MESSAGE)))
            //            {
            //                lastmessage = DateTime.Now;
            //                if (InBackground)
            //                {
            //                    if (OnNotification != null)
            //                        //"Crew Reminder", "You have not recorded for a while, why dont you try something now"
            //                        OnNotification(BootleggerNotificationType.CrewReminder, "");
            //                }
            //                else
            //                {
            //                    if (OnMessage != null)
            //                        //"You have not recorded for a while, why dont you try something now"
            //                        OnMessage(BootleggerNotificationType.CrewReminder, "", false, false, true);
            //                }
            //            }
            //        }
            //    }
            //}), null, 0, (int)TimeSpan.FromMinutes(TIME_CHECK_INTERVAL).TotalMilliseconds);
        }


        /// <summary>
        /// Register this device for push notifications.
        /// </summary>
        /// <param name="code"></param>
        /// <param name="platform"></param>
        public void RegisterForPush(string code, Platform platform)
        {
            //if (socket != null && socket.IsConnected)
            Task res = GetAResponsePost(new RestRequest("/event/registerpush"), new Bootleg.API.SailsSocket.JsonMessage() { pushcode = code, platform = platform.ToString() }, new CancellationTokenSource().Token);
            //sails.Get("/event/registerpush", new Bootleg.API.SailsSocket.JsonMessage() { pushcode = code, platform = platform.ToString() });
        }

        public void StartWithLocal(Uri local)
        {
            client = new RestClient(local);
            this.server = local.Scheme + "://" + local.Host;
            this.port = local.Port;
            mainuri = new Uri(server + ":" + port);
            ValidLoginUri = new Uri(mainuri.AbsoluteUri + "event/view");
            LoginUrl = new Uri(mainuri.AbsoluteUri + "auth/mobilelogin");
            LoadCache();
            LoadCachedUser();
        }


        //2 ip
        public bool CheckLocalIP()
        {
            IPAddress[] addresses = Dns.GetHostAddresses(Dns.GetHostName());
           // string ipAddress = string.Empty;

            foreach (var address in addresses)
            {
                if (address.ToString().StartsWith("10.10.10", StringComparison.InvariantCulture))
                    return true;
            }

            return false;
        }

        //4 connection
        public async Task<bool> CheckApplication()
        {
            RestRequest request = new RestRequest("/status");
            var resp = await client.ExecuteGetTaskAsync(request);
            return resp.StatusCode == HttpStatusCode.OK;
        }

        //public static async Task<Uri> FindLocalServer(string defaultip)
        //{
        //    //check connection with server:
        //    try
        //    {

        //        using (var deviceLocator = new SsdpDeviceLocator())
        //        {
        //            //"urn:bootlegger:device:server:1" 
        //            //"uuid:30f4d4fe-59e6-11e8-9c2d-fa7ae01bbebc"
        //            var foundDevices = await deviceLocator.SearchAsync("urn:bootlegger:device:server:1", TimeSpan.FromSeconds(20)); // Can pass search arguments here (device type, uuid). No arguments means all devices.
        //            //var mydevice = from n in foundDevices where n.NotificationType == "urn:bootlegger:device:server:1" select n;
        //            //var mydevice = from n in foundDevices where n.NotificationType == "urn:bootlegger:device:server:1" select n;
        //            Uri url;
        //            try
        //            {
        //                var mydevice = foundDevices.First();
        //                url = mydevice.DescriptionLocation;
        //            }
        //            catch
        //            {
        //                url = new Uri("http://" + defaultip);
        //            }


        //            //SsdpRootDevice deviceinfo = mydevice.First() as SsdpRootDevice;

        //            //mydevice.First().DescriptionLocation
        //            //Uri host = mydevice.DescriptionLocation;
        //            //Uri ho st = new Uri(Encoding.Default.GetString(tc));
        //            RestRequest request = new RestRequest("/status");
        //            TaskCompletionSource<HttpStatusCode> tcs = new TaskCompletionSource<HttpStatusCode>();
        //            request.RequestFormat = DataFormat.Json;
        //            var client = new RestClient(url);

        //            var resp = await client.ExecuteGetTaskAsync(request);
        //            if (resp.StatusCode == HttpStatusCode.OK)
        //            {
        //                return url;
        //            }
        //            else
        //            {
        //                throw new Exception("Invalid server information in packet");
        //            }
        //        }
        //    }
        //    catch (Exception e)
        //    {
        //        throw e;
        //    }
        //}

        /// <summary>
        /// Logout of the API
        /// </summary>
        public void Logout()
        {
            session = new CookieContainer();

            originalcookie = null;
            try
            {
                SaveOfflineSelection(true);
            }
            catch (Exception)
            {

            }
            CurrentEvent = null;
            CurrentUser = null;

            CurrentClientRole = null;
            //CurrentOfflineSelection = null;
            OnReportLog("logout");

            MyEvents = new List<Shoot>();
            UploadQueue.Clear();

            //destroy connection
            if (Connected)
            {

                //if (CurrentEvent != null)
                //{
                //    await sails.Get("/event/signout", new SailsSocket.SailsEventArgs() { id = CurrentEvent.id });
                //}
                //else
                //{
                //    await sails.Get("/event/signout", new SailsSocket.SailsEventArgs() { });
                //}


                //socket.Close();
#if SOCKETS
                try
                {

                    socket?.Close();
                }
                catch { }
#endif
                Connected = false;
            }

            try
            {
                if (metaupload != null)
                    metaupload.Abort();
            }
            catch { }

        }

        [Obsolete("Local mode not supported by this version of the API")]
        public async Task Connect(Uri server, string localcode, CancellationToken cancel)
        {
            try
            {
                TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
                this.server = server.Host;
                this.port = server.Port;
                mainuri = new Uri(mainuri.Scheme + "://" + this.server + ":" + this.port);

                await CheckVersion();

                //client = new RestClient();

                var request = new RestRequest(mainuri.ToString() + "auth/local_login/" + localcode, Method.GET);
                request.RequestFormat = DataFormat.Json;
                //request.ContentType = "application/json";
                request.AddHeader("Accept", "application/json");
                client.CookieContainer = session;
                var resp = client.GetAsync(request, (o, e) =>
                {
                    if (o.StatusCode == HttpStatusCode.OK)
                    {
                        originalcookie = session.GetCookies(mainuri)[0];
                        tcs.TrySetResult(o.Content);
                    }
                    else
                    {
                        tcs.TrySetException(new Exception("invalid code or server"));
                    }
                });
                await tcs.Task;
            }
            catch
            {
                throw new Exception("local code not valid");
            }

            try
            {
                //start handler with cookies:
                //start socket
                await StartSocket();
            }
            catch (Exception)
            {
                throw new Exception("Socket Error");
            }

            CurrentUser = await GetMyProfile(cancel);
            SaveLoggedInUser();
            //MyEvents = await ListMyEvents(cancel);
        }

        private async Task Connect(CancellationToken cancel)
        {
            //test web connection to server:
            try
            {
                await CheckVersion();
            }
            catch
            {

            }
            //return;

            //get session cookie:
            try
            {
                var request = new RestRequest("auth");
                request.RequestFormat = DataFormat.Json;
                //request.AddParameter("sails.sid", _cookie_value, ParameterType.Cookie);
                //Console.WriteLine("using: " + originalcookie);
                request.AddHeader("Cookie", originalcookie.ToString());
                client.CookieContainer = session;

                //var resp = await client.ExecuteTaskAsync(request,cancel);
                var resp = await GetAResponse(request, cancel);
                //if (resp.StatusCode != HttpStatusCode.OK)
                //    throw new BadPasswordException();
                //if (resp.StatusCode != HttpStatusCode.Forbidden)
                //    throw new ApiKeyException();
            }
            catch (TaskCanceledException can)
            {
                throw can;
            }
            catch (Exception e)
            {
                throw e;
            }

            //check that the api key is valid...

            //only connect socket if its for capture...



            try
            {
                //start handler with cookies:
                //start socket
                //await StartSocket();
                Connected = true;
            }
            catch (Exception)
            {
                throw new Exception("Socket Error");
            }

            CurrentUser = await GetMyProfile(cancel);
            SaveLoggedInUser();
            OnReportLog("connected");
            LoadUploads();
        }

        #endregion //connection

        #region Constructors

        public static void TESTKILL()
        {
            _client = null;
        }


        public static void Create(string server, int port, string cachedir, string apikey, string custom_app_build, string videos_dir, bool include_all_uploads)
        {
            if (_client == null)
                _client = new Bootlegger(server, port, cachedir, apikey, custom_app_build, videos_dir, include_all_uploads);
        }

        ///// <summary>
        ///// Bootlegger API constructor for custom builds (whitelabel apps) which sends additional information to the server about what config to use.
        ///// </summary>
        ///// <param name="server">Server with the API running</param>
        ///// <param name="port">Port of server</param>
        ///// <param name="cachedir">Local directory (native platform dependent) where a non-volitile cache should be stored</param>
        ///// <param name="apikey">API Key related to this application</param>
        ///// <param name="custom_app_build"></param>
        internal Bootlegger(string server, int port, string cachedir, string apikey, string custom_app_build, string videos_dir, bool include_all_uploads) : this(server, port, cachedir, apikey, videos_dir, include_all_uploads)
        {
            CustomAppBuildId = custom_app_build;
        }

        /// <summary>
        /// Bootlegger API constructor for default operation.
        /// </summary>
        /// <param name="server">Server with the API running</param>
        /// <param name="port">Port of server</param>
        /// <param name="cachedir">Local directory (native platform dependent) where a non-volitile cache should be stored</param>
        /// <param name="apikey">API Key related to this application</param>
        internal Bootlegger(string server, int port, string cachedir, string apikey, string videos_dir, bool include_all_uploads)
        {
            API_KEY = apikey;
            INCLUDE_ALL_UPLOADS = include_all_uploads;
            client = new RestClient(server);
            storagelocation = new DirectoryInfo(cachedir);
            videoslocation = new DirectoryInfo(videos_dir);
            //Console.WriteLine(storagelocation);
            OnReportError += new Action<Exception>(delegate { });
            OnReportLog += new Action<string>(delegate { });
            //File.CreateText(cachedir + "/" + "testfile.txt");

            var dbpath = Path.Combine(cachedir, "bootleggerdb4.db3");
            database = new SQLiteConnection(dbpath);
            database.BusyTimeout = TimeSpan.FromSeconds(3);
            //database = new SQLiteConnection(Path.GetFullPath(cachedir + Path.DirectorySeparatorChar +"bootlegger.db"));
            lock (database)
            {
                database.CreateTable<MediaItem>();
                database.CreateTable<Shoot>();
                database.CreateTable<Edit>();
                database.CreateTable<User>();
            }

            //FOR DEBUGGING:
            //database.DeleteAll<Shoot>();

            //#if MONO
            //            SharedResources.BootleggerCerts certs = new SharedResources.BootleggerCerts();
            //#endif

            this.server = server;
            this.port = port;
            this.cachedir = cachedir;
            mainuri = new Uri(server + ":" + port);
            ValidLoginUri = new Uri(mainuri.AbsoluteUri + "event/view");

            LoginUrl = new Uri(mainuri.AbsoluteUri + "auth/mobilelogin");
            UploadQueue = new List<MediaItem>();
            //CurrentCoverage = -1;


            //BackgroundWorker worker = new BackgroundWorker();
            //worker.DoWork += worker_DoWork;
            //worker.RunWorkerAsync();

            Directory.CreateDirectory(cachedir + "/bootlegger");

            //Heartbeat
            System.Timers.Timer heartbeat = new System.Timers.Timer();
            heartbeat.Interval = 5000;
            heartbeat.Elapsed += heartbeat_Elapsed;
            heartbeat.Start();

            //check for an load offline event selection
            LoadCache();
            LoadCachedUser();
            LoadUploads();
        }

        /// <summary>
        /// Get list of events that CurrentUser can contribute to from the server.
        /// </summary>
        /// <param name="cancel"></param>
        /// <returns></returns>
        public async Task ListMyEvents(CancellationToken cancel)
        {
            try
            {
                RestRequest request = new RestRequest("event/myevents/");
                var events = await GetAResponse<List<Shoot>>(request, cancel);

                //var events = await Task.Run(() => { return JsonConvert.DeserializeObject<List<Shoot>>(s); });
                foreach (var e in events)
                {
                    switch (e.status)
                    {
                        case "OWNER":
                            e.MyParticipation = Shoot.Participation.OWNER;
                            break;
                        case "INVITED":
                            e.MyParticipation = Shoot.Participation.INVITED;
                            break;
                        default:
                            e.MyParticipation = Shoot.Participation.PUBLIC;
                            break;
                    }
                }

                //var contributed = EventsContributedTo();

                List<string> ids = new List<string>();
                List<Shoot> newevents = new List<Shoot>();

                var allevents = events;

                foreach (var e in allevents)
                {
                    if (e.group == null && !ids.Contains(e.id))
                    {
                        ids.Contains(e.id);
                        newevents.Add(e);
                    }
                    if (e.group != null && e.events.Count > 0)
                    {
                        if (!ids.Contains(e.group))
                        {
                            ids.Add(e.group);
                            newevents.Add(e);
                        }
                        else
                        {
                            newevents.Find((t) => { return t.group == e.group; }).events.Concat(e.events);
                            newevents.Find((t) => { return t.group == e.group; }).events = newevents.Find((t) => { return t.group == e.group; }).events.Distinct().ToList();
                        }
                    }
                }

                try
                {
                    MyEvents = newevents.OrderBy(o =>
                    {
                        if (o.group == null || o.group == "")
                        {
                            try
                            {
                                var dt = DateTime.ParseExact(o.starts, "dd-MM-yyyy", CultureInfo.InvariantCulture);
                                return dt;
                            }
                            catch
                            {
                                return DateTime.Now;
                            }
                        }
                        else
                        {
                            return DateTime.Now;
                        }
                    }).Where((o) =>
                    {
                        //in date, or user has recorded videos for the event...
                        if (o.group == null || o.group == "")
                            try
                            {
                                return ((from n in UploadQueue where n.Static_Meta[MetaDataFields.EventId] == o.id select n).Count() > 0 || DateTime.ParseExact(o.ends, "dd-MM-yyyy", CultureInfo.InvariantCulture) >= DateTime.Now.Subtract(TimeSpan.FromDays(1)) || o.MyParticipation == Shoot.Participation.OWNER);
                            }
                            catch
                            {
                                return true;
                            }
                        else
                            return true;
                    }).ToList();
                    //Console.WriteLine("after filtering events " + events.Count());
                }
                catch (Exception)
                {
                    //return events;
                    MyEvents = events;
                }
            }
            catch (Exception e)
            {
                if (MyEvents == null)
                    MyEvents = new List<Shoot>();
                throw e;
            }
        }

        #endregion //constructors

        #region Private Methods

        private void heartbeat_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            //send a heartbeat to the server...
#if SOCKETS
            if (socket != null && socket.IsConnected && sails != null && CurrentUser != null && CurrentEvent != null)
            {
                sails.Get("/event/heartbeat", new SailsSocket.SailsEventArgs() { id = CurrentEvent.id });
            }
#endif
        }

        private void worker_DoWork()
        {
            try
            {
                int total;
                lock (UploadQueue)
                {
                    total = UploadQueueEditing.Count;
                }
                while (CanUpload && total > 0)
                {
                    if (Connected)
                    {
                        //pick the next one in the upload queue that does not have retries maxed
                        ProcessQueue();

                        lock (UploadQueue)
                        {
                            var maxed = from n in UploadQueue where n.retrycount >= 5 select n;
                            //all current uploads have failed
                            if (maxed.Count() == UploadQueue.Count && UploadQueue.Count > 0)
                            {
                                CanUpload = false;
                                OnCurrentUploadsFailed?.Invoke();
                                //reset all uploads...
                                UploadQueue.ForEach((m) => m.retrycount = 0);
                            }
                        }
                    }
                    Thread.Sleep(100);
                }
            }
            catch (Exception)
            {
                Console.WriteLine("Uploading worker stopped");
            }
        }

        private async Task CheckVersion()
        {
            //get server version and throw error if the wrong version...
            RestRequest r = new RestRequest("auth/status");
            IRestResponse o = null;

            int version = -1;
            try
            {
                o = await client.ExecuteTaskAsync(r);
                Hashtable data = await DecodeJson<Hashtable>(o.Content);
                //Console.WriteLine(o.Content);
                version = int.Parse(data["version"].ToString());
                data = null;
            }
            catch (Exception)
            {
                throw new Exception("Server Not Responding");
            }
            if (version > API_VERSION)
            {
                //throw new NotSupportedException("Incorrect App Version, please update");
                OnApiVersionChanged?.Invoke();
            }
        }

        private void metaupload_DoWork(object sender, DoWorkEventArgs e)
        {
            int counter = 0;
            try
            {
                while (true)
                {
                    pause_worker_flag.WaitOne();
                    if (!Connected)
                    {
                        try
                        {
                            //Task.WaitAll(StartSocket());

                            Task<User> tu = GetMyProfile(new CancellationToken());
                            Task.WaitAll(tu);
                            CurrentUser = tu.Result;

                            SaveLoggedInUser();
                            Task.WaitAll(ConnectToEvent(CurrentEvent, true, new CancellationToken(), true));
                            Connected = true;
                        }
                        catch (Exception)
                        {

                            //Console.WriteLine("cant start socket in offline mode");
                        }
                    }
                    else
                    {
                        //TODO: debug hack (5 mins)
                        //if (counter % (1) == 0)
                        if (counter % (6 * 5) == 0)
                            try
                            {
                                Task.WaitAll(ConnectToEvent(CurrentEvent, true, new CancellationToken(), true));
                            }
                            catch (Exception)
                            {

                            }
                    }

                    if (Connected)
                    {
                        //if has connection, take next upload from the list that has not been uploaded to server:

                        var first = from n in UploadQueue where n.Status == MediaItem.MediaStatus.NOTONSERVER select n;
                        //var first = UploadQueue.Find(o => o.Status == MediaItem.MediaStatus.NOTONSERVER);
                        //Console.WriteLine(UploadQueue.FindAll(o => o.Status == MediaItem.MediaStatus.NOTONSERVER).Count + " not on server");
                        if (first.Count() > 0)
                        {
                            //not been uploaded to the server at all yet -- so do it now...
                            try
                            {
                                if (!uploadmediaalreadyhappening)
                                {
                                    //Task<MediaItem> t = CreateMedia(first.First(), first.First().Static_Meta, first.First().TimedMeta, first.First().Thumb, true);
                                    var media = first.First();
                                    var task = UploadMedia(media);
                                    //DEBUG -- ERROR HERE IN THE FOLLOWING SEQUENCE:
                                    // connected -> record -> disconnect -> record -> connect -> record ->  *crash*


                                    Task.WaitAll(new Task[] { task }, 9000);
                                }
                            }
                            catch
                            {
                                Console.WriteLine("Server Fail in uploading");
                            }
                        }
                    }
                    counter++;
                    Thread.Sleep(10000);
                }
            }
            catch
            {
                Console.WriteLine("Metadata Uploader Stopped");
            }
        }

        private void LoadUploads()
        {
            if (CurrentUser != null)
            {
                lock (database)
                {
                    //housekeeping -- remove uploads where the file does not exist:
                    var notthere = (from s in database.Table<MediaItem>().ToList()
                                    where !File.Exists(s.Filename)
                                    && (s.Status != MediaItem.MediaStatus.DONE)
                                    select s).ToList();
                    //remove from the db media-items that are missing files, but not on the server yet

                    foreach (var s in notthere)
                    {
                        database.Delete<MediaItem>(s.id);
                    }
                }

                lock (database)
                {
                    var its = (from n in database.Table<MediaItem>() where n.Status != MediaItem.MediaStatus.DONE select n).ToList();

                    //where n.Meta["created_by"] == CurrentUser.id
                    lock (UploadQueue)
                        UploadQueue = new List<MediaItem>();
                    foreach (var i in its)
                    {
                        if (i.Static_Meta[MetaDataFields.CreatedBy] == CurrentUser.id || INCLUDE_ALL_UPLOADS)
                            lock (UploadQueue)
                                UploadQueue.Add(i);
                    }
                }
                lock (UploadQueue)
                    UploadQueue.Sort((x, y) => x.CreatedAt.CompareTo(y.CreatedAt));
            }
        }

        private void LoadCache()
        {
            if (File.Exists(cachedir + "/bootlegger/eventcache.json"))
            {
                try
                {
                    var text = File.ReadAllText(cachedir + "/bootlegger/eventcache.json");

                    EventCache = JsonConvert.DeserializeObject<List<OfflineSelection>>(text).Where(o => o?.Event?.id != null).ToList();
                }
                catch (Exception e)
                {

                }
            }
        }

        private void SaveCache()
        {
            try
            {


                File.WriteAllText(cachedir + "/bootlegger/eventcache.json", JsonConvert.SerializeObject(EventCache));
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        private void LoadCachedUser()
        {
            //load last touched event from the cache and put into the user object:
            try
            {
                //var evs = EventCache.OrderByDescending(o => o.LastTouched);
                //if (evs.Count() > 0 && !evs.First().LoggedOut)
                lock (database)
                {
                    var cu = (from u in database.Table<User>() orderby u.LastTouched descending select u);
                    if (cu.Count() > 0)
                    {
                        CurrentUser = cu.First();

                        //HACK HACK HACK
                        //CurrentUser.session = "xxxxxx";


                        //DEBUG HACK:
                        originalcookie = new Cookie("sails.sid", CurrentUser.session, "/", LoginUrl.Host);
                        session.Add(originalcookie);
                    }

                }
            }
            catch
            {
                //no previous user
                CurrentUser = null;
            }
        }

        private OfflineSelection GetCachedEvent(string eventid)
        {
            var ev = (from n in EventCache where n.Event != null && n.Event.id == eventid select n);
            if (ev.Count() == 0)
                throw new RoleNotSelectedException();
            else
                return ev.First();
        }

        private void UpdateAndCheckForEventChanges(Shoot old, Shoot newevent)
        {
            //Console.WriteLine("new: " + newevent.roles[0].Shots.Count);

            //FOR DEBUGGING

            //TODO -- CHECK THIS WORKS AND DOES NOT SAVE A NULL ROLE IN OFFLINE MODE
            //var cached = GetCachedEvent(old.id);

            //Role old_role;
            //if (cached.Role != null && cached.Shoot.offline)
            //    old_role = cached.Role;
            //else
            var old_role = CurrentClientRole;

            if (CurrentClientRole != null)
            {
                CurrentClientRole = CurrentEvent.roles.Find(o => o.id == CurrentClientRole.id);
                //Console.WriteLine("SETTING NEW ROLE FROM UPDATE");
            }

            //Console.WriteLine("assigned: " +CurrentClientRole.Shots.Count);

            //{
            bool ischanged = false;

            if (!InBackground)
            { 
                if (old.publicshare != newevent.publicshare || string.Compare(old.release, newevent.release) > 0)
                {
                    ischanged = true;
                    OnPermissionsChanged?.Invoke();
                } else if (old.currentphase != newevent.currentphase)
                {
                    ischanged = true;
                    //phase different -- check role against ones in this phase, and warn
                    //OnEventUpdated?.Invoke();
                    sails_OnPhaseChange(newevent.currentphase);
                }
                else if (CurrentClientRole != null && (!old.shottypes.SequenceEqual(newevent.shottypes, new Utils()) || !old_role.Shots.SequenceEqual(CurrentClientRole.Shots, new Utils())))
                {
                    //shots different (changed, hidden etc)
                    ischanged = true;
                    //OnEventUpdated?.Invoke();
                    sails_OnNewMessage("", false, false);
                    //Task.Run(GetImages);
                }
            }

            //do an update anyway as the object references have updated
            OnEventUpdated?.Invoke();

            //update current client role
            SaveOfflineSelection();
        }


        private void SaveLoggedInUser()
        {


            if (CurrentUser != null)
            {
                lock (database)
                {
                    CurrentUser.LastTouched = DateTime.Now;
                    CurrentUser.session = originalcookie.Value;
                    database.InsertOrReplace(CurrentUser);
                }

                //foreach (var e in EventCache)
                //{
                //    if (e.User.id == CurrentUser.id)
                //        e.LoggedOut = false;
                //}
                SaveCache();
            }
        }

        private void SaveOfflineSelection(bool remove = false)
        {
            //update the events cache
            OfflineSelection evc = null;
            try
            {
                if (CurrentEvent != null)
                {
                    var evs = (from n in EventCache where n.Event != null && n.Event.id == CurrentEvent.id select n);
                    if (evs.Count() > 0)
                        evc = evs.First();
                }
                else
                {
                    var evs = (from n in EventCache where n.User != null && n.User.id == CurrentUser.id select n);
                    if (evs.Count() > 0)
                        evc = evs.First();
                }
            }
            catch
            {

            }



            if (remove)
            {
                lock (database)
                {
                    //var users = database.Table<User>().ToList();
                    //Console.WriteLine(.Count());
                    try
                    {
                        database.Delete(CurrentUser);
                    }
                    catch
                    {
                        //cant delete user:
                    }
                    //database.Delete<User>(CurrentUser);
                }
            }

            if (evc != null)
            {
                evc.LastTouched = DateTime.Now;
                if (remove)
                {

                    //evc.LoggedOut = true;
                    foreach (var e in EventCache)
                    {
                        // e.LoggedOut = true;
                        e.LastTouched = DateTime.Now;
                    }
                }
                else
                {
                    //evc.LoggedOut = false;
                    if (CurrentEvent!=null)
                        evc.Event = CurrentEvent;
                    evc.User = CurrentUser;
                    //Console.WriteLine("SAVING ROLE IN CACHE: " + CurrentClientRole);
                    if ((evc.Event.offline && CurrentClientRole != null) || !evc.Event.offline)
                        evc.Role = CurrentClientRole;
                }
                SaveCache();
            }
        }

        private void SetLocalImagePaths()
        {
            CurrentEventBaseUri = new Uri(this.storagelocation.FullName + "/bootlegger/");

            if (CurrentEvent == null)
                return;

            if (this.storagelocation == null)
                return;

            //if (storagelocation != null)
            //    this.storagelocation = storagelocation;

            //if (CurrentEvent.RolePath == "")
            //{
            //    CurrentEvent.RolePath = this.storagelocation.FullName + "/bootlegger/" + CurrentEvent.roleimg;
            //}



            foreach (Shot s in CurrentEvent._shottypes)
            {
                if (!s.RelativePaths)
                {
                    s.icon = this.storagelocation.FullName + "/bootlegger/icons/" + s.icon;
                    s.image = this.storagelocation.FullName + "/bootlegger/" + s.image;
                    s.RelativePaths = true;
                }
            }

            if (CurrentClientRole != null)
            {
                foreach (Shot s in CurrentClientRole._shots)
                {
                    if (!s.RelativePaths)
                    {
                        s.icon = this.storagelocation.FullName + "/bootlegger/icons/" + s.icon;
                        s.image = this.storagelocation.FullName + "/bootlegger/" + s.image;
                        s.RelativePaths = true;
                    }
                }
            }
        }

        private async Task RegainConnection()
        {
            if (CurrentEvent != null)
            {
                await sails.Get("/event/resub/", new SailsSocket.SailsEventArgs() { id = CurrentEvent.id, force = true });
            }
            else
            {
                await sails.Get("/event/resub/", new SailsSocket.SailsEventArgs() { force = true });
            }

            if (!OfflineMode)
            {
                if (OnGainedConnection != null)
                    OnGainedConnection();
            }
        }

        private Task StopSocket()
        {
            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
#if SOCKETS
            socket?.Close();
#endif
            return Task.FromResult(true);
        }

        private Task StartSocket()
        {

            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
#if SOCKETS

            if (socket == null || (socket.ReadyState == WebSocket4Net.WebSocketState.Closed || socket.ReadyState == WebSocket4Net.WebSocketState.None))
            {
                NameValueCollection headers = new NameValueCollection();

                if (string.IsNullOrWhiteSpace(originalcookie.ToString()))
                {
                    tcs.SetCanceled();
                    return tcs.Task;
                }

                headers.Add("Cookie", originalcookie.ToString());

                try
                {
                    socket = new Client(mainuri.ToString(), headers);
                }
                catch (Exception e)
                {
                    //whats this?
                }

                //socket.Message+=socket_Message;
                socket.Opened += async (o, e) =>
                {
                    //first time connect:
                    if (!Connected)
                    {
                        Console.WriteLine("Socket Connected");
                        Connected = true;
                        if (CurrentEvent != null && !CurrentEvent.offline)
                            await RegainConnection();
                        else
                            tcs.TrySetResult(true);
                    }
                    else
                    {
                        //reconnect to server (without rest of connection routine)

                    }
                };


                socket.Error += (o, e) =>
                {
                    Connected = false;
                    tcs.TrySetException(e.Exception);
                    if (!OfflineMode)
                    {
                        if (OnLostConnection != null)
                            OnLostConnection();
                    }
                };


                socket.ConnectionRetryAttempt += socket_ConnectionRetryAttempt;
                socket.SocketConnectionClosed += socket_SocketConnectionClosed;
                socket.RetryConnectionAttempts = 2;

                sails = new SailsSocket(socket);

                sails.OnNewMessage += sails_OnNewMessage;
                sails.OnTimer += sails_OnTimer;
                sails.OnChangeRole += sails_OnChangeRole;
                sails.OnChangeShot += sails_OnChangeShot;
                sails.OnLive += sails_OnLive;
                sails.OnShootNow += sails_OnShootNow;
                sails.OnModeChange += sails_OnModeChange;
                sails.OnEventStarted += sails_OnEventStarted;
                sails.OnUserUpdate += sails_OnUserUpdate;
                sails.OnEventUpdated += sails_OnEventUpdated;
                sails.OnPhaseChange += sails_OnPhaseChange;
                sails.OnLoginElsewhere += Sails_OnLoginElsewhere;
                sails.OnCantReconnect += Sails_OnCantReconnect;
                sails.OnEditUpdated += Sails_OnEditUpdated;
                try
                {
                    socket.Connect();
                }
                catch
                { }

                return tcs.Task;
            }
            else
            {

                tcs.SetResult(false);
                return tcs.Task;
            }

#else
            sails = new SailsSocket(false);
            tcs.SetResult(false);
            return tcs.Task;
#endif
        }

        private void Sails_OnEditUpdated(Edit obj)
        {
            //find the edit with this id:
            lock (database)
            {
                var ed = database.Get<Edit>(obj.id);
                if (ed.progress != obj.progress || ed.failed != obj.failed)
                {
                    database.Update(obj);
                    var theedit = database.Get<Edit>(obj.id);
                    //Console.WriteLine(theedit.id);
                    OnEditUpdated?.Invoke(theedit);
                }
            }
        }

        private void Sails_OnCantReconnect()
        {
            OnCantReconnect?.Invoke();
        }

        private void Sails_OnLoginElsewhere()
        {
            OnLoginElsewhere?.Invoke();
        }

        private void sails_OnPhaseChange(int phase)
        {
            CurrentEvent.currentphase = phase;
            if (OfflineMode)
                SaveOfflineSelection();

            if (InBackground)
            {
                OnNotification?.Invoke(BootleggerNotificationType.PhaseChanged, CurrentEvent.CurrentPhase.name);
                //OnNotification("Shoot Changed", "You should now be shooting " + CurrentEvent.CurrentPhase.name);
            }
            else
            {
                OnPhaseChange?.Invoke(CurrentEvent.CurrentPhase);
            }
        }

        private void sails_OnEventUpdated(Shoot ev)
        {
            //event has changed - make sure to update all the adapters etc
            var tmp = CurrentEvent;
            CurrentEvent = ev;
            CurrentEvent.LinkRolestoShots();
            UpdateAndCheckForEventChanges(tmp, CurrentEvent);
        }

        private void sails_OnUserUpdate(int arg1, int arg2, int arg3, int arg4)
        {
            CurrentUser.ShotLength = arg1;
            CurrentUser.WarningLength = arg2;
            CurrentUser.CountdownLength = arg3;
            CurrentUser.CameraGap = arg4;
        }

        private void socket_ConnectionRetryAttempt(object sender, EventArgs e)
        {
            if (!OfflineMode)
            {

                OnLostConnection?.Invoke();
            }

            //server trying again to connect...
        }

        private void socket_SocketConnectionClosed(object sender, EventArgs e)
        {
            //connection actually closed:
            if (OnServerDied != null)
            {
                if (CurrentEvent != null && !CurrentEvent.offline)
                {
                    CurrentClientRole = null;
                    OnServerDied();
                }
            }
        }

        private void sails_OnEventStarted()
        {
            CurrentEvent.HasStarted = true;
            OnEventStarted?.Invoke();
        }

        private void sails_OnModeChange(string obj)
        {
            CurrentEvent.CurrentMode = obj;
            OnModeChange?.Invoke(obj);
        }

        private void sails_OnShootNow(int obj)
        {
            OnCountdown?.Invoke(obj);
        }

        private void sails_OnLive(bool obj, int length)
        {
            OnLive?.Invoke(obj, length);
        }

        private void sails_OnChangeShot(int obj, string meta, int coverage)
        {
            if (CurrentEvent != null && CurrentEvent.shottypes != null)
            {
                if (OnShotRequest != null)
                {
                    var shot = (from n in CurrentEvent.shottypes where n.id == obj select n).First();
                    if (shot != null)
                    {
                        //set shot that has been requested
                        CurrentServerShotType = shot;
                        //CurrentCoverage = coverage;
                        shot.meta = meta;
                        OnShotRequest(shot, meta);
                    }
                }
            }
        }

        private void sails_OnChangeRole(int obj)
        {
            if (CurrentEvent != null && CurrentEvent.roles != null)
            {
                var role = (from n in CurrentEvent.roles where n.id == obj select n);
                if (role.Count() > 0)
                {
                    CurrentClientRole = role.First();

                    if (OnRoleChanged != null)
                    {
                        if (role != null)
                        {
                            OnRoleChanged(role.First());
                        }
                    }
                }
            }
        }

        private void sails_OnNewMessage(string obj, bool dialog, bool shots)
        {
            var tp = BootleggerNotificationType.CrewReminder;
            if (string.IsNullOrEmpty(obj))
            {
                tp = BootleggerNotificationType.ShootUpdated;
            }

            if (!InBackground)
            {
                if (OnMessage != null)
                    OnMessage(tp, obj, dialog, shots, false);
            }
            else
            {
                if (OnNotification != null && !dialog && !shots)
                    OnNotification(tp, obj);
            }
        }

#if SOCKETS
        private void socket_Error(object sender, SocketIOClient.ErrorEventArgs e)
        {
            //do something with the socket?

            //socket.Connect();

            Console.WriteLine(e.Message);

            //socket disconnect
        }
#endif

        private void sails_OnTimer(int obj)
        {
            if (OnTime != null)
                OnTime(obj);
        }

        private void DoFilePost(MediaItem media)
        {
            if (File.Exists(media.Filename))
            {
                media.Status = MediaItem.MediaStatus.UPLOADING;
                try
                {
                    UploadFileEx(media, mainuri.ToString() + "media/upload/" + media.id, "thefile", null, null, session);
                    media.Status = MediaItem.MediaStatus.DONE;
                }
                catch (BadPasswordException)
                {
                    //session key invalid, need to re-login
                    throw new BadPasswordException();
                }
                catch (Exception e)
                {
                    //file not uploaded
                    media.Status = MediaItem.MediaStatus.FILEUPLOADERROR;
                    throw e;
                }
                finally
                {
                    lock (database)
                        database.Update(media);
                }
            }
            else
            {
                media.Status = MediaItem.MediaStatus.FILENOTEXISTERROR;
                lock (database)
                    database.Delete(media);
            }
        }

        private async Task<User> GetMyProfile(CancellationToken cancel)
        {
            RestRequest request = new RestRequest("event/me/");
            return await GetAResponse<User>(request, cancel);
        }

        private async Task<Y> GetAResponse<Y>(RestRequest req, CancellationToken cancel)
        {
            req.ReadWriteTimeout = 8000;
            req.AddQueryParameter("apikey", API_KEY);
            if (!string.IsNullOrEmpty(CustomAppBuildId))
                req.AddQueryParameter("cbid", CustomAppBuildId);
            if (!string.IsNullOrEmpty(Language))
                req.AddQueryParameter("loc", Language);
            //req.AddHeader("Cookie", originalcookie.ToString());
            //Console.WriteLine(originalcookie.ToString());
            req.RequestFormat = DataFormat.Json;
            req.Method = Method.GET;
            client.CookieContainer = session;
            client.Timeout = (int)TimeSpan.FromSeconds(30).TotalMilliseconds;

            //await Task.Delay(5000);

#if DEBUG
            Console.WriteLine(req.Resource);
#endif

            try
            {
                var resp = await client.ExecuteTaskAsync(req, cancel);
                //var error = resp.ErrorException;
                req = null;

                switch (resp.StatusCode)
                {
                    case HttpStatusCode.OK:
                        Y obj = await Task<Y>.Factory.StartNew(() =>
                        {
                            try
                            {
                                return JsonConvert.DeserializeObject<Y>(resp.Content);
                            }
                            catch (Exception f)
                            {
                                throw f;
                            }
                        }
                        );
                        return obj;
                    case HttpStatusCode.Unauthorized:
                        var err = new ApiKeyException("Invalid API Key");
                        OnApiKeyInvalid?.Invoke();
                        OnReportError(err);
                        throw err;
                    case HttpStatusCode.Forbidden:
                        var err4 = new SessionLostException();
                        OnReportError(err4);
                        OnSessionLost?.Invoke();
                        throw err4;
                    case HttpStatusCode.InternalServerError:
                    case HttpStatusCode.NotFound:
                        var err1 = new ServerErrorException();
                        OnReportError(err1);
                        throw err1;
                    default:
                        var err2 = new Exception(resp.Content);
                        OnReportError(err2);
                        throw err2;
                }
            }
            catch (WebException e)
            {
                throw e;
            }
            catch (Exception e)
            {
                throw new NoNetworkException();
            }
        }

        private async Task<string> GetAResponse(RestRequest req, CancellationToken cancel)
        {
            req.ReadWriteTimeout = 8000;
            req.AddQueryParameter("apikey", API_KEY);
            if (!string.IsNullOrEmpty(Language))
                req.AddQueryParameter("loc", Language);
            req.RequestFormat = DataFormat.Json;
            req.Method = Method.GET;
            client.Timeout = (int)TimeSpan.FromSeconds(30).TotalMilliseconds;


            //await Task.Delay(5000);

            try
            {
                var resp = await client.ExecuteTaskAsync(req, cancel);
                req = null;

                switch (resp.StatusCode)
                {
                    case HttpStatusCode.OK:
                        return resp.Content;
                    case HttpStatusCode.Unauthorized:
                        var err = new ApiKeyException("Invalid API Key");
                        OnApiKeyInvalid?.Invoke();
                        OnReportError(err);
                        throw err;
                    case HttpStatusCode.Forbidden:
                        var err4 = new SessionLostException();
                        OnReportError(err4);
                        OnSessionLost?.Invoke();
                        throw err4;
                    case HttpStatusCode.InternalServerError:
                        var err1 = new ServerErrorException();
                        OnReportError(err1);
                        throw err1;
                    default:
                        var err2 = new NoNetworkException();
                        OnReportError(err2);
                        throw err2;
                }
            }
            catch (WebException e)
            {
                throw e;
            }
        }

        private async Task<string> GetAResponsePost(RestRequest req, object postbody, CancellationToken cancel)
        {
            //TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
            //const int timeoutMs = 8000;
            //var ct = new CancellationTokenSource(timeoutMs);
            //ct.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);
            req.ReadWriteTimeout = 8000;
            req.AddQueryParameter("apikey", API_KEY);
            if (!string.IsNullOrEmpty(Language))
                req.AddQueryParameter("loc", Language);
            req.RequestFormat = DataFormat.Json;
            req.Method = Method.POST;
            req.JsonSerializer = NewtonsoftJsonSerializer.Default;
            req.AddJsonBody(postbody);
            client.Timeout = (int)TimeSpan.FromSeconds(30).TotalMilliseconds;

            //await Task.Delay(5000);

            try
            {
                var resp = await client.ExecuteTaskAsync(req, cancel);
                req = null;
                //Thread.Sleep(5000);

                switch (resp.StatusCode)
                {
                    case HttpStatusCode.OK:
                        return resp.Content;
                    case HttpStatusCode.Unauthorized:
                        var err = new ApiKeyException("Invalid API Key");
                        OnApiKeyInvalid?.Invoke();
                        OnReportError(err);
                        throw err;
                    case HttpStatusCode.InternalServerError:
                    case HttpStatusCode.NotFound:
                        //var err3 = new ApiKeyException("Invalid API Key");
                        var err3 = new ServerErrorException();
                        //OnApiKeyInvalid?.Invoke();
                        OnReportError(err3);
                        throw err3;
                    case HttpStatusCode.ServiceUnavailable:
                        var err4 = new StoriesDisabledException(resp.Content);
                        throw err4;
                        //throw new Exception();

                    default:
                        var err2 = new NoNetworkException();
                        OnReportError(err2);
                        throw err2;
                }
            }
            catch (WebException e)
            {
                throw e;
            }
        }

        ~Bootlegger()
        {
#if SOCKETS
            if (socket != null)
                socket.Close();
#endif
        }

        private void UploadThumb(MediaItem media)
        {
            if (media.Thumb != null && File.Exists(media.Thumb))
            {
                var targetfilename = DateTime.Now.Ticks + "_" + new FileInfo(media.Thumb).Name;
                //client = new RestClient();
                RestRequest request = new RestRequest($"api/media/signupload/{media.event_id}");
                request.AddQueryParameter("filename", targetfilename);
                request.AddQueryParameter("apikey", API_KEY);
                //request.AddHeader("Cookie", originalcookie.ToString());
                client.CookieContainer = session;
                var result = client.Execute(request);
                if (result.StatusCode != HttpStatusCode.OK)
                    throw new Exception();
                Hashtable signed;
                try
                {
                    signed = JsonConvert.DeserializeObject<Hashtable>(result.Content);
                }
                catch
                {
                    throw new BadPasswordException();
                }

                if (signed.ContainsKey("signed_request"))
                {
                    var thefile = new FileInfo(media.Filename);
                    HttpWebRequest webrequest = (HttpWebRequest)WebRequest.Create(signed["signed_request"].ToString());
                    webrequest.CookieContainer = session;
                    webrequest.Method = "PUT";
                    //webrequest.ContentType = "image";
                    //webrequest.Headers.Add("x-amz-acl", "public-read");

                    FileStream fileStream = new FileStream(media.Thumb, FileMode.Open, FileAccess.Read);
                    long length = fileStream.Length;
                    webrequest.ContentLength = length;
                    // webrequest.AllowWriteStreamBuffering = false;
                    Stream requestStream = webrequest.GetRequestStream();
                    // Write out the file contents
                    byte[] buffer = new Byte[checked((uint)Math.Min(4096, (int)fileStream.Length))];
                    int bytesRead = 0;
                    //int total = 0;
                    //int last = 0;
                    //int current = 0;
                    while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        requestStream.Write(buffer, 0, bytesRead);
                    }
                    WebResponse responce = webrequest.GetResponse();
                    Stream s = responce.GetResponseStream();
                    StreamReader sr = new StreamReader(s);
                    string res = sr.ReadToEnd();
                    request = new RestRequest("api/media/uploadthumbcomplete/" + media.id, Method.POST);
                    //request.AddHeader("Cookie", originalcookie.ToString());
                    request.AddParameter("filename", targetfilename);
                    request.AddQueryParameter("apikey", API_KEY);
                    client.CookieContainer = session;
                    result = client.Execute(request);
                    if (result.StatusCode != HttpStatusCode.OK)
                        throw new Exception();
                    media.ThumbOnServer = true;
                    lock (database)
                        database.Update(media);
                    try
                    {
                        File.Delete(targetfilename);
                    }
                    catch
                    {
                        //cant delete file...
                    }
                    try
                    {
                        File.Delete(targetfilename + ".jpg");
                    }
                    catch
                    {
                        //cant delete file...
                    }
                }
            }
        }

        private void FireGlobalUploadProgress(double sub_perc = 0)
        {
            //current perc, current item, current total
            OnGlobalUploadProgress?.Invoke((sub_perc + _uploadcurrentcount) / _uploadlastcount, _uploadcurrentcount, _uploadlastcount);
        }

        private void ProcessQueue()
        {
            try
            {

                IEnumerable<MediaItem> readytoupload;
                lock (UploadQueue)
                {
                    readytoupload = (from n in UploadQueueEditing where n.retrycount < 5 select n);
                }

                if (readytoupload.Count() > 0)
                {
                    MediaItem media = readytoupload.First();
                    //pick first one that has no error...

                    Console.WriteLine("uploading: " + media.id);

                    try
                    {
                        //metadata
                        if (media.Status == MediaItem.MediaStatus.NOTONSERVER || media.Status == MediaItem.MediaStatus.CREATEMEDIAERROR || media.id.Contains("-")) //dahses are present if it is a local guid, and not actually on the server (to deal with left over media which was in the wrong state)
                        {
                            Task t = UploadMedia(media);
                            Task.WaitAll(t);
                        }

                        //thumb
                        if (!media.ThumbOnServer)
                            UploadThumb(media);

                        //file
                        if (media.Status != MediaItem.MediaStatus.DONE && media.Status != MediaItem.MediaStatus.NOTONSERVER)
                        {
                            DoFilePost(media);
                        }


                        if (media.Status == MediaItem.MediaStatus.DONE)
                            CompleteUpload(media);


                    }
                    catch (BadPasswordException)
                    {
                        //go to login screen...
                        OnSessionLost?.Invoke();
                    }
                    catch (Exception e)
                    {
                        // media.Status = MediaItem.MediaStatus.CREATEMEDIAERROR;
                        media.retrycount++;
                        lock (database)
                            database.Update(media);
                        Thread.Sleep(1000);
                    }
                }
            }
            catch (Exception e)
            {
                FireDebug(e.ToString());
            }
        }

        private void CompleteUpload(MediaItem media)
        {
            if (media.Status == MediaItem.MediaStatus.DONE)
            {
                try
                {
                    File.Delete(media.Filename);
                }
                catch
                {
                    //cant remove file
                }

                lock (UploadQueue)
                    UploadQueue.Remove(media);
                lock (database)
                    database.Delete<MediaItem>(media.id);
                _uploadcurrentcount++;
                FireGlobalUploadProgress();
                //add one to the event thats associated with it:
                var ev = GetCachedEvent(media.Static_Meta[MetaDataFields.EventId]);
                ev.Event.myclips++;
                SaveCache();

                //check for last file...
                lock (UploadQueue)
                {
                    if (UploadQueueEditing.Count == 0)
                    {
                        //turn off uploads for now
                        CanUpload = false;
                        OnCurrentUploadsComplete?.Invoke();
                    }
                }
            }
        }

        #endregion //private methods

        #region Review and Editing

        /// <summary>
        /// Gets real temporary URL with no redirects to the completed video, or the local URI if not uploaded yet.
        /// </summary>
        /// <param name="videofile"></param>
        /// <returns></returns>
        public async Task<string> GetVideoUrl(MediaItem media, bool cachebust = false)
        {
            if (!string.IsNullOrEmpty(media.lowres) && File.Exists(videoslocation.FullName + "/bootlegger_cache/" + media.id + ".mp4"))
            {
                return @"file:///" + videoslocation.FullName + "/bootlegger_cache/" + media.id + ".mp4";
            }
            else
            {
                var videofile = (!string.IsNullOrEmpty(media.lowres)) ? media.lowres : media.Filename;

                if (string.IsNullOrEmpty(videofile))
                    return null;

                //TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
                if (videofile?.StartsWith("http", StringComparison.InvariantCulture) ?? false)
                {
                    //get the redirect url of the video:
                    var request = new RestRequest(new Uri(videofile), Method.HEAD);
                    if (cachebust)
                        request.AddQueryParameter("cachebust", DateTime.Now.Millisecond.ToString());
                    //request.AddHeader("Accept", "application/json");
                    client.CookieContainer = session;
                    var resp = await client.ExecuteTaskAsync(request);
                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        return resp.ResponseUri.ToString();
                    }
                    else
                    {
                        if (!cachebust)
                            return await GetVideoUrl(media, true);
                        else
                            throw new Exception("Cannot find video");
                    }
                }
                else
                {
                    return @"file:///" + videofile;
                }
            }
        }

        /// <summary>
        /// Gets list of available bed track music to use.
        /// </summary>
        /// <returns></returns>
        public async Task<List<Music>> GetMusic()
        {
            var req = new RestRequest("/api/editing/music");
            var resp = await GetAResponse<List<Music>>(req, new CancellationToken());
            return resp;
        }

        /// <summary>
        /// Gets real temporary URL with no redirects to the completed edit video.
        /// </summary>
        /// <param name="edit"></param>
        /// <returns></returns>
        public async Task<string> GetEditUrl(Edit edit)
        {
            //get the redirect url of the video:
            //TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
            var request = new RestRequest(new Uri(server + "/watch/getvideo/" + edit.id), Method.HEAD);
            request.AddQueryParameter("cachebust", DateTime.Now.Millisecond.ToString());
            client.CookieContainer = session;
            var resp = await client.ExecuteTaskAsync(request);
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                return resp.ResponseUri.ToString();
            }
            else
            {
                throw new Exception("Cannot find video");
            }
        }

        /// <summary>
        /// Updates media with new metadata.
        /// </summary>
        /// <param name="media"></param>
        public async Task UpdateMeta(MediaItem media)
        {

            //update this in the background:
            await GetAResponsePost(new RestRequest("/media/update/"), new SailsSocket.MediaArgs() { id = media.id, static_meta = JsonConvert.SerializeObject(media.Static_Meta), timed_meta = JsonConvert.SerializeObject(media.TimedMeta) }, new CancellationTokenSource().Token);

            //await sails.Get("/media/update/", new SailsSocket.MediaArgs() { id = media.id, static_meta = JsonConvert.SerializeObject(media.Static_Meta), timed_meta = JsonConvert.SerializeObject(media.TimedMeta) });
        }

        /// <summary>
        /// List of edits for the CurrentUser.
        /// </summary>
        public Dictionary<BootleggerEditStatus, List<Edit>> MyEdits
        {
            get
            {
                lock (database)
                {
                    var edits = (from n in database.Table<Edit>() where n.user_id == CurrentUser.id select n).ToList();
                    return (from n in edits
                            where n.media != null && n.media.Count > 0
                            group n by (n.EditStatus) into newGroup
                            orderby newGroup.Key
                            select newGroup).ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.failed).ThenByDescending(o => o.progress).ThenByDescending(o => o.createdAt).ToList());
                }
            }
        }

        /// <summary>
        /// Returns media that I have captured that is available for editing for the CurrentEvent, i.e. videos that have been successfully uploaded.
        /// </summary>
        public List<MediaItem> MyMediaEditing
        {
            get
            {
                //if public editing -- list all media from this shoot:
                if (CurrentEvent?.publicedit ?? false)
                {
                    lock (database)
                        return (from n in database.Table<MediaItem>()
                                where n.event_id == CurrentEvent.id && n.Status == MediaItem.MediaStatus.DONE
                                orderby n.CreatedAt descending
                                select n).ToList();
                }
                else
                {
                    //only list my media
                    lock (database)
                        return (from n in database.Table<MediaItem>()
                                where n.event_id == CurrentEvent.id && n.created_by == CurrentUser.id && n.Status == MediaItem.MediaStatus.DONE
                                orderby n.CreatedAt descending
                                select n).ToList();
                }
            }
        }

        /// <summary>
        /// Returns cached media from id.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public MediaItem GetMediaItem(string id)
        {
            lock (database)
                return database.Find<MediaItem>(id);
        }

        /// <summary>
        /// Returns list of media waiting to upload. If CurrentEvent is set, only returns media for the CurrentEvent.
        /// </summary>
        public List<MediaItem> UploadQueueEditing
        {
            get
            {
                lock (UploadQueue)
                {
                    if (CurrentEvent != null)
                        return (from n in UploadQueue
                                where n.Static_Meta[MetaDataFields.EventId] == CurrentEvent.id
                                select n).ToList();
                    else
                        return UploadQueue;
                }
            }
        }

        public string CustomAppBuildId { get; private set; }

        public async Task<string> GetEventFromShortcode(string code)
        {
            var req = new RestRequest("/event/lookupshoot/" + code);
            var resp = await GetAResponse<Hashtable>(req, new CancellationToken());
            return resp["id"].ToString();
        }

        /// <summary>
        /// Removes (deletes) an item of media from the local device. If it has already been uploaded in any form, then it will notify the server.
        /// </summary>
        /// <param name="mediaItem"></param>
        public async Task RemoveLocalFile(MediaItem mediaItem)
        {
            //remove from local list
            lock (UploadQueue)
            {
                UploadQueue.Remove(mediaItem);
                lock (database)
                    database.Delete(mediaItem);
            }

            //   /media/remove/:id
            if (mediaItem.Status != MediaItem.MediaStatus.NOTONSERVER)
            {
                var req = new RestRequest("/media/remove/" + mediaItem.id);
                var resp = await GetAResponse(req, new CancellationToken());
            }
        }

        /// <summary>
        /// Put the API in review and editing mode for the given event.
        /// </summary>
        /// <param name="event"></param>
        /// <param name="cancel"></param>
        /// <returns></returns>
        public async Task ConnectForReview(bool lowbandwidth, Shoot @event, CancellationToken cancel)
        {
            Console.WriteLine(CurrentEvent?.topics.Count());
            //stops any current uploads
            CanUpload = false;

            //TODO: THIS INFORMATION SHOULD BE CACHED??
            var test = EventCache;
            //try get the event from the cache
            var ev = from n in EventCache where n.Event.id == @event.id select n;
            if (ev.Count() == 1)
                CurrentEvent = ev.First().Event;

            Console.WriteLine(CurrentEvent?.topics.Count());

            if (!lowbandwidth)
            {
                try
                {
                    //attempt to update:
                    var tmpCurrentEvent = await GetEventInfo(@event.id, cancel);
                    if (CurrentEvent == null)
                        CurrentEvent = tmpCurrentEvent;
                    else
                    {
                        CurrentEvent.numberofclips = tmpCurrentEvent.numberofclips;
                        CurrentEvent.numberofcontributors = tmpCurrentEvent.numberofcontributors;
                    }

                    if (string.IsNullOrEmpty(CurrentEvent.localroleimage))
                        CurrentEvent.localroleimage = CurrentEvent.roleimg;
                }
                catch (Exception)
                {
                    //cant get up-to-date event info:

                    //lock (database)
                    //    CurrentEvent = database.Find<Shoot>(@event.id);

                    if (CurrentEvent == null || CurrentEvent.shottypes.Count == 0)
                        throw new Exception("Shoot has not been cached.");

                }
            }
            else
            {
                if (CurrentEvent == null || CurrentEvent.shottypes.Count == 0)
                    throw new Exception("Shoot has not been cached.");
            }

            CurrentEvent.private_user_id = CurrentUser.id;

            try
            {
                lock (database)
                    database.Update(CurrentEvent);
            }
            catch
            {
                lock (database)
                    database.Insert(CurrentEvent);
            }

            Console.WriteLine(CurrentEvent?.topics.Count());
        }

        /// <summary>
        /// Leave review and editing mode, setting CurrentEvent to null.
        /// </summary>
        public void DisconnectForReview()
        {
            //TODO
            //cancel task for downloading everyones media
            if (_medialoader != null)
                _medialoader.CancelAsync();

            //disconnect socket handlers??
            CurrentEvent = null;

            //CurrentEvent = null;
            StopSocket();
        }

        BackgroundWorker _medialoader;

        int _currentpage = 0;
        int _pagelimit = 100;

        /// <summary>
        /// List of filter types that can be applied to the QueryMedia function. String value is a key that can be used for UI lookup and translation.
        /// </summary>
        public static Dictionary<MediaItemFilterType, string> MediaItemFilter = new Dictionary<MediaItemFilterType, string>() {
                     {MediaItemFilterType.CONTRIBUTOR,"Contributor" },
                     {MediaItemFilterType.DATE,"DateShot" },
                    //{ MediaItemFilterType.STARS,"Stars" },
                    {MediaItemFilterType.SHOT,"Shot" },
                    {MediaItemFilterType.ROLE,"Camera" },
                    {MediaItemFilterType.LENGTH,"Length" },
                    {MediaItemFilterType.PHASE,"ShootPhase" },
                    {MediaItemFilterType.TOPIC,"Topic" }
                };



        private bool IsEditable(MediaItem n)
        {
            var canuse = (CurrentEvent?.publicedit ?? false) || CurrentUser.id == n.id;
            return (n.Status == MediaItem.MediaStatus.DONE && (!string.IsNullOrEmpty(n.lowres) && n.MediaType == Shot.ShotTypes.VIDEO) || n.MediaType == Shot.ShotTypes.AUDIO) && canuse;
        }

        private bool TopicFilter(MediaItem n, List<string> filter)
        {
            //if(n.Static_Meta?.ContainsKey($"{((!WhiteLabelConfig.PUBLIC_TOPICS) ? BootleggerClient.CurrentUser?.id : "")}-{MetaDataFields.Topics}") ?? false)
            //{
            //    var len = n.Static_Meta?[$"{((!WhiteLabelConfig.PUBLIC_TOPICS) ? BootleggerClient.CurrentUser?.id : "")}-{MetaDataFields.Topics}"].Split(',');

            //}

            if (filter.Count == 0)
                return (
                    //contains key:
                    n.Static_Meta?.ContainsKey($"{((!WhiteLabelConfig.PUBLIC_TOPICS) ? BootleggerClient.CurrentUser?.id : "")}-{MetaDataFields.Topics}") ?? false) &&
                    //key has something in it:
                    n.Static_Meta?[$"{((!WhiteLabelConfig.PUBLIC_TOPICS) ? BootleggerClient.CurrentUser?.id : "")}-{MetaDataFields.Topics}"].Split(',').Length > 0;
            else
            {
                if ((n.Static_Meta?.ContainsKey($"{((!WhiteLabelConfig.PUBLIC_TOPICS) ? BootleggerClient.CurrentUser?.id : "")}-{MetaDataFields.Topics}") ?? false))
                {
                    var meta = n.Static_Meta[$"{((!WhiteLabelConfig.PUBLIC_TOPICS) ? BootleggerClient.CurrentUser?.id : "")}-{MetaDataFields.Topics}"].Split(',');
                    return filter.Any(o => meta.Contains(o));
                }
                else
                    return false;
            }
        }

        public List<MediaItem> QueryMediaByTopic(List<string> filter)
        {
            //var test = from n in database.Table<MediaItem>() where n.event_id == CurrentEvent.id && IsEditable(n) select n;
            //var list = test.ToList();
            lock (database)
            {
                var results = (from n in database.Table<MediaItem>()
                               where n.event_id == CurrentEvent.id
                               && n.Contributor != null
                               && n.Contributor != ""
                               select n)
                        .Where(IsEditable)
                        .Where(n => TopicFilter(n, filter))
                        .OrderBy(n => n.Static_Meta?[$"{((!WhiteLabelConfig.PUBLIC_TOPICS) ? BootleggerClient.CurrentUser?.id : "")}-{MetaDataFields.Topics}"])
                        .ToList();

                //var topics = results.Select(n => n.Static_Meta["-topics"]);
                return results;
            }
        }

        /// <summary>
        /// Obtain a sorted and grouped list from the local cache of all media available to edit for CurrentEvent.
        /// </summary>
        /// <param name="filter">Grouping category to use</param>
        /// <param name="dir">Direction of sort</param>
        /// <returns></returns>
        public Dictionary<string, List<MediaItem>> QueryMedia(MediaItemFilterType filter = MediaItemFilterType.CONTRIBUTOR, MediaItemFilterDirection dir = MediaItemFilterDirection.DESCENDING)
        {
            //var test = from n in database.Table<MediaItem>() where n.event_id == CurrentEvent.id && IsEditable(n) select n;
            //var list = test.ToList();
            lock (database)
            {
                switch (filter)
                {
                    case MediaItemFilterType.DATE:
                        return (
                            from n in database.Table<MediaItem>()
                            where n.event_id == CurrentEvent.id && n.Contributor != null && n.Contributor != ""
                            group n by n.CreatedAt.DayOfYear into newGroup
                            orderby newGroup.Key
                            select newGroup).OrderBy(o => o.Key, dir == MediaItemFilterDirection.DESCENDING).ToDictionary(g => g.First().CreatedAt.ToString("ddd d MMM yy"), g => (dir == MediaItemFilterDirection.ASCENDING) ? g.OrderBy(o => o.CreatedAt).Where(IsEditable).ToList() : g.OrderByDescending(o => o.CreatedAt).Where(IsEditable).ToList());
                    case MediaItemFilterType.LENGTH:
                        return (
                            from n in database.Table<MediaItem>()
                            where n.event_id == CurrentEvent.id && n.Contributor != null && n.Contributor != ""
                            group n by (((int)n.ClipLength.TotalSeconds / 5) * 5) into newGroup
                            orderby newGroup.Key
                            select newGroup).OrderBy(o => o.Key, dir == MediaItemFilterDirection.ASCENDING).ToDictionary(g => (((int)g.First().ClipLength.TotalSeconds / 5) * 5).ToString() + " secs", g => (dir == MediaItemFilterDirection.ASCENDING) ? g.OrderBy(o => o.ClipLength).Where(IsEditable).ToList() : g.OrderByDescending(o => o.ClipLength).Where(IsEditable).ToList());
                    case MediaItemFilterType.PHASE:
                        return (
                            from n in database.Table<MediaItem>()
                            where n.event_id == CurrentEvent.id && n.Contributor != null && n.Contributor != ""
                            group n by (n.Static_Meta[MetaDataFields.MetaPhase]) into newGroup
                            orderby newGroup.Key
                            select newGroup).OrderBy(o => o.Key).ToDictionary(g => g.First().meta.phase_ex[MetaDataFields.Name].ToString(), g => (dir == MediaItemFilterDirection.ASCENDING) ? g.OrderBy(o => o.ClipLength).Where(IsEditable).ToList() : g.OrderByDescending(o => o.ClipLength).Where(IsEditable).ToList());
                    case MediaItemFilterType.ROLE:
                        //var test = database.Table<MediaItem>().ToList();
                        return (
                            from n in database.Table<MediaItem>()
                            where n.event_id == CurrentEvent.id && n.Contributor != null && n.Contributor != ""
                            group n by (n.Static_Meta[MetaDataFields.Role]) into newGroup
                            orderby newGroup.Key
                            select newGroup).OrderBy(o => o.Key).ToDictionary(g => g.First().meta.role_ex[MetaDataFields.Name].ToString(), g => (dir == MediaItemFilterDirection.ASCENDING) ? g.OrderBy(o => o.ClipLength).Where(IsEditable).ToList() : g.OrderByDescending(o => o.ClipLength).Where(IsEditable).ToList());
                    case MediaItemFilterType.SHOT:
                        return (
                           from n in database.Table<MediaItem>()
                           where n.event_id == CurrentEvent.id && n.Contributor != null && n.Contributor != ""
                           group n by ((n.Static_Meta.ContainsKey(MetaDataFields.Shot)) ? n.Static_Meta[MetaDataFields.Shot] : "Unknown") into newGroup
                           orderby newGroup.Key
                           select newGroup).OrderBy(o => o.Key).ToDictionary(g => g.First().meta.shot_ex[MetaDataFields.Name].ToString(), g => (dir == MediaItemFilterDirection.ASCENDING) ? g.OrderBy(o => o.ClipLength).Where(IsEditable).ToList() : g.OrderByDescending(o => o.ClipLength).Where(IsEditable).ToList());
                    case MediaItemFilterType.TOPIC:
                        var key = $"{((!WhiteLabelConfig.PUBLIC_TOPICS) ? BootleggerClient.CurrentUser?.id : "")}-{MetaDataFields.Topics}";
                        return (
                           from n in database.Table<MediaItem>()
                           where n.event_id == CurrentEvent.id && n.Contributor != null && n.Contributor != ""
                           select n)
                           .ToList()
                           .Where(n => TopicFilter(n, new List<string>()))
                           .GroupBy(n => (n.Static_Meta.ContainsKey(key) ? n.Static_Meta[key] : "Unknown"), b => b)
                           .ToDictionary(g => g.Key, g => (dir == MediaItemFilterDirection.ASCENDING) ? g.OrderBy(o => o.ClipLength).Where(IsEditable).ToList() : g.OrderByDescending(o => o.ClipLength).Where(IsEditable).ToList());
                    case MediaItemFilterType.CONTRIBUTOR:
                    default:
                        return (
                                from n in database.Table<MediaItem>()
                                where n.event_id == CurrentEvent.id && n.Contributor != null && n.Contributor != ""
                                group n by n.Contributor into newGroup
                                orderby newGroup.Key
                                select newGroup)
                                .ToDictionary(
                                    g => g.Key,
                                    g => (dir == MediaItemFilterDirection.ASCENDING) ? g.OrderBy(o => o.CreatedAt).Where(IsEditable).ToList() : g.OrderByDescending(o => o.CreatedAt).Where(IsEditable).ToList())
                                    .OrderBy(a => (a.Key == CurrentUser.displayName) ? 1 : 2)
                                    //.ThenBy(a=>a)
                                    .ToDictionary(g=>g.Key,g=>g.Value);
                }
            }
        }


        /// <summary>
        /// Initiates downloading all media for the CurrentEvent.
        /// OnMoreMediaLoaded and OnMediaLoadingComplete are fired.
        /// </summary>
        /// <param name="cancel"></param>
        public void GetEveryonesMedia(CancellationToken cancel)
        {
            int totalcount = 0;
            int localtotal = -1;

            lock (database)
                //check if the number is different to the one in the db:
                localtotal = (from n in database.Table<MediaItem>()
                              where n.event_id == CurrentEvent.id
                              select n).Count();

            RestRequest request1 = new RestRequest("media/mediacount/" + CurrentEvent.id);

            //TEMP FIX TO FORCE DOWNLOAD OF METADATA:

            //try
            //{
            //    var ff = await GetAResponse<Hashtable>(request1, cancel);

            //    if (int.Parse(ff["count"].ToString()) == localtotal)
            //    {
            //        if (OnMediaLoadingComplete != null)
            //            lock (database)
            //                OnMediaLoadingComplete(database.Table<MediaItem>().Where(r => r.event_id == CurrentEvent.id).Count());
            //        return;
            //    }
            //}
            //catch
            //{
            //    OnMediaLoadingComplete?.Invoke((database.Table<MediaItem>().Where(r => r.event_id == CurrentEvent.id).Count()));
            //    return;
            //}

            //database.DeleteAll<MediaItem>();
            List<MediaItem> inserted = new List<MediaItem>();

            int knowndeleted = 0;

            //RE-WRITING FOR SQL QUERIES
            if (_medialoader == null || !_medialoader.IsBusy)
            {
                _medialoader = new BackgroundWorker();
                _medialoader.WorkerSupportsCancellation = true;
                _medialoader.DoWork += (o, e) =>
                {
                    try
                    {
                        bool keepgoing = true;
                        _currentpage = 0;
                        while (keepgoing && !cancel.IsCancellationRequested && CurrentEvent != null)
                        {
                            RestRequest request = new RestRequest("media/nicejson/" + CurrentEvent.id + "?limit=" + _pagelimit + "&skip=" + (_currentpage * _pagelimit) + "&" + DateTime.Now.Millisecond);
                            var f = GetAResponse<List<MediaItem>>(request, cancel);
                            Task.WaitAll(f);
                            //var task = await GetAResponse(request);
                            var ress = f.Result;
                            totalcount += ress.Count;
                            //Console.WriteLine("batch count: " + ress.Count);
                            if (ress.Count() < _pagelimit)
                                keepgoing = false;
                            inserted.Clear();

                            foreach (var m in ress)
                            {
                                //Console.WriteLine("lowres-in: " + m.lowres);
                                if (!m.deleted)
                                {
                                    lock (database)
                                    {
                                        var mm = database.Find<MediaItem>(m.id);
                                        //try
                                        //{
                                        //    Console.WriteLine(m.Static_Meta?["-topics"]);
                                        //}
                                        //catch
                                        //{

                                        //}
                                        if (mm != null && mm.Status == MediaItem.MediaStatus.DONE)
                                        {
                                            database.Update(m);

                                            //var check = database.Find<MediaItem>(m.id);
                                            //try
                                            //{
                                            //    var topcis = check.Static_Meta?["-topics"];
                                            //    var fas = false;
                                            //}
                                            //catch { }

                                        }
                                        else if (mm == null)
                                        {
                                            database.Insert(m);
                                            inserted.Add(m);
                                        }
                                        else
                                        {

                                        }
                                    }
                                }
                                else
                                {
                                    knowndeleted++;
                                }
                            }

                            if (OnMoreMediaLoaded != null)
                                OnMoreMediaLoaded(inserted);

                            _currentpage++;
                        }
                    }
                    catch (Exception ex)
                    {
                        //failed to load media

                    }
                    //Console.WriteLine("totalcount: " + totalcount);

                    //Console.WriteLine("known deleted: " + knowndeleted);

                };

                //TODO work out the ids that have actually been inserted / changed

                _medialoader.RunWorkerCompleted += (o, e) =>
                {
                    if (OnMediaLoadingComplete != null && CurrentEvent != null)
                        lock (database)
                            OnMediaLoadingComplete(0);

                    //OnMediaLoadingComplete(database.Table<MediaItem>().Where(r => r.event_id == CurrentEvent.id).Count());
                };
                _medialoader.RunWorkerAsync();
            }
        }

        /// <summary>
        /// Gets all the edits from the server for this CurrentUser. Also registers for updates on Edits that have not finished processing.
        /// </summary>
        /// <param name="cancel"></param>
        /// <returns></returns>
        public async Task GetMyEdits(CancellationToken cancel, bool fireevent)
        {
            try
            {
                List<Edit> alledits = new List<Edit>();
                lock (database)
                    alledits = database.Table<Edit>().ToList();

                RestRequest request = new RestRequest("watch/myedits/");
                var ress = await GetAResponse<List<Edit>>(request, cancel);
                //var ress = JsonConvert.DeserializeObject< List<Edit>>(s);
                foreach (var r in ress)
                {
                    var mm = alledits.FindLast(o => o.id == r.id);

                    if (mm != null)
                    {
                        lock (database)
                        {
                            database.Update(r);
                        }

                        if (r.progress != mm.progress || r.status != mm.status || r.code != mm.code || r.path != mm.path || r.failed != mm.failed)
                        {
                            //Console.WriteLine(mm.progress);
                            OnEditUpdated?.Invoke(r);
                        }
                    }
                    else
                    {
                        lock (database)
                        {
                            database.Insert(r);
                        }
                    }
                }

                //find all the edits in the db but not in the dump, then remove from the db
                var missing = alledits.Select(o => o.id).Except(ress.Select(o => o.id));

                lock (database)
                {
                    foreach (var e in missing)
                    {
                        database.Delete<Edit>(e);
                    }
                }
            }
            catch (Exception e)
            {
                FireDebug(e.ToString());
            }
        }

        /// <summary>
        /// Gets all the media from the server that this user has produced for the CurrentEvent
        /// </summary>
        /// <param name="cancel"></param>
        /// <returns></returns>
        public async Task GetMyMedia(CancellationToken cancel)
        {
            if (!loadingmymedia && CurrentEvent != null)
            {
                loadingmymedia = true;
                RestRequest request = new RestRequest("media/mymedia/" + CurrentEvent.id);
                var ress = await GetAResponse<List<MediaItem>>(request, cancel);
                //var ress = JsonConvert.DeserializeObject<List<MediaItem>>(s);
                var isuploaded = from n in ress where n.path != "" select n;
                foreach (var r in ress)
                {
                    if (!r.deleted)
                    {
                        lock (database)
                        {
                            var ll = database.Find<MediaItem>(r.id);
                            if (ll != null && ll.Status == MediaItem.MediaStatus.DONE)
                            {
                                database.Update(r);
                            }
                            else if (ll == null)
                            {
                                database.Insert(r);
                            }
                        }
                    }
                }
                //MyMediaEditing.AddRange(isuploaded);
                loadingmymedia = false;
            }
        }

        /// <summary>
        /// Not implemented yet
        /// </summary>
        /// <param name="edit"></param>
        public async void Star(Edit edit)
        {
            //media/star
        }

        async Task<T> DecodeJson<T>(string s)
        {
            Task<T> thetask = Task<T>.Factory.StartNew(() => JsonConvert.DeserializeObject<T>(s));
            return await thetask;
        }

        /// <summary>
        /// Get up-to-date information about this edit from the server
        /// </summary>
        /// <param name="_id"></param>
        /// <param name="cancel"></param>
        /// <returns></returns>
        public async Task<Edit> GetEdit(string _id, CancellationToken cancel)
        {
            //lock(database)
            //    return database.Find<Edit>(o => o.id == _id);

            return await GetAResponse<Edit>(new RestRequest("/watch/edit/" + _id), cancel);
        }

        /// <summary>
        /// Delete edit from local device and server.
        /// </summary>
        /// <param name="edit"></param>
        /// <returns></returns>
        public async Task DeleteEdit(Edit edit)
        {
            try
            {
                await GetAResponse<Edit>(new RestRequest("/watch/deleteedit/" + edit.id), new CancellationToken());
                lock (database)
                    database.Delete<Edit>(edit.id);
            }
            catch (Exception)
            {

                await Task.Yield();
            }
        }
#if SOCKETS
        public async void RegisterForEditUpdates()
#else
        public void RegisterForEditUpdates()
#endif
        {
            string[] edits = new string[0];
            //lock(database)
            //{
            List<Edit> alledits = new List<Edit>();
            lock (database)
                alledits = database.Table<Edit>().ToList();

            edits = (from n in alledits where n.user_id == CurrentUser.id && n.progress < 97 && !n.failed select n.id).ToArray();
            //edits = (from n in database.Table<Edit>() where n.user_id == CurrentUser.id && n.progress < 98 && !n.failed select n.id).ToArray();
            //find all edits that are not complete, and register with the server to get updates on their progress...
            //}
            if (edits.Length > 0)
            {
#if SOCKETS
                        await StartSocket();
                        await sails.Get("/watch/editupdates", new SailsSocket.EditArgs() { edits = edits });
#else
                if (editprogresspoller == null)
                //editprogresspoller?.CancelAsync();
                //if (editprogresspoller == null)
                //{
                //start thread to poll for event updates:
                {
                    editprogresspoller = new BackgroundWorker();
                    editprogresspoller.WorkerSupportsCancellation = true;
                    editprogresspoller.DoWork += Editprogresspoller_DoWork;
                    editprogresspoller.RunWorkerAsync();
                }
                //}
#endif
            }
        }

        private async void Editprogresspoller_DoWork(object sender, DoWorkEventArgs e)
        {
            List<Edit> alledits = new List<Edit>();
            var keepgoing = true;
            while (keepgoing && !e.Cancel)
            {
                //get unfinished edits:
                lock (database)
                    alledits = database.Table<Edit>().ToList();

                var unfinished = (from n in alledits where n.user_id == CurrentUser.id && n.progress < 97 && !n.failed select n.id).ToArray();
                //if (unfinished.Length == 0)
                //{
                //    keepgoing = false;
                //    break;
                //}

                if (unfinished.Length > 0)
                {
                    //poll for updates, and update db:
                    await GetMyEdits(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token, true);
                }

                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        BackgroundWorker editprogresspoller;

        /// <summary>
        /// Stop getting updates when edits have changed
        /// </summary>
#if SOCKETS
        public async void UnregisterForEditUpdates()
#else
        public void UnregisterForEditUpdates()
#endif
        {
            if (CurrentUser != null)
            {
                string[] edits = new string[0];
                //lock(database)
                //{
                List<Edit> alledits = new List<Edit>();
                lock (database)
                    alledits = database.Table<Edit>().ToList();

                edits = (from n in alledits where n.user_id == CurrentUser.id && n.progress < 97 && !n.failed select n.id).ToArray();
                //edits = (from n in database.Table<Edit>() where n.user_id == CurrentUser.id && n.progress < 98 && !n.failed select n.id).ToArray();
                //find all edits that are not complete, and register with the server to get updates on their progress...
                //}
                if (edits.Length > 0)
                {
#if SOCKETS
                            try
                            {
                                await StartSocket();
                                await sails.Get("/watch/canceleditupdates", new SailsSocket.EditArgs() { edits = edits });
                                await StopSocket();
                            }
                            catch
                            {
                                //silent fail -- could be because the connection is in the process of being closed.
                            }
#else
                    //cancel background worker:
                    editprogresspoller?.CancelAsync();
#endif
                }
            }
        }

        private void AddTopicLabels(Edit edit)
        {
            //new code to add topic labels:

            if (CurrentEvent != null)
            {
                foreach (var media in edit.media)
                {
                    try
                    {
                        var foundmedia = database.Get<MediaItem>(media.id);
                        var topicid = foundmedia.Static_Meta[$"{((!WhiteLabelConfig.PUBLIC_TOPICS) ? BootleggerClient.CurrentUser?.id : "")}-{MetaDataFields.Topics}"].Split(',');
                        //var topicid = media.Static_Meta[]
                        var tag = CurrentEvent.topics.Find((arg) => arg.id == topicid.First());
                        if (tag.burn)
                            media.tag = tag;
                        else
                            media.tag = null;
                    }
                    catch
                    {
                        //no topic found with this id...
                    }
                }
            }
        }

        /// <summary>
        /// Update an edit, but without initiating processing.
        /// </summary>
        /// <param name="edit"></param>
        /// <returns></returns>
        public async Task SaveEdit(Edit edit)
        {
            //clone so we are not editing the same one...

            var tmpEdit = new Edit
            {
                id = edit.id,
                title = edit.title,
                media = edit.media.ToList()
            };

            //update an edit -- but without starting it
            if (tmpEdit.media.Last().id == null && string.IsNullOrEmpty(tmpEdit.media.Last().titletext))
                tmpEdit.media.RemoveAt(edit.media.Count - 1);

            AddTopicLabels(tmpEdit);

            //Console.WriteLine(JsonConvert.SerializeObject(new SailsSocket.EditArgs() { title = edit.title, description = edit.description, media = edit.media }));

            var res = await GetAResponsePost(new RestRequest("/watch/saveedit/" + ((tmpEdit.id != null) ? tmpEdit.id : "")), new SailsSocket.EditArgs() { title = tmpEdit.title, description = tmpEdit.description, media = tmpEdit.media }, new CancellationTokenSource().Token);

            var e = await DecodeJson<Edit>(res);

            edit.id = e.id;

            lock (database)
            {
                try
                {
                    var there = database.Get<Edit>(edit.id);
                    database.Update(e);
                }
                catch
                {
                    database.Insert(e);
                }
            }
        }

        ///// <summary>
        ///// Restart a failed edit.
        ///// </summary>
        ///// <param name="edit"></param>
        ///// <returns></returns>
        //public async Task RestartEdit(Edit edit)
        //{
        //    //returns code, sharing link?
        //    //watch/newedit
        //    if (edit.media.Last().id == null)
        //        edit.media.RemoveAt(edit.media.Count - 1);

        //    var res = await GetAResponsePost(new RestRequest("/watch/restartedit/" + ((edit.id != null) ? edit.id : "")), new SailsSocket.EditArgs() { }, new CancellationTokenSource().Token);

        //    //string res = await sails.Post("/watch/restartedit/" + ((edit.id != null) ? edit.id : ""), new SailsSocket.EditArgs() { });
        //    //var e = await DecodeJson<Edit>(res);
        //    edit.progress = 0;
        //    edit.failed = false;
        //    lock (database)
        //        database.Update(edit);
        //    RegisterForEditUpdates();
        //}

        /// <summary>
        /// Save and then start processing an edit. Also registers for push updates about this edit.
        /// </summary>
        /// <param name="edit"></param>
        /// <returns></returns>
        public async Task StartEdit(Edit edit)
        {
            //returns code, sharing link?
            //watch/newedit
            if (edit.media.Last().id == null)
                edit.media.RemoveAt(edit.media.Count - 1);

            AddTopicLabels(edit);

            Edit returnededit = edit;
            bool cantprocess = false;

            try
            {
                var res = await GetAResponsePost(new RestRequest("/watch/newedit/" + ((edit.id != null) ? edit.id : "")), new SailsSocket.EditArgs() { title = edit.title, description = edit.description, media = edit.media }, new CancellationTokenSource().Token);
                returnededit = await DecodeJson<Edit>(res);
            }
            catch (StoriesDisabledException e)
            {
                returnededit = await DecodeJson<Edit>(e.Content);
                returnededit.progress = null;
                returnededit.code = null;
                returnededit.shortlink = null;
                cantprocess = true;
            }

            //string res = await sails.Post("/watch/newedit/" + ((edit.id != null) ? edit.id : ""), new SailsSocket.EditArgs() { title = edit.title, description = edit.description, media = edit.media });
            

            edit.id = returnededit.id;

            lock (database)
            {
                try
                {
                    var there = database.Get<Edit>(edit.id);
                    database.Update(returnededit);
                }   
                catch
                {
                    database.Insert(returnededit);
                }
            }

            if (cantprocess)
                throw new StoriesDisabledException();

            RegisterForEditUpdates();
        }
        #endregion

        #region Create Shoot

        public async Task<List<ShootTemplate>> GetOnlineSeedTemplates()
        {
            var results = await GetAResponse<List<ShootTemplate>>(new RestRequest("/api/commission/seedtemplates/"), new CancellationToken());
            return results.Where(arg1 => arg1.original).ToList();
        }

        public async Task<List<ShootTemplate>> GetSeedTemplates(string content)
        {
            var obj = await Task<List<ShootTemplate>>.Factory.StartNew(() =>
            {
                try
                {
                    return JsonConvert.DeserializeObject<List<ShootTemplate>>(content);
                }
                catch (Exception f)
                {
                    throw f;
                }
            }
            );
            return obj;
        }

        public async Task<Shoot> CreateShoot(string name, bool publicshoot, ShootTemplate template)
        {
            var newshoot = new Dictionary<string, object>();
            newshoot.Add("eventtype", new Dictionary<string, object>());
            (newshoot["eventtype"] as Dictionary<string, object>).Add("id", template.id);
            newshoot.Add("name", name);
            newshoot.Add("ispublic", publicshoot);

            var content = await GetAResponsePost(new RestRequest("/api/commission/createinstantshoot/"), newshoot, new CancellationToken());
            var obj = await Task<Shoot>.Factory.StartNew(() =>
                {
                    try
                    {
                        return JsonConvert.DeserializeObject<Shoot>(content);
                    }
                    catch (Exception f)
                    {
                        throw f;
                    }
                }
            );

            return obj;
        }
        #endregion

        public void OnPause()
        {
            //stop threads and sockets:
            pause_worker_flag.Reset();
        }

        public void OnResume()
        {
            //resume threads and sockets:
            pause_worker_flag.Set();
        }

        public event Action<int, int, int> OnDownloadProgress;
        public event Action<int> OnDownloadComplete;

        TaskCompletionSource<bool> downloadtcs;
        private readonly DirectoryInfo videoslocation;

        public async Task CacheVideos(Shoot ev, CancellationToken cancel)
        {
            try
            {
                downloadtcs = new TaskCompletionSource<bool>();

                OnMediaLoadingComplete += Bootlegger_OnMediaLoadingComplete;
                //cache all videos for a given event:
                GetEveryonesMedia(cancel);
                //TODO: for each media that is valid, start a download request:

                await downloadtcs.Task;
                OnMediaLoadingComplete -= Bootlegger_OnMediaLoadingComplete;

                var valid = from n in MyMediaEditing where !string.IsNullOrEmpty(n.path) select n;

                videoslocation.CreateSubdirectory("bootlegger_cache");

                int totalvids = valid.Count();
                int donevids = 0;
                foreach (var m in valid)
                {
                    if (cancel.IsCancellationRequested)
                        return;

                    FileInfo tmpfile = new FileInfo(videoslocation.FullName + "/bootlegger_cache/" + m.id + ".part");
                    FileInfo zipfile = new FileInfo(videoslocation.FullName + "/bootlegger_cache/" + m.id + ".mp4");


                    if (!zipfile.Exists)
                    {

                        try
                        {
                            var url = await GetVideoUrl(m);

                            var t1 = Task.Factory.StartNew(() =>
                            {
                                try
                                {
                                    byte[] buffer = new byte[4096];

                                    WebRequest wr = WebRequest.Create(url);
                                    long counter = 0;
                                    using (WebResponse response = wr.GetResponse())
                                    {
                                        using (Stream responseStream = response.GetResponseStream())
                                        {
                                            using (FileStream memoryStream = tmpfile.OpenWrite())
                                            {
                                                int count = 0;
                                                do
                                                {
                                                    count = responseStream.Read(buffer, 0, buffer.Length);
                                                    memoryStream.Write(buffer, 0, count);
                                                    counter += count;
                                                    var counterval = (int)((((counter / (double)response.ContentLength) + donevids) / (double)totalvids) * 100);
                                                    Console.WriteLine(counterval);
                                                    OnDownloadProgress(counterval, donevids, totalvids);
                                                } while (count != 0 && !cancel.IsCancellationRequested);

                                                memoryStream.Flush();
                                                memoryStream.Close();
                                            }
                                        }
                                    }

                                    tmpfile.MoveTo(zipfile.FullName);
                                }
                                catch (Exception)
                                {

                                }
                            });

                            await t1;
                            donevids++;
                            OnDownloadProgress((int)((donevids / (double)totalvids) * 100), donevids, totalvids);
                        }
                        catch (Exception)
                        {

                        }
                    }
                    donevids++;
                }
            }
            catch (Exception)
            {

            }
        }

        private void Bootlegger_OnMediaLoadingComplete(int obj)
        {
            downloadtcs.TrySetResult(true);
        }
    }
}
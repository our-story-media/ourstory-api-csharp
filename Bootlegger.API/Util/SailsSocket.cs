
/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Bootleg.API.Model;
using Newtonsoft.Json;
#if SOCKETS
using SocketIOClient.Messages;
#endif


namespace Bootleg.API
{
    class SailsSocket
    {

        public event Action<string, bool, bool> OnNewMessage;
        public event Action<int> OnChangeRole;
        public event Action<int, string, int> OnChangeShot;
        public event Action<int> OnShootNow;
        public event Action<int> OnTimer;
        public event Action<bool, int> OnLive;
        public event Action<string> OnModeChange;
        public event Action OnEventStarted;
        public event Action<int, int, int, int> OnUserUpdate;
        public event Action<int> OnPhaseChange;
        public event Action<Shoot> OnEventUpdated;
        public event Action<Edit> OnEditUpdated;
        public event Action OnLoginElsewhere;
        public event Action OnCantReconnect;

#if SOCKETS
        SocketIOClient.Client socket;

        public SailsSocket(SocketIOClient.Client socket)
        {
            this.socket = socket;
            socket.Message += socket_Message;
        }

        void socket_Message(object sender, SocketIOClient.MessageEventArgs e)
        {
            //Console.WriteLine(e.Message.RawMessage);
            //FIX FOR BAD PARSING OF SAILS MESSAGE
            //Console.WriteLine(e.Message.Shoot);
            try
            {


                if (e.Message.MessageText != null)
                {

                    if (e.Message.Event == "message")
                    {

                    }
                    //Console.WriteLine(e.Message.MessageText);
                    
                    var sm = JsonConvert.DeserializeObject<SailsMessage>(e.Message.MessageText);

                    if ((e.Message.Event == "message" || e.Message.Event == "user") && sm.args[0].data.ContainsKey("msg"))
                    {
                        if (sm.args[0].data.ContainsKey("forcedie") && OnCantReconnect!=null)
                        {
                            OnCantReconnect();
                        }
                        else if (OnNewMessage != null)
                        {
                            OnNewMessage(sm.args[0].data["msg"].ToString(), sm.args[0].data.ContainsKey("dialog"), sm.args[0].data.ContainsKey("shots"));
                        }
                    }

                    //if (sm.model == "event" && sm.data.ContainsKey("timer") && OnTimer!=null)
                    //{
                    //    OnTimer(int.Parse(sm.data["timer"].ToString()));
                    //}

                    //if (sm.model == "event" && sm.data.ContainsKey("msg") && OnNewMessage != null)
                    //{
                    //    OnNewMessage(sm.data["msg"].ToString(), sm.data.ContainsKey("dialog"), sm.data.ContainsKey("shots"));
                    //}

                    if (e.Message.Event == "user" && sm.args[0].data.ContainsKey("changerole") && OnChangeRole != null)
                    {
                        OnChangeRole(int.Parse(sm.args[0].data["changerole"].ToString()));
                    }

                    if (e.Message.Event == "user" && sm.args[0].data.ContainsKey("getshot") && OnChangeShot != null)
                    {
                        OnChangeShot(int.Parse(sm.args[0].data["getshot"].ToString()), sm.args[0].data["meta"].ToString(), int.Parse(sm.args[0].data["coverage_class"].ToString()));
                    }

                    if (e.Message.Event == "user" && sm.args[0].data.ContainsKey("shootnow") && OnShootNow != null)
                    {
                        OnShootNow(int.Parse(sm.args[0].data["shootnow"].ToString()));
                    }

                    if (e.Message.Event == "user" && sm.args[0].data.ContainsKey("loginelsewhere") && OnLoginElsewhere != null)
                    {
                        OnLoginElsewhere();
                    }

                    if (e.Message.Event == "user" && sm.args[0].data.ContainsKey("live") && !sm.args[0].data.ContainsKey("length") && OnLive != null)
                    {
                        if (sm.args[0].data.ContainsKey("shot_length"))
                        {
                            OnLive(bool.Parse(sm.args[0].data["live"].ToString()), int.Parse(sm.args[0].data["shot_length"].ToString()));
                        }
                        else
                        {
                            OnLive(bool.Parse(sm.args[0].data["live"].ToString()), 0);
                        }
                    }

                    if (e.Message.Event == "user" && sm.args[0].data.ContainsKey("modechange") && OnModeChange != null)
                    {
                        OnModeChange(sm.args[0].data["modechange"].ToString());
                    }

                    if (e.Message.Event == "user" && sm.args[0].data.ContainsKey("length") && OnUserUpdate != null)
                    {
                        OnUserUpdate(int.Parse(sm.args[0].data["length"].ToString()), int.Parse(sm.args[0].data["warning"].ToString()), int.Parse(sm.args[0].data["live"].ToString()), int.Parse(sm.args[0].data["cameragap"].ToString()));
                    }

                    if (e.Message.Event == "user" && sm.args[0].data.ContainsKey("eventstarted") && OnEventStarted != null)
                    {
                        if (bool.Parse(sm.args[0].data["eventstarted"].ToString()))
                            OnEventStarted();
                    }

                    if (e.Message.Event == "user" && sm.args[0].data.ContainsKey("phasechange") && OnPhaseChange != null)
                    {
                        OnPhaseChange(int.Parse(sm.args[0].data["phasechange"].ToString()));
                    }

                    if (e.Message.Event == "user" && sm.args[0].data.ContainsKey("eventupdate") && OnEventUpdated != null)
                    {
                        OnEventUpdated(JsonConvert.DeserializeObject<Shoot>(sm.args[0].data["eventupdate"].ToString()));
                    }

                    if (e.Message.Event == "edits" && sm.args[0].data.ContainsKey("edit") && OnEditUpdated != null)
                    {
                        OnEditUpdated(JsonConvert.DeserializeObject<Edit>(sm.args[0].data["edit"].ToString()));
                    }
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

#else
        public SailsSocket(bool sockets)
        {
            //dummy
        }
#endif

        Stopwatch sw = new Stopwatch();
        internal void Get(string path)
        {
#if SOCKETS
            if (Bootlegger.DEBUGMODE)
            {
                sw.Reset();
                sw.Start();
            }

            if (socket != null && socket.IsConnected)
            {
                socket.Emit("get", new SailsArgs() { url = path, data = new ApiKeyArgs() {apikey=Bootlegger.API_KEY } }, callback: (oe) =>
                {
                    //Console.WriteLine(oe);
                    if (Bootlegger.DEBUGMODE)
                    {
                        sw.Stop();
                        Bootlegger.FireDebug("get " + path + ": " + sw.ElapsedMilliseconds);
                    }
                });
            }
#endif
        }

        internal Task<bool> Get(string path, object d = null)
        {
            var tcs = new TaskCompletionSource<bool>();
#if SOCKETS
            if (Bootlegger.DEBUGMODE)
            {
                sw.Reset();
                sw.Start();
            }
           

            const int timeoutMs = 5000;
            var ct = new CancellationTokenSource(timeoutMs);
            ct.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);
            if (d == null)
                d = new ApiKeyArgs();
            (d as ApiKeyArgs).apikey = Bootlegger.API_KEY;


            if (socket != null && socket.IsConnected)
            {
                socket.Emit("get", new SailsArgs() { url = path, data = d}, callback: (oe) =>
                {
                    if (Bootlegger.DEBUGMODE)
                    {
                        sw.Stop();
                        Bootlegger.FireDebug("get " + path + ": " + sw.ElapsedMilliseconds);
                    }
                    tcs.TrySetResult(true);
                });

                //Timer t = new Timer(new TimerCallback((o)=>{
                //    tcs.TrySetResult(true);
                //}),null,0,2000);
                
            }
            else
            {
                tcs.TrySetResult(true);
            }

#else
            tcs.TrySetResult(true);
#endif
            return tcs.Task;
        }


        internal Task<string> GetResult(string path, object d = null)
        {
            var tcs = new TaskCompletionSource<string>();
#if SOCKETS
            const int timeoutMs = 5000;
            var ct = new CancellationTokenSource(timeoutMs);
            ct.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);
            if (d == null)
                d = new ApiKeyArgs();
            (d as ApiKeyArgs).apikey = Bootlegger.API_KEY;
            socket.Emit("get", new SailsArgs() { url = path, data = d }, callback: (oe) =>
            {
                Console.WriteLine((oe as JsonEncodedEventMessage).Args[0].ToString().Replace("\\", string.Empty).TrimStart('"').TrimEnd('"'));
                tcs.TrySetResult((oe as JsonEncodedEventMessage).Args[0].ToString().Replace("\\", string.Empty).TrimStart('"').TrimEnd('"'));
            });
#else
            tcs.TrySetResult("");
#endif
            return tcs.Task;
        }


        internal Task<string> Post(string path, object d = null)
        {
            var tcs = new TaskCompletionSource<string>();
#if SOCKETS
            const int timeoutMs = 5000;
            var ct = new CancellationTokenSource(timeoutMs);
            ct.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);
            if (d == null)
                d = new ApiKeyArgs();
            (d as ApiKeyArgs).apikey = Bootlegger.API_KEY;
            socket.Emit("post", new SailsArgs() { url = path, data = d }, callback: (oe) =>
            {   
                tcs.TrySetResult((oe as JsonEncodedEventMessage).Args[0].ToString().Replace("\\", string.Empty).TrimStart('"').TrimEnd('"'));
            });
#else
            tcs.TrySetResult("");
#endif
            return tcs.Task;
        }

        internal class ApiKeyArgs
        {
            public string apikey;
        }

        internal class SailsEventArgs:ApiKeyArgs
        {
            public string id;

            public bool force { get; set; }
        }

        public class MediaArgs:ApiKeyArgs
        {
            public string id;
            public string timed_meta;
            public string static_meta;
        }

        public class EditArgs : ApiKeyArgs
        {
            public List<MediaItem> media;
            public string title;
            public string description;
            public string[] edits;
            public bool failed;
            public string failreason;
        }

        public class JsonMessage:ApiKeyArgs
        {
            public string id;
            public string msg;
            public int code;
            public string event_id;
            public string eventid;
            public int roleid;
            public int shotid;
            public bool confirm;
            public bool privacy;
            public string pushcode { get; set; }
            public string platform { get; set; }
        }

        class SailsArgs
        {
            public string url;
            public object data;
        }

        class SailsData
        {
            public string verb;
            public string id;
            public Dictionary<string, object> data;
        }

        class SailsMessage
        {
            public string name;
            public List<SailsData> args;
        }

        internal class CallbackMessage
        {
            public string name;
            public string args;
            public string data;
            public string type;
        }


    }
}

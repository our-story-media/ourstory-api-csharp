/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SQLite;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Bootleg.API.Model
{
    public class User
    {
        public User()
        {
            permissions = new Dictionary<string, bool>();
        }

        public DateTime LastTouched { get; set; }

        JObject _profile;
        public string profile_ser
        {
            get
            {
                //Console.WriteLine("ser: " + JsonConvert.SerializeObject(Meta));
                return JsonConvert.SerializeObject(_profile);
            }
            set
            {
                if (value != null)
                    _profile = JsonConvert.DeserializeObject<JObject>(value);
            }
        }


        //public string name { get; set; }
        [Ignore]
        public JObject profile { get { return _profile; } set { _profile = value; } }


        Dictionary<string, bool> _permissions;
        public string permissions_ser
        {
            get
            {
                //Console.WriteLine("ser: " + JsonConvert.SerializeObject(Meta));
                return JsonConvert.SerializeObject(_permissions);
            }
            set
            {
                if (value != null)
                    _permissions = JsonConvert.DeserializeObject<Dictionary<string, bool>>(value);
            }
        }


        //public string name { get; set; }
        [Ignore]
        public Dictionary<string, bool> permissions { get { return _permissions; } set { _permissions = value; } }
        [PrimaryKey]
        public string id { get; set; }
        public int CycleLength { get { return WarningLength + CountdownLength + CameraGap; } }
        public int WarningLength { get; set; }
        public int CountdownLength { get; set; }
        public string displayName { get { return profile["displayName"].ToString(); } }
		public string EmailAddress { get { return profile ["emails"].First["value"].ToString (); } }

        public string ProfileImg
        {
            get
            {
                try
                {
                    return ((profile["photos"] as JArray)[0] as JObject)["value"].ToString();
                }
                catch
                {
                    return "";
                }
            }
        }

        public override string ToString()
        {
            return "Name: " + displayName;
        }

        public int ShotLength { get; set; }
        public int CameraGap { get; set; }

        public string session { get; set; }
    }
}

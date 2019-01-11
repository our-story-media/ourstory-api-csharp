/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SQLite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bootleg.API.Model
{
    [Table("Events")]
    public class Shoot : IEquatable<Shoot>, IEqualityComparer<Shoot>
    {
        [PrimaryKey]
        public string id { get; set; }
        public string icon { get; set; }
        public string iconbackground { get; set; }
        public string description { get; set; }
        public string name { get; set; }
        public string joincode { get; set; }

        public Shoot()
        {
            //_topics = new List<Topic>();
        }

        //public string topics_ser
        //{
        //    get
        //    {
        //        return JsonConvert.SerializeObject(_topics);
        //        //return JsonConvert.SerializeObject(new List<Topic>()
        //        //{
        //           //     new Topic() { id = "1",color="#ff00ff", values=new Dictionary<string, string>(){{"en","ONLY ONE"} } }
        //           //});
        //    }
        //    set
        //    {
        //        if (value != null)
        //           _topics = JsonConvert.DeserializeObject<List<Topic>>(value);
        //    }
        //}

        //private List<Topic> _topics = new List<Topic>();

        //[Ignore]
        //public List<Topic> topics { get => _topics; set => _topics = value; }

        [Ignore]
        public List<Topic> topics { get; set; }

        //{ get {
        //        return new List<Topic>() {
        //    new Topic() { id = "1",color="#ff00ff", values=new Dictionary<string, string>(){{"en","asdasd"} } }
        //};
        //} }

        [Ignore]
        public DateTime? RealStarts
        {
            get
            {
                try
                {
                    return DateTime.ParseExact(starts + " " + starts_time, "dd-MM-yyyy htt", CultureInfo.InvariantCulture);
                }
                catch
                {
                    return null;
                }
            }
        }
        [Ignore]
        public DateTime? RealEnds
        {
            get
            {
                //try
                //{
                if (DateTime.TryParseExact(ends + " " + ends_time, "dd-MM-yyyy htt", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result))
                    return result;
                else
                    return null;
                //}
                //catch
                //{
                //    return null;
                //}
            }
        }

        public string organiserprofile { get; set; }
        public string organisedby { get; set; }

        public int numberofcontributors { get; set; }
        public int numberofclips { get; set; }

        //for grouped events
        public string group { get; set; }
        [Ignore]
        public List<Shoot> events { get; set; }

        //the html release for the contributors
        public string release { get; set; }
        public string shotrelease { get; set; }

        //SPECIAL FIELD FOR SAVING TO LOCAL CACHE -- NOT USED FOR SERVER COMMS
        public string private_user_id { get; set; }
        [Ignore]
        public string codename { get; set; }
        [Ignore]
        public Dictionary<string, object> eventtype { get; set; }
        [Ignore]
        public string CurrentMode { get; set; }
        public string starts { get; set; }
        public string ends { get; set; }
        public string starts_time { get; set; }
        public string ends_time { get; set; }

        public bool offline { get; set; }
        [Ignore]
        public string generalrule { get; set; }
        [Ignore]
        public string roleimg { get; set; }
        [Ignore]
        public int version { get; set; }

        public bool ispublic { get; set; }
        [Ignore]
        public bool publicview { get; set; }
        [Ignore]
        public bool publicedit { get; set; }
        [Ignore]
        public bool publicshare { get; set; }

        public string offlinecode { get; set; }

        private string _eventcss;
        [Ignore]
        public int currentphase { get; set; }
        [Ignore]
        public string eventcss { get { return "<style>" + _eventcss + "</style>"; } set { _eventcss = value; } }
        public enum Participation { PUBLIC, OWNER, INVITED };
        public string status { get; set; }
        public Participation MyParticipation { get; set; }
        [Ignore]
        public List<MetaPhase> phases { get; set; }
        [Ignore]
        public MetaPhase CurrentPhase
        {
            get
            {
                if (phases != null && currentphase < phases.Count)
                {
                    return phases[currentphase];
                }
                else
                {
                    return new MetaPhase() { name = "" };
                }
            }
        }
        [Ignore]
        public bool HasStarted { get; set; }
        public List<Shot> shottypes = new List<Shot>();

        public string location { get; set; }

        [Ignore]
        public List<Shot> _shottypes
        {
            get
            {
                return (from n in shottypes where !n.hidden select n).ToList();
            }
        }
        [Ignore]
        public List<Role> roles { get; set; }
        [Ignore]
        public Dictionary<int, CoverageClass> coverage_classes { get; set; }

        internal void LinkRolestoShots()
        {
            foreach (var r in roles)
            {
                r._shots.Clear();
                foreach (var s in r.shot_ids)
                {
                    var shot = (from n in shottypes where n.id == s select n);
                    if (shot.Count() == 1)
                        r._shots.Add(shot.First());
                }

                if (r._shots.Count == 0)
                {
                    foreach (var s in shottypes)
                    {
                        r._shots.Add(s);
                    }
                }
            }
        }

        public string last_touched { get; set; }

        public override string ToString()
        {
            return name;
        }

        public override int GetHashCode()
        {
            return id.GetHashCode();
        }

        public bool Equals(Shoot other)
        {
            if (this.id != null && other.id != null)
                return other.id == this.id;
            else
                return false;
        }

        public List<string> AllMeta
        {
            get
            {
                List<string> classes = new List<string>();
                foreach (var k in coverage_classes)
                {
                    classes.AddRange(k.Value.items);
                }
                return classes;
            }
        }

        public int myclips { get; set; }
        public bool Featured { get; internal set; }
        public bool Contributed { get; internal set; }

        public string localroleimage { get; set; }

        public bool Equals(Shoot x, Shoot y)
        {
            if (x.id != null && y.id != null)
                return x.id == y.id;
            else
                return false;
        }

        public int GetHashCode(Shoot obj)
        {
            if (obj is null) return 0;

            //Get hash code for the Name field if it is not null. 
            if (obj.group != null)
                return obj.group.GetHashCode();
            else
                return obj.id.GetHashCode();
            //int hashProductName = obj.id== null ? 0 : obj.id.GetHashCode();

            //Calculate the hash code for the product. 
            //return hashProductName;
        }
    }
}

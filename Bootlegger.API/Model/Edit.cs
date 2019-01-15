/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using Newtonsoft.Json;
using SQLite;
using System;
using System.Collections.Generic;
using static Bootleg.API.Bootlegger;

namespace Bootleg.API.Model
{
    [Table("Edits")]
    public class Edit:IEquatable<Edit>
    {
        public Edit()
        {
            _media = new List<MediaItem>();
            id = "";
        }

        [PrimaryKey]
        public string id
        {
            get; set;
        }

        List<MediaItem> _media;

        public string media_ser
        {
            get
            {
                //Console.WriteLine("ser: " + JsonConvert.SerializeObject(Meta));
                return JsonConvert.SerializeObject(_media);
            }
            set
            {
                if (value != null)
                    _media = JsonConvert.DeserializeObject<List<MediaItem>>(value);
            }
        }

        [Ignore]
        public List<MediaItem> media { get { return _media; } set { _media = value; } }


        public BootleggerEditStatus EditStatus {
            get
            {
                if (string.IsNullOrEmpty(code))
                    return BootleggerEditStatus.Draft;
                else
                {
                    if (progress > 97 && !failed)
                        return BootleggerEditStatus.Complete;
                    else
                        return BootleggerEditStatus.InProgress;
                    }
            }
        }

        //public BootleggerEditStatus EditStatus { get set; }

        public DateTime createdAt { get; set; }
        public string shortlink { get; set; }
        public double? progress { get; set; }
        public string status { get; set; }
        public string code { get; set; }
        public bool failed { get; set; }
        public string title { get; set; }
        public string description { get; set; }
        public string path { get; set; }
        public string user_id { get; set; }
        public string failreason { get; set; }

        public bool Equals(Edit other)
        {
            return other.id == id;
        }

        //public event Action<Edit> OnUpdate;

        //internal void FireUpdate()
        //{
        //    if (OnUpdate != null)
        //        OnUpdate(this);
        //}
    }
}
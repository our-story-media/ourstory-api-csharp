/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using System;

namespace Bootleg.API.Model
{
    public class Shot
    {

        public enum ShotTypes { VIDEO, PHOTO, AUDIO, TITLE };
        public int id;
        public string name;
        public string description;
        
        public string image;
        public string icon;
        public string meta;

        public bool release;

        public int wanted;
        public int? coverage_class;
        public bool hidden = false;

        public Uri IconUri {get;set;}
        public override string ToString()
        {
            return name;
        }
        //added for v2
        private string _instructions = "";
        public string instructions {
            get { return (_instructions!=null)?_instructions : ""; }
            set { _instructions = value;}
        }

        public int max_length = 15;

        public ShotTypes shot_type;

        public bool RelativePaths = false;
    }
}

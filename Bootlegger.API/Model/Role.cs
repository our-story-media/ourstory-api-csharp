/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Bootleg.API.Model
{
    public class Role
    {
        public int id;
        public string name {get;set;}
        public string description { get; set; }
        public List<int> shot_ids;
        public List<Shot> Shots { 
            get { 
                return (from n in _shots where !n.hidden select n).ToList(); 
            }
        }
        public List<float> position { get; set; }
        public string phase;

        public PointF Position
        {
            get
            {
                if (position!=null && position.Count == 2)
                {
                    return new PointF(position[0], position[1]);
                }
                else
                {
                    return new PointF();
                }
            }
        }

        public List<Shot> _shots;

        public Role()
        {
            shot_ids = new List<int>();
            _shots = new List<Shot>();
            //Shots = new List<Shot>();
        }

        public override string ToString()
        {
            return name;
        }

    }
}

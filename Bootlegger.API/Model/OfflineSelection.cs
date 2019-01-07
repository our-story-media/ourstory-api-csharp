/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using System;

namespace Bootleg.API.Model
{
    public class OfflineSelection
    {

        public Role Role { get; set; }

        public Shoot Event { get; set; }

        public User User { get; set; }

        //public string Session { get; set; }

        public DateTime LastTouched { get; set; }

        //public bool LoggedOut { get; set; }
    }
}
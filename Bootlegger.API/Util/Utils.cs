/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using Bootleg.API.Model;
using System;
using System.Collections.Generic;

namespace Bootleg.API
{
    public class Utils:IEqualityComparer<Shot>
    {

        public bool Equals(Shot x, Shot y)
        {
            //Check whether the objects are the same object.  
            if (Object.ReferenceEquals(x, y)) return true;

            //Check whether the products' properties are equal.  
            var same = x != null && y != null && x.name.Equals(y.name) && x.hidden.Equals(y.hidden) && x.id.Equals(y.id) && x.wanted.Equals(y.wanted) && x.max_length.Equals(y.max_length) && x.description.Equals(y.description);
            //Console.WriteLine(x.wanted + " = " + y.wanted);
            return same;
        }

        public int GetHashCode(Shot obj)
        {
            return obj.GetHashCode();
        }
    }
}
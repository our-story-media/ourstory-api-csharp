/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
 ﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Bootleg.API.Model
{
    public class CoverageClass
    {
        public string name { get; set; }
        public List<string> items { get; set; }
    }
}

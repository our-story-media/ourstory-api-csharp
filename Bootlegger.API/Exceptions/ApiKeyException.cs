/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using System;

namespace Bootleg.API.Exceptions
{
    public class ApiKeyException : Exception
    {
        public ApiKeyException()
        {

        }

        public ApiKeyException(string e) : base(e)
        {

        }
    }
}
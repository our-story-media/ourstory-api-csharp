/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using System;
using System.Runtime.Serialization;

namespace Bootleg.API.Exceptions
{
    [Serializable]
    public class RoleNotSelectedException : Exception
    {
        public RoleNotSelectedException()
        {
        }

        public RoleNotSelectedException(string message) : base(message)
        {
        }

        public RoleNotSelectedException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected RoleNotSelectedException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
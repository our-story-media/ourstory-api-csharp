/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
namespace Bootleg.API.Model
{
    public class RoleStatus
    {
        public enum RoleState { NO, CONFIRM, OK };
        public RoleState State { get; set; }
        public string Message { get; set; }
    }
}
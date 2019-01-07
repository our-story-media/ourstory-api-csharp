/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;

namespace Bootleg.API
{
    public static class Extensions
    {

        public static T DeepCopy<T>(this T obj)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                BinaryFormatter formatter = new BinaryFormatter();
                formatter.Serialize(stream, obj);
                stream.Position = 0;

                return (T)formatter.Deserialize(stream);
            }
        }

        public static double DistanceTo(this PointF t,PointF other)
        {
            return Math.Sqrt(Math.Pow(other.X - t.X, 2) + Math.Pow(other.Y - t.Y, 2));
        }

        public static IOrderedEnumerable<TSource> OrderBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, bool isAscending)
        {
            return isAscending ? source.OrderBy(keySelector) : source.OrderByDescending(keySelector);
        }
    }
}

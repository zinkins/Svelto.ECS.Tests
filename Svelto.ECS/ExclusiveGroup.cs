﻿using System;
using System.Collections.Generic;
using Svelto.ECS.Internal;

#pragma warning disable 660,661

namespace Svelto.ECS
{
    /// <summary>
    /// Exclusive Groups guarantee that the GroupID is unique.
    ///
    /// The best way to use it is like:
    ///
    /// public static class MyExclusiveGroups //(can be as many as you want)
    /// {
    ///     public static ExclusiveGroup MyExclusiveGroup1 = new ExclusiveGroup();
    ///
    ///     public static ExclusiveGroup[] GroupOfGroups = { MyExclusiveGroup1, ...}; //for each on this!
    /// }
    /// </summary>
    ///
    public class ExclusiveGroup
    {
        public ExclusiveGroup()
        {
            _group = ExclusiveGroupStruct.Generate();
        }

        public ExclusiveGroup(string recognizeAs)
        {
            _group = ExclusiveGroupStruct.Generate();

            _serialisedGroups.Add(recognizeAs, _group);
        }

        public ExclusiveGroup(ushort range)
        {
            _group = new ExclusiveGroupStruct(range);
#if DEBUG
            _range = range;
#endif
        }

        public static implicit operator ExclusiveGroupStruct(ExclusiveGroup group)
        {
            return group._group;
        }

        public static explicit operator uint(ExclusiveGroup group)
        {
            return group._group;
        }

        public static ExclusiveGroupStruct operator+(ExclusiveGroup a, uint b)
        {
#if DEBUG
            if (a._range == 0)
                throw new ECSException("adding values to a not ranged ExclusiveGroup");
            if (b >= a._range)
                throw new ECSException("Using out of range group");
#endif            
            return a._group + b;
        }

        readonly ExclusiveGroupStruct _group;

        //I use this as parameter because it must not be possible to pass null Exclusive Groups.
        public struct ExclusiveGroupStruct : IEquatable<ExclusiveGroupStruct>, IComparable<ExclusiveGroupStruct>,
                                IEqualityComparer<ExclusiveGroupStruct>
        {
            public static bool operator ==(ExclusiveGroupStruct c1, ExclusiveGroupStruct c2)
            {
                return c1.Equals(c2);
            }

            public static bool operator !=(ExclusiveGroupStruct c1, ExclusiveGroupStruct c2)
            {
                return c1.Equals(c2) == false;
            }

            public bool Equals(ExclusiveGroupStruct other)
            {
                return other._id == _id;
            }

            public int CompareTo(ExclusiveGroupStruct other)
            {
                return other._id.CompareTo(_id);
            }

            public bool Equals(ExclusiveGroupStruct x, ExclusiveGroupStruct y)
            {
                return x._id == y._id;
            }

            public int GetHashCode(ExclusiveGroupStruct obj)
            {
                return _id.GetHashCode();
            }

            internal static ExclusiveGroupStruct Generate()
            {
                ExclusiveGroupStruct groupStruct;

                groupStruct._id = _globalId;
                DBC.ECS.Check.Require(_globalId + 1 < ushort.MaxValue, "too many exclusive groups created");
                _globalId++;

                return groupStruct;
            }

            /// <summary>
            /// Use this constructor to reserve N groups
            /// </summary>
            internal ExclusiveGroupStruct(ushort range)
            {
                _id =  _globalId;
                DBC.ECS.Check.Require(_globalId + range < ushort.MaxValue, "too many exclusive groups created");
                _globalId += range;
            }

            internal ExclusiveGroupStruct(uint groupID)
            {
                _id = groupID;
            }

            public ExclusiveGroupStruct(byte[] data, uint pos)
            {
                _id = (uint)(
                    data[pos++]
                    | data[pos++] << 8
                    | data[pos++] << 16
                    | data[pos++] << 24
                );
                
                DBC.ECS.Check.Ensure(_id < _globalId, "Invalid group ID deserialiased");
            }

            public static implicit operator uint(ExclusiveGroupStruct groupStruct)
            {
                return groupStruct._id;
            }

            public static ExclusiveGroupStruct operator+(ExclusiveGroupStruct a, uint b)
            {
                var group = new ExclusiveGroupStruct();

                group._id = a._id + b;

                return group;
            }

            uint        _id;
            static uint _globalId;
        }

/// <summary>
/// todo: this is wrong must change
/// </summary>
/// <param name="holderGroupName"></param>
/// <returns></returns>
/// <exception cref="Exception"></exception>
        public static ExclusiveGroupStruct Search(string holderGroupName)
        {
            if (_serialisedGroups.ContainsKey(holderGroupName) == false)
                throw new Exception("Serialized Group Not Found ".FastConcat(holderGroupName));

            return _serialisedGroups[holderGroupName];
        }

/// <summary>
/// todo:  this is wrong must change
///
/// </summary>
        static readonly Dictionary<string, ExclusiveGroupStruct> _serialisedGroups = new Dictionary<string,
            ExclusiveGroupStruct>();
#if DEBUG
        readonly ushort _range;
#endif        
    }
}
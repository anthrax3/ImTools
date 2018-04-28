﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ImTools.Benchmarks
{
    public struct TypedValue<T>
    {
        public Type Key;
        public T Value;
    }

    public static class TypeHashCache
    {
        public const int InitialCapacity = 1 << 5; // 32

        public static TypedValue<T>[] Empty<T>(int initialCapacity = InitialCapacity)
        {
            // reserve quarter of capacity for hash conflicts
            return new TypedValue<T>[initialCapacity];
        }

        [MethodImpl((MethodImplOptions)256)]
        public static T GetValueOrDefault<T>(this TypedValue<T>[] slots, Type key)
        {
            var capacity = slots.Length;
            var capacityMask = capacity - 1;
            var hash = key.GetHashCode();

            capacity >>= 2; // search only for quarter of capacity
            for (var distance = 0; distance < capacity; ++distance)
            {
                var slot = slots[(hash + distance) & capacityMask];

                if (slot.Key == key)
                    return slot.Value;

                if (slot.Key == null) // not found, stop on an empty key
                    break;
            }

            return default(T);
        }

        // may return new slots if old slot capacity is not enough to add a new item
        public static TypedValue<T>[] AddOrUpdate<T>(this TypedValue<T>[] slots, Type key, T value)
        {
            var capacity = slots.Length;
            var hash = key.GetHashCode();
            if (TryPut(slots, capacity, hash, key, value))
                return slots;

            // Expand slots: Create a new slots and re-populate them from the old slots.
            // Expanding slots will double the capacity.
            var newCapacity = capacity << 1;
            var newSlots = new TypedValue<T>[newCapacity];

            for (var i = 0; i < capacity; i++)
            {
                var slot = slots[i];
                var slotKey = slot.Key;
                if (slotKey != null)
                    TryPut(newSlots, newCapacity, slotKey.GetHashCode(), slotKey, slot.Value);
            }

            TryPut(newSlots, newCapacity, hash, key, value);
            return newSlots;
        }

        private static bool TryPut<T>(TypedValue<T>[] slots, int capacity, int hash, Type key, T value)
        {
            capacity = capacity >> 2; // search only for quarter of capacity
            for (var distance = 0; distance < capacity; ++distance)
            {
                var index = (hash + distance) & (capacity - 1);
                if (Interlocked.CompareExchange(ref slots[index].Key, key, null) == null)
                {
                    slots[index].Value = value;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Immutable http://en.wikipedia.org/wiki/AVL_tree with integer keys and <typeparamref name="V"/> values.</summary>
    public sealed class ImTypeMap<V>
    {
        /// <summary>Empty tree to start with.</summary>
        public static readonly ImTypeMap<V> Empty = new ImTypeMap<V>();

        /// <summary>Key.</summary>
        public readonly Type Key;
        private readonly int _keyHash;

        /// <summary>Value.</summary>
        public readonly V Value;

        /// <summary>Left sub-tree/branch, or empty.</summary>
        public readonly ImTypeMap<V> Left;

        /// <summary>Right sub-tree/branch, or empty.</summary>
        public readonly ImTypeMap<V> Right;

        /// <summary>Height of longest sub-tree/branch plus 1. It is 0 for empty tree, and 1 for single node tree.</summary>
        public readonly int Height;

        /// <summary>Returns true is tree is empty.</summary>
        public bool IsEmpty => Height == 0;

        /// <summary>Returns new tree with added or updated value for specified key.</summary>
        /// <param name="key"></param> <param name="value"></param>
        /// <returns>New tree.</returns>
        public ImTypeMap<V> AddOrUpdate(Type key, V value) =>
            AddOrUpdateImpl(key.GetHashCode(), key, value);

        /// <summary>Returns new tree with added or updated value for specified key.</summary>
        /// <param name="key">Key</param> <param name="value">Value</param>
        /// <param name="updateValue">(optional) Delegate to calculate new value from and old and a new value.</param>
        /// <returns>New tree.</returns>
        public ImTypeMap<V> AddOrUpdate(Type key, V value, Update<V> updateValue) =>
            AddOrUpdateImpl(key.GetHashCode(), key, value, false, updateValue);

        /// <summary>Returns new tree with updated value for the key, Or the same tree if key was not found.</summary>
        /// <param name="key"></param> <param name="value"></param>
        /// <returns>New tree if key is found, or the same tree otherwise.</returns>
        public ImTypeMap<V> Update(Type key, V value) =>
            AddOrUpdateImpl(key.GetHashCode(), key, value, true, null);

        /// <summary>Get value for found key or null otherwise.</summary>
        /// <param name="key"></param> <param name="defaultValue">(optional) Value to return if key is not found.</param>
        /// <returns>Found value or <paramref name="defaultValue"/>.</returns>
        public V GetValueOrDefault(Type key, V defaultValue = default(V))
        {
            var keyHash = key.GetHashCode();
            var node = this;
            while (node.Height != 0 && node.Key != key)
                node = keyHash < node._keyHash ? node.Left : node.Right;
            return node.Height != 0 ? node.Value : defaultValue;
        }

        /// <summary>Returns true if key is found and sets the value.</summary>
        /// <param name="key">Key to look for.</param> <param name="value">Result value</param>
        /// <returns>True if key found, false otherwise.</returns>
        public bool TryFind(Type key, out V value)
        {
            var keyHash = key.GetHashCode();

            var node = this;
            while (node.Height != 0 && node._keyHash != keyHash)
                node = keyHash < node._keyHash ? node.Left : node.Right;

            if (node.Height != 0)
            {
                value = node.Value;
                return true;
            }

            value = default(V);
            return false;
        }

        /// <summary>Returns all sub-trees enumerated from left to right.</summary> 
        /// <returns>Enumerated sub-trees or empty if tree is empty.</returns>
        public IEnumerable<ImTypeMap<V>> Enumerate()
        {
            if (Height == 0)
                yield break;

            var parents = new ImTypeMap<V>[Height];

            var node = this;
            var parentCount = -1;
            while (node.Height != 0 || parentCount != -1)
            {
                if (node.Height != 0)
                {
                    parents[++parentCount] = node;
                    node = node.Left;
                }
                else
                {
                    node = parents[parentCount--];
                    yield return node;
                    node = node.Right;
                }
            }
        }

        /// <summary>Removes or updates value for specified key, or does nothing if key is not found.
        /// Based on Eric Lippert http://blogs.msdn.com/b/ericlippert/archive/2008/01/21/immutability-in-c-part-nine-academic-plus-my-avl-tree-implementation.aspx </summary>
        /// <param name="key">Key to look for.</param> 
        /// <returns>New tree with removed or updated value.</returns>
        public ImTypeMap<V> Remove(Type key) =>
            RemoveImpl(key.GetHashCode(), key);

        #region Implementation

        private ImTypeMap() { }

        private ImTypeMap(int keyHash, Type key, V value)
        {
            _keyHash = keyHash;
            Key = key;
            Value = value;
            Left = Empty;
            Right = Empty;
            Height = 1;
        }

        private ImTypeMap(int keyHash, Type key, V value, ImTypeMap<V> left, ImTypeMap<V> right, int height)
        {
            _keyHash = keyHash;
            Key = key;
            Value = value;
            Left = left;
            Right = right;
            Height = height;
        }

        private ImTypeMap(int keyHash, Type key, V value, ImTypeMap<V> left, ImTypeMap<V> right)
        {
            _keyHash = keyHash;
            Key = key;
            Value = value;
            Left = left;
            Right = right;
            Height = 1 + (left.Height > right.Height ? left.Height : right.Height);
        }

        private ImTypeMap<V> AddOrUpdateImpl(int keyHash, Type key, V value)
        {
            return Height == 0  // add new node
                ? new ImTypeMap<V>(keyHash, key, value)
                : (key == Key // update found node
                    ? new ImTypeMap<V>(keyHash, key, value, Left, Right)
                    : (keyHash < _keyHash  // search for node
                        ? (Height == 1
                            ? new ImTypeMap<V>(_keyHash, Key, Value, new ImTypeMap<V>(keyHash, key, value), Right, height: 2)
                            : new ImTypeMap<V>(_keyHash, Key, Value, Left.AddOrUpdateImpl(keyHash, key, value), Right).KeepBalance())
                        : (Height == 1
                            ? new ImTypeMap<V>(_keyHash, Key, Value, Left, new ImTypeMap<V>(keyHash, key, value), height: 2)
                            : new ImTypeMap<V>(_keyHash, Key, Value, Left, Right.AddOrUpdateImpl(keyHash, key, value)).KeepBalance())));
        }

        private ImTypeMap<V> AddOrUpdateImpl(int keyHash, Type key, V value, bool updateOnly, Update<V> update)
        {
            return Height == 0 ? // tree is empty
                (updateOnly ? this : new ImTypeMap<V>(keyHash, key, value))
                : (keyHash == _keyHash ? // actual update
                    new ImTypeMap<V>(keyHash, key, update == null ? value : update(Value, value), Left, Right)
                    : (keyHash < _keyHash    // try update on left or right sub-tree
                        ? new ImTypeMap<V>(_keyHash, Key, Value, Left.AddOrUpdateImpl(keyHash, key, value, updateOnly, update), Right)
                        : new ImTypeMap<V>(_keyHash, Key, Value, Left, Right.AddOrUpdateImpl(keyHash, key, value, updateOnly, update)))
                    .KeepBalance());
        }

        private ImTypeMap<V> KeepBalance()
        {
            var delta = Left.Height - Right.Height;
            if (delta >= 2) // left is longer by 2, rotate left
            {
                var left = Left;
                var leftLeft = left.Left;
                var leftRight = left.Right;
                if (leftRight.Height - leftLeft.Height == 1)
                {
                    // double rotation:
                    //      5     =>     5     =>     4
                    //   2     6      4     6      2     5
                    // 1   4        2   3        1   3     6
                    //    3        1
                    return new ImTypeMap<V>(leftRight._keyHash, leftRight.Key, leftRight.Value,
                        left: new ImTypeMap<V>(left._keyHash, left.Key, left.Value,
                            left: leftLeft, right: leftRight.Left), right: new ImTypeMap<V>(_keyHash, Key, Value,
                            left: leftRight.Right, right: Right));
                }

                // todo: do we need this?
                // one rotation:
                //      5     =>     2
                //   2     6      1     5
                // 1   4              4   6
                return new ImTypeMap<V>(left._keyHash, left.Key, left.Value,
                    left: leftLeft, right: new ImTypeMap<V>(_keyHash, Key, Value,
                        left: leftRight, right: Right));
            }

            if (delta <= -2)
            {
                var right = Right;
                var rightLeft = right.Left;
                var rightRight = right.Right;
                if (rightLeft.Height - rightRight.Height == 1)
                {
                    return new ImTypeMap<V>(rightLeft._keyHash, rightLeft.Key, rightLeft.Value,
                        left: new ImTypeMap<V>(_keyHash, Key, Value,
                            left: Left, right: rightLeft.Left), right: new ImTypeMap<V>(right._keyHash, right.Key, right.Value,
                            left: rightLeft.Right, right: rightRight));
                }

                return new ImTypeMap<V>(right._keyHash, right.Key, right.Value,
                    left: new ImTypeMap<V>(_keyHash, Key, Value,
                        left: Left, right: rightLeft), right: rightRight);
            }

            return this;
        }

        private ImTypeMap<V> RemoveImpl(int keyHash, Type key, bool ignoreKey = false)
        {
            if (Height == 0)
                return this;

            ImTypeMap<V> result;
            if (key == Key || ignoreKey) // found node
            {
                if (Height == 1) // remove node
                    return Empty;

                if (Right.IsEmpty)
                    result = Left;
                else if (Left.IsEmpty)
                    result = Right;
                else
                {
                    // we have two children, so remove the next highest node and replace this node with it.
                    var successor = Right;
                    while (!successor.Left.IsEmpty) successor = successor.Left;
                    result = new ImTypeMap<V>(successor._keyHash, successor.Key, successor.Value,
                        Left, Right.RemoveImpl(successor._keyHash, successor.Key, ignoreKey: true));
                }
            }
            else if (keyHash < _keyHash)
                result = new ImTypeMap<V>(_keyHash, Key, Value, Left.RemoveImpl(keyHash, key), Right);
            else
                result = new ImTypeMap<V>(_keyHash, Key, Value, Left, Right.RemoveImpl(keyHash, key));

            return result.KeepBalance();
        }

        #endregion
    }


    internal static class ImTypeCache
    {
        private const int NumberOfMapBuckets = 16;
        private const int NumberOfMapMask = NumberOfMapBuckets - 1;  // get last 4 bits, fast (hash % NumberOfTrees)

        public static ImHashMap<Type, V>[] Empty<V>() => 
            new ImHashMap<Type, V>[NumberOfMapBuckets];

        [MethodImpl((MethodImplOptions)256)]
        public static bool TryFind<V>(this ImHashMap<Type, V>[] maps, Type key, out V value)
        {
            var hash = key.GetHashCode();

            var map = maps[hash & NumberOfMapMask];
            if (map != null)
            {
                for (; map.Height != 0; map = hash < map.Hash ? map.Left : map.Right)
                {
                    if (map.Key == key)
                    {
                        value = map.Value;
                        return true;
                    }
                    if (map.Hash == hash)
                        return map.TryFindConflictedValue(key, out value);
                }
            }

            value = default(V);
            return false;
        }

        [MethodImpl((MethodImplOptions)256)]
        public static V GetValueOrDefault<V>(this ImHashMap<Type, V>[] maps, Type key)
        {
            var hash = key.GetHashCode();

            var map = maps[hash & NumberOfMapMask];
            if (map != null)
            {
                for (; map.Height != 0; map = hash < map.Hash ? map.Left : map.Right)
                {
                    if (map.Key == key)
                        return map.Value;
                    if (map.Hash == hash)
                        return map.GetConflictedValueOrDefault(key, default(V));
                }
            }

            return default(V);
        }

        [MethodImpl((MethodImplOptions)256)]
        public static ImHashMap<Type, V>[] AddOrUpdate<V>(this ImHashMap<Type, V>[] maps, Type key, V value)
        {
            var hash = key.GetHashCode();

            var mapIndex = hash & NumberOfMapMask;
            var map = maps[mapIndex];
            if (map == null)
                map = ImHashMap<Type, V>.Empty;

            map = map.AddOrUpdate(hash, key, value);

            var newMaps = new ImHashMap<Type, V>[NumberOfMapBuckets];
            Array.Copy(maps, 0, newMaps, 0, NumberOfMapBuckets);
            newMaps[mapIndex] = map;

            return newMaps;
        }

        [MethodImpl((MethodImplOptions)256)]
        public static ImHashMap<Type, V>[] Update<V>(this ImHashMap<Type, V>[] maps, Type key, V value)
        {
            var hash = key.GetHashCode();

            var mapIndex = hash & NumberOfMapMask;
            var map = maps[mapIndex];
            if (map == null)
                return maps;

            var newMap = map.Update(hash, key, value, null);
            if (newMap == map)
                return maps;

            var newMaps = new ImHashMap<Type, V>[NumberOfMapBuckets];
            Array.Copy(maps, 0, newMaps, 0, NumberOfMapBuckets);
            newMaps[mapIndex] = newMap;

            return newMaps;
        }
    }
}

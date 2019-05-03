using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace System.Collections.Immutable
{
    static class ImmutableExtensions
    {
        public static ImmutableDictionary<K,V> ToImmutableDictionary<K,V>(this IDictionary<K,V> dictionary)
        {
            return new ImmutableDictionary<K, V>(dictionary);
        }
    }

    class ImmutableDictionary
    {
        internal static ImmutableDictionary<K, V> Create<K, V>(IEqualityComparer<K> comparer)
        {
            return new ImmutableDictionary<K, V>(comparer);
        }
    }

    /// <summary>
    /// Inefficient ImmutableDictionary implementation: keep a mutable dictionary and wrap all operations.
    /// </summary>
    /// <typeparam name="K"></typeparam>
    /// <typeparam name="V"></typeparam>
    class ImmutableDictionary<K, V>
    {
        /// <summary>
        /// The underlying dictionary.
        /// </summary>
        public Dictionary<K, V> Backing;

        //
        // READ-ONLY OPERATIONS
        //

        public ICollection<K> Keys
        {
            get
            {
                return Backing.Keys;
            }
        }

        public ICollection<V> Values
        {
            get
            {
                return Backing.Values;
            }
        }

        public int Count
        {
            get
            {
                return Backing.Count;
            }
        }

        public V this[K key]
        {
            get
            {
                return Backing[key];
            }
        }

        internal bool TryGetValue(K key, out V value)
        {
            return Backing.TryGetValue(key, out value);
        }

        internal bool Contains(KeyValuePair<K, V> item)
        {
            return Backing.Contains(item);
        }

        internal bool ContainsKey(K key)
        {
            return Backing.ContainsKey(key);
        }

        internal IEnumerator<KeyValuePair<K, V>> GetEnumerator()
        {
            return Backing.GetEnumerator();
        }

        //
        // WRITE OPERATIONS
        //

        internal ImmutableDictionary<K, V> SetItem(K key, V value)
        {
            var clone = new ImmutableDictionary<K, V>(Backing);
            clone.Backing.Add(key, value);

            return clone;
        }

        internal ImmutableDictionary<K, V> AddRange(IEnumerable<KeyValuePair<K, V>> serializableList)
        {
            var clone = new ImmutableDictionary<K, V>(Backing);

            foreach (var item in serializableList)
            {
                clone.Backing.Add(item.Key, item.Value);
            }

            return clone;
        }


        internal ImmutableDictionary<K, V> Remove(K key)
        {
            var clone = new ImmutableDictionary<K, V>(Backing);
            clone.Backing.Remove(key);

            return clone;
        }

        internal ImmutableDictionary<K, V> Clear()
        {
            return new ImmutableDictionary<K, V>(Backing.Comparer);
        }

        internal ImmutableDictionary()
        {
            Backing = new Dictionary<K, V>();
        }

        internal ImmutableDictionary(IEqualityComparer<K> comparer)
        {
            Backing = new Dictionary<K, V>(comparer);
        }

        internal ImmutableDictionary(IDictionary<K, V> source)
        {
            if (source is ImmutableDictionary<K, V> imm)
            {
                Backing = new Dictionary<K, V>(imm.Backing, imm.Backing.Comparer);
            }
            else
            {
                Backing = new Dictionary<K, V>(source);
            }
        }

        internal static ImmutableDictionary<K, V> Empty
        {
            get
            {
                return new ImmutableDictionary<K, V>();
            }
        }
    }
}

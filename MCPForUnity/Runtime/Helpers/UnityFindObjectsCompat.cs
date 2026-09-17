using System;
using UObject = UnityEngine.Object;

namespace MCPForUnity.Runtime.Helpers
{
    // Part of MCP for Unity's compat-shim family. See UnityCompatShims.cs in this
    // folder for the full list of shims, the audit policy, and the reflection pattern.
    /// <summary>
    /// Compatibility wrappers for the FindObjectsByType family across Unity 6 releases.
    ///
    /// API timeline inside the supported Unity 6 range:
    ///   6.0-6.4 : FindObjectsByType(sortMode) / FindAnyObjectByType
    ///   6.5+    : FindObjectsByType() (no sort param) / FindAnyObjectByType
    /// </summary>
    public static class UnityFindObjectsCompat
    {
        /// <summary>Find all active objects of type T.</summary>
        public static T[] FindAll<T>() where T : UObject
        {
#if UNITY_6000_5_OR_NEWER
            return UObject.FindObjectsByType<T>();
#else
            return UObject.FindObjectsByType<T>(UnityEngine.FindObjectsSortMode.None);
#endif
        }

        /// <summary>Find all active objects of the given runtime type.</summary>
        public static UObject[] FindAll(Type type)
        {
#if UNITY_6000_5_OR_NEWER
            return UObject.FindObjectsByType(type, UnityEngine.FindObjectsInactive.Exclude);
#else
            return UObject.FindObjectsByType(type, UnityEngine.FindObjectsSortMode.None);
#endif
        }

        /// <summary>Find all objects of the given runtime type, optionally including inactive.</summary>
        public static UObject[] FindAll(Type type, bool includeInactive)
        {
#if UNITY_6000_5_OR_NEWER
            return UObject.FindObjectsByType(type,
                includeInactive ? UnityEngine.FindObjectsInactive.Include : UnityEngine.FindObjectsInactive.Exclude);
#else
            return UObject.FindObjectsByType(type,
                includeInactive ? UnityEngine.FindObjectsInactive.Include : UnityEngine.FindObjectsInactive.Exclude,
                UnityEngine.FindObjectsSortMode.None);
#endif
        }

        /// <summary>Find any single object of the given runtime type (no ordering guarantee).</summary>
        public static UObject FindAny(Type type)
        {
            return UObject.FindAnyObjectByType(type);
        }
    }
}

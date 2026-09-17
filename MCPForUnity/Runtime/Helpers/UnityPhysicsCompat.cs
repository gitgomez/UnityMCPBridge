using System;
using System.Reflection;
using UnityEngine;

namespace MCPForUnity.Runtime.Helpers
{
    // Part of MCP for Unity's compat-shim family. See UnityCompatShims.cs in this
    // folder for the full list of shims, the audit policy, and the reflection pattern.
    /// <summary>
    /// Version-compatible wrappers for Physics / Physics2D properties whose surface
    /// changes across Unity versions.
    ///
    /// Currently covered:
    ///   - Physics.autoSyncTransforms     (deprecated in Unity 6.x; replacement is Physics.SyncTransforms())
    ///   - Physics2D.autoSyncTransforms   (deprecated in Unity 6.x; replacement is Physics2D.SyncTransforms())
    ///   - Physics.simulationMode         (normalized behind the bridge's stable enum)
    ///
    /// We use reflection rather than direct property access so calls stay clean of
    /// CS0618 warnings AND survive eventual removal of the obsolete property without
    /// a recompile of this package.
    /// </summary>
    public static class UnityPhysicsCompat
    {
        /// <summary>
        /// Bridge-stable description of the 3D physics simulation mode.
        /// </summary>
        public enum SimulationMode
        {
            FixedUpdate,
            Update,
            Script,
            Unknown,
        }

        // ---------- Physics2D ----------

        private static PropertyInfo _physics2DAutoSync;
        private static bool _physics2DProbed;

        private static PropertyInfo Physics2DAutoSyncProp
        {
            get
            {
                if (!_physics2DProbed)
                {
                    _physics2DProbed = true;
                    _physics2DAutoSync = typeof(Physics2D).GetProperty(
                        "autoSyncTransforms",
                        BindingFlags.Public | BindingFlags.Static);
                }
                return _physics2DAutoSync;
            }
        }

        /// <summary>
        /// Reads <c>Physics2D.autoSyncTransforms</c> if the property exists in this
        /// Unity version. Returns <c>null</c> if the property has been removed.
        /// </summary>
        public static bool? GetPhysics2DAutoSyncTransforms()
        {
            var prop = Physics2DAutoSyncProp;
            if (prop == null || !prop.CanRead) return null;
            try { return (bool)prop.GetValue(null); }
            catch { return null; }
        }

        /// <summary>
        /// Writes <c>Physics2D.autoSyncTransforms</c> if the property exists and is
        /// writable. Returns <c>true</c> if the write happened, <c>false</c> if the
        /// property is unavailable in this Unity version.
        /// </summary>
        public static bool TrySetPhysics2DAutoSyncTransforms(bool value)
        {
            var prop = Physics2DAutoSyncProp;
            if (prop == null || !prop.CanWrite) return false;
            try
            {
                prop.SetValue(null, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---------- Physics (3D) ----------

        private static PropertyInfo _physicsAutoSync;
        private static bool _physicsProbed;

        private static PropertyInfo PhysicsAutoSyncProp
        {
            get
            {
                if (!_physicsProbed)
                {
                    _physicsProbed = true;
                    _physicsAutoSync = typeof(Physics).GetProperty(
                        "autoSyncTransforms",
                        BindingFlags.Public | BindingFlags.Static);
                }
                return _physicsAutoSync;
            }
        }

        /// <summary>
        /// Reads <c>Physics.autoSyncTransforms</c> if the property exists in this
        /// Unity version. Returns <c>null</c> if the property has been removed.
        /// </summary>
        public static bool? GetPhysicsAutoSyncTransforms()
        {
            var prop = PhysicsAutoSyncProp;
            if (prop == null || !prop.CanRead) return null;
            try { return (bool)prop.GetValue(null); }
            catch { return null; }
        }

        /// <summary>
        /// Writes <c>Physics.autoSyncTransforms</c> if the property exists and is
        /// writable. Returns <c>true</c> if the write happened, <c>false</c> if the
        /// property is unavailable in this Unity version.
        /// </summary>
        public static bool TrySetPhysicsAutoSyncTransforms(bool value)
        {
            var prop = PhysicsAutoSyncProp;
            if (prop == null || !prop.CanWrite) return false;
            try
            {
                prop.SetValue(null, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---------- Physics simulation mode (3D) ----------

        /// <summary>
        /// Reads the current Unity 6 physics simulation mode.
        /// </summary>
        public static SimulationMode GetPhysicsSimulationMode()
        {
            return ParseSimulationMode(Physics.simulationMode.ToString());
        }

        /// <summary>
        /// Sets the current Unity 6 physics simulation mode.
        /// </summary>
        public static bool TrySetPhysicsSimulationMode(SimulationMode mode)
        {
            if (mode == SimulationMode.Unknown) return false;
            if (!Enum.TryParse(mode.ToString(), true, out UnityEngine.SimulationMode unityMode))
                return false;

            Physics.simulationMode = unityMode;
            return true;
        }

        private static SimulationMode ParseSimulationMode(string s)
        {
            if (string.IsNullOrEmpty(s)) return SimulationMode.Unknown;
            switch (s.ToLowerInvariant())
            {
                case "fixedupdate": return SimulationMode.FixedUpdate;
                case "update": return SimulationMode.Update;
                case "script": return SimulationMode.Script;
                default: return SimulationMode.Unknown;
            }
        }
    }
}

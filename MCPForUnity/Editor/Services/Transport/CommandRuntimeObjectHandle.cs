using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Services.Transport
{
    /// <summary>Creates and resolves additive stable scene-object and asset handles.</summary>
    internal static class CommandRuntimeObjectHandle
    {
        internal static JObject BuildSceneObjectHandle(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            string globalObjectId = null;
            try
            {
                string candidate = GlobalObjectId.GetGlobalObjectIdSlow(gameObject).ToString();
                if (!string.IsNullOrEmpty(candidate)
                    && !candidate.StartsWith("GlobalObjectId_V1-0-", StringComparison.Ordinal))
                {
                    globalObjectId = candidate;
                }
            }
            catch { }

            string scenePath = gameObject.scene.path;
            string sceneGuid = string.IsNullOrEmpty(scenePath)
                ? null
                : AssetDatabase.AssetPathToGUID(scenePath);
            return new JObject
            {
                ["kind"] = "scene_object",
                ["global_object_id"] = globalObjectId,
                ["scene_guid"] = string.IsNullOrEmpty(sceneGuid) ? null : sceneGuid,
                ["hierarchy_path"] = "/" + GameObjectLookup.GetGameObjectPath(gameObject),
                ["instance_id"] = gameObject.GetInstanceIDCompat(),
                ["epoch"] = CommandRuntimeState.Epoch,
                ["revision"] = CommandRuntimeState.Snapshot().Value<long>("revision"),
                ["session_local"] = string.IsNullOrEmpty(globalObjectId)
            };
        }

        internal static JObject BuildAssetHandle(UnityEngine.Object asset)
        {
            if (asset == null
                || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    asset,
                    out string guid,
                    out long localFileId))
            {
                return null;
            }

            return new JObject
            {
                ["kind"] = "asset",
                ["guid"] = guid,
                ["local_file_id"] = localFileId,
                ["path"] = AssetDatabase.GetAssetPath(asset),
                ["revision"] = CommandRuntimeState.Snapshot().Value<long>("asset_revision")
            };
        }

        internal static bool TryResolveSceneObject(JToken token, out GameObject gameObject)
        {
            gameObject = null;
            JObject wrapper = token as JObject;
            JObject handle = wrapper?["handle"] as JObject ?? wrapper;
            if (handle == null
                || (handle.Value<string>("kind") != "scene_object"
                    && handle["global_object_id"] == null
                    && handle["instance_id"] == null))
            {
                return false;
            }

            string globalObjectId = handle.Value<string>("global_object_id");
            if (!string.IsNullOrEmpty(globalObjectId))
            {
                try
                {
                    if (GlobalObjectId.TryParse(globalObjectId, out GlobalObjectId parsed))
                    {
                        UnityEngine.Object resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed);
                        gameObject = resolved as GameObject ?? (resolved as Component)?.gameObject;
                        if (gameObject != null)
                        {
                            return true;
                        }
                    }
                }
                catch { }
            }

            string sceneGuid = handle.Value<string>("scene_guid");
            string hierarchyPath = handle.Value<string>("hierarchy_path");
            if (!string.IsNullOrEmpty(sceneGuid) && !string.IsNullOrEmpty(hierarchyPath))
            {
                string expectedScenePath = string.IsNullOrEmpty(sceneGuid)
                    ? null
                    : AssetDatabase.GUIDToAssetPath(sceneGuid);
                gameObject = FindBySceneAndHierarchy(expectedScenePath, hierarchyPath);
                if (gameObject != null)
                {
                    return true;
                }
            }

            string epoch = handle.Value<string>("epoch");
            int? instanceId = handle.Value<int?>("instance_id");
            if (instanceId.HasValue
                && string.Equals(epoch, CommandRuntimeState.Epoch, StringComparison.Ordinal))
            {
                gameObject = UnityObjectIdCompat.InstanceIDToObjectCompat(instanceId.Value) as GameObject;
            }

            return gameObject != null;
        }

        internal static UnityEngine.Object ResolveAssetHandle(JObject handle)
        {
            if (handle == null || handle.Value<string>("kind") != "asset")
            {
                return null;
            }

            string guid = handle.Value<string>("guid");
            long? localFileId = handle.Value<long?>("local_file_id");
            string path = string.IsNullOrEmpty(guid)
                ? handle.Value<string>("path")
                : AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
            if (!localFileId.HasValue)
            {
                return assets.Length > 0 ? assets[0] : null;
            }

            foreach (UnityEngine.Object asset in assets)
            {
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        asset,
                        out _,
                        out long candidateId)
                    && candidateId == localFileId.Value)
                {
                    return asset;
                }
            }
            return null;
        }

        private static GameObject FindBySceneAndHierarchy(
            string expectedScenePath,
            string hierarchyPath)
        {
            string normalized = hierarchyPath.Trim('/');
            if (string.IsNullOrEmpty(normalized))
            {
                return null;
            }

            string[] parts = normalized.Split('/');
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid()
                    || (!string.IsNullOrEmpty(expectedScenePath)
                        && !string.Equals(
                            scene.path,
                            expectedScenePath,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (!string.Equals(root.name, parts[0], StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (parts.Length == 1)
                    {
                        return root;
                    }

                    Transform child = root.transform.Find(string.Join("/", parts, 1, parts.Length - 1));
                    if (child != null)
                    {
                        return child.gameObject;
                    }
                }
            }
            return null;
        }
    }
}

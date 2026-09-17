using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Runtime.Helpers;
using MCPForUnity.Editor.Services.Transport;

namespace MCPForUnity.Editor.Resources.Scene
{
    /// <summary>
    /// Resource handler for reading GameObject data.
    /// Provides read-only access to GameObject information without component serialization.
    /// 
    /// URI: unity://scene/gameobject/{instanceID-or-GlobalObjectId}
    /// </summary>
    [McpForUnityResource("get_gameobject")]
    public static class GameObjectResource
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            JToken target = GetTarget(@params);
            if (target == null)
            {
                return new ErrorResponse("'instanceID' or scene-object 'target' handle is required.");
            }

            try
            {
                var go = ObjectResolver.ResolveGameObject(target);
                if (go == null)
                {
                    return new ErrorResponse($"GameObject target '{target}' not found.");
                }

                return new
                {
                    success = true,
                    data = SerializeGameObject(go)
                };
            }
            catch (Exception e)
            {
                McpLog.Error($"[GameObjectResource] Error getting GameObject: {e}");
                return new ErrorResponse($"Error getting GameObject: {e.Message}");
            }
        }

        /// <summary>
        /// Serializes a GameObject without component details.
        /// For component data, use GetComponents or GetComponent resources.
        /// </summary>
        public static object SerializeGameObject(GameObject go)
        {
            if (go == null)
                return null;

            var transform = go.transform;
            
            // Get component type names (not full serialization)
            var componentTypes = go.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => c.GetType().Name)
                .ToList();

            // Get children instance IDs (not full serialization)
            var childrenIds = new List<int>();
            foreach (Transform child in transform)
            {
                childrenIds.Add(child.gameObject.GetInstanceIDCompat());
            }

            return new
            {
                instanceID = go.GetInstanceIDCompat(),
                handle = CommandRuntimeObjectHandle.BuildSceneObjectHandle(go),
                name = go.name,
                tag = go.tag,
                layer = go.layer,
                layerName = LayerMask.LayerToName(go.layer),
                active = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                isStatic = go.isStatic,
                transform = new
                {
                    position = SerializeVector3(transform.position),
                    localPosition = SerializeVector3(transform.localPosition),
                    rotation = SerializeVector3(transform.eulerAngles),
                    localRotation = SerializeVector3(transform.localEulerAngles),
                    scale = SerializeVector3(transform.localScale),
                    lossyScale = SerializeVector3(transform.lossyScale)
                },
                parent = transform.parent != null ? transform.parent.gameObject.GetInstanceIDCompat() : (int?)null,
                children = childrenIds,
                parentHandle = transform.parent != null
                    ? CommandRuntimeObjectHandle.BuildSceneObjectHandle(transform.parent.gameObject)
                    : null,
                childHandles = transform.Cast<Transform>()
                    .Select(child => CommandRuntimeObjectHandle.BuildSceneObjectHandle(child.gameObject))
                    .ToList(),
                componentTypes = componentTypes,
                path = GameObjectLookup.GetGameObjectPath(go)
            };
        }

        internal static JToken GetTarget(JObject @params)
        {
            return @params?["target"]
                ?? @params?["handle"]
                ?? @params?["instanceID"]
                ?? @params?["instance_id"]
                ?? @params?["id"];
        }

        private static object SerializeVector3(Vector3 v)
        {
            return new { x = v.x, y = v.y, z = v.z };
        }
    }

    /// <summary>
    /// Resource handler for reading all components on a GameObject.
    /// 
    /// URI: unity://scene/gameobject/{instanceID-or-GlobalObjectId}/components
    /// </summary>
    [McpForUnityResource("get_gameobject_components")]
    public static class GameObjectComponentsResource
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            JToken target = GameObjectResource.GetTarget(@params);
            if (target == null)
            {
                return new ErrorResponse("'instanceID' or scene-object 'target' handle is required.");
            }

            // Pagination parameters
            int pageSize = ParamCoercion.CoerceInt(@params["pageSize"] ?? @params["page_size"], 25);
            int cursor = ParamCoercion.CoerceInt(@params["cursor"], 0);
            bool includeProperties = ParamCoercion.CoerceBool(@params["includeProperties"] ?? @params["include_properties"], true);

            pageSize = Mathf.Clamp(pageSize, 1, 100);

            try
            {
                var go = ObjectResolver.ResolveGameObject(target);
                if (go == null)
                {
                    return new ErrorResponse($"GameObject target '{target}' not found.");
                }

                var allComponents = go.GetComponents<Component>().Where(c => c != null).ToList();
                int total = allComponents.Count;

                var pagedComponents = allComponents.Skip(cursor).Take(pageSize).ToList();
                
                var componentData = new List<object>();
                foreach (var component in pagedComponents)
                {
                    if (includeProperties)
                    {
                        componentData.Add(GameObjectSerializer.GetComponentData(component));
                    }
                    else
                    {
                        componentData.Add(new
                        {
                            typeName = component.GetType().FullName,
                            instanceID = component.GetInstanceIDCompat()
                        });
                    }
                }

                int? nextCursor = cursor + pagedComponents.Count < total ? cursor + pagedComponents.Count : (int?)null;

                return new
                {
                    success = true,
                    data = new
                    {
                        gameObjectID = go.GetInstanceIDCompat(),
                        gameObjectHandle = CommandRuntimeObjectHandle.BuildSceneObjectHandle(go),
                        gameObjectName = go.name,
                        components = componentData,
                        cursor = cursor,
                        pageSize = pageSize,
                        nextCursor = nextCursor,
                        totalCount = total,
                        hasMore = nextCursor.HasValue,
                        includeProperties = includeProperties
                    }
                };
            }
            catch (Exception e)
            {
                McpLog.Error($"[GameObjectComponentsResource] Error getting components: {e}");
                return new ErrorResponse($"Error getting components: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Resource handler for reading a single component on a GameObject.
    /// 
    /// URI: unity://scene/gameobject/{instanceID-or-GlobalObjectId}/component/{componentName}
    /// </summary>
    [McpForUnityResource("get_gameobject_component")]
    public static class GameObjectComponentResource
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            JToken target = GameObjectResource.GetTarget(@params);
            if (target == null)
            {
                return new ErrorResponse("'instanceID' or scene-object 'target' handle is required.");
            }

            string componentName = ParamCoercion.CoerceString(@params["componentName"] ?? @params["component_name"] ?? @params["component"], null);
            if (string.IsNullOrEmpty(componentName))
            {
                return new ErrorResponse("'componentName' parameter is required.");
            }

            try
            {
                var go = ObjectResolver.ResolveGameObject(target);
                if (go == null)
                {
                    return new ErrorResponse($"GameObject target '{target}' not found.");
                }

                // Find the component by type name
                Component targetComponent = null;
                foreach (var component in go.GetComponents<Component>())
                {
                    if (component == null) continue;
                    
                    var typeName = component.GetType().Name;
                    var fullTypeName = component.GetType().FullName;
                    
                    if (string.Equals(typeName, componentName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fullTypeName, componentName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetComponent = component;
                        break;
                    }
                }

                if (targetComponent == null)
                {
                    return new ErrorResponse($"Component '{componentName}' not found on GameObject '{go.name}'.");
                }

                return new
                {
                    success = true,
                    data = new
                    {
                        gameObjectID = go.GetInstanceIDCompat(),
                        gameObjectHandle = CommandRuntimeObjectHandle.BuildSceneObjectHandle(go),
                        gameObjectName = go.name,
                        component = GameObjectSerializer.GetComponentData(targetComponent)
                    }
                };
            }
            catch (Exception e)
            {
                McpLog.Error($"[GameObjectComponentResource] Error getting component: {e}");
                return new ErrorResponse($"Error getting component: {e.Message}");
            }
        }
    }
}

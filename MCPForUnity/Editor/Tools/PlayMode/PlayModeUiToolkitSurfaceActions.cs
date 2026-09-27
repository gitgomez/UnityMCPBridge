using System;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.Tools.PlayMode
{
    internal static partial class PlayModeUiToolkitActions
    {
        private static object ValidateSurface(ToolParams p)
        {
            if (!(p.GetRaw("surface") is JObject options)
                || p.GetRaw("position") == null || p.GetRaw("position").Type == JTokenType.Null
                || HasElementQueryFields(p) || p.GetRaw("collection") is JObject)
                return ErrorResponse.FromCode("invalid_coordinate_space", "camera_viewport requires position and surface, without an element/collection query.");
            string[] keys = { "camera", "target", "camera_search_method", "target_search_method" };
            if (options.Properties().Any(property => !keys.Contains(property.Name)))
                return ErrorResponse.FromCode("invalid_coordinate_space", "surface contains unknown options.");
            foreach (string field in new[] { "camera", "target" })
            {
                JToken value = options[field];
                JToken methodToken = options[field + "_search_method"];
                string method = methodToken == null || methodToken.Type == JTokenType.Null ? null : methodToken.ToString();
                if (value?.Type != JTokenType.String || string.IsNullOrWhiteSpace(value.Value<string>())
                    || (method != null && (methodToken.Type != JTokenType.String || (method != "by_id" && method != "by_name" && method != "by_path"))))
                    return ErrorResponse.FromCode("invalid_coordinate_space", "surface requires camera/target strings and optional by_id, by_name or by_path search methods.");
            }
            return null;
        }

        private sealed class SurfaceContext
        {
            internal Camera Camera;
            internal MeshRenderer Renderer;
            internal MeshFilter Filter;
            internal MeshCollider Collider;
            internal Mesh Mesh;
            internal Material Material;
            internal RenderTexture Texture;
        }

        private static bool TryResolveSurface(ToolParams p, out SurfaceContext surface, out object error)
        {
            surface = null; error = null;
            var options = (JObject)p.GetRaw("surface");
            if (!InteractPlayMode.TryResolveTarget(options["camera"], (string)options["camera_search_method"], false,
                    out GameObject cameraObject, out string cameraError))
                error = ErrorResponse.FromCode("ui_surface_camera_resolution_failed", cameraError);
            else if (!InteractPlayMode.TryResolveTarget(options["target"], (string)options["target_search_method"], false,
                    out GameObject target, out string targetError))
                error = ErrorResponse.FromCode("ui_surface_resolution_failed", targetError);
            else
            {
                var cameras = cameraObject.GetComponents<Camera>();
                var renderers = target.GetComponents<Renderer>();
                var colliders = target.GetComponents<Collider>();
                var filter = target.GetComponent<MeshFilter>();
                if (cameras.Length != 1 || renderers.Length != 1 || !(renderers[0] is MeshRenderer renderer)
                    || colliders.Length != 1 || !(colliders[0] is MeshCollider collider) || filter == null)
                    error = ErrorResponse.FromCode("ui_surface_geometry_unsupported", "Requires exactly one Camera, and one MeshRenderer, MeshFilter and MeshCollider on the explicit surface GameObject.");
                else
                    surface = new SurfaceContext { Camera = cameras[0], Renderer = renderer, Filter = filter, Collider = collider,
                        Mesh = filter.sharedMesh, Material = renderer.sharedMaterial };
            }
            return error == null;
        }

        private static bool TryMapSurfacePoint(DocumentContext context, SurfaceContext surface, Vector2 viewport,
            out Vector2 panelPoint, out object mapping, out object error)
        {
            panelPoint = default; mapping = null; error = null;
            Camera camera = surface.Camera;
            MeshRenderer renderer = surface.Renderer;
            MeshCollider collider = surface.Collider;
            Material material = surface.Material;
            RenderTexture texture = context.Document != null ? context.Document.panelSettings?.targetTexture : null;
            if (!IsDocumentMutable(context) || texture == null || !texture.IsCreated()
                || !IsFinitePositiveRect(context.Panel.visualTree.worldBound))
                error = ErrorResponse.FromCode("ui_toolkit_texture_required", "Surface mapping requires a live flat panel with a created RenderTexture and finite positive bounds.");
            else if (surface.Texture != null && surface.Texture != texture)
                error = ErrorResponse.FromCode("ui_surface_binding_changed", "The panel texture changed during the gesture.");
            else if (camera == null || !camera.isActiveAndEnabled || camera.stereoEnabled
                || renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.forceRenderingOff
                || (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0)
                error = ErrorResponse.FromCode("ui_surface_not_visible", "Requires an active mono Camera and enabled surface inside its culling mask.");
            else if (collider == null || !collider.enabled || collider.isTrigger || collider.convex
                || surface.Filter == null || surface.Mesh == null || surface.Filter.sharedMesh != surface.Mesh
                || collider.sharedMesh != surface.Mesh || surface.Mesh.subMeshCount != 1
                || !surface.Mesh.HasVertexAttribute(VertexAttribute.TexCoord0)
                || surface.Mesh.GetTopology(0) != MeshTopology.Triangles
                || !camera.gameObject.scene.GetPhysicsScene().Equals(collider.gameObject.scene.GetPhysicsScene()))
                error = ErrorResponse.FromCode("ui_surface_geometry_unsupported", "Requires matching single-submesh triangle geometry with UV0, a non-convex non-trigger MeshCollider, and the camera's physics scene.");
            else if (material == null || renderer.sharedMaterials.Length != 1 || renderer.sharedMaterial != material
                || renderer.HasPropertyBlock() || material.shader == null)
                error = ErrorResponse.FromCode("ui_surface_material_unsupported", "Requires one stable shared material without MaterialPropertyBlock overrides.");
            if (error != null) return false;

            string shader = material.shader.name;
            string property = shader == "Unlit/Texture" ? "_MainTex"
                : shader == "Universal Render Pipeline/Unlit" ? "_BaseMap" : null;
            if (property == null || !material.HasProperty(property)
                || (shader == "Universal Render Pipeline/Unlit" && (material.GetFloat("_Surface") != 0 || material.GetFloat("_AlphaClip") != 0)))
                error = ErrorResponse.FromCode("ui_surface_material_unsupported", "Only Unlit/Texture and opaque non-alpha-clipped Universal Render Pipeline/Unlit are supported; custom shader UV mapping is not inferred.");
            else if (material.GetTexture(property) != texture)
                error = ErrorResponse.FromCode("ui_surface_texture_mismatch", "The surface material is not bound to this UIDocument's targetTexture.");
            else if (material.GetTextureScale(property) != Vector2.one || material.GetTextureOffset(property) != Vector2.zero)
                error = ErrorResponse.FromCode("ui_surface_uv_unsupported", "Requires identity texture tiling/offset; wrapped, transformed or custom UV mapping is not supported.");
            if (error != null) return false;
            surface.Texture = texture;

            Ray ray = camera.ViewportPointToRay(new Vector3(viewport.x, 1f - viewport.y, 0), Camera.MonoOrStereoscopicEye.Mono);
            float forward = Vector3.Dot(ray.direction, camera.transform.forward);
            float distance = (camera.farClipPlane - camera.nearClipPlane) / forward;
            if (!(forward > 0) || float.IsInfinity(distance) || float.IsNaN(distance) || distance <= 0)
                error = ErrorResponse.FromCode("ui_surface_camera_unsupported", "Camera must provide a finite forward near-to-far ray.");
            else if (!collider.gameObject.scene.GetPhysicsScene().Raycast(ray.origin, ray.direction, out RaycastHit hit,
                distance, camera.cullingMask, QueryTriggerInteraction.Ignore))
                error = ErrorResponse.FromCode("ui_surface_raycast_miss", "No non-trigger 3D collider was hit within the camera clip range.");
            else if (hit.collider != collider)
                error = ErrorResponse.FromCode("ui_surface_occluded", "Another collider is in front of the requested surface.",
                    new { blockingInstanceId = hit.collider.gameObject.GetInstanceID() });
            else if (Vector3.Dot(hit.normal, ray.direction) >= 0)
                error = ErrorResponse.FromCode("ui_surface_backface", "Back-facing surface hits are not supported.");
            else
            {
                Vector2 uv = hit.textureCoord;
                if (!(uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1))
                    error = ErrorResponse.FromCode("ui_surface_uv_unsupported", "The hit UV0 must be finite and inside [0,1]; wrapping is not inferred.");
                else
                {
                    panelPoint = NormalizedToPanel(context.Panel, new Vector2(uv.x, 1f - uv.y));
                    mapping = new { cameraInstanceId = camera.gameObject.GetInstanceID(), surfaceInstanceId = renderer.gameObject.GetInstanceID(),
                        textureInstanceId = texture.GetInstanceID(), shader, textureProperty = property,
                        textureUv = new { x = uv.x, y = uv.y, origin = "bottom_left" },
                        worldPoint = new { x = hit.point.x, y = hit.point.y, z = hit.point.z }, hitDistance = hit.distance,
                        panelPosition = DescribeVector(panelPoint), occlusion = "physics_3d_camera_mask_non_trigger" };
                }
            }
            return error == null;
        }

        private static object DragSurface(ToolParams p, PointerAddress address, Vector2 end, int steps)
        {
            // Preflight every requested sample before PointerDown. Recheck after callbacks as well.
            object endMapping = null;
            for (int i = 1; i <= steps; i++)
                if (!TryMapSurfacePoint(address.Context, address.Surface, Vector2.Lerp(address.NormalizedPosition, end, i / (float)steps),
                        out Vector2 point, out endMapping, out object error)) return error;
                else if (!IsSurfaceDocumentHit(address, point))
                    return ErrorResponse.FromCode("ui_raycast_miss", "A drag sample does not hit the requested UIDocument.");
            using (var probe = PointerMoveEvent.GetPooled(new Event { type = EventType.MouseMove, button = -1 }))
                if (probe.pressedButtons != 0 || address.Panel.GetCapturingElement(PointerId.mousePointerId) != null)
                    return ErrorResponse.FromCode("pointer_busy", "Surface dragging requires a free mouse pointer.");

            Vector2 previous = address.PanelPoint;
            int moves = 0;
            object failure = null;
            bool up = false;
            VisualElement releaseTarget = address.HitElement;
            try
            {
                SendPointerDown(address.HitElement, previous);
                for (int i = 1; i <= steps; i++)
                {
                    if (!TryMapSurfacePoint(address.Context, address.Surface, Vector2.Lerp(address.NormalizedPosition, end, i / (float)steps),
                            out Vector2 point, out endMapping, out failure)) break;
                    if (address.HitElement.panel != address.Panel || !IsSurfaceDocumentHit(address, point))
                    {
                        failure = ErrorResponse.FromCode("ui_raycast_miss", "The drag target detached or the mapped point left its document.");
                        break;
                    }
                    releaseTarget = address.Panel.Pick(point);
                    SendPointerMove(releaseTarget, point, point - previous);
                    previous = point;
                    moves++;
                }
            }
            catch (Exception exception)
            {
                failure = ErrorResponse.FromCode("ui_surface_dispatch_failed", exception.Message);
            }
            finally
            {
                up = releaseTarget.panel == address.Panel
                    && (releaseTarget == address.Context.Root || address.Context.Root.Contains(releaseTarget));
                // Pooling MouseUp clears the synthetic pressed bit even after detachment.
                using (var release = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, mousePosition = previous, button = 0 }))
                    if (up) { release.target = releaseTarget; releaseTarget.SendEvent(release); }
            }
            EditorApplication.QueuePlayerLoopUpdate();
            if (failure != null || !up)
                return ErrorResponse.FromCode("pointer_dispatch_interrupted", "Surface drag was interrupted after PointerDown; inspect state before retrying.",
                    new { cause = failure, eventsInvoked = new { pointerDown = true, pointerMove = moves, pointerUp = up } });
            return new SuccessResponse("Camera/surface-mapped pointer drag dispatched.", new {
                document = DescribeDocument(address.Context), coordinateSpace = "camera_viewport",
                startPosition = DescribeNormalized(address.NormalizedPosition, p), endPosition = DescribeNormalized(end, p),
                surfaceMapping = address.SurfaceMapping, endSurfaceMapping = endMapping, steps,
                eventsInvoked = new { pointerDown = true, pointerMove = moves, pointerUp = up }, backend = "runtime_ui_toolkit" });
        }

        private static bool IsSurfaceDocumentHit(PointerAddress address, Vector2 point)
        {
            VisualElement hit = address.Panel.Pick(point);
            return hit != null && (hit == address.Context.Root || address.Context.Root.Contains(hit));
        }
    }
}

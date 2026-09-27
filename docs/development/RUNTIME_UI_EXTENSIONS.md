# Runtime UI extensions: collections and mapped panels

Collection addressing, flat-panel/texture-UV mapping and explicit camera-to-mesh
surface mapping are available starting with v10.3.0. Use matching Unity package
and server versions. Native world-space panel picking remains unsupported.

## Delivery order and ownership

Extend `interact_play_mode`, its CLI, the UI Toolkit backend, existing Unity/Python
regressions and generated contract together. Do not add another transport,
receipt ledger, device-input layer, dependency or project-specific callback.

1. Virtualized ListView/TreeView addressing and explicit realization.
2. Explicit RenderTexture/world-surface coordinate mapping.
3. Native world-space panels only where the active Unity public API permits a
   verified 3D picking/event path. Never advertise 2D panel picking as that path.

## Collections

The existing document and element selectors identify the collection. Optional
`collection` options select one item by exactly one engine `id` or data `index`;
`query` scopes the existing element selectors to that realized row. An index is
the current flattened visible-model index for trees, not a hierarchy path. An
engine item ID is not a durable game identifier or a reload-stable handle.

- `inspect_collection` pages logical item IDs, including collapsed tree nodes,
  without expanding, scrolling, selecting, binding or serializing application
  data. `offset` defaults to 0 (maximum 100000); `limit` defaults to 50 (1-100).
- `inspect_ui` with an item address distinguishes `itemExists`, `realized`, and
  actual element `exists`/visibility. It never realizes an item implicitly.
- `reveal_item` scrolls explicitly and waits for normal layout/virtualization.
  Collapsed ancestors are expanded only with `expand_ancestors=true`.
- `set_collection_expanded` changes one tree node using the explicit `value`.
- Existing click/text/toggle/hover/drag/scroll actions can target a realized row
  or its scoped child. They never reveal/select an item as a hidden side effect.
- `wait_ui(condition="realized")` observes realization without causing it.

Reveal uses one asynchronous Unity command and one Runtime-v1 receipt. It has a
bounded deadline, checks document/collection/item identity on every frame, and
fails explicitly on detachment, data replacement, disappearance or timeout.
Expansion/scroll effects are not rolled back on failure. Inspect before retrying.
No transport-version bump or general `if_match` mechanism is necessary: the
additive input schema and generated hash change; existing retry policy remains.

## Coordinate mapping

Keep legacy normalized screen-space behavior unchanged. Add explicit coordinate
spaces for flat panel-normalized points (top-left) and target-texture UVs
(bottom-left). Validate finite points, panel dimensions and document membership.
The same mapping must apply to drag start/end, hit testing and returned metadata.

For a RenderTexture shown on a world-space mesh, `coordinate_space="camera_viewport"`
requires `position` and `surface={"camera":"Camera", "target":"Screen"}`.
The optional `camera_search_method` and `target_search_method` accept `by_id`,
`by_name` or `by_path`. Ambiguous names fail. Coordinates are top-left relative to
the named camera's viewport, not the whole Game View; drag endpoints use the same
space. Element/collection queries cannot be mixed with this address.

The bounded path requires an active mono camera and an enabled MeshRenderer,
MeshFilter and non-convex/non-trigger MeshCollider on one explicit GameObject.
Rendering and collision must share one triangle submesh with UV0 in the same
physics scene. One shared material must bind the exact panel targetTexture:
`Unlit/Texture` via `_MainTex`, or opaque, non-alpha-clipped
`Universal Render Pipeline/Unlit` via `_BaseMap`. Identity tiling/offset is required;
property blocks, custom shaders, skinned meshes and transformed/wrapped UVs fail.
No material instances, callbacks, layers, colliders or device state are modified.

`ViewportPointToRay` picks the nearest non-trigger 3D collider inside the camera
culling mask and clip range. A different collider is `ui_surface_occluded`;
missing hits, backfaces, wrong texture bindings and unavailable geometry have
distinct errors. Hit UV0 (bottom-left) maps into the actual scaled panel bounds
(top-left), followed by normal panel picking and document-membership validation.
`surfaceMapping` returns camera/surface/texture instance IDs, world hit, UV,
panel point and the explicit occlusion model. These IDs are not persistent.

This is **physics-collider occlusion**, not pixel-accurate final rendering:
transparent objects, missing colliders, camera stacking, post-processing and
shader deformation are not visibility guarantees. Physics queries use current
physics state; the Bridge does not advance simulation or sync transforms for the
application. Flat panel-coordinate dispatch alone proves no world visibility.

Drag samples the straight camera-viewport segment (1-64 steps), raycasts every
sample before PointerDown, then revalidates the same captured object identities,
material binding and ray path after callbacks at each movement. This is sampled,
not continuous collision coverage. Later invalidation returns
`pointer_dispatch_interrupted` with the cause and partial event count; release
clears the synthetic mouse state. No rollback and no blind retry. The application
`SetScreenToPanelSpaceFunction` is not replaced. Receipt/retry semantics remain
one existing Runtime-v1 command per action.

Native world-space is a separate capability, not synonymous with rendering a
flat panel into a texture. On the currently inspected Unity 6000.3.9f1 runtime,
reflection exposes `PanelRenderMode` but not public `PanelSettings.renderMode`.
The exact [6000.3.9f1 source](https://github.com/Unity-Technologies/UnityCsReference/blob/6000.3.9f1/Modules/UIElements/Core/GameObjects/PanelSettings.cs)
declares `renderMode` **internal**, unlike the current 6000.3 documentation.
`PanelSettings` belongs to the built-in `com.unity.modules.uielements` /
`UnityEngine.UIElementsModule`, whose API follows the Editor version. This is not
a missing uGUI/UI Builder package. Native world-space picking remains unsupported;
do not upgrade Unity or call private engine methods implicitly.

## Verification

Local evidence on Unity 6000.3.9f1 (2026-09-27): 301 targeted Python/CLI tests,
33 Unity EditMode tests and both UI Toolkit PlayMode tests passed. Generated
contract/skill/reference checks passed. Real MCP calls in the existing isolated
test project verified an initially unrealized item at index 1700 of 2000,
explicit reveal, a scoped normal button click and changed status text; collapsed
tree-child rejection/opt-in expansion; and a UV coordinate click on a flat
960x540 RenderTexture. Reveal had one Runtime-v1 request/receipt. No gameplay
assignments were changed. This is panel-event evidence, not world-surface or
native-world-space verification.

Camera/surface follow-up on 2026-09-27: 328 targeted Python/CLI tests and 39 Unity
EditMode tests passed. All three UI Toolkit PlayMode tests passed again through
MCP test job `41e27ce54ec94398b63e1e4b2eeb8744`. The new case covers perspective
projection onto a rotated mesh, a scaled 960x540 panel, UV origin, hover/drag,
intermediate-path occlusion before PointerDown, missing/ambiguous addresses,
texture/property-block/tiling/collider rejection, and interrupted-drag release.
An independent real MCP click in the existing test project hit UV (0.3, 0.7)
and changed a normal Button callback's Label to `surface-clicked`; `wait_ui`
confirmed the text. Live evidence uses `Unlit/Texture`; URP Unlit was not exercised
in a separate rendered scene. Native world-space dispatch remains unverified and
unsupported. No application gameplay state was changed.

The verification scope is:

- Existing Python/CLI tests: validation, forwarding and unchanged legacy calls.
- Existing Unity test project: a genuinely virtualized off-screen ListView item,
  collapsed TreeView child, scoped row control, changing/detached collection,
  timeout and no hidden selection/expansion during reads.
- Mapped panels: non-square/scaled RenderTexture, UV origin, pointer actions,
  drag endpoints, ambiguous/missing surface and world occlusion.
- Real MCP path and resulting normal UI events, not direct product callbacks.
  Long application ScrollViews are useful regression cases but do not prove
  ListView/TreeView virtualization. Never change gameplay assignments for a UI test.

## API references

- [Unity collection controller](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/UIElements.CollectionViewController.html)
- [Unity tree controller](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/UIElements.BaseTreeViewController.html)
- [Unity world-space UI](https://docs.unity3d.com/6000.3/Documentation/Manual/ui-systems/create-world-space-ui.html)
- [Unity panel mapping API](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/UIElements.PanelSettings.SetScreenToPanelSpaceFunction.html)

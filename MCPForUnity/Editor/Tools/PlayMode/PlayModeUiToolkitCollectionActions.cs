using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.Tools.PlayMode
{
    internal static partial class PlayModeUiToolkitActions
    {
        internal static object ValidateCollection(ToolParams p, string action, string uiSystem)
        {
            bool collectionAction = action == "inspect_collection" || action == "reveal_item" || action == "set_collection_expanded";
            JToken raw = p.GetRaw("collection");
            bool present = raw != null && raw.Type != JTokenType.Null;
            object Invalid(string message) => ErrorResponse.FromCode("invalid_collection_parameters", message);
            if (!present && !collectionAction) return null;
            if (uiSystem != "ui_toolkit" || action == "ping" || action == "key_ui")
                return Invalid("collection requires a UI Toolkit element action.");
            if (!present && action != "inspect_collection") return Invalid("collection is required for this action.");
            if (present && !(raw is JObject)) return Invalid("collection must be an object.");
            var options = raw as JObject ?? new JObject();
            var allowed = action == "inspect_collection"
                ? new HashSet<string> { "offset", "limit" }
                : new HashSet<string> { "index", "id", "query" };
            if (action == "reveal_item") allowed.Add("expand_ancestors");
            if (action == "set_collection_expanded") allowed.Remove("query");
            if (options.Properties().Any(property => !allowed.Contains(property.Name)))
                return Invalid("Unsupported collection fields for this action.");
            if (action != "inspect_collection" && ((options["id"] != null) == (options["index"] != null)))
                return Invalid("Provide exactly one collection id or index.");
            foreach (string key in new[] { "id", "index", "offset", "limit" })
            {
                JToken token = options[key];
                if (token == null) continue;
                long min = key == "id" ? int.MinValue : key == "limit" ? 1 : 0;
                long max = key == "offset" ? 100000 : key == "limit" ? 100 : int.MaxValue;
                if (token.Type != JTokenType.Integer || !long.TryParse(token.ToString(), out long number) || number < min || number > max)
                    return Invalid($"collection.{key} must be an integer between {min} and {max}.");
            }
            if (options["query"] != null)
            {
                if (!(options["query"] is JObject query)
                    || query.Properties().Any(property => !new[] { "element_name", "element_class", "element_type", "element_index" }.Contains(property.Name))
                    || query.Properties().Any(property => property.Name != "element_index" && property.Value.Type != JTokenType.String)
                    || !TryReadElementQuery(new ToolParams(query), true, out _, out _))
                    return Invalid("collection.query requires valid element_* selectors.");
            }
            if (options["expand_ancestors"] != null && options["expand_ancestors"].Type != JTokenType.Boolean)
                return Invalid("collection.expand_ancestors must be boolean.");
            if (p.GetRaw("position") != null && p.GetRaw("position").Type != JTokenType.Null)
                return Invalid("collection addresses require an element query, not position.");
            if (action == "set_collection_expanded" && p.GetRaw("value")?.Type != JTokenType.Boolean)
                return Invalid("set_collection_expanded requires a boolean value.");
            if (action == "reveal_item")
            {
                JToken token = p.GetRaw("timeout_seconds");
                double timeout = p.GetFloat("timeout_seconds", 5).Value;
                if ((token != null && token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                    || double.IsNaN(timeout) || timeout < 0.1 || timeout > 30)
                    return Invalid("reveal_item timeout_seconds must be between 0.1 and 30.");
            }
            return null;
        }

        private static bool TryResolveCollection(DocumentContext context, ElementQuery query,
            out BaseVerticalCollectionView collection, out object error)
        {
            collection = null;
            ElementResolution resolved = ResolveElement(context, query);
            error = !resolved.Success ? ErrorResponse.FromCode(resolved.Code, resolved.Error) : null;
            if (error != null) return false;
            collection = resolved.Element as BaseVerticalCollectionView;
            if (collection == null || collection.viewController == null)
                error = ErrorResponse.FromCode("ui_toolkit_collection_required", "The element query must resolve an initialized ListView or TreeView collection.");
            return error == null;
        }

        private static bool TryResolveCollectionItem(BaseVerticalCollectionView collection, JObject address, out int id)
        {
            var controller = collection.viewController;
            if (address["index"] != null)
            {
                int index = address.Value<int>("index");
                if (index < 0 || index >= controller.GetItemsCount()) { id = -1; return false; }
                id = controller.GetIdForIndex(index);
                return true;
            }
            id = address.Value<int>("id");
            if (collection is BaseTreeView tree) return tree.viewController.Exists(id);
            int resolvedIndex = controller.GetIndexForId(id);
            return resolvedIndex >= 0 && resolvedIndex < controller.GetItemsCount()
                && controller.GetIdForIndex(resolvedIndex) == id;
        }

        private static ElementResolution ResolveCollectionElement(DocumentContext context, ElementQuery query, JObject address)
        {
            ElementResolution resolved = ResolveElement(context, query);
            if (!resolved.Success) return resolved;
            if (!(resolved.Element is BaseVerticalCollectionView collection) || collection.viewController == null)
                return ElementResolution.Fail("ui_toolkit_collection_required", "The element query must resolve an initialized collection.");
            if (!TryResolveCollectionItem(collection, address, out int id))
                return ElementResolution.Fail("collection_item_not_found", "The collection item does not exist.");
            return ResolveRealizedItem(collection, id, address);
        }

        private static ElementResolution ResolveRealizedItem(BaseVerticalCollectionView collection, int id, JObject address)
        {
            VisualElement row = collection.GetRootElementForId(id);
            if (row == null)
                return ElementResolution.Fail("collection_item_not_realized", "The item exists but has no realized row; use reveal_item explicitly.");
            if (address["query"] is JObject nested)
            {
                TryReadElementQuery(new ToolParams(nested), true, out ElementQuery query, out _);
                return ResolveElementInRoot(row, query);
            }
            return ElementResolution.Ok(row);
        }

        private static JObject DescribeCollectionItem(BaseVerticalCollectionView collection, int id)
        {
            var tree = collection as BaseTreeView;
            return JObject.FromObject(new
            {
                id, index = collection.viewController.GetIndexForId(id),
                realized = collection.GetRootElementForId(id) != null,
                selected = collection.selectedIds.Contains(id),
                parentId = tree == null ? (int?)null : tree.viewController.GetParentId(id),
                hasChildren = tree != null && tree.viewController.HasChildren(id),
                expanded = tree == null ? (bool?)null : tree.IsExpanded(id),
            });
        }

        private static object InspectCollectionItem(ToolParams p, DocumentContext context, ElementQuery query, string message = null)
        {
            var resolvedCollection = ResolveElement(context, query);
            if (resolvedCollection.Missing)
                return new SuccessResponse("Runtime collection does not currently exist.", new {
                    exists = false, documentExists = true, itemExists = false, realized = false,
                    visible = false, hitTestVisible = false, interactable = false,
                    document = DescribeDocument(context), backend = "runtime_ui_toolkit" });
            if (!TryResolveCollection(context, query, out var collection, out object error)) return error;
            var address = (JObject)p.GetRaw("collection");
            bool itemExists = TryResolveCollectionItem(collection, address, out int id);
            VisualElement row = itemExists ? collection.GetRootElementForId(id) : null;
            ElementResolution element = row != null ? ResolveRealizedItem(collection, id, address) : null;
            if (element != null && !element.Success && !element.Missing)
                return ErrorResponse.FromCode(element.Code, element.Error);
            JObject state = element?.Success == true
                ? JObject.FromObject(BuildInspection(context, element.Element, query, p.GetBool("include_text", true)))
                : JObject.FromObject(new { exists = false, documentExists = true, visible = false, hitTestVisible = false,
                    interactable = false, document = DescribeDocument(context), backend = "runtime_ui_toolkit" });
            state["itemExists"] = itemExists;
            state["realized"] = row != null;
            state["collectionItem"] = itemExists ? DescribeCollectionItem(collection, id) : null;
            return new SuccessResponse(message ?? "Runtime collection item inspected without realization.", state);
        }

        private static bool TryCollectionContext(ToolParams p, string action, bool mutable,
            out DocumentContext context, out ElementQuery query, out BaseVerticalCollectionView collection, out object error)
        {
            context = null; query = null; collection = null;
            error = RequirePlayMode(!mutable, action);
            if (error != null) return false;
            if (!TryReadElementQuery(p, true, out query, out string queryError))
            { error = ErrorResponse.FromCode("invalid_ui_toolkit_query", queryError); return false; }
            var document = ResolveDocument(p, !mutable);
            if (!document.Success) { error = ErrorResponse.FromCode(document.Code, document.Error); return false; }
            context = document.Context;
            if (mutable && !IsDocumentMutable(context))
            { error = ErrorResponse.FromCode("ui_not_interactable", "The runtime document is not active on a panel."); return false; }
            if (!TryResolveCollection(context, query, out collection, out error)) return false;
            if (mutable && !collection.enabledInHierarchy)
            { error = ErrorResponse.FromCode("ui_not_interactable", "The collection is disabled."); return false; }
            return true;
        }

        private static object InspectCollection(ToolParams p)
        {
            if (!TryCollectionContext(p, "inspect_collection", false, out var context, out _, out var collection, out object error)) return error;
            JObject options = p.GetRaw("collection") as JObject ?? new JObject();
            int offset = options.Value<int?>("offset") ?? 0;
            int limit = options.Value<int?>("limit") ?? 50;
            var tree = collection as BaseTreeView;
            IEnumerable<int> ids = tree != null ? tree.viewController.GetAllItemIds()
                : Enumerable.Range(0, collection.viewController.GetItemsCount()).Select(collection.viewController.GetIdForIndex);
            int[] page = ids.Skip(offset).Take(limit + 1).ToArray();
            return new SuccessResponse("Runtime collection inspected without scrolling or binding rows.", new
            {
                document = DescribeDocument(context), collection = DescribeElement(collection, context.Root),
                kind = tree == null ? "list" : "tree", indexSpace = tree == null ? "data" : "visible_flattened",
                visibleItemCount = collection.viewController.GetItemsCount(),
                items = page.Take(limit).Select(id => DescribeCollectionItem(collection, id)).ToArray(),
                offset, limit, nextOffset = page.Length > limit ? (int?)(offset + limit) : null,
                backend = "runtime_ui_toolkit",
            });
        }

        private static object SetCollectionExpanded(ToolParams p)
        {
            if (!TryCollectionContext(p, "set_collection_expanded", true, out _, out _, out var collection, out object error)) return error;
            if (!(collection is BaseTreeView tree))
                return ErrorResponse.FromCode("ui_toolkit_tree_required", "Expansion requires a TreeView collection.");
            if (!TryResolveCollectionItem(collection, (JObject)p.GetRaw("collection"), out int id))
                return ErrorResponse.FromCode("collection_item_not_found", "The tree item does not exist.");
            bool expanded = p.GetBool("value");
            if (expanded) tree.ExpandItem(id); else tree.CollapseItem(id);
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            return new SuccessResponse("Tree item expansion requested; inspect state after layout.", DescribeCollectionItem(collection, id));
        }

        internal static Task<object> RevealItemAsync(ToolParams p)
        {
            if (!TryCollectionContext(p, "reveal_item", true, out var context, out var query, out var collection, out object error))
                return Task.FromResult(error);
            var address = (JObject)p.GetRaw("collection");
            if (!TryResolveCollectionItem(collection, address, out int id))
                return Task.FromResult<object>(ErrorResponse.FromCode("collection_item_not_found", "The collection item does not exist."));
            var controller = collection.viewController;
            var source = collection.itemsSource;
            object item = controller.GetItemForId(id);
            int originalIndex = controller.GetIndexForId(id);
            var tree = collection as BaseTreeView;
            var ancestors = new List<int>();
            if (tree != null)
            {
                int parent = tree.viewController.GetParentId(id);
                while (parent != -1)
                {
                    if (ancestors.Count >= 128 || ancestors.Contains(parent))
                        return Task.FromResult<object>(ErrorResponse.FromCode("collection_tree_too_deep", "Ancestor traversal exceeded 128 nodes or found a cycle."));
                    ancestors.Add(parent);
                    parent = tree.viewController.GetParentId(parent);
                }
                if (ancestors.Any(parentId => !tree.IsExpanded(parentId)) && address.Value<bool?>("expand_ancestors") != true)
                    return Task.FromResult<object>(ErrorResponse.FromCode("collection_item_collapsed", "The item is behind collapsed ancestors; explicitly set expand_ancestors=true to expand them."));
            }
            var completion = new TaskCompletionSource<object>();
            double deadline = EditorApplication.timeSinceStartup + p.GetFloat("timeout_seconds", 5).Value;
            bool scrollRequested = false;
            void Complete(object result)
            {
                EditorApplication.update -= Tick;
                AssemblyReloadEvents.beforeAssemblyReload -= Interrupted;
                EditorApplication.quitting -= Interrupted;
                completion.TrySetResult(result);
            }
            void Interrupted() => Complete(ErrorResponse.FromCode("collection_reveal_interrupted", "Reveal interrupted; scrolling/expansion may already have happened. Inspect before retrying."));
            void Tick()
            {
                try
                {
                    object playError = RequirePlayMode(false, "reveal_item");
                    if (playError != null) { Complete(playError); return; }
                    if (context.Document == null || !IsDocumentMutable(context) || collection.panel != context.Panel
                        || !context.Root.Contains(collection) || context.Document.rootVisualElement != context.Root)
                    { Interrupted(); return; }
                    bool exists = tree != null ? tree.viewController.Exists(id)
                        : controller.GetIndexForId(id) >= 0 && controller.GetIndexForId(id) < controller.GetItemsCount();
                    object currentItem = exists ? controller.GetItemForId(id) : null;
                    bool sameItem = item == null ? currentItem == null
                        : item.GetType().IsValueType || item is string ? Equals(item, currentItem) : ReferenceEquals(item, currentItem);
                    if (collection.viewController != controller || !ReferenceEquals(source, collection.itemsSource)
                        || !exists || !sameItem
                        || (address["index"] != null && controller.GetIndexForId(id) != originalIndex))
                    { Complete(ErrorResponse.FromCode("collection_changed", "The collection or addressed item changed during reveal; inspect before retrying.")); return; }
                    if (!scrollRequested)
                    {
                        for (int i = ancestors.Count - 1; i >= 0; i--) tree.ExpandItem(ancestors[i]);
                        collection.ScrollToItemById(id);
                        scrollRequested = true;
                    }
                    else
                    {
                        var resolved = ResolveRealizedItem(collection, id, address);
                        if (resolved.Success && TryFindReachablePoint(context, resolved.Element, out _, out _))
                        { Complete(InspectCollectionItem(p, context, query, "Runtime collection item revealed and inspected.")); return; }
                    }
                    if (EditorApplication.timeSinceStartup >= deadline)
                    { Complete(ErrorResponse.FromCode("collection_reveal_timeout", "The requested item did not become reachable before the deadline; scroll/expansion may have occurred.", DescribeCollectionItem(collection, id))); return; }
                    EditorApplication.QueuePlayerLoopUpdate();
                }
                catch (Exception ex)
                { Complete(ErrorResponse.FromCode("collection_reveal_failed", $"Reveal failed after possible scroll/expansion: {ex.Message}")); }
            }
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Interrupted;
            EditorApplication.quitting += Interrupted;
            EditorApplication.QueuePlayerLoopUpdate();
            return completion.Task;
        }
    }
}

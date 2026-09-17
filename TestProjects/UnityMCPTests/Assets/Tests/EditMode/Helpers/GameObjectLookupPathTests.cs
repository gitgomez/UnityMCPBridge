using MCPForUnity.Editor.Helpers;
using NUnit.Framework;
using UnityEngine;

namespace MCPForUnityTests.Editor.Helpers
{
    public class GameObjectLookupPathTests
    {
        private GameObject _root;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("MCP_Path_Root");
            var branch = new GameObject("Branch");
            branch.transform.SetParent(_root.transform);
            var leaf = new GameObject("Leaf");
            leaf.transform.SetParent(branch.transform);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
                Object.DestroyImmediate(_root);
        }

        [TestCase("MCP_Path_Root/Branch/Leaf")]
        [TestCase("/MCP_Path_Root/Branch/Leaf")]
        [TestCase("\\MCP_Path_Root\\Branch\\Leaf\\")]
        [TestCase("Branch/Leaf")]
        [TestCase("/Branch//Leaf/")]
        public void MatchesPath_AcceptsCanonicalAndCompatiblePathForms(string path)
        {
            GameObject leaf = _root.transform.Find("Branch/Leaf").gameObject;

            Assert.IsTrue(GameObjectLookup.MatchesPath(leaf, path));
        }

        [TestCase("/MCP_Path_Root/Branch/Leaf", "MCP_Path_Root/Branch/Leaf")]
        [TestCase("\\MCP_Path_Root\\Branch\\Leaf\\", "MCP_Path_Root/Branch/Leaf")]
        [TestCase(" //MCP_Path_Root///Branch/Leaf/ ", "MCP_Path_Root/Branch/Leaf")]
        public void NormalizeHierarchyPath_ReturnsUnityRootRelativeForm(
            string input,
            string expected)
        {
            Assert.AreEqual(expected, GameObjectLookup.NormalizeHierarchyPath(input));
        }
    }
}

using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Transport
{
    [TestFixture]
    public class CommandMutationPolicyTests
    {
        [Test]
        public void GeneratedPolicies_ContainManifestMutationMetadata()
        {
            CommandRuntimeToolPolicy scene = CommandRuntimeContract.ToolPolicies["manage_scene"];
            CommandRuntimeToolPolicy import = CommandRuntimeContract.ToolPolicies["import_model"];

            Assert.AreEqual(53, CommandRuntimeContract.ToolPolicies.Count);
            Assert.AreEqual("manage_scene", scene.Handler);
            Assert.AreEqual("scene", scene.MutationClass);
            Assert.IsTrue(scene.Destructive);
            Assert.AreEqual("asset", import.MutationClass);
            Assert.IsFalse(import.Destructive);
        }

        [Test]
        public void ReadOnlyAlias_IsAllowedWhenHandlerMatches()
        {
            CommandMutationAuthorization result = CommandMutationPolicy.Authorize(
                "manage_script",
                Runtime("read_only", "get_sha"),
                "unrestricted");

            Assert.IsTrue(result.Allowed);
        }

        [Test]
        public void Standard_AllowsNonDestructiveAssetButDeniesDestructiveScene()
        {
            CommandMutationAuthorization import = CommandMutationPolicy.Authorize(
                "import_model",
                Runtime("standard", "import_model"),
                "unrestricted");
            CommandMutationAuthorization scene = CommandMutationPolicy.Authorize(
                "manage_scene",
                Runtime("standard", "manage_scene"),
                "unrestricted");

            Assert.IsTrue(import.Allowed);
            Assert.IsFalse(scene.Allowed);
            Assert.AreEqual("MUTATION_PROFILE_DENIED", scene.Code);
            Assert.AreEqual("scene", scene.Data.Value<string>("mutation_class"));
            Assert.IsTrue(scene.Data.Value<bool>("destructive"));
        }

        [Test]
        public void LocalProfile_CanTightenRequestedServerProfile()
        {
            CommandMutationAuthorization result = CommandMutationPolicy.Authorize(
                "manage_scene",
                Runtime("destructive", "manage_scene"),
                "standard");

            Assert.IsFalse(result.Allowed);
            Assert.AreEqual("MUTATION_PROFILE_DENIED", result.Code);
            Assert.AreEqual("standard", result.Data.Value<string>("local_profile"));
        }

        [Test]
        public void Destructive_DeniesBuildPackageAndExternalSideEffects()
        {
            AssertDenied("manage_build");
            AssertDenied("manage_packages");
            AssertDenied("execute_code");
        }

        [Test]
        public void UnknownTool_IsCompatibleOnlyWhenBothSidesAreUnrestricted()
        {
            CommandMutationAuthorization compatible = CommandMutationPolicy.Authorize(
                "project_custom_tool",
                Runtime("unrestricted", "project_custom_tool"),
                "unrestricted");
            CommandMutationAuthorization restricted = CommandMutationPolicy.Authorize(
                "project_custom_tool",
                Runtime("unrestricted", "project_custom_tool"),
                "standard");

            Assert.IsTrue(compatible.Allowed);
            Assert.IsFalse(restricted.Allowed);
            Assert.AreEqual("TOOL_CONTRACT_NOT_FOUND", restricted.Code);
        }

        [Test]
        public void ClaimedToolMustMatchActualUnityHandler()
        {
            CommandMutationAuthorization result = CommandMutationPolicy.Authorize(
                "manage_scene",
                Runtime("unrestricted", "find_gameobjects"),
                "unrestricted");

            Assert.IsFalse(result.Allowed);
            Assert.AreEqual("TOOL_CONTRACT_MISMATCH", result.Code);
        }

        [Test]
        public void InvalidProfile_IsRejected()
        {
            CommandMutationAuthorization result = CommandMutationPolicy.Authorize(
                "find_gameobjects",
                Runtime("typo", "find_gameobjects"),
                "unrestricted");

            Assert.IsFalse(result.Allowed);
            Assert.AreEqual("INVALID_MUTATION_PROFILE", result.Code);
        }

        private static JObject Runtime(string profile, string toolName)
        {
            return new JObject
            {
                ["profile"] = profile,
                ["tool_name"] = toolName
            };
        }

        private static void AssertDenied(string toolName)
        {
            CommandRuntimeToolPolicy policy = CommandRuntimeContract.ToolPolicies[toolName];
            CommandMutationAuthorization result = CommandMutationPolicy.Authorize(
                policy.Handler,
                Runtime("destructive", toolName),
                "unrestricted");

            Assert.IsFalse(result.Allowed, toolName);
            Assert.AreEqual("MUTATION_PROFILE_DENIED", result.Code, toolName);
        }
    }
}

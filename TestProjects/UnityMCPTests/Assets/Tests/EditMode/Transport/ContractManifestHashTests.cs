using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace MCPForUnityTests.Editor.Transport
{
    [TestFixture]
    public class ContractManifestHashTests
    {
        [Test]
        public void CanonicalManifestHash_MatchesGeneratedRuntimeConstant()
        {
            JObject manifest = LoadManifest();

            string canonicalJson = Canonicalize(manifest);
            string actualHash;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalJson));
                actualHash = "sha256:" + string.Concat(digest.Select(value => value.ToString("x2")));
            }

            Assert.AreEqual(1, manifest.Value<int>("manifest_version"));
            Assert.AreEqual(CommandRuntimeContract.ContractVersion, manifest.Value<int>("manifest_version"));
            Assert.AreEqual(CommandRuntimeContract.BuiltInSchemaHash, actualHash);
        }

        [Test]
        public void Manifest_CoversSortedPublicToolSurface()
        {
            JObject manifest = LoadManifest();
            var tools = (JArray)manifest["tools"];
            string[] names = tools
                .Children<JObject>()
                .Select(tool => tool.Value<string>("name"))
                .ToArray();

            Assert.AreEqual(53, names.Length);
            CollectionAssert.AreEqual(
                names.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                names);
            Assert.AreEqual(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        }

        private static JObject LoadManifest()
        {
            string repositoryRoot = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", ".."));
            string manifestPath = Path.Combine(
                repositoryRoot,
                "Contracts",
                "tool-contracts.v1.json");

            Assert.IsTrue(File.Exists(manifestPath), $"Contract manifest not found: {manifestPath}");
            return JObject.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
        }

        private static string Canonicalize(JToken token)
        {
            var builder = new StringBuilder();
            using (var stringWriter = new StringWriter(builder, CultureInfo.InvariantCulture))
            using (var jsonWriter = new JsonTextWriter(stringWriter)
            {
                Formatting = Formatting.None,
                Culture = CultureInfo.InvariantCulture,
                StringEscapeHandling = StringEscapeHandling.Default
            })
            {
                WriteCanonicalToken(jsonWriter, token);
            }

            return builder.ToString();
        }

        private static void WriteCanonicalToken(JsonWriter writer, JToken token)
        {
            if (token is JObject obj)
            {
                writer.WriteStartObject();
                foreach (JProperty property in obj.Properties().OrderBy(
                    property => property.Name,
                    StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalToken(writer, property.Value);
                }
                writer.WriteEndObject();
                return;
            }

            if (token is JArray array)
            {
                writer.WriteStartArray();
                foreach (JToken item in array)
                {
                    WriteCanonicalToken(writer, item);
                }
                writer.WriteEndArray();
                return;
            }

            token.WriteTo(writer);
        }
    }
}

using MCPForUnity.Editor.Tools.Input;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Tools
{
    public class InputPlayModeTests
    {
        [TestCase("{\"action\":\"key\"}")]
        [TestCase("{\"action\":\"key\",\"keys\":[]}")]
        [TestCase("{\"action\":\"key\",\"keys\":[\"Space\",\"Space\"]}")]
        [TestCase("{\"action\":\"key\",\"keys\":[\"a\"]}")]
        [TestCase("{\"action\":\"key\",\"keys\":[true]}")]
        [TestCase("{\"action\":\"key\",\"keys\":[\"W\"],\"duration_seconds\":true}")]
        [TestCase("{\"action\":\"key\",\"keys\":[\"W\"],\"duration_seconds\":6}")]
        [TestCase("{\"action\":\"key\",\"keys\":[\"W\"],\"duration_seconds\":0}")]
        [TestCase("{\"action\":\"move\"}")]
        [TestCase("{\"action\":\"move\",\"position\":[0,0],\"delta\":[1,1]}")]
        [TestCase("{\"action\":\"move\",\"position\":[false,0]}")]
        [TestCase("{\"action\":\"move\",\"delta\":[4097,0]}")]
        [TestCase("{\"action\":\"click\",\"position\":[0,0],\"button\":\"back\"}")]
        [TestCase("{\"action\":\"drag\",\"position\":[0,0]}")]
        [TestCase("{\"action\":\"drag\",\"position\":[0,0],\"end_position\":[1,1],\"steps\":1.5}")]
        [TestCase("{\"action\":\"scroll\",\"position\":[0,0],\"scroll_delta\":[0,0]}")]
        [TestCase("{\"action\":\"scroll\",\"position\":[0,0],\"scroll_delta\":[0,101]}")]
        [TestCase("{\"action\":\"status\",\"keys\":[\"Space\"]}")]
        [TestCase("{\"action\":\"cancel\",\"operation_id\":\"bad\"}")]
        public void InvalidInputIsRejectedBeforeRuntimeAdmission(string json)
        {
            var task = InputPlayMode.HandleCommand(JObject.Parse(json));
            Assert.IsTrue(task.IsCompleted);
            Assert.That(JObject.FromObject(task.Result).Value<string>("code"), Is.EqualTo("invalid_input_parameters"));
        }
        [Test] public void EditModeStatusIsReadableButInputIsRejected()
        {
            var state = JObject.FromObject(InputPlayMode.HandleCommand(JObject.Parse("{\"action\":\"status\"}")).Result);
            Assert.IsTrue(state.Value<bool>("success"));
            Assert.IsFalse(state["data"].Value<bool>("ready"));
            var input = JObject.FromObject(InputPlayMode.HandleCommand(JObject.Parse("{\"action\":\"key\",\"keys\":[\"Space\"]}")).Result);
            Assert.That(input.Value<string>("code"), Is.EqualTo("play_mode_required"));
        }
    }
}

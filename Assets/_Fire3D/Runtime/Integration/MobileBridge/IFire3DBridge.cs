using System.Collections.Generic;
using Fire3D.Contracts;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Fire3D.Unity.Integration
{
    /// <summary>Outgoing channel to the Mobile shell. Mobile persists events durably and syncs them.</summary>
    public interface IFire3DBridge
    {
        void Send(BridgeMessage message);
    }

    public static class BridgeMessages
    {
        public const string Ready = "Ready";
        public const string Launched = "Launched";
        public const string Events = "Events";
        public const string Completed = "Completed";
        public const string Error = "Error";
        public const string Exited = "Exited";

        public static BridgeMessage Make(string type, string sessionId, JToken payload = null) =>
            new() { Type = type, SessionId = sessionId, Payload = payload ?? new JObject() };

        public static BridgeMessage EventsBatch(string sessionId, List<SessionEvent> events) =>
            Make(Events, sessionId, JArray.FromObject(events, Newtonsoft.Json.JsonSerializer.Create(ContractJson.Settings)));

        public static BridgeMessage Failure(string sessionId, string code, string message) =>
            Make(Error, sessionId, new JObject { ["code"] = code, ["message"] = message });
    }

    /// <summary>
    /// Android: calls the static Java/Kotlin entry point the Mobile native module registers
    /// (<c>com.fet3d.unity.Fire3DUnityBridge.onUnityMessage(String)</c>). Elsewhere: logs and keeps a copy.
    /// </summary>
    public sealed class Fire3DBridge : IFire3DBridge
    {
        public const string AndroidClass = "com.fet3d.unity.Fire3DUnityBridge";

        public readonly List<BridgeMessage> Sent = new();
        public bool LogMessages = true;

        public void Send(BridgeMessage message)
        {
            Sent.Add(message);
            var json = ContractJson.Write(message);
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var bridge = new AndroidJavaClass(AndroidClass);
                bridge.CallStatic("onUnityMessage", json);
            }
            catch (AndroidJavaException ex)
            {
                Debug.LogWarning($"[Fire3D] Bridge unavailable ({ex.Message}); running standalone.");
            }
#else
            if (LogMessages)
                Debug.Log($"[Fire3D bridge] {(json.Length > 400 ? json.Substring(0, 400) + "…" : json)}");
#endif
        }
    }
}

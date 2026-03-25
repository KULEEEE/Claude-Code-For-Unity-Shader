using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Receives generated images from the MCP server (Nano Banana / Gemini)
    /// and forwards them to the active AI Chat window.
    /// </summary>
    public static class NanoBananaReceiver
    {
        /// <summary>
        /// Called by UnityAgentServer when an "image/generated" message arrives.
        /// </summary>
        public static void HandleImageReceived(string base64Data, string description)
        {
            var windows = Resources.FindObjectsOfTypeAll<AIChatWindow>();
            if (windows.Length > 0)
            {
                windows[0].DisplayGeneratedImage(base64Data, description);
                Debug.Log("[UnityAgent] Nano Banana image received and displayed.");
            }
            else
            {
                Debug.LogWarning("[UnityAgent] Received generated image but AI Chat window is not open.");
            }
        }
    }
}

namespace UnityAgent.Editor
{
    /// <summary>
    /// Interface for windows that host an AIChatTab.
    /// Decouples the chat tab from any specific EditorWindow.
    /// </summary>
    public interface IChatHost
    {
        void Repaint();
        string SelectedLanguage { get; }

        // Image generation settings — optional
        string ImageBackend => "gemini";  // "gemini" or "comfyui"
        string GeminiApiKey => null;
        string GeminiModel => null;
        string ComfyUIUrl => "http://127.0.0.1:8188";
        UnityEngine.Texture2D ReferenceImage => null;
        void DrawImageGenSettings() {}
    }
}

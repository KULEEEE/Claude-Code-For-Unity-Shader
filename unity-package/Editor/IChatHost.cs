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
    }
}
